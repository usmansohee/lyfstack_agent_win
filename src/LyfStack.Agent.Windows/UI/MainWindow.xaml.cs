using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LyfStack.Agent.Windows.Configuration;
using LyfStack.Agent.Windows.Hosting;
using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;
using LyfStack.Agent.Windows.Services;
using LyfStack.Agent.Windows.Sync;
using MediaColor = System.Windows.Media.Color;

namespace LyfStack.Agent.Windows.UI;

public partial class MainWindow : Window
{
    private readonly SqliteSessionStore _store;
    private readonly ActivityTrackingService _trackingService;
    private readonly SyncService _syncService;
    private readonly ObservableCollection<SessionRow> _rows = new();
    private readonly ObservableCollection<TopAppRow> _topApps = new();
    private readonly ObservableCollection<CategoryStatRow> _categoryStats = new();
    private readonly ObservableCollection<string> _categoryRuleLines = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _suppressStartupToggle;
    private bool _syncingRangeCombos;
    private List<UsageSession> _filteredSessions = new();

    private static readonly SolidColorBrush Green = new(MediaColor.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush Amber = new(MediaColor.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush Red = new(MediaColor.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush Gray = new(MediaColor.FromRgb(0x94, 0xA3, 0xB8));

    public MainWindow(
        SqliteSessionStore store,
        ActivityTrackingService trackingService,
        SyncService syncService)
    {
        InitializeComponent();

        _store = store;
        _trackingService = trackingService;
        _syncService = syncService;

        HistoryGrid.ItemsSource = _rows;
        TopAppsList.ItemsSource = _topApps;
        CategoryStatsList.ItemsSource = _categoryStats;
        CategoryRulesList.ItemsSource = _categoryRuleLines;
        Icon = TrayIconFactory.CreateWindowIcon();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshUi();
        _refreshTimer.Start();

        _trackingService.SampleProcessed += OnSampleProcessed;
        _syncService.SyncCompleted += OnSyncCompleted;

        Loaded += (_, _) =>
        {
            LoadSettingsIntoUi();
            RefreshUi();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _trackingService.SampleProcessed -= OnSampleProcessed;
            _syncService.SyncCompleted -= OnSyncCompleted;
        };
    }

    public event Action? HideRequested;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        HideRequested?.Invoke();
    }

    private void OnSampleProcessed(ActivitySample sample, SessionUpdateResult? update) =>
        Dispatcher.InvokeAsync(RefreshUi);

    private void OnSyncCompleted(SyncResult result) =>
        Dispatcher.InvokeAsync(() =>
        {
            ApplySyncResult(result);
            RefreshUi();
        });

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => RefreshUi();

    private void HistoryRangeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingRangeCombos)
        {
            return;
        }

        SyncRangeCombo(HistoryRangeCombo, HistoryRangeCombo2);
        RefreshUi();
    }

