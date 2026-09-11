using DVRTool.Core.Updates;

namespace DVRTool.Cli;

/// <summary>
/// <c>dvrtool update …</c> — the fleet path for keeping DVRTool current: check, download the
/// verified installer, or install it. Exit codes are scriptable.
/// </summary>
/// <remarks>
/// <c>install</c> follows the house gate for anything that changes a machine: it refuses
/// without <c>--yes</c>. Nothing here talks to a recorder, so it runs before Program builds a
/// client and never asks for device credentials.
/// </remarks>
internal static class UpdateCommands
{
    /// <summary>`check` when a newer release is available and not skipped.</summary>
    public const int ExitAvailable = 10;

    private const string Usage = """
        dvrtool update — check for, download, or install a newer DVRTool

        Usage:
          dvrtool update check
          dvrtool update download
          dvrtool update install [--yes] [--quiet]

        Subcommands:
          check       Ask the release channel for the newest DVRTool, verify its signed
                      manifest, and compare with this install. Exit 0 = up to date,
                      10 = newer available, 1 = could not check.
          download    check, then download the installer into
                      %LOCALAPPDATA%\DVRTool\updates and verify its size and SHA-256
                      against the signed manifest. Prints the verified path.
          install     download, then run the MSI as an in-place upgrade (msiexec
                      /passive — a progress bar, no questions; UAC prompts once).
                      Refused without --yes. --quiet uses /qn instead, for a remote
                      session that has no desktop to show the bar on.

        Options:
          --yes       actually install (install only)
          --quiet     msiexec /qn instead of /passive (install only)
          --json      `check`: print the result as one JSON object

        Every release is accepted only if its manifest is signed by the DVRTool release
        key baked into this build — where it was downloaded from is not what makes it
        trusted. A machine installed with `msiexec … UPDATES=0` reports "disabled by
        policy" and does nothing. See docs/updates.md.
        """;

    internal static bool TryRunHelp(string subcommand, Dictionary<string, string> opts, out int exitCode)
    {
        exitCode = 0;
        if (subcommand.Length > 0 && subcommand != "help" && !opts.ContainsKey("help"))
            return false;
        Console.WriteLine(Usage);
        exitCode = subcommand.Length == 0 && !opts.ContainsKey("help") ? 2 : 0;
        return true;
    }

    internal static async Task<int> RunAsync(string subcommand, Dictionary<string, string> opts,
        CancellationToken ct)
    {
        switch (subcommand)
        {
            case "check":
                return await CheckAsync(opts.ContainsKey("json"), ct) is UpdateCheck.Available
                    ? ExitAvailable
                    : LastExit;
            case "download":
            {
                var check = await CheckAsync(json: false, ct);
                if (check is not UpdateCheck.Available available)
                    return LastExit;
                string path = await DownloadAsync(available, ct);
                return path.Length > 0 ? 0 : 1;
            }
            case "install":
            {
                var check = await CheckAsync(json: false, ct);
                if (check is not UpdateCheck.Available available)
                    return LastExit;
                if (!opts.ContainsKey("yes"))
                {
                    Console.WriteLine();
                    Console.WriteLine("Refusing to install without --yes. Re-run with --yes to download the");
                    Console.WriteLine("installer, verify it, and run it as an in-place upgrade.");
                    return 2;
                }
                string path = await DownloadAsync(available, ct);
                if (path.Length == 0)
                    return 1;
                var policy = UpdatePolicy.Read();
                bool passive = !opts.ContainsKey("quiet");
                Console.WriteLine($"Launching: msiexec {UpdateInstaller.MsiexecArguments(path, passive)}");
                UpdateInstaller.Launch(path, relaunchExe: null, passive);
                Console.WriteLine("Installer started. This dvrtool.exe is about to be replaced; " +
                                  "the new version is in place when msiexec exits.");
                if (policy.InstalledVersionText is { Length: > 0 } was)
                    Console.WriteLine($"  (registry Version was {was})");
                return 0;
            }
            default:
                Console.Error.WriteLine($"error: unknown update subcommand '{subcommand}'\n");
                Console.WriteLine(Usage);
                return 2;
        }
    }

    // Exit code the most recent CheckAsync settled on for the non-Available outcomes.
    private static int LastExit;

