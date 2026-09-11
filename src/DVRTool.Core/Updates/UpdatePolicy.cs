using Microsoft.Win32;

namespace DVRTool.Core.Updates;

/// <summary>
/// Where this DVRTool is installed and whether it is allowed to update itself — read from the
/// installer's <c>HKLM\SOFTWARE\DVRTool</c> key, never inferred from the exe's own folder.
/// </summary>
/// <remarks>
/// Two refusals live here. A copy running from anywhere other than the MSI's
/// <c>InstallDir</c> (a dev build under <c>bin\</c>, an unzipped folder) is not an installed
/// copy: running msiexec from it would upgrade some other install and leave the copy in use
/// untouched, so it never offers. And <c>Updates\Enabled = 0</c>, set at install time with
/// <c>msiexec … UPDATES=0</c>, is the per-machine switch for a locked-down client workstation
/// where the operator must not be prompted — the GUI shows nothing at all.
/// </remarks>
public sealed record UpdatePolicy(
    bool IsInstalled,
    bool Enabled,
    string? InstallDir,
    string? GuiExe,
    string? CliExe,
    string? InstalledVersionText)
{
    public const string RegistryPath = @"SOFTWARE\DVRTool";
    public const string UpdatesRegistryPath = @"SOFTWARE\DVRTool\Updates";

    /// <summary>The reason this policy forbids checking, or null when it allows it.</summary>
    public string? DisabledReason =>
        !Enabled ? "Updates are disabled by policy on this machine (UPDATES=0 at install)."
        : !IsInstalled ? "Not an installed copy of DVRTool (running outside the install folder); update by installing an MSI."
        : null;

    /// <summary>Reads the registry and compares against the running exe's location.</summary>
    public static UpdatePolicy Read(string? runningExePath = null)
    {
        runningExePath ??= Environment.ProcessPath;
        if (!OperatingSystem.IsWindows())
            return new UpdatePolicy(false, true, null, null, null, null);

        string? installDir = null, gui = null, cli = null, version = null;
        bool enabled = true;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(RegistryPath);
            installDir = key?.GetValue("InstallDir") as string;
            gui = key?.GetValue("GuiExe") as string;
            cli = key?.GetValue("CliExe") as string;
            version = key?.GetValue("Version") as string;
            using var updates = hklm.OpenSubKey(UpdatesRegistryPath);
            if (updates?.GetValue("Enabled") is int flag)
                enabled = flag != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
                                       or UnauthorizedAccessException)
        {
            // Unreadable registry reads as "not installed" rather than a guess.
        }
        return new UpdatePolicy(IsUnder(runningExePath, installDir), enabled, installDir, gui,
            cli, version);
    }

    /// <summary>True when <paramref name="exePath"/> lies inside <paramref name="installDir"/>.</summary>
    public static bool IsUnder(string? exePath, string? installDir)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(installDir))
            return false;
        try
        {
            string exe = Path.GetFullPath(exePath);
            string dir = Path.GetFullPath(installDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException)
        {
            return false;
        }
    }
}
