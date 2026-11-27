# Architecture

Loom (code name Lokn) is a single-process Windows desktop shell: a docking workspace whose
features are modules. The executable is `Loom.exe`; namespaces, projects and data folders
keep the name Lokn. This page is the map. [modules.md](modules.md) is the module contract
in detail, and [omp-backend.md](omp-backend.md) covers the Omp transport.

## Layers

Dependencies point one way: features and the shell depend on Core, never the reverse.

| Layer | Assembly | Holds | May reference |
|---|---|---|---|
| `Core/` | `Lokn.Core` | Module contracts, `LoknApplication`, the hosts (`LoknHosts`: This PC and paired hosts), command registry, search registry, panel workspace and docking tree, settings, the notification feed, `Theme`, `IWorkspaceHost` with `LocalWorkspaceHost` (processes, files, watchers and the pseudoconsole: ConPTY on Windows, a Linux pty elsewhere) and `OfflineWorkspaceHost`, `DataUpgrade` (`version.json`, backups and restore; see [Data and upgrades](#data-and-upgrades)) and `IAgentActivity` (how many agents work on This PC) | Goo only |
| `Shell/` | `Lokn` | Goo views: window chrome with its titlebar, sidebar and status bar slots, the sidebar column and profile switcher, dock rendering, the Search palette, the settings editor (a Shell tab, the Modules tab with its Community list, then one tab per module), `ShellModule` (id `shell`: window settings and commands, the `shell_*` agent tools, and the Search palette's Panels, Actions and Settings), `ModuleStates` (which modules are on, in `modules.json`), `ThemeModule` in `Theming/` (the theme file, the Tern catalog and the picker; it cannot be disabled), `ModuleHost` with `ModuleCompatibility` (loading module DLLs and module updates that fit), `UpdatesModule` in `Updates/` (id `updates`: app and module updates; see [Updates](#updates)), `CommunityModule` in `Community/` (id `community`: installs, updates and removes modules from the community hub as source folders Hotload loads; see [modules.md](modules.md#community-modules)), and `AttentionNotifier` (acknowledging requests the user looked at, and opening clicked Windows notifications) | Core, Goo |
| `Modules/<Name>/` | one `Lokn.Modules.<Name>` DLL each | Features: `Omp` (also publishes `IAgentService`), `Git` (the one git runner, `GitProcess`, each watched folder's live repository, branch and changes, the Git panel with its graph and file diff modes and its `git.actions` and `git.messages` points, and the Files side view's Changes and History modes), `Projects` (a named working folder per profile with a session of its own, its icon and the trusted modules it carries, and `IWorkingFolder`, with the `projects.folders` point other sessions such as tasks give their folder through; it lists them in the sidebar or tab strip while Conversations is not loaded), `Conversations` (with the chat panel in `Chat/`, Home: the empty state of a conversation panel, opening each project's session through Projects, and the Git views' review in chat and commit message writer; Git is optional), `Markdown` (`IMarkdownRenderer` and the chat's Markdown view), `Files` (the side view at the left of the workspace: the file tree and the modes other modules add through `files.modes`), `Editor` (a text editor with history, highlighting and gutter marks, `lokn://editor/open`), `Notifications` (the feed page, the bell, its popover and banners, the chime and Windows notifications; it depends on no module, and modules describe their notifications with `NotificationKind`), `GitHub`, `Worktrees`, `Terminal`, `Workspaces` (saved workspaces, one set per session; always on), `Browser`, and the example `Welcome` | Core, Goo, and another module only through a project reference plus a declared dependency |
| `Modules/Hotload/` | `Lokn` | The runtime compiler for source modules; part of the host because it carries Roslyn | Core, Goo |
| `Program.cs` | `Lokn` | Backs the data up when a newer version first runs on it (`DataUpgrade.Prepare`), configures the Goo app identity and single instance, opens the window while the module host reads, loads and checks the module DLLs on a background thread (`ModuleHost.BeginStart`), then on the open window starts the modules, loads the shell's own modules, restores the workspace and raises `LoknApplication.Started`, where modules open their own startup view (`ModuleContext.WhenStarted`), all before the first frame; with no startup view the shell shows its empty pane | Core and shell only, never a feature module, not even by ID |
| Source modules | one in-memory assembly per folder | Features compiled at runtime from `%LOCALAPPDATA%\Lokn\modules` | Core and Goo public API only |
| `tests/Lokn.Tests/` | `Lokn.Tests` | Executable checks, no test framework | Everything, including internals |

Core has no knowledge of views. The shell renders Core's state and never owns
feature state. `Lokn.Core` exposes internals to `Lokn`, `Lokn.Tests` and the module
assemblies it names in `Core/Lokn.Core.csproj`, so the shell and built-in modules can
call internal members such as `LoknApplication.ReportError`; source modules cannot.

The application never references a feature module at compile time. A module the
executable referenced would load with it and could not be replaced while it runs.

## The application object

`LoknApplication` is the one shared service object. Everything reaches the rest of
the app through it:

- `Commands` (`CommandRegistry`): every command and shortcut; the Search palette lists them
  under Actions.
- `Search` (`SearchRegistry`): the providers behind the Search palette, one or more per type
  such as Tasks or Chats, its ranking, and the items the user ran from it, per profile; see
  [modules.md](modules.md#search).
- `Tools` (`AgentToolRegistry`): tools modules offer to agents. Omp chats offer them
  to their agent through `OmpToolBridge`; see [omp-backend.md](omp-backend.md#lokn-tools-for-agents).
- `Panels` (`PanelWorkspace`): open panels and the docking tree.
- `Settings` (`SettingsService`): typed settings with global and per-profile layers.
- `Notifications` (`NotificationFeed`): the feed behind the titlebar bell, one for all
  profiles. `AttentionNotifier` posts each attention request the user did not see, and
  marks a panel's entries read when its request clears.
- `Load`, `Unload`, `Enable`, `Reload`, `Remove`: the module lifecycle.
- `GetModule<T>()`: the loaded module of a type, which is how one module reaches
  another's services, such as `OmpModule.Service` or `ConversationsModule.Hosts`.
- `Hosts` (`LoknHosts`): the machines this Loom shows. `Hosts.Local` is This PC (in-process,
  or through its daemon); the Remote module adds each paired host with `Hosts.Add` and
  removes it with `Hosts.Remove`. Each `LoknHost` has an `Id` (`"local"` or the paired
  host's id), an observable `Name`, `State` (`Local`, `Connecting`, `Connected`, `Failed`,
  `Stopped`, `Removed`), `StatusText` and `CanControl`, its own `BrowserTabs`, and its
  `Workspace` (`IWorkspaceHost`): the disk, processes, file watchers and pseudoterminals of
  the user's work there. This PC's is `LocalWorkspaceHost.Shared`; a paired host's is the
  Remote module's while connected and an `OfflineWorkspaceHost` (every call fails)
  otherwise; replacing it raises the host's `WorkspaceChanged`. `Hosts.Current` is the host
  of the current session, which the sidebar's host switcher changes; `Hosts.Get` gives a
  `Removed` placeholder for an id no longer paired, such as one a restored layout names.
  File operations of the local host finish before they return, and `WhenDone` continues a
  finished task at once, so views stay synchronous on this PC. Native dialogs, Explorer and
  local caches stay on this PC.
- `Workspace`, `WorkspaceChanged` and `BrowserTabs`: the current host's workspace and
  browser tabs, for work that follows the host the user is on, such as a source module's
  command. `WorkspaceChanged` fires when the current host's workspace is replaced or another
  host becomes current. Panels use their own host instead (below).
- `Window` (internal): the shell's Goo window, bound while the shell is mounted, for
  native file dialogs and the clipboard. Built-in modules only; null in headless runs.

It is not thread-safe. All application and workspace mutation happens on the UI
thread. Background work returns through `PanelContext.Post` or `ModuleContext.Post`,
which refuse work once their lifetime is cancelled.

## Modules

A module implements `ILoknModule`: an `Id`, a `Title`, `Register(ModuleBuilder)`, and
optional `Start(ModuleContext)` and `Stop()`. Loading is transactional:

1. `Register` stages panels, commands and settings on a `ModuleBuilder`. Local IDs are
   prefixed with the module ID (`notes` + `note` gives panel type `notes.note`).
2. Every ID and shortcut is checked against what is already registered.
3. Contributions are committed under a cancellation token owned by the module.
4. `Start` runs. If anything throws, the whole module is rolled back.

Unloading cancels the token, closes the module's panels, removes its contributions
and calls `Stop`. A module that others depend on cannot be unloaded or reloaded
while they are loaded.

`ModuleHost` loads every `Lokn.Modules.*.dll` in the `modules` folder beside the
executable and creates each module through its parameterless constructor, in the
order their `Dependencies` require. A module gets what it needs from other modules
through `GetModule<T>()`, never through constructor arguments. Module projects
deploy their DLL into that folder when built; `Modules/Directory.Build.props` and
`Directory.Build.targets` hold the shared settings. A module that sets
`DeployModule` to `false`, like `Welcome`, is built but not shipped. The daemon's
`ModuleHost` (`DaemonRules`) creates only its allowed modules, follows the window's
`modules.json` live without writing it, and holds every reload back until no chat's agent
works ([remote.md](remote.md#roles)).

## Panels and the workspace

- A **panel type** is a factory, `Func<PanelContext, PanelContent>`, registered by a
  module.
- A **panel** is one live `PanelContent` instance. It owns its state for as long as it
  is open and returns a fresh Goo declaration from every `Build()`.
- The **docking tree** (`PanelWorkspace.Root`) is an immutable tree of `DockSplit` and
  `DockPane` records. Each pane holds at most one panel; there are no tabs.
- Moving or swapping a panel changes the tree only. The `PanelContent` instance and
  its mounted Goo cells keep their identity, because the shell keeps all panel views
  as siblings keyed by panel ID.
- Each pane shows a header, drawn by the shell: the panel's title, maximize or restore and close buttons,
  and the drag source for docking (`shell.pane-headers` turns it off). `PanelWorkspace.MaximizedPanelId` names
  the panel that fills the workspace while the others stay mounted and hidden in their panes
  (`ToggleMaximize`; `IsShown` is false for them). It is not part of the tree or of saved layouts, and any
  change to the tree, or focusing another panel, ends it. A panel's header lies over the top of its body
  and the content stays the body's first child, as in a hidden panel's body, so showing or hiding a panel
  never moves its content among its siblings: that would remount its text editors, which Goo allows once.
- A **host-bound** panel type (`builder.Panel( …, onHost: true )`, `PanelTypeInfo.IsHostBound`)
  works on one host, This PC or a paired host. `PanelWorkspace.Open( …, hostId )` binds it to
  the given host, else the current one; `OpenBeside` to the given host, else the host of the
  panel it opens beside. The panel reads it as `PanelContext.HostId`, `Host` and `Workspace`,
  keeps it across module reloads, restarts (`workspace.json`) and saved workspaces, and
  follows `context.Host.WorkspaceChanged`. A panel can report that it works on another host
  now through `PanelContent.HostId`, as a chat panel showing a paired host's chat does;
  `PanelWorkspace.GetHostId`/`GetHost` read either, and `CloseHost` closes every panel of a
  host. Other panel types have no host (`HostId` null, `Host` is This PC). The shell names a
  paired host in a chip at the pane's top-right corner and in the pane menu, and covers the
  panel while its host is offline, or offers **Close panel** once the host was removed.
- **Slots** are window areas that are not panels: `ShellSlots.Sidebar`, `TitleLeft`,
  `TitleRight`, `StatusLeft` and `StatusRight`. Modules fill them with
  `builder.Slot( slot, order, … )`, a `PanelContent` created after the module starts and
  closed when it unloads (`LoknApplication.Slots`). Any number of modules fill one slot,
  sorted by `order`. The shell draws the sidebar sections stacked in a fixed column left
  of the docking area, with its resizable edge and the Work/Personal profile switcher,
  so layouts, sessions and saved workspaces never include or move them. Conversations
  fills the sidebar; without it, Projects lists the projects and Workspaces the current
  session's saved workspaces.

Mutate layout only through `PanelWorkspace` methods so that sessions, the tree and
the active pane stay consistent.

## Reloading modules

Both kinds of module are replaced through `LoknApplication.Reload`, which suspends
the module's panels (capturing `CaptureState()` and leaving their panes empty in
place), releases the old module, loads the new one, and resumes the panels from the
new factories with `RestoredState`. Given several modules it does this as one step,
so a module and the modules that depend on it restart together. If any replacement
fails to load, every previous instance is loaded again.

### Built-in modules

`scripts/reload-module.ps1 -Name <Module>` builds one module project. The build
deploys the DLL, and `ModuleHost` does the rest:

1. A `FileSystemWatcher` on the `modules` folder and a debounce timer notice the new
   file. Its bytes are read into memory, so the file is never locked and the next
   build can replace it.
2. The rebuilt assembly is loaded together with every loaded module assembly that
   references it, each into a fresh collectible `AssemblyLoadContext`. Assemblies of
   one generation resolve each other; everything else (Core, Goo, .NET) comes from
   the application, so those types stay shared. The modules Loom starts with load into
   contexts that are not collectible: the runtime compiles a collectible assembly's
   methods fully optimized on their first call, without tiered compilation, which
   tripled the time startup spent compiling module code
   ([performance](performance.md#startup)). A reload replaces them like any other
   generation, but the startup copy stays in memory.
3. The new module instances are passed to `LoknApplication.Reload` as one group.
4. The outcome is appended to `%TEMP%\Lokn\modules.log` as a `LOADED`, `RELOADED` or
   `FAILED` line, which the script waits for and prints.

Rebuilding Omp or Git therefore also reloads Conversations and Worktrees, and rebuilding Projects
reloads Conversations and Worktrees with it. Reloading Omp, Git, Projects or
Conversations reopens the chat panels, which stops their Omp processes; the
conversations resume from their saved sessions. Only module code reloads this way:
a change to Core, the shell or `Program.cs` needs a restart, and a module built
against a changed Core must not be loaded into an application still running the
old one. A module cannot bring its own NuGet packages.

### Source modules

`HotloadModule` (in `Modules/Hotload/`) is an ordinary module that hosts others:

1. `Start` compiles every folder under the modules directory with Roslyn
   (`ModuleCompiler`), loads each result into its own collectible
   `AssemblyLoadContext`, and calls `app.Reload(module)`. This happens before the
   workspace is restored so saved panels find their types. `ModuleCache` keeps each
   build in `cache\modules\`, keyed by the module's sources and the size and write time
   of every assembly it can reference, so a start with unchanged modules loads their
   builds without loading Roslyn.
2. A `FileSystemWatcher` plus a debounce timer rescans on change. Compilation runs on
   the timer thread; applying the result is posted to the UI thread.
3. Each compiled module is swapped in with `LoknApplication.Reload`.

Roslyn lives only in the `Lokn` assembly. Core's part is the generic `Reload` and
`Remove` API plus `PanelWorkspace.SuspendModule` and `ResumePanels`.

`HotloadModule` also writes editor and agent files into the modules directory: a
`Directory.Build.props`, a `.csproj` per module folder, and an `AGENTS.md` copied
from `Modules/Hotload/ModuleGuide.md`.

## Fault boundaries

Feature code is trusted but contained. Registration, startup, stop, panel factories,
root panel builds, command execution and posted work are wrapped; a failure becomes
`LoknApplication.LastError`, which the shell shows as a dismissible notice. A panel
whose build throws renders its own error surface without affecting siblings.

## Persistence

All under `%LOCALAPPDATA%\Lokn\`:

| File | Owner | Contents |
|---|---|---|
| `settings.json` | `SettingsService` | Global and per-profile values, active profile |
| `workspace.json` | `PanelWorkspace` | Docking tree, active pane, each panel's `CaptureState()` and, for a host-bound panel on a paired host, its `Host` |
| `projects.json` | `ProjectStore` (Projects) | Every profile's projects: name, profile and working directory |
| `conversations.json` | `ConversationStore` | Chats and tasks, each with the project it belongs to |
| `conversations\<chat>.json` | `ConversationStore` | One chat's cached rich messages, read when it first opens |
| `notifications.json` | `NotificationFeed` | The newest 200 notifications, shared by every profile, with read state, the panel each opens and an optional link |
| `workspaces.json` | `WorkspacesModule` | Sessions (Home, Chats, one per project), each with its saved workspaces and current workspace, and the current session |
| `worktrees.json` | `WorktreesModule` | Per-repository worktree setup, recorded native builds and native links |
| `sessions\` | Omp | Per-conversation Omp session files |
| `theme.json` | `ThemeModule` | Theme token overrides layered over the theme picked in Settings; read only, written once by **Theme: edit theme file** with every token commented out |
| `modules\` | `HotloadModule` | Source modules and their generated project files |
| `cache\modules\` | `HotloadModule` | The newest build of each source module folder |
| `version.json` | `DataUpgrade` (Core) | `lastRunVersion`, the version that last ran on the folder, and `upgradedFrom` until Home's What's new card is dismissed |
| `backups\<version>-<yyyyMMdd-HHmmss>\` | `DataUpgrade` (Core) | The data files as they were before a newer version first ran; the three newest are kept ([Data and upgrades](#data-and-upgrades)) |

Writes go to a sibling temporary file and then replace the target. A file that fails
to parse is left untouched and that session does not overwrite it. `ConversationStore`
reads the index when the Conversations module starts and saves on a background thread
about half a second after a change (at most two seconds while changes keep coming),
writing only the index and the chats that changed; unloading the module writes what is
still pending. A `conversations.json` from before this layout, holding every chat's
messages, is read whole once and rewritten as the index plus one file per chat.

`ProjectStore` reads `projects.json` when the Projects module starts and writes it at
once on every change. Projects used to be kept in `conversations.json`: while
`projects.json` does not exist, the Projects module imports that file's `Projects` array
once and writes `projects.json`, and `ConversationStore` keeps the array in its index
until `projects.json` is written, then drops it. A chat whose project no longer exists
leaves it when history loads, unless `projects.json` could not be read.

Outside that folder, the Notifications module (`NotificationIdentity`) keeps a Start menu `Loom.lnk` declaring the
app ID `dev.lokn.desktop` and an `HKCU\Software\Classes\lokn-notification` protocol
that a notification click opens, and the Remote module keeps the `loom` protocol for pairing links. Windows shows
notifications only for an app ID a Start menu shortcut declares. They are rewritten when the running executable
changes, never by a bare instance.

## Bare instances

`Loom.exe --bare <folder>` (a window) and `Loom.exe --host --bare <folder>` (its daemon) run a second, isolated Loom for
demos, checks and `scripts/bench.ps1`. It changes nothing of the user's Loom (`BareInstance`, in the shell):

- `Main` calls `BareInstance.Enter` before anything else, which points `LoknPaths.DataRoot` at the folder. Every file
  above, pasted attachments, Git avatars, Omp's account folders, logs (`host.log`, `modules.log`, `stream.log`) and
  temporary files (`LoknPaths.TempRoot`, `%TEMP%\Lokn` for the user's Loom) are then in the folder, and Goo's Vulkan
  pipeline cache in `cache\vulkan` unless `GOO_VK_PIPELINE_CACHE_DIR` names another.
- It never runs Velopack: no downloaded update applied at start, no installer hook (which would stop the user's daemon
  and remove its sign-in entry), no log in `%LOCALAPPDATA%\velopack`.
- Its app ID is `dev.lokn.desktop.bare.<8 hex digits of the folder's hash>`, so its single-instance election and
  notifications are its own. Its daemon's mutex, stop event and pipe carry the folder's 16-digit hash
  (`HostRunner.ComputeMutexName`, `DaemonControl`, `PipeHost`), so the window starts (`--host --bare <folder>`), links
  with and stops only its own daemon: at a `local`-role start, on a role switch and in the fallback alike.
- It writes no `LoomHost` Run value (`DaemonControl.CanChangeLoginItem`), no `loom` protocol (`PairingProtocol.CanInstall`),
  and no `lokn-notification` protocol or Start menu shortcut (`NotificationIdentity.CanInstall`); it shows no Windows
  notifications.

One file stays outside: Goo's single-instance election keeps a lock file per app ID in `%LOCALAPPDATA%\goo`
(`InstanceScope`), which Goo offers no way to move; `scripts/bench.ps1` deletes the one each launch leaves.
`IsolationChecks` (suite `default`) compares a bare instance's names, files and daemon command with the user's, and runs
`Loom.exe --host --bare` to its test exit to see that it moves into its folder first and runs no Velopack.

## Data and upgrades

Loom and its daemon call `DataUpgrade.Prepare` (Core) first thing at start, before any module loads or any
migration such as `MoveSaved` writes: `Program.RunDesktop` for the window (data folder: `%LOCALAPPDATA%\Lokn`, or the
bare folder) and `HostRunner.Start` for the daemon. It runs under the mutex `Local\Lokn.upgrade.<hash of the folder>`,
so when the desktop and the daemon start together only the first backs up and the second finds `version.json` current.

- **Backup.** When `version.json`'s `lastRunVersion` is older than the starting version, or the folder has data but no
  `version.json` (data from 0.2.0 or earlier, named `earlier`), it copies `conversations.json`, the `conversations\`
  message files, `projects.json`, `workspaces.json`, `workspace.json`, `settings.json`, `modules.json` and
  `browser-tabs.json` (those that exist) into `backups\<version>-<yyyyMMdd-HHmmss>\` through a `.partial` folder, then
  deletes all but the three newest backups. A fresh folder gets no backup. A backup that fails is reported (the window's
  error notice, the daemon's `host.log` as `FAILED backup`) and Loom starts normally.
- **`version.json`.** `{ "lastRunVersion": "0.3.0", "upgradedFrom": "earlier" }`, written after the backup, also by an older
  version (without a backup). `upgradedFrom` stays until Home's What's new card is dismissed
  (`ConversationsModule.WhatsNew`); the card shows while it is older than 0.3.0, so a fresh install never shows it.
- **Restore.** Settings › Shell › Data › Backups lists the backups (`DataBackups`). **Restore…** asks, writes
  `backups\restore.json`, closes the window normally and starts Loom again (`ShellRestart`, after `Main` let go of the
  single-instance lease). The next desktop start stops the daemon of its folder, then `Prepare` copies the backup back:
  each data file the backup holds replaces the current one, each it lacks is deleted, and `conversations\` is replaced
  whole. Only the desktop restores (`canRestore`); a daemon that starts first leaves the request alone.

Opt-in defaults: a fresh or upgraded install starts with `remote.role` `local` (no daemon), `remote.network` `off`, and
`remote.login-item` off; the daemon's icon only exists while a daemon runs, which only the `daemon` role starts. LAN,
Tailscale, Relay and Funnel and paired hosts are marked Preview in Remote access.

**Upgrade check** (`UpgradeChecks`, suites `updates` and `default`). `tests/Lokn.Tests/Fixtures/data-0.2.0/` is a data
folder written by the real 0.2.0 build (`main`, through a driver in its test project: two projects, three chats with
messages and a task, settings in Global, Work and Personal, six Workspaces sessions with saved workspaces, and a layout with
chat, Git, browser, terminal and file panels; paths under `C:\LoomFixture`). The check backs a copy up, opens it with every
module as Loom does (`LoknPaths.UseDataRoot`), and checks that every chat, message, project, workspace and setting is kept,
the layout restores with the 0.2.0 browser panel's pages as session tabs, `version.json` is written, the What's new card
shows once and nothing starts a daemon.

**Downgrade check.** The same check then opens the folder 0.3.0 saved with the 0.2.0 build. `pwsh scripts/loader-020.ps1`
builds it once: `git archive main` into `%TEMP%\loom-020`, with the driver `tests/Downgrade-0.2.0/FixtureDriver.cs`
(`--load-data <folder>`, and `--make-fixture <folder>`, which wrote the fixture) added to its test project; or point
`LOKN_LOADER_020` at a loader's `Lokn.Tests.dll`. Without one the check prints `SKIP`. Result with 0.3.0: `LOAD OK`, nothing thrown; 2 projects, 3 chats with 4, 4 and 2 messages
and the task, all 6 sessions with their saved workspaces (the Review workspace with its browser and terminal panels),
and the settings. 0.3.0 needed no change for it: 0.2.0 ignores the files it does not know (`version.json`, `backups\`,
`browser-tabs.json`, `chats.lock`, `remote-hosts.json`) and the `remote.*` settings, and a browser panel's saved state
keeps 0.2.0's `Address` and `Tabs` beside its session tab `Ids`. What does not carry over: chats that ran in the daemon
are in the same files, but 0.2.0 has no daemon, so its `LoomHost` sign-in entry, if on, starts a 0.2.0 window at
sign-in until **Start the daemon at sign-in** is turned off first; a panel on a paired host opens on This PC.

## Installer

`scripts/installer.ps1` publishes Loom and packs it with [Velopack](https://velopack.io)'s `vpk`
(a local tool in `dotnet-tools.json`, at the same version as the `Velopack` package in
`Lokn.csproj`). `Loom-win-Setup.exe` asks nothing and needs no administrator: it shows
`assets/installer/splash.gif` with a progress bar, installs to `%LOCALAPPDATA%\Loom`, adds a
desktop shortcut with the app ID and starts Loom, whose Notifications module then writes the
Start menu shortcut and the protocol above. The splash is the Home mark, drawn by
`assets/installer/splash.py` from `HomeLogo`'s geometry; run it again after changing the mark or
the theme colors.

Velopack starts `Loom.exe` with a hook argument when it installs, updates or uninstalls, so
`Program.Main` answers that first, before any window or single-instance election. On uninstall,
`Installation.RemoveRegistration` deletes the protocol when it opens this install, and Velopack
deletes the install folder and every shortcut into it. `%LOCALAPPDATA%\Lokn` is kept. The
package version is `<Version>` in `Lokn.csproj`, and each version can be packed once into
`artifacts\installer`, which keeps earlier releases for update deltas. The version's
`## <Version>` section of `CHANGELOG.md` is packed as its release notes; without one, packing
stops. Nothing is signed yet, so SmartScreen warns before the first run.

## Updates

An installed Loom updates itself and its modules separately, from one feed. Loom run from a
build takes no updates.

```text
feed (Update source setting; default: the GitHub repository)
├─ releases v0.2.0, v0.3.0 …            app: Velopack packages, read by AppUpdater
└─ release "modules" (pre-release)      modules: modules.json + Lokn.Modules.<Name>-<version>.dll

%LOCALAPPDATA%\Loom\current\modules\         bundled module DLLs; an app update replaces them
%LOCALAPPDATA%\Loom\module-updates\          applied module updates
%LOCALAPPDATA%\Loom\module-updates\staged\   downloaded, waiting for Apply now or the next start
```

`UpdatesModule` (`Shell/Updates/`) checks 30 seconds after startup and then every 6 hours while
**Check for updates automatically** (`updates.automatic`) is on; **Updates: check for updates**
checks now and always posts what it found. **Update source** (`updates.source`) takes a GitHub
repository, a web address or a folder (`UpdateFeed`); blank turns updates off.

The **Check for updates** button on Settings › Updates runs the same check. It shows the result inline, in the same
words as the notice, and offers **Restart to update** or **Apply module updates** when an update is ready. A
ready update also posts the notice with its chime, so it can start from the Notifications menu. A background
check posts only when the ready set changes; a command always posts.

- **App.** `AppUpdater` downloads a new release with Velopack and posts **Restart now**, which
  closes the window normally, so state is saved, and lets Velopack replace the files and start
  Loom again with the same arguments. An update never cuts an agent's turn off unless the user says
  so: **Restart now** asks `UpdateGate` first, which counts the agents working on This PC through `IAgentActivity`
  (Core; the Conversations module publishes This PC's store's working count, which in the `daemon` role mirrors the
  daemon's). With none it restarts; otherwise it posts **Update when agents finish**, which restarts as soon as none
  works (checked every 2 s), and **Update now (stops N agents)**. Velopack's before-update hook
  (`HostRunner.StopForUpdate`) records in `daemon-update.flag` that the daemon ran and stops it gracefully; the
  after-update hook (`HostRunner.StartAfterUpdate`) starts it again from the new build when it ran or its sign-in entry
  is on. Otherwise Velopack applies a downloaded release at the next start, but not while the daemon runs, since its
  agents may work: then the release waits for **Restart now**. While a new Loom waits, module
  updates are not fetched, because it brings its own modules.
- **Modules.** Each built-in module has its own `<Version>` in its project, and publishing passes
  the app version to the app only (`GlobalPropertiesToRemove` in `Lokn.csproj`). `ModuleUpdater`
  reads `modules.json`, downloads per loaded module the newest version above the running one,
  checks its SHA-256, asks `ModuleHost.Check` whether it fits, and stages it. A version that does
  not fit is skipped for the next older one, so an older Loom still gets the newest module that
  works with it. **Apply now** moves staged files into `module-updates`, which reloads those
  modules (for Omp, Conversations, Projects or Git that stops running agent turns); otherwise
  `ModuleHost.Start` applies them at the next start. Each loaded update is logged in `modules.log`
  as `USING <assembly> <version> (update)`.

Compatibility is checked, not declared. `ModuleHost` loads the newest DLL of each module and runs
`ModuleCompatibility.FindProblem`, which loads every type of the module and resolves every type and
member it references against the assemblies actually running: Core, Goo, .NET and the other
modules. A module update built against API this Loom lacks is refused before any of its code runs:
it is logged as `SKIPPED` in `modules.log` and deleted, a refusal while Loom runs is reported in the
notification feed, and the next older version loads, down to the bundled one. A bundled module that stops fitting because of
an update it uses refuses that update instead. For this to work, module references to the
application's assemblies bind by name, not version (`ModuleLoadContext`), and Core's assembly
version stays 1.0.0. The check cannot see behavior changes behind an unchanged signature; change
the signature, or the member's name, when its meaning changes.

`scripts/release.ps1` publishes both: the installer and update packages as release `v<Version>`
(with a delta from the previous release), then every module whose version the published
`modules.json` lacks. `-ModulesOnly` skips the app, `-DryRun` uploads nothing. A module whose code
changed without a new `<Version>` ships only with the next app release; the script warns about it.
Velopack reads stable releases only, so it ignores the `modules` pre-release.

## Where a change goes

| You want to | Put it in |
|---|---|
| Add a feature | A new module: a source module folder, or `Modules/<Name>/` with its own `Lokn.Modules.<Name>.csproj`, referenced from `Lokn.csproj` and the test project |
| Add a panel, command or setting to a feature | That module's `Register` |
| Change docking rules or panel ownership | `Core/PanelWorkspace*.cs`, with a check in `CoreChecks` or `LayoutChecks` |
| Change how panes, grips or the Search palette are laid out | `Shell/` |
| List a feature's items in the Search palette | That module's `Register`, with `builder.SearchProvider` |
| Change colors, fonts or spacing | `theme.json`; a new token goes in `Core/Theme.cs` |
| Change the module lifecycle | `Core/LoknApplication.cs` |
| Change how source modules compile or reload | `Modules/Hotload/` |

Do not pass feature details through Core, reach into another module's state, or add
a shell dependency to Core.

## Verifying

The checks are an executable; flags select suites.

```powershell
dotnet build -c Release
dotnet run --project tests/Lokn.Tests -c Release
dotnet run --project tests/Lokn.Tests -c Release -- --features-only
dotnet run --project tests/Lokn.Tests -c Release -- --hotload-only
```

Add `--ui` for the suites that open a real Vulkan window. A running Lokn locks its
output directory; if the build cannot copy files, build and run the checks with
`-c Debug` or whichever configuration the running instance is not using.
