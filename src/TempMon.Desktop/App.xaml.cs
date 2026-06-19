using System.Windows;
using TempMon.Core;
using TempMon.Desktop.Hosting;
using TempMon.Desktop.ViewModels;

namespace TempMon.Desktop;

public partial class App : Application
{
    private const int PollSeconds = 3;

    private SensorPoller? _poller;
    private TempServer? _server;
    private DashboardViewModel? _vm;
    private CancellationTokenSource? _cts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool elevated = ElevationHelper.IsElevated();

        _poller = new SensorPoller();
        _vm = new DashboardViewModel(elevated, PollSeconds);
        _vm.Update(_poller.Poll());   // prime the UI with one read

        var window = new MainWindow { DataContext = _vm };
        window.Show();

        // Start the HTTP server and publish the discovery file.
        try
        {
            _server = new TempServer(_poller, elevated);
            var baseUrl = await _server.StartAsync();
            EndpointFile.Write(baseUrl, elevated);
            _vm.Endpoint = baseUrl.ToString().TrimEnd('/');
        }
        catch (Exception ex)
        {
            _vm.Endpoint = "http server failed — " + ex.Message;
        }

        StartPollLoop();
    }

    private void StartPollLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Poll off the UI thread; marshal the immutable snapshot back for rendering.
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = _poller!.Poll();
                    Dispatcher.Invoke(() => _vm!.Update(snapshot));
                    await Task.Delay(TimeSpan.FromSeconds(PollSeconds), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch
                {
                    // A transient read failure shouldn't kill the loop; try again next tick.
                    try { await Task.Delay(TimeSpan.FromSeconds(PollSeconds), token); }
                    catch (TaskCanceledException) { break; }
                }
            }
        }, token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _poller?.Dispose();
        EndpointFile.TryDelete();
        base.OnExit(e);
    }
}
