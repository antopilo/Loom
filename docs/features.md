# Using Loom

A tour of what Loom does and how to use it. To build Loom, see the [README](../README.md);
to extend it, see [architecture](architecture.md) and [modules](modules.md).

## Home

Home is what a conversation panel shows while it has no conversation: the animated
wordmark, cards for the active profile's recent projects (and **Scratch**, the conversations
outside projects), an **Open folder…** card that opens a directory as a project, and a
prompt bar. Loom opens on Home. Enter in the prompt bar starts a chat in that same panel,
in the current project's session when you are in one, and sends what you typed.
Project cards and sidebar headings pick their icons automatically. Put
`.lokn/icon.png` (or `.jpg`, `.jpeg`, `.gif`, `.webp`) in a project for an explicit
icon. Without that override, an s&box project (`.sbproj`) or engine checkout uses
the s&box program icon from its engine installation. Other projects use conventional
`icon`, `logo`, and `favicon` images in the root, then `assets`, `public`, and `docs`.
A project with none of these can show an emoji instead: **Set icon…** in its sidebar menu opens a picker of suggested emoji
and a field for any other (`ProjectInfo.Icon`, kept in `projects.json` and sent to paired hosts with the `SetIcon` store operation).
An icon image in the folder wins over the emoji. Project names and chat titles show emoji in the emoji face.
**Ctrl+Shift+H** comes back to Home. The wordmark holds still when
Windows asks for reduced motion.

Clicking a conversation in the sidebar, or the sidebar's **New chat**, opens that
conversation's own **session**: at first just its chat, full window; after that, the
layout you left it in, with its terminals, diffs or other panels. Deleting a
conversation goes Home when its session is open and forgets the session.

Opening a conversation from Home, or from a project's session, reuses a conversation
panel rather than opening another: the one you asked for, else the active one, else an
empty one on screen, else any on screen. A panel whose agent is still working is never
switched away from; the conversation opens in a new panel in its pane, and the working
one keeps running, hidden, until you open it again.

Each project is also a session with its own saved workspaces and layout. Its first
opening shows its latest chat; after that, it reopens exactly as you
left it. Leaving a session hides its panels instead of closing them, so an agent working
in one conversation or project keeps working while you are in another. Projects are
their own module: with Conversations disabled, the sidebar (or tab strip) lists the
active profile's projects with a **+** to add one, and a project opens its session with
one empty pane, its saved workspaces listed below it.

Work and Personal each remember their own current session and selected conversation:
switching profile returns to where that profile was, or Home the first time. **Alt+1**
switches to Work and **Alt+2** to Personal; the sidebar swipes in from that profile's side.

