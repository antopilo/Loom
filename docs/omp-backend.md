# Omp backend

Lokn hosts the installed Omp executable over its documented JSONL RPC protocol using `--mode rpc-ui`. Structured assistant messages, tool lifecycle/progress, and tool dialogs are exposed to the chat module. This is not a Tern terminal emulator: Tern Surface Protocol (TSP) is a separate in-band terminal UI protocol, not an account or chat service. Omp's RPC does not emit TSP node trees.

## Account profiles

Open **Omp setup** from the command palette, or **Accounts and setup** in a chat's
model picker. Nothing has to be typed: setup detects and shows

- **the Omp install**: the `omp` setting is detected each time it runs, in this order:
  Tern's bundled `%LOCALAPPDATA%\Tern\omp\bin\omp.exe`, `%PI_INSTALL_DIR%\omp.exe`,
  the official installer's `%LOCALAPPDATA%\omp\omp.exe`, bun's
  `%USERPROFILE%\.bun\bin\omp.exe`, then PATH (re-read from the registry, so an Omp
  installed after Lokn started is found), with the version from `omp --version`. When
  none is found, **Install Omp** runs the official installer
  (`irm https://omp.sh/install.ps1 | iex`), shows its progress and detects again; the
  Get started panel offers the same install. **Choose omp.exe** / **Use another
  omp.exe** takes a path, which must exist as typed; when a saved path stops working,
  **Use detected Omp** goes back to the detected one.
- **every Omp profile**: Omp's `default`, each folder in `~/.omp/profiles`, and the
  `work` and `personal` profiles Lokn's chats use by default. For each,
  `omp --profile <name> models --json` shows the providers it is signed in to and how
  many models it can use, and `omp --profile <name> config get modelRoles --json`
  shows its own default model.

A card per Lokn profile, Work and Personal, shows the Omp **Account** it uses and the
**Model** its chats start with, each with a status dot. **Change** on the account lists
every profile with its sign-in state; picking one saves it for that Lokn profile only.
A new name creates a profile, and Omp creates its folder at the first sign-in that
follows. When the account is not signed in but another Omp profile is, **Use <name>**
saves that one for the Lokn profile. **Sign in** opens the Sign in page (below) and
detects again once it signs in or closes. The model is either chosen in Lokn, this account's own Omp default, or
left to Omp; **Change** opens the model picker, and **Use Omp's default** drops
Lokn's choice. A chosen model the account cannot use is flagged. Choices save at
once to the Work or Personal settings layer; **Detect again** rereads everything.

Get started's **Sign in** item does the same with one click. When the active profile's
Omp account is not signed in but another is (Omp's `default` first, else the first signed
in), it says so, such as `Your Omp account "default" is signed in (anthropic · 21 models).
Sign in will use it for Work and Personal`, and **Sign in** saves that name as the account
profile of the active Lokn profile and of the other one while its setting is blank.
Otherwise **Sign in** opens the Sign in page and completes when a sign-in succeeds there
or the page closes; Get started then checks every item again.

The model picker lists recent models, then every available one with its context
window and input · output price per million tokens; type to filter, Up/Down to move,
Enter to choose, Escape to close. In a chat, the model name under the prompt opens
the same picker. **Use for this chat** sends Omp `set_model` for that chat's session,
which Omp keeps with the session; **Make default** saves the model for new chats of
that chat's profile. Recent models are remembered per profile until Lokn closes.
Credentials stay with Omp rather than in Lokn's settings file.

## Model routing

**Model routing** in the model picker's footer opens the selected Work or Personal
Omp profile's routing panel. It reads Omp's effective `modelRoles`,
`retry.fallbackChains`, `modelPresets` and `defaultThinkingLevel` through
`omp --profile <name> config get … --json`; Lokn does not keep a second routing
configuration.

Each built-in or configured role has a primary selector and an ordered,
comma-separated fallback list. A selector can include Omp's thinking suffix, such
as `openai/gpt-5.6:low`, or a role alias such as `@smol`. Enter `[]` for an explicit
empty fallback chain; a blank field removes that role from the saved record. **Save
routing** writes both records with Omp's supported `config set` command.

