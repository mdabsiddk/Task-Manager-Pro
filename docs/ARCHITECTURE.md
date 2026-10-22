# Architecture

The technical design of Task Manager Pro. Read this before changing the code.

## Goals

1. **Safe.** Only documented, reversible Windows mechanisms. No drivers, no thread suspension, no failed memory allocations. Every change can be undone.
2. **No background cost by default.** Limits are kernel or OS settings that outlive the app. The window process exits completely when closed.
3. **Tiny when it does run in the background.** The optional engine is event-driven, uses mscorlib and raw Win32 only, and has no polling loop.
4. **Looks and behaves like the Windows 11 Task Manager.** It follows the system theme, groups processes into app trees, shows totals in the headers and colours cells as a heat map.
5. **No dependencies.** It builds with the `csc.exe` that ships with the .NET Framework in Windows.

## Processes and roles

One executable, `TaskManagerPro.exe`, plays several roles:

```
 Shortcut / Start menu ──► TaskManagerPro.exe (asInvoker)
                              │  Show event exists? ──yes──► signal it, exit (no UAC prompt)
                              │  not elevated?      ──yes──► ShellExecute "runas" … --ui (one UAC prompt)
                              ▼
                    ┌──────────────────── window process (--ui) ────────────────────┐
                    │ WinForms UI · ProcessSampler · ETW network · metadata/icons     │
                    │ engine NOT running → enforces limits itself (LimitController)   │
                    │ engine running     → client: saves rules, signals the engine    │
                    │ on close: Detach() (limits stay) → exit; optionally starts      │
                    │           the engine if "Run in background" is on               │
                    └────────────────────────────────────────────────────────────────┘
                    ┌──────────────── background engine (--engine / --autostart) ─────┐
                    │ optional · mscorlib + Win32 only · tray icon · message loop     │
                    │ ETW Kernel-Process "ProcessStart" ─► 150 ms debounce ─► Enforce │
                    │ opens the window as a child process (--ui) on tray click        │
                    └────────────────────────────────────────────────────────────────┘
```

The engine is started by a Task Scheduler sign-in task with *highest privileges* (no UAC prompt), and only when *Run in background* is on.

### Talking between the window and the engine (`Ipc.cs`)

| Direction | Mechanism | Purpose |
|---|---|---|
| launcher → running instance | `Local\TaskManagerPro.Show` event, **Medium integrity label** so a non-elevated launcher can signal an elevated process | Bring the window up without a UAC prompt |
| window → engine | `Exit`, `ConfigChanged`, `Pause`, `Resume` events | Commands |
| engine → window | `UiActivate`, `UiClose` events | Focus the window / close it on tray *Exit* |
| engine → window | `Local\TaskManagerPro.Status` shared memory (64 KB, sequence lock) | Paused flag and the governor state of every limited app |
| either | `Local\TaskManagerPro.Engine` mutex | "Is the engine running?" |

## Folder layout

| Folder | Rules |
|---|---|
| `src/Core` | Shared by both processes. **mscorlib only:** no `System.dll`, `System.Core.dll` (no `HashSet`, `Stopwatch`, `Process`) and no WinForms, so the engine never loads them. |
| `src/Engine` | The background engine: raw `RegisterClassEx`, `Shell_NotifyIcon`, `MsgWaitForMultipleObjects`. |
| `src/UI` | WinForms window. May use anything. |
| `Program.cs` | Chooses the role. Must not touch WinForms types; `RunUi()` is `NoInlining` so the JIT never loads WinForms for the engine. |

## Sampling (`ProcessSampler`)

- `NtQuerySystemInformation(SystemProcessInformation)` returns, for every process in one call:
  - CPU time
  - private and total working set
  - I/O transfer counts
  - parent PID, session and start time

  This is what Task Manager and System Informer use. No process handles are opened. It takes about 3–6 ms for roughly 230 processes.
- **CPU %** = Δ CPU time ÷ (elapsed time × logical processors), measured with QPC.
- **Memory** column = private working set, the same number Task Manager shows. The memory **limit** caps the total working set.
- The buffer is reused between calls and grows only on `STATUS_INFO_LENGTH_MISMATCH`.

## App grouping (`ProcessTree`)

