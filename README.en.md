# Jianlan

[中文](README.md) ｜ **English**

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/logo-on-dark.png">
  <img src="docs/assets/logo.png" alt="Jianlan" width="420">
</picture>

A lighter, faster file manager for Windows. Extract it and run — no installation.

A dozen Explorer windows open at once, and moving files between drives means jumping back and forth.
The batch you were half-way through yesterday takes another five minutes to find again.
Jianlan is built for exactly that: **two panes side by side, tabs for every folder, colour tags to track
progress, and everything back where you left it next time**.

Current version **0.2.1**, interface available in Simplified Chinese and English.

---

## Why use it

| What gets in your way | What Jianlan does |
|---|---|
| Too many windows, hard to tell apart | Many tabs in one window; close, restore, or open a tab in a new window |
| Copying between two folders, back and forth | `F3` opens a second pane; drag directly across. Each pane remembers its own view and sort |
| You forget which files you already handled | Colour tags, several colours per file, summarised by colour in the sidebar |
| You just want a quick look at an image or a document | `Alt+P` opens the preview pane — images, text, PDF, Office files, audio and video, without launching anything |
| A slip of the hand renames or deletes the wrong thing | `Ctrl+Z` undoes the last action: rename it back, or restore from the Recycle Bin |
| Waiting seconds for a file manager to start | Cold start around 0.7 s; scrolling a 10,000-item folder stays smooth |

---

## Download and run

1. Get `Jianlan-0.2.1-portable-win-x64.zip` (about 70 MB) from **Releases**.
2. Extract it anywhere you can write, e.g. `D:\Tools\Jianlan`.
   **Avoid `C:\Program Files`** — portable mode keeps its settings next to the program.
3. Double-click `Jianlan.exe`.

No installer, no administrator rights, and **no separate .NET installation** — the runtime is inside the zip.

To verify the download, compare against the `.sha256` file published alongside it:

```powershell
Get-FileHash .\Jianlan-0.2.1-portable-win-x64.zip -Algorithm SHA256
```

**Uninstalling**: close the app and delete the extracted folder. It writes no registry keys,
installs no services, and does not start with Windows.

