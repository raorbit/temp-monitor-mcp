using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using TempMon.Core;
using TempMon.Desktop.Autostart;
using TempMon.Desktop.Hosting;
using TempMon.Desktop.Tray;
using TempMon.Desktop.ViewModels;

namespace TempMon.Desktop;

public partial class App : Application
{
    private const int PollSeconds = 3;
    private const string SingleInstanceName = @"Global\TempMon.Desktop.SingleInstance";
    private const string ShowEventName = @"Global\TempMon.Desktop.Show";

    private SensorPoller? _poller;
    private TempServer? _server;
    private DashboardViewModel? _vm;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private DispatcherTimer? _freshnessTimer;
    private TaskbarIcon? _trayIcon;
    private MainWindow? _window;
    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private volatile bool _shuttingDown;
    private bool _disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // OnStartup is async void, so any exception that escapes it becomes an unobserved exception on
        // the dispatcher and tears the elevated sole-sensor authority down with no banner and no log.
        // Guard the whole startup path and surface a fatal error instead of crashing silently.
        try
        {
            // Single-instance guard FIRST — before any SensorPoller/TempServer construction — so two
            // elevated launches can't both grab the sole LHM/WinRing0 reader or race the endpoint file.
            _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isNew);
            if (!isNew)
            {
                // The running instance owns the sensor authority — ask it to surface its dashboard, then
                // quit. This makes a re-launch feel like "bring the existing window back".
                SignalRunningInstance();
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown();
                return;
            }

            // Listen for a re-launch asking us to show the dashboard.
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            StartShowListener();

            bool elevated = ElevationHelper.IsElevated();
            bool autoStarted = e.Args.Any(a =>
                string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

            _poller = new SensorPoller();
            _vm = new DashboardViewModel(elevated, PollSeconds);
            _vm.Update(_poller.Poll());   // prime the UI with one read

            _window = new MainWindow { DataContext = _vm };
            if (!autoStarted)
                _window.Show();           // Windows-launched (--autostart): start hidden to tray.

            // Tray icon owns its own lifecycle; disposed once in TearDown (issues the Shell_NotifyIcon delete).
            _trayIcon = TrayIconFactory.Create(_vm, ShowDashboard, ExitApp, ToggleAutostart);

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
                // The server never bound: delete any discovery file so the MCP server doesn't trust a dead
                // endpoint (it gets a fast connection-refused on the default port instead of a bogus URL),
                // and surface the failure in the tray tooltip — not just the buried Endpoint label.
                EndpointFile.TryDelete();
                _vm.Endpoint = "http server failed — " + ex.Message;
                _vm.ServerFailed = true;
            }

            StartPollLoop();
            StartFreshnessTimer();
            RefreshAutoStartState();   // off the UI thread — schtasks shells a process
        }
        catch (Exception fatal)
        {
            // A throw before the inner try (mutex / event-handle / tray construction) would otherwise be
            // an invisible crash. Surface it, then exit — OnExit/TearDown are _disposed-guarded, so this
            // Shutdown() can't double-dispose.
            try
            {
                MessageBox.Show("TempMon failed to start:\n\n" + fatal, "TempMon",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* nothing more we can do */ }
            Shutdown();
        }
    }

    /// <summary>Ticks once a second to age the dashboard's "updated Ns ago" label off the snapshot
    /// timestamp, so a stalled poll loop shows as a growing (then amber) staleness instead of a frozen
    /// "just now" — the UI surface that should make a stuck reader obvious.</summary>
    private void StartFreshnessTimer()
    {
        _freshnessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _freshnessTimer.Tick += (_, _) => _vm?.RefreshFreshness();
        _freshnessTimer.Start();
    }

    private void StartPollLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Poll off the UI thread; marshal the immutable snapshot back for rendering. Runs while the
        // window is hidden to tray — the VM update refreshes the tray header/tooltip too. The marshal
        // carries the loop's token, so TearDown's Cancel() aborts a still-queued Update op and the loop
        // ends at once — instead of stalling on a dispatcher that's blocked trying to join this task.
        _pollTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = _poller!.Poll();
                    await Dispatcher.InvokeAsync(
                        () => _vm!.Update(snapshot), DispatcherPriority.Normal, token).Task;
                    await Task.Delay(TimeSpan.FromSeconds(PollSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation (token, or an aborted queued Update op) ends the loop cleanly.
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

    /// <summary>Background thread that waits for a re-launch signal and surfaces the dashboard.</summary>
    private void StartShowListener()
    {
        var ev = _showEvent;
        if (ev is null) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                if (_shuttingDown) break;
                try { Dispatcher.Invoke(ShowDashboard); }
                catch (Exception) { break; }   // dispatcher tearing down
            }
        })
        { IsBackground = true, Name = "TempMon-show-listener" };
        thread.Start();
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch
        {
            // Best effort — the running instance still owns the sensor authority regardless.
        }
    }

    /// <summary>Restores the dashboard window from the tray (icon double-click / Open dashboard / relaunch).</summary>
    private void ShowDashboard()
    {
        _window?.ShowFromTray();
    }

    /// <summary>Reads the real auto-start state off the UI thread and, when enabled, re-creates the
    /// task (idempotent /F) so a moved or reinstalled exe self-repairs its stored launch path.</summary>
    private void RefreshAutoStartState()
    {
        _ = Task.Run(() =>
        {
            bool enabled = AutoStartManager.IsEnabled();
            if (enabled) AutoStartManager.Enable();
            PushAutoStartState(enabled);
        });
    }

    /// <summary>Toggles the elevated ONLOGON auto-start task off the UI thread, then re-reads its
    /// real state so a failed schtasks call leaves the flyout check in the truthful position.</summary>
    private void ToggleAutostart()
    {
        if (_vm is null) return;
        bool turnOn = !_vm.AutoStartEnabled;

        _ = Task.Run(() =>
        {
            if (turnOn) AutoStartManager.Enable();
            else AutoStartManager.Disable();
            PushAutoStartState(AutoStartManager.IsEnabled());
        });
    }

    private void PushAutoStartState(bool enabled)
    {
        try { Dispatcher.Invoke(() => { if (_vm is not null) _vm.AutoStartEnabled = enabled; }); }
        catch (Exception) { /* dispatcher shutting down */ }
    }

    /// <summary>The single teardown path (tray Exit). Disposes everything exactly once, then exits.</summary>
    private void ExitApp()
    {
        TearDown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Safety net: if the process is ending without going through ExitApp, still release everything.
        TearDown();
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    /// <summary>Idempotent disposal of every owned resource; guarded so ExitApp + OnExit don't
    /// double-dispose (a second tray Dispose would otherwise risk a ghost-icon edge case).</summary>
    private void TearDown()
    {
        if (_disposed) return;
        _disposed = true;
        _shuttingDown = true;

        _freshnessTimer?.Stop();
        _showEvent?.Set();   // release the listener thread's WaitOne so it can exit

        // Cancel the poll loop, then briefly join it BEFORE blocking on the server. Cancel() aborts any
        // Update op the last iteration queued on this (UI) thread, so the loop ends without the dispatcher
        // having to pump — and the bounded waits mean a wedged shutdown can still never hang the exit.
        _cts?.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* cancellation / aggregate — fine */ }

        try { _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* bounded dispose */ }

        _poller?.Dispose();
        EndpointFile.TryDelete();
        _trayIcon?.Dispose();   // issues the Shell_NotifyIcon delete — no ghost icon
        _trayIcon = null;
        _showEvent?.Dispose();
        _showEvent = null;
    }
}
