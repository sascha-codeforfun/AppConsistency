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

**Two systems.** An app whose folder name ends in `LINUX` is a Linux app, written with Avalonia
where a Windows app uses WPF. It is held to the same standard, with the Windows-only parts
replaced by their Linux counterparts or reported as not applicable; see *Linux apps*. Everything
else in this file describes a Windows app unless it says otherwise.

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
- A folder whose name ends in `LINUX`, in capitals (`CrawlerSuiteLINUX`), holds a Linux app. The
  app keeps its name inside: the assembly in `RepoBuilderLINUX` is `RepoBuilder`.
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

The table and the rules below are a Windows app's. *Linux apps* says what changes for a Linux app.

| Category | Checks |
|---|---|
| Package | Single wrapper folder, no build output (`bin`, `obj`, `.vs`) in the zip, no obsolete IDE files, no git files, no unlisted files, release timestamps |
| Project | Project file and exe count, solution files, target framework (`net10.0-windows` or newer), WPF, output type `WinExe`, tests kept out of the app build, `README`, license, version |
| Publish | Self-contained single-file publish settings |
| Icon and header | `app.ico` as the exe icon, `app_256.png` as a resource, header block with the app name and description |
| Dark mode | Dark title bar, dark window background |
| Dependencies | Third-party packages against the approved list, no loose DLL references, test project packages |
| Security | Admin rights, web content combined with admin rights, what an app does to your computer and your build declared in the README |
| Build leaks | Deterministic compilation, no debug information, no commit hash in the version, no build paths, machine values, build times or path-hashed names baked into the output |
| Code | Single-file safe paths (no `Assembly.Location`), no AppData, writes next to the exe, writes machine-wide |

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
  `Directory.Build.props`, the README and the license. Files a project names count as listed too,
  whether the build uses them or only keeps them alongside: its `Resource`, `EmbeddedResource`,
  `Content`, `Page` and `Compile` items (wildcards included), its `None` items, copied or not (such
  as the source drawing of an icon the app embeds), the files `ApplicationIcon` and
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

  Clicking a file in the list previews it on the right, so a stray old file can be told from one
  that is needed. Clicking selects; only the tick box (or Space) ticks. The preview shows:
  - **Facts**: encoding, line endings, number of lines and size; for a picture, its format and
    pixel size.
  - **Named in**: every line elsewhere in the repo that names the file, as *file:line* with the
    line itself, or *Named nowhere else in the repo*.
  - **Paths it names**: for a text file, the file and folder paths it mentions that aren't in the
    repo, which shows an outdated layout note or a log of earlier versions for what it is. A word
    counts as a path when it has a separator or ends in an extension some file in the repo has;
    web addresses, rooted paths and build output folders are left out.
  - **The file**: what it is is judged by its content, not its extension. Text (UTF-8, UTF-16
    with a byte order mark, or 8-bit, without NUL or stray control characters) shows as text, the
    first 2,000 lines. PNG, JPEG, GIF, BMP and ICO show as a thumbnail; click it (or Enter) for a
    window at full size with zoom (Ctrl+wheel or + / −, 0 to fit, 1 for 100 %, Esc to close).
    SVG is text, so it shows as text. Anything else shows its first 512 bytes in hex.
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
- **Declared in the README.** Some things an app can do are only fine when it says so up front:
  running as administrator, changing the firewall, leaving a scheduled task behind, running code
  when you build it. Every such thing needs a line in a README section with exactly this heading,
  at any level from `#` to `######`. For FirewallManager:

  ```markdown
  ## Before you build or run it

  - **Needs administrator rights:** runs as administrator; Windows asks when it starts.
  - **Changes security settings:** changes Windows Firewall policy (rules, profiles, logging) and Defender's cloud protection settings.
  - **Starts on its own:** creates a scheduled task that restores the firewall if a lock-down goes wrong.
  - **Starts other programs:** netsh, gpupdate, auditpol, reg and PowerShell.
  - **Changes the whole computer:** writes under HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall.
  - **Talks to other computers:** looks up claude.ai, api.anthropic.com and api.nuget.org to build allow rules; sends nothing.
  ```

  A declaration tells a reader the author meant it; it doesn't make it safe. So each label says, as a
  verb, what the app does, and the rule only checks that it's said.

  One bullet per topic, starting with its label in bold and a colon (`**Label:**` or `**Label**:`),
  then a sentence in plain words. A topic the app doesn't touch is left out; an app with nothing to
  declare needs no section at all. The labels are fixed, and what counts for each is read from the
  code and the project files, never run:

  | Label | What calls for it |
  |---|---|
  | **Needs administrator rights** | `requireAdministrator` or `highestAvailable` in the app manifest, `Verb = "runas"` |
  | **Changes security settings** | `netsh advfirewall` set/add/delete/import, `auditpol /set`, `bcdedit`, `vssadmin delete shadows`, Defender and firewall PowerShell commands, the `HNetCfg.FwPolicy2` COM type, the machine's certificate store opened for writing, and the firewall, Defender, UAC and audit registry paths where the same method writes to the registry |
  | **Starts on its own** | `schtasks /create`, Task Scheduler XML, the `Schedule.Service` COM type, `sc create`, `New-Service`, a service installer, a `CurrentVersion\Run` key written, a file written to the Startup folder |
  | **Runs code from outside the app** | `Assembly.LoadFrom`, `Assembly.Load` of bytes, `NativeLibrary`, compiling or emitting code at run time, PowerShell `Invoke-Expression`, starting a file the same method downloaded or wrote |
  | **Runs code when you build** | An `<Exec>` anywhere in a project, `.props` or `.targets` file; a target that runs a real task (anything but `Message`, `Warning`, `Error` and setting properties or items) and joins the build through `BeforeTargets`, `AfterTargets`, a hook name such as `BeforeBuild`, `InitialTargets`, or `DependsOnTargets` / `CallTarget` from one of those; an inline or compiled task (`UsingTask`); an analyzer `ProjectReference`; a project of the repo that is an analyzer or source generator |
  | **Starts other programs** | Every program started that the check can name, by its name. Opening a URL, a folder, a document or Explorer doesn't count; a program it can't name is mentioned, not counted |
  | **Changes the whole computer** | A registry write under HKLM, `reg import`, a file written under Program Files, ProgramData or Windows |
  | **Talks to other computers** | `HttpClient`, `WebClient`, sockets, `Dns` lookups, `Ping`, mail, a web server. The hosts written in that code are named |
  | **Reads or sends personal data** | The Windows user name, stored credentials, protected data, reading the clipboard, browser profile folders |

  A tool counts where the code names it, whether it starts it directly or through a helper
  (`Netsh(…)`, `Run(Tool.Schtasks, "/create …")`). Comments never count, and neither do messages:
  a command or a path counts only where the code uses it. An API counts by the shape of its use,
  `new NetworkCredential(…)` or `CredentialCache.DefaultCredentials`, never because something is
  merely named like it: a method called `NetworkCredential` is no credential. App code counts only if it ends up in the
  exe: the app project and the projects it references, without test projects and without code a
  project leaves out of its build. Build files count wherever they are, since whoever builds the
  repo runs them. A PowerShell script passed with `-EncodedCommand` isn't counted here: encoding is
  about how readable the code is, which RepoLegibility judges.

  Each topic is its own row, *Declared in the README (Talks to other computers)* and so on, so an
  exemption can cover one topic. An undeclared *Needs administrator rights*, *Changes security
  settings*, *Starts on its own*, *Runs code from outside the app* or *Runs code when you build*
  **fails**: they change how safe the machine is, keep code running after the app is gone, or run
  code nobody has read. The other four **warn**. The row names what was found, with file and line,
  and gives a ready line to paste. A declared topic the check doesn't find **warns** too: a line the
  code no longer bears out is a false statement as well, so when an app stops doing something, the
  README follows. Its Fix removes the line (and the section, once nothing is left in it). If the app
  does it in a way the check can't see, exempt that topic with the reason. One such way the check
  does see: an app that makes no network call itself but starts `dotnet`, which fetches the packages
  its cache doesn't hold from nuget.org. Where the README declares *Talks to other computers* and the
  code names `dotnet` (alone, or as the last part of a path) and starts programs, the line is borne
  out and the Pass row says so. This only ever confirms a declared line: an app that starts `dotnet`
  and declares nothing is not asked to. An unknown label or a label used twice warns, and an app whose
  topics are all declared gets one Pass row that names them as declared. The labels of the first
  version of this section (*Stays on the machine*, *Personal data* and the rest) are unknown labels
  now; the warning names the new one. A heading or bullet inside a code block, like the example
  above, doesn't count. The Fix on a row adds its suggested line to the README, adding the section
  if there is none: before the first `##` heading that starts with building or running (*Build*,
  *Installation*, *Requirements*, *Usage*, …), or else before the first `##` heading. The line is a
  draft from what the check found, shown word for word in the preview; the wording stays yours to
  change. RepoLegibility reads the same section with the same labels and shows a declared finding as
  *declared in the README*, without changing how serious it is.
