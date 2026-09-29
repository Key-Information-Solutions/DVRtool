using System.Globalization;
using System.Xml.Linq;
using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="IIpFilterClient"/> / <see cref="IIpFilterWriter"/> face of the Hikvision
/// client: the recorder's own IP filter (web UI: Configuration → Network → Advanced → IP
/// Address Filter, on the firmwares that show it at all).
/// </summary>
/// <remarks>
/// <para>
/// Read off live firmware on 2026-09-29 — an M-series NVR (DS-9632NI-M8, V5.04.081, filter on
/// with 14 blocked addresses) and the lab recorder (filter off, empty):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>/ISAPI/System/Network/capabilities</c> carries <c>isSupportIPFilter</c> — the honest
/// answer to "does this firmware have it", including on models whose web UI never shows the
/// page.
/// </description></item>
/// <item><description>
/// <c>/ISAPI/System/Network/ipFilter</c> is one document: <c>enabled</c>, the list's
/// <c>permissionType</c> (<c>deny</c> = blocklist, <c>allow</c> = allowlist), and an
/// <c>IPFilterAddressList size="32"</c> of <c>IPFilterAddress</c> entries, each repeating the
/// permission and holding one address under <c>AddressMask/ipAddress</c> (or
/// <c>ipv6Address</c>). <c>addressFilterType opt="mask"</c> is the only type — despite the
/// name there is no mask field, so one entry is one host and there are no ranges.
/// </description></item>
/// <item><description>
/// <c>/ipFilter/capabilities</c> declares the legal values the same way the streaming
/// capabilities do: <c>id min=1 max=32</c>, <c>permissionType opt="deny,allow"</c>,
/// <c>ipAddress min=7 max=15</c>.
/// </description></item>
/// <item><description>
/// <c>/ISAPI/Security/onlineUser</c> lists sessions logged in <i>now</i> (web UI, iVMS, SDK)
/// with their client address. A digest-authenticated ISAPI request opens no session, so
/// DVRTool's own reads never appear there — which is why this workstation's own addresses
/// are protected separately, by the caller.
/// </description></item>
/// </list>
/// <para>
/// The write is the whole document, read-modify-write through the Config tab's guard: the
/// device's own document with the address list replaced, refused when it moved since the
/// read, then read back and compared. Entries are renumbered 1…n, which is what the id field
/// is — a slot, not an identity.
/// </para>
/// </remarks>
public sealed partial class HikvisionClient : IIpFilterClient, IIpFilterWriter
{
    private const string NetworkCapsPath = "/ISAPI/System/Network/capabilities";
    private const string IpFilterPath = "/ISAPI/System/Network/ipFilter";
    private const string IpFilterCapsPath = "/ISAPI/System/Network/ipFilter/capabilities";
    private const string OnlineUserPath = "/ISAPI/Security/onlineUser";

    private IpFilterState? _lastIpFilter;

    public async Task<IpFilterState> GetIpFilterAsync(CancellationToken ct = default)
    {
        var failures = new List<ConfigNote>();

        // The roster first: a firmware that says no has no filter page to read, and one
        // that says nothing (no flag, or no capabilities node) is asked directly.
        var netCaps = await TryGetXmlAsync(NetworkCapsPath, ct, null);
        string? flag = netCaps?.Root is { } capsRoot ? Descendant(capsRoot, "isSupportIPFilter") : null;
        if (flag is not null && !flag.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _lastIpFilter = null;
            return IpFilterState.NotSupported();
        }

        var doc = await TryGetXmlAndRememberAsync(IpFilterPath, ct, failures, "the IP filter");
        if (doc is null)
        {
            _lastIpFilter = null;
            // No flag and no document is a firmware without the feature; a flag that said yes
            // and a document that failed is a failed read, never "not supported".
            if (flag is null)
                return IpFilterState.NotSupported();
            throw new NvrException("the recorder declares an IP filter and would not return it: " +
                (failures.Count > 0 ? failures[0].Value : "no document"));
        }

        var caps = await TryGetXmlAsync(IpFilterCapsPath, ct, null);
        var state = ParseIpFilter(doc, caps);

        var sessions = await ReadOnlineSessionsAsync(failures, ct);
        var own = await ReadOwnAddressesAsync(failures, ct);

        state = state with { Sessions = sessions, OwnAddresses = own, Failures = failures };
        _lastIpFilter = state;
        return state;
    }

