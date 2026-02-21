using System.Windows;
using System.Windows.Controls;
using MyDM.Core.Data;
using MyDM.Core.Media;
using WinForms = System.Windows.Forms;

namespace MyDM.App.Views;

public partial class SettingsWindow : Window
{
    private readonly DownloadRepository _repository;

    public SettingsWindow(DownloadRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        LoadSettings();
    }

    private void LoadSettings()
    {
        DefaultPathTextBox.Text = _repository.GetSetting("DefaultSavePath")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MyDM");

        SpeedLimitTextBox.Text = _repository.GetSetting("GlobalSpeedLimit") ?? "0";
        FfmpegPathTextBox.Text = _repository.GetSetting("FfmpegPath") ?? "";
        TimeoutTextBox.Text = _repository.GetSetting("ConnectionTimeout") ?? "30";
        MaxRetriesTextBox.Text = _repository.GetSetting("MaxRetries") ?? "10";
    }

    private void BrowseDefaultPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WinForms.FolderBrowserDialog
        {
            SelectedPath = DefaultPathTextBox.Text
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            DefaultPathTextBox.Text = dialog.SelectedPath;
            _repository.SetSetting("DefaultSavePath", dialog.SelectedPath);
        }
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ffmpeg|ffmpeg.exe",
            Title = "Locate ffmpeg.exe"
        };
        if (dialog.ShowDialog() == true)
        {
            FfmpegPathTextBox.Text = dialog.FileName;
            _repository.SetSetting("FfmpegPath", dialog.FileName);
        }
    }

    private async void TestFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var muxer = new FfmpegMuxer(FfmpegPathTextBox.Text);
        var version = await muxer.GetVersionAsync();
        if (version != null)
        {
            FfmpegStatusLabel.Content = $"✅ {version}";
            _repository.SetSetting("FfmpegPath", FfmpegPathTextBox.Text);
        }
        else
        {
            FfmpegStatusLabel.Content = "❌ ffmpeg not found or not working";
        }
    }

    private void RegisterNativeHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Create the native messaging host manifest and register it
            var hostPath = Path.Combine(AppContext.BaseDirectory, "MyDM.NativeHost.exe");
            var manifestPath = Path.Combine(AppContext.BaseDirectory, "com.mydm.native.json");

            var manifest = $$"""
            {
                "name": "com.mydm.native",
                "description": "MyDM Native Messaging Host",
                "path": "{{hostPath.Replace("\\", "\\\\")}}",
                "type": "stdio",
                "allowed_origins": [
                    "chrome-extension://*/"
                ]
            }
            """;

            File.WriteAllText(manifestPath, manifest);

            // Register in Windows registry for Chrome and Edge
            var registryPaths = new[]
            {
                @"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.mydm.native",
                @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.mydm.native"
            };

            foreach (var regPath in registryPaths)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regPath);
                key?.SetValue("", manifestPath);
            }

            NativeHostStatus.Content = "✅ Native Host registered successfully";
        }
        catch (Exception ex)
        {
            NativeHostStatus.Content = $"❌ Failed: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _repository.SetSetting("DefaultSavePath", DefaultPathTextBox.Text);
        _repository.SetSetting("GlobalSpeedLimit", SpeedLimitTextBox.Text);
        _repository.SetSetting("FfmpegPath", FfmpegPathTextBox.Text);
        _repository.SetSetting("ConnectionTimeout", TimeoutTextBox.Text);
        _repository.SetSetting("MaxRetries", MaxRetriesTextBox.Text);

        MessageBox.Show("Settings saved.", "MyDM", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
