# Loom (code name Lokn)

A C# 14 / .NET 10 desktop shell built on Goo: a docking workspace whose features are
modules, used as a native chat client for the Omp agent backend.

**Loom** is the name users see: window title, `Loom.exe`, icon, Start menu shortcut, Home
wordmark. **Lokn** stays in code: namespaces, project names, the `%LOCALAPPDATA%\Lokn` and
`%TEMP%\Lokn` folders, and the app ID. Brand assets are in `assets/brand/`.

Read these before changing anything:

- [docs/architecture.md](docs/architecture.md): layers, lifecycle, hot reload, where a change goes.
- [docs/modules.md](docs/modules.md): the module, panel, command and settings contract.
- [docs/omp-backend.md](docs/omp-backend.md): only when touching `Modules/Omp` or `Modules/Conversations/Chat`.

## Rules that are easy to break

- **Core never references the shell or a feature.** `Core/` depends on Goo only.
- **New features are modules.** Prefer a source module (see below). A built-in module
  is its own project, `Modules/<Name>/Lokn.Modules.<Name>.csproj`, with a
  parameterless constructor; the application finds its DLL and loads it.
- **The application never references a feature module.** `Program.cs` and the shell
  use string IDs only. A module the executable referenced could not be reloaded.
- **Modules do not touch each other's state.** When one needs another, add a project
  reference, declare the module ID in `Dependencies`, and reach it with
  `context.App.GetModule<T>()`.
- **Everything mutates on the UI thread.** Return from background work with
  `PanelContext.Post` or `ModuleContext.Post`.
- **Panel state lives on the `PanelContent` instance**, not on its pane or position.
  `Build()` returns a fresh declaration every time.
- **Change layout only through `PanelWorkspace` methods.** The docking tree is
  immutable records.
- **Warnings are errors**, nullable is enabled, and every member has a `<summary>`.
  Formatting follows `.editorconfig`: tabs, CRLF, Allman braces, spaces inside call
  parentheses.
