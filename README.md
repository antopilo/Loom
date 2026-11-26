<p align="center">
	<img src="assets/brand/loom.svg" width="112" alt="Loom">
</p>

<h1 align="center">Loom</h1>

<p align="center">
	A native Windows and Linux workspace for AI coding agents.<br>
	Chats, terminals, Git, a browser and your own tools, docked side by side.
</p>

---

Loom is a desktop client for the [Omp](https://github.com/can1357/oh-my-pi) agent backend,
built in C# 14 on .NET 10 with the [Goo](https://github.com/obselate/goo) UI framework.
Every feature is a module, and modules reload while Loom runs, so you can change the app
from inside the app.

## Features

- **Agent chats.** Streaming replies, tool calls folded into one summary line, diff cards,
  todo progress, native dialogs, model picker and routing, usage limits, queue and steer.
- **Projects and tasks.** Each project and task keeps its own panels and layout. Agents keep
  working in the background while you are somewhere else.
- **Docking workspace.** Split, drag, dock and swap panels. Save layouts and switch between
  them with an animation.
- **Built-in tools.** Terminal (ConPTY), Git graph and diffs with hunk staging and review,
  worktrees, file tree, editor, WebView2 browser, GitHub pull requests and checks.
- **Agents can use the app.** Agents open panels, enter worktrees, drive the browser and show
  HTML reports through Loom's tools.
- **Notifications.** A feed, banners and Windows notifications when a chat finishes or asks
  a question. You can answer from the banner.
- **Work and Personal profiles.** Separate settings, history and Omp accounts.
- **Themes.** Loom's own theme and the whole Tern theme catalog, with a live preview.
- **Extensible.** Write a module as a folder of `.cs` files. Loom compiles it and reloads it
  every time you save. Install modules other people wrote from the
  [community hub](https://github.com/antopilo/loom-modules) in Settings › Modules, or submit
  your own with a pull request.

See [Using Loom](docs/features.md) for the full guide.

## Install

Download `Loom-win-Setup.exe` from the [releases](https://github.com/antopilo/Loom/releases)
and run it. It installs for the current user, needs no administrator rights and no .NET, and
you can remove it from **Settings › Apps**. Your data in `%LOCALAPPDATA%\Lokn` stays when you
uninstall. Loom updates itself and its modules in the background and asks before it applies
an update. On Linux there is no installer yet: build Loom from source. Its data is in
`~/.local/share/Lokn`, and the browser and Windows notifications are not available there.

**Requirements:** Windows 11 x64 or Linux x64, a GPU with Vulkan 1.3 and current drivers, and
[Omp](https://github.com/can1357/oh-my-pi) for chats (Loom's **Get started** panel helps you
set it up). Git is optional and enables the Git, worktree and change features.

## Build from source

You need the .NET SDK 10.0.401 or a later 10.0.4xx patch, and PowerShell 7.

```powershell
git clone https://github.com/antopilo/Loom.git
cd Loom
pwsh scripts/run.ps1          # build and start Loom
pwsh scripts/check.ps1        # build and run the default checks
pwsh scripts/installer.ps1    # build artifacts\installer\Loom-win-Setup.exe
```

On Linux, `dotnet build Lokn.csproj -c Release` builds Loom and its modules, and
`bin/Release/net10.0/Loom` starts it.

Each script prints a final status line and exits non-zero when it fails. Run
`Get-Help scripts/<name>.ps1` for the options of a script.

|Script|Does|
|---|---|
|`build.ps1`|Builds the app, every module and the checks|
|`run.ps1`|Starts Loom; `-Restart` closes, rebuilds and restarts it|
|`check.ps1`|Runs check suites: `-Suite worktrees,hotload`, `-Ui` for window checks|
|`reload-module.ps1`|Rebuilds one built-in module and swaps it into the running Loom|
|`new-module.ps1`, `check-module.ps1`|Creates and compile-checks a source module|
|`publish.ps1`|Self-contained build in `artifacts\publish\win-x64`|
|`installer.ps1`|Windows installer in `artifacts\installer`|
|`release.ps1`|Publishes the app and new module versions to GitHub; `-DryRun` only shows what it would publish|

GitHub Actions runs the build and the headless checks on every push and pull request
(`.github/workflows/ci.yml`). Pushing a `v<Version>` tag, or running the **Release** workflow,
publishes a release with `release.ps1` (`.github/workflows/release.yml`).

## How it is built

```text
Loom.exe ── Program.cs, Shell/          window, docking, palette, settings, module host
   │
   ├── Lokn.Core                        public contracts: modules, panels, commands, settings
   │
   ├── modules\Lokn.Modules.*.dll       built-in features, loaded and reloaded at run time
   │      Omp · Conversations · Projects · Workspaces · Git · GitHub · Worktrees
   │      Terminal · Files · Editor · Markdown · Browser · Notifications · Setup …
   │
   └── %LOCALAPPDATA%\Lokn\modules\     source modules, compiled by Loom on save
```

Core depends on Goo only. The app never references a feature module, and modules never
touch each other's state, so each one can be turned off or reloaded on its own.

|Folder|Contents|
|---|---|
|`Core/`|`Lokn.Core`: the public API every module uses|
|`Shell/`|Window, docking, palette, settings, theme and module host|
|`Modules/`|One project per built-in feature; `Welcome` is the minimal example|
|`tests/Lokn.Tests/`|Check suites, a plain executable with no test framework|
|`scripts/`|Build, run, check, publish and installer scripts|
|`assets/`|Brand and installer artwork|
|`docs/`|Guides and design notes|

## Documentation

- [Using Loom](docs/features.md): every feature, from Home to the Git panel
- [Architecture](docs/architecture.md): layers, lifecycle, hot reload, persistence, installer
- [Writing modules](docs/modules.md): the module, panel, command and settings contract
- [Omp backend](docs/omp-backend.md): how chats talk to Omp, and the tools agents get
- [AGENTS.md](AGENTS.md): rules for coding agents that work on this repository

## Names

**Loom** is the product: window title, `Loom.exe`, icon, installer. **Lokn** is the code
name, kept in namespaces, project names, the `%LOCALAPPDATA%\Lokn` data folder and the app ID.