Named presets capture the edited role assignments and default thinking level. Apply
writes those values to Omp; delete changes only `modelPresets`. Routing changes
affect new sessions and subsequent subagent or fallback decisions. They do not
silently switch the active model of an open chat.

## Agent Hub

**Agents** on a chat's prompt rail connects the chat when necessary and opens the
native **Omp Agent Hub** beside it. The panel subscribes with
`set_subagent_subscription: events` and reads `get_subagents`; it shows each
subagent's status, task, activity, current tool, resolved model, tokens, context,
cost, duration and persisted transcript. Selecting an agent reads its transcript
with `get_subagent_messages`.

**Send** uses `steer_subagent`; **Stop** uses `cancel_subagent` and does not abort
the parent turn. Omp RPC does not expose revival of parked or aborted agents, so
Lokn states that limit instead of simulating revival with a new model turn. Omp's
terminal Agent Hub or `agent://` messaging can revive them.

Focused configuration checks use:

```powershell
dotnet run --project tests/Lokn.Tests -c Release -- --omp-setup-only --ui
```

These use a fixture executable that answers `--version`, `models --json` and
`config get modelRoles`, and a temporary profiles folder; they never log in or send
a model prompt. Start Lokn with `--omp-setup` to open setup directly.

**Sign in** (the `omp.accounts` panel, also **Omp accounts** in the command palette) signs
one Lokn profile's Omp account in to a provider. It starts Omp at once with
`--mode rpc-ui --profile <name> --no-session --model anthropic/claude-sonnet-4-5` and
lists `get_login_providers`, each with a **Signed in** tag and **Connect** or **Add
account**. Omp refuses to start RPC for a profile without a default model (`No default
model selected`), which every profile has until it signs in; any built-in model lets it
start, and the session never sends a prompt. **Connect** sends `login`; when Omp's
`open_url` arrives the browser opens straight away, since the user just asked for it, and
the page shows the address with **Open page again**. Omp's input, confirm and select
dialogs are answered in the page. A successful `login` shows it and completes whoever
waits for the page (`OmpModule.SignInAsync`).

RPC rejects secret prompts such as API keys, so those providers, and any failed login or
start, offer **Sign in in terminal**: it runs `"<omp.exe>" --profile <name> login` in a
Loom terminal through `ITerminalLauncher`, or in Omp's own console window when the
Terminal module is not loaded. **Check again** (automatic after the console window closes)
reconnects and counts a signed-in provider as finished.

Blank **Account profile** settings map to the named Omp profiles `work` and `personal`;
set a name in the Work or Personal settings scope to reuse another Omp profile. Omp owns
credentials; Lokn never copies tokens into its settings or conversation files. Omp's
normal environment/config credential resolution still applies.

Already-open sessions retain their captured profile. Switching profiles never redirects an in-progress conversation to another subscription. Each conversation gets a separate session directory, continued using `--session-dir <directory> --continue`.

## MCP servers

Open **Omp MCP servers** from the command palette. Omp owns MCP; the panel edits the
same files Omp's `/mcp add`, `enable` and `disable` write, and Lokn keeps no copy:

- **Work** / **Personal**: the user file of that profile's Omp profile,
  `~/.omp/profiles/<name>/agent/mcp.json`, or `~/.omp/agent/mcp.json` for `default`.
- **Project**: `<folder>\.omp\mcp.json`, used by every profile in that folder.

Each server is edited as its JSON definition, so every Omp field works. Saving checks
Omp's rules (name characters, `command` or `url`, known `type`), keeps unknown fields
and the server's place, and replaces the file in one step. Disable sets `enabled: false`;
Enable removes it and any `disabledServers` entry. Servers from other tools (Claude Code,
Cursor, …) are not listed; `/mcp list` in a chat shows all of them and their status.
New chats load changes at start; an open chat needs `/mcp reload`.

## Usage and resets

Every chat has **Usage** on the rail below its prompt. Clicking it reads
`omp --profile <name> usage --json` for that chat's captured profile and opens the
accounts, limit bars, reset times and saved resets above the button. After a read,
the rail shows the connected provider's most constrained limit as a percentage left.
Opening or pressing **Refresh** makes the explicit read; ordinary chat rendering
never asks a provider for usage. The latest successful profile report is shared with
other chats and the full **Omp usage** panel, while Omp caches its provider answers.

