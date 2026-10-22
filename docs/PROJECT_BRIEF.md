# Project Brief

## Requirements

**Original request (v1)**

Build an installable Windows task manager, modelled on open-source task managers, with a clean UI like Task Manager:
- Show every program in a table, with running apps on top, ordered by RAM/CPU use.
- Columns are sortable.
- Next to Task Manager's usual columns, four extra columns set the **maximum CPU, memory, disk and network** each program may use.
- It must never break Windows' safety rules or crash anything, while forcing programs to stay within their limits.
- It must be lightweight: set it once and it keeps working all year, never slowing the user down or wasting RAM.

**Follow-up (Smart Task Manager → v2)**

Improve the project, including:
- End task
- running in the background when closed
- starting minimised in the tray

Also organise the files and write good Markdown documentation.

**Task Manager Pro v2.0**

- Name the project "Task Manager Pro v2.0".
- Follow the system theme (dark/light) automatically.
- Look and work like the Windows 11 Task Manager:
  - apps with their sub-processes, shown with icons, which can be viewed and ended
  - total usage percentages in the column headers
- Put each "Max" limit column right next to its usage column, with one click to type.
- Use English, with Task Manager's terminology.
- Reduce the background RAM use (about 16 MB before).
- **Best of all, the limits should keep working after the app is closed, so nothing has to run in the background at all.**

## Requirement → implementation

| Requirement | Where / how |
|---|---|
| Installer | `install.bat`: self-elevates, builds, installs to Program Files, runs ngen, creates shortcuts, adds an Installed apps entry, upgrades Smart Task Manager. `uninstall.bat` removes everything |
| Name "Task Manager Pro v2.0" | Window title, assembly info, installer, data folder, task name |
| Windows 11 Task Manager look | `UI/MainForm.cs`, `UI/ProcessListView.cs`, `UI/Controls.cs` |
| Follows system dark/light theme | `UI/Theme.cs`: registry `AppsUseLightTheme` + `UserPreferenceChanged`; dark title bar, scroll bars, menus |
| Apps with sub-processes and icons; end either | `Core/ProcessTree.cs`, `UI/ProcessMetadata.cs`, tree rows; *End task* on an app or on a sub-process |
| Total % in column headers | CPU (`GetSystemTimes`), Memory (memory load), Disk (`IOCTL_DISK_PERFORMANCE`), Network (adapter utilisation) |
| Running apps on top, by usage | *Group by type* puts **Apps** first; default sort is Memory, largest first; every column is sortable |
| Max column beside each usage column; one click to type | Max CPU / Max memory / Max disk / Max network; single-click editor with `Tab` to the next |
| Never break safety / never crash | Documented, reversible APIs only; critical and Windows-component lists; PID + start-time checks; exact restore |
| Force programs to stay within limits | Job Object CPU hard cap, working-set hard max, I/O priority, Windows QoS |
| Keep working after closing; nothing in the background | Kernel settings stay on running apps (`Detach`); `PerfOptions` and QoS cover apps started later |
| Low background RAM when wanted | Optional engine: mscorlib + Win32 only, ETW event-driven, 0.2–3.5 MB measured |
| Set once, works all year | Limits in `%ProgramData%`; persistent OS-level limits; optional sign-in task without UAC |
| English, Task Manager terminology | UI, scripts, documentation |
| Organised files, Markdown docs | `src/Core`, `src/Engine`, `src/UI`, `assets/`, `docs/`; README, ARCHITECTURE, CHANGELOG |

## Open-source references

- **System Informer** (formerly Process Hacker):
  - sampling every process with one `NtQuerySystemInformation` call
  - I/O priority
  - ETW for per-process network
- **ProcessGovernor** (lowleveldesign): Job Object CPU rate hard caps.
- **Windows Task Manager:** grouping apps into process trees, the Apps / Background / Windows sections, header totals, heat map and End task behaviour.
