using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using AppConsistency.Core;
using AppConsistency.Interop;
using AppConsistency.Ui;
using HueThemeKit;

namespace AppConsistency;

public partial class MainWindow : Window
{
    /// <summary>How long a snapshot must stay unchanged before it is read: it may still be copying in.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>The fallback look at every app folder, for changes the watcher missed (shares, sleep).</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(30);

    private readonly ObservableCollection<AppAudit> _apps = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private AppAudit? _current;
    private string _scannedRoot = "";
    private bool _busy;
    private string? _noticePath;
    private readonly Dictionary<AppAudit, HashSet<FixTarget>> _queues = new();
    /// <summary>The files ticked in the removal picker, per app, while that fix is queued.</summary>
    private readonly Dictionary<AppAudit, List<string>> _unlistedPicks = new();
    private List<CheckRowView> _rows = new();

    /// <summary>Selected filter pill; null shows everything. Kept when switching apps.</summary>
    private CheckStatus? _filter;

    /// <summary>
    /// Selected header pill: the app list shows only apps with this worst status, or with
    /// License, only apps with a license to review; null shows all.
    /// </summary>
    private CheckStatus? _appFilter;

    // Watching the history folder for newer snapshots.
    private HistoryWatcher? _watcher;
    private bool _singleApp;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastPollUtc;
    private bool _tickRunning;
    /// <summary>A changed snapshot's fingerprint at the last look; it is read once two looks agree.</summary>
    private readonly Dictionary<string, SnapshotFingerprint> _settling = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        Standard.Current = _settings.ToStandard();
        AppList.ItemsSource = _apps;
        CollectionViewSource.GetDefaultView(_apps).Filter = item => item is AppAudit app && Shows(app);
        HueTitleBar.Attach(this);
        ShowVersion();
        ReleaseDateBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _timer.Tick += Timer_Tick;
        Loaded += OnLoaded;
        Closed += (_, _) => StopWatching();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var start = args.Length > 1 ? args[1] : _settings.LastPath;

        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
        {
            PathBox.Text = start;
            await ScanAsync();
        }
        else
        {
            SetStatus("Pick the history folder: one subfolder per app, each holding numbered snapshots like App-0001.zip.");
        }

