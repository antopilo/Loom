# Changelog

Each release's section becomes its release notes: `scripts/installer.ps1` passes the section whose heading is the
version being packed. Write for users: what changed for them, not how.

## 0.4.27

- **Maximize a pane.** Right-click a pane's grip and choose **Maximize panel** to give it the whole workspace while the
  others keep working hidden; restore it the same way, or by focusing another panel.
- **Optional pane headers.** Settings › Shell › **Pane headers** gives every pane a header with its title and, when you
  point at it, maximize and close buttons. Drag it to dock or swap, double-click it to maximize. Off by default.
- **Effort at a glance.** Point at the thinking-effort ring under the prompt to see its effort.
- **Tidy sidebar.** Chats line up under every project, with or without a project icon.
- **Round expand button** on image cards.

## 0.4.26

- **Updated UI engine.** Loom runs on Goo 0.32.10, whose new layout engine places and sizes panels the way web pages do.

## 0.4.25

- **Even code rows.** Lines in searches and diffs no longer show twice as tall in files with mixed line endings.
- **File picker reads left to right.** The @ file list lines its names up on the left.
- **Clearer palette selection.** The selected command shows on every theme.

## 0.4.24

- **Commit cards.** A commit the agent makes shows as a card under its command, with its subject, branch, hash and counts. Click it to open the
  commit in the Git panel.
- **Commit IDs are links.** A commit ID in a reply, such as 33f2aab4, opens that commit in the Git panel.
- **Clicks land where you point.** File, web and commit links after code in the same line open on the first click.
- **Smoother scrolling** through replies full of code and links.
- **Tool reasons stay.** Bash and other steps keep the reason the agent gave after you reopen a chat or restart Loom.

## 0.4.23

- **Pick your fonts.** Settings › Theme has an Interface font and a Code font: type an installed family, such as Inter or Cascadia Code.
- **Middle-click scrolling.** Click the middle mouse button in a chat and move the pointer to scroll, faster the farther you move.
- **Links react to the pointer.** A file link gets a soft background under the pointer, and a web link a thicker underline.
- **Fewer text drawings.** Agents draw diagrams as real drawings instead of boxes and arrows made of characters.
- **Image cards for Markdown images.** Images a reply links in Markdown show in the same card as attached images.
- **Taskbar icon fix.** A leftover Start menu shortcut from older builds no longer leaves the taskbar icon blank.

## 0.4.22

- **Image cards.** Images in chats show in a card with their name, the scale they show at and a button to open them full size.
- **Quieter code blocks.** Copy and Run are small icons that appear over a code block's corner when you point at it, instead of taking a row.
- **Soft top edge.** The top of a chat fades out as it scrolls, like the area above the prompt.

## 0.4.21

- **Drawings in chats.** Agents can draw layouts, diagrams and mockups as SVG, and Loom shows them as drawings instead of ASCII art.
- **Links you can see.** File paths and web addresses in a reply show with an icon and open on click: files in the editor, pages in the browser.
  Ctrl-click opens them in your own apps instead.
- **Clearer tool steps.** Reads show as a group of file chips, shell commands as a box with their output, searches as matching lines with
  their context, and edits as a clean diff with the file and its counts on one line. Code in these views has syntax colors, `.mjs` included.
- **Easier reading.** Headings, inline code, links and tables look like Tern's, inline code sits in a box, and paragraphs, lists and code have
  more room around them.
- **Smoother scrolling up.** Scrolling up a long chat no longer jumps or pulls you back down.
- **Sidebar selection** stays under its row while the list scrolls, and the token and cost popup is a clearer card.

## 0.4.20

- **Math and diagrams in chats.** LaTeX formulas and Mermaid flowcharts and sequence diagrams show in messages.
- **Faster chat switching.** A chat's messages load in the background, so opening a long chat no longer stalls the window.
- **Drag tasks and chats** to reorder them in the sidebar; rows slide aside to make room.
- **Less memory in the background.** Loom trims its memory after its window has been in the background for a while.
- **Design snapshots** wait until the design has loaded.

## 0.4.19

- **Loom runs on Linux.** System fonts, local terminals, window backdrops (Off and Glass), worktree links and opening
  folders in the file manager now work on Linux.
- **A new sidebar look.** Project names use Fraunces, the fold arrow sits in the margin, and row buttons show on hover
  without moving anything. Chats rename themselves as their task changes.
