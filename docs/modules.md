# Adding a module

Add a feature as its own project, `Modules/Notes/Lokn.Modules.Notes.csproj`,
containing only `<Project Sdk="Microsoft.NET.Sdk" />`; `Modules/Directory.Build.props`
supplies the settings and the Core reference. Reference the project from
`Lokn.csproj` with `ReferenceOutputAssembly="false"` so it builds with the
application, and from the test project if checks use it. The application loads the
DLL at startup and `scripts/reload-module.ps1 -Name Notes` rebuilds and swaps it
while Lokn runs. A daemon (`Loom.exe --host`, [remote.md](remote.md#roles)) started from the
same build swaps it too, but only once none of its chats' agents works; the script reports both.
The module class needs a parameterless constructor. Features use
Goo and `Lokn.Core`; they do not need shell implementation types.

A minimal feature looks like this:

```csharp
using Goo;
using Lokn.Core;

public sealed class NotesModule : ILoknModule
{
	public string Id => "notes";
	public string Title => "Notes";

	public void Register( ModuleBuilder builder )
	{
		builder.Panel( "note", "Note", context => new NoteContent( context ) );
	}
}

internal sealed class NoteContent( PanelContext context ) : PanelContent
{
	private readonly State<string> _text = new( "" );

	public override Blob Build()
	{
		return new TextEntry
		{
			Value = _text.Value,
			Controlled = true,
			Placeholder = "Write a note…",
			OnChange = value => context.Run( () => _text.Value = value ),
		};
	}
}
```

`Panel("note", …)` registers panel type `notes.note` and automatically contributes
command `notes.open.note`, labeled **Open Note**. Every opening creates a new panel
instance. `context.App.Panels.Open("notes.note")` opens it in the active pane.
Use `Open("notes.note", input: document)` to pass feature-defined input into
`PanelContext.Input`; the factory can match it to a feature-owned record type.

## Versions and updates

A built-in module has its own release version, `<Version>` in its project. Raise it when the
module should reach installed copies of Loom on its own; `scripts/release.ps1` then publishes it
as a module update. An installed Loom loads an update only when every type and member it uses
exists in that Loom (see [architecture](architecture.md#updates)), so a module that needs new
Core API reaches users with the app release that brings that API. Source modules take no updates
from that feed: Lokn compiles them against the running app, so they always fit it.

## Community modules

Anyone can publish a source module through the community hub,
[antopilo/loom-modules](https://github.com/antopilo/loom-modules): one folder per module,
`modules/<id>/`, with `module.json` (`id`, `name`, `author`, `description`, `version`) and its `.cs`
files, added or changed by pull request. The hub's CI checks the files, compiles the module against
Loom's Core, checks that it defines exactly one module whose `Id` is the folder name, and requires a
higher `version` for every change; maintainers review the code before they merge. On each merge the
hub publishes `index.json`, which lists every module with the SHA-256 of each file at that commit.

**Settings › Modules › Community** lists the hub's modules. **Install** downloads a module's files
at the index's commit, checks each hash, and writes them to `community\<id>` in the data folder;
`CommunityModule` (`Shell/Community/`) contributes that folder to `app.ModuleFolders`, so Hotload
compiles and loads it like any source module, and a module that does not compile against this Loom
does not load. **Remove** deletes the folder. With **Update community modules automatically**
(`community.automatic`), installed modules update to a higher hub version 20 seconds after startup
and every 6 hours; **Community modules: check for updates** does it now. **Community module source**
(`community.source`) takes another GitHub repository with the same layout, or a folder holding
`index.json` and `modules/`.

## Source modules and hot reload

The desktop loads `HotloadModule`, which compiles modules from source while Lokn
runs. Each directory below `%LOCALAPPDATA%\Lokn\modules` is one module: all of its
`.cs` files compile into one in-memory assembly that must contain exactly one
`ILoknModule` with a parameterless constructor. Run **Hotload: open modules folder**
from the palette to reveal the directory.

```text
%LOCALAPPDATA%\Lokn\modules\
	Notes\
		NotesModule.cs
		NoteContent.cs
```

Modules can also live outside that directory, such as in a repository of your own.
The **Module folders** setting (`hotload.folders`) lists extra folders separated by
semicolons, with `%VARIABLES%` expanded. A listed folder holding `.cs` files is one
module; any other listed folder holds one module per subfolder. Editing the setting
loads and removes their modules at once, a folder that does not exist is reported,
and their files reload on save like any other source module. A module folder outside
`%LOCALAPPDATA%\Lokn\modules` that has no project gets one importing the shared
`Directory.Build.props` by path.

A project can carry modules of its own in `<project directory>\.lokn\modules`, one
module per subfolder, so a repository ships the panels its work needs. Their code runs
with the user's rights, so Home (or, without Conversations, the Projects list in the
sidebar) names a project's modules and asks before loading them; **Load** adds the
directory to the **Trusted project modules** setting (`projects.module-projects`) and
loads them at once, and **Not now** asks again at the next start. At startup, Lokn loads
the modules of every trusted project in the active profile. The Projects module
contributes those folders through `LoknApplication.ModuleFolders`, which Hotload reads
beside `hotload.folders`. The setting was `conversations.module-projects` while
Conversations owned projects; the Projects module moves a value saved under that
identifier to the new one, in every scope, when it starts.

Saving, adding, renaming, or deleting a file recompiles that directory and reloads
its module without restarting Lokn. Adding a directory loads a new module; deleting
one removes its module. Sources are C# 14 with nullable enabled and the same
implicit usings as the application. They can reference Goo, `Lokn.Core`, and the
.NET libraries; shell types such as `ShellView` are internal and unavailable. Style
panels with `Theme.Current`, read inside `Build()`, so they follow the user's theme.

For editor IntelliSense, Lokn writes a `Directory.Build.props` into the modules
directory at startup and a minimal `.csproj` into each module directory that has
none. Open a module's project, or the whole directory, in your IDE. The shared file
references the running application's `Goo.dll` and `Lokn.Core.dll` and is
rewritten when those paths change; an existing `.csproj` is never overwritten.
These files only serve the editor: Lokn still compiles the `.cs` files itself, and
editor build output goes to the temp directory.

A reload keeps the module's panels in their panes. Each panel closes and reopens
from the new factory, receiving whatever `CaptureState()` returned as
`PanelContext.RestoredState`; state that is not captured is reset. `Start` runs
again on every reload, so avoid opening panels there. A panel type the new source
no longer registers closes its panes. `App.Panels.IsSuspending` is true while a
reload captures and closes panels, so `CaptureState()` and `Closed()` can tell a
reload, which reopens the panel at once, from closing or shutdown; chat panels use it
to continue a turn the reload stopped.

A compile error appears in the shell notice with its file and line while the
previous implementation keeps running. A module that compiles but fails
registration or startup also falls back to the previous implementation. A source
module cannot reuse the ID of another module, and a module that others depend on
cannot be reloaded while they are loaded.

Hosts can use the same mechanism directly: `app.Reload(module)` replaces a module
by ID, and `app.Remove(id)` unloads and forgets one. Omit `HotloadModule` from
`Program.Main` to run without the compiler. The first compile adds roughly a second
to startup when at least one source module exists.

## Registration and ownership

`Register` only declares contributions through `ModuleBuilder`. Local identifiers
are prefixed by the owning module ID. All identifiers and keyboard shortcuts are
checked before any registrations are committed. Duplicate IDs, ambiguous shortcuts,
and missing dependencies reject the module without replacing existing features.
The Search palette owns Ctrl+K and Ctrl+Shift+P; modules cannot shadow these shortcuts.
Global shortcuts and Ctrl+K run before the focused control sees the key, unless an
open panel's `PanelContent.ClaimsKey(key, modifiers)` returns true. A panel claims
keys only while its own input element has focus; the terminal does so for every key
except Ctrl+Shift combinations, so shells receive Ctrl+W and Ctrl+K. Ctrl+Shift+P
can never be claimed.

Override `Start(ModuleContext)` to open initial panels or start module work after
registration. `ModuleContext.Post(action)` dispatches module work to the UI thread
and refuses it after unload. If startup throws, the new module's commands, panels, and lifetime
are rolled back. `Stop()` runs during rollback, unload, and shutdown. Keep startup
side effects confined to the module's owned contributions.

## Files, processes and terminals

Reach the user's files, programs and terminals through an `IWorkspaceHost` (in Core), never
through `File`, `Directory`, `FileSystemWatcher`, `Process` or a pseudoconsole of the module's
own: `StatAsync`, `ListAsync`, `ReadAsync`, `WriteAsync`, `DeleteAsync`, `MoveAsync`,
`CreateDirectoryAsync`, `Watch`, `RunAsync` (a program, without a shell) and `OpenPty` (a
terminal). Loom shows This PC and any number of paired hosts at once (`app.Hosts`); each
`LoknHost` has its own `Workspace` and `BrowserTabs`, and a direct call would act on this PC,
at a path that may not exist here.

- **A panel that works on files, a repository or a terminal is host-bound**:
  `builder.Panel( "tree", "Files", create, onHost: true )`. Each panel of the type is bound to
  a host when it opens (`Panels.Open( …, hostId )`, else the host of the panel it opens beside
  with `OpenBeside`, else `app.Hosts.Current`), keeps it across restarts, saved workspaces
  and reloads, and the shell names a paired host on its pane and covers the panel while that
  host is offline. Use `context.Workspace` (its host's workspace), `context.Host` (its
  `LoknHost`, such as `context.Host.BrowserTabs`) and `context.Host.WorkspaceChanged`, which
  fires when the Remote module connects or disconnects the host. A panel that shows another
  host's content than its context's, like a chat panel, reports it through
  `PanelContent.HostId`; `app.Panels.GetHostId( panelId )` reads either.
- **Work that follows the host the user is on**, such as a command, uses `app.Workspace`,
  `app.BrowserTabs` and `app.WorkspaceChanged`: those of `app.Hosts.Current`. Read them on
  each use.
- **Tools** see the host whose agent called in `AgentToolContext.Host` (null for This PC);
  `context.App.Hosts.Get( context.Host )` is that host.

A host that is not connected has an `OfflineWorkspaceHost`, whose calls fail with an
`IOException`. File operations may finish after they return; `task.WhenDone( post, done )`
goes on at once when one already finished. A module's own files, such as a cache in the data
folder, are this PC's and may use `File`.

## Extending the window and other modules

Five registrations let a module extend the shell and other modules. All of them end
when the module unloads, so a reload replaces them cleanly.

|Registration|Read with|For|
|---|---|---|
|`Slot( slot, order, context => content )`|`app.Slots.Get( slot )`|Content in a window area: `ShellSlots.Sidebar`, `Tabs`, `TitleLeft`, `TitleRight`, `StatusLeft`, `StatusRight`; or in a slot another module draws, named `module.name`.|
|`ExtensionPoint<T>( "name" )`|`app.Extensions.Get<T>( "module.name" )`|Declares where other modules add values of type `T`, such as actions or renderers.|
|`Contribute( "module.name", value )`|the declaring module|Adds a value to another module's point. A value of the wrong type is refused at load.|
|`Service<TContract>( implementation )`|`app.Services.Get<TContract>()`|Publishes an API; one loaded module publishes each contract. Resolve it on each use: a reload replaces it, and it is null while its module is not loaded.|
|`Route( "name", request => … )`|`app.OpenLink( LoknLinks.Create( "module", "name", … ) )`|Opens `lokn://module/name/rest?query` links from notifications, chats and other modules.|

Core defines the shared service contracts. Resolve each on every use, and leave the
feature out when it is null:

|Contract|Published by|For|
|---|---|---|
|`IWorkingFolder`|Projects|`Current`: the open session's folder, where a new terminal, file tree or Git panel starts: a project's directory, else what a `SessionFolder` contributed to `projects.folders` gives, such as a task's worktree or project directory from Conversations; null elsewhere.|
|`IMarkdownRenderer`|Markdown|Draws Markdown text; without it, show plain text.|
|`IAgentService`|Omp|`AskAsync` and `AskSmallAsync`: one-off questions to the profile's model, such as naming chats or writing commit messages.|
|`IFileSideView`|Files|`Show( app, modeKey )`: shows the Files side view at the left edge in a mode, opening it when hidden, and leaves the focus where it was.|

Panel lookups by content type use `app.Panels.FindContent<T>()`, which returns the panel's
identifier and content, shown or hidden. `app.Settings.Changed` reports every settings change,
and `app.Settings.MoveValues( previousId, setting )` keeps saved values when a setting is renamed.
A module needs no `InternalsVisibleTo` for any of these; new modules must not ask for one.

Slot content is an ordinary `PanelContent`, created once after `Start` with the module's
lifetime, rebuilt like a panel, and closed when the module unloads. It is not a panel: it
never appears in the docking tree, layouts, or `shell_state`. The shell owns the
sidebar's width and visibility, keeps the profile switcher below its sections, and shows a
status contribution only while the status bar is on. The **Navigation** setting
(`shell.navigation`) switches the sidebar for tabs: the `Tabs` contributions and the
profile switcher then fill the middle of the titlebar, between the shell's actions and
the window controls. Conversations fills it with its chats, grouped as described below. Ctrl+B and the sidebar button hide only the sidebar; with tabs, both are off.
While the sidebar is hidden, resting the pointer on the window's left edge floats it out over the workspace (`SidebarPeek`); it slides
back 350 ms after the pointer leaves it, or at once on Escape. In both,
the first section fills the space and later ones take only what they need. Without
Conversations, a saved workspace is a **task**, in a project or not, and the sidebar looks
and behaves like the chat sidebar: **New task**, **Search** and **New project**, then each
project under a bold heading that folds its tasks, then the tasks outside projects under
**Tasks**. With Conversations, the chat sidebar starts with a header: the host switcher, then icon buttons for **New project**
and each `SidebarAction` contribution (Files, Notifications; `SidebarRows.BuildHeaderButton`), then the wide **New chat** button
and **Search**. A project's heading is a chevron that folds it, its icon, and its name with its chat count, which opens the
project's overview while the Overview module is loaded (`SidebarRows.BuildLinkedGroupHeading`). Its right side has fixed columns:
the folder's tools (**Files**, **Git**, **Terminal**: the `TaskStarter`s of those titles), the repository's **Pull Requests**
when it is on GitHub, then the state column. A tool's icon opens the task of that name in its folder, or creates it there with its
starter, so a project's tools stay out of its chat list; worktree labels carry their own tools. The state column shows the
chat's thread (`StitchMark`) while its agent works, a dot while it waits, a pin when pinned, and the row's **…** under the pointer.
Archived chats and **Tidy chats** are in a heading's **…** menu. The chats outside projects sit under **Scratch**: the 5 latest, pinned ones and the open one,
then **View all (N)**, which shows every one until **Show less**. Projects draws the task list; while Projects is not loaded, Workspaces draws it
without the projects. Rows show their actions on hover (a project's **+** and **…**, a task's pin
and **…**; in the chat sidebar, only **…**), a right-click opens the row's menu, pinned tasks come first, and double-click or
F2 renames a task in place. Resting the pointer on a project's **+** opens a grid of icons
(`SidebarRows.BuildAddButton` and `BuildStarters`), one per `TaskStarter` contributed to
`TaskStarter.Point` (declared by Workspaces) by `Order`, then **Empty task**: choosing one creates an empty task in the
project named after the starter's `Title` (**Empty task** gives "New task"), opens its workspace and runs the starter's `Open`,
which usually opens one panel there. A task started with **Chat** is named like any new chat, by its first prompt.
Conversations offers **Chat**, Terminal **Terminal**, Git **Git**, Files **Files** and Browser
**Browser**; a source module adds its own with
`builder.Contribute( TaskStarter.Point, new TaskStarter( "Notes", glyph, 30, app => app.Panels.Open( "notes.page" ) ) )`.
Every item of the sidebar drags: onto a pane, an `IPanelDrop` shows the item's panel there
(`PanelWorkspace.ShowAt` for an open panel, shown or hidden, and `OpenAt` for a new one), and within
the list, a row with a `RowOrder` moves among its group. Chat rows open their chat; a task without a
chat brings the active panel of its workspace (`WorkspacesModule.MovePanel`, which takes it out of the
saved layout with `PanelWorkspace.TakeFromLayout`), and a tool icon's task goes once its panel left.
Project headings reorder with `RowOrder( …, IsHeading: true )`: the other headings stay in place and
the one whose place it takes is outlined; `ProjectStore.Move` keeps the order as `ProjectInfo.Position`.
A `SidebarAction` with `OpenAt` drags onto a pane, as Files and Notifications do; any other button can
use `SidebarRows.BuildPanelDrag`.
Dragging a task onto another of its list moves it there (`RowOrder`, `SidebarRows.BuildRowDrag`):
only among tasks pinned as it is, in the same session, project or worktree list. Workspaces keeps
a session's task order (`WorkspacesModule.MoveTask`); Conversations keeps
`ConversationInfo.Position` (`ConversationStore.MoveConversation`), and tasks started since the
last move list above the placed ones, newest first. Both sidebars build their rows with `SidebarRows` (Workspaces)
and their project forms with `ProjectRows` (Projects). In tabs mode, the strip shows what the
user actually uses, grouped: one group per project (its icon and name, which open the project,
then its tasks or chats as tabs and a small **+** that starts one there) and a **Tasks** or
**Scratch** group for those outside projects, on a shared tint, then a **+** that starts one
outside projects. A tab shows when it is current, pinned, or used in the last 7 days, at most
3 per group and 8 in all, the current one always; groups follow their most recently used tab,
tabs their last use, and a group with nothing to show is left out unless it holds the current
tab. To keep tabs from moving under the pointer, the strip re-sorts only when the current tab
changes from outside it (sidebar, Search, a command); after its own clicks, or while chats
work, everything keeps its place and newly shown tabs go last. Everything stays in the Search
palette. A task's last use is when its saved workspace was last on screen
(`SavedWorkspace.LastUsed`, `WorkspaceTask.LastUsed`); a chat's is its last activity or the
last opening of its workspace. `RecentTabs` and `RecentTabOrder` (Workspaces) pick, order and
draw the groups for all three modules. With Conversations loaded only its strip shows;
otherwise Projects draws the task strip, with a button that adds a project, and without
Projects, Workspaces draws only the Tasks group.

A project's brief (the Overview module's page) takes items from other modules: contribute a
`BriefSection` (Core) to `BriefSection.Point` (`overview.sections`). Its `Read` gets a
`BriefProject` (the project's id, name, folder, host, profile, branch, `origin` remote, and the
brief's panel) and returns a `BriefContent` with `BriefItem`s (headline, short state, `Description`,
`Time`, icon, an `Open` action, and `NeedsYou`), an `Empty` note and `BriefLink`s, or null to add
nothing. The brief mixes every source as Dia's Morning Brief does: items that need the user go under
**Top to-dos**, the others under **New updates** by `Time` (an item with no time and no need shows
nowhere), each with the section's `Title` and the item's state as its source line; notes and links
follow New updates. The page calls `Read` on the UI thread on every build, so return what the module
holds and fetch in the background into a Goo `State` that `Read` reads: the page builds again when
it changes. Items also go to the model that writes the brief's line, and an exception shows as a
note. GitHub adds the project's pull requests this way; a source module can add tickets the same way:

```csharp
builder.Contribute( BriefSection.Point, new BriefSection( "tickets.mine", "Tickets", 20, project =>
	project.Name != "Atlas" ? null : new BriefContent( [.. _tickets.Value.Select( ticket =>
		new BriefItem( ticket.Key, $"{ticket.Key} {ticket.Summary}", ticket.Status )
		{
			Description = ticket.LastComment,
			Time = ticket.Updated,
			NeedsYou = ticket.Status == "In review",
		} )] )
	{ Empty = "No tickets assigned to you." } ) );
```

Types shared by a point or a service live in a project both modules reference. Source
modules see only Core, so they can use slots and routes, and points or services whose
types are Core or .NET types.

Declare `Dependencies => ["other-module"]` only when the feature actually needs
another module's contributions. Load dependencies first. A required module cannot
be disabled while a dependent is loaded. Independent modules never need references
to each other. The Shell module and Workspaces cannot be disabled; the **Modules** tab in
Settings turns the rest on and off, turns on what a module needs with it, and keeps the
choice in `%LOCALAPPDATA%\Lokn\modules.json`. The daemon reads the same file and follows it
live: a module turned off stops there too, with the modules that need it, except Remote and
the modules it needs ([remote.md](remote.md#roles)).

`app.Unload(id)` removes the module's commands and panel types, closes its open
panels, and cancels its token. `app.Enable(id)` registers a previously composed
module again with a fresh lifetime. Modules may be stopped and restarted; reset
module-owned background work accordingly. Shutdown stops modules in reverse load
order. The Modules panel exposes these same APIs.

## Panel lifetime and state

A panel's `PanelContent` instance exists until the panel closes. Keep persistent
panel state on that instance, typically in Goo `State<T>` values. Return a fresh
Blob declaration from every `Build()`. Nested `Cell.Mount<T>()` views also retain
their identity during docking and swaps because the shell keeps every panel view under the
same retained parent. Every panel has its own visible pane.

Derive nested views from `GuardedCell` or `GuardedCell<TInput>` (Core) and override
`BuildView`, never from Goo's `Cell` directly. Goo builds a nested cell in its own frame
loop, outside the panel's guard, so an exception there would close Lokn; a guarded cell
draws a warning mark instead and reports the failure once through `CellFaults.Faulted`,
which the shell shows as its error notice. Never call `Cell.Post` from a build: Goo
refuses it. Defer work after the build with `Host.Post` instead.

A view that moves on its own, such as a looping animation, a spinner, a blinking caret or a
clock, runs only while someone can see it, so an idle Loom draws no frames
([performance](performance.md)). Read `LiveMotion.IsAttended( Host )` (decorative motion and
activity marks: the view is shown and the window has the keyboard) or `LiveMotion.IsSeen( Host )`
(what the user reads, such as an elapsed time: shown and not minimized) in `BuildView`, and pass
it to a `MotionSwitch` that starts and stops the view's timer or animation; Goo rebuilds the view
when the answer changes. The shell marks a panel the layout hides, or one under a modal window,
as not shown. Declare a `State<T>` in a view whose timer calls back often: a cell without one
rebuilds after every callback, even when nothing changed.

Use `PanelContext.Lifetime` for cancellable work. `PanelContext.Post(action)` posts
from a worker to the UI thread, refuses work after close, and checks cancellation
again before executing queued work. Call `context.Run(action)` around synchronous
feature callbacks to route exceptions into the shell's dismissible error notice.
Goo `State<T>` updates automatically rebuild the panel Cells that read them.

Override `PanelContent.Closed()` only for resources that the panel actually owns
and needs to release at close. The token is already cancelled when the hook runs.
Nested Goo Cell lifetimes still follow normal mount/unmount behavior. Do not tie
panel-owned state or background work to a pane's location in the docking tree.

## Popup windows

Any registered panel type opens as a popup window over the shell instead of in a pane:

```csharp
context.App.Popups.Open( "notes.page", input, new PopupOptions { Width = 800, Height = 600 } );
```

The panel is created by its type's factory as usual, with its own `PanelContext` and lifetime,
but stays outside the dock tree: it is not saved with the layout. Opening a type that is
already open brings its window to the front. Escape, a click on the scrim or the close button
closes it (`App.Popups.Close( window )`), which ends the panel's lifetime and calls `Closed()`;
so does unloading its module. While a popup is open, global shortcuts do not run; its panel's
`ClaimsKey` still applies and Ctrl+K opens Search over it. Settings opens this way.

The frame is `ModalWindow`, the same one Search and `ConfirmDialog` use: a raised card
over a scrim with a modal focus scope, which hides native surfaces such as browser pages while it
shows. A view that needs its own modal window mounts it directly with
`Cell.Mount<ModalWindowInput, ModalWindow>`.

## Asking for the user

A panel that is waiting for the user calls `context.RequestAttention( title, message )`,
for example when a chat finishes or asks a question. The workspace outlines the pane
in `Theme.Attention`, and the desktop chimes and, while Lokn is in the background,
shows a Windows notification; clicking it opens the panel's feed entry as below. A request for the panel the
user is already looking at is acknowledged at once. Repeating a request while one is
pending only updates its message. The request ends when the user activates the
panel, when the panel calls `context.ClearAttention()`, or when it closes;
`App.Panels.GetAttention( panelId )` reads the pending request and
`App.Panels.WaitingPanelIds` lists the waiting panels. Reading either during a build
rebuilds the reader when a panel starts or stops waiting.

`context.RequestAttention( new PanelAttention( title, message ) { Tone, Project, Details } )` also tells the feed
how to mark the entry (`NotificationTone.Success`, `Failure` or `Attention`), which project it belongs to, and
short facts to show with it, such as a branch or a change count. A chat sets all three.

A request the user did not see at once is also recorded in the notification feed
behind the titlebar bell, shared by Work and Personal; opening the entry, from the feed,
a banner or a Windows notification, travels to where the panel belongs. With
Conversations loaded, a chat's entry opens its task, and another panel's entry opens
the task or project whose saved layout holds it; either way it switches to the
workspace of that session holding the panel, and to the other profile when the
panel belongs to it, rather than pulling the panel into the current layout. Only a
hidden panel no saved layout holds comes beside the active pane. Acknowledging the
request marks its entries read.

## Posting notifications

Any module, source modules too, records a notification without asking for attention:

```csharp
context.App.Notifications.Post( new( "GitHub", "Checks failed on #12" )
{
	Link = new( "https://github.com/o/r/pull/12" ),        // or LoknLinks.Create( "mymodule", "route", … )
	Source = "github",                                       // module id or panel type
	Kind = "github.checks-failed",                           // picks the icon
	PanelId = null,                                          // the panel opening it shows
	Actions = [new( "Rerun", LoknLinks.Create( "github", "rerun", "12" ) )],
	Tone = NotificationTone.Failure,                         // the mark beside its icon
	Project = "o/r",                                         // the Notifications page filters by it
	Details = ["main", "3f2c1a9"],                           // short facts shown with it
} );
```

That adds it to the feed and badges the bell, plays the chime once per burst, and shows
no Windows notification. The feed groups consecutive entries with the same title like
chat messages from one sender, so use a stable source name such as `GitHub`. `Actions`
show as buttons in the feed and the banner; each follows its link, so a button usually
calls one of the module's own routes. `Items`, `UnreadCount`, `MarkRead`, `MarkAllRead`,
`Remove` and `Clear` read and manage the feed on the UI thread; `Posted` reports each new
entry.

The Notifications module (`Modules/Notifications`) draws the Notifications page
(`notifications.feed`: filters by state, source and project, a search, and a reading pane) and adds the bell to
`ShellSlots.TitleRight`: the bell owns its popover (All, Waiting and Unread tabs) and the banners under it, and
hides native surfaces while they show with `app.CoverNativeSurfaces( owner, isCovered )`. Without the module there
is no bell. It opens the page with the `notifications.open` command or `lokn://notifications/feed`, and declares
these extension points other modules contribute to:

|Point|Contribution|Effect|
|---|---|---|
|`NotificationKind.Point`|`new NotificationKind( kind, glyph ) { Icon, IsUpdate, Summarize }`|The icon of entries whose `Kind` is `kind`, else whose `Source` or source module is: `Icon` is a vector, such as a logo, drawn instead of the glyph. `IsUpdate` folds entries into a summary line, and `Summarize( count, first )` words it, such as "3 checks passed". GitHub ships its mark and wording this way; the feed has no code for any one module.|
|`NotificationOpener.Point`|`new NotificationOpener( ( app, item ) => … )`|Opens an entry before the default (activate its panel, else follow its link). Conversations uses it to travel to a chat's task.|
|`NotificationPromptSource.Point`|`new NotificationPromptSource( app => prompts )`|Questions panels wait on. Each `NotificationPrompt.Build( surface )` returns compact answering controls for `banner`, `feed` (the popover) or `page`, without a frame: the view draws the card around them. Conversations offers its chats' questions this way.|

Opening an entry marks it read, then asks the openers in load order. The shell closes a
banner when the user looks at its panel, when a newer entry from the same panel, or
without a panel the same title, replaces it, when the popover opens, or when more than
three would show. A chat's banner shows the question its agent waits on and stays until
it is answered.

## Getting started

The Setup module (`Modules/Setup`) draws **Get started** (`setup.get-started`), which lists
what Loom needs from the machine and installs each in one click, never asking for a path. It
opens with the `setup.open` command or `lokn://setup/open`, and by itself once per launch, after
startup, when a required item's check finds it not ready. It declares one extension point:

|Point|Contribution|Effect|
|---|---|---|
|`SetupItem.Point` (`setup.items`)|`new SetupItem { Key, Title, Purpose, IsRequired, Order, CheckAsync, FixLabel, FixAsync }`|One row of Get started, in `Order`: a status dot, `Title` with a Required or Optional tag, `Purpose`, and the `Detail` of the `SetupStatus` that `CheckAsync` returns off the UI thread. While the state is `Missing`, the `FixLabel` button runs `FixAsync`, whose progress lines show under the row and whose exception message shows as the failure; `Blocked` disables it until another item is ready. One fix runs at a time and every item is checked again after it. Git (10), Omp (20), the Omp sign-in (30) and WebView2 (40) contribute theirs.|

Once every required item is ready, the page says **Loom is ready.** and **Start a chat** runs
`conversations.new`. Items a module load or reload adds are checked when they appear. For checks and
fixes, `ToolLocator` (Core) finds a tool on its known paths, then on PATH read fresh from the registry,
and runs winget installs, PowerShell scripts and other processes, reporting their output as progress.

## Opening links

Call `context.App.OpenLink( uri, context.Id )` when the user follows a link, instead
of starting a process. A `lokn://module/name/…` link goes to the route `name` that
`module` registered; a link no loaded route opens is reported as an error. Other links go
to the modules that registered `builder.LinkHandler( request => … )`, asked in load order;
a handler returns true when it opened the link. The Browser
module takes http and https links and opens each in a new tab of the browser panel on
screen, or in a new browser panel beside the panel id given when none is shown. Pages an
agent opens (`browser_show`, `browser_navigate`) go to the browser beside its chat, in
the chat's profile; a chat the user is not looking at gets its browser hidden beside it,
and its tools never drive the browser on screen. A link no handler takes goes to the
default browser when it is http or https.

## Commands

Register a command through the same module builder:

```csharp
builder.Command(
	"open-note",
	"Notes: create note",
	context => context.App.Panels.Open( "notes.note" ),
	new( Key.N, KeyModifiers.Primary ) );
```

Use `CommandAsync` for `Func<CommandContext, Task>` callbacks. It supplies the
owning module token, prevents reentry while running, and catches execution errors.
The registry dispatches native commands through Goo's async UI dispatcher, so
awaited command work resumes on the UI thread.
Feature code using `ConfigureAwait(false)` or explicit workers must dispatch UI
changes back through a live panel context. Cancellation is cooperative: async code
must honor its token before applying results.

An optional `canExecute` predicate controls both palette and shortcut execution.
Keep predicates fast and free of side effects: they are queried while rendering.
A throwing predicate disables its command. `app.Commands.ExecuteAsync(id)` returns
false for missing, unavailable, cancelled, or failed commands. Execution failures
produce a dismissible shell notice. Predicates do not mutate shell error state
during a build.

## Search

The Search palette (Ctrl+K, Ctrl+Shift+P or the titlebar search icon) finds what modules
list. A module lists its items under a type, one tab of the palette:

```csharp
builder.SearchProvider( "notes", "Notes", 70, context => Notes.Select( note =>
	new SearchItem( note.Id.ToString(), note.Title, () => Open( context.App, note ) )
	{
		Detail = note.Folder,                                   // quiet text on the right
		Glyph = "\uE70B",                                       // a Segoe Fluent Icons character
		Keywords = [note.Body],
		LastUsed = note.EditedAt,                               // ranks it among Recents
		Actions = [new MenuAction( "Delete", () => Delete( note ) ) { IsDestructive = true }],
	} ) );
```

- The provider is `module.type` (`notes.notes`) and leaves with its module. Modules listing
  the same type share its tab, named and placed by the lowest `order`: Workspaces and
  Projects both list `tasks`. The built-in types are Tasks (10), Projects (20), Chats (30),
  Panels (40), Actions (50) and Settings (60). `/type ` in the palette filters by the key.
- The provider runs on the UI thread every time the palette builds and the palette ranks
  what it returns, so return every candidate and keep it cheap; reading state there
  rebuilds the palette when it changes. `context.Query` is the typed text, for a provider
  that must narrow a large set itself. A provider that throws lists nothing, and its
  failure reaches the shell notice once.
- Keep `Id` stable: the palette remembers what the user ran by provider and item, per
  profile, while Lokn runs (`app.Search.Recents`).
- Every query word must match. A title starting with the word ranks first, then a later
  title word starting with it, the title containing it, and the word's letters in order
  from a title word's start; keywords score 60% and the detail 30% of a title match. All
  groups matches by type, the best group first, six per type; a type's tab lists them all.
- With no query, All shows Recents (the items run last, then those with the newest
  `LastUsed`), then each type's first four items, newest first. A type whose providers
  list nothing has no tab.
- `Run` runs after the palette closes; `Actions` open in a menu from the selected row's
  ••• or Alt+Enter. Both are guarded: a failure becomes the shell notice.
- `app.Search.Search( query, type )`, `ComputeTypes()`, `Run( hit )` and
  `SearchRegistry.ReadFilter( text, types )` are what the palette itself calls.

In the palette, Up and Down select, Enter runs, Alt+Enter opens the selected item's
actions and Escape closes. Left and Right at either end of the query, or Ctrl+Left and
Ctrl+Right anywhere, change tab; `/` starting the query lists the types and `/tasks `
switches to Tasks. Hovering selects a row, clicking runs it, and clicking a tab filters.

|Type|Listed by|Items|Running one|Actions|
|---|---|---|---|---|
|Tasks|Workspaces, Projects|Tasks outside projects, then each project's tasks with its name; none while Conversations is loaded|Shows the task in its session|Pin or Unpin, Delete task|
|Projects|Projects, or Conversations while loaded|The active profile's projects with their directory|Opens the project's session; with Conversations a first opening shows its latest chat|New task; with Conversations New chat and New task|
|Chats|Conversations|The active profile's tasks with and without a chat, pinned first, then the latest|Opens the task's workspace|Pin or Unpin, Start a chat here, Delete task|
|Panels|Shell|Open panels, hidden ones too, with their module|Activates it; a hidden one travels to its task through `NotificationOpener` as its notification would|Close panel|
|Actions|Shell|Commands that can run now, with their shortcut|Runs the command|None|
|Settings|Shell|Every setting, with its category|Opens Settings on its tab with it highlighted|None|

## Agent tools

A module can give agents tools, so an agent can use the feature the way a person
would but through a stable API instead of the screen. Connected Omp chats normally
receive every registered tool; Economy-mode chats start with workspace control and
load other module groups on demand. See [Omp backend](omp-backend.md#lokn-tools-for-agents).

```csharp
builder.Tool(
	"append",
	"Appends a line to the open note. Use it to record findings for the user.",
	"""{ "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"] }""",
	( context, arguments ) =>
	{
		var text = arguments.GetProperty( "text" ).GetString()!;
		…
		return new( "Appended." );
	} );
```

- The tool's name is the module ID and local ID joined by `_`, with any character
  other than letters, digits, `_` and `-` replaced by `_`: `notes_append`. Names are
  limited to 64 characters and must be unique, checked with the rest of registration.
- The description and JSON Schema are what the model sees; write them for it.
- Handlers run on the UI thread. `ToolAsync` takes `Task<AgentToolResult>` handlers,
  which start on the UI thread and receive `context.Cancellation`, cancelled when the
  agent abandons the call or the module unloads.
- `context.Caller` is the panel whose agent made the call, such as its chat, so a
  tool can open something beside it. Use `Panels.OpenBeside( typeId, context.Caller, input )`
  rather than `Open`: the caller may be a chat working in the background, in another
  session or profile, and its panel must not land in the layout the user is looking at.
  `Panels.GetProfile( context.Caller )` gives the caller's profile, so what it opens
  uses the same account.
- Return `new( text )`, `AgentToolResult.Json( value )` for structured data, or
  `AgentToolResult.Error( message )`. Throwing `ArgumentException`,
  `InvalidOperationException`, `KeyNotFoundException`, `FormatException` or
  `JsonException` also returns an error to the agent; any other exception is also
  reported in the shell notice.
- `context.CallId` is the agent's identifier for the call. `context.Report( result )`
  shows progress in the call's card while the tool runs; it is callable from any
  thread and the latest report wins. The agent only reads the final result.
- `app.Tools.Items` lists the tools and `app.Tools.InvokeAsync` runs one, from any
  thread, which is what a bridge to an agent does. Unloading the module removes its
  tools and raises `app.Tools.Changed`.
- Chats that run in the daemon (Remote access, role `daemon`) get the tools of the modules the
  daemon loads ([remote.md](remote.md#roles)). The tools of every other module, source modules
  included, reach those chats only while Loom on the daemon's PC is open and connected to it:
  that window lends the daemon its tools and runs their calls ([Host tools](remote.md#host-tools)).
  A paired host's chats get only this PC's browser tools; their calls carry the host in
  `context.Host`.

The Shell module's own tools (`shell_state`, `shell_capture_panel`, `shell_open_panel`,
`shell_focus_panel`, `shell_move_panel`, `shell_close_panel`,
`shell_list_commands`, `shell_run_command`) are in `Shell/ShellTools.cs`.
`shell_capture_panel` saves only the named visible panel without opening another panel. It uses the
Remote module's native capture contribution when available, so native child surfaces such as a
Browser page appear too; Goo's scene capture is the fallback. Agents use it instead of the Remote
module's window tools to inspect Loom itself. The module was called `workspace` before; Lokn moves
saved `workspace.*` settings to `shell.*` at startup. Workspaces (saved layouts) is a separate
feature module and is always on.

The Remote module's window tools (`window_list`, `window_show`, `window_capture`, `window_click`,
`window_type`, `window_key`, `window_scroll`) let agents see and drive other desktop windows. The user of
this PC sees them on screen under a control bar that stops the agent with Stop or Esc, and paired devices
stream them; they are offered with `app.Tools.Offer` while `remote.agent-windows` is on ([Window tools](remote.md#window-tools)).

### Tool cards

A module can draw calls to its own tools as a card in the chat instead of the chat's
tool line, and act from it: a build with a progress bar per step, a test run, a
screenshot to approve. The card draws from what the call carries, so it looks the
same while the tool runs, after Lokn restarts, and in history.

```csharp
builder
	.ToolAsync( "build", "Builds the project, showing each step to the user.", schema, async ( context, arguments ) =>
	{
		context.Report( new( "Compiling", Details: new { steps } ) );
		…
		return new( failed ? $"Build failed: {summary}" : summary, Details: new { steps } );
	} )
	.ToolCard( "build", card => BuildCard( card ) );
```

- `AgentToolResult.Details` is any object, serialized with camel-case names and
  without nulls. The agent reads `Text`; the card reads `card.Details`, which holds
  the latest report while the call runs and then the result's details, kept with the
  conversation.
- Agents usually run Lokn's tools by writing their JSON arguments to `xd://<tool>`, so
  the call arrives as a `write`. The chat still draws it with the tool's card: the card
  gets the tool's own arguments and details, which Omp keeps under the result's `xdev`
  details, and the written text as the arguments until the result arrives.
- Omp keeps only the text of a result with `IsError: true`, so its details never reach
  the card. A tool whose card shows failures, such as a failed build step, returns
  `IsError: false`, records the failure in `Details`, and says so in `Text`.
- The card gets `card.Status` (`Requested`, `Running`, `Done`, `Failed`),
  `card.Arguments`, `card.Text` and `card.CallId`. Draw only from these, never from
  live module objects; a module that needs to reach a running call, such as to stop
  it, keeps it by `CallId`.
- `card.State<T>()` is the card's view state, kept per chat and call, such as which
  sections are open. Change it inside `card.Run( change )`, which redraws the card.
- `card.Prompt( message )` sends the agent a message as if the user typed it: a new
  turn when it is idle, or queued after the running turn.
- The build delegate runs on the UI thread on every redraw of the call; keep it cheap.
  A card that throws is reported once and its calls fall back to the tool line.
- A card is only for the module's own tools; registration fails otherwise. Calls
  with a card stay visible rather than folding into the chat's activity summary.
  Reloading the module redraws every call with the new card.

## Workspace API

- `Open(typeId, paneId: null, input: null)` fills an empty pane or splits an occupied
  pane to the right, keeping every panel visible.
- `Activate(panelId)` focuses the pane containing the panel. A hidden panel (below)
  first comes back into the layout, in the active pane or beside it.
- `OpenBeside(typeId, besidePanelId, input)` opens a panel in a split beside another.
  When that panel is hidden, the new one stays hidden and waits; it appears beside its
  source the first time the source comes back on screen, through `Show`, `Activate` or
  a session's layout. Closing the source closes a panel still waiting for it.
  `GetWaitingBeside(panelId)` tells whether a panel waits.
- `PanelContent.Profile` is the settings profile a panel works in, such as a chat's
  account; null follows the active profile. `GetProfile(panelId)` reads it.
- `ActivatePane(paneId)` chooses where future panels and commands operate.
- `SplitActive(axis)` creates and selects an empty neighboring pane.
- `Move(panelId, paneId, placement)` swaps occupied panes or edge-docks a panel
  without recreating its content. `PanelPlacement.Swap` is the default. An empty
  target is filled directly. Moving a pane onto itself is a no-op.
- `UpdateSplitRatio(splitId, ratio)` updates the first-child fraction.
- `HideAll()` hides every panel behind one empty pane, closing nothing; `Show(panelId, paneId)`
  puts a panel in a pane, hiding the panel it held, and moves a panel shown elsewhere.
  Sessions build their first layout with these, and a new chat takes the chat pane's place.
- `Close(panelId)` and `CloseActive()` cancel and remove panels, collapsing empty
  source splits. The final empty pane remains usable. Hidden panels close too.
- `CaptureLayout()` returns the tree, active pane and shown panels' captured state as
  JSON. `ApplyLayout(layout)` replaces the tree with one: each panel in it is matched
  to an open panel by identifier (following module reload renames), then by type and
  equal captured state, else opened from its saved state. Open panels the layout
  leaves out are **hidden**: still open, still working, not drawn. `IsShown(panelId)`
  tells them apart; `Panels` lists both. `ReadLayoutTree(layout)` reads a captured
  layout's tree, for previews. The Workspaces module builds saved workspaces on these, one
  set per session (`WorkspacesModule.OpenSession`); Projects opens a session per project
  (`ProjectsModule.OpenProject`, keyed `project:<id>`), and Conversations opens Home, a session
  per task, and a project's session with its latest chat as the first layout.

`Root` is an immutable tree of `DockPane` and `DockSplit` records. Inspect it to
understand layout; mutate it through workspace methods so panel ownership and
active selection remain consistent. All application and workspace mutation happens
on the UI thread. Feature projects can open panels and commands without knowing
about shell views or the docking renderer.

## Fault boundaries

Registration, startup, stopping, panel factories, root panel builds, and global
command execution are guarded. A root build failure is rendered inside that panel
and does not remove sibling panels. Use `PanelContext.Run` for callbacks and
normal Goo error handling in nested Cells. Arbitrary nested Cell lifecycle failures
or unguarded callback exceptions are still ordinary application bugs.

Modules are trusted code in one process. This contract provides ownership,
dependency checks, rollback, and cooperative cancellation; it is not a security or
process isolation boundary. Modules should not directly modify other modules'
state, keep static cross-feature event subscriptions, or assume another feature is
present without declaring a dependency.

## Module settings

Declare typed `Setting<bool>`, `Setting<int>`, or `Setting<string>` definitions
with IDs under the owning module's namespace, then register them alongside its
panels and commands:

```csharp
public static readonly Setting<bool> Compact = new(
	"notes.compact",
	"Compact notes",
	"Uses a compact layout for note panels.",
	defaultValue: false,
	category: "Notes" );

public void Register( ModuleBuilder builder )
{
	builder
		.Setting( Compact )
		.Panel( "note", "Note", context => new NoteContent( context ) );
}
```

An optional `isValid` delegate constrains values, such as an integer range. Invalid
defaults reject the definition; invalid edited or stored values never reach features.
Settings registration participates in the same transactional checks as commands
and panel types. The Settings panel shows one tab per module that registers settings,
named by the module's `Title` (the shell's Workspace tab first), and groups a tab's
settings by `category`; `app.Settings.FindModule( setting )` tells which module owns one.
Unloading a module removes its tab while retaining saved values for re-enabling the feature.

Set `Editor = (app, scope) => …` in a setting's initializer to replace the settings
panel's built-in field with the module's own view. It builds inside the settings
panel for the scope being edited and reads and writes through `app.Settings`; the
theme picker is one.

Read effective values on the UI thread with `context.App.Settings.Get(Compact)`.
Reads made inside `PanelContent.Build()` participate in Goo state tracking, so
profile switches and edits rebuild the views that read settings. Keep a panel's
own transient state on its existing model; profile changes do not recreate panels.

Use `Get(setting, SettingsScope.Work)` to inspect a scope independently of the
active profile. `Set(setting, scope, value)` stores an explicit global or profile
value. `HasValue` distinguishes an explicit value from inheritance, and `Reset`
removes that layer's value. `SetActiveProfile` and `ToggleProfile` change which
profile supplies effective settings. Work and Personal each inherit global values
unless they have an explicit override; changing a global value never copies it
into either profile.

The settings service is available as `LoknApplication.Settings`. Applications may
pass a file path to `new LoknApplication(path)`; omitting it keeps the service in
memory for tests and headless usage. The desktop composition uses
`SettingsService.DefaultPath`. Failed saves report a shell notice while keeping
edits active in the current session. Re-submitting a value retries the save.

## Restoring panels

The desktop passes `workspacePath: PanelWorkspace.DefaultLayoutPath` to
`LoknApplication` and calls `RestoreWorkspace()` after registering its modules.
Shutdown saves the live tree before cancelling module or panel lifetimes.
Omitting `workspacePath` keeps layout persistence disabled for isolated tests.

A panel can override `CaptureState()` to return a small `JsonElement` identifying
its feature-owned document. Read `PanelContext.RestoredState` in the panel factory
to reopen it. Normal `Input` remains the typed input supplied by `Open`; restored
JSON is a separate contract. For example, the conversation panel saves its chat ID
and immutable account profile, while the conversation store saves its messages.
Do not capture process handles or callbacks.

`PanelWorkspace.SaveLayout(path)` and `RestoreLayout(path)` expose explicit
snapshot operations. They retain pane/panel IDs, split axes and ratios, empty
panes, and active pane. Restore validates the whole document first, then skips
unregistered panel types and collapses their empty branches. A factory failure
remains a contained panel error. Invalid files remain untouched and prevent that
session from overwriting the failed source.

## Feature composition

Modules reach each other through Core's services and extension points first, and through
`context.App.GetModule<T>()` only for a module they are built on, never through
constructor arguments, because the application creates each module itself. Loaded
by the application, the Conversations module opens its history file, builds chat
panels with the Omp module's service (a chat is an Omp client), asks one-off questions
through `IAgentService`, and declares `Dependencies = ["omp", "workspaces", "projects", "markdown"]`
so those modules cannot unload underneath them. Git is optional: without it a chat shows no
branch and **View changes** is greyed out. Workspaces is always on. The Projects module owns projects, their file
and their `ProjectStore` (`GetModule<ProjectsModule>().Store`): Conversations attaches it to its
`ConversationStore` when it starts, groups chats by `ProjectId`, and takes a deleted
project's chats out of it through `ProjectStore.Deleted`. While Git is loaded, each chat holds a `GitModule.Watch( host, folder )`
for its working folder on its host; holders of one folder on one host share a single reader and its file watchers.
Worktrees reads This PC's project list from `GetModule<ConversationsModule>().Hosts.Local.Store.ProjectStore` the same
way, and runs git through `GitProcess`, as every feature does; `GitProcess` runs it through the `IWorkspaceHost` it is given.

**Worktrees in the chat sidebar.** Conversations declares `conversations.worktrees`
(`WorktreeActions.Point`) and Worktrees contributes one `WorktreeActions`: list a repository's
working trees, create a branch's worktree (`<repository>.worktrees/<branch>` with the repository's
saved setup: sparse folders, shared folders and the native build, the same
`WorktreeSharing.CreateAsync` the panel and `worktrees_enter` use), remove one (folder links first;
the main checkout, uncommitted changes and anything git refuses are refused with their reason,
never forced), open the Worktrees panel on a repository, and a change count. Conversations never
references Worktrees, which keeps the create logic in one place. While it is loaded, a project
whose chats work in a linked worktree lists each such worktree as an unindented label with its
chats under it, in git's order, the main checkout last; a worktree no chat works in is left out
(the Worktrees panel still lists it), and a project whose chats all work in its directory stays
flat. A label shows a **+** (resting on it opens the starter grid) and a **…** with **New chat**,
**New task**, **Open folder** and **Remove worktree** (disabled for the main checkout; it asks in
a `ConfirmDialog` in the middle of the window, which shows git's refusal and stays open while git
works, then moves the worktree's chats back with `ConversationStore.ReleaseWorktree`).
The project's **…** adds **New worktree…**, a form under the heading (branch, optional start
point, Create; Escape cancels) that creates the worktree and opens a new chat in it, and
**Manage worktrees**. The sidebar lists a repository again when the change count moves (any
create or remove through the sidebar, panel or tool) and when the main checkout's watched git
state changes; a worktree added outside Lokn shows after the next such change. Each label's folder
is watched (`GitWatch`); once the folder is missing or no longer the top of a repository and holds
no `.git`, as after `git worktree remove` in a shell, the sidebar calls `ReleaseWorktree` on it.
Without Worktrees, labels show only once a project's chats work in several folders, with
**New chat**, **New task** and **Open folder**.

**Repository commands.** Beside `GitProcess` and the watched `GitState`, the Git module's
`GitRepository` reads a repository's history, references, stashes, commit files and patches,
working-tree changes and in-progress operation, and runs the panel's commands: staging,
committing, branches, merges, rebases, cherry-picks, reverts, resets, tags, fetch, pull,
push and stashes. Every method runs git through `GitProcess` with a no-op editor and literal
pathspecs, and throws `InvalidOperationException` with git's message. `GitGraph.Layout`
turns a `--topo-order` log into lanes and line segments for a commit graph column.

**Git views.** The Git module draws the Git panel (`git.panel`); other modules open it with
`GitModule.OpenPanel( app, besidePanelId, directory, focus, staged )`, as chats do: without
`focus` it shows the commit graph beside the working tree, with the full path of a changed
file it shows that file's diff. Opening it again for the same repository reuses the open
panel. The panel was `conversations.git` while Conversations drew it, and the diff panel it
replaced was `conversations.diff` and then `git.diff`; the Git module registers all three as
former identifiers with `builder.PanelAlias( formerTypeId, id )`, so `RestoreLayout` and
`ApplyLayout` (startup and saved workspaces) open old panels as Git panels on the folder they
saved. Hunks are staged, unstaged and discarded with `GitRepository.StageHunkAsync`,
`UnstageHunkAsync` and `DiscardHunkAsync`, which `git apply` one hunk's patch from
`GitPatch.BuildHunkPatch`; `GitPatch.Parse` is the one unified-diff parser. Git declares two
points: `git.actions` (`GitAction`, a working-tree button or a commit or branch menu entry; an
action may link a review with `GitActionContext.LinkReview`, whose verdict and P0-P3 findings
the panel then shows and marks on the diff) and `git.messages` (`GitMessageWriter`, which
writes the commit box's summary and why). Conversations contributes **Review in chat** (an Omp
`/review` chat) and a writer that asks the profile's small Omp model.

**Side view modes.** The Files module's panel (`files.tree`, the sidebar's **Files**) is a side
view with a row of mode buttons under its header. The built-in Files mode (`FileTreeMode.FilesKey`)
is the file tree and always comes first; modules add modes by contributing
`new FileTreeMode( key, glyph, name, order, context => content )` to `files.modes`, listed after it
by `order`. A mode's content is a `PanelContent` created the first time the user picks the mode,
with a `FileTreeModeContext` (the Files panel's `PanelContext`, the `IFileTreeView` whose `Root`
is the shown folder, and the mode's restored state); it is kept while other modes show, built in a
cell of its own, captured with the panel (the selected mode and every mode's state) and closed
with it. A mode whose module reloads is created again from its new contribution with the old
content's state; while it is not loaded, the tree shows. Header buttons (`files.buttons`) show in
the Files mode and in modes that set `ShowsButtons`. Git adds **Changes** (the working tree as
unstaged and staged folder trees with checkboxes and counts, clicking a file shows its diff in the Git
panel while the list keeps the focus, a commit box with **Write it**, **Amend** and the generate-or-commit button, and the HEAD
footer) and **History** (branches, remotes, tags and stashes above the commit log and the selected
commit). A Git panel the user opens on the open project's folder (not a restored one) opens the side
view in **Changes** through `IFileSideView`. Files and Git never reference each other; both use the Core contracts.

The constructors that take a store and a panel factory remain for checks and other
hosts, which can supply their own conversation renderer and backend dependency.

Omp subprocesses own native pipe and process handles. Closing a chat stops its
process; unloading Omp waits for its children to exit or terminates them after the
shutdown deadline. Provider credentials remain with Omp, and conversations capture
their profile before any asynchronous startup. See [Omp backend](omp-backend.md)
for setup and transport details.