- Each process climbs to its top-most ancestor.
  - It stops at shell and service hosts (`explorer`, `svchost`, `services`, `sihost`, …).
  - It also stops at a parent that is younger than the child (a reused PID) or in another session.
- Trees whose roots share a name are merged, so two `chrome.exe` roots become one *Google Chrome* app.
- This is why *PowerShell* and *Console Window Host* appear under the app that launched them, just like in Task Manager.
- The **rule key is the root name** (for example `chrome`), and a limit covers the whole tree.

## Enforcement (`ResourceGovernor`)

| Resource | Mechanism | Restored to |
|---|---|---|
| CPU | Named Job Object `Local\TaskManagerPro.Job.<app>` with `JOB_OBJECT_CPU_RATE_CONTROL_ENABLE \| HARD_CAP`, `CpuRate = % × 100`. Child processes join automatically. Nested jobs work on Windows 8+. | Cap removed; a job without limits has no effect |
| CPU (fallback) | Core affinity: `floor(% × cores)` cores, at least one | Original mask |
| CPU / disk | `BELOW_NORMAL` priority; `IDLE` while over the limit (80% hysteresis, window only) | Original priority class |
| Memory | `SetProcessWorkingSetSizeEx(..., MAX_ENABLE)` with the limit divided by the process count (at least 16 MB); trimmed every ≥ 15 s while over the limit (window only) | Original min/max and flags |
| Disk | `NtSetInformationProcess(ProcessIoPriority)`: Low (1), Very low (0) while over the limit | Original I/O priority |

Rules:
- **Never raise.** A priority or I/O priority that is already lower is left alone.
- **No needless system calls.** The governor computes the wanted state and opens a process only if something differs.
- **Protected processes stop being retried.** If a process can't be opened, it is marked `NoAccess` and not tried again.

### Why limits survive closing the app

All of the settings above live **in the kernel, on the process**. `Detach()` simply forgets its bookkeeping and closes its handles. Nothing is undone.

A job object lives as long as it contains processes. Windows does, however, drop the job's **name** as soon as the last *handle* closes. To let a later session find the job again (to change or lift the cap), the governor duplicates a **query-only** job handle into each limited process (`KeepJobNameAlive`). That handle grants nothing and closes automatically when the process exits.

When a later session sees a process inside the app's job (or one Windows started with our *PerfOptions*), it **adopts** it. It treats the current lowered values as ours and restores Windows defaults on release: Normal priority, Normal I/O, no hard working-set limit, full affinity.

### Apps started later, with nothing running (`PerfOptions`)

`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<app>.exe\PerfOptions` holds:

| Value | When written |
|---|---|
| `CpuPriorityClass = 5` (BelowNormal) | the app has a CPU limit |
| `IoPriority = 1` (Low) | the app has a disk limit |
| `PagePriority = 2` (low memory priority) | the app has a memory limit |
| `TaskManagerPro = 1` (marker) | always: only values carrying this marker are ever removed, and keys someone else configured are never taken over |

The kernel applies these values at process creation. They are synced on every rule change, removed on pause, and removed on uninstall.

### Network (`NetworkQos`)

- Policies are created with `New-NetQosPolicy -AppPathNameMatchCondition <app>.exe -ThrottleRateActionBitsPerSecond …`. They are persistent and apply to outbound traffic only.
- PowerShell runs with `-EncodedCommand`, so no quoting issues arise.
- Commands run in a single serial background queue, and `Flush()` waits for it before a short-lived process exits.

### Minimums and misuse protection (`LimitPolicy`, `AppProfiles`, `RuleStore`, `AppPaths`)

**Per-app minimums from the app's own data.** `AppProfileStore.Observe` runs in the window's sampling thread:
- It takes an exponential moving average (α = 0.1, at most one sample per app every 15 s) of private memory, CPU %, disk and network.
- It samples **only apps without an active limit**, so a limit can't lower its own minimum.

`LimitPolicy.FloorsFor` picks the first of these that applies:

| Situation | Floor |
|---|---|
| Learned profile (at least 3 samples) | ½ × typical use |
| No profile yet, app running unlimited | ½ × current use |
| No data at all | Windows-level floor only |

