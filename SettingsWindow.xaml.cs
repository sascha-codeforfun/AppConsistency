using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppConsistency.Core;
using HueThemeKit;
using Shape = System.Windows.Shapes.Path;

namespace AppConsistency;

/// <summary>
/// The standard, editable. One card per setting: its name, its key in AppConsistency.json, what it
/// does and its default. The lock at the top right says where the value comes from: locked, the
/// built-in default applies and the key isn't in the file; unlocked, the setting is written to the
/// file and can be edited. Lists have one row per entry with an Active switch (off keeps it in the
/// file without effect) and Remove, and beside each entry how many apps of the last scan it affects.
/// Those numbers come from the apps in memory; nothing is read from or written to disk for them.
/// Save writes the file and the window's owner rescans. Cancel changes nothing.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<AppAudit> _apps;
    private readonly List<Card> _cards = new();

    private SettingsWindow(AppSettings settings, IReadOnlyList<AppAudit> apps)
    {
        InitializeComponent();
        HueTitleBar.Attach(this);
        _settings = settings;
        _apps = apps;

        int scanned = apps.Count(a => !a.Skipped && a.SnapshotPath is not null);
        FileText.Text = $"Saved in {AppSettings.FilePath}. " +
                        (scanned == 0 ? "No scan yet, so the entries have no numbers." : $"Numbers from the last scan of {scanned} app{(scanned == 1 ? "" : "s")}.");
        if (settings.Problem is { } problem) ShowProblem(problem + " Saving replaces it with these settings.");

        var file = settings.Standard;

        Add(new PackageListCard(this, file?.ApprovedPackages));

        Add(new NameListCard(this, "Private files", "Standard.PrivateFiles",
            "Your own working notes. Kept in every zip you build and in the backup; left out only when you stage for publishing. " +
            "File names, matched at any depth.",
            Standard.DefaultPrivateFiles, file?.PrivateFiles, NameKind.FileName, "File name, e.g. NOTES.md",
            (f, list) => f.PrivateFiles = list));

        Add(new NameListCard(this, "Whitelist", "Standard.Whitelist",
            "Files every repo may hold. Anything else the build doesn't use is reported as unlisted. " +
            "A pattern matches a file name (*.cs, app_*.png); with a slash, the end of a path.",
            Standard.DefaultWhitelist, file?.Whitelist, NameKind.Pattern, "Pattern, e.g. *.resx",
            (f, list) => f.Whitelist = list));

        Add(new NameListCard(this, "Web packages", "Standard.WebPackages",
            "Packages that bring web content into the app. An app that asks for admin rights and uses one of these is flagged.",
            Standard.DefaultWebPackages, file?.WebPackages, NameKind.Package, "Package name",
            (f, list) => f.WebPackages = list));

        Add(new FieldsCard(this, "Minimum target framework", "Standard.MinimumTargetFramework",
            "The lowest target framework a project may use. Newer passes, as long as the platform is the same.",
            [new Field("Target framework", Standard.DefaultMinimumTargetFramework, file?.MinimumTargetFramework,
                v => Standard.ParseTargetFramework(v) is null ? $"\"{v}\" isn't a target framework like net10.0-windows." : null)],
            (f, v) => f.MinimumTargetFramework = v[0]));

        var icon = file?.Icon;
        Add(new FieldsCard(this, "Icon and header", "Standard.Icon",
            "The exe icon every app sets as ApplicationIcon, and the header image beside its name, with its size in pixels.",
            [
                new Field("Exe icon", Standard.DefaultIcon.ExeIcon, icon?.ExeIcon, FileNameError),
                new Field("Header image", Standard.DefaultIcon.HeaderImage, icon?.HeaderImage, FileNameError),
                new Field("Header image size (px)", Standard.DefaultIcon.HeaderImageSize.ToString(), icon is null ? null : icon.HeaderImageSize.ToString(),
                    v => int.TryParse(v, out var n) && n is >= 16 and <= 4096 ? null : "The header image size is a whole number from 16 to 4096.")
            ],
            (f, v) => f.Icon = new IconSettings { ExeIcon = v[0], HeaderImage = v[1], HeaderImageSize = int.Parse(v[2]) }));

        var publish = file?.Publish;
        var p = Standard.DefaultPublish;
        Add(new FieldsCard(this, "Publish properties", "Standard.Publish",
            "What every app's .csproj sets for a single self-contained exe, and its DebugType. The fixes write these values.",
            [
                new Field("RuntimeIdentifier", p.RuntimeIdentifier, publish?.RuntimeIdentifier, NotEmpty("RuntimeIdentifier")),
                new Field("SelfContained", p.SelfContained, publish?.SelfContained, NotEmpty("SelfContained")),
                new Field("PublishSingleFile", p.PublishSingleFile, publish?.PublishSingleFile, NotEmpty("PublishSingleFile")),
                new Field("IncludeNativeLibrariesForSelfExtract", p.IncludeNativeLibrariesForSelfExtract, publish?.IncludeNativeLibrariesForSelfExtract, NotEmpty("IncludeNativeLibrariesForSelfExtract")),
                new Field("EnableCompressionInSingleFile", p.EnableCompressionInSingleFile, publish?.EnableCompressionInSingleFile, NotEmpty("EnableCompressionInSingleFile")),
                new Field("DebugType", p.DebugType, publish?.DebugType, NotEmpty("DebugType"))
            ],
            (f, v) => f.Publish = new PublishSettings
            {
                RuntimeIdentifier = v[0], SelfContained = v[1], PublishSingleFile = v[2],
                IncludeNativeLibrariesForSelfExtract = v[3], EnableCompressionInSingleFile = v[4], DebugType = v[5]
            }));

        var release = file?.Release;
        var r = Standard.DefaultRelease;
        Add(new FieldsCard(this, "Release names", "Standard.Release",
            "Names of what goes to Downloads: the suffix of the copy staged for publishing, and the backup and readme collection. " +
            "A {…} part is the time it's made, in .NET date format.",
            [
                new Field("Publish suffix", r.PublishSuffix, release?.PublishSuffix, NamePartError),
                new Field("Backup name", r.BackupName, release?.BackupName, ZipNameError),
                new Field("Readme collection name", r.ReadmeCollectionName, release?.ReadmeCollectionName, ZipNameError)
            ],
            (f, v) => f.Release = new ReleaseSettings { PublishSuffix = v[0], BackupName = v[1], ReadmeCollectionName = v[2] },
            v => $"For example: App{v[0]}.zip · {Standard.FormatName(v[1], DateTime.Now)} · {Standard.FormatName(v[2], DateTime.Now)}"));

        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>True when the settings were saved; the caller applies them and rescans.</summary>
    public static bool Ask(Window owner, AppSettings settings, IReadOnlyList<AppAudit> apps) =>
        new SettingsWindow(settings, apps) { Owner = owner }.ShowDialog() == true;

    private void Add(Card card)
    {
        _cards.Add(card);
        card.Render();
        CardsPanel.Children.Add(card.Root);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = _cards.Where(c => c.Unlocked).SelectMany(c => c.Validate()).ToList();
        if (errors.Count > 0)
        {
            ShowProblem("Not saved. " + string.Join(" ", errors));
            return;
        }

        var file = new StandardFile();
        foreach (var card in _cards.Where(c => c.Unlocked)) card.Write(file);

        var previous = _settings.Standard;
        _settings.Standard = file.IsEmpty ? null : file;
        try
        {
            _settings.SaveAll();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _settings.Standard = previous;
            ShowProblem($"Couldn't save {AppSettings.FilePath}: {ex.Message} Nothing was changed. " +
                        "A folder that can't be written (Program Files) can't hold the settings.");
        }
    }

    private void ShowProblem(string text)
    {
        ProblemText.Text = text;
        ProblemPanel.Visibility = Visibility.Visible;
    }

    // ---------------- Validation helpers ----------------

    private static string? FileNameError(string v) =>
        v.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? $"\"{v}\" isn't a file name." : null;

    private static Func<string, string?> NotEmpty(string label) =>
        v => v.Trim().Length == 0 ? $"{label} can't be empty." : null;

    private static string? NamePartError(string v) =>
        v.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? $"\"{v}\" can't be part of a file name." : null;

    private static string? ZipNameError(string v)
    {
        var outside = Regex.Replace(v, @"\{[^{}]*\}", "");
        if (outside.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return $"\"{v}\" isn't a usable file name.";
        if (!v.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return $"\"{v}\" has to end in .zip.";
        if (!v.Contains('{')) return $"\"{v}\" needs a {{time}} part, or every run would have the same name.";
        return null;
    }

    // ---------------- Building blocks ----------------

    private static TextBlock Text(string text, string style, bool wrap = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    private static Border RowBorder()
    {
        var border = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1)
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Base");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return border;
    }

    private static Button Ghost(string text)
    {
        var button = new Button { Content = text };
        button.SetResourceReference(StyleProperty, "Hue.Button.Ghost");
        return button;
    }

    /// <summary>"used by 1 of 41 apps", a bar, and a button that lists the apps beneath.</summary>
    private static UIElement UsageLine(Usage usage)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };

        var summary = Text(usage.Total == 0 ? "no scan yet" : usage.Summary, "Text.Caption");
        summary.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(summary);

        var bar = new ProgressBar
        {
            Width = 160, Minimum = 0, Maximum = 1, Value = usage.Share,
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = usage.Total > 0
        };
        AutomationProperties.SetName(bar, usage.Summary);
        line.Children.Add(bar);

        panel.Children.Add(line);
        if (usage.Lines.Count == 0)
        {
            line.ToolTip = usage.Total == 0 ? "Scan first to see which apps this affects." : "No app of the last scan.";
            return panel;
        }

        line.ToolTip = string.Join("\n", usage.Lines.Take(25)) + (usage.Lines.Count > 25 ? $"\n… and {usage.Lines.Count - 25} more" : "");

        var details = Text(string.Join("\n", usage.Lines), "Text.Mono", wrap: true);
        details.Margin = new Thickness(0, 8, 0, 0);
        details.Visibility = Visibility.Collapsed;

        var toggle = Ghost("Show apps");
        toggle.Margin = new Thickness(10, 0, 0, 0);
        toggle.Padding = new Thickness(8, 0, 8, 0);
        toggle.Click += (_, _) =>
        {
            bool show = details.Visibility != Visibility.Visible;
            details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = show ? "Hide apps" : "Show apps";
        };
        line.Children.Add(toggle);
        panel.Children.Add(details);
        return panel;
    }

    // ---------------- Cards ----------------

    /// <summary>The frame every setting shares: title, key, description, default, and the lock.</summary>
    private abstract class Card
    {
        protected readonly SettingsWindow Window;
        protected readonly StackPanel Body = new() { Margin = new Thickness(0, 16, 0, 0) };
        private readonly Button _lock = new() { Width = 36, Height = 32, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        private readonly string _title, _key, _defaultText;

        public bool Unlocked { get; private set; }
        public Border Root { get; } = new() { Margin = new Thickness(0, 0, 0, 16) };

        protected Card(SettingsWindow window, string title, string key, string description, string defaultText, bool unlocked)
        {
            Window = window;
            _title = title;
            _key = key;
            _defaultText = defaultText;
            Unlocked = unlocked;

            Root.SetResourceReference(StyleProperty, "Hue.Card");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel();
            var name = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            head.Children.Add(name);
            var keyText = Text(key, "Text.Mono");
            keyText.Margin = new Thickness(0, 3, 0, 0);
            head.Children.Add(keyText);
            var about = Text(description, "Text.Caption", wrap: true);
            about.Margin = new Thickness(0, 8, 0, 0);
            about.MaxWidth = 560;
            about.HorizontalAlignment = HorizontalAlignment.Left;
            head.Children.Add(about);
            grid.Children.Add(head);

            var def = Text("Default: " + defaultText, "Text.Caption", wrap: true);
            def.MaxWidth = 300;
            def.Margin = new Thickness(24, 2, 12, 0);
            def.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(def, 1);
            grid.Children.Add(def);

            _lock.SetResourceReference(StyleProperty, "Hue.Button.Ghost");
            _lock.Click += OnLock;
            Grid.SetColumn(_lock, 2);
            grid.Children.Add(_lock);

            Grid.SetRow(Body, 1);
            Grid.SetColumnSpan(Body, 3);
            grid.Children.Add(Body);

            Root.Child = grid;
            UpdateLock();
        }

        public abstract void Render();
        protected abstract void ResetToDefault();
        public abstract void Write(StandardFile file);
        public virtual IEnumerable<string> Validate() => [];

        private void UpdateLock()
        {
            var icon = new Shape { Data = (Geometry)Window.FindResource(Unlocked ? "Icon.Unlock" : "Icon.Lock") };
            icon.SetResourceReference(StyleProperty, "Hue.Icon");
            _lock.Content = icon;
            _lock.SetResourceReference(ForegroundProperty, Unlocked ? "Brush.Accent" : "Brush.TextMuted");
            _lock.ToolTip = Unlocked
                ? "Set in the file. Click to use the default again."
                : "The default applies. Click to set it in the file.";
            AutomationProperties.SetName(_lock, Unlocked ? $"Use the default for {_title}" : $"Set {_title} in the file");
        }

        private void OnLock(object sender, RoutedEventArgs e)
        {
            if (Unlocked)
            {
                if (!HueConfirm.Ask(Window, "Use the default again?",
                        $"{_title} ({_key}) is removed from the file when you save, and the default applies again: {_defaultText}.",
                        "Use default", destructive: true))
                    return;
                ResetToDefault();
            }
            Unlocked = !Unlocked;
            UpdateLock();
            Render();
        }

        /// <summary>A short line under a list, e.g. what an empty list means, or why an add was refused.</summary>
        protected static TextBlock Note(string text, bool warn = false)
        {
            var note = Text(text, "Text.Caption", wrap: true);
            note.Margin = new Thickness(0, 0, 0, 8);
            if (warn) note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.WarnText");
            return note;
        }

        /// <summary>An entry row: the content on the left, Active and Remove on the right.</summary>
        protected Border Row(UIElement content, bool enabled, Action<bool> setEnabled, Action remove)
        {
            var border = RowBorder();
            var dock = new DockPanel { LastChildFill = true };

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);

            var active = new HueSwitch
            {
                Content = "Active",
                IsChecked = enabled,
                IsEnabled = Unlocked,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Off keeps the entry in the file without effect, for a temporary change."
            };
            content.Opacity = enabled ? 1 : 0.55;
            active.Checked += (_, _) => { setEnabled(true); content.Opacity = 1; };
            active.Unchecked += (_, _) => { setEnabled(false); content.Opacity = 0.55; };
            right.Children.Add(active);

            var removeButton = Ghost("Remove");
            removeButton.Margin = new Thickness(8, 0, 0, 0);
            removeButton.IsEnabled = Unlocked;
            removeButton.Click += (_, _) => { remove(); Render(); };
            right.Children.Add(removeButton);

            dock.Children.Add(right);
            dock.Children.Add(content);
            border.Child = dock;
            return border;
        }
    }

    /// <summary>Approved packages: name, minimum version and reason, with how many apps use each.</summary>
    private sealed class PackageListCard : Card
    {
        private List<PackageEntry> _entries;
        private string? _error;

        public PackageListCard(SettingsWindow window, List<PackageEntry>? fromFile)
            : base(window, "Approved packages", "Standard.ApprovedPackages",
                "Third-party NuGet packages you accept. Any other package fails the third-party packages check, " +
                "and so does one below its minimum version. Packages in libraries the app references count too.",
                "none", fromFile is not null)
        {
            _entries = (fromFile ?? Standard.DefaultApprovedPackages.ToList()).Select(p => p.Clone()).ToList();
        }

        protected override void ResetToDefault() => _entries = Standard.DefaultApprovedPackages.Select(p => p.Clone()).ToList();

        public override void Write(StandardFile file) => file.ApprovedPackages = _entries.Select(p => p.Clone()).ToList();

        public override IEnumerable<string> Validate()
        {
            foreach (var p in _entries.Where(p => !IsVersion(p.MinVersion)))
                yield return $"{p.Name}: \"{p.MinVersion}\" isn't a version like 1.2.3.";
        }

        public override void Render()
        {
            Body.Children.Clear();
            if (_entries.Count == 0)
                Body.Children.Add(Note("No approved packages: every third-party package fails."));

            foreach (var entry in _entries.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var left = new StackPanel();
                var name = Text(entry.Name, "Text.Mono");
                name.FontSize = 13;
                name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                left.Children.Add(name);

                if (Unlocked)
                {
                    var fields = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                    fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                    fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var min = new TextBox { Text = entry.MinVersion, ToolTip = "Minimum version" };
                    min.TextChanged += (_, _) => entry.MinVersion = min.Text.Trim();
                    AutomationProperties.SetName(min, $"{entry.Name} minimum version");
                    var reason = new TextBox { Text = entry.Reason, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Why it's approved" };
                    reason.TextChanged += (_, _) => entry.Reason = reason.Text.Trim();
                    AutomationProperties.SetName(reason, $"{entry.Name} reason");
                    Grid.SetColumn(reason, 1);
                    fields.Children.Add(min);
                    fields.Children.Add(reason);
                    left.Children.Add(fields);
                }
                else
                {
                    var detail = Text($"from {entry.MinVersion} · {entry.Reason}", "Text.Body", wrap: true);
                    detail.Margin = new Thickness(0, 4, 0, 0);
                    left.Children.Add(detail);
                }

                left.Children.Add(UsageLine(SettingsUsage.Package(entry.Name, Window._apps)));
                Body.Children.Add(Row(left, entry.Enabled, on => entry.Enabled = on, () => _entries.Remove(entry)));
            }

            if (!Unlocked) return;

            if (_error is not null) Body.Children.Add(Note(_error, warn: true));

            var add = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var newName = new TextBox { ToolTip = "Package name, as in the PackageReference" };
            var newMin = new TextBox { Margin = new Thickness(8, 0, 0, 0), ToolTip = "Minimum version, e.g. 1.2.3" };
            var newReason = new TextBox { Margin = new Thickness(8, 0, 0, 0), ToolTip = "Why it's approved: what .NET doesn't offer" };
            AutomationProperties.SetName(newName, "New package name");
            AutomationProperties.SetName(newMin, "New package minimum version");
            AutomationProperties.SetName(newReason, "New package reason");
            var addButton = new Button { Content = "Add package", Margin = new Thickness(8, 0, 0, 0) };
            addButton.Click += (_, _) =>
            {
                var n = newName.Text.Trim();
                _error = n.Length == 0 ? "Enter the package name to add."
                    : _entries.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ? $"{n} is already on the list."
                    : !IsVersion(newMin.Text.Trim()) ? "Enter a minimum version like 1.2.3."
                    : null;
                if (_error is null)
                    _entries.Add(new PackageEntry { Name = n, MinVersion = newMin.Text.Trim(), Reason = newReason.Text.Trim() });
                Render();
            };
            Grid.SetColumn(newMin, 1);
            Grid.SetColumn(newReason, 2);
            Grid.SetColumn(addButton, 3);
            add.Children.Add(newName);
            add.Children.Add(newMin);
            add.Children.Add(newReason);
            add.Children.Add(addButton);

            var labels = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            foreach (var col in add.ColumnDefinitions.Take(3))
                labels.ColumnDefinitions.Add(new ColumnDefinition { Width = col.Width });
            string[] captions = ["Package", "Minimum version", "Reason"];
            for (int i = 0; i < captions.Length; i++)
            {
                var caption = Text(captions[i], "Text.Caption");
                caption.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
                Grid.SetColumn(caption, i);
                labels.Children.Add(caption);
            }
            Body.Children.Add(labels);
            Body.Children.Add(add);
        }

        private static bool IsVersion(string v) => Regex.IsMatch(v, @"^\d+(\.\d+){0,3}$");
    }

    private enum NameKind { FileName, Pattern, Package }

    /// <summary>A list of names or patterns: private files, the whitelist, web packages.</summary>
    private sealed class NameListCard : Card
    {
        private readonly IReadOnlyList<NameEntry> _defaults;
        private readonly NameKind _kind;
        private readonly string _addHint;
        private readonly Action<StandardFile, List<NameEntry>> _write;
        private List<NameEntry> _entries;
        private string? _error;

        public NameListCard(SettingsWindow window, string title, string key, string description,
                            IReadOnlyList<NameEntry> defaults, List<NameEntry>? fromFile, NameKind kind, string addHint,
                            Action<StandardFile, List<NameEntry>> write)
            : base(window, title, key, description,
                defaults.Count == 0 ? "none" : string.Join(", ", defaults.Select(d => d.Name)), fromFile is not null)
        {
            _defaults = defaults;
            _kind = kind;
            _addHint = addHint;
            _write = write;
            _entries = (fromFile ?? defaults.ToList()).Select(e => e.Clone()).ToList();
        }

        protected override void ResetToDefault() => _entries = _defaults.Select(e => e.Clone()).ToList();

        public override void Write(StandardFile file) => _write(file, _entries.Select(e => e.Clone()).ToList());

        public override void Render()
        {
            Body.Children.Clear();
            if (_entries.Count == 0)
                Body.Children.Add(Note(_kind == NameKind.FileName ? "None: nothing is private." : "None."));

            foreach (var entry in _entries)
            {
                var left = new StackPanel();
                var name = Text(entry.Name, "Text.Mono");
                name.FontSize = 13;
                name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                left.Children.Add(name);
                left.Children.Add(UsageLine(_kind switch
                {
                    NameKind.FileName => SettingsUsage.FileName(entry.Name, Window._apps),
                    NameKind.Pattern => SettingsUsage.Pattern(entry.Name, Window._apps),
                    _ => SettingsUsage.Package(entry.Name, Window._apps)
                }));
                Body.Children.Add(Row(left, entry.Enabled, on => entry.Enabled = on, () => _entries.Remove(entry)));
            }

            if (!Unlocked) return;

            if (_error is not null) Body.Children.Add(Note(_error, warn: true));

            var add = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
            var addButton = new Button { Content = "Add", Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
            DockPanel.SetDock(addButton, Dock.Right);
            var box = new TextBox { ToolTip = _addHint };
            AutomationProperties.SetName(box, _addHint);
            void AddEntry()
            {
                var n = box.Text.Trim();
                _error = n.Length == 0 ? "Enter a name to add."
                    : _entries.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ? $"{n} is already on the list."
                    : _kind == NameKind.FileName && n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? $"\"{n}\" isn't a file name."
                    : null;
                if (_error is null) _entries.Add(new NameEntry { Name = n });
                Render();
            }
            addButton.Click += (_, _) => AddEntry();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                AddEntry();
            };
            add.Children.Add(addButton);
            add.Children.Add(box);

            var caption = Text(_addHint, "Text.Caption");
            caption.Margin = new Thickness(0, 8, 0, 4);
            Body.Children.Add(caption);
            Body.Children.Add(add);
        }
    }

    /// <param name="Default">The built-in value, as text.</param>
    /// <param name="FromFile">The file's value, or null when the setting isn't in the file.</param>
    /// <param name="Check">An error message for a value that can't be used, or null.</param>
    private sealed record Field(string Label, string Default, string? FromFile, Func<string, string?> Check);

    /// <summary>One or more text fields that are saved together: the target framework, the icon names, publish properties, release names.</summary>
    private sealed class FieldsCard : Card
    {
        private readonly IReadOnlyList<Field> _fields;
        private readonly Action<StandardFile, string[]> _write;
        private readonly Func<string[], string>? _preview;
        private string[] _values;

        public FieldsCard(SettingsWindow window, string title, string key, string description,
                          IReadOnlyList<Field> fields, Action<StandardFile, string[]> write, Func<string[], string>? preview = null)
            : base(window, title, key, description,
                string.Join(", ", fields.Select(f => fields.Count == 1 ? f.Default : $"{f.Label} {f.Default}")),
                fields.Any(f => f.FromFile is not null))
        {
            _fields = fields;
            _write = write;
            _preview = preview;
            _values = fields.Select(f => f.FromFile ?? f.Default).ToArray();
        }

        protected override void ResetToDefault() => _values = _fields.Select(f => f.Default).ToArray();

        public override void Write(StandardFile file) => _write(file, _values.Select(v => v.Trim()).ToArray());

        public override IEnumerable<string> Validate() =>
            _fields.Select((f, i) => f.Check(_values[i].Trim())).Where(e => e is not null).Select(e => e!);

        public override void Render()
        {
            Body.Children.Clear();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock? preview = _preview is null ? null : Text(_preview(_values), "Text.Caption", wrap: true);

            for (int i = 0; i < _fields.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Text(_fields[i].Label, "Text.Body");
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, i == 0 ? 0 : 8, 12, 0);
                Grid.SetRow(label, i);
                grid.Children.Add(label);

                int index = i;
                var box = new TextBox { Text = _values[i], IsEnabled = Unlocked, Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
                AutomationProperties.SetName(box, _fields[i].Label);
                box.TextChanged += (_, _) =>
                {
                    _values[index] = box.Text;
                    if (preview is not null) preview.Text = _preview!(_values);
                };
                Grid.SetRow(box, i);
                Grid.SetColumn(box, 1);
                grid.Children.Add(box);
            }

            Body.Children.Add(grid);
            if (preview is not null)
            {
                preview.Margin = new Thickness(0, 10, 0, 0);
                Body.Children.Add(preview);
            }
        }
    }
}