Open **Omp usage** from the command palette for the same report in a full panel. It
uses the active profile and refreshes every minute while open. An exhausted limit
shows in the error color. Saved limit resets are counted, and are spent with
`omp usage reset` in a terminal, never by Lokn.

When a provider fails a reply, for example with a rate limit, the chat shows the
provider's message and the time to try again in place of the empty reply.

## Folder and git line

Each chat starts Omp in one folder: its project's directory, otherwise the folder chosen
for it, otherwise the user's home folder (`ConversationStore.GetDirectory`; diffs and
tool paths use the same folder). Lokn's own directory is never a default: a chat outside
projects that ran Omp there before this default keeps it as its chosen folder. In a chat
outside projects, the rail under the prompt starts with that folder's name; a project chat
shows no folder name, since its project already names it. Then, from the Git module's watch
of the folder, come a drawn branch icon and the branch name (or the short commit when
HEAD is detached), `↑n ↓m` against the upstream when not level, and `+added −removed`
when uncommitted changes add or remove lines; clicking the changes or the branch opens the
folder's Git panel (below).
Outside a repository only the name shows. In a chat outside projects the name is a button
that opens the native folder chooser; the choice is saved with the chat. It is refused while
the agent works or connects. A connected Omp stops, and the next prompt starts it in the new
folder with the same `--session-dir`, so `--continue` keeps the conversation.

A turn that edited or wrote files ends, once it finishes, with a card: **Edited N files**,
the lines added and removed, and its first three files with their own counts; **Show N
more files** lists the rest. The counts come from the turn's successful `edit`, `write`
and `ast_edit` results, summed per file, so changes made through shell commands are not
counted, and a `write` counts every line of the file it wrote as added. **View changes**
opens the folder's Git panel beside the chat, and clicking a file opens it on that file's diff.

The transcript scrollbar carries a short line for each of your prompts, where the thumb's
top sits when that prompt is at the top of the view; the prompt being read shows in the
accent color. Clicking a line scrolls its prompt to the top. Rows that were never on
screen are placed by an estimated height until measured, so lines far from the view
can shift slightly as the transcript is read, the same as the thumb.

## Git panel

**Open Git** in the palette, the branch or changes on a chat's rail, or a file in a chat's
diff cards opens `git.panel` on a folder (`GitPanelInput`; from the palette the open project's
folder). It lives in `Modules/Git/Panel` and reads and changes the repository only through
`GitRepository`. Opening it again for a folder of the same repository brings the open panel
forward (`GitModule.OpenPanel`), on the file asked for, if any.