In every case the floor is at least the Windows-level floor:

| Resource | Windows-level floor |
|---|---|
| CPU | ½ logical processor |
| Memory | 32 MB × processes (at least 64 MB) |
| Disk | 1 MB/s |
| Network | 30 KB/s (about 0.25 Mbps) |

`LimitPolicy.Sanitize` raises any non-zero limit to the floor and drops rules for Windows components or invalid names. It runs:
- on every `RuleStore.Set`
- on every load
- in `LimitController.SetRule`

The governor also never sets a job cap below ½ core or a working set below 32 MB per process.

**Locked data folder.** `AppPaths.EnsureDataDir` runs at window and engine start:
- It sets a protected DACL on `%ProgramData%\TaskManagerPro`: SYSTEM and Administrators get full control, with no inheritance from ProgramData. (ProgramData normally lets *Users* create files in sub-folders.)
- It resets the ACL of files already inside.
- If the folder was never locked before, any existing `seal.key` is discarded, because it could have been planted.

**Seals.** `limits.cfg` and `profiles.cfg` end with `#seal:` and an HMAC-SHA256 (Base64) of their data lines, keyed with `seal.key` (32 random bytes from `RNGCryptoServiceProvider`). The seal is compared in constant time.
- **`limits.cfg` without a valid seal:**
  - the rules go to `PendingReview` (sanitized) and **nothing is applied**
  - `SetRule` refuses to overwrite the file
  - the window shows the review bar; the engine shows a tray balloon
  - *Accept* re-seals the file; *Reject* keeps a `limits.rejected-<time>.cfg` copy and starts empty
- **`profiles.cfg` without a valid seal:** it is ignored and relearned.

**Self-healing.** `LimitController.SyncPersistent` runs when the enforcing process starts. It rewrites PerfOptions and QoS from the sealed rules.

**Emergency.** `--restore-all` (with self-elevation) does the following, then shows a confirmation:
1. closes the window (`UiClose`) and stops the engine
2. `SetPaused(true)`: restores running apps and removes PerfOptions and QoS
3. `ReleaseEverything`

### `LimitController`

This is the one place that defines what a rule change means:

1. Restore the app's running processes (only in the process that enforces).
2. Save `limits.cfg`.
3. Sync PerfOptions.
4. Update QoS if the network limit changed.

Pause, Clear all, Release everything (uninstall) and Detach (close) are defined in the same place.

## The background engine (`Engine`)

- **One thread** runs a `MsgWaitForMultipleObjects` loop. It waits on the IPC events, the window process handle, and an internal "process started" event.
- **Process starts:** an ETW real-time session (`Microsoft-Windows-Kernel-Process`, keyword `0x10`, event 1 = ProcessStart) sets the internal event. A 150 ms one-shot timer then debounces bursts of starts, and the engine samples once (about 5 ms) and enforces statically (no usage-based throttling).
  - ETW normally delivers events once per second. The engine's session uses `EVENT_TRACE_USE_MS_FLUSH_TIMER` with a 200 ms flush, so a newly started app is limited about **0.6 s** after it starts (measured: 579 / 592 / 592 ms; it was 2.3 s with the default 1 s flush).
- **No work when idle:** with no rules, or while paused, the ETW session is stopped. If ETW is unavailable (for example without admin rights), the engine falls back to a 3-second timer.
- **Tray icon** uses raw `Shell_NotifyIcon`, re-added on `TaskbarCreated` (Explorer restart). The icon comes from `ExtractIconEx` of the exe.
- **Memory:**
  - The working set is trimmed 5 s after start, every 5 minutes, and after the window closes.
  - `app.config` disables the concurrent GC thread.
  - The installer runs `ngen`, so the code doesn't have to be JIT-compiled into private memory.
- **Measured** (not elevated, JIT, polling fallback):

  | Situation | Private working set |
  |---|---|
  | No limits | 0.2–0.6 MB |
  | One limit | 1.6–3.5 MB |

  CPU stayed at 0.00 s, and no `System.*` or WinForms assemblies were loaded.

## The window (`UI/`)

### Sampling pipeline

A background timer (1, 2 or 4 s) runs these steps:

1. `ProcessSampler.Sample()`
2. `EnumWindows` decides which apps have a real window: visible, unowned, not a tool window, not cloaked, with a title
3. ETW per-process network bytes are applied
4. `ProcessTree.Group()` builds the app trees
5. `ProcessMetadata` resolves the path, description (`FileVersionInfo`) and small icon (`SHGetFileInfo`), cached per PID+start time and per file
6. Limits are enforced, or the engine's status is read
7. Header totals are computed
8. The snapshot is passed to the UI thread with `BeginInvoke`

### Header totals

| Header | Source |
|---|---|
| CPU | `GetSystemTimes` |
| Memory | `GlobalMemoryStatusEx` memory load |
| Disk | Busiest physical disk's active time from `IOCTL_DISK_PERFORMANCE` (1 − Δidle ÷ Δquery time) |
| Network | Busiest adapter's utilisation from `NetworkInterface` statistics ÷ link speed |

### Per-process network

A `Microsoft-Windows-Kernel-Network` ETW session reads TCP/UDP send and receive events (IDs 10/11/26/27/42/43/58/59). Each event carries the PID and the byte count.

### `ProcessListView`

An owner-drawn control (no DataGridView):
- two-line headers: the total on top, the title below, and a sort chevron
- tree rows with chevrons and icons
- heat-map cells
- pill-shaped limit boxes that open an in-place editor **on a single click**
- column resizing, keyboard navigation, and a themed scroll bar

Only visible rows are painted. Selection and expansion survive refreshes through stable row keys.

### Theme

- `HKCU\…\Themes\Personalize\AppsUseLightTheme` is read at start and re-read on `SystemEvents.UserPreferenceChanged`, so the theme switches live.
- `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` themes the title bar.
- `SetWindowTheme("DarkMode_Explorer")` themes the scroll bars.
- A custom `ToolStripProfessionalRenderer` themes the menus.
- Icon glyphs come from *Segoe Fluent Icons* (Windows 11) or *Segoe MDL2 Assets* (Windows 10).

## Updates (`UI/Updater.cs`)

**Finding the newest version.** A `HEAD` request to `https://github.com/<repo>/releases/latest` without following redirects. The `Location` header ends in `/tag/vX.Y.Z`. This needs no API token and has no rate limit.

**Downloading.**
1. Download `TaskManagerPro.zip` and `SHA256SUMS.txt` from that release.
2. Compare the zip's SHA-256 with the list. On a mismatch nothing is installed.
3. Extract to `%TEMP%\TaskManagerPro-update-<guid>`. Folders older than a day are removed on the next update.

**Installing.** The package's own `install.bat /update [/reopen]` runs in a separate `cmd` process, and the updating process exits so its exe can be replaced.
- `/update` skips pauses.
- `install.bat` closes the running window and engine (`--exit`) and retries the copy for up to ~10 s.
- Afterwards it runs `--autostart`, which restarts the engine only if *Run in background* is on.
- With `/reopen` it reopens the window.

**Weekly check.** The scheduled task *TaskManagerPro Update* has these settings:
- `CalendarTrigger` once a week, with a 2-hour random delay
- `RunOnlyIfNetworkAvailable` and `StartWhenAvailable`, so a missed run happens at the next opportunity
- `HighestAvailable`, so no UAC prompt appears
- action: `TaskManagerPro.exe --update-check`

`--update-check` then works like this:
- offline: it logs and retries next week
- no newer version: nothing happens
- newer version: it installs, quietly unless the window is open, or asks first if *Ask before updating* is on

The installer creates the task with `--register-updates`, following the *Check for updates every week* setting. The menu toggles it, and the uninstaller removes it. The window also checks at start when the last check is more than 7 days old, and shows a blue *Update now* bar.

## Files

**`limits.cfg`** (UTF-8, compatible with Smart Task Manager):
```
# Task Manager Pro limits - app|maxCpu%|maxMemoryMB|maxDiskMBps|maxNetworkKBps (0 = no limit)
chrome|30|1024|0|0
Steam|0|0|20|512
```

**`settings.cfg` / `state.cfg`:** `key=value`. Unknown keys are kept.

**Writing:** all files are written to a `.tmp` file first and then swapped in with `File.Replace`, so a power cut never leaves a half-written file.