        if (_settings.Problem is { } problem) SetStatus(problem, "Brush.Warn");
    }

    private bool Shows(AppAudit app) => _appFilter switch
    {
        null => true,
        CheckStatus.License => app.NeedsLicenseReview,
        var status => app.Overall == status
    };

    // ---------- Scanning ----------

    private async Task ScanAsync()
    {
        if (_busy) return;

        var root = PathBox.Text.Trim().Trim('"');
        if (root.Length == 0 || !Directory.Exists(root))
        {
            SetStatus("That folder doesn't exist. Check the path or use Browse.", "Brush.Warn");
            return;
        }

        root = Path.GetFullPath(root);
        PathBox.Text = root;
        _settings.LastPath = root;
        _settings.SavePath();

        StopWatching();
        SetBusy(true);
        _queues.Clear();
        _unlistedPicks.Clear();
        _apps.Clear();
        _appFilter = null;
        CollectionViewSource.GetDefaultView(_apps).Refresh();
        SummaryPills.Visibility = Visibility.Collapsed;
        ShowDetails(null);
        EmptyText.Text = "Scanning…";

        try
        {
            var candidates = await Task.Run(() => RepoLocator.Locate(root));
            if (candidates.Count == 0)
            {
                EmptyText.Text = "No app folders or numbered snapshots found here.";
                SetStatus("Nothing to check in " + root, "Brush.Warn");
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                SetStatus($"Checking {candidate.AppName} ({i + 1} of {candidates.Count})…", "Brush.Accent");
                _apps.Add(await Task.Run(() => Auditor.Audit(candidate)));
            }

            _scannedRoot = root;
            _singleApp = candidates.Count == 1 && SamePath(candidates[0].FolderPath, root);

            var first = _apps.FirstOrDefault(a => a.Overall == CheckStatus.Fail)
                        ?? _apps.FirstOrDefault(a => a.Overall == CheckStatus.Warn)
                        ?? _apps[0];
            AppList.SelectedItem = first;
            AppList.ScrollIntoView(first);

            StartWatching(root, _singleApp);
            SetStatus(SummaryLine() + (_watcher?.IsWatching == false
                ? " This folder can't be watched, so it's checked for newer snapshots every 30 seconds."
                : ""), "Brush.Ok");
            UpdateSummaryPills();
        }
        catch (Exception ex)
        {
            EmptyText.Text = "The scan stopped with an error.";
            SetStatus("Scan failed: " + ex.Message, "Brush.Error");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>The status bar: a line of text and a dot whose colour is a theme brush key.</summary>
    private void SetStatus(string text, string dotKey = "Brush.Stale")
    {
        StatusText.Text = text;
        StatusDot.SetResourceReference(ForegroundProperty, dotKey);
    }

    /// <summary>
    /// While busy, nothing else starts: no second action and no automatic rescan. The wait cursor
    /// is left off while a dialog is open within an action, so the dialog doesn't look stuck.
    /// </summary>
    private void SetBusy(bool busy, bool showCursor = true)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        CopyButton.IsEnabled = !busy && _apps.Count > 0;
        BackupAllButton.IsEnabled = !busy && _apps.Any(a => a.IsZipSnapshot);
        CollectReadmeButton.IsEnabled = !busy && _apps.Any(a => a.SnapshotPath is not null && a.Readmes.Count + a.Licenses.Count > 0);
        Mouse.OverrideCursor = busy && showCursor ? Cursors.AppStarting : null;
    }

    /// <summary>Runs work off the window's thread with the wait cursor showing.</summary>
    private static async Task<T> Working<T>(Func<T> work)
    {
        Mouse.OverrideCursor = Cursors.AppStarting;
        try { return await Task.Run(work); }
        finally { Mouse.OverrideCursor = null; }
    }

    private static async Task Working(Action work) => await Working(() => { work(); return true; });

    private void ShowVersion()
    {
        VersionText.Text = "version " + AppInfo.Version;
        SdkText.Text = AppInfo.SdkVersion is { } sdk ? "built with SDK " + sdk : "";
        SdkText.Visibility = AppInfo.SdkVersion is null ? Visibility.Collapsed : Visibility.Visible;
        RuntimeText.Text = "runtime " + AppInfo.RuntimeVersion;
        StandardText.Text = "standard: " + Standard.Current.Source;
        StandardText.ToolTip = Standard.Current.Source == "built-in"
            ? "No settings of your own: the built-in standard, with no approved packages and no private files."
            : AppSettings.FilePath;
    }

    /// <summary>The gear: edit the standard. Saving applies it and checks every app again.</summary>
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true, showCursor: false);
        bool saved;
        try
        {
            saved = SettingsWindow.Ask(this, _settings, _apps.ToList());
        }
        finally
        {
            SetBusy(false);
        }
        if (!saved) return;

        Standard.Current = _settings.ToStandard();
        ShowVersion();
        if (_scannedRoot.Length > 0 || PathBox.Text.Trim().Length > 0) await ScanAsync();
        else SetStatus("Settings saved.", "Brush.Ok");
    }

    /// <summary>
    /// The header pills: apps by worst status, the same counts as the status line, then the apps
    /// with a license to review in indigo. A zero count goes grey and can't be clicked; the active
    /// filter gets its text colour as the edge and a glow.
    /// </summary>
    private void UpdateSummaryPills()
    {
        void Set(ToggleButton pill, CheckStatus status, int count, string word, string family)
        {
            bool active = _appFilter == status;

            pill.Content = $"{count} {word}";
            pill.IsChecked = active;
            pill.IsEnabled = count > 0 || active;
            pill.Tag = active ? "Glow" : null;   // the pill template lights the dot for "Glow"

            pill.SetResourceReference(ForegroundProperty, count > 0 ? $"Brush.{family}Text" : "Brush.TextDim");
            pill.SetResourceReference(BackgroundProperty, count > 0 ? $"Brush.{family}Soft" : "Brush.Raised");
            pill.SetResourceReference(BorderBrushProperty,
                count == 0 ? "Brush.Line" : active ? $"Brush.{family}Text" : $"Brush.{family}Edge");
        }

        Set(PassPill, CheckStatus.Pass, _apps.Count(a => a.Overall == CheckStatus.Pass), "passed", "Ok");
        Set(WarnPill, CheckStatus.Warn, _apps.Count(a => a.Overall == CheckStatus.Warn), "warned", "Warn");
        Set(FailPill, CheckStatus.Fail, _apps.Count(a => a.Overall == CheckStatus.Fail), "failed", "Error");
        Set(LicensePill, CheckStatus.License, _apps.Count(a => a.NeedsLicenseReview), "license", "Indigo");
        SummaryPills.Visibility = _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Header pill: show only those apps; the same pill again shows all.</summary>
    private void AppPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton pill) return;
        var status = pill == PassPill ? CheckStatus.Pass
            : pill == WarnPill ? CheckStatus.Warn
            : pill == LicensePill ? CheckStatus.License
            : CheckStatus.Fail;

        _appFilter = _appFilter == status ? null : status;
        CollectionViewSource.GetDefaultView(_apps).Refresh();
        UpdateSummaryPills();

        // Keep the selected app when it's still in the list; otherwise open the first one shown.
        var visible = _apps.Where(Shows).ToList();
        if (AppList.SelectedItem is not AppAudit selected || !visible.Contains(selected))
        {
            var first = visible.FirstOrDefault();
            AppList.SelectedItem = first;
            if (first is not null) AppList.ScrollIntoView(first);
        }

        var note = _appFilter switch
        {
            CheckStatus.Pass => " Showing only passed apps.",
            CheckStatus.Warn => " Showing only warned apps.",
            CheckStatus.Fail => " Showing only failed apps.",
            CheckStatus.License => " Showing only apps with a license to review.",
            _ => ""
        };
        SetStatus(SummaryLine() + note, "Brush.Ok");
    }

    private string SummaryLine()
    {
        int fail = _apps.Count(a => a.Overall == CheckStatus.Fail);
        int warn = _apps.Count(a => a.Overall == CheckStatus.Warn);
        int pass = _apps.Count(a => a.Overall == CheckStatus.Pass);
        int skipped = _apps.Count(a => a.Skipped);
        int license = _apps.Count(a => a.NeedsLicenseReview);

        var text = $"Checked {_apps.Count} app{(_apps.Count == 1 ? "" : "s")}: {pass} passed, {warn} warned, {fail} failed";
        if (skipped > 0) text += $", {skipped} without snapshots";
        if (license > 0) text += $"; {license} with a license to review";
        return text + ".";
    }

    // ---------- Watching for newer snapshots ----------

    private void StartWatching(string root, bool singleApp)
    {
        StopWatching();
        _watcher = new HistoryWatcher(root, singleApp);
        _settling.Clear();
        _lastPollUtc = DateTime.UtcNow;
        _timer.Start();
    }

    private void StopWatching()
    {
        _timer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _settling.Clear();
    }

    /// <summary>
    /// Once a second: every app folder the watcher saw change, or the periodic check found changed,
    /// is looked at once it has been quiet for a moment. A snapshot is read only when two looks a
    /// moment apart agree (it has finished copying in), and then only that app is rescanned.
    /// Nothing runs while a scan or an action is busy; it waits its turn.
    /// </summary>
    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_tickRunning || _busy || _watcher is null || _scannedRoot.Length == 0) return;
        _tickRunning = true;

        try
        {
            var watcher = _watcher;
            var now = DateTime.UtcNow;

            if (watcher.TakeMissedEvents() || now - _lastPollUtc >= PollEvery)
            {
                _lastPollUtc = now;
                var root = _scannedRoot;
                var single = _singleApp;
                var known = _apps.Select(a => (a.FolderPath, a.Fingerprint, a.Pending)).ToList();
                foreach (var folder in await Task.Run(() => PollForChanges(root, single, known)))
                    watcher.MarkFolder(folder, now - Quiet);
            }

            foreach (var (folder, last) in watcher.Touched().OrderBy(t => t.LastChange))
            {
                if (_busy || _watcher != watcher) break;
                if (DateTime.UtcNow - last < Quiet) continue;

                var single = _singleApp;
                var (candidate, print) = await Task.Run(() => Look(folder, single));
                if (_busy || _watcher != watcher) break;   // an action or a scan started meanwhile
                var app = FindApp(folder);

                if (candidate is null)
                {
                    watcher.Clear(folder, last);
                    _settling.Remove(folder);
                    if (app is not null && !single) RemoveApp(app);
                    continue;
                }

                if (app is not null && !app.Pending && Equals(print, app.Fingerprint))
                {
                    watcher.Clear(folder, last);
                    _settling.Remove(folder);
                    continue;
                }

                // Read it only once it has stopped changing: the same size and time on two looks.
                if (!_settling.TryGetValue(folder, out var seen) || !Equals(seen, print))
                {
                    _settling[folder] = print!;
                    watcher.MarkFolder(folder, DateTime.UtcNow);
                    continue;
                }

                _settling.Remove(folder);
                watcher.Clear(folder, last);

                SetBusy(true, showCursor: false);
                try
                {
                    await RescanAsync(candidate, app, announce: true);
                }
                finally
                {
                    SetBusy(false);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't check for newer snapshots: " + ex.Message, "Brush.Warn");
        }
        finally
        {
            _tickRunning = false;
        }
    }

    /// <summary>An app folder as it is now: its newest snapshot and that snapshot's fingerprint.</summary>
    private static (AppCandidate? Candidate, SnapshotFingerprint? Print) Look(string folder, bool singleApp)
    {
        var candidate = RepoLocator.LocateApp(folder, singleApp);
        return (candidate, candidate is null ? null : RepoLocator.Fingerprint(candidate));
    }

    /// <summary>The periodic check: app folders whose newest snapshot differs from the one checked, and new app folders.</summary>
    private static List<string> PollForChanges(string root, bool singleApp,
        List<(string FolderPath, SnapshotFingerprint? Fingerprint, bool Pending)> known)
    {
        var changed = new List<string>();

        foreach (var (folder, print, pending) in known)
        {
            try
            {
                var (_, now) = Look(folder, singleApp);
                if (pending || !Equals(now, print)) changed.Add(folder);
            }
            catch
            {
                changed.Add(folder);
            }
        }

        if (!singleApp)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (!Path.GetFileName(dir).StartsWith('.') && !known.Any(k => SamePath(k.FolderPath, dir)))
                        changed.Add(dir);
            }
            catch
            {
                // The folder is unreachable right now; the next check tries again.
            }
        }

        return changed;
    }

    /// <summary>
    /// Checks one app again and puts the result where the old one was. Queued fixes stay queued
    /// when the findings didn't change. The selection, the filters and the header follow.
    /// </summary>
    private async Task<AppAudit> RescanAsync(AppCandidate candidate, AppAudit? old, bool announce)
    {
        if (announce) SetStatus($"Rescanning {candidate.AppName}…", "Brush.Accent");

        var audit = await Task.Run(() => Auditor.Audit(candidate));
        old ??= FindApp(candidate.FolderPath);
        bool wasSelected = old is not null && ReferenceEquals(AppList.SelectedItem, old);

        if (old is not null)
        {
            bool same = old.Signature == audit.Signature;
            if (_queues.Remove(old, out var queue) && same) _queues[audit] = queue;
            if (_unlistedPicks.Remove(old, out var picks) && same) _unlistedPicks[audit] = picks;
        }

        int index = old is null ? -1 : _apps.IndexOf(old);
        if (index >= 0) _apps[index] = audit;
        else InsertSorted(audit);

        CollectionViewSource.GetDefaultView(_apps).Refresh();
        if (wasSelected) AppList.SelectedItem = audit;
        UpdateSummaryPills();

        if (audit.Pending) _watcher?.MarkFolder(candidate.FolderPath, DateTime.UtcNow);

        if (announce)
        {
            var what = old is null ? "new app, checked"
                : audit.Pending ? $"{audit.SnapshotName} is still being written, checked again shortly"
                : !string.Equals(old.SnapshotName, audit.SnapshotName, StringComparison.OrdinalIgnoreCase)
                    ? $"rescanned, {old.VersionText} → {audit.VersionText}"
                    : $"rescanned, {audit.SnapshotName} changed";
            var findings = old is null || audit.Pending ? ""
                : old.Signature == audit.Signature ? ". Same findings" : ". Findings changed";
            SetStatus($"{audit.AppName}: {what}{findings}.", audit.Pending ? "Brush.Warn" : "Brush.Ok");
        }

        return audit;
    }

    private void InsertSorted(AppAudit audit)
    {
        int at = 0;
        while (at < _apps.Count && string.Compare(_apps[at].AppName, audit.AppName, StringComparison.OrdinalIgnoreCase) < 0) at++;
        _apps.Insert(at, audit);
    }

    private void RemoveApp(AppAudit app)
    {
        _queues.Remove(app);
        _unlistedPicks.Remove(app);
        _apps.Remove(app);
        UpdateSummaryPills();
        SetStatus($"{app.AppName}: the folder is gone, so it's off the list.", "Brush.Warn");
    }

    private AppAudit? FindApp(string folder) => _apps.FirstOrDefault(a => SamePath(a.FolderPath, folder));

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Before an action: looks again at the apps it uses. A newer snapshot is checked first. When its
    /// findings are the same the action carries on with it; when they differ the action stops, so you
    /// see them before anything is written.
    /// </summary>
    private async Task<bool> EnsureCurrentAsync(IReadOnlyList<AppAudit> apps, string action)
    {
        var changed = new List<(AppAudit Old, AppAudit New)>();
        var single = _singleApp;

        foreach (var app in apps.ToList())
        {
            var (candidate, print) = await Task.Run(() => Look(app.FolderPath, single));
            if (candidate is null) continue;   // gone: the action itself reports what it can't read
            if (!app.Pending && Equals(print, app.Fingerprint)) continue;

            SetStatus($"Checking {app.AppName} again before {action}…", "Brush.Accent");
            changed.Add((app, await RescanAsync(candidate, app, announce: false)));
        }

        if (changed.Count == 0) return true;

        var different = changed.Where(c => c.New.Pending || c.New.Signature != c.Old.Signature).ToList();
        if (different.Count == 0)
        {
            SetStatus("Picked up a newer snapshot with the same findings before " + action + ": " +
                      string.Join(", ", changed.Select(c => $"{c.New.AppName} {c.Old.VersionText} → {c.New.VersionText}")) + ".",
                "Brush.Accent");
            return true;
        }

        ShowNotice(
            $"{string.Join(", ", different.Select(d => d.New.AppName))} changed before {action}: a newer snapshot came in " +
            "with different findings and has been checked. Look at the findings, then try again. Nothing was saved.",
            success: false);
        return false;
    }

    // ---------- Details ----------

    private void ShowDetails(AppAudit? audit)
    {
        _current = audit;
        ClearNotice();

        if (audit is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        StageButton.IsEnabled = audit.IsZipSnapshot && !audit.Pending;
        DetailName.Text = audit.AppName;
        DetailVersion.Text = audit.AppVersion ?? (audit.Skipped || audit.Pending ? "" : "no version");
        DetailVersion.SetResourceReference(TextBlock.ForegroundProperty, audit.AppVersion is null ? "Brush.TextMuted" : "Brush.TextDim");
        AdminBadge.Visibility = audit.Elevated ? Visibility.Visible : Visibility.Collapsed;
        TestsBadge.Visibility = audit.HasTests ? Visibility.Visible : Visibility.Collapsed;
        // The word says what the state is; the amber only ranks it.
        bool offList = audit.Packages == PackageState.Foreign;
        PackagesBadgeButton.Visibility = audit.Packages == PackageState.None ? Visibility.Collapsed : Visibility.Visible;
        PackagesBadgeText.Text = offList ? "Packages off the list" : "Approved packages";
        PackagesBadge.SetResourceReference(TextElement.ForegroundProperty,
            offList ? "Brush.WarnText" : "Brush.TextDim");
        PackagesBadgeButton.ToolTip = (offList
            ? "Uses packages that aren't on the approved list"
            : "Uses packages from the approved list") + ". Click to see the approved list.";
        DetailSource.Text = audit.SnapshotPath ?? audit.FolderPath;
        DetailSummary.Text = audit.Skipped
            ? "No numbered snapshots in this folder."
            : $"Snapshot {audit.VersionText}, newest of {audit.SnapshotCount}.";

        RefreshChecks();
        UpdateApplyButton();
    }

    private void RefreshChecks()
    {
        if (_current is null) return;

        UpdateFilterBar();
        _queues.TryGetValue(_current, out var queue);

        _rows = _current.Results
            .Where(r => _filter is null || r.Status == _filter)
            .Select(r => new CheckRowView(r, r.CanFix && queue?.Contains(r.Target) == true))
            .ToList();

        var groups = _rows
            .GroupBy(row => row.Result.Category)
            .Select(g => new CheckGroup(g.Key, g.ToList()))
            .ToList();

        CheckGroups.ItemsSource = groups;

        AllPassText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllPassText.Text = _current.Skipped ? "Nothing to check." : _filter switch
        {
            CheckStatus.Fail => "Nothing needs attention in this app.",
            CheckStatus.Warn => "Nothing to review in this app.",
            CheckStatus.License => "No license finding in this app.",
            CheckStatus.Info => "No informational findings in this app.",
            CheckStatus.Pass => "No checks pass in this app.",
            _ => "Nothing to check."
        };
    }

    // ---------- Events ----------

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the history folder" };
        var current = PathBox.Text.Trim().Trim('"');
        if (Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FolderName;
            await ScanAsync();
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ScanAsync();
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowDetails(AppList.SelectedItem as AppAudit);

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        CheckStatus? clicked = (sender as FrameworkElement)?.Tag is string tag
                               && Enum.TryParse<CheckStatus>(tag, out var status)
            ? status
            : null;

        // Clicking the active pill (or All) goes back to showing everything.
        _filter = clicked is null || clicked == _filter ? null : clicked;
        RefreshChecks();
    }

    private void UpdateFilterBar()
    {
        if (_current is null) return;

        int Count(CheckStatus status) => _current.Results.Count(r => r.Status == status);
        int fail = Count(CheckStatus.Fail), warn = Count(CheckStatus.Warn), license = Count(CheckStatus.License),
            info = Count(CheckStatus.Info), pass = Count(CheckStatus.Pass);

        FilterAll.Content = $"All {_current.Results.Count}";
        FilterFailText.Text = $"{fail} need attention";
        FilterWarnText.Text = $"{warn} to review";
        FilterLicenseText.Text = $"{license} license";
        FilterInfoText.Text = $"{info} info";
        FilterPassText.Text = $"{pass} good";

        // Empty pills stay clickable while selected, so the filter can always be switched off.
        FilterFail.IsEnabled = fail > 0 || _filter == CheckStatus.Fail;
        FilterWarn.IsEnabled = warn > 0 || _filter == CheckStatus.Warn;
        FilterLicense.IsEnabled = license > 0 || _filter == CheckStatus.License;
        FilterInfo.IsEnabled = info > 0 || _filter == CheckStatus.Info;
        FilterPass.IsEnabled = pass > 0 || _filter == CheckStatus.Pass;

        FilterAll.IsChecked = _filter is null;
        FilterFail.IsChecked = _filter == CheckStatus.Fail;
        FilterWarn.IsChecked = _filter == CheckStatus.Warn;
        FilterLicense.IsChecked = _filter == CheckStatus.License;
        FilterInfo.IsChecked = _filter == CheckStatus.Info;
        FilterPass.IsChecked = _filter == CheckStatus.Pass;
    }

    private void Reveal_Click(object sender, RoutedEventArgs e) =>
        RevealInExplorer(_current?.SnapshotPath ?? _current?.FolderPath);

    private void NoticeReveal_Click(object sender, RoutedEventArgs e) => RevealInExplorer(_noticePath);

    private void RevealInExplorer(string? path)
    {
        if (path is null) return;

        try
        {
            var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open Explorer: " + ex.Message, "Brush.Error");
        }
    }

    // ---------- Release date ----------

    /// <summary>The day Stage and Apply date every file with, at 00:00:00; false when the field isn't a valid date.</summary>
    private bool TryReleaseDay(out DateTime day) =>
        DateTime.TryParseExact(ReleaseDateBox.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
        && CleanZip.IsValidDay(day);

    private void ReleaseDate_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TryReleaseDay(out _)) ReleaseDateBox.ClearValue(BorderBrushProperty);
        else ReleaseDateBox.SetResourceReference(BorderBrushProperty, "Brush.Warn");
    }

    private bool ReleaseDayOrNotice(out DateTime day)
    {
        if (TryReleaseDay(out day)) return true;
        ShowNotice("The release date isn't a valid date. Use yyyy-MM-dd, between 1980 and 2107. Nothing was saved.", success: false);
        ReleaseDateBox.Focus();
        return false;
    }

    // ---------- Stage and fix ----------

    private async void Stage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _current is not { } selected) return;
        if (!ReleaseDayOrNotice(out var day)) return;

        SetBusy(true, showCursor: false);
        try
        {
            if (!await EnsureCurrentAsync([selected], "staging")) return;
            if (_current is not { IsZipSnapshot: true, Pending: false, SnapshotPath: { } source } audit) return;

            // Private files: keep them (App.zip, for RepoBuilder and you) or leave them out (App-PUBLISH.zip).
            bool forPublishing = false;
            if (audit.PrivateFiles.Count > 0)
            {
                var choice = StageChoiceWindow.Ask(this, audit.AppName, audit.PrivateFiles,
                    Staging.StagedName(source), Staging.PublishName(source));
                if (choice == StageChoice.Cancel) return;
                forPublishing = choice == StageChoice.ForPublishing;

                // A public README that still names a private file would point at nothing.
                if (forPublishing && audit.PrivateMentions.Count > 0
                    && !ConfirmWindow.Ask(this,
                        "The README still mentions private files",
                        "The copy for publishing leaves these files out, so these README lines would point at nothing " +
                        "and tell the public the notes exist. Reword them first, or stage anyway.",
                        string.Join("\n", audit.PrivateMentions),
                        "Stage anyway"))
                    return;
            }

            var target = Path.Combine(KnownFolders.Downloads,
                forPublishing ? Staging.PublishName(source) : Staging.StagedName(source));
            if (ReportIfExists(target)) return;

            try
            {
                await Working(() => Staging.Stage(source, target, day, forPublishing));
                var privateNote = audit.PrivateFiles.Count == 0 ? ""
                    : forPublishing ? $" Left out for publishing: {string.Join(", ", audit.PrivateFiles)}."
                    : " Private files kept.";
                ShowNotice($"Staged {Path.GetFileName(source)} to Downloads as {Path.GetFileName(target)}, every file dated {day:yyyy-MM-dd} 00:00:00." + privateNote,
                    success: true, target);
            }
            catch (Exception ex)
            {
                if (!ReportIfExists(target)) ShowNotice("Couldn't stage the snapshot: " + ex.Message, success: false);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// One zip in Downloads with the newest snapshot of every app inside, each under the name
    /// Stage to Downloads would give it, and everything inside dated today at 00:00:00. The file
    /// name keeps the full time. The history folder is only read; nothing is overwritten.
    /// </summary>
    private async void BackupAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true, showCursor: false);
        try
        {
            if (!await EnsureCurrentAsync(_apps.Where(a => a.IsZipSnapshot).ToList(), "the backup")) return;

            var sources = _apps
                .Where(a => a.IsZipSnapshot && !a.Pending && a.SnapshotPath is not null)
                .Select(a => (a.AppName, Source: a.SnapshotPath!))
                .ToList();
            if (sources.Count == 0) return;

            var now = DateTime.Now;
            var target = Path.Combine(KnownFolders.Downloads, Staging.BackupName(now));
            if (ReportIfExists(target)) return;

            var progress = new Progress<string>(line => SetStatus(line, "Brush.Accent"));

            Staging.BackupResult result;
            try
            {
                result = await Working(() => Staging.Backup(sources, target, now.Date, progress));
            }
            catch (Exception ex)
            {
                ShowNotice("The backup failed and nothing was saved: " + ex.Message, success: false);
                return;
            }

            var text = new StringBuilder(
                $"Backed up {result.Added.Count} of {sources.Count} app{(sources.Count == 1 ? "" : "s")} to Downloads as {Path.GetFileName(target)}, " +
                $"everything inside dated {now:yyyy-MM-dd} 00:00:00.");
            if (result.Duplicates.Count > 0)
                text.Append($" Left out, same name as another app: {string.Join(", ", result.Duplicates)}.");
            if (result.Failed.Count > 0)
                text.Append($" Couldn't add: {string.Join("; ", result.Failed)}.");

            ShowNotice(text.ToString(), success: result.Duplicates.Count == 0 && result.Failed.Count == 0, target);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// One zip in Downloads with the readmes and license files from every app's newest snapshot,
    /// one folder per app, dated today at 00:00:00. When an app has more than one readme, you tick
    /// which ones go in first; license files always go in. Read-only on history.
    /// </summary>
    private async void CollectReadme_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true, showCursor: false);
        try
        {
            if (!await EnsureCurrentAsync(_apps.Where(a => a.SnapshotPath is not null && !a.Skipped).ToList(), "collecting")) return;

            var withSnapshot = _apps.Where(a => a.SnapshotPath is not null && !a.Skipped && !a.Pending).ToList();
            var withFiles = withSnapshot.Where(a => a.Readmes.Count + a.Licenses.Count > 0).ToList();
            if (withFiles.Count == 0) return;

            var several = withFiles.Where(a => a.Readmes.Count > 1).ToList();
            Dictionary<string, List<string>>? picked = null;
            if (several.Count > 0)
            {
                picked = ReadmePickWindow.Ask(this,
                    several.Select(a => (a.AppName, (IReadOnlyList<string>)a.Readmes)).ToList(),
                    withFiles.Count - several.Count);
                if (picked is null) return;
            }

            var sources = new List<ReadmeCollector.Source>();
            var untickedApps = new List<string>();
            foreach (var app in withFiles)
            {
                IReadOnlyList<string> readmes = app.Readmes;
                if (app.Readmes.Count > 1)
                {
                    if (picked is null || !picked.TryGetValue(app.AppName, out var chosen) || chosen.Count == 0)
                    {
                        untickedApps.Add(app.AppName);
                        readmes = [];
                    }
                    else
                    {
                        readmes = chosen;
                    }
                }
                if (readmes.Count + app.Licenses.Count == 0) continue;
                sources.Add(new ReadmeCollector.Source(app.AppName, app.SnapshotPath!, app.IsZipSnapshot, readmes, app.Licenses));
            }
            if (sources.Count == 0) return;

            var now = DateTime.Now;
            var target = Path.Combine(KnownFolders.Downloads, ReadmeCollector.CollectName(now));
            if (ReportIfExists(target)) return;

            var progress = new Progress<string>(line => SetStatus(line, "Brush.Accent"));

            ReadmeCollector.CollectResult result;
            try
            {
                result = await Working(() => ReadmeCollector.Collect(sources, target, now.Date, progress));
            }
            catch (Exception ex)
            {
                ShowNotice("Collecting the readmes and licenses failed and nothing was saved: " + ex.Message, success: false);
                return;
            }

            int licenses = result.Added.Count(a => LicenseCheck.IsLicenseName(RepoSnapshot.NameOf(a)));
            int readmeCount = result.Added.Count - licenses;
            int apps = result.Added.Select(a => a[..a.IndexOf('/')]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var text = new StringBuilder(
                $"Collected {readmeCount} readme{(readmeCount == 1 ? "" : "s")} and {licenses} license file{(licenses == 1 ? "" : "s")} " +
                $"from {apps} app{(apps == 1 ? "" : "s")} to Downloads as {Path.GetFileName(target)}.");

            var noReadme = withSnapshot.Where(a => a.Readmes.Count == 0).Select(a => a.AppName).ToList();
            if (noReadme.Count > 0)
                text.Append($" No readme: {string.Join(", ", noReadme)}.");
            if (untickedApps.Count > 0)
                text.Append($" No readme ticked: {string.Join(", ", untickedApps)}.");
            if (result.Failed.Count > 0)
                text.Append($" Couldn't add: {string.Join("; ", result.Failed)}.");

            ShowNotice(text.ToString(), success: result.Failed.Count == 0, target);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if ((sender as FrameworkElement)?.DataContext is not CheckRowView row || !row.CanFix) return;
        if (_busy)
        {
            row.IsQueued = !row.IsQueued;   // put the toggle back; nothing changes while busy
            return;
        }

        var audit = _current;
        var queue = QueueFor(audit);

        // Removing unlisted files needs a decision per file: nothing goes unless it's ticked.
        if (row.Fix == FixKind.RemoveUnlistedFiles && row.IsQueued)
        {
            List<string>? picks;
            SetBusy(true, showCursor: false);
            try
            {
                picks = UnlistedPickWindow.Ask(this, audit.AppName, audit.Unlisted);
            }
            finally
            {
                SetBusy(false);
            }

            if (picks is null || _current != audit)
            {
                row.IsQueued = false;
                UpdateApplyButton();
                return;
            }
            _unlistedPicks[audit] = picks;
        }
        else if (row.Fix == FixKind.RemoveUnlistedFiles)
        {
            _unlistedPicks.Remove(audit);
        }

        // The ToggleButton has already moved; the queue follows it.
        if (row.IsQueued) queue.Add(row.Target);
        else queue.Remove(row.Target);
        UpdateApplyButton();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _current is not { } selected) return;
        if (!ReleaseDayOrNotice(out var day)) return;

        SetBusy(true, showCursor: false);
        try
        {
            if (!await EnsureCurrentAsync([selected], "applying the fixes")) return;

            var audit = _current;
            if (audit is not { IsZipSnapshot: true, Pending: false, SnapshotPath: { } source }) return;
            if (!_queues.TryGetValue(audit, out var queue) || queue.Count == 0) return;
            _unlistedPicks.TryGetValue(audit, out var picks);

            var target = Path.Combine(KnownFolders.Downloads, Staging.StagedName(source));
            if (ReportIfExists(target)) return;

            FixPlan plan;
            try
            {
                var fixes = queue.ToList();
                var ticked = picks?.ToList();
                plan = await Working(() => FixPlanner.Plan(source, target, fixes, ticked, day));
            }
            catch (Exception ex)
            {
                ShowNotice("Couldn't prepare the fixes: " + ex.Message, success: false);
                return;
            }

            if (!plan.HasChanges)
            {
                ShowNotice("None of the queued fixes change anything. " + string.Join(" ", plan.Skipped), success: false);
                return;
            }

            var details = new StringBuilder();
            details.AppendLine($"From  {source}");
            details.AppendLine($"To    {target}");
            foreach (var (heading, lines) in plan.Sections)
            {
                details.AppendLine();
                details.AppendLine(heading);
                foreach (var line in lines) details.AppendLine("  " + line);
            }
            if (plan.Skipped.Count > 0)
            {
                details.AppendLine();
                details.AppendLine("Not applied");
                foreach (var line in plan.Skipped) details.AppendLine("  " + line);
            }

            int changes = plan.ChangeCount;
            var confirmed = ConfirmWindow.Ask(this,
                $"Apply fixes to {audit.AppName}",
                "The snapshot is copied to Downloads without its version number, and these changes are made to the copy. " +
                $"Every file in it is dated {day:yyyy-MM-dd} 00:00:00; everything else stays as it is, and your history folder isn't changed.",
                details.ToString().TrimEnd(),
                "Apply fixes");
            if (!confirmed) return;

            // The preview was of this snapshot. If a newer one arrived meanwhile, don't write a stale copy.
            var single = _singleApp;
            var (_, print) = await Task.Run(() => Look(audit.FolderPath, single));
            if (!Equals(print, audit.Fingerprint))
            {
                _watcher?.MarkFolder(audit.FolderPath, DateTime.UtcNow - Quiet);
                ShowNotice("A newer snapshot arrived while the preview was open, so nothing was saved. " +
                           "It's checked in a moment; look at the findings, then apply again.", success: false);
                return;
            }

            try
            {
                await Working(() => FixPlanner.Apply(plan));
                queue.Clear();
                _unlistedPicks.Remove(audit);
                foreach (var row in _rows) row.IsQueued = false;
                UpdateApplyButton();
                ShowNotice(
                    $"Saved to Downloads as {Path.GetFileName(target)} with {changes} change{(changes == 1 ? "" : "s")}, " +
                    $"every file dated {day:yyyy-MM-dd} 00:00:00. Rebuild it with your tool.",
                    success: true, target);
            }
            catch (Exception ex)
            {
                if (!ReportIfExists(target)) ShowNotice("The fixes failed and nothing was saved: " + ex.Message, success: false);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private HashSet<FixTarget> QueueFor(AppAudit audit)
    {
        if (!_queues.TryGetValue(audit, out var queue))
            _queues[audit] = queue = new HashSet<FixTarget>();
        return queue;
    }

    private void UpdateApplyButton()
    {
        int count = _current is not null && _queues.TryGetValue(_current, out var queue) ? queue.Count : 0;
        ApplyButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.Content = $"Apply {count} fix{(count == 1 ? "" : "es")}";
    }

    /// <summary>An existing file in Downloads may be newer or different, so it's never touched.</summary>
    private bool ReportIfExists(string target)
    {
        if (!File.Exists(target)) return false;
        ShowNotice($"Downloads already has {Path.GetFileName(target)}. Nothing was changed.", success: false, target);
        return true;
    }

    private void ShowNotice(string text, bool success, string? path = null)
    {
        NoticeText.Text = text;
        NoticePanel.SetResourceReference(StyleProperty, success ? "Hue.Callout.Ok" : "Hue.Callout.Warn");
        _noticePath = path;
        NoticeButton.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        NoticePanel.Visibility = Visibility.Visible;
        SetStatus(text, success ? "Brush.Ok" : "Brush.Warn");
    }

    private void ClearNotice()
    {
        NoticePanel.Visibility = Visibility.Collapsed;
        _noticePath = null;
    }

    private void Packages_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        var approved = StandardsRules.Allowlist.ToList();
        var used = _current.PackageUses.Where(p => p.State != PackageUseState.TestOnly).ToList();
        var testOnly = _current.PackageUses.Where(p => p.State == PackageUseState.TestOnly).ToList();
        var unused = approved
            .Where(a => !used.Any(u => u.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        int width = Math.Max(
            _current.PackageUses.Count == 0 ? 0 : _current.PackageUses.Max(p => p.Name.Length),
            approved.Max(p => p.Name.Length));

        var lines = new List<(string Text, string? BrushKey)>();

        void Section(string heading)
        {
            if (lines.Count > 0) lines.Add(("", null));
            lines.Add((heading, "Brush.Text"));
        }

        if (used.Count > 0)
        {
            Section($"{_current.AppName} uses");
            foreach (var package in used)
            {
                var (mark, brush) = package.State switch
                {
                    PackageUseState.Ok => ("ok ", "Brush.OkText"),
                    PackageUseState.Old => ("old", "Brush.WarnText"),
                    _ => ("no ", "Brush.ErrorText")
                };
                lines.Add(($"  {mark}  {package.Name.PadRight(width)}  {(package.Version ?? "version not set"),-14}  {package.Note}", brush));
            }
        }

        if (testOnly.Count > 0)
        {
            Section("Used by tests only");
            foreach (var package in testOnly)
                lines.Add(($"       {package.Name.PadRight(width)}  {(package.Version ?? ""),-14}  {package.Note}", "Brush.TextMuted"));
        }

        if (unused.Count > 0)
        {
            Section(used.Count > 0 ? "Approved, not used here" : "Approved third-party packages");
            foreach (var package in unused)
                lines.Add(($"       {package.Name.PadRight(width)}  {(package.MinVersion + "+"),-14}  {package.Reason}", "Brush.TextMuted"));
        }

        ConfirmWindow.ShowLines(this,
            "Third-party packages",
            "Green is approved and current enough, amber is older than the approved minimum, red isn't approved at all. " +
            "Grey is approved but unused here, or used only by tests.",
            lines);
    }

    private void CopyFindings_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        var note = _filter switch
        {
            CheckStatus.Fail => "Showing only findings that need attention.",
            CheckStatus.Warn => "Showing only findings to review.",
            CheckStatus.License => "Showing only the license finding.",
            CheckStatus.Info => "Showing only informational findings.",
            CheckStatus.Pass => "Showing only checks that pass.",
            _ => null
        };

        try
        {
            Clipboard.SetText(ReportBuilder.BuildApp(_current, _rows.Select(r => r.Result).ToList(), note));
            ShowNotice($"{_current.AppName}'s findings copied to the clipboard.", success: true);
        }
        catch (Exception ex)
        {
            ShowNotice("Couldn't copy the findings: " + ex.Message, success: false);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportBuilder.Build(_scannedRoot, _apps));
            SetStatus("Report copied to the clipboard.", "Brush.Ok");
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't copy the report: " + ex.Message, "Brush.Error");
        }
    }
}
