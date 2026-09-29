using System.Net;
using System.Net.Sockets;

namespace DVRTool.Core;

/// <summary>What a recorder's IP filter does with the addresses on its list.</summary>
public enum IpFilterMode
{
    /// <summary>The document named no mode, or one this code does not know.</summary>
    Unknown,

    /// <summary>A blocklist: the listed addresses are refused, everyone else is let in.</summary>
    Deny,

    /// <summary>
    /// An allowlist: the listed addresses are the <b>only</b> ones let in. DVRTool never writes
    /// a filter in this mode — see <see cref="IpFilterPlan"/>.
    /// </summary>
    Allow,
}

/// <summary>One address on a recorder's IP filter, in the device's own order.</summary>
/// <param name="Id">The device's slot number. Positional; DVRTool renumbers on write.</param>
/// <param name="Address">The address as the device holds it, normalized when it parses.</param>
/// <param name="Mode">The entry's own permission, which should always equal the list's.</param>
public sealed record IpFilterEntry(int Id, string Address, IpFilterMode Mode = IpFilterMode.Deny);

/// <summary>
/// Somebody currently logged into the recorder, from its online-user list — the one live
/// source of "blocking this address would cut off a session that is open right now".
/// </summary>
public sealed record IpFilterSession(string User, string Address, string LoginTime);

/// <summary>A recorder's whole IP filter as read.</summary>
public sealed record IpFilterState
{
    /// <summary>
    /// The firmware has an IP filter at all. False is a fact about the model or firmware — the
    /// recorder answered and said no — never a failed read.
    /// </summary>
    public required bool Supported { get; init; }

    public bool Enabled { get; init; }

    public IpFilterMode Mode { get; init; } = IpFilterMode.Unknown;

    public IReadOnlyList<IpFilterEntry> Entries { get; init; } = [];

    /// <summary>
    /// How many addresses the list can hold, from the device's own <c>size=</c> attribute or
    /// its capabilities. 0 means the device did not say, and a plan then refuses to guess.
    /// </summary>
    public int Capacity { get; init; }

    /// <summary>The capabilities document declares an IPv6 field.</summary>
    public bool AcceptsIpv6 { get; init; }

    /// <summary>
    /// The modes the capabilities document declares (<c>deny</c>, <c>allow</c>), verbatim.
    /// Empty when the capabilities could not be read.
    /// </summary>
    public IReadOnlyList<string> DeclaredModes { get; init; } = [];

    /// <summary>Who is logged into the recorder right now, and from where.</summary>
    public IReadOnlyList<IpFilterSession> Sessions { get; init; } = [];

    /// <summary>
    /// The recorder's own LAN addresses and its default gateway, which a filter must never
    /// name: a router that masquerades port-forwarded traffic makes every remote client
    /// arrive from the gateway.
    /// </summary>
    public IReadOnlyList<string> OwnAddresses { get; init; } = [];

    /// <summary>Parts that were asked for and could not be read, each with why.</summary>
    public IReadOnlyList<ConfigNote> Failures { get; init; } = [];

    public static IpFilterState NotSupported(IReadOnlyList<ConfigNote>? failures = null) =>
        new() { Supported = false, Failures = failures ?? [] };

    /// <summary>
    /// Some entry's own permission disagrees with the list's. Never seen on live firmware; a
    /// list in that state is refused rather than interpreted.
    /// </summary>
    public bool IsMixed => Entries.Any(e => e.Mode != IpFilterMode.Unknown && e.Mode != Mode);

    /// <summary>Whether an address is on the list, compared as an address, not as text.</summary>
    public bool Contains(string address) =>
        Entries.Any(e => IpFilterAddress.Same(e.Address, address));

    /// <summary>"on, deny, 14 of 32", "off (deny, empty)", "not supported".</summary>
    public string Summary
    {
        get
        {
            if (!Supported)
                return "not supported";
            string count = Capacity > 0 ? $"{Entries.Count} of {Capacity}" : $"{Entries.Count}";
            string mode = Mode switch
            {
                IpFilterMode.Deny => "blocklist",
                IpFilterMode.Allow => "ALLOWLIST",
                _ => "unknown mode",
            };
            return Enabled
                ? $"on, {mode}, {count}"
                : $"off ({mode}, {(Entries.Count == 0 ? "empty" : count)})";
        }
    }
}