- **No AppData.** Anything an app writes under the user's AppData (Local, Roaming or LocalLow)
  is left behind for good when the app is gone, so it **fails**, every time. Settings belong in
  `AssemblyName.json` next to the exe. Read from the code, nothing is run: every call that writes a
  file or creates a folder is followed back through the variables, fields, properties and methods
  its path is built from, to where the folder comes from (`SpecialFolder.LocalApplicationData`,
  `SpecialFolder.ApplicationData`, `%LOCALAPPDATA%`, `%APPDATA%`, or an `AppData\` path in the
  current user's profile). A bare `AppData\…` text on its own, such as a path into another user's
  profile or a list of other apps' folders, places nothing. Each write is listed with its file and
  line, and with the names its path came through when it came through other names
  (`via ChangeSetsDirectory → BackupRoot at AppPaths.cs line 12`), so a surprising place can be
  checked from the finding itself. Reading or deleting there doesn't count. Writes to the
  temp folder are listed as Info: temp is meant for that, and only the app knows whether it cleans
  up. Code a project leaves out of its build (`<Compile Remove>`) and test projects are skipped.
  Paths built with string interpolation are followed through their holes:
  `$"{AppContext.BaseDirectory}\\{name}.json"` leads to the exe's folder. Text inside strings and
  comments is never taken for a call or a definition. A name is followed to its own definition:
  `AppPaths.Root` only to `Root` in `AppPaths`, a bare `FilePath` to the enclosing class's own (or
  to the only one in the code, never to one of several), and properties with a getter body
  (`get { … return …; }`) by what they return. A member of a variable (`dialog.FileName`,
  `_config.Folder`, `ctx.Folder`) isn't followed at all: neither the member nor the object is the
  path, so a same-named member elsewhere or the settings file the object came from can't lend the
  path its folder. A path from a file or folder dialog
  is the user's choice and isn't listed. A path assembled in ways the check can't follow is missed,
  never guessed.
- **Writes next to the exe.** `AssemblyName.json` there is the settings standard. Anything else
  written next to the exe (other settings names, logs, caches) is listed as Info, since a read-only
  folder such as Program Files makes those writes fail.
- **Writes machine-wide.** Writes outside every user's folders are listed as Info: under the system
  drive's root (`Path.GetPathRoot(Environment.SystemDirectory)`, `%SystemDrive%`), Windows, Program
  Files, ProgramData, folders shared by all users, or a fixed path such as `C:\Tools`. They stay
  when the app and every user are gone and usually need admin rights. Some apps need them on
  purpose (a backup only Administrators may change); the README declares them under *Changes the
  whole computer*, and these writes count as evidence for that topic.
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

  With no license file at all, on a zip, the Fix adds the standard MIT license as `LICENSE.txt` in
  the wrapper folder: the release date's year and the license holder from *Settings*, UTF-8 with LF
  line endings, so the same year and name always give the same file. It's only offered once the
  license holder is set, and it never replaces or edits a license that's already there.
- **Image files.** Not a verdict either: like the license, it's shown in **indigo** and never
  makes an app warn or fail. Every PNG, ICO and JPEG (found by extension, then recognised by its
  bytes) is held to its **level**:
  - **As is**: nothing changes, every byte stays, metadata included. For files that must stay
    exactly as they are: a sample where a shade carries meaning, or a license that asks for the
    file unaltered or with its metadata.
  - **Strip**: metadata nothing displays is removed: C2PA content credentials, text chunks,
    EXIF (GPS position, camera, software, serial number, thumbnail), XMP, IPTC, comments, time
    stamps, background colour, private chunks and bytes after the end of the image. What changes
    how the picture looks stays: colour profile, gamma, colour space, pixel density (WPF sizes an
    image by it) and, for a JPEG, the EXIF orientation flag, written back alone so a photo still
    shows the right way up. A JPEG's compressed picture is copied byte for byte.
  - **Lossless** (the default): Strip, then recompressed with every pixel kept, which is checked
    twice: by ImageKit's own decoder and by Windows' decoder. A JPEG stays at Strip, since
    re-encoding it always loses quality; the PNG images inside an .ico are re-encoded as RGBA
    8-bit only, what icon loaders expect.
  - **Smallest**: Lossless, plus a palette of up to 256 colours where the loss stays within the
    limit. The limit is measured as CIEDE2000 ΔE: **invisible** (the default) means 99 % of pixels
    within 1.0 and no spot beyond 3.0, **slight** 99 % within 2.3 and no spot beyond 6.0. A spot is
    the difference after averaging 3 × 3 pixels, the way the eye blends neighbouring pixels: a lone
    anti-aliased edge pixel that shifted shade counts for little, a changed area counts in full.

  The level comes from *Settings*: an entry for the app and the file (or a folder pattern), or
  the default. The row lists each file that doesn't meet its level yet, with what it carries and
  about how much smaller it would get without loss, and shows a small preview of every image file,
  to-do ones first; a click opens it at full size. A file's saving counts from 10 % and 4 KB. The
  row also says how much of the repo the images are and what the saving is against the packed
  zip: on a small app the effect is big, on a large one negligible. Metadata always makes the row
  **artwork**, in the header and the filters; a saving alone does so only from 1 % of the packed
  zip, below that it's noted as Info. Files kept as they are are listed for information.

  The Fix opens a picker: every image with its level, which can be changed there, and nothing
  ticked. Clicking a file shows it with what its level does: the metadata that goes and what stays,
  the size before and after. For Smallest, the palette is shown next to the original, with a map
  of what changed (red, brighter where the change is larger), all of it at full size on a click;
  it's used only when you tick **Use the palette** for that file. A changed level can be
  remembered in *Settings*, so the next scan knows it. Only the ticked files change, in the fixed
  copy.
- **Version.** The app project's `<Version>` (or `VersionPrefix`), from the `.csproj` or a
  `Directory.Build.props` above it. None warns, because every build is then 1.0.0 and the header
  has no version to show; on a zip the Fix adds `<Version>1.0.0</Version>`.
- **No commit in the version.** Every project that ends up in the exe needs
  `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>`,
  in the `.csproj` or a `.props` file. Without it the SDK appends `+<commit hash>` to the product
  version whenever the build finds git information, so a build from a clone differs from one from a
  zip and changes with every commit. Missing or `true` fails; on a zip the Fix adds it next to
  `DebugType`, or in a group of its own when there's none.

## Linux apps

RepoBuilder builds for Linux as well, and files a Linux app's source under
`HISTORY\<App>LINUX\<App>LINUX-NNNN.zip`. AppConsistency takes the system from that folder name:
it ends in `LINUX`, in capitals and exactly, and is longer than the suffix. `PenguinLinux` is a
Windows app. The list shows *Linux* beside such an app, the details a *Linux* or *Windows* tag, and
both reports name a Linux app as one.

**The folder and the project have to agree.** That is a check of its own, *System*, the first row
under Project. A Linux app always has the row. A Windows app gets it only when something is off,
so a Windows app's findings are what they were before Linux apps existed.

| Folder | App project | System row | Judged as |
|---|---|---|---|
| `…LINUX` | a `linux-` runtime, or none | Pass | Linux |
| `…LINUX` | WPF, Windows Forms or a `-windows` framework | **Fail**: a Windows-only project filed as Linux | Windows |
| `…LINUX` | a `win-` runtime | **Fail**: the project is for Windows, the folder says Linux | Linux |
| plain | a `linux-` runtime | **Fail**: a Linux project filed as Windows | Linux |
| plain | holds `.axaml` files | **Warn**: Avalonia markup in a folder that isn't a Linux app's | Windows |
| plain | anything else | no row | Windows |

A snapshot in the wrong folder gets that one failure. The other rules then judge the app by what
its project is, so the finding isn't buried under the other system's rules. A plain folder whose
project references Avalonia without a `linux-` runtime is a Windows app with an unapproved
package, as before.

**What each rule does for a Linux app.** Every rule is in one of three groups: unchanged, replaced
by its Linux counterpart, or not applicable. A rule that doesn't apply stays in the list as Info
with an **N/A** pill and the reason, so it is plain it was considered. It counts as neither passed
nor failed.

| Rule | For a Linux app |
|---|---|
| Single wrapper folder, No build output, No obsolete IDE files, No git files, Private files, Release timestamps, Image files | Unchanged |
| Unlisted files | Unchanged, with the Linux whitelist on top: `*.axaml`. `AvaloniaResource` and `AvaloniaXaml` items name files like `Resource` and `Page` do |
| System | New, see above |
| Project file, No solution files, Tests kept out of the app build, README, README mentions private files, License, Version | Unchanged |
| Target framework | Replaced: `net10.0` or newer **without** a platform. `net10.0-windows` fails |
| WPF | Replaced by **Avalonia**: the app project references the `Avalonia` package and sets neither `UseWPF` nor `UseWindowsForms` |
| Output type | `WinExe` passes, and so does `Exe`: on Linux they are the same |
| Publish settings | Replaced: the same five properties with a `linux-` runtime (`linux-x64`, `linux-arm64`). The Fix writes the Linux runtime |
| Exe icon, Icon file, Header image file | Unchanged: `app.ico` as `ApplicationIcon`, `app_256.png` for the header |
| Header image resource | Replaced: `<AvaloniaResource>`, or `EmbeddedResource` read with `GetManifestResourceStream`. WPF's `<Resource>` warns |
| Header block | Unchanged, read from `.axaml` instead of `.xaml`, or from code |
| Dark title bar | **N/A**: the desktop (KDE, GNOME) draws the title bar; an app cannot colour it |
| Dark theme | New: Avalonia's theme variant is set to dark, `RequestedThemeVariant="Dark"` on the `Application` in `App.axaml` or `RequestedThemeVariant = ThemeVariant.Dark` in code. Missing or another variant fails |
| Dark window background | Unchanged, read from `.axaml`: the window's own `Background`, its `ControlTheme` (`Theme="{StaticResource Hue.Window}"`) or a `Style Selector="Window"`. A window that sets none takes the theme's and passes, where WPF's white default fails |
| Third-party packages | Replaced: the Hybrid list **plus** the Linux list, where a Windows app has Hybrid plus Windows (see *Settings*). A package on the forbidden list fails whatever else is approved. The package badge follows |
| Avalonia packages on one version | New: every Avalonia package of the app and its libraries on one version, except those with a version line of their own (`Avalonia.Controls.DataGrid`). Different versions fail |
| No loose DLL references, Test project packages | Unchanged |
| Admin rights | Replaced: a Linux program has no manifest. A start of `sudo`, `pkexec`, `doas` or `su` is what asks for admin rights, and is reported |
| Web content with admin rights | Unchanged, on that basis |
| Declared in the README | Unchanged for what .NET does the same on both systems: starting programs, the network, loading code, the build, personal data. See below for the rest |
| Build leaks (all of them) | Unchanged |
| Single-file safe paths, Writes next to the exe | Unchanged |
| No AppData | Unchanged where the code uses .NET's folders: on Linux `ApplicationData` is `~/.config` and `LocalApplicationData` is `~/.local/share`, and a write there fails. With nothing found the row is Info, not Pass: paths written out as text (`~/.config`, `$XDG_CONFIG_HOME`) aren't followed yet |
| Writes machine-wide | Not checked on Linux, and says so: `/etc`, `/usr`, `/opt` and `/var` aren't known yet |
| Answers --version | New: the code compares an argument with `--version`. A Linux program file carries no version a file manager could show, so the suite's programs print name, version and the SHA-256 of their own file. Not found warns |
| Name says Linux | New: a window title in `.axaml` or a text constant in the code says "… for Linux", so a screenshot can't be taken for the Windows edition's. Not found warns. Only the title and constants are read; the header and the `--version` output aren't checked one by one |

**What isn't checked on Linux yet.** The declaration check knows the Windows names for four topics
(netsh, the registry, Program Files), which find nothing in a Linux app whatever it does. So every
Linux app has a row *Declared in the README (not checked on Linux)* that lists them:

- **Needs administrator rights**: only a start of `sudo`, `pkexec`, `doas` or `su` is seen.
- **Changes security settings**: none of Linux's forms (ufw, iptables, nftables, SELinux, AppArmor,
  polkit).
- **Starts on its own**: only `systemctl enable`, `crontab` and a file written under
  `~/.config/autostart`.
- **Changes the whole computer**: not `/etc`, `/usr`, `/opt` or `/var`.

Where one of these is found, the line in the README is required as on Windows. Where one is
declared and nothing is found, the row is Info: the check can't call a line out of date that it
couldn't have confirmed. For the other five topics a declared line the code doesn't bear out warns,
as on Windows.

`new ProcessStartInfo("xdg-open")`, with the program as one string literal, is read for Linux apps.
For Windows apps it is passed over, as in every version so far, so their findings don't change with
this version. `xdg-open` and `dbus-send` are programs like any other: they belong under *Starts
other programs*.

**Fixes** are offered for Linux apps as for Windows apps. They change the repo zip only, which is
source: a Linux program's executable flag is set in the `tar.gz` RepoBuilder makes, not here. The
zips AppConsistency writes don't carry Unix permission bits, so a repo that one day holds a shell
script would lose its executable flag in a staged or fixed copy.

## Exemptions

Sometimes an app has a real reason to break a rule. An exemption in your `AppConsistency.json` (or
in *Settings*) records it, with the reason:

```json
"Standard": {
  "Exemptions": [
    {
      "Rule": "No AppData",
      "App": "EremitVM",
      "File": "Services/ConsolePreset.cs",
      "Method": "Ensure",
      "Origin": "ApplicationData",
      "Reason": "VMConnect's own per-VM settings file; VMConnect reads it only from its own AppData folder. Removed with the VM."
    }
  ]
}
```

- `Rule`, `App` and `Reason` are required. `Rule` is the name as the findings show it; a project's
  own row of a rule, like *No debug information (Core)*, counts as that rule. `App` is the app's
  folder name in the history folder.
- `File`, `Method` and `Origin` narrow it, and each one given has to match. `File` is the end of a
  path, at a folder boundary. `Method` (the method a write sits in) and `Origin` (where it points:
  `ApplicationData`, `LocalApplicationData`, a whole word of what the finding shows) apply to the
  write checks, where every write is matched on its own: a new AppData write somewhere else still
  fails. On other rules, `File` means the finding mentions that file.
- Only findings that warn or fail can be exempted. A covered finding shows a grey **Exempt** pill
  with the reason above the original detail, and no longer makes the app warn or fail; the app's
  line says how many are exempt, and the report lists them with their reasons.
- An exemption that covers nothing in its app's scan shows as *Unused exemption* (Info): the rule
  passes now, or a name is spelled differently. One for an app that isn't in the folder is named in
  the status bar after a full scan.
- `"Enabled": false` keeps an entry in the file without effect.

The built-in list is empty: exemptions are your decisions, so they live only in your JSON.

## The window

**Header.** On the left are the app name and description. In the centre are pills counting the
scanned apps by their worst status (passed, warned, failed), and in indigo the apps with a
license to review (no license file, another license, or MIT that isn't standard) and the apps
with artwork to review (image files that don't meet their level yet). Click one to
show only those apps in the list; click it again, or another pill, to change the filter. A new
scan shows all apps again. On the right are the version, the SDK the exe was built with, and the
runtime it's running on.

**Toolbar**

| Button | What it does |
|---|---|
| Browse… | Pick the history folder |
| Copy report | Copies every warning and failure across all apps as plain text, plus every license and artwork to review |
| BACKUP all | Writes `yyyy-MM-dd-HH-mm-ss-REPO-SOURCE-BACKUP.zip` to Downloads, holding the newest snapshot of every app under its name without the version number (`EolInspector.zip`). Everything inside, each repo zip and every file in it, is dated the day you press the button at 00:00:00, so code from a backup is easy to tell apart. Private files are always included: it's your archive. The file name keeps the full time, so several backups on one day stay apart, and the backup file itself keeps its real time |
| Collect README | Writes `yyyy-MM-dd-HH-mm-ss-REPO-READMES.zip` to Downloads, holding the readmes and license files from every app's newest snapshot, one folder per app, each file at its path inside the repo and dated today at 00:00:00. When an app has more than one readme, you tick which ones go in first; license files always go in |
| Scan | Checks the folder. Every app is listed at once, greyed, and lights up as soon as its check is done; finished apps can be opened while the rest run. Apps are checked side by side on all cores but one, and the status bar says how long the scan took |

**Per app**

- The app list shows each app's version on the right; the details show it large next to the
  name ("no version" when the project sets none). Under the name: the snapshot, and the repo's
  size packed (the zip) and unpacked, with its number of files.
- The filter pills (all, good, info, license, artwork, to review, need attention) narrow the list of
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
  off debug information, keep the commit out of the version, add the MIT license, change the
  image files you tick at their level, or set release timestamps. Queue the fixes, then press **Apply**. A
  preview lists every change before anything is written. The fixed copy goes to Downloads,
  every file dated the release date at 00:00:00, and the history folder stays as it is. It keeps
  the private files: it goes through RepoBuilder before anything is published.
- The badges next to an app name show the system (Windows or Linux), admin rights, test projects
  and third-party packages. Click the package badge for the packages the app may use: the Hybrid
  list and its own system's.

## Settings

The gear in the header opens the standard. One card per setting: its name, its key in
`AppConsistency.json`, what it does and its default.

| Setting | Key | Built-in default |
|---|---|---|
| Hybrid: approved packages | `Standard.Hybrid.ApprovedPackages` | none. Packages for Windows and Linux apps alike |
| Windows: approved packages | `Standard.Windows.ApprovedPackages` | none. On top of the Hybrid ones, for Windows apps only |
| Windows: minimum target framework | `Standard.Windows.MinimumTargetFramework` | `net10.0-windows`; newer passes, same platform |
| Windows: runtime | `Standard.Windows.RuntimeIdentifier` | `win-x64`: what the publish fix writes. The check accepts any `win-` runtime |
| Linux: approved packages | `Standard.Linux.ApprovedPackages` | none. On top of the Hybrid ones, for Linux apps only: Avalonia and its parts |
| Linux: forbidden packages | `Standard.Linux.ForbiddenPackages` | `Avalonia.Desktop`: it brings the Windows and macOS backends along |
| Linux: packages with a version of their own | `Standard.Linux.OwnVersionPackages` | `Avalonia.Controls.DataGrid` |
| Linux: whitelist | `Standard.Linux.Whitelist` | `*.axaml`, on top of the whitelist |
| Linux: minimum target framework | `Standard.Linux.MinimumTargetFramework` | `net10.0`; newer passes, a platform suffix fails |
| Linux: runtime | `Standard.Linux.RuntimeIdentifier` | `linux-x64`: what the publish fix writes. The check accepts any `linux-` runtime |
| Private files | `Standard.PrivateFiles` | none |
| License holder | `Standard.LicenseHolder` | none: the MIT license fix isn't offered until you set it |
| Exemptions | `Standard.Exemptions` | none: every app is held to every rule |
| Images | `Standard.Images` | default level `Lossless`, palette limit `Invisible` |
| Image levels | `Standard.ImageLevels` | none: every image file gets the default level; the image picker adds an entry when you change a level there |
| Whitelist | `Standard.Whitelist` | `*.cs`, `*.xaml`, `*.csproj`, `*.slnx`, `app.ico`, `app.png`, `app.svg`, `app_*.png`, `app.manifest`, `Directory.Build.props` |
| Web packages | `Standard.WebPackages` | `HtmlAgilityPack`, `Microsoft.Web.WebView2` |
| Icon and header | `Standard.Icon` | `app.ico`, `app_256.png`, 256 px |
| Publish properties | `Standard.Publish` | self-contained, single file, native libraries included, compressed, `DebugType none`. The runtime is the system's own |
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

**What differs by system sits in three blocks** inside `Standard`; everything else has nothing to do
with the system and sits directly in `Standard`.

- `Hybrid` holds packages approved for both systems: the ones Windows and Linux apps really share,
  such as an HTML parser both editions of one app ship.
- `Windows` holds what is true of Windows apps only: their packages, the lowest target framework and
  the runtime the publish fix writes.
- `Linux` mirrors it for Linux apps, and adds the forbidden packages, the packages with a version of
  their own, and the extra whitelist.

An app may use a package that is in `Hybrid` or in its own system's list. A package in the other
system's list is not approved: a Windows app that references Avalonia fails, and so does a Linux app
that references a Windows-only package. A minimum version is a floor, not a pin: `12.1.4` approves
`12.1.4` and newer. Packages only a test project uses are never checked, so they need no entry.
All three lists are empty by default: which packages and versions to trust is yours to decide.

**A file from before 1.0.31** had one `Standard.ApprovedPackages` list, `Standard.MinimumTargetFramework`
and the runtime inside `Standard.Publish`. It keeps working and means what it meant: the one list is
read as the `Hybrid` list, since it counted for every app, and the framework and runtime as the
`Windows` ones. The file is rewritten in today's layout the next time the settings are saved. Moving
the Windows-only packages from `Hybrid` to `Windows` is then yours to do, in Settings.

Example `AppConsistency.json`:

```json
{
  "LastPath": "D:\\REPO\\HISTORY",
  "Standard": {
    "Hybrid": {
      "ApprovedPackages": [
        { "Name": "HtmlAgilityPack", "MinVersion": "1.11.72", "Reason": "HTML parsing, no built-in .NET parser" }
      ]
    },
    "Windows": {
      "ApprovedPackages": [
        { "Name": "Microsoft.Web.WebView2", "MinVersion": "1.0.4129.50", "Reason": "embedded browser, no built-in WPF equivalent" }
      ]
    },
    "Linux": {
      "ApprovedPackages": [
        { "Name": "Avalonia", "MinVersion": "12.1.4", "Reason": "the window toolkit, what WPF is on Windows" },
        { "Name": "Avalonia.X11", "MinVersion": "12.1.4", "Reason": "Linux windowing, also under XWayland" }
      ]
    },
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

## Reusing ImageKit

The image work lives in `ImageKit/`, its own namespace, with nothing from AppConsistency or WPF
in it: only .NET's own `System.IO.Compression` for zlib. Copy the folder into another project
and it builds.

```csharp
using ImageKit;

ImageInfo info = ImageFile.Inspect(bytes);         // format, size, metadata found and what it holds
ImageResult r = ImageFile.Process(bytes, ImageLevel.Lossless, new ImageOptions
{
    LossLimit = LossLimit.Invisible,               // for ImageLevel.Smallest
    ExternalDecoder = myDecoder                    // optional second opinion on every result
});
// r.Bytes, r.Changed, r.Removed, r.SavedBytes, r.PixelsIdentical, r.Palette (ΔE), r.Notes

PngImage png = Png.Decode(bytes);                  // every colour type, bit depth, interlacing
byte[] smaller = Png.Encode(png);                  // smallest lossless form it finds
PngImage? reduced = Palette.Quantize(png, 256);    // perceptual palette, no dithering
LossStats loss = ColourDifference.Measure(a, b, width, colours);
```

The format is recognised from the bytes, never from a file name. Nothing throws on a damaged
file: `Inspect` reports the problem, `Process` hands back the original bytes with a note.

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
