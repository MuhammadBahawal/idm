using System.Windows;
using System.Windows.Controls;
using MyDM.App.ViewModels;

namespace MyDM.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is string category)
        {
            if (category == "------------") return;
            _viewModel.FilterByCategory(category);
        }
    }

    private void DownloadList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.ShowDetailsCommand.Execute(null);
    }
}
