# Changelog

Each release's section becomes its release notes: `scripts/installer.ps1` passes the section whose heading is the
version being packed. Write for users: what changed for them, not how.

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
