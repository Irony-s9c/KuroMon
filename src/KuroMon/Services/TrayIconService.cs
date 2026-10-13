using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using KuroMon.Models;
using KuroMon.ViewModels;
using Forms = System.Windows.Forms;

namespace KuroMon.Services;

/// <summary>
/// Notification area icon that draws the selected value (e.g. CPU temperature) as a number,
/// shows all values in its tooltip and hosts the context menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainViewModel _vm;
    private readonly Forms.ToolStripMenuItem _autoStartItem;
    private readonly Forms.ToolStripMenuItem _overlayItem;
    private readonly Forms.ToolStripMenuItem _autoOptimizeItem;
    private readonly Dictionary<TrayMetric, Forms.ToolStripMenuItem> _metricItems = [];
    private IntPtr _currentHandle;
    private string _lastIconKey = string.Empty;

    public event Action? OpenMainRequested;
    public event Action? TogglePanelRequested;
    public event Action? ExitRequested;

    public TrayIconService(MainViewModel vm)
    {
        _vm = vm;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("KuroMon を開く", null, (_, _) => OpenMainRequested?.Invoke()).Font =
            new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var metricMenu = new Forms.ToolStripMenuItem("アイコンに表示する値");
        AddMetric(metricMenu, TrayMetric.CpuTemperature, "CPU 温度");
        AddMetric(metricMenu, TrayMetric.CpuUsage, "CPU 使用率");
        AddMetric(metricMenu, TrayMetric.GpuTemperature, "GPU 温度");
        AddMetric(metricMenu, TrayMetric.GpuUsage, "GPU 使用率");
        AddMetric(metricMenu, TrayMetric.MemoryUsage, "メモリ使用率");
        menu.Items.Add(metricMenu);

        _overlayItem = new Forms.ToolStripMenuItem("オーバーレイを表示");
        _overlayItem.Click += (_, _) => _vm.OverlayEnabled = !_vm.OverlayEnabled;
        menu.Items.Add(_overlayItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("メモリを解放", null, (_, _) => _vm.OptimizeMemoryCommand.Execute(null));
        _autoOptimizeItem = new Forms.ToolStripMenuItem("ゲーム中に自動でメモリを解放");
        _autoOptimizeItem.Click += (_, _) => _vm.MemoryAutoOptimize = !_vm.MemoryAutoOptimize;
        menu.Items.Add(_autoOptimizeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        _autoStartItem = new Forms.ToolStripMenuItem("Windows 起動時に自動起動") { CheckOnClick = false };
        _autoStartItem.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(_autoStartItem);

        menu.Items.Add("ログフォルダを開く", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke());
        menu.Opening += (_, _) => RefreshMenuChecks();

        _icon = new Forms.NotifyIcon
        {
            Text = "KuroMon",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) TogglePanelRequested?.Invoke();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenMainRequested?.Invoke();
        };

        AppSettings.Changed += OnSettingsChanged;
        RefreshMenuChecks();
        Update();
    }

    private void OnSettingsChanged()
    {
        RefreshMenuChecks();
        _lastIconKey = string.Empty;
        Update();
    }

    /// <summary>Redraws the icon and tooltip from the latest sample.</summary>
    public void Update()
    {
        var metric = AppSettings.Current.TrayMetric;
        var vm = metric switch
        {
            TrayMetric.GpuTemperature => _vm.GpuTemperature,
            TrayMetric.CpuUsage => _vm.CpuUsage,
            TrayMetric.GpuUsage => _vm.GpuUsage,
            TrayMetric.MemoryUsage => _vm.MemoryUsage,
            _ => _vm.CpuTemperature,
        };

        var text = vm.IsAvailable && double.TryParse(vm.ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
            : "--";

        var key = $"{text}|{vm.Level}|{metric}";
        if (key != _lastIconKey)
        {
            _lastIconKey = key;
            SetIcon(text, vm.Level);
        }

        _icon.Text = BuildTooltip(_vm.LastSnapshot);
    }

    public void ShowBalloon(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(4000);
    }

    private void AddMetric(Forms.ToolStripMenuItem parent, TrayMetric metric, string label)
    {
        var item = new Forms.ToolStripMenuItem(label);
        item.Click += (_, _) =>
        {
            AppSettings.Current.TrayMetric = metric;
            AppSettings.Current.Save();
        };
        _metricItems[metric] = item;
        parent.DropDownItems.Add(item);
    }

    private void RefreshMenuChecks()
    {
        foreach (var (metric, item) in _metricItems) item.Checked = metric == AppSettings.Current.TrayMetric;
        _autoStartItem.Checked = AppSettings.Current.AutoStart;
        _overlayItem.Checked = AppSettings.Current.OverlayEnabled;
        _autoOptimizeItem.Checked = AppSettings.Current.MemoryAutoOptimize;
    }

    private void ToggleAutoStart()
    {
        if (!AutoStartService.SetEnabled(!AppSettings.Current.AutoStart))
        {
            ShowBalloon("KuroMon", "自動起動の設定に失敗しました。管理者として起動しているか確認してください。");
        }
        RefreshMenuChecks();
    }

    public static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLog.Directory);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{AppLog.Directory}\"");
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to open log folder.", ex);
        }
    }

    private static string BuildTooltip(SensorSnapshot? s)
    {
        if (s is null) return "KuroMon";

        static string T(float? v) => v is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) + "°C" : "N/A";
        static string P(float? v) => v is { } x ? x.ToString("0", CultureInfo.InvariantCulture) + "%" : "N/A";

        var sb = new StringBuilder();
        sb.Append("KuroMon").Append('\n');
        sb.Append("CPU ").Append(T(s.CpuTemperature)).Append(" / ").Append(P(s.CpuLoad)).Append('\n');
        sb.Append("GPU ").Append(T(s.GpuTemperature)).Append(" / ").Append(P(s.GpuLoad)).Append('\n');
        sb.Append("MEM ").Append(P(s.MemoryLoad))
          .Append(" (").Append(s.MemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture))
          .Append('/').Append(s.MemoryTotalGb.ToString("0.0", CultureInfo.InvariantCulture)).Append(" GB)");

        var text = sb.ToString();
        return text.Length > 127 ? text[..127] : text;
    }

    private void SetIcon(string text, MetricLevel level)
    {
        var size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var back = level switch
            {
                MetricLevel.Normal => Color.FromArgb(0x2E, 0x7D, 0x32),
                MetricLevel.Warning => Color.FromArgb(0xF9, 0xA8, 0x25),
                MetricLevel.Critical => Color.FromArgb(0xC6, 0x28, 0x28),
                _ => Color.FromArgb(0x61, 0x61, 0x61),
            };
            var fore = level == MetricLevel.Warning ? Color.Black : Color.White;

            float radius = size / 4f;
            using (var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), radius))
            using (var brush = new SolidBrush(back))
            {
                g.FillPath(brush, path);
            }

            // Draw the digits as a path scaled to fill the icon (a 16 px icon has no room for font padding).
            using var family = new FontFamily("Segoe UI");
            using var textPath = new GraphicsPath();
            textPath.AddString(text, family, (int)System.Drawing.FontStyle.Bold, size, PointF.Empty, StringFormat.GenericTypographic);
            var bounds = textPath.GetBounds();
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                float maxW = size - 2.5f;
                float maxH = size * 0.72f;
                float scale = Math.Min(maxW / bounds.Width, maxH / bounds.Height);
                using var m = new Matrix();
                m.Translate(size / 2f, size / 2f);
                m.Scale(scale, scale);
                m.Translate(-(bounds.X + bounds.Width / 2f), -(bounds.Y + bounds.Height / 2f));
                textPath.Transform(m);
                using var textBrush = new SolidBrush(fore);
                g.FillPath(textBrush, textPath);
            }
        }

        var handle = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = Icon.FromHandle(handle);
        old?.Dispose();
        if (_currentHandle != IntPtr.Zero) DestroyIcon(_currentHandle);
        _currentHandle = handle;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        AppSettings.Changed -= OnSettingsChanged;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        if (_currentHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentHandle);
            _currentHandle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