## Build

```
csc -target:winexe -optimize+ -platform:anycpu
    -win32manifest:src\app.manifest -win32icon:assets\app.ico
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll
    -recurse:src\*.cs
```

- The compiler supports **C# 5**, so these features can't be used:
  - `$""` string interpolation
  - `?.`
  - `nameof`
  - expression-bodied members
  - `out var`
- `anycpu` makes the app run as 64-bit on 64-bit Windows. The ETW struct offsets assume x64; on 32-bit Windows the app falls back to polling and shows no per-process network usage.

## Verification (v2.0)

A smoke test drove the real code against a helper process that burns two CPU cores and holds 300 MB:

| Check | Result |
|---|---|
| `SYSTEM_PROCESS_INFORMATION` size (x64) | 256 bytes |
| Grouping | every process in exactly one app; a child joins the tree of the app that started it |
| Limit 10% CPU / 150 MB | CPU about 9.7–10.4% (was 12.5%), working set 74 MB (was 311 MB), lower priority |
| **After the governor detached (app closed)** | **CPU 9.4–10.4%, 74 MB, BelowNormal: limits still active** |
| A later session removes the limit | cap lifted (12.4%), Normal priority, working set back to 300 MB |
| Background engine applies a rule, then `--exit` | limits applied, and still active after the engine exited |
| Engine memory | 0.2–0.6 MB with no limits, 1.6–3.5 MB with one; no WinForms/System assemblies loaded |
| Status channel | write/read round trip |
| Disk active time / network utilisation | readable |
| UI | light and dark render; Apps section first; sub-processes and icons; header totals; one-click limit editor |
| Task Scheduler XML | accepted by `schtasks /Create` |

### Elevated verification (run as administrator)

| Check | Result |
|---|---|
| ETW Kernel-Process session | starts; reports the started process with the right PID |
| ETW Kernel-Network session | starts; a 30 MB loopback transfer was attributed to the right process (about 24.8 MB/s) |
| `PerfOptions` | written (`CpuPriorityClass=5`, `IoPriority=1`, `PagePriority=2`) and recognised by the marker. **Windows itself started a new app with BelowNormal priority and Low I/O, with no Task Manager Pro running.** Removed cleanly (key deleted) |
| Network QoS | policy `TaskManagerPro_<app>` created with `ThrottleRateAction` 4,194,304 bit/s (512 KB/s); removed cleanly |
| Sign-in task | created with `HighestAvailable`, `PT0S` and `--autostart`; removed cleanly |
| Background engine (elevated, ETW) | applied the exact caps (8.8% CPU, 74 MB) to an app started after it, about 0.6 s after the app started; private working set **0.2 MB**; no polling fallback |
| `--exit`, then `--release-all` | limits stayed after the engine exited (10.3%); `--release-all` lifted the cap (12.5%), restored Normal priority and 300 MB |

### Misuse-protection verification (elevated)

| Check | Result |
|---|---|
| Data folder ACL | protected; only Administrators and SYSTEM. After install, a non-admin shell can neither list nor write the folder |
| Unsealed `limits.cfg` (`1% / 1 MB / 1 KB/s`, plus a rule for `explorer`) | not applied (needs review); the explorer rule was dropped; values raised to 4% / 64 MB / 30 KB/s (16-core PC) |
| Accept, then edit the sealed file from outside | accept re-seals; an outside edit is detected and nothing applied; saving over it is refused; reject leaves no limits and keeps a copy |
| Floors from app data | learned 1000 MB / 40% / 10 MB/s / 1000 KB/s gives floors of 504 MB / 20% / 5 MB/s / 500 KB/s; with no profile, 600 MB in use gives 304 MB; with no data, 4 processes give 128 MB and ½ core |
| Profiles | only unlimited apps are learned; they survive a reload; a tampered file is ignored |
| `--restore-all` | a limited app went back to normal (12.3% CPU, 300 MB, Normal priority); limits paused, PerfOptions removed, rules kept |
| `install.bat` | end to end: build, stop the running copy, copy, ngen native image, shortcuts (including *Restore all apps*), Installed apps entry, start |

**Not run end to end:** `uninstall.bat` (its `--release-all` step was verified above).
