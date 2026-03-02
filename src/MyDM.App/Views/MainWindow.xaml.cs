using System.Windows;
using System.Windows.Controls;
using System.Linq;
using MyDM.App.Utilities;
using MyDM.App.ViewModels;

namespace MyDM.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowLayoutHelper.ApplyAdaptiveLayout(this, widthRatio: 0.95, heightRatio: 0.93);
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem treeItem && treeItem.Tag is string category && !string.IsNullOrWhiteSpace(category))
        {
            _viewModel.FilterByCategory(category);
        }
    }

    private void DownloadList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.ShowDetailsCommand.Execute(null);
    }

    private void DownloadList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete)
        {
            return;
        }

        DeleteSelected_Click(sender, e);
        e.Handled = true;
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = DownloadList.SelectedItems
            .OfType<DownloadItemViewModel>()
            .ToList();

        if (selected.Count == 0 && _viewModel.SelectedDownload != null)
        {
            selected.Add(_viewModel.SelectedDownload);
        }

        _viewModel.DeleteDownloads(selected);
    }

    private void AllDownloadsMenu_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.FilterByCategory("All Downloads");
    }

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "MyDM Download Manager\nCompatible with browser extension and desktop engine.",
            "About MyDM",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
