# Fire Department Attendance Tracker (Windows)

A native Windows desktop app (C# / .NET 8 / WPF) that tracks fire department members, meetings, calls, and attendance. It also handles quarterly Excel exports, meeting minutes (Word), and accounting.
Version 2 is a full rewrite of the original Python/tkinter app. It keeps every feature and reads the **same database and settings files**.

---

## Installing

1. Open the **[latest release](../../releases/latest)** page.
2. Download **`FireDeptAttendance-win-Setup.exe`** and run it.
   - It installs per-user (no admin rights needed) and adds Start-menu and desktop shortcuts.
   - Windows SmartScreen may warn because the installer is not code-signed. Click **More info → Run anyway**.
3. Start **Fire Department Attendance Tracker** from the Start menu.

### Install location

- **Default:** `Setup.exe` is a one-click installer with no folder-picker screen. It installs to `%LocalAppData%\FireDeptAttendance` (usually `C:\Users\<you>\AppData\Local\FireDeptAttendance`). The program files are in its `current` sub-folder, and updates replace that sub-folder.
- **Custom folder:** run the installer from a Command Prompt with `--installto`:

  ```
  FireDeptAttendance-win-Setup.exe --installto "D:\Apps\FireDeptAttendance"
  ```

  Use an **empty, dedicated folder**. If the folder already has files in it, the installer asks before it **replaces the folder's contents**. Add `--silent` to skip the installer's UI.
- **Portable:** `FireDeptAttendance-win-Portable.zip` runs without installing. Extract it to any folder you can write to (for example a USB stick or `D:\Apps`) and run `FireDeptAttendance.exe`. Velopack documents the portable package as able to update itself, but this has not been tested with this app.
- **Settings → Data & Storage → Install location** shows where the running copy lives and has an **Open folder** button.

The install folder holds **only program files**. Your database and settings are kept separately in the data folder (see below), so reinstalling, updating, or moving the install folder never touches your records.

## How updates work

- Each time the app starts, it checks this repository's GitHub Releases in the background. No account or token is needed.
- If a newer version exists, you'll see **"Update available (vX.Y.Z) — install now?"**. Click **Yes** to download it, replace the program files, and restart the app.
- Use **Check for Updates** (on the toolbar) to check manually. The current version is shown next to it.
- When you're offline, the startup check fails silently.
- Updates only replace the program files. **Your database and settings are never touched**, because they live in a separate data folder (see below).

## Where your data is stored

By default, all user data lives in:

```
%APPDATA%\FireDeptAttendance\
    fire_dept.db      ← members, events, attendance, minutes (SQLite)
    settings.json     ← theme, font size, rates, folder preferences, minutes header/footer
    signature.png     ← optional signature image used in minutes (any image extension)
    backups\          ← automatic copies made before every import / restore
```

(Usually `C:\Users\<you>\AppData\Roaming\FireDeptAttendance`.)

### Data & Storage window

Open it with the **💾 Data & Storage** button on the status bar, from **Settings → Data & Storage…**, or from the **Excel Export** tab → **Data** section. It has four sections:

- **Data folder:** shows the current folder and how many members and events it contains.
  - **Change…** picks a new folder, for example a OneDrive or network folder, or another drive. You then choose one of:
    - **Move** your data there (the originals are deleted after a successful copy).
    - **Copy** it there (the originals are kept).
    - **Use the data already in that folder** (nothing is copied).

    If the target folder already holds a database, you can't overwrite it by accident. Invalid or read-only folders are rejected with an explanation.
  - **Open folder** opens the folder in Explorer.
  - **Reset to default** switches back to `%APPDATA%\FireDeptAttendance`.
- **Import previous data:** import from a database file, re-import from the last location, or restore the previous backup (see below).
- **Default export folders:** where the Excel export, the batch minutes export, and single minutes documents are saved. Each one has **Browse…**, **Use Documents**, and **Open folder** buttons. If a folder is not set or no longer exists, the Documents folder is used. The folder you last saved to is remembered automatically.
- **Install location:** the program's version and folder (see *Install location* above).

How the chosen data folder is remembered: a small pointer file, `%APPDATA%\FireDeptAttendance\location.json`, stores the custom folder. Delete that file to go back to the default. If the environment variable `FIREDEPT_DATA_DIR` is set, it overrides everything, and the folder setting is locked in the app. If a custom folder becomes unavailable (for example, an unplugged drive), the app offers to start with the default folder instead.

**Manual backups:** close the app, then copy the data folder.

## Importing your previous data (from the Python version or another PC)

The Python app kept `fire_dept.db` (plus `settings.json` and `signature.*`) **in the same folder as `app.py`** or, for the packaged build, **next to its `.exe`**. Find that file first. Its size should be more than a few KB if it holds records.

1. Open **Data & Storage → Import from database…**, or click **📥 Import from database…** on the Excel Export tab.
2. Pick the old `fire_dept.db`. The app checks that it really is an attendance database and shows a preview: the number of members (active/total), events (meetings/calls), quarters, attendance marks, and the date range.
   - If the file contains **no members and no events**, you get an explicit warning. That file is probably not the one you want.
3. Confirm the import. You'll also be asked whether to bring over that folder's `settings.json` and signature image. Your folder preferences are always kept.
4. Before anything is replaced, the current database is saved to `backups\fire_dept_YYYYMMDD_HHMMSS_before_import.db`. The file is copied with SQLite's backup API, which correctly picks up `-wal`/`-journal` files, and older schemas are upgraded automatically. **The source file itself is never modified.**

Other options:

- **Re-import from last location:** the last imported file is remembered and can be re-imported with one click.
- **Restore previous backup:** puts back the most recent file from `backups\`. The current data is backed up first, so a restore can be undone too.
- **First run:** if the data folder has no database, or only an empty one, the app first searches common locations for an old `fire_dept.db`:
  - the program folder and its parent;
  - `Documents` / `Desktop` sub-folders named like `FireDeptAttendance` or `fire_dept_attendance` (and their `dist` sub-folders);
  - `Downloads\FireDeptAttendance` and `Downloads\fire_dept_attendance`.

  Empty databases are skipped. If nothing is found, the app **asks** you to locate the file, start with an empty database, or decide later. It no longer starts empty silently.
- **Manual:** close the app and copy `fire_dept.db`, `settings.json`, and `signature.*` into the data folder.

## Changelog

### v2.0.4
- Reduced top page margin to 0.5" so the minutes header starts higher (editor + Word export). Side and bottom margins stay at 1".

### v2.0.3
- **Word-like page view for meeting minutes.** You type the minutes on a white US-Letter page that matches the Word export: 8.5 × 11 in, 1 in margins, Calibri 11 pt, single spacing, no gaps between paragraphs, signature 2 in wide.
  - The header and footer from Settings appear above and below the minutes, in export order, with placeholders filled in and the signature shown. Dashed separators with **Header / Minutes / Footer** tags mark each area.
  - A ribbon has **B / I / U**, **Insert signature**, **Spell check** and **Zoom** (50–200 % or *Page width*; Ctrl + mouse wheel also zooms).
  - Dashed red markers show where a new page starts, and the ribbon shows the approximate page count.
  - Clicking anywhere on the page places the cursor in the nearest editable area.
- **Settings → Minutes Header / Footer** uses the same page view. You edit the header and footer in place, with grayed sample minutes in between.
- Pasted text loses fonts, sizes and colours that the export can't keep (bold / italic / underline stay), so the page matches the export.
- The exporter now takes its page size, margins, font and spacing from one shared layout definition, which the page view also uses.
- No change to how minutes are stored (`rich-v1`). Existing minutes, headers and footers load unchanged.

### v2.0.2
- **Meeting minutes editor:**
  - **Tab** now inserts a tab character in the minutes body, replacing any selected text, instead of jumping to the next control.
  - **Shift+Tab** removes one leading tab from the selected line(s).
  - **Ctrl+Tab** / **Ctrl+Shift+Tab** move focus out of the editor.
  - Tabs are saved with the minutes and appear as real tabs in Word (.docx) exports, both single and batch, and in .txt exports.
- The minutes body now fills the dialog and scrolls (scrollbar and mouse wheel), so long minutes are fully visible. Both minutes dialogs fit the screen height, can be resized, and keep their buttons visible.

### v2.0.1
- Import from database with preview, automatic backup and restore; configurable data folder; default export folders; install-location info.

### v2.0.0
- Native Windows (C# / WPF) rewrite with automatic updates.

## Publishing a new version (maintainers)

Releases are built entirely by GitHub Actions (`.github/workflows/release.yml`):

```bash
# after committing your changes to main
git tag v2.0.1          # bump the version
git push origin v2.0.1
```

The workflow, running on `windows-latest`, then does the following:

1. Runs the core test suite.
2. Publishes a self-contained `win-x64` build. The version is taken from the tag, so `v2.0.1` becomes `2.0.1`.
3. Packages it with [Velopack](https://velopack.io): Setup.exe, a portable zip, full and delta update packages, and `releases.win.json`.
4. Creates the GitHub Release and uploads every asset.

Installed copies pick up the new release on their next start. Tags must be semantic versions (`vMAJOR.MINOR.PATCH`) and must increase.

## Building locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```bash
dotnet build FireDeptAttendance.sln -c Release
dotnet run --project src/FireDeptAttendance               # run the app
dotnet run --project tests/FireDeptAttendance.Tests      # core tests (optionally pass a path to a fire_dept.db)
```

Set the `FIREDEPT_DATA_DIR` environment variable to use a different data folder (handy for testing).

## Project layout

| Path | Purpose |
|---|---|
| `src/FireDeptAttendance.Core` | Database (Microsoft.Data.Sqlite), settings, dates, Excel export (ClosedXML), Word export (DocumentFormat.OpenXml), rich-text model. No UI. |
| `src/FireDeptAttendance` | WPF app: main window, Members / Attendance / Export / Accounting tabs, dialogs, themes, Velopack updater. |
| `tests/FireDeptAttendance.Tests` | Console test runner for the core library. |
