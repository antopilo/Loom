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

The trace also has startup steps (`step <name> <ms> jit <methods> <ms>`: how long a step took on its thread and how
long that thread compiled methods in it), `open` once the native window is open, and `jit <methods> <ms>` at the first
frame for the whole process. `-Scenario start` prints them under each launch.

## Startup

Measured with bare instances on this PC (Ryzen 9 7900X, RTX 3090 selected by Goo over the AMD iGPU, two displays at
180 Hz), the bench's `workspace` layout, warm disk cache: `bench.ps1 -Scenario start` for the build output, and a
launch script over the same template for publish variants (median of 4 to 6 launches each).

### Where the time went (0.4.7)

| Cause | Evidence |
|---|---|
| JIT: Goo (4.9 MB IL), Yoga, SDL bindings, Core and Loom shipped as IL | 8,970 methods, 1,050 ms of compilation before the first frame (`JitInfo`); Goo alone 3,478 methods (`DOTNET_JitDisasmSummary`). |
| Module code compiled fully optimized | Collectible contexts get no tiered compilation: all 782 module methods compiled at FullOpts; the same methods in a non-collectible context compile 2.7 times faster. Precompiled module DLLs are ignored either way, because modules load from bytes (`LoadFromStream`); checked by crossgen-compiling them (no fewer methods compiled). |
| Goo opens the window on the UI thread: 850 to 900 ms | `Window.Open` (EventPipe samples): SDL window and display pacing 460 ms, Vulkan instance, device and DirectComposition swapchain 320 ms. Of the 460, `SDL_GetCurrentDisplayMode` takes 415 ms (see Goo issue 5). |
| Module DLLs read, loaded and checked on the UI thread | `ModuleCompatibility.FindProblem` 90 to 120 ms (it resolves every member reference, many through exceptions), loading 20 ms, reading 8 ms. |
| Home's logo geometry built in the first frame | `HomeLogo` static constructor 36 ms (fully optimized); 231 ms once module code runs at tier 0. |
| Not causes | Vulkan implicit layers (`VK_LOADER_LAYERS_DISABLE=~implicit~`: no change); GPU choice (Goo already picks the discrete GPU); the Goo pipeline cache (persisted by Goo; the bench shares a warm one); module `Start` IO in a bare instance (130 ms for all modules, mostly JIT). |

### Fixes

| Fix | Where | Gain (first frame) |
|---|---|---|
| Publish ReadyToRun for every default-context assembly except the Windows SDK projection, WinRT and Tmds.DBus (loaded on demand). Roslyn stays included: a source module's first compile takes 380 ms instead of 935 ms. | `Lokn.csproj` (`PublishReadyToRun`) | 2,250 → 1,770 ms (JIT 9,000 → 2,900 methods) |
| The modules Loom starts with load into non-collectible contexts, so their code gets tiered compilation; reloads stay collectible. | `Shell/ModuleHost.cs` (`LoadGeneration`) | with the next two rows, 1,765 → 1,509 ms; JIT 600 → 300 ms |
| Home's logo geometry is computed on a background thread when Conversations starts, and its distance loop is compiled optimized at once. | `Modules/Conversations/ConversationsModule.cs` (`Start`), `Home/HomeLogo.cs` (`MeasureWeft`) | (above) |
| The window opens first; reading, loading and checking module DLLs runs on a background thread meanwhile (`ModuleHost.BeginStart`); the modules start on the open window before its first frame. | `Program.cs`, `Shell/ModuleHost.cs` (`BeginStart`, `Prepare`, `Install`) | 1,622 → 1,480 ms |

Rejected after measuring on the final publish: `TieredPGO` off (−22 ms, within noise; keeps steady-state PGO), tiered
compilation off (+276 ms), quick JIT for loops off (+137 ms). The window opened before its content is transparent until
the first frame, as before; it is visible for the same time it used to be.

### Before and after

| Build | First frame | Interactive | Compiled before first frame |
|---|---|---|---|
| 0.4.7 build output (`bench.ps1 -Scenario start -Starts 5`) | 2,200 ms | 2,236 ms | 8,970 methods, 1,050 ms |
| 0.4.7 publish (what users run) | 2,250 ms | 2,250 ms | 8,920 methods, 1,060 ms |
| 0.4.8 build output (not precompiled; same bench) | 1,935 ms | 1,968 ms | not recorded |
| 0.4.8 publish (`bench.ps1 -Exe <publish>\Loom.exe`, and the launch script) | 1,524 ms (1,480 to 1,510) | 1,534 ms | 3,700 methods, 300 ms |