The sidebar writes project names in Fraunces (under the SIL Open Font License, in
`Modules/Workspaces/Fonts`); its other lines keep the app's font. A project's fold arrow sits in the left margin
and shows while you point at the project, and always while it is folded. A row's small buttons
(a project's tools and pull requests, and **…**) appear over the row's right end while you point
at it; they take no space otherwise, so titles keep the full width and nothing moves.

### Project overview

Hover a project's heading in the sidebar and click its overview button (four squares), or
choose **Overview** in its **…** menu, to open one page for the whole project. The page is
its own session, like a chat's: it fills the window, and the layout you leave it in comes back
next time. The palette's **Project overview** opens the page of the project you are in, or a
list of projects to choose from. Under the project's name, folder and branch:

- **Prompt bar**: type and press Enter to start a chat in the project with what you typed, or
  Ctrl+Enter to make a task named after it.
- **Needs you**: the chats that wait for you, then those whose agent works.
- **Recent**: the three chats or tasks used last; **All N chats and tasks** shows the rest.
- **Designs and reports**, on the right: the designs in the project's `.lokn\design` folder,
  which open in the Design studio, and the html reports its agents showed with `browser_show`,
  which open in the browser. Reports show from Loom 0.4.13 on; earlier ones do not.
- **GitHub**, under them, when the project's `origin` remote is on GitHub: the newest comments,
  reviews, checks and merges of that repository from the notification feed, unread ones in
  blue, and **Open pull requests**. GitHub posts only what changes while Loom runs.

A paired host's project shows its chats, tasks and designs. Its designs do not open, and its
reports stay on that PC.

## Workspace

- **Ctrl+\\** and **Ctrl+Shift+\\** split the active pane right or down into an empty
  neighboring pane; the palette's **Open** commands open panels.
- Every panel occupies its own visible tile. Opening a panel fills the active empty
  pane or splits an occupied pane to the right.
- Panels have no title bar. Hovering the top of a pane reveals a dotted grip.
- Drag the grip near an edge of another pane to dock; drop in the center to swap panels.
- Right-click the grip (or focus it and press Enter) for the pane's actions: open a panel, split right or down, close.
- Drag dividers to resize. Focus a divider with Tab and use its arrow keys to resize.
- Close panes from the grip menu or with **Ctrl+W**. Empty source panes collapse automatically.
- The titlebar **search** icon, **Ctrl+K**, or **Ctrl+Shift+P** opens **Search**: tasks, projects, chats,
  open panels, commands (Actions) and settings, with a tab per type. With nothing typed it shows Recents
  and the first items of each type. Up/Down select, Enter runs, Alt+Enter opens the item's other actions,
  Left/Right at either end of the query (or Ctrl+Left/Right) change tab, `/tasks ` filters to a type,
  and Escape closes.
- Run **Open Modules** to disable or re-enable optional features at runtime.
- **Settings › Modules › Community** lists modules other people wrote, from the
  [community hub](https://github.com/antopilo/loom-modules). Search them, then **Install**,
  **Update** or **Remove** one; it loads at once. Each one is reviewed before it joins the hub, but
  it runs with your rights, so install only what you trust. Installed ones update automatically
  unless you turn off **Update community modules automatically** below the list.

Panel models and their nested Goo Cells remain mounted across moves and swaps.
There are no tab stacks. Closing a panel or unloading its module cancels its
lifetime and removes its view.

### Saved workspaces

**Open Workspaces** shows the current session's saved workspaces as a grid of tiles, each
with a miniature of its layout; dock it in the sidebar. Click a tile to switch: panels both
layouts share slide to their new place, panels the new one adds fade in, and panels it
leaves out are hidden, not closed, so chats and terminals keep running and come back
as they were. The **+** tile saves the current layout as a new workspace and names
it. Double-click or F2 renames a tile, right-click renames or deletes it, and
dragging tiles reorders them. **Ctrl+Alt+Right/Left** switch to the next or previous
workspace. The current workspace follows your changes and is kept as you leave it, by
switching workspace or session. Sessions and their workspaces save in
`%LOCALAPPDATA%\Lokn\workspaces.json`; a file from before sessions opens as the Chats
session. Agents can list, switch and save the current session's workspaces with the
`workspaces_*` tools. Hidden panels last until Loom closes; a workspace's panels reopen
from their saved state after a restart.

## Settings and profiles

The titlebar switch selects **Work** or **Personal**. The gear icon opens **Settings**
as a normal workspace panel, with the titlebar, sidebar, and pane navigation still
available. **Open Settings** in the command palette opens the same panel.

**Window backdrop** under Settings › Shell › Appearance chooses an opaque window,
wallpaper-based **Mica**, or desktop-blurring **Acrylic**. Mica is the default.
The titlebar, sidebar, status bar, and space around opaque content panels reveal the
material; unsupported system compositors use the theme's normal canvas color.

Choose **Global defaults**, **Work**, or **Personal** in the editor. Values resolve
from the selected profile's override, then global defaults, then the module's
built-in default. **Override** creates an explicit profile value; **Use global**
removes it so later global changes flow through. **Reset** restores a global value
to its built-in default. Text and number edits save with Enter; Boolean changes
apply immediately.

Profiles apply live to existing panels without changing their layout or state.
The initial settings cover the status bar, pane animation duration, and Welcome
heading. Modules can register their own typed settings in the same editor.

Settings and the active profile persist in `%LOCALAPPDATA%\Lokn\settings.json`.
A fresh install starts with Personal and inherited defaults. Writes use a sibling
temporary file before replacing the saved file. A malformed file remains untouched;
Loom reports the error and uses session-only settings until the file is corrected
and the app restarts.

## Worktrees

Run **Open Worktrees** from the palette to manage the git working trees and branches of a
project's repository. Choose one of the active profile's projects, or **Workspace**
for Loom's own working directory. Each working tree shows its branch, directory, checked-out
folders, native build, and disk usage; the main working tree's size includes the shared `.git`
directory, and shared folders and shared native builds are not counted.

Type a branch and choose **Create worktree** to check it out in
`<repository>.worktrees\<branch>` beside the repository, or use **Create worktree** on a
branch in the list. A local branch is reused, a branch only a remote has becomes a local
branch tracking it, and any other name creates a new branch from the start point (default:
the current commit). With **Folders**, the working tree is a sparse checkout of those folders
and the top-level files, so large folders it does not need are never written.

**Setup** saves per repository, in `%LOCALAPPDATA%\Lokn\worktrees.json`:

- **Default folders** for new working trees.
- **Shared folders**: ignored folders, such as download or shader caches, that every new working
  tree links to the main checkout's copy with a junction. A folder git tracks files in is
  refused, because git deletes through a link to such a folder.
- **Native input paths** and the **native output folder**: a working tree whose native input paths
  hold exactly the same files as a recorded build gets that build through hard links, and builds
  managed code only. For s&box these are `src, engine/Definitions, engine/Tools/SboxBuild,
  engine/Tools/InteropGen, engine/manifest.def` (the paths SboxBuild's `NativeInputsMatch` compares)
  and `game/bin/win64`.

**Record native build** marks a checkout's native output as built from its HEAD. Loom refuses when
native sources have uncommitted changes, or when its HEAD reflog shows the native sources changed
between the last native build and now. A recorded build stops being shared as soon as any of its
files changes. A native build in a linked working tree would rewrite the shared files in place, so
choose **Stop sharing native** there first.

**Remove** asks for a confirming second click. It refuses a working tree with changes, then removes
its folder links before git deletes the directory, so the shared folders and builds stay intact.
Chats working in it move back to the main checkout. Branches keep their commits; **Delete** on a
branch asks for a second click and git refuses unmerged work. **Fetch** fetches every remote.

Agents move into worktrees themselves. Ask a chat to work in a worktree, on a branch in parallel,
or alongside other agents, and it calls `worktrees_enter`: Loom creates the branch's worktree with
the setup above, or reuses the worktree that has the branch, and moves the chat there. The agent
works there for the rest of its turn, and its next turn starts Omp in the worktree with the same
session. `main: true` moves it back. Without that request a project chat works in the project's
directory.

A chat also follows its agent's shell. When most of a turn's bash commands ran in another worktree
of the project's repository, for example after the agent ran `git worktree add` and `cd` itself,
the chat moves there at the end of the turn: the sidebar lists it under that worktree, and its next
turn starts Omp there. A worktree that git removes outside Loom leaves the sidebar, and its chats
move back to the main checkout.

## Themes

**Settings → Theme** shows a card for Loom's own theme and every theme from Tern,
light ones first. Hovering a card previews it across the whole window; clicking
picks it for the scope being edited, so Work and Personal can each have their own.
Tokens in `%LOCALAPPDATA%\Lokn\theme.json` still override the picked theme; **Theme:
edit theme file** creates it with every token commented out.

`Shell/Theming/TernThemes.json` is Tern's theme catalog, copied unchanged from the
JSON embedded in `tern.exe`; replace it to pick up new Tern themes.

## Browser

**Browser: open URL** opens a browser panel with its address bar focused; **Open
Browser** opens the home page. Type an address, a host such as `github.com` or
`localhost:5173`, a file path, or words to search for. Pages use the Microsoft Edge
WebView2 runtime that ships with Windows 11, with cookies and history kept per settings
profile in `%LOCALAPPDATA%\Lokn\browser`. A browser panel holds tabs: **+** opens
the home page in a new tab with the address bar focused, **×** or a middle click
closes a tab, and closing the last tab closes the panel. Links that open a new window
open a new tab, the ↗ button opens the page in your default browser, and every tab
reopens after a restart. Native pages hide while the command palette or a notification
overlay is open, so those overlays remain in front. **Browser home page** and **Browser
search** are under Settings → Browser.

Web links followed in a conversation or the GitHub panel open in a new, selected tab
of the browser panel on screen, which takes focus: the one earlier links went to, or
else the first in the layout. With no browser panel on screen, the link opens a new
one beside the conversation. Without the Browser module, links open in your default
browser.

Agents can show you pages too: the `browser_show` tool opens an HTML report the agent
writes, such as benchmark charts, a plan or an interactive demo, or a local `.html`
file or web address, in a browser tab beside the conversation; later reports replace
the page in that tab while it stays open. Reports are
saved in `%LOCALAPPDATA%\Lokn\reports`, so they reopen after a restart and can be
shared. They can run JavaScript and load scripts from CDNs.

Agents can also use the browser panel as you would. `browser_tabs` lists the panels and
tabs; `browser_navigate` loads an address (opening a panel beside the conversation when
none is on screen), goes back or forward, or reloads, and waits for the page;
`browser_read` returns the page's text and its links, buttons and fields, numbered;
`browser_act` clicks, types, presses keys, chooses options, hovers and scrolls with real
mouse and keyboard input; `browser_eval` runs JavaScript; and `browser_screenshot` saves
a PNG to `%LOCALAPPDATA%\Lokn\screenshots` for the agent to look at. Without a tab id they
act on the tab shown in the browser panel on screen, and select the tab they use so you
see it. Pages keep the profile's cookies and sign-ins. Pages of a browser panel that is
not on screen do not run, so agents must bring it on screen first.

While a page has the keyboard, Ctrl+K, Ctrl+Shift+P and Loom's shortcuts such as
Ctrl+W still go to Loom, Ctrl+L or F6 focuses the address bar, and Escape goes to the
page. The page is a native window placed over its panel: menus and tooltips open as
native popups above it, the palette and dialogs hide it while open, and panels cannot
be dropped onto it while docking.

A page with `<meta name="loom-chrome" content="none">` shows without the address bar, for
tools that bring their own toolbar, such as the Design studio. The tab strip stays while
the panel has more than one tab.

## Design

Ask an agent to design a screen, and it writes the design as plain HTML and CSS in your
project, in `.lokn/design/<name>/`. The design's **studio** opens in Loom's browser beside
the chat as soon as the agent calls `design_open` or starts to write in that folder, and
swaps in each save without a flash and at the same scroll position, so you watch the
design take shape. The chip in the toolbar shows **Working…** while the agent works. The
studio fills its panel without the address bar and uses Loom's theme. **Share** opens a
small dialog with the studio's address selected and a **Copy** button.

- **Variants.** One design can have several variants, one folder each (`a/`, `b/`, `c/`).
  Switch with the toolbar or `[` and `]`; **Compare** (V) shows them side by side.
- **Screens.** **Fit**, **Desktop** (1440 × 900), **Tablet** (768 × 1024) and **Mobile**
  (390 × 844), or keys 1 to 4. Tablet and phone show in a device frame; a screen larger
  than the panel is scaled down, and the toolbar shows the zoom.
- **Comments.** Press C (or **Comment**) and point at the design: the element under the
  pointer is outlined with its name and size. Click it and write what must change.
  **Add & send** (Ctrl+Enter) sends it to the chat at once; **Add** keeps it. The
  **Comments** panel (`\`) lists the threads with numbered pins: hover one to find its
  element, resolve or reopen it, send one, or **Send N comments to chat**. The chat gets a
  message with each comment's file, selector, text, and the element's markup and main
  styles, so the agent edits it without reading the page first: a new turn when the agent
  is idle, else after its current turn. The agent marks each done with `design_resolve`.
- **Export.** The **⋯** menu downloads a variant as a zip archive, opens it alone, or
  copies the studio's address. Agents use `design_export` for the same archive.

Agents look at a design in text first. `design_check` reports, with each element's
selector, what overflows the screen, clipped text, low contrast, broken images, missing
alt text, small tap targets on phones, and how many font sizes and text colors the page
uses: a few hundred tokens. `design_snapshot` takes the first screen by default, one
element with `selector`, or the whole page with `fullPage`; `browser_screenshot` also
takes a `selector` to capture one element.

Loom serves the studio on `localhost` port 47831 of the PC whose chat it is, and every studio address names that port, so
studio tabs keep working when Loom or its daemon restarts. Of this PC's Loom processes, the one that runs the chats owns the
port: the daemon while it runs, else the window. Each checks every second, so the window hands the port to the daemon it
starts and takes it back when the daemon stops, and the owner serves any design either process opened (both read
`designs.json`). Only when another program holds 47831 does Loom serve on a free port instead. For a paired host's chat,
the host serves it, and this PC's browser reaches it through the localhost forwarding. The
server answers only `localhost` addresses, and only the studio can change comments. A
studio left open from an earlier session shows its design again at once, and waits only for a design no Loom knows yet.

## Terminal

**Open Terminal** runs a shell in a Windows pseudoconsole (ConPTY) inside a panel,
as Windows Terminal does. The xterm-compatible emulator supports 256 and 24-bit
color, the alternate screen used by full-screen programs, pointer reporting,
bracketed paste, focus reports and 10,000 lines of scrollback. **Terminal: new
PowerShell**, **Windows PowerShell**, **Command Prompt**, **Git Bash** and **WSL**
appear for the shells installed. The **Terminal shell** setting runs any other
command line, such as `nu`, `wsl -d Ubuntu` or `ssh host`; blank picks the first
installed of PowerShell, Windows PowerShell and Command Prompt.

While a terminal has focus it receives every key, including Ctrl+W, Ctrl+K and
Ctrl+\\; Ctrl+Shift shortcuts such as Ctrl+Shift+P stay with Loom. Drag to select
(double-click for a word, triple-click for a line, Shift to select over programs that
track the pointer). Ctrl+C copies a selection and otherwise interrupts; Ctrl+Shift+C
copies, Ctrl+V and Ctrl+Shift+V paste, right-click copies or pastes, Shift+PageUp and
Shift+PageDown scroll. A shell that exits with code 0 closes its panel; any other
exit leaves the output and Enter restarts it. Terminals reopen after a restart in
the directory the shell last reported (OSC 7 or OSC 9;9). ANSI colors are the
`Ansi*` tokens in `theme.json`.

## Conversations and accounts

The Conversations panel groups tasks under projects, with a working directory for
each project. A task is one entry with its own workspace and layout; a conversation is
simply a task with a chat panel, and the sidebar shows both alike. Use **+ Project**,
the **+** beside a project to start a task with a chat, or **New chat** for an ungrouped
conversation. Rest the pointer on a project's **+** for a small grid of icons, one per
thing a new task can start with: **Chat**, **Terminal**, **Git**, **Files**, **Browser**,
and whatever else loaded modules offer, then **Empty task**. Each starts a task in the
project with that panel filling its workspace; the grid follows the modules you have on.
A task without a chat opens as one empty pane: fill it with other panels, such as notes,
the notification feed or a browser, and they come back each time you open the task. **New task** in the
palette does the same in the current project, and the task's **…** menu offers
**Start a chat here**, which adds the chat beside its panels. Typing into an empty
Conversation panel inside a task does the same; in a task that already has its chat, it
starts a new task in the same project.

A dot after a chat's title shows its agent's state: a pulsing dot in the accent color
while it works, and a steady dot in the attention color once it finished or asked
something and waits for you, until you look at its panel.

Right-click a task or project, or click its **…**, for its menu; each action shows the
letter that chooses it, and Up/Down with Enter work too. Rename a task by
double-clicking its title, pressing F2 on it, or choosing **Rename** (R) in its menu;
Enter or clicking away saves and Escape cancels. A project renames the same way on its
heading. **New project** opens the folder chooser, adds the folder as a project named after
it, and starts renaming it; a folder that already is a project opens that project. A project's
menu starts a chat or task in it, renames it, picks its folder again with **Change folder…**,
and removes it; removing a project retains its tasks.

Once a project's chats work in more than one checkout, the sidebar lists them under a heading
per worktree, named by its branch, with the main checkout's chats last; chats that all work in
the project's directory stay one flat list.

Hover a chat and click its pin to keep it at the top of its project, or of the chats
outside projects; pinned chats show the pin and click again to unpin. Drag a task onto
another of its list to move it there; a line shows where it lands. Tasks move among
those pinned as they are, and new ones start at the top. A new chat is
titled with the first line of its prompt, then, after its first turn, Loom asks Omp's
small-model role for a short title. The isolated call receives only the first and
latest user prompts, each bounded to 2,000 UTF-16 code units; it receives no session
context, tools, skills or project rules. After every successful automatic or
`/compact` compaction, the same bounded call names the chat for its current task.
With **Rename chats as their task changes** on (Settings › Conversations, on by default), every 3 new prompts the chat
asks again: the call gets the current title and the latest 3 prompts, bounded the same way, and keeps the title when
it still fits. `/clear` counts the prompts from none again.
A title you set yourself, by renaming the task or with `/rename <title>` in the chat,
is never replaced, not even by a compaction.

A thin line along the top of the prompt shows how full the chat's context is, as in
Tern: the filled part is the context used, with its share at its end, and the window's
size sits at the right. A mark shows where Omp compacts automatically, from the
profile's `compaction.*` settings and the model's override in `compaction.modelThresholds`
(85% of the window by default); past it, and while compacting, the line turns to the
attention color. Point at it for a preview, or click it to pin the token and cost
panel. The panel shows context counts, model list prices, reasoning effort, observed
compactions and auxiliary title calls. It updates after each turn, model switch and
compaction.

To empty a chat in place, type `/clear` in its prompt or run **Clear conversation**
from the palette while the chat is active. Omp starts a fresh session, so the agent
keeps none of the earlier context, and the transcript empties; the panel, project and
model stay. An empty chat, new or cleared, looks like Home: the wordmark and the
recent project cards above the chat's own prompt. It is unavailable while the agent
is working.

Omp runs `/plan` and `/goal` only in its terminal interface, so Loom runs them itself:

- `/plan <task>` asks the agent for a plan, as Codex's plan mode does: Loom sends the task
  with instructions to change nothing, explore, ask its questions, and end with the plan
  between `<lokn-plan>` tags. The prompt shows as the task with a **Plan** label, and the
  plan as a card in the reply. Once the plan is finished, a card above the prompt offers
  **Implement**, which sends the approval, and **Revise**, which starts a `/plan` prompt for
  your notes. Nothing blocks edits: the agent keeps to the plan by instruction only.
- `/goal <objective>` (or `/goal set <objective>`) gives the chat a goal the agent works on,
  turn after turn, until it is done; a new objective replaces the goal. `/goal` or `/goal show`
  shows it, `/goal pause`, `/goal resume` and `/goal drop` act on it, also while the agent
  works. A line above the prompt shows the goal, its status and the tokens it used, with
  **Pause** or **Resume** and **Drop**.

Work and Personal have separate history and Omp account profiles. An existing chat
keeps its original profile when the profile switcher changes. Open **Omp accounts**
from the command palette to sign in. First-time sign-in opens Omp's interactive
login; after login, configure a provider/model in the Settings editor if that Omp
profile has no default. Credentials stay in Omp's own credential store.

Click the model name under any chat's prompt to switch models: a searchable picker
lists recent models, then every model the account can use, with context window and
price per million tokens. **Use for this chat** switches that chat's session; **Make
default** sets the model for new chats of its profile. Up/Down, Enter and Escape work
from the search box.

**Model routing** in the picker edits Omp's primary model and fallback chain for each
role, default reasoning level, and named presets. The Work and Personal Omp profiles
stay independent. Loom reads and writes Omp's configuration directly; it does not
maintain another router. **Agents** on the same rail opens a native Agent Hub beside
the chat with live subagent task, tool, model, token, context, cost and transcript
state, plus steering and stop controls.

**Usage** on the same prompt rail opens that chat profile's provider accounts, limit
bars, reset times and saved resets without sending an agent turn. After it loads, the
button shows the connected provider's most constrained limit as a percentage left.
Usage is read only when you open or refresh it; ordinary chat rendering makes no
provider usage request. **Omp usage** in the command palette opens the same data in a
full panel.

Open **Omp setup** from the palette, or **Accounts and setup** in the picker. It finds
Omp itself (Tern's bundled copy, or `omp` on PATH) and every Omp profile on this
machine with what each is signed in to and its default model. A card for Work and one
for Personal show the account and model each uses, with **Change** and **Sign in**
where something needs doing; choices save at once. Details are in
[Omp setup](omp-backend.md).

In Settings, configure **Omp executable**, **Omp account profile**, **Omp model**, and
**Economy mode** in Global defaults or either profile. Economy mode starts a new chat
at `low` reasoning effort, falling back to `minimal` and then `off` when needed, and
loads extra Loom tool groups only when the agent asks for them. A blank account profile uses
`work` or `personal`; a blank model uses Omp's configured default. Loom finds Tern's
bundled `omp.exe` automatically when installed, otherwise uses `omp` on PATH.

Each conversation has a single-line prompt that grows with its content: Enter sends,
Shift+Enter adds a line, and **Stop** interrupts the running turn. While the agent works,
a **Queue** / **Steer** switch shows beside the send button. On **Queue**, Enter (or the
send button) queues the message for after the turn; on **Steer**, it steers the running
turn, and the agent reads it at its next step. Ctrl+Enter does the other one. Waiting
messages show at the top of the prompt box, steering first, from Omp's `queue_update`:
**Steer now** moves a queued message to steering, and **×** takes a message back into an
empty prompt. Steering the model already received shows as **Steered** and cannot be
taken back. **+**, Ctrl+V and
drag-and-drop attach files and images. Merely opening history does not start an agent
or invoke a model. **Connect** resumes the backend session without submitting a prompt.
A chat whose agent was working when its module reloaded reopens, reconnects once the
old Omp process has exited, and sends a short prompt asking the agent to continue
where it left off. Closing a chat or restarting Loom never sends one.

Rich messages support Markdown headings/emphasis, lists/checklists, tables, code
and diffs, images, reasoning folds, task phases, tool status/output, and native
select, confirmation, input, editor, questionnaire, and form dialogs. Unknown structured
content remains available in a readable JSON fold.

A tool's question shows above the prompt as a card: the question, **×** to cancel it,
and its answers as a row of buttons, one under another when they have descriptions.
The recommended answer (Confirm, the option the agent recommends or checked, a list's
first-focused option, or Submit) is filled and shows **Ctrl ⏎**: Ctrl+Enter gives it while
the prompt is empty or the card has focus. A lone question with one choice is answered by
a click, and an answer of your own typed in the prompt replaces the choice; questions with
several answers, or several questions, are picked and then submitted.

Omp's `ask` tool sends an HTML `<form>` (`method: "form"`). Loom reads it the way Omp's
terminal does (`HtmlForm`): sections from `<fieldset>`/`<legend>`, prose, lists, tables
and `<pre>` as Markdown, radio and checkbox groups and `<select>` as buttons, an **Other**
box for a radio or checkbox group of two or more options that has no "Other" option, text
inputs and text areas, and `<img>` (data URI or a file relative to the chat's folder; a web
image is a link) and `<svg>` as pictures. Submit checks `required`, numbers, `min`/`max`,
lengths, email, URL and `pattern` with Omp's messages before it sends `data`, since Omp
refuses a form answer that breaks them. A file image on a host's disk shows as its
description in a client.

When a chat finishes its turn or asks you a question, its pane gets a calm blue
outline until you click into it. If you are in another pane, a soft two-note chime
plays; if Loom is in the background, a silent Windows notification also appears, and
clicking it brings Loom forward on that chat. Nothing plays for the chat you are
already looking at. **Chime when a panel needs you** and **Notify when a panel needs
you** under Settings › Notifications (the Notifications module's tab) turn each off. Windows only shows notifications
for apps in the Start menu, so Loom keeps a `Loom` shortcut there and a per-user
`lokn-notification:` protocol for the click, both pointing at the executable you
last ran. Loom runs as a single instance: launching it again brings the open window
forward.

Each of those requests is also kept in the notification feed. **Notifications**, under
**New chat** in the sidebar, shows how many are unread and opens the feed in its own
workspace, full window, read like a conversation: the chat's centered column and text,
oldest at the top and newest at the bottom where it opens, and a separator for each day.
A notification with something to read, such as an agent's closing message, shows like a
reply: its source's icon (an agent, GitHub's mark, a terminal, the browser, or a bell)
and name, then its text as Markdown. Short updates from one source in a row, such as
GitHub checks or chats finishing, fold into one summary line like a chat's tool calls,
for example "2 checks passed, 1 check failed", whose arrow opens it onto each update.
Hovering a line shows its time in the left margin and its remove button at the right
end. Unread notifications have a dot, and **Mark all read** and **Clear all** sit under
the feed. Clicking a notification marks it read and opens its conversation's workspace,
its panel, or its link. The bell at the right of the title bar opens the same feed in a
popover, with **Mark all read** and a button to the full feed in its header.
Each new notification also shows briefly in a banner under the bell: its source's icon
and name, which open it, the first line of its message, and a close button. A banner
leaves after a few seconds, later while the pointer is on it, as soon as you look at
its panel, or when a newer notification from the same chat or source replaces it; at
most three show at once, and opening the popover closes them.
While an agent waits on a question, such as a choice, a confirmation or typed text, the
feed, the popover and the chat's banner show that question under the chat's name, with
the same answers the chat offers, so you can answer without leaving where you are. A
banner holding a question stays until the question is answered or withdrawn. Answering,
here or in the chat, marks that chat's notifications read; the chat's name opens it for context.
Looking at a chat marks its notifications read. Work and Personal
share one feed, kept in `%LOCALAPPDATA%\Lokn\notifications.json` with the newest 200.

GitHub updates arrive in the same feed under **GitHub**, whether or not the Pull
Requests panel is open: checks passing or failing on your pull requests, approvals and
requested changes, new comments and reviews, merges and closes, new review requests,
and finished workflow runs. The GitHub module polls every **Refresh interval** while
it is loaded, never prompts for sign-in in the background, and only reports changes
noticed after Loom started. A burst of updates chimes once; clicking one opens its page.

Chat keeps a minimal layout in Loom's warm charcoal palette: a centered reading
column, your turns in right-aligned bubbles, the agent's replies as plain text, and
the agent's work compacted: each run of reasoning and tool calls between its replies,
even across several messages, is one muted summary line such as "Read 2 files, edited
ShellView.cs, ran 3 commands ›", led by a spinner while it runs, a red cross if a tool
failed, or a quiet check. Opening it lists each reasoning fold and tool line: an icon
for the kind of tool, the agent's stated intent, and the command or path. Opening a
tool shows its script, command or written file in syntax colors, cut after eight lines
with a fade and a "N more lines" fold, then its output the same way.

Todo lists stay out of the summary as their own card: the overall count, a progress
ring per phase, and a mark per task (an empty ring, an accent ring while in progress,
a checked disc when done). A finished phase folds to its header. Markdown checklists
use the same marks, and fenced code is colored for C-family languages, Python, shell
and JSON from the theme's ANSI colors, with a language badge. A floating ↓ returns to
the newest message after you scroll up. The line under the prompt shows the project,
model and profile; the model opens Omp setup. A ring beside the model fills with the
thinking effort; pointing at it names the effort, and clicking steps to the next one.
Code and tool commands remain monospace.
Process pipes explicitly use UTF-8, including emoji and non-ASCII punctuation; terminal
color/link escapes are removed from tool output before native rendering.

File changes show as diff cards under their tool line, inside the opened summary: Omp's `edit` results (one card
per changed file) and `write` calls (the written content as added lines). Each card
has the file name, its folder relative to the project, `+added −removed` counts and
the first lines with line numbers, a change bar and green/red tints; unchanged lines
between hunks collapse to one `⋯ N unchanged lines` row. Clicking a card opens the
project's **Git** panel beside the chat on that file's diff. Later clicks reuse
that panel.

The **Git** panel (palette: Open Git) has two modes:

- **Graph**: the commit table (Branch / Tag, Graph, Commit message, Date, SHA) under a
  pinned **Working tree** row. Beside it shows the working tree (unstaged and staged
  folder trees with checkboxes and the commit box) or the selected commit (its message,
  people, SHA, parents and changed files as a tree or a list).
- **File**: clicking a changed file, in the working tree, in a commit, or in the Files
  side view's Changes mode, turns the panel into that file's diff: its path and counts,
  **Edit**, **Stage file** or **Unstage file**, **Unstaged** / **Staged**, **Wrap**,
  **Unified** / **Split**, and hunk navigation. Each hunk of a modified working-tree file
  has **Stage hunk** and **Discard** (a second click confirms), or **Unstage hunk**. ✕ or
  Esc returns to the graph. A commit's file is read-only.

The file mode also adds line review notes: click the `+` beside a line, write the
reviewer focus, then choose **Review**. Loom starts Omp's `/review` workflow in that
exact repository. Its final verdict and P0-P3 findings return to the Git panel, where
findings appear below the toolbar and on their affected diff lines.

The transcript and the Git panel's lists are virtual lists (`Core/MeasuredRows.cs`):
only rows within about one screen of the viewport are built and laid out, each row's
measured height replaces an estimate, and unchanged rows are not rebuilt. Streaming
into or resizing a long chat or diff costs what is on screen, not the whole content.
Rows far off screen are not in the accessibility tree, and text selection stays within
one message.

Loom uses Omp's structured `rpc-ui` interface for streaming messages, tool calls,
and native dialogs. The [Tern Surface Protocol](https://docs.stencil.so/tern/protocol/index.md)
is a terminal UI transport, not an account or chat API. Omp's RPC does not emit TSP
surface trees; Loom renders its structured events with Goo. This is not a complete
Tern terminal or stylesheet implementation.

The agent in a chat can also use Loom itself. Loom offers it tools over Omp's host-tool
protocol: it can see the window's panels and layout, open, focus, move and close
panels, and run palette commands. Modules add their own tools with
`ModuleBuilder.Tool`. With **Economy mode**, workspace control and worktree entry stay
available, while `lokn_load_tools` adds Browser, Tracy and other module groups to that
chat only when needed. Turn off **Let agents control Loom** in Settings to withdraw
them from every chat. See [Omp backend](omp-backend.md#lokn-tools-for-agents).

Projects save in `%LOCALAPPDATA%\Lokn\projects.json` (imported once from
`conversations.json`, which held them before). Conversation metadata saves in `%LOCALAPPDATA%\Lokn\conversations.json`, and each
chat's cached rich messages in `%LOCALAPPDATA%\Lokn\conversations\<chat>.json`, read
when the chat first opens. Saves are written in the background a moment after a change;
history in the older single-file format moves into this layout on first start. Omp session files remain in per-conversation
directories below `%LOCALAPPDATA%\Lokn\sessions`. Layout, split ratios, focused pane,
and conversation panel identities save in `%LOCALAPPDATA%\Lokn\workspace.json`
before shutdown. Missing module panels are omitted on restore; unreadable history
and layout files remain untouched and their errors appear in the shell.

## Updates

An installed Loom looks for new versions 30 seconds after it starts and then every 6 hours,
and downloads them in the background. A new Loom shows **Restart now** in the notification
feed; module updates show **Apply now**, which reloads those modules. If you do neither, the
update applies the next time Loom starts. A module update that does not match your version of
Loom is never loaded: the previous version keeps running.

**Updates: check for updates** in the palette checks at once and says what it found. Under
Settings › Updates, **Check for updates automatically** turns the background checks off, and
**Update source** picks where updates come from: a GitHub repository, a web address or a
folder. A Loom run from a build takes no updates.
