using System.Windows;
using System.Windows.Controls;
using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Utilities;

namespace MyDM.App.Views;

public partial class AddUrlDialog : Window
{
    private readonly DownloadEngine _engine;
    private readonly DownloadRepository _repository;

    public AddUrlDialog(DownloadEngine engine, DownloadRepository repository)
    {
        InitializeComponent();
        _engine = engine;
        _repository = repository;

        // Set default save path
        var defaultPath = _repository.GetSetting("DefaultSavePath")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MyDM");
        SavePathTextBox.Text = defaultPath;

        // Check clipboard for URL
        try
        {
            var clip = Clipboard.GetText();
            if (UrlHelper.IsValidUrl(clip))
            {
                UrlTextBox.Text = clip;
                FileNameTextBox.Text = UrlHelper.ExtractFileName(clip);
            }
        }
        catch { }

        UrlTextBox.TextChanged += (_, _) =>
        {
            if (UrlHelper.IsValidUrl(UrlTextBox.Text) && string.IsNullOrEmpty(FileNameTextBox.Text))
                FileNameTextBox.Text = UrlHelper.ExtractFileName(UrlTextBox.Text);
        };
    }

    private async void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        await AddDownloadAsync(startImmediately: true);
    }

    private async void AddPaused_Click(object sender, RoutedEventArgs e)
    {
        await AddDownloadAsync(startImmediately: false);
    }

    private async Task AddDownloadAsync(bool startImmediately)
    {
        var url = UrlTextBox.Text.Trim();
        if (!UrlHelper.IsValidUrl(url))
        {
            MessageBox.Show("Please enter a valid URL.", "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var category = (CategoryCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
        if (category == "Auto-detect") category = null;

        var connections = int.Parse((ConnectionsCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "8");

        try
        {
            var item = await _engine.AddDownloadAsync(
                url, SavePathTextBox.Text, FileNameTextBox.Text, connections, category);

            if (!string.IsNullOrEmpty(DescriptionTextBox.Text))
            {
                item.Description = DescriptionTextBox.Text;
                _repository.Update(item);
            }

            if (startImmediately)
            {
                await _engine.StartDownloadAsync(item.Id);
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to add download: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = FileNameTextBox.Text,
            InitialDirectory = SavePathTextBox.Text,
            Title = "Choose save location"
        };

        if (dialog.ShowDialog() == true)
        {
            SavePathTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? SavePathTextBox.Text;
            FileNameTextBox.Text = Path.GetFileName(dialog.FileName);
        }
    }
}
