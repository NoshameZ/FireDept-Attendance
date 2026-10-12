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

A portable zip (`FireDeptAttendance-win-Portable.zip`) is also attached to each release. It runs without installing, but only the **installed** copy updates itself automatically.

## How updates work

- Each time the app starts, it checks this repository's GitHub Releases in the background. No account or token is needed.
- If a newer version exists, you'll see **"Update available (vX.Y.Z) — install now?"**. Click **Yes** to download it, replace the program files, and restart the app.
- Use **Check for Updates** (on the toolbar) to check manually. The current version is shown next to it.
- When you're offline, the startup check fails silently.
- Updates only replace the program files. **Your database and settings are never touched**, because they live in a separate data folder (see below).

## Where your data is stored

All user data lives in:

```
%APPDATA%\FireDeptAttendance\
    fire_dept.db      ← members, events, attendance, minutes (SQLite)
    settings.json     ← theme, font size, rates, default locations, minutes header/footer
    signature.png     ← optional signature image used in minutes (any image extension)
```

(Usually `C:\Users\<you>\AppData\Roaming\FireDeptAttendance`. To open it, paste `%APPDATA%\FireDeptAttendance` into Explorer's address bar.)

**Backups:** close the app, then copy that folder.

## Migrating your existing database (from the Python version)

The new app opens the existing `fire_dept.db` and `settings.json` **as-is**, with no conversion. Older databases get any missing columns added automatically, the same way the Python app did. There are three ways to bring your data over:

1. **Automatic (first run):** if `%APPDATA%\FireDeptAttendance\fire_dept.db` does not exist yet, the app looks for `fire_dept.db` in two places:
   - **next to the program's .exe**
   - **in that folder's parent folder**

   If found, it copies `fire_dept.db`, `settings.json`, and any `signature.*` file into the data folder.
2. **Import prompt:** if no database is found at all, the app asks whether to import one. Click **Yes** and browse to your old `fire_dept.db`. Its `settings.json` and `signature.*` are copied from the same folder.
3. **Manual:** close the app and copy `fire_dept.db`, `settings.json`, and `signature.*` into `%APPDATA%\FireDeptAttendance\`.

Existing files in the data folder are **never overwritten** by the automatic migration or by the import. Your original files are only copied, never moved or modified.

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
