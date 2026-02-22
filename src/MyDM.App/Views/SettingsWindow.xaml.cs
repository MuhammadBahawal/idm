using System.Windows;
using MyDM.Core.Data;
using MyDM.Core.Media;
using WinForms = System.Windows.Forms;

namespace MyDM.App.Views;

public partial class SettingsWindow : Window
{
    private const string DefaultExtensionId = "gnpallpkcdihlckdkddppkhgblokapdj";
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
        ExtensionIdTextBox.Text = _repository.GetSetting("ExtensionId") ?? DefaultExtensionId;
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
            FfmpegStatusLabel.Content = $"OK: {version}";
            _repository.SetSetting("FfmpegPath", FfmpegPathTextBox.Text);
        }
        else
        {
            FfmpegStatusLabel.Content = "ERROR: ffmpeg not found or not working";
        }
    }

    private void RegisterNativeHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hostPath = ResolveNativeHostPath();
            if (!File.Exists(hostPath))
            {
                NativeHostStatus.Content = "ERROR: MyDM.NativeHost.exe not found. Build MyDM.NativeHost first.";
                return;
            }

            var manifestPath = Path.Combine(AppContext.BaseDirectory, "com.mydm.native.json");
            var extensionId = (ExtensionIdTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(extensionId))
            {
                extensionId = DefaultExtensionId;
            }
            _repository.SetSetting("ExtensionId", extensionId);

            var manifest = $$"""
            {
                "name": "com.mydm.native",
                "description": "MyDM Native Messaging Host",
                "path": "{{hostPath.Replace("\\", "\\\\")}}",
                "type": "stdio",
                "allowed_origins": [
                    "chrome-extension://{{extensionId}}/"
                ]
            }
            """;

            File.WriteAllText(manifestPath, manifest);

            var registryPaths = new[]
            {
                @"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.mydm.native",
                @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.mydm.native"
            };

            foreach (var regPath in registryPaths)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regPath);
                key?.SetValue(string.Empty, manifestPath);
            }

            NativeHostStatus.Content = $"OK: Native Host registered for extension {extensionId}";
        }
        catch (Exception ex)
        {
            NativeHostStatus.Content = $"ERROR: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _repository.SetSetting("DefaultSavePath", DefaultPathTextBox.Text);
        _repository.SetSetting("GlobalSpeedLimit", SpeedLimitTextBox.Text);
        _repository.SetSetting("FfmpegPath", FfmpegPathTextBox.Text);
        _repository.SetSetting("ConnectionTimeout", TimeoutTextBox.Text);
        _repository.SetSetting("MaxRetries", MaxRetriesTextBox.Text);
        _repository.SetSetting("ExtensionId", ExtensionIdTextBox.Text.Trim());

        MessageBox.Show("Settings saved.", "MyDM", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string ResolveNativeHostPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "MyDM.NativeHost.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "MyDM.NativeHost", "bin", "Debug", "net8.0", "MyDM.NativeHost.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MyDM.NativeHost", "bin", "Debug", "net8.0", "MyDM.NativeHost.exe"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }
}
