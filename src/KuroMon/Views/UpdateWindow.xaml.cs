using System.Windows;
using KuroMon.Services;

namespace KuroMon.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _cts = new();

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        Themes.WindowGlass.Attach(this, Themes.Backdrop.Acrylic);
        _info = info;
        VersionText.Text = $"{AppConfig.CurrentVersionText}  →  {info.TagName}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "(更新内容の記載はありません)" : info.ReleaseNotes;
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>Path of the downloaded .exe when the user chose to update.</summary>
    public string? DownloadedPath { get; private set; }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "ダウンロード中...";

        try
        {
            using var service = new UpdateService();
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusText.Text = $"ダウンロード中... {p:0}%";
            });
            DownloadedPath = await service.DownloadAsync(_info, progress, _cts.Token);
            StatusText.Text = "再起動して更新を適用します";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // window closed during download
        }
        catch (Exception ex)
        {
            StatusText.Text = "ダウンロードに失敗しました: " + ex.Message;
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
        }
    }
}
