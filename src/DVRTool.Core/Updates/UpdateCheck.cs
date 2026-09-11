namespace DVRTool.Core.Updates;

/// <summary>What an update check concluded — the one shape both front ends render.</summary>
public abstract record UpdateCheck
{
    /// <summary>The installed build is the newest published (or newer).</summary>
    public sealed record UpToDate(Version Installed, Version Latest) : UpdateCheck;

    /// <summary>A newer build exists and this install may take it.</summary>
    public sealed record Available(Version Installed, UpdateManifest Manifest, Uri MsiUrl,
        Uri ReleaseUrl) : UpdateCheck;

    /// <summary>A newer build exists but the operator said to skip this one.</summary>
    public sealed record Skipped(Version Installed, Version Latest) : UpdateCheck;

    /// <summary>Checking is not applicable here: policy off, or not an installed copy.</summary>
    public sealed record Disabled(string Reason) : UpdateCheck;

    /// <summary>The check could not be completed; <paramref name="Reason"/> is one line.
    /// <paramref name="SignatureRejected"/> marks the one failure that is never quiet.</summary>
    public sealed record Failed(string Reason, bool SignatureRejected = false) : UpdateCheck;

    private UpdateCheck() { }
}

/// <summary>The pure decision, given what was found.</summary>
public static class UpdateDecision
{
    public static UpdateCheck Decide(Version installed, UpdateManifest manifest, Uri msiUrl,
        Uri releaseUrl, Version? skipped)
    {
        var latest = manifest.Version;
        if (latest <= installed)
            return new UpdateCheck.UpToDate(installed, latest);
        if (!manifest.Accepts(installed))
            return new UpdateCheck.Failed(
                $"DVRTool {ProductVersion.Format(latest)} cannot upgrade an install older than " +
                $"{manifest.MinimumVersion}; install it by hand.");
        if (skipped is not null && skipped == latest)
            return new UpdateCheck.Skipped(installed, latest);
        return new UpdateCheck.Available(installed, manifest, msiUrl, releaseUrl);
    }
}