/// <summary>
/// Parsing and classifying addresses — the checks that stand between a line of a log file and
/// an entry on a customer's recorder.
/// </summary>
public static class IpFilterAddress
{
    /// <summary>
    /// A single IPv4 address in dotted-quad form, or an IPv6 address, normalized — or null.
    /// Strict on purpose: <see cref="IPAddress.TryParse(string, out IPAddress)"/> accepts
    /// <c>"1"</c> as <c>0.0.0.1</c> and <c>"10.1"</c> as <c>10.0.0.1</c>, which is how a
    /// truncated log line becomes a block on somebody else. Ranges and CIDR are refused
    /// because the firmware has no field for them (<c>addressFilterType opt="mask"</c> holds
    /// one address).
    /// </summary>
    public static IPAddress? Parse(string text)
    {
        string s = text.Trim();
        if (s.Length == 0 || s.Contains('/') || s.Contains('-'))
            return null;
        if (!IPAddress.TryParse(s, out var ip))
            return null;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var parts = s.Split('.');
            // A leading zero is refused rather than read: some parsers take "010" as octal 8,
            // and the address the operator meant and the one written must be the same one.
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 3 ||
                    !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0')))
                return null;
            return ip;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (s.Contains('%'))
                return null; // a scope id names an interface on this machine, not the recorder's
            return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        }
        return null;
    }

    /// <summary>
    /// Every unicast address on this workstation's own interfaces — the addresses it reaches a
    /// LAN recorder from. Not its public address behind NAT, which nothing local can know
    /// without asking a third party; that one is protected by naming it (<c>--protect</c>).
    /// </summary>
    public static IReadOnlyList<string> ThisWorkstation()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => !IPAddress.IsLoopback(a))
                .Select(a => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString())
                .Select(s => s.Split('%')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>The same address, however each was written.</summary>
    public static bool Same(string a, string b)
    {
        var x = Parse(a);
        var y = Parse(b);
        return x is not null && y is not null
            ? x.Equals(y)
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The canonical spelling, or the text trimmed when it does not parse.</summary>
    public static string Normalize(string text) => Parse(text)?.ToString() ?? text.Trim();

    /// <summary>
    /// Why this address can never go on a filter, whatever the operator says: it is not one
    /// host out on the internet but a whole class of them, or nobody.
    /// </summary>
    public static string? NeverBlockable(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return "a loopback address";
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None))
            return "not a host address";
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 0)
                return "not a host address";
            if (b[0] >= 224)
                return b[0] < 240 ? "a multicast address" : "a reserved address";
        }
        else if (ip.IsIPv6Multicast)
        {
            return "a multicast address";
        }
        return null;
    }

    /// <summary>
    /// Whether the address is on somebody's LAN rather than out on the internet — private,
    /// carrier-grade NAT, link-local or IPv6 unique-local. Blocking one is sometimes right (a
    /// rogue device on the customer's network) and far more often the site's own iVMS
    /// workstation, so a plan asks for it explicitly.
    /// </summary>
    public static bool IsLocal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 ||
                (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
    }
}

/// <summary>One address the operator asked about and what the plan decided for it.</summary>
public sealed record IpFilterDecision(string Asked, string Address, IpFilterAction Action,
    string Reason = "");

public enum IpFilterAction
{
    /// <summary>Goes onto the list.</summary>
    Add,

    /// <summary>Already on the list; nothing to write.</summary>
    AlreadyPresent,

    /// <summary>Comes off the list.</summary>
    Remove,

    /// <summary>Asked to remove and not on the list; nothing to write.</summary>
    NotPresent,

    /// <summary>Refused, with a reason. A plan with any refusal writes nothing.</summary>
    Refused,
}

/// <summary>What the operator is asking one recorder's filter to become.</summary>
/// <param name="Add">Addresses to block.</param>
/// <param name="Remove">Addresses to unblock.</param>
/// <param name="Enable">Turn the filter on (true) or off (false); null leaves it.</param>
/// <param name="AllowLocal">Permit private / LAN addresses on the list.</param>
/// <param name="AllowLoggedIn">Permit an address that is logged into the recorder right now.</param>
/// <param name="Protected">
/// Addresses this workstation is known to reach the recorder from — its own interfaces, and
/// anything the operator names — which no override lets through.
/// </param>
public sealed record IpFilterRequest(
    IReadOnlyList<string>? Add = null,
    IReadOnlyList<string>? Remove = null,
    bool? Enable = null,
    bool AllowLocal = false,
    bool AllowLoggedIn = false,
    IReadOnlyList<string>? Protected = null)
{
    public IReadOnlyList<string> Add { get; init; } = Add ?? [];
    public IReadOnlyList<string> Remove { get; init; } = Remove ?? [];
    public IReadOnlyList<string> Protected { get; init; } = Protected ?? [];
}