    private static async Task<UpdateCheck> CheckAsync(bool json, CancellationToken ct)
    {
        var installed = ProductVersion.Current;
        var policy = UpdatePolicy.Read();
        var settings = UpdateSettings.Load();

        UpdateCheck result;
        if (policy.DisabledReason is { } reason)
        {
            result = new UpdateCheck.Disabled(reason);
        }
        else
        {
            using var client = new UpdateClient(installed);
            result = await client.CheckAsync(settings.SkippedVersion, ct);
            settings.LastCheck = DateTimeOffset.Now;
            if (result is UpdateCheck.Available a)
                settings.LastSeenVersionText = ProductVersion.Format(a.Manifest.Version);
            else if (result is UpdateCheck.UpToDate u)
                settings.LastSeenVersionText = ProductVersion.Format(u.Latest);
            settings.TrySave();
        }

        if (json)
            Console.WriteLine(ToJson(installed, result));
        else
            Print(installed, result);

        LastExit = result switch
        {
            UpdateCheck.UpToDate or UpdateCheck.Skipped => 0,
            UpdateCheck.Available => ExitAvailable,
            UpdateCheck.Disabled => 3,
            _ => 1,
        };
        return result;
    }

    private static void Print(Version installed, UpdateCheck result)
    {
        Console.WriteLine($"Installed: DVRTool {ProductVersion.Format(installed)}");
        switch (result)
        {
            case UpdateCheck.UpToDate u:
                Console.WriteLine($"Latest:    {ProductVersion.Format(u.Latest)} — up to date.");
                break;
            case UpdateCheck.Available a:
                Console.WriteLine($"Latest:    {ProductVersion.Format(a.Manifest.Version)} — NEWER");
                Console.WriteLine($"  installer: {a.Manifest.FileName} ({a.Manifest.Size / (1024.0 * 1024):F1} MB)");
                if (a.Manifest.PublishedAt is { } when)
                    Console.WriteLine($"  published: {when.ToLocalTime():yyyy-MM-dd HH:mm}");
                Console.WriteLine($"  notes:     {a.Manifest.NotesUrl ?? a.ReleaseUrl.ToString()}");
                Console.WriteLine("  manifest signature: OK (DVRTool release key " +
                                  $"{a.Manifest.KeyId ?? UpdateSigning.KeyId})");
                break;
            case UpdateCheck.Skipped s:
                Console.WriteLine($"Latest:    {ProductVersion.Format(s.Latest)} — newer, but skipped on this " +
                                  "machine (GUI: Skip this version). Delete %APPDATA%\\DVRTool\\updates.json to unskip.");
                break;
            case UpdateCheck.Disabled d:
                Console.WriteLine($"Updates:   disabled — {d.Reason}");
                break;
            case UpdateCheck.Failed f:
                Console.Error.WriteLine((f.SignatureRejected ? "SIGNATURE REJECTED: " : "Could not check: ") + f.Reason);
                break;
        }
    }

    private static string ToJson(Version installed, UpdateCheck result)
    {
        var o = new Dictionary<string, object?>
        {
            ["installed"] = ProductVersion.Format(installed),
            ["status"] = result switch
            {
                UpdateCheck.UpToDate => "up-to-date",
                UpdateCheck.Available => "available",
                UpdateCheck.Skipped => "skipped",
                UpdateCheck.Disabled => "disabled",
                _ => "failed",
            },
        };
        switch (result)
        {
            case UpdateCheck.UpToDate u: o["latest"] = ProductVersion.Format(u.Latest); break;
            case UpdateCheck.Skipped s: o["latest"] = ProductVersion.Format(s.Latest); break;
            case UpdateCheck.Available a:
                o["latest"] = ProductVersion.Format(a.Manifest.Version);
                o["file"] = a.Manifest.FileName;
                o["size"] = a.Manifest.Size;
                o["sha256"] = a.Manifest.Sha256;
                o["notesUrl"] = a.Manifest.NotesUrl ?? a.ReleaseUrl.ToString();
                break;
            case UpdateCheck.Disabled d: o["reason"] = d.Reason; break;
            case UpdateCheck.Failed f: o["reason"] = f.Reason; o["signatureRejected"] = f.SignatureRejected; break;
        }
        return System.Text.Json.JsonSerializer.Serialize(o);
    }

    /// <summary>Downloads with a console progress line; "" on failure (already reported).</summary>
    private static async Task<string> DownloadAsync(UpdateCheck.Available available, CancellationToken ct)
    {
        using var client = new UpdateClient(ProductVersion.Current);
        long total = available.Manifest.Size;
        int lastPercent = -1;
        var progress = new Progress<long>(done =>
        {
            int percent = (int)(done * 100 / Math.Max(total, 1));
            if (percent != lastPercent)
            {
                lastPercent = percent;
                Console.Write($"\rDownloading {available.Manifest.FileName}: {percent,3}%");
            }
        });
        try
        {
            string path = await client.DownloadAsync(available, progress, ct);
            Console.WriteLine();
            Console.WriteLine($"Verified (size + SHA-256): {path}");
            return path;
        }
        catch (UpdateVerificationException ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"REJECTED: {ex.Message}");
            return "";
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"Download failed: {ex.Message}");
            return "";
        }
    }
}