    private void HistoryRangeCombo2_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _syncingRangeCombos)
        {
            return;
        }

        SyncRangeCombo(HistoryRangeCombo2, HistoryRangeCombo);
        RefreshUi();
    }

    private void SyncRangeCombo(System.Windows.Controls.ComboBox source, System.Windows.Controls.ComboBox target)
    {
        _syncingRangeCombos = true;
        try
        {
            string? tag = (source.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            foreach (ComboBoxItem item in target.Items)
            {
                if (item.Tag?.ToString() == tag)
                {
                    target.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _syncingRangeCombos = false;
        }
    }

    private void PauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _trackingService.IsPaused = !_trackingService.IsPaused;
        PauseButton.Content = _trackingService.IsPaused ? "Resume tracking" : "Pause tracking";
        FooterHint.Text = _trackingService.IsPaused
            ? "Tracking paused."
            : "History lives in its own tab. Sync uploads only new/changed sessions.";
        RefreshUi();
    }

    private async void SyncButton_OnClick(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        SyncDot.Fill = Amber;
        SyncStatusText.Text = "Syncing…";
        FooterHint.Text = "Uploading new/changed sessions…";
        SettingsHint.Text = "Syncing…";

        try
        {
            SyncResult result = await _syncService.SyncNowAsync("manual");
            ApplySyncResult(result);
            FooterHint.Text = result.Success
                ? result.SessionCount == 0
                    ? "Nothing new to sync."
                    : $"Synced {result.SessionCount} new/changed session(s)."
                : result.Message;
            SettingsHint.Text = result.Message;
        }
        finally
        {
            SyncButton.IsEnabled = true;
            RefreshUi();
        }
    }

    private void ExportCsv_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"lyfstack-activity-{DateTime.Now:yyyyMMdd}.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, SessionExportService.ToCsv(_filteredSessions, _syncService.Settings.CategoryRules));
            FooterHint.Text = $"Exported CSV ({_filteredSessions.Count} sessions).";
        }
    }

    private void ExportJson_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = $"lyfstack-activity-{DateTime.Now:yyyyMMdd}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, SessionExportService.ToJson(_filteredSessions, _syncService.Settings.CategoryRules));
            FooterHint.Text = $"Exported JSON ({_filteredSessions.Count} sessions).";
        }
    }

    private void AddCategoryRule_OnClick(object sender, RoutedEventArgs e)
    {
        string process = CategoryProcessBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(process))
        {
            return;
        }

        if (!process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            process += ".exe";
        }

        string category = (CategoryPickBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Other";
        string line = $"{process} → {category}";
        if (!_categoryRuleLines.Any(x => x.StartsWith(process, StringComparison.OrdinalIgnoreCase)))
        {
            _categoryRuleLines.Add(line);
        }

        CategoryProcessBox.Text = "";
    }

    private void RemoveCategoryRule_OnClick(object sender, RoutedEventArgs e)
    {
        if (CategoryRulesList.SelectedItem is string selected)
        {
            _categoryRuleLines.Remove(selected);
        }
    }

    private void StartupToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle)
        {
            return;
        }

        try
        {
            if (StartupToggle.IsChecked == true)
            {
                AgentPaths.EnsureDataDirectory();
                AgentDeployer.DeployCurrentBuild();
                StartupRegistration.Install(AgentDeployer.HiddenLauncherPath);
                StartupStatusText.Text = "On";
                SettingsHint.Text = "Startup enabled.";
            }
            else
            {
                StartupRegistration.Uninstall();
                StartupStatusText.Text = "Off";
                SettingsHint.Text = "Startup disabled.";
            }
        }
        catch (Exception ex)
        {
            _suppressStartupToggle = true;
            StartupToggle.IsChecked = StartupRegistration.IsInstalled();
            _suppressStartupToggle = false;
            SettingsHint.Text = $"Startup change failed: {ex.Message}";
        }

        RefreshStartupUi();
    }

    private void SaveSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = new AgentSettings
        {
            AutoSyncEnabled = AutoSyncToggle.IsChecked == true,
            AutoSyncIntervalMinutes = ReadIntervalMinutes(),
            SyncEndpointUrl = string.IsNullOrWhiteSpace(EndpointBox.Text)
                ? new AgentSettings().SyncEndpointUrl
                : EndpointBox.Text.Trim(),
            IgnoredProcesses = IgnoreListBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList(),
            CategoryRules = ParseCategoryRules()
        };

        AgentSettingsStore.Save(settings);
        _syncService.ApplySettings(settings);
        SettingsHint.Text = "Settings saved.";
        RefreshUi();
    }

    private List<CategoryRule> ParseCategoryRules()
    {
        var rules = new List<CategoryRule>();
        foreach (string line in _categoryRuleLines)
        {
            string[] parts = line.Split('→', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                rules.Add(new CategoryRule { ProcessName = parts[0], Category = parts[1] });
            }
        }

        return rules;
    }

    private void LoadSettingsIntoUi()
    {
        AgentSettings settings = _syncService.Settings;
        AutoSyncToggle.IsChecked = settings.AutoSyncEnabled;
        EndpointBox.Text = settings.SyncEndpointUrl;
        IgnoreListBox.Text = string.Join(Environment.NewLine, settings.IgnoredProcesses);

        _categoryRuleLines.Clear();
        foreach (CategoryRule rule in settings.CategoryRules)
        {
            _categoryRuleLines.Add($"{rule.ProcessName} → {rule.Category}");
        }

        foreach (ComboBoxItem item in SyncIntervalCombo.Items)
        {
            if (item.Tag?.ToString() == settings.AutoSyncIntervalMinutes.ToString())
            {
                SyncIntervalCombo.SelectedItem = item;
                break;
            }
        }

        PauseButton.Content = _trackingService.IsPaused ? "Resume tracking" : "Pause tracking";
        RefreshStartupUi();
    }

    private void RefreshStartupUi()
    {
        bool installed = StartupRegistration.IsInstalled();
        _suppressStartupToggle = true;
        StartupToggle.IsChecked = installed;
        _suppressStartupToggle = false;
        StartupStatusText.Text = installed ? "On" : "Off";
    }

    private int ReadIntervalMinutes()
    {
        if (SyncIntervalCombo.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out int minutes))
        {
            return minutes;
        }

        return 15;
    }

    private void RefreshUi()
    {
        AgentSettings settings = _syncService.Settings;
        UsageSession? current = _trackingService.SessionTracker.CurrentSession;

        if (_trackingService.IsPaused)
        {
            NowAppText.Text = "Paused";
            NowStateText.Text = "PAUSED";
            StatusText.Text = "Tracking paused";
            StatusDot.Fill = Amber;
            PauseButton.Content = "Resume tracking";
        }
        else if (current is not null)
        {
            NowAppText.Text = current.ApplicationName;
            NowStateText.Text = current.LastState.ToString().ToUpperInvariant();
            StatusText.Text = current.LastState == ActivityState.Idle ? "Idle" : "Tracking active";
            StatusDot.Fill = current.LastState == ActivityState.Idle ? Amber : Green;
            PauseButton.Content = "Pause tracking";
        }
        else
        {
            NowAppText.Text = "—";
            NowStateText.Text = "Waiting";
            StatusText.Text = "Tracking active";
            StatusDot.Fill = Green;
            PauseButton.Content = "Pause tracking";
        }

        IReadOnlyList<UsageSession> recent = _store.GetRecent(5000);
        DateTimeOffset todayLocal = DateTimeOffset.Now.Date;
        TimeSpan todayActive = TimeSpan.Zero;
        foreach (UsageSession session in recent)
        {
            if (session.StartedAt.ToLocalTime() >= todayLocal)
            {
                todayActive += session.ActiveDuration;
            }
        }

        TodayActiveText.Text = FormatDuration(todayActive);
        SessionCountText.Text = _store.Count().ToString();

        DateTimeOffset? rangeStart = GetSelectedHistoryRangeStart();
        string rangeLabel = GetSelectedHistoryRangeLabel();

        _filteredSessions = recent
            .Where(session => rangeStart is null || session.StartedAt.ToLocalTime() >= rangeStart.Value)
            .ToList();

        _rows.Clear();
        foreach (UsageSession session in _filteredSessions)
        {
            _rows.Add(SessionRow.FromSession(
                session,
                CategoryResolver.Resolve(session.ProcessName, settings.CategoryRules, session.ExecutablePath, session.ApplicationName)));
        }

        HistoryCountText.Text = _filteredSessions.Count == 1
            ? $"Showing 1 session · {rangeLabel}"
            : $"Showing {_filteredSessions.Count} sessions · {rangeLabel}";

        ActivitySummary summary = ActivitySummaryBuilder.Build(_filteredSessions, settings.CategoryRules, topN: 4);
        SummaryTitleText.Text = $"Summary · {rangeLabel}";
        SummaryActiveText.Text = FormatDuration(summary.TotalActive);
        SummaryIdleText.Text = FormatDuration(summary.TotalIdle);
        SummaryTrackedText.Text = FormatDuration(summary.TotalTracked);
        SummaryFocusText.Text = $"{summary.ActivePercent:0}%";
        SummaryMetaText.Text =
            $"{summary.SessionCount} sessions · {summary.UniqueApps} apps · Pending sync: {_store.CountPendingSync()}";

        _topApps.Clear();
        foreach (AppUsageStat app in summary.TopApps)
        {
            _topApps.Add(new TopAppRow
            {
                Name = app.ApplicationName,
                Category = app.Category,
                ActiveText = FormatDuration(app.Active)
            });
        }

        if (_topApps.Count == 0)
        {
            _topApps.Add(new TopAppRow { Name = "No activity yet", Category = "—", ActiveText = "" });
        }

        _categoryStats.Clear();
        foreach (CategoryUsageStat cat in summary.ByCategory.Take(4))
        {
            _categoryStats.Add(new CategoryStatRow
            {
                Category = cat.Category,
                ActiveText = FormatDuration(cat.Active),
                SessionsText = cat.SessionCount == 1 ? "1 session" : $"{cat.SessionCount} sessions"
            });
        }

        if (_categoryStats.Count == 0)
        {
            _categoryStats.Add(new CategoryStatRow { Category = "No data", ActiveText = "", SessionsText = "" });
        }

        LastSyncInfo? last = HttpActivitySyncClient.LoadLastSync();
        DeviceInfoSnapshot device = DeviceProfileStore.Capture(settings, last?.SyncedAt);
        ApplyDeviceInfo(device, last);

        if (last is null)
        {
            SyncDot.Fill = Gray;
            SettingsSyncDot.Fill = Gray;
            SyncStatusText.Text = "Not synced yet";
            LastSyncText.Text = "";
            SettingsLastSyncTitle.Text = "Not synced yet";
            SettingsLastSyncDetails.Text = "Manual or automatic sync will appear here.";
        }
        else
        {
            SolidColorBrush color = last.Success ? Green : Red;
            SyncDot.Fill = color;
            SettingsSyncDot.Fill = color;
            SyncStatusText.Text = last.Success ? "Connected" : "Last sync failed";
            LastSyncText.Text = $"{last.SyncedAt.ToLocalTime():MMM d HH:mm} · {last.SessionCount} sessions";
            SettingsLastSyncTitle.Text = last.Success ? "Connected" : "Failed";
            SettingsLastSyncDetails.Text =
                $"{last.SyncedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                $"Trigger: {last.Trigger ?? "unknown"} · Sessions: {last.SessionCount}\n" +
                $"{last.Message}";
        }

        if (_syncService.Settings.AutoSyncEnabled && _syncService.NextScheduledSyncUtc is DateTimeOffset next)
        {
            TimeSpan remaining = next - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            NextSyncText.Text =
                $"Next auto-sync: {next.ToLocalTime():HH:mm:ss} (in {FormatDuration(remaining)}) · every {_syncService.Settings.AutoSyncIntervalMinutes} min";
        }
        else
        {
            NextSyncText.Text = "Next auto-sync: off";
        }

        RefreshStartupUi();
    }

    private void ApplyDeviceInfo(DeviceInfoSnapshot device, LastSyncInfo? last)
    {
        DeviceNameText.Text = device.DeviceName;
        DeviceUserText.Text = device.UserName;
        DeviceModelText.Text = $"{device.Manufacturer} · {device.Model}";
        DeviceIdText.Text = device.DeviceId.ToString("D");
        DeviceOsText.Text = device.OsCaption;
        DeviceOsBuildText.Text = string.IsNullOrWhiteSpace(device.OsBuild) ? "—" : device.OsBuild;
        DeviceArchText.Text = device.Architecture;
        DeviceCpuText.Text = device.CpuName;
        DeviceRamText.Text = device.RamGb;
        DeviceGpuText.Text = device.GpuName;
        DeviceVersionText.Text = device.AgentVersion;
        DeviceInstalledText.Text = device.FirstInstalledAt.ToLocalTime().ToString("MMM d, yyyy  HH:mm");
        DeviceFirstSyncText.Text = device.FirstSyncedAt?.ToLocalTime().ToString("MMM d, yyyy  HH:mm") ?? "Not synced yet";
        DeviceLastSyncText.Text = last is null
            ? "No sync yet"
            : $"{last.SyncedAt.ToLocalTime():MMM d, yyyy  HH:mm} · {(last.Success ? "ok" : "failed")}";
        DeviceEndpointText.Text = device.SyncEndpoint;

        SettingsDeviceNameText.Text = device.DeviceName;
        SettingsInstalledText.Text = device.FirstInstalledAt.ToLocalTime().ToString("MMM d, yyyy");
        SettingsFirstSyncText.Text = device.FirstSyncedAt?.ToLocalTime().ToString("MMM d, yyyy") ?? "Not yet";
    }

    private void ApplySyncResult(SyncResult result)
    {
        SyncDot.Fill = result.Success ? Green : Red;
        SettingsSyncDot.Fill = result.Success ? Green : Red;
        SyncStatusText.Text = result.Success ? "Connected" : "Sync failed";
        LastSyncText.Text = result.SyncedAt is null
            ? ""
            : $"{result.SyncedAt.Value.ToLocalTime():MMM d HH:mm} · {result.SessionCount} sessions";
    }

    private DateTimeOffset? GetSelectedHistoryRangeStart()
    {
        string tag = (HistoryRangeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Today";
        DateTime today = DateTime.Now.Date;
        return tag switch
        {
            "Week" => new DateTimeOffset(StartOfWeekMonday(today)),
            "Month" => new DateTimeOffset(new DateTime(today.Year, today.Month, 1)),
            "Year" => new DateTimeOffset(new DateTime(today.Year, 1, 1)),
            "All" => null,
            _ => new DateTimeOffset(today)
        };
    }

    private static DateTime StartOfWeekMonday(DateTime date)
    {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-diff);
    }

    private string GetSelectedHistoryRangeLabel() =>
        (HistoryRangeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Today";

    private static string FormatDuration(TimeSpan value) =>
        value < TimeSpan.FromHours(1)
            ? value.ToString(@"mm\:ss")
            : value.ToString(@"h\:mm\:ss");
}

public sealed class TopAppRow
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string ActiveText { get; init; } = "";
}

public sealed class CategoryStatRow
{
    public string Category { get; init; } = "";
    public string ActiveText { get; init; } = "";
    public string SessionsText { get; init; } = "";
}
