using System.Diagnostics;
using Microsoft.Win32;

namespace SessionLimit;

/// <summary>
/// Launch-on-login, via the per-user Run key.
///
/// Deliberately HKCU rather than a Startup-folder shortcut or a scheduled task:
/// it needs no elevation, no COM interop to author a .lnk, and the user can see and
/// remove it from Task Manager's Startup tab like any other app.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SessionLimit";

    /// <summary>Path to the running executable, quoted for the registry.</summary>
    private static string ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
                path = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            return string.IsNullOrEmpty(path) ? "" : $"\"{path}\"";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Error("startup: read failed", ex);
            return false;
        }
    }

    /// <summary>Returns true if the stored command still points at this executable.</summary>
    public static bool IsCurrent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s &&
                   string.Equals(s.Trim(), ExePath.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null) return false;

            if (enabled)
            {
                var exe = ExePath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, exe, RegistryValueKind.String);
                Log.Info($"startup: enabled -> {exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("startup: disabled");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("startup: write failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Keeps the stored path in step after the app is moved or reinstalled — otherwise
    /// "start with Windows" silently points at an executable that no longer exists.
    /// </summary>
    public static void RefreshIfStale()
    {
        if (IsEnabled() && !IsCurrent()) Set(true);
    }
}