The 0.4.8 timeline: process start to `single-instance` 175 ms, window open 880 ms (module DLLs ready in the
background 175 ms in), modules, shell, workspace and Home 180 ms, first frame 270 ms. The publish grows from 143 MB to
168 MB (Goo +5.4 MB, Roslyn +16.7 MB, Hexa SDL +1.9 MB, Core and Loom +1.2 MB).

What remains: Goo's `Window.Open`, most of it `SDL_GetCurrentDisplayMode` (Goo issue 5), and work a real data folder
adds that a bare instance does not (Hotload compiling source modules after an update, `RemoteModule.TakeChats`
waiting for the daemon to stop, `DataUpgrade` backups on a new version). The `module.<id>` steps in the trace show
which module a slow real start spends its time in.

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

## Switching

`bench.ps1 -Scenario switch` opens chats and switches profiles in a loop, as a click would: `LOKN_PERF_DRIVE`
(`Shell/PerfTrace.cs`) runs one step every 1.5 s and writes `switch <step> handler <ms> first <ms> frames <n> maxgap <ms>
dropped <n> gaps <ms,…>`: the step's own time on the UI thread, the time to the first presented frame, and the intervals
between the frames after it. Each step's first run is cold (its chat or profile not shown yet in the session). Its chat 8
has 1,400 messages in a 9 MB file, like the longest real chats; real files reach 27 MB.

### Causes (after 0.4.18)

| Cause | Evidence | Fix |
|---|---|---|
| A chat's messages file was deserialized on the UI thread the first time its view built. | `ConversationStore.ReadMessages` 250 ms of a 363 ms first frame for the 9 MB chat (EventPipe samples). | The view reads `PeekMessages`, which reads the file on a worker and shows an empty transcript until it is applied (`ConversationStore.StartReadingMessages`); selecting a chat starts the read. Paths that change messages still read at once. |
| Every panel the layout hid was laid out at full size on each switch. Panels stay mounted, so the cost grew with every chat opened in the session. | Warm chat switch with 3 mounted chats 12.5 ms to the first frame, with 9 chats 19.2 ms. | Hidden bodies are `Display.None` (`ShellView.BuildHiddenBody`): 9.5 and 14.4 ms. Goo still walks hidden nodes in `Layout.readRect`, so some growth remains. |
| `workspaces.json` (100 KB on a real data folder) was serialized with new `JsonSerializerOptions` and written and replaced on the UI thread at each chat or profile switch. | `WorkspaceStore.Save` 2.2 ms a switch in the bench, mostly `CreateFile` and `MoveFile`. | Serialized with cached options and written by a worker; saves during a write collapse into the newest (`WorkspaceStore.Flush` on stop). |
| Losing the focus ran a blocking, compacting full collection at once, so switching away froze the window and the first switch after coming back paid for the regrown heap. | `MemoryManager.TrimMemory` 36 ms on the UI thread with the bench's small heap. | The trim waits until the window has been in the background for 30 s. |

### Before and after

Same machine (180 Hz display, frames every 5.6 ms), `bench.ps1 -Scenario switch -Seconds 45`, Release build output, averages of
the warm runs; cold rows are one run each.

| Step | Before: first frame | After: first frame |
|---|---|---|
| Short chat | 19.1 ms | 8.7 ms |
| Long chat (240 messages) | 20.5 ms | 8.1 ms |
| Chat of another project | 17.1 ms | 9.2 ms |
| 1,400-message chat | 17.6 ms | 9.2 ms |
| 1,400-message chat, cold | 363 ms | 7 ms, then one 143 ms frame when the messages arrive |
| Profile to Personal | 38.3 ms | 22.4 ms |
| Profile to Work | 36.7 ms | 24.3 ms |

What remains: Goo's layout of the whole tree (most of a warm switch's frame), building the transcript rows of a long chat
once its messages arrive (`ChatContent.ComputeRows` and each message's facts, all messages at once), and cold switches that
compile code (a Release build output; a published build is precompiled).

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
5. **Opening a window enumerates every fullscreen display mode.** `SdlHost.RefreshDisplayPacing` calls
   `SDL_GetCurrentDisplayMode`, which in SDL 3.4 first fills the display's fullscreen mode list
   (`SDL_UpdateFullscreenDisplayModes`), calling `IDXGIOutput::FindClosestMatchingMode` for each mode. On this PC's
   2560-pixel-wide 180 Hz display (636 modes) that takes 415 ms of the UI thread in every `Window.Open`, measured with
   SDL3.dll alone; `SDL_GetDesktopDisplayMode`, which pacing needs, takes 9 ms. Loom cannot work around it safely:
   SDL is not thread-safe and Goo initializes it inside `Window.Open`.

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