/// <summary>
/// The one piece of arithmetic both front ends describe an IP-filter write from: which
/// addresses go on, which come off, which are refused and why, and what the list will be
/// afterwards. Pure, so the CLI dry run and the GUI confirmation cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule the whole feature rests on: DVRTool only ever edits a blocklist.</b> On a
/// Hikvision recorder the same list is either the addresses refused (<c>deny</c>) or the
/// <i>only</i> addresses let in (<c>allow</c>). In deny mode an add can lock out at most the
/// address it names, which is why each add is checked against this workstation, the
/// recorder itself and everybody logged in right now. In allow mode every remove, and turning
/// the filter on, can lock out everyone, this workstation included, and nothing here could
/// undo it from outside. So a filter in allow mode (or with no readable mode, or with mixed
/// entries) is refused whole, and there is deliberately no way to change the mode: that
/// stays a decision made on the recorder's own page by somebody standing where it cannot
/// lock them out.
/// </para>
/// <para>
/// <b>A plan with any refusal writes nothing.</b> A partial block of a list pulled from a log
/// is not the list the operator reviewed, and "12 of 14 went on" is the kind of success
/// nobody reads closely enough to find the two.
/// </para>
/// </remarks>
public sealed record IpFilterPlan
{
    public required IpFilterState Before { get; init; }

    public required IReadOnlyList<IpFilterDecision> Decisions { get; init; }

    /// <summary>The list as it will be written, in order — existing entries first.</summary>
    public required IReadOnlyList<string> After { get; init; }

    /// <summary>The filter's on/off switch after the write.</summary>
    public required bool EnabledAfter { get; init; }

    /// <summary>Why the whole plan is refused, when it is — before any address is looked at.</summary>
    public string? Blocked { get; init; }

    public IEnumerable<IpFilterDecision> Adds => Decisions.Where(d => d.Action == IpFilterAction.Add);
    public IEnumerable<IpFilterDecision> Removes => Decisions.Where(d => d.Action == IpFilterAction.Remove);
    public IEnumerable<IpFilterDecision> Refusals => Decisions.Where(d => d.Action == IpFilterAction.Refused);

    /// <summary>Nothing stops this plan from being written.</summary>
    public bool Allowed => Blocked is null && !Refusals.Any();

    /// <summary>The write would change something on the recorder.</summary>
    public bool HasWork => Allowed && (Adds.Any() || Removes.Any() || EnabledAfter != Before.Enabled);

    /// <summary>
    /// The filter will hold entries and still be off — a write that succeeds and blocks
    /// nobody, which is worth saying out loud because the web UI's own list looks the same
    /// either way.
    /// </summary>
    public bool InertAfter => !EnabledAfter && After.Count > 0;

    public static IpFilterPlan For(IpFilterState before, IpFilterRequest request)
    {
        string? blocked = WhyNotWritable(before);
        var decisions = new List<IpFilterDecision>();
        var after = before.Entries.Select(e => e.Address).ToList();

        if (blocked is null)
        {
            var protectedIps = request.Protected
                .Concat(before.OwnAddresses)
                .Select(IpFilterAddress.Parse)
                .OfType<IPAddress>()
                .ToList();

            foreach (string asked in request.Remove)
            {
                var ip = IpFilterAddress.Parse(asked);
                if (ip is null)
                {
                    decisions.Add(new(asked, asked.Trim(), IpFilterAction.Refused,
                        "not a single IP address (ranges and subnets are not supported by the recorder)"));
                    continue;
                }
                string address = ip.ToString();
                if (decisions.Any(d => d.Address == address))
                    continue;
                int at = after.FindIndex(a => IpFilterAddress.Same(a, address));
                if (at < 0)
                {
                    decisions.Add(new(asked, address, IpFilterAction.NotPresent));
                    continue;
                }
                after.RemoveAt(at);
                decisions.Add(new(asked, address, IpFilterAction.Remove));
            }

            foreach (string asked in request.Add)
            {
                var ip = IpFilterAddress.Parse(asked);
                if (ip is null)
                {
                    decisions.Add(new(asked, asked.Trim(), IpFilterAction.Refused,
                        "not a single IP address (ranges and subnets are not supported by the recorder)"));
                    continue;
                }
                string address = ip.ToString();
                if (decisions.Any(d => d.Address == address))
                {
                    if (decisions.Any(d => d.Address == address && d.Action == IpFilterAction.Remove))
                        decisions.Add(new(asked, address, IpFilterAction.Refused,
                            "asked to add and to remove in the same request"));
                    continue;
                }

                string? why = WhyNotAddable(ip, before, request, protectedIps);
                if (why is not null)
                {
                    decisions.Add(new(asked, address, IpFilterAction.Refused, why));
                    continue;
                }
                if (after.Any(a => IpFilterAddress.Same(a, address)))
                {
                    decisions.Add(new(asked, address, IpFilterAction.AlreadyPresent));
                    continue;
                }
                after.Add(address);
                decisions.Add(new(asked, address, IpFilterAction.Add));
            }

            // Capacity is judged on the whole result, and refuses the adds that overflow it by
            // name — never a quiet "the first N went on".
            if (before.Capacity > 0 && after.Count > before.Capacity)
            {
                int over = after.Count - before.Capacity;
                var overflow = decisions.Where(d => d.Action == IpFilterAction.Add).TakeLast(over).ToList();
                foreach (var d in overflow)
                {
                    decisions[decisions.IndexOf(d)] = d with
                    {
                        Action = IpFilterAction.Refused,
                        Reason = $"the list holds {before.Capacity} and this request would need " +
                            $"{after.Count} — remove some first",
                    };
                    after.Remove(d.Address);
                }
            }
            else if (before.Capacity <= 0 && decisions.Any(d => d.Action == IpFilterAction.Add))
            {
                blocked = "the recorder did not say how many addresses its list holds, so an add " +
                    "cannot be checked against it";
            }
        }

        bool enabledAfter = request.Enable ?? before.Enabled;
        return new IpFilterPlan
        {
            Before = before,
            Decisions = decisions,
            After = after,
            EnabledAfter = enabledAfter,
            Blocked = blocked,
        };
    }

