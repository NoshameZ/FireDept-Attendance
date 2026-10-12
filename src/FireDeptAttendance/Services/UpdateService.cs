using System.Reflection;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace FireDeptAttendance.Services;

/// <summary>
/// Auto-update via Velopack + GitHub Releases (public repo, no token).
/// Updates only replace the program files in the install folder; the database and
/// settings live in %APPDATA%\FireDeptAttendance and are never touched.
/// </summary>
public static class UpdateService
{
    public static string RepoUrl =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepoUrl")?.Value
        ?? "https://github.com/NoshameZ/FireDept-Attendance";

    /// <summary>Version shown in the UI (Velopack's installed version, else the assembly version).</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var mgr = CreateManager();
                if (mgr.IsInstalled && mgr.CurrentVersion != null) return mgr.CurrentVersion.ToString();
            }
            catch { /* not installed / no metadata */ }
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info)) return info.Split('+')[0];
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    private static UpdateManager CreateManager() => new(new GithubSource(RepoUrl, null, false));

    private static bool _busy;

    /// <summary>
    /// Check GitHub for a newer release. <paramref name="manual"/> = user clicked
    /// "Check for Updates" (show results/errors); otherwise fail silently (e.g. offline).
    /// </summary>
    public static async Task CheckAsync(Window owner, bool manual)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
            {
                if (manual)
                    MessageBox.Show(owner,
                        "Automatic updates are only available when the app is installed with the Setup program " +
                        "from the GitHub Releases page.\n\nCurrent version: v" + CurrentVersion,
                        "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            UpdateInfo? info;
            try { info = await mgr.CheckForUpdatesAsync(); }
            catch (Exception ex)
            {
                if (manual)
                    MessageBox.Show(owner, "Could not check for updates (are you online?).\n\n" + ex.Message,
                        "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (info == null)
            {
                if (manual)
                    MessageBox.Show(owner, $"You're up to date (v{CurrentVersion}).", "Check for Updates",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newVer = info.TargetFullRelease.Version.ToString();
            var ok = MessageBox.Show(owner, $"Update available (v{newVer}) — install now?\n\n" +
                                            "The app will restart. Your data and settings are kept.",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;

            try
            {
                owner.Cursor = System.Windows.Input.Cursors.Wait;
                await mgr.DownloadUpdatesAsync(info);
                mgr.ApplyUpdatesAndRestart(info);
            }
            catch (Exception ex)
            {
                owner.Cursor = null;
                MessageBox.Show(owner, "The update could not be installed:\n" + ex.Message, "Update failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            if (manual)
                MessageBox.Show(owner, ex.Message, "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
        }
    }
}
