using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LyfStack.Agent.Windows.Configuration;
using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;
using LyfStack.Agent.Windows.Services;
using LyfStack.Agent.Windows.Sync;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace LyfStack.Agent.Windows.UI;

internal sealed class TrayAppHost : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly WaitHandle _stopSignal;
    private readonly SqliteSessionStore _store;
    private readonly ActivityTrackingService _tracking;
    private readonly SyncService _sync;
    private readonly bool _startHidden;

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _pauseMenuItem;
    private MainWindow? _window;
    private Application? _app;
    private Icon? _icon;
    private DispatcherTimer? _trayTipTimer;

    public TrayAppHost(
        AgentOptions options,
        SqliteSessionStore store,
        ActivityTrackingService tracking,
        SyncService sync,
        CancellationTokenSource cts,
        WaitHandle stopSignal,
        bool startHidden)
    {
        _ = options;
        _store = store;
        _tracking = tracking;
        _sync = sync;
        _cts = cts;
        _stopSignal = stopSignal;
        _startHidden = startHidden;
    }

    public int Run()
    {
        _app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        _icon = TrayIconFactory.Create(connected: true);
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "LyfStack Agent",
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open LyfStack Agent", null, (_, _) => ShowWindow());
        _pauseMenuItem = new System.Windows.Forms.ToolStripMenuItem("Pause tracking");
        _pauseMenuItem.Click += (_, _) => TogglePause();
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add("Sync now", null, async (_, _) => await SyncFromTrayAsync());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        _window = new MainWindow(_store, _tracking, _sync);
        _window.HideRequested += () =>
        {
            _notifyIcon.ShowBalloonTip(
                1800,
                "LyfStack Agent",
                "Still running in the tray.",
                System.Windows.Forms.ToolTipIcon.Info);
        };

        _sync.StartPeriodicSync();
        _sync.SyncCompleted += result =>
        {
            if (!_startHidden && _window?.IsVisible == true)
            {
                return;
            }

            _notifyIcon?.ShowBalloonTip(
                2000,
                "LyfStack Sync",
                result.Message,
                result.Success
                    ? System.Windows.Forms.ToolTipIcon.Info
                    : System.Windows.Forms.ToolTipIcon.Error);
        };

        _trayTipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trayTipTimer.Tick += (_, _) => UpdateTrayTooltip();
        _trayTipTimer.Start();
        UpdateTrayTooltip();

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_stopSignal.WaitOne(500))
                {
                    _app.Dispatcher.Invoke(Quit);
                    break;
                }

                await Task.Delay(200);
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _tracking.RunAsync(_cts.Token, _stopSignal);
            }
            catch (Exception ex)
            {
                _app.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"Tracking stopped: {ex.Message}",
                        "LyfStack Agent",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Quit();
                });
            }
        });

        if (_startHidden)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
        }

        _app.Run();
        return 0;
    }

    private void TogglePause()
    {
        _tracking.IsPaused = !_tracking.IsPaused;
        if (_pauseMenuItem is not null)
        {
            _pauseMenuItem.Text = _tracking.IsPaused ? "Resume tracking" : "Pause tracking";
        }

        UpdateTrayTooltip();
        _notifyIcon?.ShowBalloonTip(
            1600,
            "LyfStack Agent",
            _tracking.IsPaused ? "Tracking paused" : "Tracking resumed",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void UpdateTrayTooltip()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        string app = _tracking.IsPaused
            ? "Paused"
            : (_tracking.SessionTracker.CurrentSession?.ApplicationName ?? "Waiting");

        DateTimeOffset today = DateTimeOffset.Now.Date;
        TimeSpan todayActive = TimeSpan.Zero;
        foreach (UsageSession session in _store.GetRecent(500))
        {
            if (session.StartedAt.ToLocalTime() >= today)
            {
                todayActive += session.ActiveDuration;
            }
        }

        string tip = $"LyfStack · {Trim(app, 18)} · today {Format(todayActive)}";
        _notifyIcon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task SyncFromTrayAsync()
    {
        try
        {
            SyncResult result = await _sync.SyncNowAsync("manual");
            _notifyIcon?.ShowBalloonTip(
                2500,
                "LyfStack Sync",
                result.Message,
                result.Success
                    ? System.Windows.Forms.ToolTipIcon.Info
                    : System.Windows.Forms.ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            _notifyIcon?.ShowBalloonTip(
                2500,
                "LyfStack Sync",
                ex.Message,
                System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private void Quit()
    {
        _cts.Cancel();
        _trayTipTimer?.Stop();
        _ = _sync.DisposeAsync();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }

        _app?.Shutdown();
    }

    public void Dispose()
    {
        _trayTipTimer?.Stop();
        _notifyIcon?.Dispose();
        _icon?.Dispose();
    }

    private static string Format(TimeSpan value) =>
        value < TimeSpan.FromHours(1) ? value.ToString(@"mm\:ss") : value.ToString(@"h\:mm\:ss");

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public static void TryDetachConsole()
    {
        try
        {
            FreeConsole();
        }
        catch
        {
        }
    }
}