- **Long lists stay fast.** The sidebar and other long lists build only the rows you can see, and long views scroll
  instead of cutting off. Buttons, rows and pages keep the space they need in small panes.
- **Scrollbars** stay visible while you hover them and fade after you scroll.
- **The edited files card** shows when the agent's run ends, and elapsed time past an hour shows in hours, minutes and seconds.
- **Tooltips** have transparent corners, and WebP images show as thumbnails in chats and as project icons.
- **Browser pages can hide the address bar**; the design studio uses it and gets **Share**.

## 0.4.18

- **Act on a notification from the list.** Buttons such as **Restart now** show on the notification's row, under the
  bell and on the Notifications page, so you no longer open it first.
- **The Notifications page fits small panes.** When the page is narrow, its filters move to tabs above the list; when
  it is very narrow, a notification opens in the list's place, with a button back.
- **Project icons.** Projects without an icon image can use an emoji, and paired hosts' projects show their icons.
- **Design studio** opens in a new tab when no panel shows it, and **agent browser pages** open in the window that
  shows the chat.
- **Animated sidebar selection.** One highlight now slides between active chats and tasks, like the Settings page selection.

## 0.4.17

- **A calmer sidebar.** New chat and Search sit at the top, with New project and Notifications as small icons beside your
  host. Each project shows its name and chat count; click the name to open its overview, or the arrow to fold it.
- **Tools where they belong.** Files, Git and Terminal are small icons on each project and worktree, and open in that
  folder. Pull requests have their own icon on projects hosted on GitHub. They no longer take a row each in your chats.
- **See which chats work.** A working chat shows the Loom thread sewing; a chat that waits for you shows a dot. Archived
  chats and Tidy chats are in each project's … menu.
- **The sidebar slides in.** With the sidebar hidden (Ctrl+B), move the pointer to the window's left edge and it floats
  in over your work, then slides away when you leave it.

## 0.4.16

- **A project hub that shows what matters.** A project's overview page now starts with a prompt bar: type and press
  Enter to start a chat in the project, or Ctrl+Enter for a task. Below it, **Needs you** lists the chats that wait
  for you or work, and **Recent** only the last three. On the right are its designs and reports, and the newest
  pull request comments, reviews, checks and merges of its GitHub repository.
- **Search in Settings.** Settings has a list of its sections on the left and a search field that finds any setting
  by its name or description.
- **What's new after an update.** After Loom updates, a page shows what changed in the new version.
- **See windows before you stream them.** The window list of a stream shows a small picture and the icon of each
  window.
- **Less memory in the background.** Loom gives back memory while its window is not in front.

## 0.4.15

- **A new Notifications page.** Filter by what waits for you, what is unread, where it came from (Chats, GitHub,
  Browser…) or project, search everything, and read the selected notification in full beside the list, with its
  branch and changes. Questions from chats are answered right there.
- **A clearer bell.** The list under the bell shows the newest first, by day, with a mark for done, failed or
  waiting, a line of detail and its age. Tabs show only what waits or what is unread.
- **Smaller banners.** A new notification shows as a compact card with one line of its message.
- **Questions fit in the banner and the bell.** Answers are one row each, with number keys 1–9. Questions with
  several parts go one step at a time, and forms use compact fields. A form with pictures says to answer it in the
  chat. The chat itself keeps its own question card.

## 0.4.14

- **A project's overview is its own page.** The overview button opens the project's overview alone, full window, as
  a chat opens. Come back to it later and it is as you left it. Switching profile and back returns to it.

## 0.4.13

- **Design screens with your agent.** Ask an agent to design a screen and it writes the design as HTML and CSS in
  your project. A studio opens beside the chat and shows each save live. Switch variants and screen sizes, pin
  comments to elements and send them to the chat, and export a variant as a zip.
- **One page for each project.** Hover a project in the sidebar and click its new overview button, or choose
  **Overview** in its **…** menu. The page lists the project's chats and tasks, with the ones that wait for you and
  the ones that work at the top, its recent activity, its designs, which open in the studio, and the reports its
  agents showed. Start a new chat or task from the page too. **Project overview** in Search (Ctrl+K) opens it as well.

## 0.4.12