- **Graph mode**: up to 5,000 commits of every branch, remote, tag and HEAD in topological
  order, as a table: Branch / Tag, Graph (`GitGraph.Layout`), Commit message, Date and SHA, with
  no search (the Files side view's History mode keeps one). A branch badge folds in its remote
  branch of the same name as a globe; a branch only on a remote shows a globe and its name without
  the remote's (`ComputeRefLabels`); `+n` lists more. Right-click opens a badge's menu, double-click
  checks it out. The commit a branch or tag points at shows its author's avatar in place of its dot,
  joined to its badges by a line; every row tints the graph from its commit to the message in its
  lane's color and marks the message with a bar of that color. Avatars (`GitAvatars`) are GitHub
  account pictures for no-reply addresses and Gravatars otherwise, kept in
  `%LOCALAPPDATA%\Lokn\avatars` for a week (an address without one for a day), with initials on the
  lane color until or unless one loads; **Author pictures** (`git.avatars`, on by default) turns
  fetching off. A pinned **Working tree** row on top counts unstaged and staged files; staging and
  committing live in the Files side view's Changes mode only. Selecting a commit shows it beside the
  table: its subject, branches and tags, its author with avatar, address, date and age, when it was
  committed and by whom when that differs, its SHA, parents that select the parent, its message, and
  its files as a tree or a list. Icons are Lucide outlines drawn as vectors (`GitIcons`) with their
  names as tooltips. The graph, a file's diff and a newly selected commit fade and rise in
  (`GitReveal`); rows, buttons and avatars ease between states.
- **File mode**: clicking a file in a commit, in the Files side view's Changes mode, or in a chat's
  diff cards shows its diff (`GitFileView`): path and counts, **Edit**, **Discard** and
  **Stage file** or **Unstage file**, **Unstaged** / **Staged**, **Wrap**, **Unified** /
  **Split** with the changed part of paired lines tinted, and ↑ n/N ↓ between hunks. Each hunk
  of a modified working-tree file has **Discard** (a second click confirms) and **Stage hunk**,
  or **Unstage hunk**; added, deleted, renamed, untracked and conflicted files change as a whole.
  The diff reloads with the working changes, and moves to the other side when its side has
  nothing left. ✕ or Esc returns to the graph. A commit's file is read-only.
- **Review**: a `+` beside any working-tree diff line opens an operator note; saved notes show
  `•`. **Review** starts an exact-folder Omp chat with `/review`, includes every note and its
  anchored diff row as reviewer focus, and links the chat back to the Git panel. The requested
  final `<lokn-review>` JSON supplies verdict, confidence and P0-P3 findings. Finished findings
  appear below the toolbar and as P0-P3 markers on affected diff lines; selecting a finding
  opens its changed file. If Omp does not return a valid block, the review remains available in
  its chat and Lokn does not infer findings from prose.
- **Commands**: the toolbar (the checked-out branch or commit, then icons with their names as tooltips: Fetch, Pull with a
  menu for merge or rebase, Push, New branch, Stash, Pop, the working-tree actions modules add with a `GitAction.Icon`,
  such as Review in chat with its note count as a badge, and Reload) and right-click menus on
  commits, branches, remote branches and tags (in the Files side view's History mode, stashes
  too). Each runs through `GitContent.Run`, which runs it off the UI thread, shows its progress
  with **Stop**, disables the other commands, shows git's error until dismissed, and then reloads
  refs, log, stashes and changes. Names come from an in-panel dialog; hard reset, force push and
  every delete or drop ask first, and deleting an unmerged branch asks again before forcing it.
  A merge, rebase, cherry-pick or revert in progress shows a banner with **Continue** and **Abort**.
- The refs and log reload when the folder's watched state changes branch, commit, changed files or
  upstream distance, and after every command.

## Attachments

The chat composer attaches files with **+** (native file chooser), Ctrl+V (copied
files, or a copied image saved under `%LOCALAPPDATA%\Lokn\attachments\<profile>\<chat>`),
or by dropping files from Explorer. On send, each path is appended to the prompt as a
quoted Omp file mention, `@"C:\path\file.png"`. Omp itself reads text files, resizes
images, converts documents and samples videos, so Lokn never base64-encodes media into
the 1 MiB inbound RPC frame. A mention that no longer resolves stays plain prose.

## Slash commands

Typing `/` at the start of the prompt lists every command the connected Omp offers
(builtins, skills, extensions, custom and file commands) above the composer, filtered
as the name is typed: names or aliases starting with it first, then names containing
it. Up/Down select, Tab completes `/name `, Enter runs the selected command (or
completes it when its hint marks a required `<argument>`), Esc closes the list until
the prompt changes. The prompt is sent as typed when nothing matches.

The catalog comes from Omp's `available_commands_update` frame, pushed at startup and
whenever command metadata changes. Typing `/` in a chat that is not connected yet
connects it so the list can load; no prompt is sent.

A command Omp runs locally, such as `/session` or `/usage`, answers with a
`command_output` frame instead of a turn. The chat shows it in the transcript under the
command as typed, without terminal colors; output fenced in ```` ``` ```` renders as
Markdown. Background commands print after Omp admits the prompt: `/compact` sends
`manual_compaction_start`, during which the chat shows as working, then
`manual_compaction_end` and its result line. Omp keeps these out of its session, so they
are gone after the chat reconnects. Output Lokn did not ask for is not shown. Notices
(`notify`, `setStatus`) go to the status line. Custom messages Omp does not mark
`display: true`, such as its `<system-notice>` reminders to the model, stay out of the
transcript, as in Omp's own interface. Displayed ones are written for the model too,
wrapped in tags such as `<system-notice>`, so `ChatContent.Notices.cs` shows them as one
line with a fold and never the tags: a background job (`async-result`) as its status,
duration and label from `details.jobs`, with the agent's `<output>` or the bash output
inside; a message from another agent (`irc:incoming`) as its sender and `details.message`;
a process exit (`launch-completion`) as its text. Other types show their type name, the
text without its wrapping tag, and their details as JSON.

The thin context meter above the prompt reads `get_state` after turns, model changes
and compactions. Pointing at it previews the numbers; clicking it pins a token and
cost panel with context use, Omp's automatic-compaction threshold, reasoning effort,
the catalog's input/output list prices, an approximate next-input list cost, and
compaction and auxiliary-title-call counts observed by that panel. In Economy mode,
at 70% context use it also recommends a new chat for a different task or `/compact`
for the same task.

A chat's title starts as the first line of its first prompt. After the first turn, and
after each committed compaction, Lokn runs `OmpService.AskSmallAsync`: isolated
`omp -p --smol` with thinking, tools, skills, rules, extensions, context files, LSP,
session persistence and Omp title generation disabled. Its material contains only the
first and latest user prompts, each bounded to 2,000 UTF-16 code units. Omp's own
`/rename` generator is not used because it puts an icon and a category code such as
`FEED:` in front of a long title. The store cleans every generated title, including
one from a `/rename` the user runs without a title, and on load it cuts down titles
saved in the older form. A title the user typed is never replaced.

Before a prompt goes to a chat that already holds two prompts and whose context is at least
20% full, Lokn asks `OmpService.AskSmallAsync` whether it starts a new task, with the chat's
first and latest prompts and the new one, each bounded the same way. Before Omp reports the
context, the share is estimated from the saved text at four characters a token. While it
asks, the rail says so and the stop button cancels the check, keeping the prompt. An answer
of `NEW` shows a card above the prompt: **New chat** sends the prompt and its attachments in a
new chat of the same project, or of the same folder outside projects, and empties this
chat's prompt; **Send here** (or Enter) sends it in this chat; **Don't ask in this chat** sends
it and saves `ConversationInfo.IsTaskCheckOff`. Any other answer, a failure, or no answer
within 10 seconds sends the prompt here. Slash commands, attachment-only prompts and chats of
the inactive profile are never checked. **Suggest a new chat for a new task**
(`conversations.new-task-check`, on by default, global or per Work/Personal profile) turns
the check off.

The broom beside a project's name, or **Tidy chats** in its menu, tidies that project's chats
after the user confirms what it will do in a dialog in the middle of the window. It removes the
chats that were never started or got no message for 14 days, unless they are pinned, named by the
user, hold a draft, or are open, selected or working; the dialog lists them first. It then names every other started chat the
user did not name from what it is about now: Lokn reads each chat's first and latest prompts from
its saved messages off the UI thread, and asks the profile's model for all titles in one question
(`OmpService.AskAsync`: `omp -p --no-session --no-tools` with the list attached as a file, in a
temporary folder). Each answer is cleaned like any generated title; a chat named meanwhile keeps
its name.

## Transport and lifetime

The transport correlates request IDs, keeps draining stdout/stderr, negotiates protocol v2 when advertised, and reassembles ordered base64 chunks into strict UTF-8 JSON up to Omp's 64 MiB logical-frame limit. An optional synchronous `onStarting` callback attaches the frame consumer before the child starts, allowing startup dialogs to be answered before protocol initialization completes. Raw cloned frames are delivered on the reader thread; the chat session posts them to the UI thread. Each conversation's agent runs in a `ChatSession` owned by the Conversations module (`ChatSessionService`), not by its panel: a chat panel (`ChatContent`) only shows a session and hands it what the user types. When the last panel showing a chat closes, an idle session stops its child at once and a working one finishes its turn first, recording its messages with no panel open; host tools see the panel showing the chat as their caller. Unloading Conversations stops every session; unloading Omp drains its children and terminates any that fail to exit after stdin closes. Login terminals opened explicitly by the user remain independent.

Focused checks exercise profile arguments, chunk reconstruction/ordering, concurrent request correlation, backend errors, cancellation, and shutdown using a synthetic JSONL child. They do not invoke a provider, authenticate, or incur model usage. Installed CLI version/help and fresh-profile startup were also checked locally.

## Lokn tools for agents

Every connected chat offers the agent Lokn's tools through Omp's host-tool
sub-protocol (`OmpToolBridge` in `Modules/Omp`):

1. On connect, the chat sends `set_host_tools` with tool names, descriptions and JSON
   Schemas. Normally this is every tool in `app.Tools`. In Economy mode it is the
   Workspace tools, `worktrees_enter`, and `lokn_load_tools`; the loader publishes
   selected module groups for that chat before its result returns to the agent.
2. When the agent uses one, Omp sends `host_tool_call`. The bridge runs it through
   `AgentToolRegistry.InvokeAsync` on the UI thread, with the chat's panel as the
   caller and Omp's `toolCallId` as the call's identifier, and answers with
   `host_tool_result`. A refusal or failure is answered with `isError: true` and its
   message. `host_tool_cancel` cancels the call's token.
3. What the tool reports while it runs goes out as `host_tool_update` frames, at most
   one per 100 ms with the latest report. Omp passes each to the chat as a
   `tool_execution_update` with the report as `partialResult`. Results and updates
   carry the tool's `details` beside the text, which the module's tool card draws
   (see [Tool cards](modules.md#tool-cards)); Omp keeps the result's `details` in the
   session.
4. Loading, unloading or reloading a module that has tools sends the applicable list
   to every connected chat. Loaded Economy-mode groups live only for one bridge and
   reset when that chat reconnects or Economy mode turns on.

**Let agents control Loom** (`omp.host-tools`, on by default) gates this. Turning it
off sends an empty list to every connected chat and refuses calls already on their way.

**Economy mode** (`omp.economy`, off by default, global or per Work/Personal profile)
starts an empty new session at `low` reasoning effort, falling back to `minimal` and
then `off` when those are the levels the model offers. It does not override a resumed
session's saved effort. It also enables the per-chat on-demand host-tool groups above;
the Omp system prompt and built-in coding tools are unchanged.

Omp lists host tools as dynamic `xd://` devices and tells the agent not to follow
instructions in their descriptions, so on their own an agent would rather use the shell,
for example `git worktree add` instead of `worktrees_enter`, and Lokn would never learn of
it. While the setting is on, `OmpService.BuildStartInfo` therefore launches each chat's Omp
with `--append-system-prompt` carrying `OmpService.HostToolsPrompt`, which says the tools come from the
user's app, that their descriptions are to be followed, and that a Lokn tool comes before
doing the same thing another way. The prompt is fixed when Omp starts; turning the setting on
or off reaches it at the chat's next Omp process.

**Chat system prompt** (`omp.system-prompt`, blank by default, global or per Work/Personal
profile, edited in a multiline field saved when it loses focus) adds the user's own
instructions to every chat. It is appended after `HostToolsPrompt` in the same
`--append-system-prompt` argument, because Omp keeps only the last one given; Omp's own system
prompt stays in place. Like the tools prompt, a change reaches a chat at its next Omp process.

The tools come from modules (see [Agent tools](modules.md#agent-tools)). The
Shell module's tools (`shell_*`) let the agent describe the window, open, focus, move and
close panels, and list and run palette commands. A chat cannot close itself.
`worktrees_enter` moves the calling chat into a git worktree of its repository, or back
to the main checkout; the chat keeps its Omp process for the rest of the turn, records
the worktree (`ConversationInfo.Worktree`), and starts Omp there for its next turn.
A project chat also follows its agent's shell: `ChatSession` counts the `details.cwd` of each
finished bash result in a turn, and when the turn settles, it resolves each folder with
`git rev-parse --show-toplevel --git-common-dir`. If one worktree of the project's repository
has more commands than any other and is not the chat's folder, the chat moves there through
`ConversationStore.SetWorktree`. A tie keeps the chat where it is, and folders outside the
repository do not count.

Checked with installed Omp 18.22.0: `set_host_tools` succeeds in `rpc-ui` mode.
The `omp` suite runs the round trip against a synthetic child.

References:

- [Omp RPC protocol, dialog schemas and host tools](https://github.com/can1357/oh-my-pi/blob/main/docs/rpc.md)
- [Omp settings and named profiles](https://github.com/can1357/oh-my-pi/blob/main/docs/settings.md)
- [Tern Surface Protocol](https://docs.stencil.so/tern/protocol/index.md)