    public async Task<IpFilterChange> ApplyIpFilterAsync(IpFilterPlan plan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_lastIpFilter is null || !ReferenceEquals(_lastIpFilter, plan.Before))
            throw new NvrException(
                "refusing to write the IP filter: the plan was not made from this client's own " +
                "latest read. Read the filter, plan, then write.");
        if (!plan.Allowed)
            throw new NvrException("refusing to write the IP filter: " +
                (plan.Blocked ?? plan.Refusals.First().Reason));
        if (!plan.HasWork)
            return new IpFilterChange(plan.Before, plan.Before, Rejected: false,
                Note: "nothing to change");

        var doc = await ReReadForWriteAsync(IpFilterPath, "the IP filter", ct);
        BuildIpFilterDocument(doc, plan.After, plan.EnabledAfter, plan.Before.Mode);
        await PutXmlAsync(IpFilterPath, doc, ct);

        var after = await GetIpFilterAsync(ct);
        bool enabledOk = after.Enabled == plan.EnabledAfter;
        var missing = plan.After.Where(a => !after.Contains(a)).ToList();
        var extra = after.Entries.Select(e => e.Address)
            .Where(a => !plan.After.Any(p => IpFilterAddress.Same(p, a))).ToList();
        bool rejected = !enabledOk || missing.Count > 0 || extra.Count > 0 ||
            after.Mode != plan.Before.Mode;

        var notes = new List<string>();
        if (!enabledOk)
            notes.Add($"the filter is {(after.Enabled ? "on" : "off")}, not " +
                $"{(plan.EnabledAfter ? "on" : "off")}");
        if (missing.Count > 0)
            notes.Add("not held: " + string.Join(", ", missing));
        if (extra.Count > 0)
            notes.Add("still listed: " + string.Join(", ", extra));
        if (after.Mode != plan.Before.Mode)
            notes.Add($"the mode changed to {after.Mode}");