- **Loom opens on AMD graphics cards.** On PCs whose graphics driver cannot draw a see-through window, such as AMD
  Radeon cards, Loom closed at once without a window. It now starts again with a solid window. Mica and Acrylic
  backdrops do not show on these PCs.
- **The message box grows with long lines.** When a message wraps onto more lines, the box grows up to show them,
  as it does for Shift+Enter, up to eight lines.
- **Opening an image no longer closes Loom.** The editor shows a PNG, JPEG or other image as a picture, and says when
  another file is not text instead of showing its bytes. Such files cannot be saved from the editor.

## 0.4.11

- **The agent's questions show again.** Omp's ask tool now sends a form, which Loom did not show, so a chat waited
  for an answer you could not give. Loom, its clients and the web page now show the form and send your answers.
- **Quicker answers to the agent's questions.** A question shows its answers as a row of buttons, and one click
  answers a single-choice question. The recommended answer is highlighted: press Ctrl+Enter to give it.
- **Plan before the agent changes anything.** Type `/plan` and your task: the agent explores, asks its questions and
  writes a plan, with **Implement** and **Revise** buttons. Nothing changes until you click **Implement**.
- **Give a chat a goal.** `/goal` and an objective make the agent work turn after turn until the goal is done. A line
  above the prompt shows it, with **Pause**, **Resume** and **Drop**.

## 0.4.10

- **WebP project icons now show.** Project folders can use WebP for their explicit or automatically found icon.
- **No more flicker or crash from streaming your own screen.** A PC now offers its screens only to other PCs. Streamed
  on the PC itself, the screen showed itself again and again, and its clicks moved the pointer the stream read.
- **Sharper streams.** The Window stream no longer shrinks windows and screens to 1600 pixels wide: it streams them
  at their full size, or at the size of the panel showing them when that is smaller, and follows the panel when you
  resize it. A 3440×1440 ultrawide screen streams at full size.
- **Stream a screen at your own resolution.** Streaming another PC's screen switches it to the resolution of the
  screen you watch on, or the closest it has, and puts it back when you stop, when the connection drops, or when
  that PC's background service stops; after a crash, the service puts it back when it starts again. Turn it off with
  **Use this device's resolution** in the stream's toolbar. When the screen has no matching mode, the stream says
  which one it uses.
- **Window streams stay steady while new frames arrive.** A decoded frame remains visible until its replacement
  finishes uploading to the graphics card instead of flashing the panel background between frames.
- **Agents inspect Loom without opening a stream of Loom.** The Shell now captures one visible panel
  directly. Browser pages and other native panel surfaces are included without opening a Window stream.

## 0.4.9

- **Stream a whole screen.** The Window stream lists each screen of the PC above its windows. A screen streams with
  the pointer, and clicks, keys and typed text go wherever the PC's focus is, so you can use the PC as if you sat at it.
- **Hover help no longer shows as a black box** under the title bar's buttons, such as Settings.

## 0.4.8

- **Loom opens faster.** On a test PC the window shows its content after 1.5 s instead of 2.2 s: Loom ships precompiled,
  opens its window while it reads its modules, and starts them faster.
- **The background service uses far less memory.** After it loads your chats, it gives the memory it no longer needs
  back to Windows once no agent works, instead of keeping about 250 MB for good.

## 0.4.7

- **Loom rests when nothing moves.** An idle chat or Settings no longer redraws the window 20 times a second, which
  cost CPU and battery. Animations such as the Home logo, the working stitch, spinners and the prompt's blinking caret
  run only while you can see them and Loom has the keyboard, and pause when it is in the background, minimized, or
  behind a dialog. Remote access in Settings no longer refreshes itself a thousand times a second.

## 0.4.6

- **Popups open smoothly.** Every popup, menu, notification and dialog fades in the same way.
- **The + beside a project works again.** Its menu no longer flickers, and a click on + starts a new chat.
- **Agents arrange panels better.** A panel an agent opens goes below a tall panel and beside a wide one, so the
  screen is no longer cut into thin columns.