- **A module ships as an update only with a new `<Version>`** in its own project. An
  installed Loom refuses a module update that uses Core or Goo API it lacks, so a module
  that needs new Core API reaches users with the next app release
  ([architecture](docs/architecture.md#updates)).

## Source modules

Features can be written and changed while Lokn runs. Each folder under
`%LOCALAPPDATA%\Lokn\modules` is one module, compiled by Lokn and reloaded on save;
the **Module folders** setting adds folders anywhere else, such as a user's own repository.
The per-user folder has its own generated `AGENTS.md` with the authoring rules and a minimal
module; its source is [Modules/Hotload/ModuleGuide.md](Modules/Hotload/ModuleGuide.md).
Edit the source file, not the generated copy.

Source modules can use only the public API of `Lokn.Core` and Goo. If a feature needs
shell internals such as `ShellView`, it has to be a built-in module. Colors, fonts and
spacing come from the public `Theme.Current`; never hard-code them in a view.

Modules for other people go to the community hub, [antopilo/loom-modules](https://github.com/antopilo/loom-modules),
as source in `modules/<id>/` by pull request (see [docs/modules.md](docs/modules.md#community-modules));
users install them from Settings › Modules › Community. Now Playing lives there, not in this repository.

## Scripts

Use the scripts in `scripts/` instead of raw `dotnet` commands. Each prints a final
status line and exits non-zero on failure. They pick Debug automatically when a
running Lokn has the Release output locked.

| Task | Command |
|---|---|
| Build everything | `pwsh scripts/build.ps1` |
| Publish a self-contained build for other PCs (`artifacts\publish\win-x64`) | `pwsh scripts/publish.ps1` |
| Build the Windows installer (`artifacts\installer\Loom-win-Setup.exe`; raise `<Version>` in `Lokn.csproj` and add its `CHANGELOG.md` section first) | `pwsh scripts/installer.ps1` |
| See what a release would publish, without uploading | `pwsh scripts/release.ps1 -DryRun` |
| Publish the app and new module versions to GitHub (`-ModulesOnly` for modules alone) | `pwsh scripts/release.ps1` |
| Run the default checks | `pwsh scripts/check.ps1` |
| Run chosen suites | `pwsh scripts/check.ps1 -Suite worktrees,hotload` |
| Start Lokn when it is not running | `pwsh scripts/run.ps1` |
| Close, rebuild and restart Lokn | `pwsh scripts/run.ps1 -Restart` |
| Build one built-in module and reload it in the running Lokn | `pwsh scripts/reload-module.ps1 -Name Worktrees` |
| Create a hot-reloaded source module | `pwsh scripts/new-module.ps1 -Name Notes` |
| Compile-check a source module | `pwsh scripts/check-module.ps1 -Name Notes` |
| Compile the Goo shader effects after changing a `.slang` file (needs the Slang release Goo pins, through `SLANG_SDK`) | `pwsh scripts/shaders.ps1` |
| Compile-check a project's `.lokn\modules` (all, or one with `-Name`) | `pwsh scripts/check-module.ps1 -Project F:\path\to\repo` |
| Build the 0.2.0 data loader the upgrade checks' downgrade step runs (`%TEMP%\loom-020`) | `pwsh scripts/loader-020.ps1` |
| Measure start time, idle CPU, frames per second and memory in bare instances (opens windows; never runs a build without bare isolation, and stops with `ISOLATION BREACH`, restoring what it can, if a launch changed the user's daemon, protocols, Run values or Start menu shortcut) | `pwsh scripts/bench.ps1 -Scenario home -Seconds 10` |

Headless suites: `default`, `features`, `all`, `layout`, `conversations`, `omp`,
`chat`, `worktrees`, `terminal`, `git`, `remote`, `hotload`, `updates`, `settings`. Report which suites you actually ran.

The daemon, its clients and the protocol are in [docs/remote.md](docs/remote.md). `Loom.exe --host --bare <folder>`
runs an isolated daemon for checks; never start a daemon on the user's own data without asking. The daemon follows
Settings › Modules (`modules.json`) and reloads rebuilt modules itself, but only once none of its chats' agents works.

Three things need the user's go-ahead first, because they take over their desktop,
their running app, or their users' copies of Loom:

- **Window checks.** `-Ui` and the `chat-view`, `terminal-view`, `notifications-view`, `omp-setup` and `startup` suites open
  a real window for a few seconds. Run them only when a change affects rendering or
  input.
- **Restarting.** `run.ps1 -Restart` closes the user's running Lokn. It asks the
  window to close normally so state is saved, and never kills it. The restart runs in
  a detached process, because an agent inside a Lokn chat is stopped when Lokn
  closes; the outcome is written to `%TEMP%\Lokn\restart.log`.
- **Releasing.** `release.ps1` without `-DryRun`, a pushed `v*` tag, or running the
  Release workflow publishes to GitHub, and installed copies of Loom download what it
  publishes. CI (`.github/workflows/ci.yml`) runs the `default` and `features` suites on
  every push to `main` and every pull request.

What a change needs before it is live:

- **Source module:** nothing. Saving its file reloads it.
- **Built-in module** (`Modules/Omp`, `Git` (with the Git panel and the side view's Changes and History modes), `Projects`, `Conversations` (with `Chat` and Home), `Files`, `Editor`, `Notifications`, `Worktrees`, `Terminal`, `Browser`, `Design` (HTML design studio and `design_*` tools), `Overview` (a project's overview page), `Workspaces`, `Setup` (Get started), `Remote` (daemon gateway, client, Remote access, Window stream)):
  `reload-module.ps1 -Name <Module>`. It prints `RELOADED`, `FAILED` or `NO CHANGE`;
  the same lines are kept in `%TEMP%\Lokn\modules.log`. Modules that depend on the
  rebuilt one reload with it. A daemon started from the same build reloads the module
  too and the script prints its outcome from its `host.log` (`%TEMP%\Lokn\host.log`, or
  in its bare folder): `RELOADED`, `FAILED`, or `DEFERRED` while a chat works there, in
  which case the daemon reloads once no chat works and logs the result then.
- **Core, the shell (with its theme in `Shell/Theming`), `Program.cs`, `Modules/Hotload`:** a restart.

Reloading Omp, Git, Projects or Conversations (which includes Chat) reopens the chat panels and
stops their running agent turns; reloading Projects reloads Conversations with it. If you are an
agent working inside a Lokn chat, that includes you: the reload still completes, so read the log
afterwards. Get the user's go-ahead before reloading any of those four.
