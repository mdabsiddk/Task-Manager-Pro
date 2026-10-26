# Changelog

## 2.5.4 (2026-10-27)

- **Memory limit is a pure ceiling.** The caps are no longer lifted below some share of the limit (that let the app jump above the limit and be pulled back). Once an app has gone over its limit, the caps stay and are recomputed at every check so they add up to exactly the limit: the app can grow up to the limit when it needs to, is never pushed below it, and is brought back whenever it goes over. Removing the limit or pausing lifts them. Measured: 800 MB limit, 603 → 793 MB with no overshoot; 1500 MB limit, 919 MB.

## 2.5.3 (2026-10-27)

- Memory caps are lifted completely when the app drops below **80%** of its limit (was 20%; 70% in 2.5.0).
- Notice cards show only the ring animation: the number of seconds left is gone.

## 2.5.2 (2026-10-27)

- Notice cards stay **3 seconds** (was 3 minutes); the ring around the icon counts the seconds down smoothly, and hovering still pauses it.
- **Clear minimum-limit notices, only when needed:** opening a Max box no longer shows a notice. Saving a value below the app's minimum shows *Limit not saved* with the exact lowest value and the real reason, for example "The lowest memory limit for Electron is 128 MB (32 MB for each of its 4 processes)." Earlier the reason could wrongly say "a quarter of its use" when the Windows per-process floor was the higher one.

## 2.5.1 (2026-10-27)

- Memory caps are now lifted completely only when the app drops below **20%** of its limit (was 70%). Until then the caps always add up to the whole limit, so they only matter when the app tries to go over it. Measured: with a 1500 MB limit the test app got all it wanted (919 MB) while the caps stayed in place.

## 2.5.0 (2026-10-27)

- **Memory caps only bite when the app tries to go over its limit.** Once an app has gone over, its caps are recomputed at every check so they always add up to the whole limit: the app is pulled back to the limit, but never held below it. Below 70% of the limit the caps are lifted completely (was 85%).
  - Measured (Chrome-like test app, 926 MB): 600 MB limit → 603 MB; then 800 MB → the caps grow and the app uses 799 MB; then 1500 MB → caps lifted, 904 MB.
- **Lower minimum limits:** a limit may now go down to a quarter (25%) of what the app uses right now (was half of its usual use). When the app isn't running or is already limited, a quarter of its learned usual use applies. The Windows-level floors stay.
  - Saved limits are no longer raised when they are loaded again; a limits file changed from outside is still held to the app's learned minimum.
- **New notice cards (toasts)** for every notice in the app: bottom-right above the taskbar, in the current theme, with a countdown ring around the icon (3 minutes; paused while the mouse is over the card) and a ✕ button. Up to three stack; they never take the focus. Limit hints, errors, update and save messages, and the engine's notices (which were tray balloons) all use them, with shorter wording. Questions that need a Yes/No answer stay dialogs.

## 2.4.0 (2026-10-23)

- **Memory limits use the whole limit.** The limit is now one budget for the whole app (all its processes), measured like the Memory column:
  - below the limit nothing is capped, so the app runs at full speed
  - above it, each process gets a share in proportion to what it uses, so the app as a whole stays at the limit
  - the caps are lifted again below 85% of the limit
  - Earlier versions split the limit equally between processes: with 25 Chrome processes, 1000 MB meant 40 MB each, so Chrome stayed at 200–250 MB and was slow. Caps left by earlier versions are removed automatically.
  - Measured with a Chrome-like test app (926 MB): a 600 MB limit settles at 603 MB within 3 seconds with the biggest process keeping 163 MB (the old method: 46 MB each); a 1500 MB limit leaves it at its full 905 MB.
  - With the window closed, the engine checks the app's total every 5 seconds, only while a memory limit exists.
- **New app icon:** a green tile with a gauge whose needle stays below the amber limit zone.
- The **Restore all apps** Start menu shortcut is no longer created (and is removed on update). Use **Pause limits** in the window, or `TaskManagerPro.exe --restore-all`.

## 2.3.0 (2026-10-23)

