# Architecture

Loom (code name Lokn) is a single-process Windows desktop shell: a docking workspace whose
features are modules. The executable is `Loom.exe`; namespaces, projects and data folders
keep the name Lokn. This page is the map. [modules.md](modules.md) is the module contract
in detail, and [omp-backend.md](omp-backend.md) covers the Omp transport.

## Layers

Dependencies point one way: features and the shell depend on Core, never the reverse.

| Layer | Assembly | Holds | May reference |
|---|---|---|---|
| `Core/` | `Lokn.Core` | Module contracts, `LoknApplication`, command registry, search registry, panel workspace and docking tree, settings, the notification feed, `Theme` | Goo only |
| `Shell/` | `Lokn` | Goo views: window chrome with its titlebar, sidebar and status bar slots, the sidebar column and profile switcher, dock rendering, the Search palette, the settings editor (a Shell tab, the Modules tab, then one tab per module), `ShellModule` (id `shell`: window settings and commands, the `shell_*` agent tools, and the Search palette's Panels, Actions and Settings), `ModuleStates` (which modules are on, in `modules.json`), `ThemeModule` in `Theming/` (the theme file, the Tern catalog and the picker; it cannot be disabled), `ModuleHost` with `ModuleCompatibility` (loading module DLLs and module updates that fit), `UpdatesModule` in `Updates/` (id `updates`: app and module updates; see [Updates](#updates)), and `AttentionNotifier` (acknowledging requests the user looked at, and opening clicked Windows notifications) | Core, Goo |
| `Modules/<Name>/` | one `Lokn.Modules.<Name>` DLL each | Features: `Omp` (also publishes `IAgentService`), `Git` (the one git runner, `GitProcess`, each watched folder's live repository, branch and changes, the Git panel with its graph and file diff modes and its `git.actions` and `git.messages` points, and the Files side view's Changes and History modes), `Projects` (a named working folder per profile with a session of its own, its icon and the trusted modules it carries, and `IWorkingFolder`, with the `projects.folders` point other sessions such as tasks give their folder through; it lists them in the sidebar or tab strip while Conversations is not loaded), `Conversations` (with the chat panel in `Chat/`, Home: the empty state of a conversation panel, opening each project's session through Projects, and the Git views' review in chat and commit message writer; Git is optional), `Markdown` (`IMarkdownRenderer` and the chat's Markdown view), `Files` (the side view at the left of the workspace: the file tree and the modes other modules add through `files.modes`), `Editor` (a text editor with history, highlighting and gutter marks, `lokn://editor/open`), `Notifications` (the feed page, the bell, its popover and banners, the chime and Windows notifications; it depends on no module, and modules describe their notifications with `NotificationKind`), `GitHub`, `Worktrees`, `Terminal`, `Workspaces` (saved workspaces, one set per session; always on), `Browser`, and the example `Welcome` | Core, Goo, and another module only through a project reference plus a declared dependency |
| `Modules/Hotload/` | `Lokn` | The runtime compiler for source modules; part of the host because it carries Roslyn | Core, Goo |
| `Program.cs` | `Lokn` | Configures the Goo app identity and single instance, starts the module host, loads the shell's own modules, restores the workspace, then raises `LoknApplication.Started`, where modules open their own startup view (`ModuleContext.WhenStarted`); with none the shell shows its empty pane | Core and shell only, never a feature module, not even by ID |
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
  another's services, such as `OmpModule.Service` or `ConversationsModule.Store`.
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
`DeployModule` to `false`, like `Welcome`, is built but not shipped.

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
   the application, so those types stay shared.
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
| `workspace.json` | `PanelWorkspace` | Docking tree, active pane, each panel's `CaptureState()` |
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

Outside that folder, `AttentionNotifier` keeps a Start menu `Lokn.lnk` declaring the
app ID `dev.lokn.desktop` and an `HKCU\Software\Classes\lokn-notification` protocol
that a notification click opens. Windows shows notifications only for an app ID a
Start menu shortcut declares. Both are rewritten when the running executable changes.

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
`artifacts\installer`, which keeps earlier releases for update deltas. Nothing is signed yet,
so SmartScreen warns before the first run.

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
checks now and always says what it found. **Update source** (`updates.source`) takes a GitHub
repository, a web address or a folder (`UpdateFeed`); blank turns updates off.

- **App.** `AppUpdater` downloads a new release with Velopack and posts **Restart now**, which
  closes the window normally, so state is saved, and lets Velopack replace the files and start
  Loom again with the same arguments, such as `--data`. Otherwise Velopack applies it at the next start. While a new Loom waits, module
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
