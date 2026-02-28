using System.Windows;
using System.Windows.Controls;
using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Media;
using WinForms = System.Windows.Forms;

namespace MyDM.App.Views;

public partial class SettingsWindow : Window
{
    private const string DefaultExtensionId = "gnpallpkcdihlckdkddppkhgblokapdj";
    private readonly DownloadRepository _repository;
    private readonly DownloadEngine _engine;

    public SettingsWindow(DownloadRepository repository, DownloadEngine engine)
    {
        InitializeComponent();
        _repository = repository;
        _engine = engine;
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
        QueueScheduleStartTextBox.Text = _repository.GetSetting("QueueScheduleStart") ?? "09:00";
        QueueScheduleStopTextBox.Text = _repository.GetSetting("QueueScheduleStop") ?? "18:00";
        QueueScheduleDaysTextBox.Text = _repository.GetSetting("QueueScheduleDays") ?? "Mon,Tue,Wed,Thu,Fri";

        AutoPopupCheckBox.IsChecked = ParseBool(_repository.GetSetting("AutoShowDownloadWindow"), defaultValue: true);
        QueueScheduleEnabledCheckBox.IsChecked = ParseBool(_repository.GetSetting("QueueScheduleEnabled"), defaultValue: false);

        SelectComboValue(MaxConcurrentCombo, _repository.GetSetting("MaxConcurrentDownloads") ?? "3");
        SelectComboValue(DefaultConnectionsCombo, _repository.GetSetting("DefaultConnections") ?? "8");
    }

    private static void SelectComboValue(System.Windows.Controls.ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static string GetComboValue(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? fallback;
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
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
            FfmpegStatusLabel.Text = $"Status: OK - {version}";
            _repository.SetSetting("FfmpegPath", FfmpegPathTextBox.Text);
        }
        else
        {
            FfmpegStatusLabel.Text = "Status: ERROR - ffmpeg not found or not working";
        }
    }

    private void RegisterNativeHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hostPath = ResolveNativeHostPath();
            if (!File.Exists(hostPath))
            {
                NativeHostStatus.Text = "ERROR: MyDM.NativeHost.exe not found. Build MyDM.NativeHost first.";
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

            NativeHostStatus.Text = $"OK: Native Host registered for extension {extensionId}";
        }
        catch (Exception ex)
        {
            NativeHostStatus.Text = $"ERROR: {ex.Message}";
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
        _repository.SetSetting("MaxConcurrentDownloads", GetComboValue(MaxConcurrentCombo, "3"));
        _repository.SetSetting("DefaultConnections", GetComboValue(DefaultConnectionsCombo, "8"));
        _repository.SetSetting("AutoShowDownloadWindow", AutoPopupCheckBox.IsChecked == true ? "1" : "0");
        _repository.SetSetting("QueueScheduleEnabled", QueueScheduleEnabledCheckBox.IsChecked == true ? "1" : "0");
        _repository.SetSetting("QueueScheduleStart", QueueScheduleStartTextBox.Text.Trim());
        _repository.SetSetting("QueueScheduleStop", QueueScheduleStopTextBox.Text.Trim());
        _repository.SetSetting("QueueScheduleDays", QueueScheduleDaysTextBox.Text.Trim());

        if (long.TryParse(SpeedLimitTextBox.Text.Trim(), out var globalKbPerSec) && globalKbPerSec >= 0)
        {
            _engine.SpeedLimiter.GlobalLimit = globalKbPerSec * 1024;
        }

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