- **Limits survive a restart.** While limits are set, the small engine (0.2–3.5 MB, no CPU while idle) starts at sign-in and when the window closes, so the exact CPU and memory caps also apply after a restart and to apps opened later. With no limits set nothing runs, and the engine exits by itself when the last limit is removed.
  - Measured with a 20% CPU limit after a simulated restart: 22.3% (96.1% without the engine).
  - The option is now **••• → Keep limits after closing and restart** (on by default; it replaces *Run in background*).
- **App icons and names like Task Manager:**
  - Store / MSIX apps (WhatsApp, Microsoft Store, Photos, Settings, Search, ...) show their real name and logo from the Start menu, instead of a generic icon.
  - Protected processes (Microsoft Defender, Hyper-V and driver services, ...) now get their path, name and icon too, read from Windows without opening the process.
  - Windows programs without an icon of their own get the Windows icon, and service hosts a gear.

## 2.2.1 (2026-10-22)

- **One place for the version:** the new `VERSION` file. The program file, About window, tray tip, updater, *Installed apps*, Setup.exe, release notes and the GitHub release all take the version from it.
- **Releases publish themselves:** change `VERSION` (or run `tools\set-version.ps1 X.Y.Z`) and push to main. GitHub then creates the tag and the release. The release notes include this version's changelog.
- The README shows the current version with a live badge, so the docs no longer hold a version number to update.

## 2.2.0 (2026-10-22)

- **New About window** (••• → About Task Manager Pro):
  - version and license
  - developer **Md. Abu Bakar**, with icon links to the website, email, GitHub, LinkedIn and Facebook
  - links open in the browser with normal (not administrator) rights; each address is also shown as selectable text with a **Copy** button
- **Developer signature** in the program and setup file details (Company / Copyright), in *Installed apps* (Publisher, support and update links), in LICENSE and in the README.

## 2.1.1 (2026-10-22)

- **Updates:**
  - The update installer now always runs in the background and writes its output to `update.log` in the data folder.
  - An open window closes and reopens in the new version by itself.

## 2.1.0 (2026-10-22)

- **Updates:**
  - **Automatic:** a weekly scheduled task, *TaskManagerPro Update*, checks GitHub when the internet is available. If a newer version exists it is installed (or the user is asked first, with *Ask before updating*); if not, nothing happens. Missed checks run at the next opportunity.
  - **Manual:** **••• → Updates → Check for updates now**, and a blue *Update now* bar when a new version is available.
  - **Safety:** downloads only from this repository's GitHub Releases, installed only if the SHA-256 checksum matches; limits and settings are kept.
- **Install guide:** the browser, SmartScreen and UAC screens, step by step with pictures.

## 2.0.2 (2026-10-22)

- **One-line install:**
  - Windows security blocked the elevated `irm … | iex` relaunch ("Access is denied", no UAC prompt). `install.ps1` now saves itself to a temp file and re-opens that file as administrator.
  - `uninstall.ps1` elevates the installed `uninstall.bat` directly.
- **Installer:**
  - The version shown in *Installed apps* was empty.
  - The app is now started with the install folder as its working directory, so the temporary download folder can always be deleted.

## 2.0.1 (2026-10-22)

- **License:** Task Manager Pro is released under the **MIT License**: free and open source for everyone, with no subscription and no fees. `LICENSE` is included in the downloads.
- **README:** clearer badges.
  - The release badge now says *release*.
  - New *downloads* and *license* badges.
  - The *Windows 10 | 11* badge now opens the requirements section.
  - A big **Download TaskManagerPro-Setup.exe** button links straight to the installer.
- **Installer:** reads the version shown in *Installed apps* from the program file itself.
- **About dialog:** shows the license and the project page.

## 2.0.0 — Task Manager Pro (2026-10-22)

The project is renamed from *Smart Task Manager* to **Task Manager Pro**. It has been rebuilt to look and work like the Windows 11 Task Manager, and so that **nothing has to keep running in the background**.

### New
- **Windows 11 Task Manager design:**
  - Processes title and search box
  - Run new task, End task, Pause limits and ••• options
  - two-line column headers with **total CPU / Memory / Disk / Network usage**
  - heat-map cells and a themed scroll bar
