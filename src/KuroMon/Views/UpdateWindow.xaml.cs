using System.Windows;
using KuroMon.Services;

namespace KuroMon.Views;

/// <summary>
/// "A new version is available". When the server gave the installer's URL and SHA-256, "Update" downloads and
/// verifies it; otherwise, or when that fails, the button opens the download page in the browser.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly CancellationTokenSource _cts = new();
    private bool _pageMode;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        Themes.WindowBackdrop.Attach(this);
        _info = info;
        VersionText.Text = $"{AppConfig.CurrentVersionText}  →  {info.TagName}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "(更新内容の記載はありません)" : info.ReleaseNotes;
        if (!info.CanInstall) SwitchToPage("自動ではアップデートできません。配布ページから新しいインストーラーを入手してください。");
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>Path of the downloaded .exe when the user chose to update.</summary>
    public string? DownloadedPath { get; private set; }

    private void SwitchToPage(string message)
    {
        _pageMode = true;
        UpdateButton.Content = "配布ページを開く";
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_pageMode)
        {
            try
            {
                UpdateService.OpenPage(_info.PageUrl);
            }
            catch (Exception ex)
            {
                AppLog.Error("Opening the download page failed.", ex);
            }
            DialogResult = false;
            return;
        }

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
            AppLog.Error("Downloading the update failed.", ex);
            Progress.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            SwitchToPage("自動アップデートできませんでした（" + ex.Message + "）。配布ページから入手してください。");
        }
    }
}
