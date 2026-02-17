using System.Windows;
using MyDM.App.ViewModels;
using MyDM.App.Views;
using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Queue;

namespace MyDM.App;

public partial class App : System.Windows.Application
{
    private MyDMDatabase? _database;
    private DownloadEngine? _engine;
    private QueueManager? _queueManager;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Initialize database
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyDM", "mydm.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _database = new MyDMDatabase(dbPath);
        _database.Initialize();

        var repository = new DownloadRepository(_database);

        // Initialize engine
        _engine = new DownloadEngine(repository);
        _engine.RestoreState();

        // Initialize queue manager
        _queueManager = new QueueManager(_engine, repository);
        _queueManager.GetDefaultQueue();

        // Create and show main window
        var viewModel = new MainViewModel(_engine, repository, _queueManager);
        var mainWindow = new MainWindow(viewModel);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.StopAll();
        _engine?.Dispose();
        _queueManager?.Dispose();
        _database?.Dispose();
        base.OnExit(e);
    }
}
