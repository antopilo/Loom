# Performance

How Loom keeps an idle window from drawing, and how to measure it.

## Measuring

`pwsh scripts/bench.ps1` runs bare instances of a build and reads `LOKN_PERF_TRACE` (`Shell/PerfTrace.cs`): one
`frames <n>` line per second with the frames the window presented, plus its CPU time. Measure a copy of a build, never the
installed Loom (`-Exe %TEMP%\...\Loom.exe -Label before|after -Out <file>.json`).

Loom opens on Home and puts the restored layout back into its session (`ConversationsModule.Start` → `OpenHome`), so
before 0.4.7 every scenario measured Home: the chat, working, streaming and settings launches now open their view with
`--open-link` (a `lokn://conversations/open/<chat>` or `lokn://shell/settings` link).

Known limits of the bench:

- **working** and **streaming** do not start a turn: the restored chat that asks its agent to continue is emptied by
  `ShowHomeAlone` before the turn starts, and the link then opens the chat idle. Those rows, and **unfocused**,
  **covered** and **minimized** (measured in the working launch), measure the idle chat.
- **unfocused** and **covered** cannot take the keyboard from Loom where the bench process may not set the foreground
  window (Windows' focus-stealing rules; a test form's `SetForegroundWindow` is refused there too): the `Focus` column then
  still reads `focused`. Focus-dependent pausing is checked in code, not by these rows, on such machines.

## Idle frames: causes and fixes (0.4.7)

| Cause | Evidence | Fix |
|---|---|---|
| `HomeLogo` (Conversations) ticked a 50 ms timer for as long as it was mounted, rebuilding ~900 glyphs 20 times a second. Hidden panels stay mounted (`ShellView.BuildHiddenBody`), so the Home panel a chat replaced kept animating behind it, and so did Home under Settings. | A temporary per-type build counter showed `HomeLogo=20`/s and nothing else in the idle chat and Settings, and the trace 20 frames/s. | The mark moves only while `LiveMotion.IsAttended` (shown, window has the keyboard, not minimized, no reduced motion); the timer stops otherwise. |
| `PromptCaret` (Conversations) declared no `State`, so Goo rebuilt it after every 16 ms poll even when nothing changed, and it polled and blinked while the window was in the background. | Counter: `PromptCaret=62`/s with the prompt idle. | A `State` makes its rebuilds tracked (only on a rectangle or blink change); polling and blinking run only while the prompt is focused and attended, and the caret rests shown otherwise. |
| `StitchMark`, `Spinner`, `ActivityOrb` (Conversations) loop for as long as they are mounted: per-frame `Anim`, or a timer feeding an endless transition, so a working chat drew at the display rate even hidden or in the background. | Code: `Every`/`Anim.Play` restarted from `Mounted` with no stop condition. | Run only while attended; the stitch pauses and resumes its `Anim`, the spinner finishes its turn, the orb rests bright. |
| `WorkingIndicator` rebuilt 4 times a second though its text changes once a second. | Code. | Rebuilds only when the elapsed text changes, and its clock runs only while `LiveMotion.IsSeen`. |
| `AccessContent` (Remote, Settings › Remote access) ticked every **1 ms**: 0.4.0 turned `TimeSpan.FromSeconds( 1 )` into `const double Tick = 1` passed to `Every( intervalMs )`. While shown it rebuilt up to 1000 times a second during pairing and polled the daemon's status every 5 ms. | `git log -L` on the constant. | `TickMs = 1000`. |
| `BrowserView` (Browser) polled its placement every 50 ms for as long as the panel existed, hidden or not, and rebuilt after each poll (no `State`). | Code. | The poll runs only while the page area is seen; hiding or covering places the page away once. |

The shared mechanism is `Core/LiveMotion.cs`: a Goo `Context<bool>` (`LiveMotion.IsShown`) that the shell provides
false under a hidden panel and under the workspace while a modal window (Search, a popup panel such as Settings) covers
it (`ShellView.BuildPanelSurface`), plus `IsSeen`/`IsAttended` reads that subscribe the building view, and a
`MotionSwitch` that starts and stops timers and animations outside Build, where Goo allows it. See
[modules.md](modules.md#panel-lifetime-and-state) for the rule new views follow.

## Goo 0.32.0 issues

Read from the decompiled `Goo.dll` 0.32.0 (`AssemblyInformationalVersion 0.32.0+6fdcd4f3`).

1. **Changes under a hidden subtree still present frames.** A panel under `Visibility = Hidden` and `Opacity = 0`
   that rebuilds makes Goo render and present a frame although nothing visible changed: the hidden Home logo alone
   gave 20 presented frames/s in an idle chat. Workaround: Loom stops such motion itself (`LiveMotion.IsShown`).
2. **The text caret blinks frames regardless of its color and of window focus.** `TextInput.Step` flips
   `Node.BlinkT` every 0.5 s while an `Entry`/`Editor` holds Goo focus, and `InputCoordinator.Step` reports the flip as
   a change that schedules a frame; `TextEditor.CaretColor = Transparent` (the chat prompt draws its own caret) does not
   stop it, nor does the window losing the keyboard. Goo exposes no blink setting. Cost: about 2 frames/s while a text
   field has focus (Home's 22 frames/s = 20 for the logo + 2). No clean Loom-side workaround; it needs a Goo option to
   disable the blink or to skip it for a transparent caret or an unfocused window.
3. **A Cell without `State` rebuilds after every callback** (documented: "declaring State or reading State in Build
   enables tracked callback rebuilding"). A view polling on `Every` without a `State` rebuilds on each tick; declare one.
4. **Windows reports no occlusion** (`Window.Occlusion` is `Unknown` on Windows), so a window covered by another but
   still holding the keyboard cannot be told apart from a visible one; such a window usually has lost the keyboard,
   which `IsAttended` follows.

## Before and after

Same machine, `scripts/bench.ps1 -Seconds 15` with the `--open-link` fix, a copy of the 0.4.6 build (`before`) and of
0.4.7 (`after`). CPU is a share of one core.

| Scenario | Before fps | Before CPU | After fps | After CPU |
|---|---|---|---|---|
| home (focused) | 22 | 8.5% | 22 | 7.5% |
| chat (long transcript, idle) | 20 | 16.5% | 0 | 0.1% |
| working (idle chat, see limits) | 20 | 14.3% | 0 | 0.1% |
| unfocused (see limits) | 20 | 13.7% | 0 | 0.3% |
| covered (see limits) | 20 | 13.4% | 0 | 0.3% |
| minimized | 0 | 7.0% | 0 | 0.1% |
| settings | 20 | 18.1% | 0 | 0.2% |
| streaming (idle chat, see limits) | 20 | 16.3% | 0 | 0.8% |

Home focused stays at 22 frames/s by design: the logo animates while Home is seen and Loom has the keyboard (20), and
Goo's caret blink adds 2 (issue 2). It stops as soon as Home is hidden, covered by a modal window, minimized or in the
background.
