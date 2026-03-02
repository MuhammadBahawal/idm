using System.IO.Pipes;
using System.Windows;
using MyDM.App.ViewModels;
using MyDM.App.Views;
using MyDM.Core;
using MyDM.Core.Data;
using MyDM.Core.Engine;
using MyDM.Core.Queue;

namespace MyDM.App;

public partial class App : System.Windows.Application
{
    private MyDMDatabase? _database;
    private DownloadEngine? _engine;
    private QueueManager? _queueManager;
    private MainViewModel? _viewModel;
    private CancellationTokenSource? _pipeCts;

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
        if (long.TryParse(repository.GetSetting("GlobalSpeedLimit"), out var globalSpeedKb) && globalSpeedKb >= 0)
        {
            _engine.SpeedLimiter.GlobalLimit = globalSpeedKb * 1024;
        }

        // Initialize queue manager
        _queueManager = new QueueManager(_engine, repository);
        _queueManager.GetDefaultQueue();

        // Create and show main window
        _viewModel = new MainViewModel(_engine, repository, _queueManager);
        var mainWindow = new MainWindow(_viewModel);
        mainWindow.Show();

        // Start IPC pipe listener for NativeHost signals
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPipeListenerAsync(_pipeCts.Token));
    }

    /// <summary>
    /// Background listener: receives DOWNLOAD_STARTED signals from NativeHost via named pipe.
    /// Each connection is one-shot, so we loop and reconnect after each message.
    /// </summary>
    private async Task RunPipeListenerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    IpcConstants.PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct);

                if (!string.IsNullOrWhiteSpace(line) && IpcConstants.TryParse(line, out var signal, out var downloadId))
                {
                    if (signal == IpcConstants.DownloadStarted)
                    {
                        Dispatcher.Invoke(() => _viewModel?.HandleExternalDownload(downloadId));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient pipe error — retry after short delay
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _engine?.StopAll();
        _engine?.Dispose();
        _queueManager?.Dispose();
        _database?.Dispose();
        base.OnExit(e);
    }
}