    /// <summary>Why this recorder's filter cannot be edited at all, or null.</summary>
    public static string? WhyNotWritable(IpFilterState state)
    {
        if (!state.Supported)
            return "this recorder has no IP filter";
        if (state.Failures.Count > 0)
            return "part of the filter could not be read (" + state.Failures[0].Value + ")";
        return state.Mode switch
        {
            IpFilterMode.Allow =>
                "the filter is an ALLOWLIST (only listed addresses may connect). DVRTool edits " +
                "blocklists only — a change to an allowlist can lock out everyone, this " +
                "workstation included. Change it on the recorder's own page.",
            IpFilterMode.Unknown =>
                "the filter's mode could not be read, so whether an entry blocks or allows is unknown",
            _ when state.IsMixed =>
                "the list mixes blocked and allowed entries, which DVRTool will not interpret",
            _ => null,
        };
    }

    private static string? WhyNotAddable(IPAddress ip, IpFilterState state, IpFilterRequest request,
        List<IPAddress> protectedIps)
    {
        if (IpFilterAddress.NeverBlockable(ip) is string never)
            return never;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !state.AcceptsIpv6)
            return "this recorder's filter does not declare an IPv6 field";
        if (protectedIps.Any(p => p.Equals(ip)))
            return state.OwnAddresses.Any(o => IpFilterAddress.Same(o, ip.ToString()))
                ? "the recorder's own address or its gateway — behind a masquerading router " +
                  "every remote connection arrives from the gateway, so blocking it blocks everyone"
                : "this workstation's own address — it would lock DVRTool out";
        if (state.Sessions.FirstOrDefault(s => IpFilterAddress.Same(s.Address, ip.ToString())) is { } session
            && !request.AllowLoggedIn)
            return $"logged into the recorder right now as '{session.User}'" +
                (session.LoginTime.Length > 0 ? $" (since {session.LoginTime})" : "") +
                " — blocking it cuts off a live session; override deliberately if it is the intruder";
        if (IpFilterAddress.IsLocal(ip) && !request.AllowLocal)
            return "a private / LAN address — usually the site's own client, not an attacker; " +
                "allow LAN addresses explicitly to block it";
        return null;
    }
}

/// <summary>What a write left on the recorder — read back, never assumed.</summary>
/// <param name="Before">The filter as it was read for the write.</param>
/// <param name="After">The filter as the recorder reports it after the write.</param>
/// <param name="Rejected">
/// The recorder accepted the request and does not hold what was asked for. Named here rather
/// than thrown, so a fleet run can say which recorder did it.
/// </param>
public sealed record IpFilterChange(IpFilterState Before, IpFilterState After, bool Rejected,
    string Note = "")
{
    public IEnumerable<string> Added => After.Entries.Select(e => e.Address)
        .Where(a => !Before.Contains(a));

    public IEnumerable<string> Removed => Before.Entries.Select(e => e.Address)
        .Where(a => !After.Contains(a));
}

