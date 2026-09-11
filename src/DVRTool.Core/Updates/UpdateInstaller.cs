using System.Diagnostics;

namespace DVRTool.Core.Updates;

/// <summary>Runs a verified MSI as an in-place major upgrade of the installed DVRTool.</summary>
/// <remarks>
/// The MSI stays the only installer: this hands it to <c>msiexec</c> and gets out of the way.
/// The launch is deliberately <b>not elevated</b> by us — msiexec raises the UAC prompt itself
/// for a per-machine upgrade, so the operator sees the standard Windows prompt naming the MSI,
/// not one for cmd.exe. The command runs under <c>cmd.exe</c>, a system binary that survives
/// our own files being replaced, so it can relaunch the new GUI after msiexec returns
/// success; <c>/passive</c> shows only the progress bar. The caller must exit before the
/// upgrade reaches its files-in-use check, or the installer stalls on a restart prompt.
/// </remarks>
public static class UpdateInstaller
{
    /// <summary>The exact msiexec arguments, for display and for the CLI's dry run.</summary>
    public static string MsiexecArguments(string msiPath, bool passive = true) =>
        $"/i \"{msiPath}\" {(passive ? "/passive" : "/qn")} /norestart";

    /// <summary>The cmd.exe command line that installs and, optionally, relaunches the GUI.</summary>
    public static string BuildCommand(string msiPath, string? relaunchExe, bool passive = true)
    {
        string install = $"msiexec {MsiexecArguments(msiPath, passive)}";
        if (string.IsNullOrWhiteSpace(relaunchExe))
            return install;
        // `start "" "<exe>"` — the empty first quoted argument is start's window title slot.
        return $"{install} && start \"\" \"{relaunchExe}\"";
    }

    /// <summary>
    /// Starts the upgrade and returns immediately. <paramref name="relaunchExe"/> is the GUI
    /// path from the installer's registry key (<see cref="UpdatePolicy.GuiExe"/>), or null.
    /// </summary>
    public static void Launch(string msiPath, string? relaunchExe, bool passive = true)
    {
        if (!File.Exists(msiPath))
            throw new FileNotFoundException("Installer not found.", msiPath);
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/d /c " + BuildCommand(msiPath, relaunchExe, passive),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        using var _ = Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe did not start.");
    }
}