        return new IpFilterChange(plan.Before, after, rejected, string.Join("; ", notes));
    }

    // ----- parsing -----

    internal static IpFilterState ParseIpFilter(XDocument doc, XDocument? caps)
    {
        var root = doc.Root ?? throw new NvrException($"GET {IpFilterPath} returned no document");
        var mode = ParseMode(Child(root, "permissionType"));
        var list = root.Elements().FirstOrDefault(e => e.Name.LocalName == "IPFilterAddressList");

        var entries = new List<IpFilterEntry>();
        foreach (var el in list?.Elements().Where(e => e.Name.LocalName == "IPFilterAddress") ?? [])
        {
            var mask = el.Elements().FirstOrDefault(e => e.Name.LocalName == "AddressMask") ?? el;
            string address = (Child(mask, "ipAddress") is { Length: > 0 } v4 ? v4
                : Child(mask, "ipv6Address") ?? "").Trim();
            if (address.Length == 0)
                continue;
            int id = int.TryParse(Child(el, "id"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int n) ? n : entries.Count + 1;
            // An entry that names no permission of its own takes the list's.
            var entryMode = Child(el, "permissionType") is { Length: > 0 } p ? ParseMode(p) : mode;
            entries.Add(new IpFilterEntry(id, IpFilterAddress.Normalize(address), entryMode));
        }

        var capsRoot = caps?.Root;
        var capsList = capsRoot?.Elements().FirstOrDefault(e => e.Name.LocalName == "IPFilterAddressList");
        int capacity = ParseSize(list) ?? ParseSize(capsList) ??
            (capsList?.Descendants().FirstOrDefault(e => e.Name.LocalName == "id")?
                .Attribute("max")?.Value is string max &&
             int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : 0);

        var declaredModes = (capsRoot?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "permissionType")?
                .Attribute("opt")?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new IpFilterState
        {
            Supported = true,
            Enabled = (Child(root, "enabled") ?? "").Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            Mode = mode,
            Entries = entries,
            Capacity = capacity,
            AcceptsIpv6 = capsList?.Descendants().Any(e => e.Name.LocalName == "ipv6Address") == true,
            DeclaredModes = declaredModes,
        };
    }

    private static IpFilterMode ParseMode(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "deny" => IpFilterMode.Deny,
        "allow" => IpFilterMode.Allow,
        _ => IpFilterMode.Unknown,
    };

    private static int? ParseSize(XElement? list) =>
        list?.Attribute("size")?.Value is string s &&
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : null;

    internal static List<IpFilterSession> ParseOnlineUsers(XDocument doc) =>
        ElementsNamed(doc.Root!, "OnlineUser")
            .Select(u => new IpFilterSession(
                (Child(u, "name") ?? "").Trim(),
                (Descendant(u, "ipAddress") ?? Descendant(u, "ipv6Address") ?? "").Trim(),
                (Child(u, "loginTime") ?? "").Trim()))
            .Where(s => s.Address.Length > 0)
            .ToList();

    /// <summary>
    /// The recorder's own addresses and gateways off the interface list. Every one is a
    /// protected address: the recorder's own IP is nonsense on its own filter, and behind a
    /// router that masquerades forwarded ports, every remote client arrives from the gateway.
    /// </summary>
    internal static List<string> ParseOwnAddresses(XDocument doc)
    {
        var own = new List<string>();
        foreach (var ip in ElementsNamed(doc.Root!, "IPAddress"))
        {
            foreach (string name in new[] { "ipAddress", "ipv6Address" })
                if (Child(ip, name) is { Length: > 0 } a)
                    own.Add(a.Trim());
            foreach (var gateway in ip.Elements().Where(e => e.Name.LocalName is "DefaultGateway" or "ipv6DefaultGateway"))
                foreach (var a in gateway.Elements().Where(e => e.Name.LocalName is "ipAddress" or "ipv6Address"))
                    if (a.Value.Trim().Length > 0)
                        own.Add(a.Value.Trim());
        }
        return own
            .Where(a => IpFilterAddress.Parse(a) is { } p && IpFilterAddress.NeverBlockable(p) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<IpFilterSession>> ReadOnlineSessionsAsync(List<ConfigNote> failures,
        CancellationToken ct)
    {
        var probe = new List<Exception>();
        var doc = await TryGetXmlAsync(OnlineUserPath, ct, probe);
        if (doc is null)
        {
            // Unsupported is no sessions to know about; a failure is a gap in the safety
            // check and is reported as one.
            if (probe.Count > 0)
                failures.Add(new ConfigNote("ipfilter", "the online-user list", probe[0].Message));
            return [];
        }
        return ParseOnlineUsers(doc);
    }

    private async Task<List<string>> ReadOwnAddressesAsync(List<ConfigNote> failures,
        CancellationToken ct)
    {
        var probe = new List<Exception>();
        var doc = await TryGetXmlAsync(InterfacesPath, ct, probe);
        if (doc is null)
        {
            failures.Add(new ConfigNote("ipfilter", "the recorder's own addresses",
                probe.Count > 0 ? probe[0].Message : $"{InterfacesPath} is not supported"));
            return [];
        }
        return ParseOwnAddresses(doc);
    }

    // ----- writing -----

    /// <summary>
    /// The device's own document with the switch set and the address list replaced. Every
    /// entry is written in the one shape the firmware produced itself; the list keeps its
    /// <c>size</c> attribute and anything else the firmware put beside the entries.
    /// </summary>
    internal static void BuildIpFilterDocument(XDocument doc, IReadOnlyList<string> addresses,
        bool enabled, IpFilterMode mode)
    {
        var root = doc.Root ?? throw new NvrException($"GET {IpFilterPath} returned no document");
        XNamespace ns = root.Name.Namespace;
        string modeText = mode == IpFilterMode.Allow ? "allow" : "deny";

        SetChild(root, "enabled", enabled ? "true" : "false");
        SetChild(root, "permissionType", modeText);

        var list = root.Elements().FirstOrDefault(e => e.Name.LocalName == "IPFilterAddressList");
        if (list is null)
        {
            list = new XElement(ns + "IPFilterAddressList");
            root.Add(list);
        }
        list.Elements().Where(e => e.Name.LocalName == "IPFilterAddress").Remove();

        int id = 0;
        foreach (string address in addresses)
        {
            var ip = IpFilterAddress.Parse(address)
                ?? throw new NvrException($"refusing to write '{address}': not a single IP address");
            bool v6 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            list.Add(new XElement(ns + "IPFilterAddress",
                new XElement(ns + "id", (++id).ToString(CultureInfo.InvariantCulture)),
                new XElement(ns + "permissionType", modeText),
                new XElement(ns + "addressFilterType", "mask"),
                new XElement(ns + "AddressMask",
                    new XElement(ns + (v6 ? "ipv6Address" : "ipAddress"), ip.ToString()))));
        }
    }
}