**Already on an older version?** Extract the new zip over the old folder — your bookmarks,
workspaces and tags all live in `data\` and are unaffected. See
[Upgrading to a new version](#upgrading-to-a-new-version).

### Requirements

| Item | Requirement |
|---|---|
| OS | Windows 10 / Windows 11, 64-bit (fully verified on Windows 11) |
| Memory | 4 GB or more; the app itself uses about 130–170 MB |
| Disk | About 190 MB extracted |
| Rights | A standard user account is enough; run as administrator to reach protected folders |

---

## Upgrading to a new version

### The one rule

All your personal data lives in `data\` next to the executable, and the release zip does **not**
contain `data\`. So "extract the new build over the old one" cannot lose settings — **as long as
you leave that folder alone**. The zip does ship `portable.flag`, so portable mode is preserved
as well.

For exactly which files live in `data\`, see [Where your data lives](#where-your-data-lives).

### Option 1: extract over the old folder (simplest)

Meant for minor updates within 0.2.x.

1. **Fully exit** Jianlan. Quit the tray icon too — and quit **any copy on another drive or in
   another folder**. The app is single-instance: as long as one copy is running, a newly launched
   one exits immediately and hands its arguments to the running instance, which looks like
   "double-click does nothing".
2. Extract the new zip into the old folder, choosing "replace" for same-name files.
3. Launch. Bookmarks, workspaces, tags, last-open tabs and window position are all still there.

### Option 2: back up first, then swap the folder (safer)

Meant for jumping across major versions, or when you want to clear leftover files as well.

1. Copy the old folder's `data\` somewhere else. **`settings.json` alone is not enough** —
   workspaces live in `workspaces.json` and the tag library in `data\tags.jsonl`.
2. Empty the old folder.
3. Extract the new zip into the original location.
4. Copy the backed-up `data\` back.

Do not hand either option to a script or a "one-click replace" tool. Those touch the program
folder itself, and you cannot see in advance where they will go wrong or what they will do to it;
done by hand in Explorer, every step — what changed, what was deleted — happens in front of you.

### Four things to check before upgrading

| Check | Why it matters |
|---|---|
| Every copy is closed | The single-instance mutex is global; another running copy makes the new one exit |
| `data\` was not copied while the app was running | `session.json` comes along, and the new version shows a one-off "last session did not finish" banner — delete that file, it holds no settings |
| The backup covers all of `data\` | `settings.json` alone misses workspaces and the tag library |
| The install path stays the same | In-place extraction keeps desktop shortcuts and pinned taskbar entries working |

### How to confirm the upgrade worked

Open **Settings → About**: the version reads like `0.2.1+b5890f5` (release plus build commit).
Check that your bookmarks and workspaces are there and the last-open tabs came back, then delete
the backup.

---

## First run

The first tab is the **Home** page with four sections: common folders, drives, recent places, favourites.
Click any of them to jump straight in.

A five-minute tour, in the order most people need it:

1. **Open two panes**: press `F3`, pick the source on the left and the destination on the right, drag across.
2. **Tag a file**: right-click → "Tags" → pick a colour. A dot appears before the name, and the sidebar
   groups files by colour.
3. **Bookmark**: `Ctrl+D` bookmarks the current folder or the selection.
4. **Save a workspace**: open the folders you are working with as tabs, then click "Save current tabs…"
   in the Workspaces section of the Home page (or right-click the "Workspaces" heading in the sidebar)
   and restore the whole set next time, split pane and pinned tabs included.
5. **Close and reopen**: window size, tabs, split ratio and column widths all come back.

### The window at a glance

| Region | What is in it |
|---|---|
| Tab bar (top) | One tab per folder; `＋` opens a new one, right-click to pin, close others, or open in a new window |
| Sidebar (left) | Common folders, This PC (per-drive free space), favourites, tags, Recycle Bin and Network; drag its edge to resize, double-click to reset |
| Main area | Toolbar (back / forward / up / refresh, breadcrumb address bar, search, view switch), file list, status bar |
| Preview pane (right, `Alt+P`) | Images, text, code, PDF, Office files, audio and video; with a single file selected you can open it, copy its path or delete it |

---

## Everyday operations

Only shortcuts that actually work are listed; mouse gestures follow.

### Browsing and navigation

| Action | Shortcut |
|---|---|
| Back / forward / up one level | `Alt+←` / `Alt+→` / `Alt+↑` (or `Backspace`) |
| Jump to the address bar | `Ctrl+L` (auto-complete: `Tab` completes, `Enter` navigates) |
| Search the current folder | `Ctrl+F`; `Esc` clears |
| Refresh | `F5` or `Ctrl+R` |
| Bookmark current folder / selection | `Ctrl+D` |
| Mouse side buttons | Back / forward |

### Tabs

| Action | Shortcut |
|---|---|
| New / close / reopen the last closed | `Ctrl+T` / `Ctrl+W` / `Ctrl+Shift+T` |
| Next / previous tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Jump to tab N / the last tab | `Ctrl+1…8` / `Ctrl+9` |

Mouse: middle-click a tab to close it, drag to reorder, right-click for pin, close others,
copy path, open in a new window. Middle-click a folder, or `Ctrl`-click it, to open it in a new tab.

### File operations

| Action | Shortcut |
|---|---|
| Open | `Enter` |
| Copy / cut / paste | `Ctrl+C` / `Ctrl+X` / `Ctrl+V` |
| Rename | `F2`, or **click the file name again** while the row is selected |
| New folder / new text file | `Ctrl+Shift+N` / `Ctrl+Shift+M` |
| Delete to Recycle Bin / delete permanently | `Delete` / `Shift+Delete` |
| Undo the last action | `Ctrl+Z` |
| Copy full path | `Ctrl+Shift+C` |
| Properties | `Alt+Enter` |
| Select all | `Ctrl+A` |

Renaming happens in place, right in the list — no dialog. Only the part before the extension is
selected when you start (`report.docx` selects `report`; folders select the whole name). `Enter`
confirms and leaves you on the same row. **Only `Enter` renames anything** — `Esc`, clicking empty
space or another row, clicking the toolbar or sidebar, switching tabs, navigating away, or scrolling
the row out of view all discard the edit without touching the disk. While the editor is fresh,
`←`/`↑` jump to the far left and `→`/`↓` to the far right; once you type, click inside the box, or
press an arrow once, they move one character at a time. Changing the extension asks first: renaming
`notes.docx` to `notes.md` would leave a file that no longer opens, so `Enter` brings up a confirmation
— say yes to go ahead, say no to stay put with the new name still in the box. An invalid or duplicate
name does not close the editor: the box turns red and the next keystroke overwrites it. Newly created
folders and text files drop straight into rename.

Drag and drop: dropping moves within a drive and copies across drives; hold `Ctrl` to force a copy,
`Shift` to force a move. Drop onto a folder row to put the items inside it. Drop onto another
application to hand the files over.

Clipboard: what `Ctrl+C` / `Ctrl+X` puts on the **system clipboard**, so you can paste it straight
into File Explorer, and files copied in File Explorer paste into Jianlan the same way. A cut is a
move on the receiving side, not a copy-then-delete. Quitting Jianlan first is fine — the data is
handed over the moment you copy.

### Views and panels

| Action | Shortcut |
|---|---|
| Details / list view | `Ctrl+Shift+1` / `Ctrl+Shift+2` |
| Cycle theme (system → light → dark → high contrast) | `Ctrl+Shift+D` |
| Toggle the second pane | `F3` (or `Ctrl+Shift+E`) |
| Switch focus between panes | `Tab` or `F6` |
| Toggle the preview pane | `Alt+P` (or `Ctrl+Shift+P`) |
| Settings | `Ctrl+,` |

---

## What else it does

- **Tags**: eight colours by default, renameable ("red" can become "urgent"), up to 32 entries;
  the tag library exports to JSON.
- **Favourites**: drop something in to bookmark it; group, rename, change icon and colour, reorder by
  dragging; broken entries are greyed out and can be cleaned up in bulk.
- **Workspaces**: save the current set of tabs (including the other half of the split and pinned tabs)
  and restore them together. Up to 50.
- **Search**: filters as you type and stays responsive even in huge folders; shows the match count and
  tells you when the result list is truncated.
- **Folder size**: calculated only when you ask for it, right-click → "Calculate folder size";
  `Esc` stops it at any time.
- **Thumbnails**: real frames for images and videos in the list, with an on-disk cache you can clear or cap.
- **Columns**: show, hide, reorder and resize the four columns; remembered per folder type.
- **Multiple windows**: right-click a tab → "Open in new window". The two windows are independent —
  closing one leaves the other running.
- **Single instance**: launching again just hands the path to the running window instead of starting
  another. You can also drop a folder onto `Jianlan.exe` to open it.
- **Crash recovery**: after an unexpected exit the next launch tells you so and offers a log export
  (one click on the About page produces a zip).

## What is in Settings

`Ctrl+,` opens seven pages:

| Page | Contents |
|---|---|
| General | Interface language (system / Simplified Chinese / English), start-up behaviour, default folder for new tabs |
| Appearance | Theme (light / dark / system / high contrast), accent colour, font and size (8–24 pt), density, sidebar and status bar toggles |
| File list | Default view and sort, hidden files, file extensions, single-click to open, thumbnail cache |
| Actions | Delete confirmation, bulk delete confirmation, overwrite confirmation, confirm before opening executables |
| Privacy & storage | Clear history, tag library import/export and stats, privacy statement, reset sections |
| Tags | Names and colours of the eight tags |
| About | Version, check for updates, export logs, licence |

---

## Where your data lives

Settings, history, favourites, tags and workspaces stay **on your machine**. Nothing is uploaded.

| How it runs | Data location |
|---|---|
| Portable zip (official build) | `data\` next to the program |
| Built from source without portable mode | `%LOCALAPPDATA%\Jianlan` |

`data\` contains:

| File | Contents |
|---|---|
| `settings.json` | Settings, favourites, history, last-open tabs and window position |
| `workspaces.json` | Workspaces |
| `data\tags.jsonl` | Colour tag library |
| `session.json` | "Did the last session end cleanly" marker; auto-generated, removed on clean exit, holds no settings |
| `logs\` | Run logs |

**To back up or move to another machine, copy the whole `data\` folder**; deleting it resets the app.
The same applies when upgrading — never overwrite or delete `data\`, see "Upgrading to a new version"
above.

**Privacy**: no telemetry, no uploads, and the program makes no network requests on its own. The only
outbound action is the one you trigger yourself with "Check for updates", which opens the release page in
your browser. See [SECURITY.md](SECURITY.md).

---

## FAQ

**The taskbar shows a blank white icon.**
Fixed in 0.2.1. If you still see one, first make sure you are running a 0.2.1-or-later build — an older
extracted folder or a stale system icon cache will still show the old icon, so extract the current zip into
a fresh folder and try again.

**Double-clicking does nothing, or antivirus blocks it.**
The binary is not code-signed, and security software often warns about new unsigned programs. If you got
it from this repository's Releases, allow it; you can also verify the `.sha256` first.

**Why did Delete send the file to the Recycle Bin?**
That is the default so the action stays undoable. Use `Shift+Delete` for permanent deletion
(it asks for confirmation).

**`Ctrl+,` does not open Settings.**
Chinese IMEs sometimes swallow punctuation chords. Use the menu entry instead, or switch to an English
input state.

**Copying a multi-GB file shows only "Copying…" with no progress bar.**
This version has no transfer progress window or cancel button yet; both are on the backlog.
Avoid triggering another paste while a copy is running.

**Will it change my file associations?**
No. It registers no file types and does not take over context menus; "More options…" simply invokes the
system menu.

**Windows 7 or macOS?**
Windows 10/11 64-bit only for now. The platform abstraction and per-OS operation mapping are documented in
the requirements specification, which is what a macOS or Linux port would be built against.

---

## Known limitations (0.2.1)

- Copying or moving large files has **no progress bar and no cancel**, only a status bar line.
- **No large-icon or tile view** — details and list only.
- A row shows at most **three colour dots**; a fourth colour is still recorded and counted in the
  sidebar, it just does not fit on the line.
- Tags, favourites and workspaces have **no single export/import for everything** (the tag library does).
- Windows 10 compatibility, 8-hour stability and offline scenarios are not fully verified yet, so this
  version makes no claims about those.

The reasoning behind these trade-offs is in [CHANGELOG.md](CHANGELOG.md) and
[docs/backlog.md](docs/backlog.md).

---

## For developers

You need the .NET 9 SDK. Build, packaging and performance-measurement commands are in
[CONTRIBUTING.md](CONTRIBUTING.md). Architecture, UI rules and the performance gate live in
[docs/architecture.md](docs/architecture.md), [docs/ui-guidelines.md](docs/ui-guidelines.md) and
[docs/performance-gate.md](docs/performance-gate.md). The full requirement set is in
[docs/requirements.md](docs/requirements.md).

## Licence

[Apache-2.0](LICENSE). Copyright 2026 Qianshan.
