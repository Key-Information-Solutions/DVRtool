namespace DVRTool.Core;

/// <summary>
/// The verification step that turns "the credentials worked" into "this is the right
/// system". Wraps <see cref="DeviceIdentityStore"/> around a device's own
/// <c>GetDeviceInfoAsync</c> so every front end asks the question the same way.
/// </summary>
public static class DeviceIdentityGuard
{
    /// <summary>
    /// Verifies an already-fetched <see cref="DeviceInfo"/>. Used by callers that read the
    /// device info anyway — the connectivity probe, the access-panel roster — so identity
    /// costs them no extra round trip.
    /// </summary>
    public static IdentityCheck Check(string address, DeviceInfo info,
        string? expectedSerial = null, string? expectedBy = null, DeviceIdentityStore? store = null) =>
        (store ?? DeviceIdentityStore.Default)
            .Verify(address, DeviceFingerprint.From(info), expectedSerial, expectedBy);

    /// <summary>Fetches the device info and verifies it. Never throws on a mismatch.</summary>
    public static async Task<IdentityCheck> CheckAsync(INvrClient client,
        string? expectedSerial = null, string? expectedBy = null, DeviceIdentityStore? store = null,
        CancellationToken ct = default)
    {
        var info = await client.GetDeviceInfoAsync(ct);
        return Check(AddressOf(client.Connection), info, expectedSerial, expectedBy, store);
    }

    /// <summary>
    /// Verifies and throws <see cref="DeviceIdentityException"/> on a mismatch — the form
    /// for anything that is about to act on the device: export footage, write a card, name a
    /// file after a site.
    /// </summary>
    public static async Task<IdentityCheck> EnsureAsync(INvrClient client,
        string? expectedSerial = null, string? expectedBy = null, DeviceIdentityStore? store = null,
        CancellationToken ct = default) =>
        Ensure(await CheckAsync(client, expectedSerial, expectedBy, store, ct));

    /// <summary>Throws when <paramref name="check"/> found the wrong hardware.</summary>
    public static IdentityCheck Ensure(IdentityCheck check) =>
        check.Verdict == IdentityVerdict.Mismatch ? throw new DeviceIdentityException(check) : check;

    /// <summary>
    /// The address an NVR's identity is pinned against: the port DVRTool authenticates on.
    /// RTSP and the SDK port are not it — they are separate forwards to the same box, and
    /// pinning three keys per device would just mean three ways to be half-pinned.
    /// </summary>
    public static string AddressOf(NvrConnection conn) =>
        DeviceAddress.Format(conn.Host, conn.HttpPort);

    /// <summary>The same, for a door panel — whose only port is the SDK one.</summary>
    public static string AddressOf(AccessPanelConnection conn) =>
        DeviceAddress.Format(conn.Host, conn.SdkPort);
}

/// <summary>How loudly a fleet-configuration finding deserves to be shown.</summary>
public enum FleetIssueSeverity
{
    /// <summary>Worth stating, nothing wrong. Several systems on one host is normal.</summary>
    Info,

    /// <summary>Probably a mistake, but a legal configuration.</summary>
    Warning,

    /// <summary>Two records that cannot both be right.</summary>
    Error,
}

public enum FleetIssueKind
{
    /// <summary>Two records point at the identical host and port.</summary>
    DuplicateAddress,

    /// <summary>Two addresses answered with the same serial: one machine, saved twice.</summary>
    SameDevice,

    /// <summary>Several records share a host and are told apart only by port.</summary>
    SharedHost,
}

/// <param name="Records">The record labels involved, in the order they were supplied.</param>
public sealed record FleetIssue(
    FleetIssueKind Kind,
    FleetIssueSeverity Severity,
    string Message,
    IReadOnlyList<string> Records);

/// <summary>One saved system, as the audit sees it.</summary>
/// <param name="Serial">
/// Its pinned or last-seen serial, when one is known. Null simply means "never connected" —
/// the audit reports what it can rather than demanding a full fleet sweep.
/// </param>
public sealed record FleetRecord(string Label, string Host, int Port, string? Serial = null)
{
    public string Address => DeviceAddress.Format(Host, Port);
}

/// <summary>
/// Cross-checks a saved fleet for the configuration mistakes that a per-device connection
/// test cannot see, because each record on its own is perfectly valid.
/// </summary>
/// <remarks>
/// The motivating case: several recorders behind one public IP, distinguished only by
/// forwarded port, all sharing one account. Every record connects; every record passes its
/// port test. What no single record can tell you is that two of them are pointed at the same
/// box, or that a port was typed twice — and with shared credentials, neither mistake
/// announces itself.
/// </remarks>
public static class FleetAudit
{
    public static IReadOnlyList<FleetIssue> Inspect(IEnumerable<FleetRecord> records)
    {
        var all = records.ToList();
        var issues = new List<FleetIssue>();

        foreach (var group in all
            .GroupBy(r => r.Address, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            var labels = group.Select(r => r.Label).ToList();
            issues.Add(new FleetIssue(FleetIssueKind.DuplicateAddress, FleetIssueSeverity.Error,
                $"{Join(labels)} all point at {group.Key} — one address cannot be several " +
                "systems. Fix the port on whichever record was copied.",
                labels));
        }

        // Same serial at different addresses. Legal (a recorder reachable over both HTTP and
        // HTTPS is one device on two ports) but far more often the port that was meant to
        // reach the second recorder still reaches the first.
        foreach (var group in all
            .Where(r => DeviceFingerprint.Normalize(r.Serial).Length > 0)
            .GroupBy(r => DeviceFingerprint.Normalize(r.Serial), StringComparer.Ordinal)
            .Where(g => g.Select(r => r.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var labels = group.Select(r => r.Label).ToList();
            issues.Add(new FleetIssue(FleetIssueKind.SameDevice, FleetIssueSeverity.Warning,
                $"{Join(labels)} are the same physical device (serial {group.First().Serial!.Trim()}) " +
                $"reached at {Join(group.Select(r => r.Address).Distinct().ToList())}. " +
                "If they were meant to be different systems, one of those ports forwards to " +
                "the wrong place.",
                labels));
        }

        foreach (var group in all
            .GroupBy(r => r.Host.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.Port).Distinct().Count() > 1))
        {
            var labels = group.Select(r => r.Label).ToList();
            issues.Add(new FleetIssue(FleetIssueKind.SharedHost, FleetIssueSeverity.Info,
                $"{Join(labels)} share the host {group.Key} and are told apart only by port. " +
                "Their serials are pinned, so a port that starts forwarding elsewhere is " +
                "reported rather than silently logged into.",
                labels));
        }

        return issues;
    }

    /// <summary>Issues about one record, for a dialog that only has that record in hand.</summary>
    public static IReadOnlyList<FleetIssue> InspectFor(
        FleetRecord subject, IEnumerable<FleetRecord> others) =>
        Inspect(others.Prepend(subject))
            .Where(i => i.Records.Contains(subject.Label, StringComparer.Ordinal))
            .ToList();

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => $"'{items[0]}'",
        2 => $"'{items[0]}' and '{items[1]}'",
        _ => string.Join(", ", items.Take(items.Count - 1).Select(i => $"'{i}'")) +
             $" and '{items[^1]}'",
    };
}