/// <summary>
/// Opt-in capability: reading a recorder's IP filter. A sibling of
/// <see cref="IDeviceConfigClient"/>, not a part of it — "who may connect to this recorder"
/// is a security question with its own sweep, and a vendor may answer one and not the other.
/// </summary>
public interface IIpFilterClient
{
    /// <summary>
    /// The filter, the recorder's own addresses and its online sessions in one call.
    /// A firmware without the feature returns <see cref="IpFilterState.Supported"/> false; a
    /// part that fails to read is a note in <see cref="IpFilterState.Failures"/>.
    /// </summary>
    Task<IpFilterState> GetIpFilterAsync(CancellationToken ct = default);
}

/// <summary>
/// Opt-in capability: editing a recorder's IP filter — the split
/// <see cref="IDeviceConfigWriter"/> makes, for the same reason: a front end can ask whether
/// to offer the button before anyone presses it.
/// </summary>
public interface IIpFilterWriter
{
    /// <summary>
    /// Writes a plan made from this client's own last read. Refuses a plan that is not
    /// <see cref="IpFilterPlan.Allowed"/>, refuses when the list moved on the device since the
    /// read, and returns what the recorder holds afterwards.
    /// </summary>
    Task<IpFilterChange> ApplyIpFilterAsync(IpFilterPlan plan, CancellationToken ct = default);
}

/// <summary>One recorder's row in the fleet IP-filter sweep.</summary>
public sealed record IpFilterAuditRow
{
    public required string DeviceName { get; init; }

    public IpFilterState? State { get; init; }

    /// <summary>Why the recorder could not be read at all.</summary>
    public string? Error { get; init; }

    /// <summary>The vendor has no implementation — not "no filter".</summary>
    public bool NotImplemented { get; init; }

    public bool Ok => Error is null && !NotImplemented && State is not null;

    public static IpFilterAuditRow Failed(string device, string error) =>
        new() { DeviceName = device, Error = error };

    public static IpFilterAuditRow Unimplemented(string device, string vendor) =>
        new() { DeviceName = device, NotImplemented = true, Error = $"not implemented for {vendor}" };

    /// <summary>The one-cell answer: "on, blocklist, 14 of 32", "not supported", "?".</summary>
    public string Summary => NotImplemented ? "n/a" : Ok ? State!.Summary : "?";

    public string Verdict
    {
        get
        {
            if (NotImplemented)
                return Error!;
            if (!Ok)
                return "could not be read — " + Error;
            var s = State!;
            if (!s.Supported)
                return "this firmware has no IP filter";
            if (IpFilterPlan.WhyNotWritable(s) is string why)
                return "read-only here: " + why;
            if (!s.Enabled && s.Entries.Count > 0)
                return "addresses listed but the filter is OFF — blocks nobody";
            if (!s.Enabled)
                return "available, not in use";
            return "blocking";
        }
    }
}

/// <summary>The fleet sweep, aggregated — the same shape as <see cref="ExceptionAudit"/>.</summary>
public sealed record IpFilterAudit
{
    public required IReadOnlyList<IpFilterAuditRow> Rows { get; init; }

    /// <summary>Every address blocked anywhere, with the recorders that block it — the matrix rows.</summary>
    public required IReadOnlyList<(string Address, IReadOnlyList<string> Devices)> Addresses { get; init; }

    public IEnumerable<IpFilterAuditRow> Unreachable => Rows.Where(r => !r.Ok && !r.NotImplemented);

    public bool IsPartial => Unreachable.Any();

    public static IpFilterAudit Build(IEnumerable<IpFilterAuditRow> rows)
    {
        var list = rows.ToList();
        var addresses = list
            .Where(r => r.Ok && r.State!.Supported)
            .SelectMany(r => r.State!.Entries.Select(e => (Address: IpFilterAddress.Normalize(e.Address), r.DeviceName)))
            .GroupBy(x => x.Address)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(x => x.DeviceName).Distinct().ToList()))
            .OrderByDescending(x => x.Item2.Count)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();
        return new IpFilterAudit { Rows = list, Addresses = addresses };
    }

    public string Summary
    {
        get
        {
            var read = Rows.Where(r => r.Ok).ToList();
            int supported = read.Count(r => r.State!.Supported);
            int on = read.Count(r => r.State!.Supported && r.State.Enabled);
            string text = $"{Rows.Count} recorder(s): {supported} have an IP filter, {on} using it, " +
                $"{Addresses.Count} distinct address(es) blocked";
            int notImpl = Rows.Count(r => r.NotImplemented);
            if (notImpl > 0)
                text += $", {notImpl} not implemented";
            if (IsPartial)
                text += $", {Unreachable.Count()} could not be read";
            return text + ".";
        }
    }
}
