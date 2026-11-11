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

        // What differs by system comes first, in the file's three blocks: Hybrid, Windows, Linux.
        AddHybridCards(file?.Hybrid);
        AddWindowsCards(file?.Windows);
        AddLinuxCards(file?.Linux);

        Heading("Every app",
            "The rest has nothing to do with the system an app is for. These settings sit directly under Standard in the file.");

        Add(new NameListCard(this, "Private files", "Standard.PrivateFiles",
            "Your own working notes. Kept in every zip you build and in the backup; left out only when you stage for publishing. " +
            "File names, matched at any depth.",
            Standard.DefaultPrivateFiles, file?.PrivateFiles, NameKind.FileName, "File name, e.g. NOTES.md",
            (f, list) => f.PrivateFiles = list));

        Add(new ExemptionListCard(this, file?.Exemptions));

        var images = file?.Images;
        Add(new FieldsCard(this, "Images", "Standard.Images",
            "The level for image files no entry under Image levels names, and how much colour difference a palette " +
            "(Smallest) may cause before it isn't offered. Levels: AsIs, Strip, Lossless, Smallest. Limits: Invisible " +
            "(99 % of pixels within ΔE 1.0, no spot beyond 3.0) or Slight (99 % within 2.3, no spot beyond 6.0).",
            [
                new Field("Default level", Standard.DefaultImages.DefaultLevel, images?.DefaultLevel,
                    v => ImageLevelEntry.TryLevel(v, out _) ? null : $"\"{v}\" isn't a level: AsIs, Strip, Lossless or Smallest."),
                new Field("Palette limit", Standard.DefaultImages.LossLimit, images?.LossLimit,
                    v => v.Trim().Equals("Invisible", StringComparison.OrdinalIgnoreCase) || v.Trim().Equals("Slight", StringComparison.OrdinalIgnoreCase)
                        ? null : $"\"{v}\" isn't a limit: Invisible or Slight.")
            ],
            (f, v) => f.Images = new ImageSettings { DefaultLevel = v[0], LossLimit = v[1] }));

        Add(new ImageLevelListCard(this, file?.ImageLevels));

        Add(new FieldsCard(this, "License holder", "Standard.LicenseHolder",
            "The name the MIT license fix writes into the copyright line, with the release date's year. " +
            "None by default, so the fix isn't offered until you set it: two or more words, as on the license.",
            [new Field("Name", Standard.DefaultLicenseHolder, file?.LicenseHolder,
                v => v.Length == 0 ? "Enter the license holder's name, or lock the setting."
                   : LicenseCheck.IsFullName(v) ? null
                   : $"The license holder \"{v}\" isn't a full name of two or more words.")],
            (f, v) => f.LicenseHolder = v[0],
            v => v[0].Trim().Length == 0 ? "The Fix writes: Copyright (c) <year> <name>"
                                         : $"The Fix writes: Copyright (c) {DateTime.Today.Year} {v[0].Trim()}"));

        Add(new NameListCard(this, "Whitelist", "Standard.Whitelist",
            "Files every repo may hold. Anything else the build doesn't use is reported as unlisted. " +
            "A pattern matches a file name (*.cs, app_*.png); with a slash, the end of a path.",
            Standard.DefaultWhitelist, file?.Whitelist, NameKind.Pattern, "Pattern, e.g. *.resx",
            (f, list) => f.Whitelist = list));

        Add(new NameListCard(this, "Web packages", "Standard.WebPackages",
            "Packages that bring web content into the app. An app that asks for admin rights and uses one of these is flagged.",
            Standard.DefaultWebPackages, file?.WebPackages, NameKind.Package, "Package name",
            (f, list) => f.WebPackages = list));

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
            "What every app's .csproj sets for a single self-contained program, and its DebugType. The fixes write these values. " +
            "The fifth publish property, the RuntimeIdentifier, differs by system: see Windows: runtime and Linux: runtime.",
            [
                new Field("SelfContained", p.SelfContained, publish?.SelfContained, NotEmpty("SelfContained")),
                new Field("PublishSingleFile", p.PublishSingleFile, publish?.PublishSingleFile, NotEmpty("PublishSingleFile")),
                new Field("IncludeNativeLibrariesForSelfExtract", p.IncludeNativeLibrariesForSelfExtract, publish?.IncludeNativeLibrariesForSelfExtract, NotEmpty("IncludeNativeLibrariesForSelfExtract")),
                new Field("EnableCompressionInSingleFile", p.EnableCompressionInSingleFile, publish?.EnableCompressionInSingleFile, NotEmpty("EnableCompressionInSingleFile")),
                new Field("DebugType", p.DebugType, publish?.DebugType, NotEmpty("DebugType"))
            ],
            (f, v) => f.Publish = new PublishSettings
            {
                SelfContained = v[0], PublishSingleFile = v[1],
                IncludeNativeLibrariesForSelfExtract = v[2], EnableCompressionInSingleFile = v[3], DebugType = v[4]
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

    /// <summary>A heading over a group of cards, with a line saying what the group is.</summary>
    private void Heading(string title, string about)
    {
        var heading = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 17,
            Margin = new Thickness(2, CardsPanel.Children.Count == 0 ? 0 : 12, 0, 4)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        CardsPanel.Children.Add(heading);

        var line = Text(about, "Text.Caption", wrap: true);
        line.Margin = new Thickness(2, 0, 0, 14);
        line.MaxWidth = 760;
        line.HorizontalAlignment = HorizontalAlignment.Left;
        CardsPanel.Children.Add(line);
    }

    private const string PackageRule =
        " Any other package fails the third-party packages check, and so does one below its minimum version. " +
        "Packages in libraries the app references count too; packages only a test project uses are never checked.";

    /// <summary>The "Hybrid" block of the file: packages approved for apps of both systems.</summary>
    private void AddHybridCards(HybridFile? hybrid)
    {
        Heading("Hybrid",
            "Packages for both systems: the ones Windows and Linux apps really share, such as an HTML parser both editions of one app " +
            "ship. A package only one system uses belongs under that system.");

        Add(new PackageListCard(this, "Hybrid: approved packages", "Standard.Hybrid.ApprovedPackages",
            "Third-party NuGet packages you accept in Windows and Linux apps alike." + PackageRule,
            "No Hybrid packages: an app may only use the packages approved for its own system.",
            Standard.DefaultApprovedPackages, hybrid?.ApprovedPackages,
            (f, list) => (f.Hybrid ??= new HybridFile()).ApprovedPackages = list));
    }

    /// <summary>The "Windows" block of the file: what holds for Windows apps only. The mirror of the Linux cards.</summary>
    private void AddWindowsCards(WindowsFile? windows)
    {
        Heading("Windows apps",
            $"Every app whose folder in the history folder doesn't end in {AppPlatforms.LinuxSuffix}. A Windows app is written with WPF.");

        Add(new PackageListCard(this, "Windows: approved packages", "Standard.Windows.ApprovedPackages",
            "Packages a Windows app may use on top of the Hybrid ones. They are never approved for a Linux app." + PackageRule,
            "No Windows packages: a Windows app may only use the Hybrid packages.",
            Standard.DefaultApprovedPackages, windows?.ApprovedPackages,
            (f, list) => (f.Windows ??= new WindowsFile()).ApprovedPackages = list));

        Add(new FieldsCard(this, "Windows: minimum target framework", "Standard.Windows.MinimumTargetFramework",
            "The lowest target framework a Windows app may use. Newer passes, as long as the platform is the same.",
            [new Field("Target framework", Standard.DefaultMinimumTargetFramework, windows?.MinimumTargetFramework,
                v => Standard.ParseTargetFramework(v) is null ? $"\"{v}\" isn't a target framework like net10.0-windows." : null)],
            (f, v) => (f.Windows ??= new WindowsFile()).MinimumTargetFramework = v[0]));

        Add(new FieldsCard(this, "Windows: runtime", "Standard.Windows.RuntimeIdentifier",
            "The RuntimeIdentifier the publish fix writes into a Windows app's .csproj when it has none. The check itself accepts any " +
            "win- runtime (win-arm64 too); the other four publish properties are the ones under Publish properties.",
            [new Field("RuntimeIdentifier", Standard.DefaultWindowsRuntimeIdentifier, windows?.RuntimeIdentifier,
                v => v.StartsWith("win-", StringComparison.OrdinalIgnoreCase) && v.Length > 4 ? null
                   : $"\"{v}\" isn't a Windows runtime like win-x64.")],
            (f, v) => (f.Windows ??= new WindowsFile()).RuntimeIdentifier = v[0]));
    }

    /// <summary>
    /// What a Linux app is held to where it differs from a Windows app: the "Linux" block of the file. An
    /// app is a Linux app when its folder in the history folder ends in LINUX. Every card writes its own key
    /// under Standard.Linux; a file without the block uses the defaults shown.
    /// </summary>
    private void AddLinuxCards(LinuxFile? linux)
    {
        Heading("Linux apps",
            $"Every app whose folder in the history folder ends in {AppPlatforms.LinuxSuffix} (capitals). A Linux app is written with Avalonia where a " +
            "Windows app uses WPF.");

        Add(new PackageListCard(this, "Linux: approved packages", "Standard.Linux.ApprovedPackages",
            "Packages a Linux app may use on top of the Hybrid ones: Avalonia and its parts. They are never approved for a " +
            "Windows app, where a reference to Avalonia still fails." + PackageRule,
            "No Linux packages are approved: a Linux app's Avalonia references fail until they are listed here.",
            Standard.DefaultLinuxApprovedPackages, linux?.ApprovedPackages,
            (f, list) => (f.Linux ??= new LinuxFile()).ApprovedPackages = list));

        Add(new NameListCard(this, "Linux: forbidden packages", "Standard.Linux.ForbiddenPackages",
            "Packages a Linux app must not reference, approved or not. Avalonia.Desktop is the usual convenience package; it brings " +
            "the Windows and macOS backends along, so a Linux app names the Linux backends in Program.cs instead.",
            Standard.DefaultLinuxForbiddenPackages, linux?.ForbiddenPackages, NameKind.Package, "Package name",
            (f, list) => (f.Linux ??= new LinuxFile()).ForbiddenPackages = list));

        Add(new NameListCard(this, "Linux: packages with a version of their own", "Standard.Linux.OwnVersionPackages",
            "The Avalonia packages of an app have to be on one version. The ones listed here are left out of that check: " +
            "the data grid is a package of its own since Avalonia 12, with its own version line.",
            Standard.DefaultLinuxOwnVersionPackages, linux?.OwnVersionPackages, NameKind.Package, "Package name",
            (f, list) => (f.Linux ??= new LinuxFile()).OwnVersionPackages = list));

        Add(new NameListCard(this, "Linux: whitelist", "Standard.Linux.Whitelist",
            "Files a Linux repo may hold on top of the whitelist under Every app: Avalonia's markup. In a folder that isn't a Linux app's, an .axaml file warns.",
            Standard.DefaultLinuxWhitelist, linux?.Whitelist, NameKind.Pattern, "Pattern, e.g. *.axaml",
            (f, list) => (f.Linux ??= new LinuxFile()).Whitelist = list));

        Add(new FieldsCard(this, "Linux: minimum target framework", "Standard.Linux.MinimumTargetFramework",
            "The lowest target framework a Linux app may use. Newer passes; a framework with a platform (net10.0-windows) fails, " +
            "since it can't be built for Linux.",
            [new Field("Target framework", Standard.DefaultLinuxMinimumTargetFramework, linux?.MinimumTargetFramework,
                v => Standard.ParseTargetFramework(v) is not { } parsed ? $"\"{v}\" isn't a target framework like net10.0."
                   : parsed.Platform.Length > 0 ? $"\"{v}\" names a platform. A Linux app's framework has none: net10.0."
                   : null)],
            (f, v) => (f.Linux ??= new LinuxFile()).MinimumTargetFramework = v[0]));

        Add(new FieldsCard(this, "Linux: runtime", "Standard.Linux.RuntimeIdentifier",
            "The RuntimeIdentifier the publish fix writes into a Linux app's .csproj when it has none. The check itself accepts any " +
            "linux- runtime (linux-arm64 too); the other four publish properties are the ones under Publish properties.",
            [new Field("RuntimeIdentifier", Standard.DefaultLinuxRuntimeIdentifier, linux?.RuntimeIdentifier,
                v => v.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) && v.Length > 6 ? null
                   : $"\"{v}\" isn't a Linux runtime like linux-x64.")],
            (f, v) => (f.Linux ??= new LinuxFile()).RuntimeIdentifier = v[0]));
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
        private readonly IReadOnlyList<PackageEntry> _defaults;
        private readonly string _emptyNote;
        private readonly Action<StandardFile, List<PackageEntry>> _write;
        private List<PackageEntry> _entries;
        private string? _error;

        /// <param name="emptyNote">What an empty list means, shown in its place.</param>
        /// <param name="write">Where the list goes in the file: the general list, or the one for Linux apps.</param>
        public PackageListCard(SettingsWindow window, string title, string key, string description, string emptyNote,
                               IReadOnlyList<PackageEntry> defaults, List<PackageEntry>? fromFile,
                               Action<StandardFile, List<PackageEntry>> write)
            : base(window, title, key, description,
                defaults.Count == 0 ? "none" : string.Join(", ", defaults.Select(d => d.Name)), fromFile is not null)
        {
            _defaults = defaults;
            _emptyNote = emptyNote;
            _write = write;
            _entries = (fromFile ?? defaults.ToList()).Select(p => p.Clone()).ToList();
        }

        protected override void ResetToDefault() => _entries = _defaults.Select(p => p.Clone()).ToList();

        public override void Write(StandardFile file) => _write(file, _entries.Select(p => p.Clone()).ToList());

        public override IEnumerable<string> Validate()
        {
            foreach (var p in _entries.Where(p => !IsVersion(p.MinVersion)))
                yield return $"{p.Name}: \"{p.MinVersion}\" isn't a version like 1.2.3.";
        }

        public override void Render()
        {
            Body.Children.Clear();
            if (_entries.Count == 0)
                Body.Children.Add(Note(_emptyNote));

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

    /// <summary>Apps allowed to break a rule, each with its reason. Identity can't be edited in place: remove and add.</summary>
    private sealed class ExemptionListCard : Card
    {
        private List<ExemptionEntry> _entries;
        private string? _error;

        public ExemptionListCard(SettingsWindow window, List<ExemptionEntry>? fromFile)
            : base(window, "Exemptions", "Standard.Exemptions",
                "An app allowed to break one rule, with the reason. A finding it covers shows as Exempt, with the reason, and " +
                "no longer makes the app warn or fail. File (the end of a path), Method and Origin narrow it; Method and Origin " +
                "apply to the write checks. One that covers nothing is reported, so the list doesn't keep stale entries.",
                "none", fromFile is not null)
        {
            _entries = (fromFile ?? Standard.DefaultExemptions.ToList()).Select(e => e.Clone()).ToList();
        }

        protected override void ResetToDefault() => _entries = Standard.DefaultExemptions.Select(e => e.Clone()).ToList();

        public override void Write(StandardFile file) => file.Exemptions = _entries.Select(e => e.Clone()).ToList();

        public override IEnumerable<string> Validate()
        {
            foreach (var e in _entries.Where(e => e.Reason.Trim().Length == 0))
                yield return $"The exemption {e.Describe} needs a reason.";
        }

        public override void Render()
        {
            Body.Children.Clear();
            if (_entries.Count == 0)
                Body.Children.Add(Note("No exemptions: every app is held to every rule."));

            foreach (var entry in _entries.OrderBy(e => e.App, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Rule, StringComparer.OrdinalIgnoreCase))
            {
                var left = new StackPanel();
                var name = Text($"{entry.App} · {entry.Rule}", "Text.Mono");
                name.FontSize = 13;
                name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                left.Children.Add(name);

                var scope = new[]
                {
                    entry.File is { Length: > 0 } f ? $"file {f}" : null,
                    entry.Method is { Length: > 0 } m ? $"method {m}" : null,
                    entry.Origin is { Length: > 0 } o ? $"origin {o}" : null
                }.Where(s => s is not null).ToList();
                if (scope.Count > 0)
                {
                    var where = Text(string.Join(" · ", scope), "Text.Caption", wrap: true);
                    where.Margin = new Thickness(0, 3, 0, 0);
                    left.Children.Add(where);
                }

                if (Unlocked)
                {
                    var reason = new TextBox { Text = entry.Reason, Margin = new Thickness(0, 8, 0, 0), ToolTip = "Why the rule doesn't apply here" };
                    reason.TextChanged += (_, _) => entry.Reason = reason.Text.Trim();
                    AutomationProperties.SetName(reason, $"Reason for {entry.Describe}");
                    left.Children.Add(reason);
                }
                else
                {
                    var reason = Text(entry.Reason, "Text.Body", wrap: true);
                    reason.Margin = new Thickness(0, 4, 0, 0);
                    left.Children.Add(reason);
                }

                left.Children.Add(UsageLine(SettingsUsage.Exemption(entry, Window._apps)));
                Body.Children.Add(Row(left, entry.Enabled, on => entry.Enabled = on, () => _entries.Remove(entry)));
            }

            if (!Unlocked) return;

            if (_error is not null) Body.Children.Add(Note(_error, warn: true));

            string[] captions = ["App", "Rule", "File (optional)", "Method (optional)", "Origin (optional)"];
            string[] hints =
            [
                "The app's folder name in the history folder",
                "The rule's name as shown, e.g. No AppData",
                "The end of the file's path, e.g. Services/ConsolePreset.cs",
                "The method the write is in (write checks only)",
                "Where the write points, e.g. ApplicationData (write checks only)"
            ];
            var boxes = new TextBox[captions.Length];
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < captions.Length + 1; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < captions.Length; i++)
            {
                var caption = Text(captions[i], "Text.Caption");
                caption.VerticalAlignment = VerticalAlignment.Center;
                caption.Margin = new Thickness(0, i == 0 ? 0 : 6, 12, 0);
                Grid.SetRow(caption, i);
                grid.Children.Add(caption);

                boxes[i] = new TextBox { ToolTip = hints[i], Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
                AutomationProperties.SetName(boxes[i], "New exemption " + captions[i]);
                Grid.SetRow(boxes[i], i);
                Grid.SetColumn(boxes[i], 1);
                grid.Children.Add(boxes[i]);
            }

            var reasonCaption = Text("Reason", "Text.Caption");
            reasonCaption.VerticalAlignment = VerticalAlignment.Center;
            reasonCaption.Margin = new Thickness(0, 6, 12, 0);
            Grid.SetRow(reasonCaption, captions.Length);
            grid.Children.Add(reasonCaption);

            var reasonRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var newReason = new TextBox { ToolTip = "Why the rule doesn't apply here" };
            AutomationProperties.SetName(newReason, "New exemption reason");
            var addButton = new Button { Content = "Add exemption", Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(addButton, 1);
            reasonRow.Children.Add(newReason);
            reasonRow.Children.Add(addButton);
            Grid.SetRow(reasonRow, captions.Length);
            Grid.SetColumn(reasonRow, 1);
            grid.Children.Add(reasonRow);

            addButton.Click += (_, _) =>
            {
                string? Opt(TextBox b) => b.Text.Trim().Length == 0 ? null : b.Text.Trim();
                var entry = new ExemptionEntry
                {
                    App = boxes[0].Text.Trim(), Rule = boxes[1].Text.Trim(),
                    File = Opt(boxes[2]), Method = Opt(boxes[3]), Origin = Opt(boxes[4]),
                    Reason = newReason.Text.Trim()
                };
                _error = entry.App.Length == 0 ? "Enter the app the exemption is for."
                    : entry.Rule.Length == 0 ? "Enter the rule, as its name is shown in the findings."
                    : entry.Reason.Length == 0 ? "Enter the reason: it's the record of why the rule doesn't apply."
                    : _entries.Any(e => e.Key == entry.Key) ? $"{entry.Describe} is already on the list."
                    : null;
                if (_error is null) _entries.Add(entry);
                Render();
            };

            var heading = Text("Add an exemption", "Text.Caption");
            heading.Margin = new Thickness(0, 8, 0, 6);
            Body.Children.Add(heading);
            Body.Children.Add(grid);
        }
    }

    /// <summary>Image levels: per app, a file or a pattern, its level and an optional reason.</summary>
    private sealed class ImageLevelListCard : Card
    {
        private static readonly string[] LevelNames = ["AsIs", "Strip", "Lossless", "Smallest"];
        private static readonly string[] LevelWords = ["As is", "Strip", "Lossless", "Smallest"];

        private List<ImageLevelEntry> _entries;
        private string? _error;

        public ImageLevelListCard(SettingsWindow window, List<ImageLevelEntry>? fromFile)
            : base(window, "Image levels", "Standard.ImageLevels",
                "What may be done to an app's image files (PNG, ICO, JPEG): As is changes nothing; Strip removes metadata " +
                "nothing displays; Lossless also recompresses, every pixel kept; Smallest also offers a palette within the " +
                "loss limit, used only where you tick it. A path is the end of a file's path, or a pattern (* within a folder " +
                "name, ** across folders). An entry naming a file wins over a pattern; of several patterns, the longest. " +
                "The image picker adds entries when you change a level there.",
                "none", fromFile is not null)
        {
            _entries = (fromFile ?? Standard.DefaultImageLevels.ToList()).Select(e => e.Clone()).ToList();
        }

        protected override void ResetToDefault() => _entries = Standard.DefaultImageLevels.Select(e => e.Clone()).ToList();

        public override void Write(StandardFile file) => file.ImageLevels = _entries.Select(e => e.Clone()).ToList();

        public override IEnumerable<string> Validate()
        {
            foreach (var e in _entries.Where(e => !ImageLevelEntry.TryLevel(e.Level, out _)))
                yield return $"The image level \"{e.Level}\" for {e.App} · {e.Path} isn't one of As is, Strip, Lossless or Smallest.";
        }

        /// <summary>The level's place in the list, or -1 for a level that can't be read, so choosing any one fires.</summary>
        private static int IndexOf(string level) =>
            ImageLevelEntry.TryLevel(level, out var l) ? (int)l : -1;

        public override void Render()
        {
            Body.Children.Clear();
            if (_entries.Count == 0)
                Body.Children.Add(Note("No entries: every image file gets the default level."));

            foreach (var entry in _entries.OrderBy(e => e.App, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                var left = new StackPanel();
                var name = Text($"{entry.App} · {entry.Path}", "Text.Mono");
                name.FontSize = 13;
                name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                name.TextWrapping = TextWrapping.Wrap;
                left.Children.Add(name);

                if (Unlocked)
                {
                    var line = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var level = new ComboBox { MinWidth = 130, ItemsSource = LevelWords, SelectedIndex = IndexOf(entry.Level) };
                    level.SelectionChanged += (_, _) => { if (level.SelectedIndex >= 0) entry.Level = LevelNames[level.SelectedIndex]; };
                    AutomationProperties.SetName(level, $"Level for {entry.App} {entry.Path}");
                    line.Children.Add(level);
                    var reason = new TextBox { Text = entry.Reason ?? "", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Why this level (optional)" };
                    reason.TextChanged += (_, _) => entry.Reason = reason.Text.Trim().Length == 0 ? null : reason.Text.Trim();
                    AutomationProperties.SetName(reason, $"Reason for {entry.App} {entry.Path}");
                    Grid.SetColumn(reason, 1);
                    line.Children.Add(reason);
                    left.Children.Add(line);
                }
                else
                {
                    var what = Text((IndexOf(entry.Level) is >= 0 and var i ? LevelWords[i] : entry.Level + " (not a level)") + (entry.Reason is { Length: > 0 } r ? " · " + r : ""), "Text.Body", wrap: true);
                    what.Margin = new Thickness(0, 4, 0, 0);
                    left.Children.Add(what);
                }

                left.Children.Add(UsageLine(SettingsUsage.ImageLevel(entry, Window._apps)));
                Body.Children.Add(Row(left, entry.Enabled, on => entry.Enabled = on, () => _entries.Remove(entry)));
            }

            if (!Unlocked) return;

            if (_error is not null) Body.Children.Add(Note(_error, warn: true));

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBox Box(int row, string caption, string hint)
            {
                var c = Text(caption, "Text.Caption");
                c.VerticalAlignment = VerticalAlignment.Center;
                c.Margin = new Thickness(0, row == 0 ? 0 : 6, 12, 0);
                Grid.SetRow(c, row);
                grid.Children.Add(c);
                var b = new TextBox { ToolTip = hint, Margin = new Thickness(0, row == 0 ? 0 : 6, 0, 0) };
                AutomationProperties.SetName(b, "New image level " + caption);
                Grid.SetRow(b, row);
                Grid.SetColumn(b, 1);
                grid.Children.Add(b);
                return b;
            }

            var app = Box(0, "App", "The app's folder name in the history folder");
            var path = Box(1, "File or pattern", "The end of a path, e.g. Assets/Scripts/*.png or screenshots/main.png");

            var levelCaption = Text("Level", "Text.Caption");
            levelCaption.VerticalAlignment = VerticalAlignment.Center;
            levelCaption.Margin = new Thickness(0, 6, 12, 0);
            Grid.SetRow(levelCaption, 2);
            grid.Children.Add(levelCaption);
            var newLevel = new ComboBox { ItemsSource = LevelWords, SelectedIndex = 0, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(newLevel, "New image level");
            Grid.SetRow(newLevel, 2);
            Grid.SetColumn(newLevel, 1);
            grid.Children.Add(newLevel);

            var reasonCaption = Text("Reason (optional)", "Text.Caption");
            reasonCaption.VerticalAlignment = VerticalAlignment.Center;
            reasonCaption.Margin = new Thickness(0, 6, 12, 0);
            Grid.SetRow(reasonCaption, 3);
            grid.Children.Add(reasonCaption);
            var reasonRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var newReason = new TextBox { ToolTip = "Why this level, e.g. a changed shade could change what it shows" };
            AutomationProperties.SetName(newReason, "New image level reason");
            var addButton = new Button { Content = "Add level", Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(addButton, 1);
            reasonRow.Children.Add(newReason);
            reasonRow.Children.Add(addButton);
            Grid.SetRow(reasonRow, 3);
            Grid.SetColumn(reasonRow, 1);
            grid.Children.Add(reasonRow);

            addButton.Click += (_, _) =>
            {
                var entry = new ImageLevelEntry
                {
                    App = app.Text.Trim(), Path = path.Text.Trim().Replace('\\', '/'),
                    Level = LevelNames[Math.Max(0, newLevel.SelectedIndex)],
                    Reason = newReason.Text.Trim().Length == 0 ? null : newReason.Text.Trim()
                };
                _error = entry.App.Length == 0 ? "Enter the app the level is for."
                    : entry.Path.Length == 0 ? "Enter a file or a pattern."
                    : _entries.Any(e => e.Key == entry.Key) ? $"{entry.App} · {entry.Path} is already on the list."
                    : null;
                if (_error is null) _entries.Add(entry);
                Render();
            };

            var heading = Text("Add an image level", "Text.Caption");
            heading.Margin = new Thickness(0, 8, 0, 6);
            Body.Children.Add(heading);
            Body.Children.Add(grid);
        }
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
                string.Join(", ", fields.Select(f => fields.Count == 1 ? (f.Default.Length == 0 ? "none" : f.Default) : $"{f.Label} {f.Default}")),
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