- **Follows the system theme automatically** (dark/light) and switches live, including the title bar, scroll bars and menus. It can also be forced to Light or Dark.
- **App trees with icons:** apps expand to show every sub-process, like Task Manager. **End task** works on a whole app or on a single sub-process.
- **Apps / Background processes / Windows processes** sections with counts (*Group by type*).
- **Network column** with per-process throughput (ETW), plus disk active time and network utilisation in the headers.
- **"Max" column next to each usage column** (Max CPU, Max memory, Max disk, Max network). **One click to type**; `Tab` moves to the next limit.
- **Limits stay active after the window is closed. No background process is needed.**
  - Running apps keep their CPU hard cap, memory cap and priorities.
  - Apps started later get lower CPU, I/O and memory priority through Windows' own *PerfOptions*.
  - Network limits are permanent Windows QoS policies.
- **Optional "Run in background" engine** for exact caps on apps started later:
  - event-driven (ETW process-start notifications with a 200 ms flush): new apps are limited about 0.6 s after they start
  - uses 0.2–3.5 MB of RAM and about 0% CPU, with no WinForms
  - starts with Windows with no UAC prompt
- **Protection against misuse:**
  - **Each app's lowest allowed limit comes from its own usage:** at least half of what it normally uses, learned while it runs without a limit. There is no fixed list of numbers. It is enforced when typing, on every file load, and in the engine.
  - **Admin-only data folder:** programs without administrator rights can't touch the settings.
  - **HMAC-sealed `limits.cfg` and `profiles.cfg`:** a file changed from outside is not applied until the user reviews it (yellow bar, then *Review limits*).
  - **Windows components can't be limited,** even through the file.
  - **Self-healing persistent limits:** PerfOptions and QoS are re-synced from the sealed rules on every start.
  - **Emergency "Restore all apps"** Start-menu shortcut (`--restore-all`).
- **Install from GitHub:**
  - One-line PowerShell installer (`irm …/install.ps1 | iex`) with SHA-256 verification, and a matching `uninstall.ps1`.
  - Single-file **TaskManagerPro-Setup.exe** and a **TaskManagerPro.zip** on GitHub Releases, built and published by GitHub Actions when a version tag is pushed.
  - A build check on every push.
- Right-click menu: **Switch to**, **Set limits**, **Remove limits**, **Open file location**, **Properties**.
- Remembers window size and position, column widths and sort order.
- **Settings:** *Always on top*, *Update speed* (High/Normal/Low), *Theme*.
- The installer **pre-compiles to native code** (ngen) and **upgrades Smart Task Manager** automatically, moving your limits over.
- The project, UI and documentation are now in **English**, using Task Manager's terminology.

### Changed
- **Closing the window ends the window process completely,** freeing all of its memory.
  - Limits are **only** undone by Pause, removing a limit, or uninstalling. Closing no longer undoes them.
- **The CPU limit is an exact Job Object hard cap.**
  - Before: core affinity plus priority, which let a 10% limit run at about 12.5%.
- **Limits apply to the whole app tree** (keyed by the root program), not just to processes with the same name.
- **Autostart only exists when *Run in background* is on.** It uses Task Scheduler with highest privileges, no 72-hour limit and no battery restrictions.
- **Data folder** is now `%ProgramData%\TaskManagerPro`, with `limits.cfg` compatible with the old format.
- **The shortcut no longer needs a UAC prompt** when Task Manager Pro is already running: the app uses `asInvoker`, a medium-integrity *Show* event, and elevates only when needed.

### Fixed (from Smart Task Manager)
- **Limits were not saved:** `limits.cfg` was kept in Program Files, which isn't writable.
- **No administrator rights:** most limits and QoS policies silently failed, and the Run-key autostart couldn't start an admin app.
- **Removing a limit didn't restore the app.**
- **Handle and GDI leaks:** `Process` objects were never disposed, and a new `Font` was created per row on every refresh.
- **Enforcement stopped while a limit was being edited.**
- **One bad line in `limits.cfg` discarded every rule.**
- **CPU and disk rules undid each other's priority.**
- **Affinity mask overflow** on machines with 64+ cores.
- **Broken Bengali text in the batch files** (code page).
- **Quoting bugs** in the QoS PowerShell command.

## 1.0.0 — Smart Task Manager

- First version: a single `Install_SmartTaskManager.bat` that compiled embedded C# code ([archive/v1](archive/v1)).
