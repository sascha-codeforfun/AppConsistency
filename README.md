# AppConsistency

Checks the newest snapshot of each app in your history folder against your build standards.

A WPF desktop tool (.NET 10, single self-contained exe, dark mode throughout). Point it at the
history folder, press **Scan**, and every app gets a list of findings: what passes, what to
review, and what needs attention. The history folder is only ever read. Anything the app writes
goes to your Downloads folder and never overwrites an existing file.

It is the checking half of a small build chain: snapshots that pass are built offline by
[RepoBuilder](https://github.com/sascha-codeforfun/RepoBuilder), which turns a repo zip into one
self-contained exe, and pushed to GitHub by RepoPublisher. Neither is needed to run
AppConsistency; they're named in some findings because the rules are written for that chain.

**One opinionated standard.** The rules check repos against one set of choices: WPF on
`net10.0-windows` or newer, a single self-contained exe, dark mode, an `app.ico` and header image,
MIT. The rules themselves are in the code. The values that are a matter of taste (approved
packages, private files, the whitelist, names and publish properties) are settings, changed with
the gear in the header (see *Settings*). The built-in values are neutral: no approved packages and
no private files, so the code carries no one's personal choices.

## History folder layout

```
D:\REPO\HISTORY\
  EolInspector\
    EolInspector-0001.zip
    EolInspector-0002.zip
    EolInspector-0004.zip      <- newest: checked
  AppConsistency\
    AppConsistency-0027.zip
  SomeTool\
    SomeTool-0003\             <- unzipped snapshot folders work too (used only when there are no zips)
```

- One subfolder per app. In each one, the snapshot with the highest trailing number is checked
  and older ones are ignored.
- You can also pick a single app's folder (one that holds `App-0001.zip`, `App-0002.zip`, …).
- Folders that start with `.` are skipped.
- The last scanned folder is remembered in `AppConsistency.json` next to the exe, written only
  when it changes, along with your settings. In a folder that can't be written, nothing is
  remembered. A folder passed on the command line takes precedence. Versions up to 1.0.12 kept it in
  `%LOCALAPPDATA%\AppConsistency\settings.json`; that is read once and then left alone, so you
  can delete it.

**What AppConsistency itself writes:** `AppConsistency.json` next to the exe, and the zips you ask
for in Downloads. Nothing in AppData, the temp folder or the registry. One thing comes from .NET,
not from the app: a single-file exe built with `IncludeNativeLibrariesForSelfExtract` unpacks its
native WPF libraries on first start to `%TEMP%\.net\<App>\<hash>\`, a new folder for every build.
That holds for every app built to this standard.

## What it checks

Each rule reports **Pass**, **Info**, **Warn** or **Fail** with a one-line reason. The license
is the one exception: it is shown in indigo (see below).

| Category | Checks |
|---|---|
| Package | Single wrapper folder, no build output (`bin`, `obj`, `.vs`) in the zip, no obsolete IDE files, no git files, no unlisted files, release timestamps |
| Project | Project file and exe count, solution files, target framework (`net10.0-windows` or newer), WPF, output type `WinExe`, tests kept out of the app build, `README`, license, version |
| Publish | Self-contained single-file publish settings |
| Icon and header | `app.ico` as the exe icon, `app_256.png` as a resource, header block with the app name and description |
| Dark mode | Dark title bar, dark window background |
| Dependencies | Third-party packages against the approved list, no loose DLL references, test project packages |
| Security | Admin rights, web content combined with admin rights |
| Build leaks | Deterministic compilation, no debug information, no commit hash in the version, no build paths, machine values, build times or path-hashed names baked into the output |
| Code | Single-file safe paths (no `Assembly.Location`), no AppData, writes next to the exe |

The rules in more detail:

- **Solution files.** Your build chain doesn't use them. An old `.sln` always warns. A `.slnx`
  in a repo with more than one `.csproj` (app, library or test) is kept for the IDE and only
  reported as Info. With a single project, a `.slnx` warns as well.
- **Obsolete IDE files.** Per-user IDE and editor state is not needed to build from the zip and
  can carry local paths, so it warns but doesn't fail. Every file in the zip is checked at any
  depth: `*.user`, `*.suo`, `*.userprefs`, `*.DotSettings.user`, `*.rsuser`, `*.userosscache`,
  `*.sln.docstates`, `*.vspscc`, `*.vssscc`, `*.VC.db`, `*.VC.opendb`, `*.sdf`,
  `.idea/workspace.xml`, `.editorconfig` and publish profiles (`*.pubxml`). Each hit is listed
  with its path and what kind of file it is. On a zip snapshot the Fix removes exactly the listed
  files from the copy in Downloads. Folder snapshots report Info. Without its `.editorconfig`, a
  build of the fixed copy uses the SDK's default analyzer settings, which can change which
  warnings show up, never the exe.
- **Git files.** `.gitignore`, `.gitattributes`, `.gitmodules`, `.gitkeep` and whole `.git/` and
  `.github/` folders, at any depth. The build reads the zip, and RepoPublisher pushes the files
  through the GitHub API and adds its own workflow, so none of them are used. Warns; the Fix
  removes exactly the listed files and folders. Folder snapshots report Info.
- **Unlisted files.** Every file is checked against a whitelist: `*.cs`, `*.xaml`, `*.csproj`,
  `*.slnx`, `app.ico`, `app.png`, `app.svg`, `app_<size>.png`, `app.manifest`,
  `Directory.Build.props`, the README and the license. Files a project uses in its build count as
  listed too: its `Resource`, `EmbeddedResource`, `Content`, `Page` and `Compile` items (wildcards
  included), `None` items copied to the output, the files `ApplicationIcon` and
  `ApplicationManifest` name, and for the Web SDK everything under `wwwroot` plus the project's
  `.json` and `.config` files. Files other rules deal with (build output, IDE files, git files,
  solution files) aren't listed twice. What's left splits in two:
  - **Used by the README**: files the README links to or embeds (`![…](docs/before.png)`,
    `[…](Samples/a.eml)`, `src=`/`href=`, by their path relative to the README), or names in its
    text (`sample-report.coveragexml`; a name inside a web address doesn't count). These are
    documentation: listed as Info, not a warning.
  - **Not referenced**: everything else (stray notes, unused screenshots, samples nothing
    mentions, office files) warns, because whether it belongs depends on the repo.

  Duplicates: when several files share a name, the README uses the one it links to, or, when it
  only names it, the one closest to the README (same folder first, then the shallowest). Every
  other copy warns, with the copy the README uses named beside it.

  The row warns when at least one file isn't referenced, and is Info when the README uses them
  all. Either way the Fix opens a list, grouped the same way, with **nothing ticked**: only the
  files you tick are removed from the copy, the rest stays. The whitelist is a setting, and is
  shown in the list.
- **Private files.** The file names you set in *Settings* (none by default), in any letter case
  and at any depth (a core and a UI project can each have their own), are your working notes.
  They're recognised for what they are: listed as Info, never a defect, never unlisted. They stay
  in every zip you build from and in BACKUP all, and are left out only when you stage for
  publishing (see *Stage to Downloads*). Content an app hands on, such as a theme's guide, isn't
  private and ships.
- **Release timestamps.** A release zip has one timestamp: every entry dated the same day at
  00:00:00, and no NTFS or extended timestamps (7-Zip, Explorer and others show those instead
  of the visible time). Anything else warns. Every zip AppConsistency writes is clean anyway
  (see *Timestamps* below); the Fix makes the release timestamps a queued fix of their own, for
  a snapshot where nothing else needs fixing.
- **Third-party packages.** Every project that ends up in the exe is checked: the app project and
  every project it references through `ProjectReference`, followed all the way down. A package in a
  library ships just the same, so it's named with the project it comes from
  (`Some.Package 1.2.3 (in App.Core)`), and it counts for the package badge and
  for "web content with admin rights". Test projects are listed apart and never checked.
- **No AppData.** Anything an app writes under the user's AppData (Local, Roaming or LocalLow)
  is left behind for good when the app is gone, so it **fails**, every time. Settings belong in
  `AssemblyName.json` next to the exe. Read from the code, nothing is run: every call that writes a
  file or creates a folder is followed back through the variables, fields, properties and methods
  its path is built from, to where the folder comes from (`SpecialFolder.LocalApplicationData`,
  `SpecialFolder.ApplicationData`, `%LOCALAPPDATA%`, `%APPDATA%`, a literal `AppData\` path). Each
  write is listed with its file and line. Reading or deleting there doesn't count. Writes to the
  temp folder are listed as Info: temp is meant for that, and only the app knows whether it cleans
  up. Code a project leaves out of its build (`<Compile Remove>`) and test projects are skipped. A
  path assembled in ways the check can't follow is missed, never guessed.
- **Writes next to the exe.** `AssemblyName.json` there is the settings standard. Anything else
  written next to the exe (other settings names, logs, caches) is listed as Info, since a read-only
  folder such as Program Files makes those writes fail.
- **README.** Expects exactly one `README.md`, found in any folder and in any letter case.
  Readmes under `bin`, `obj`, `.git`, `.vs` and `node_modules` are ignored. It warns when there
  is no readme, when the only one isn't Markdown (for example `Readme.txt`), or when there are
  several (typically an old `.txt` left next to the new `.md`).
- **README mentions private files.** A copy staged for publishing leaves the private files out,
  so a README line that links to or names one would point at nothing, and tell the public the
  notes exist. Each such line warns, with its line number. There's no fix: reword the line, or move
  it into the private file.
- **License.** Not a verdict, so it never makes an app warn or fail: it's shown in **indigo**,
  the colour for "pay attention", in one of three ways.
  - **MIT**: the standard MIT license, word for word. Only the copyright line varies:
    `Copyright (c) 2026 Jane Doe`, with a year or an upward span like `2026-2027` and a name of
    two or more words. Any name counts. Whitespace and line endings don't matter, since editors
    re-wrap the paragraphs.
  - **license**, MIT but not standard: the detail says what differs (the title, the copyright
    line, such as a placeholder name, `©` instead of `(c)` or a single word, or the wording).
  - **license**, another license, or no license file at all.

  `LICENSE`, `LICENCE` and `COPYING`, bare or as `.md` or `.txt`, count, in any letter case and
  any folder outside build output. When there are several, all are listed and the first one at the
  shallowest level is checked.
- **Version.** The app project's `<Version>` (or `VersionPrefix`), from the `.csproj` or a
  `Directory.Build.props` above it. None warns, because every build is then 1.0.0 and the header
  has no version to show; on a zip the Fix adds `<Version>1.0.0</Version>`.
- **No commit in the version.** Every project that ends up in the exe needs
  `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`,
  in the `.csproj` or a `.props` file. Without it the SDK appends `+<commit hash>` to the product
  version whenever the build finds git information, so a build from a clone differs from one from a
  zip and changes with every commit. Missing or `true` fails; on a zip the Fix adds it next to
  `DebugType`, or in a group of its own when there's none.

## The window

**Header.** On the left are the app name and description. In the centre are pills counting the
scanned apps by their worst status (passed, warned, failed), and in indigo the apps with a
license to review: no license file, another license, or MIT that isn't standard. Click one to
show only those apps in the list; click it again, or another pill, to change the filter. A new
scan shows all apps again. On the right are the version, the SDK the exe was built with, and the
runtime it's running on.

**Toolbar**

| Button | What it does |
|---|---|
| Browse… | Pick the history folder |
| Copy report | Copies every warning and failure across all apps as plain text, plus every license to review |
| BACKUP all | Writes `yyyy-MM-dd-HH-mm-ss-REPO-SOURCE-BACKUP.zip` to Downloads, holding the newest snapshot of every app under its name without the version number (`EolInspector.zip`). Everything inside, each repo zip and every file in it, is dated the day you press the button at 00:00:00, so code from a backup is easy to tell apart. Private files are always included: it's your archive. The file name keeps the full time, so several backups on one day stay apart, and the backup file itself keeps its real time |
| Collect README | Writes `yyyy-MM-dd-HH-mm-ss-REPO-READMES.zip` to Downloads, holding the readmes and license files from every app's newest snapshot, one folder per app, each file at its path inside the repo and dated today at 00:00:00. When an app has more than one readme, you tick which ones go in first; license files always go in |
| Scan | Checks the folder |

**Per app**

- The app list shows each app's version on the right; the details show it large next to the
  name ("no version" when the project sets none).
- The filter pills (all, good, info, license, to review, need attention) narrow the list of
  findings. They follow the header's good-to-bad order but use their own words on purpose: the
  header counts apps across the whole scan, the filters count the selected app's checks.
- **Copy findings** copies this app's findings, respecting the current filter.
- **Stage to Downloads** copies the snapshot to Downloads without its version number, every file
  dated the **release date** at 00:00:00. When the snapshot has private files, it asks first:
  **With private files** writes `App.zip` with everything (for RepoBuilder and you);
  **Without (for publishing)** writes `App-PUBLISH.zip` (the suffix is a setting) with every
  private file left out, for RepoPublisher. If the README still mentions a private file, it shows those lines
  and asks before staging the publish copy. Without private files there's no question.
- **Release date** (below the buttons): the day Stage and Apply date every file with, `yyyy-MM-dd`,
  today to start with. The time is always 00:00:00.
- **Show in Explorer** opens the snapshot's folder.
- **Fix.** Some findings on zip snapshots can be fixed: remove solution files, obsolete IDE
  files, git files or the unlisted files you tick, add the publish settings or a version, turn
  off debug information, keep the commit out of the version, or set release timestamps. Queue the fixes, then press **Apply**. A
  preview lists every change before anything is written. The fixed copy goes to Downloads,
  every file dated the release date at 00:00:00, and the history folder stays as it is. It keeps
  the private files: it goes through RepoBuilder before anything is published.
- The badges next to an app name show admin rights, test projects and third-party packages.
  Click the package badge for the approved list.

## Settings

The gear in the header opens the standard. One card per setting: its name, its key in
`AppConsistency.json`, what it does and its default.

| Setting | Key | Built-in default |
|---|---|---|
| Approved packages | `Standard.ApprovedPackages` | none: every third-party package fails until you approve it |
| Private files | `Standard.PrivateFiles` | none |
| Whitelist | `Standard.Whitelist` | `*.cs`, `*.xaml`, `*.csproj`, `*.slnx`, `app.ico`, `app.png`, `app.svg`, `app_*.png`, `app.manifest`, `Directory.Build.props` |
| Web packages | `Standard.WebPackages` | `HtmlAgilityPack`, `Microsoft.Web.WebView2` |
| Minimum target framework | `Standard.MinimumTargetFramework` | `net10.0-windows`; newer passes, same platform |
| Icon and header | `Standard.Icon` | `app.ico`, `app_256.png`, 256 px |
| Publish properties | `Standard.Publish` | `win-x64`, self-contained, single file, native libraries included, compressed, `DebugType none` |
| Release names | `Standard.Release` | `-PUBLISH`, `{yyyy-MM-dd-HH-mm-ss}-REPO-SOURCE-BACKUP.zip`, `{yyyy-MM-dd-HH-mm-ss}-REPO-READMES.zip` |

- **The lock** at a card's top right says where the value comes from. Locked: the built-in default
  applies, the card is read-only, and the key isn't in the file. Unlocked: the setting is written to
  the file and can be edited. Locking it again asks first, then removes it from the file on Save.
- **Lists** have one row per entry with an **Active** switch and **Remove**. Switched off, an entry
  stays in the file without effect (`"Enabled": false`), for a temporary change.
- **Usage** beside each entry: how many apps of the last scan it affects ("used by 3 of 41
  apps", "found in 3 of 41 apps", "matches 51 files in 41 of 41 apps"), with a bar, and **Show apps**
  for the list. It comes from the scan in memory; nothing is read from or written to disk for it.
- **Save and rescan** writes the file and checks every app again; **Cancel** changes nothing. A
  folder that can't be written (Program Files) can't hold settings, and the window says so.
- The header shows which standard is in use (`standard: built-in` or
  `standard: AppConsistency.json`), and both reports name it.
- The file holds only what you've unlocked, plus the last folder. A file that can't be read is
  never overwritten: the built-in standard applies and the status bar says why. Rules (WPF, one
  README, MIT, no AppData and so on), IDE and git file lists and timings stay in the code.

Example `AppConsistency.json`:

```json
{
  "LastPath": "D:\\REPO\\HISTORY",
  "Standard": {
    "ApprovedPackages": [
      { "Name": "HtmlAgilityPack", "MinVersion": "1.11.72", "Reason": "HTML parsing, no built-in .NET parser" }
    ],
    "PrivateFiles": [ "NOTES.md", { "Name": "TODO.md", "Enabled": false } ]
  }
}
```

## Timestamps

Every zip AppConsistency writes (Stage, Apply, BACKUP all, Collect README) has clean release
timestamps: the same files and content, entries in sorted order, every entry dated one day at
00:00:00, and no NTFS or extended timestamps. The same content on the same day, written by the same
AppConsistency build, always gives a byte-identical zip. Zip times are stored in local time without a time zone, so 00:00:00 reads
the same everywhere.

## Newer snapshots while the window is open

AppConsistency watches the history folder. When a new snapshot arrives or one changes, it waits
until the file has been quiet for about two seconds, checks that it has stopped changing, and
then rescans **only that app**: the list, the selection, the filters and the header counts
update in place, and the status bar says what happened (`EolInspector: rescanned, 0027 → 0028`).
A zip that is still being copied in is looked at again shortly and never reported as a failure.
New app folders appear in the list; removed ones leave it. Every 30 seconds it also compares each
app's newest snapshot with the one it checked, from the directory listing alone, as a fallback
for network shares, sleep, or changes the watcher missed.

Before Stage, Apply, BACKUP all and Collect README, the apps involved are looked at again. A
newer snapshot is checked first: with the same findings the action carries on with it; with
different findings it stops and shows them. A fix preview that was opened on an older snapshot
saves nothing.

**Other apps aren't disturbed.** The watcher opens no files; it is one change notification on the
folder, with full sharing. Every file in the history folder is opened read-only with full sharing,
so any other app can still read, write, rename or delete it at the same time. Snapshot zips up
to 200 MB are read into memory in one go and closed straight away, so a file is open for
milliseconds, not for the scan.

## Building

```
dotnet publish AppConsistency/AppConsistency.csproj -c Release -r win-x64 --self-contained true
```

No third-party packages. The publish settings (single file, native libraries included,
compression, no debug information, no commit hash in the version) are already in the csproj.

## Versioning

`<Version>` in `AppConsistency.csproj` is shown in the header. Major and minor stay fixed; only
the build number goes up, with every delivery.

## License

MIT, see `LICENSE.txt`.