- **Follow a host's agent.** When an agent on a paired PC opens or uses an app there, this Loom shows a live Window
  stream of it. Turn it off in Settings › Remote (**Show the host's screen when its agent uses an app**).

## 0.4.5

- **Old chats archive themselves.** Chats nobody wrote in for 14 days, and older chats beyond the 12 most recent in a
  project, leave the sidebar and memory. Pinned, open, working and drafted chats stay. The archive icon beside a project
  lists its archived chats; choose one to bring it back. Each chat's menu has **Archive**. Change the limits in
  Settings › Conversations (0 turns a limit off).
- **A test copy of Loom no longer touches your Loom.** A second Loom started with `--bare` (for demos and checks)
  could stop your daemon, take over Loom's links and notifications, and remove "Start the daemon at sign-in". It now
  keeps everything, logs included, in its own folder.

## 0.4.4

- **Lighter on memory.** Long chats no longer keep each shell command's internal trace in memory, inline images keep
  only small pixels (the full image loads while you view it), and Loom returns large freed memory to Windows.
- **Watch agents use your apps.** When an agent works with a window on your PC, Loom opens a live Window stream of it
  beside the chat, marks where the agent clicks and says "Agent is using this window". The agent can see the window and
  click, type, press keys and scroll in it. Turn it off in Remote access: **Let agents see and use this PC's windows**.

## 0.4.3

- **Find Loom PCs near you.** Remote access lists Loom PCs on your network; pairing sends a request that their user
  allows. Paired hosts on the same network are found again when their address changes.
- **Chats of a paired host sit under their worktrees** in the sidebar, as on this PC.
- **Much smoother window streams.** Windows are captured by the compositor and sent as H.264 video, encoded by the GPU
  when it can, at up to 30 frames per second and a fraction of the data. Older Loom versions on the other side still
  get pictures as before.

## 0.4.2

- **Settings opens in its own window.** Close it like any dialog; your layout no longer changes.
- **Remote access is simpler.** Pick Off, Local only or Anywhere. Anywhere guides you through Tailscale step by step,
  with each step marked done, and allowing Funnel is one of the steps.
- **Same network, fast path.** A Loom PC on the same network as its host now connects directly, whatever mode the
  host uses, and falls back to Tailscale or the relay when away.
- **Smoother window streams.** Frames no longer wait for each other over slow links.

## 0.4.1

- **Run shell snippets.** Bash, PowerShell and cmd code blocks in a chat have a Run button next to Copy. It opens a
  terminal on this PC beside the chat that runs the snippet and stays open.
- **Clickable links.** Click a link's text in a chat to open it, including plain `https://` addresses. The separate
  Open buttons are gone.
- **Tables show when you scroll to them.** Tables that started out of view no longer stay blank until clicked.
- **No more stuck keys.** If an Alt, Ctrl, Shift or Windows key release got lost, Backspace and other keys stopped
  working until Alt+Tab. Loom now notices and clears it.
- **Clicks keep working.** The window could ignore every click and key until Alt+Tab. Goo 0.32 fixes this.

## 0.4.0

- **Remote access is one simple page in Settings.** Settings › Remote shows **Serve over** (Off, Tailscale, Anywhere:
  a link with Tailscale Funnel, a relay, or this network) with what is left to do and a button for each step,
  **Pair to control…** and **Pair to watch…** with a QR code, your **Devices** with Revoke, and the PCs you connect to.
  Choosing a way to serve moves your chats to the background daemon by itself. With Funnel, a phone needs nothing
  installed: scan the code and the browser opens Loom.
- A code that only a Loom PC can open no longer shows as a QR code.
- **Right-click a pane's grip** for its menu; a left click no longer opens it, so dragging a panel never pops it up.

## 0.3.1

- **Connect a device in one click.** Remote access now has one button, **Connect a device**. Loom moves your chats to
  the background daemon (after running agents finish), starts it at sign-in, opens the network (Tailscale when it runs
  on this PC, else your local network) and shows a code. Scan it with the phone, or open its link on the other PC. The
  device gets control at once, so show the code to that device only. Roles, network modes, view-only codes and the
  other settings moved to **Advanced**.
- **Pairing links open Loom.** Clicking a `loom://pair` link in a browser opens Loom and pairs with that PC at once.
- **Paste a pairing link in the web client.** A browser that is not paired shows a field for the link.

## 0.3.0

- **Nothing changes unless you turn it on.** After the update Loom works as before: your chats run in this window, no
  daemon starts and no device can reach this PC. Home shows once what is new, with a button to let chats keep running
  in the background.
- **Your data is backed up before an update changes it.** The first time a new version starts, Loom copies your chats,
  projects, workspaces and settings aside; it keeps the three newest copies. Settings › Shell › Backups lists them and
  restores one: Loom closes, puts it back and starts again.
- **Updates wait for your agents.** Restart now no longer stops agents at work on this PC or in its daemon: choose
  **Update when agents finish**, or **Update now** to stop them. A daemon that ran before the update starts again after it.
- **Run your chats in the background and reach them from other devices.** Settings › Remote access (or the
  status bar) switches Loom to a daemon on this PC: chats keep working when you close Loom. The switch applies at
  once, without a restart: Loom waits for running agents to finish, then moves your open chats over and keeps your
  layout. When the daemon fails to start or keeps crashing, Loom goes back to running chats itself and tells you why.
  Pair a laptop to use the same chats, Git, files and terminals of this PC in its own Loom, or pair a phone to use them
  in the browser. Devices connect over your network, Tailscale or a relay you host, end-to-end encrypted; network
  access and paired devices are a preview.
- **Use several PCs from one Loom (preview).** Pair Loom with as many PCs as you like (Add host in Remote access): their chats
  show beside this PC's, and the host switcher above the sidebar or at the start of the tab strip moves between them
  without a restart. Git, Files, Terminal and Browser panels work on the PC they were opened for, named on their
  pane; a panel whose PC goes offline says so until it is back, and comes back to life when a removed PC is added
  again. Nothing of this PC's disk reaches another PC: a terminal there starts in that PC's own folder with its own
  shells, Files and Git show nothing until a project there is open, and the editor's Save as asks for a path on that
  PC. A PC added while you are in this PC's project stays a switch away. A device that pairs with view only now asks
  you whether to **Allow control** or keep it **View only**, and a pairing link stops working once Remote access no
  longer shows it.
- **The daemon shows an icon near the clock.** Its dot turns orange when an agent needs you and red when something
  is wrong. Click it to open Loom; right-click to open Loom, see how many agents work and devices are connected,
  open Remote access, start the daemon at sign-in, or stop it. Turn it off in Settings › Remote access.
- **The daemon follows Settings › Modules and module updates.** Turning a module off, such as Git or Browser, stops
  it in the daemon too; turning it on starts it there. Module updates reach the daemon without a restart, applied as
  soon as no agent is working, so a running turn is never cut off.
- **A new device can only read your chats until you allow it to control.** Sending prompts, answering agents, and
  using this PC's files, terminals and windows need control; allow it per device in Settings › Remote access.
- **Window stream.** Open Window stream to see a window of the host PC, such as a game editor, and use it with the
  mouse and keyboard.
- **Browser tabs follow you between windows and devices.** A browser tab now belongs to your session, not to one
  window: open Loom on another PC (or a second window) and the same tabs are there, each showing the page live; go
  somewhere in one and the others follow, while a page that only redirects differently in one window, such as to a
  sign-in page there, stays put in the others. Tabs stay open when you close Loom, and **Session tabs** in the tab
  strip shows, or closes, one another window opened; with no daemon, closing a browser panel closes its tabs.
  Agents on the host use the browser of whichever Loom is open, this PC's first; a paired PC can turn that off with
  **Let agents on the host use this device's browser** in Remote access, and it never shows them its own files or
  services. A paired PC opens the host's `localhost` pages, such as a dev server, as if it were the host. The
  phone's web client lists the tabs; open them on a computer.
- **Closing a chat no longer stops its agent.** The turn finishes in the background; when it is done or asks you
  something, a notification and the sidebar tell you, and the notification opens the chat.

## 0.2.0

- **Community modules.** Settings › Modules › Community lists modules other people wrote, from the
  [community hub](https://github.com/antopilo/loom-modules). Search them, then install, update or remove one; it
  loads at once. Installed modules update automatically, unless you turn that off below the list. Anyone can submit a
  module with a pull request to the hub.
- **Now Playing is a community module.** It is no longer built in: install it from Settings › Modules › Community.
- **Search finds settings again.** Choosing a setting in the Search palette opens Settings on its tab, with the
  setting highlighted, instead of on the first tab.
- **Updates keep how Loom was started.** Restart now starts the new version with the same options as before.

## 0.1.0

- First release: chats with Omp agents, projects and tasks, the docking workspace, terminal, Git, worktrees, files,
  editor, browser, GitHub, notifications, themes, source modules, the Windows installer, and updates of Loom and its
  modules.
