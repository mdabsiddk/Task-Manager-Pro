# Changelog

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
