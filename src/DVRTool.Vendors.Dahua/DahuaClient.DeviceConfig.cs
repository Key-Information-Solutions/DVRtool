using System.Globalization;
using DVRTool.Core;

namespace DVRTool.Vendors.Dahua;

/// <summary>
/// The <see cref="IDeviceConfigWriter"/> face of the Dahua client: the recorder's own clock,
/// NTP, LAN addressing and name.
/// </summary>
/// <remarks>
/// <para>
/// Observed on a live DH-NVR608H-128-4KS3/I (<c>docs/device-config-discovery.md</c>). Three
/// findings shape the code, and all three are about what Dahua does <em>not</em> expose:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Only RTSP's port is readable.</b> <c>HTTP</c>, <c>HTTPS</c>, <c>HTTPD</c>,
/// <c>Telnet</c>, <c>ClientPort</c> and <c>NetPort</c> all answer
/// <c>403 Authority:check failure.</c> — the same body a genuine permission denial returns, so
/// <b>a wrong config name and a real denial are indistinguishable</b>. This client therefore
/// asks only for names that were observed answering and <b>never guesses a name at
/// runtime</b>: a speculative probe would look like a permissions problem in a support call,
/// and the box locks out after five failed requests.
/// </description></item>
/// <item><description>
/// <b>Dahua declares no ranges.</b> <c>getConfigCaps&amp;name=Network</c> returns the plain
/// config — it ignores the caps verb for this name, a wider failure than the
/// channel-parameter one in <c>docs/dahua-storage.md</c> — so every bound is
/// <see cref="ServicePortRange.Fallback"/>, which the front ends label as ours.
/// </description></item>
/// <item><description>
/// <b>The zone lives in <c>NTP</c>, not <c>Locales</c></b> (<c>TimeZone=25</c> /
/// <c>TimeZoneDesc=Easterntime</c>), while <c>Locales</c> holds only the DST switch and
/// absolute DST dates. That pairing — NTP syncing happily, <c>DSTEnable=false</c>, a zone that
/// observes DST — is what had a 128-channel recorder stamping every recording an hour early.
/// </description></item>
/// </list>
/// </remarks>
public sealed partial class DahuaClient : IDeviceConfigWriter
{
    private const string CurrentTimePath = "/cgi-bin/global.cgi?action=getCurrentTime";
    private const string NtpConfigName = "NTP";
    private const string LocalesConfigName = "Locales";
    private const string NetworkConfigName = "Network";
    private const string RtspConfigName = "RTSP";
    private const string GeneralConfigName = "General";

    /// <summary>
    /// Config documents as the last read saw them, so a write can tell that the device's copy
    /// moved underneath it. Keyed by config name.
    /// </summary>
    private readonly Dictionary<string, string> _configDocs = new(StringComparer.Ordinal);

    private DeviceConfiguration? _lastConfig;

    private static string ConfigPath(string name) =>
        $"/cgi-bin/configManager.cgi?action=getConfig&name={name}";

    // ----- reads -----

    /// <summary>
    /// The clock alone. Dahua's <c>getCurrentTime</c> answers a bare
    /// <c>result=YYYY-MM-DD HH:MM:SS</c> with <b>no offset at all</b>, which is why
    /// <see cref="DeviceClock.DeclaredOffset"/> is null here rather than zero.
    /// </summary>
    public async Task<DeviceClock> GetClockAsync(CancellationToken ct = default)
    {
        var kv = ParseKeyValues(await GetTextAsync(CurrentTimePath, ct));
        return new DeviceClock(ReadCurrentTime(kv), DeclaredOffset: null, VendorZoneLabel: null,
            DstEnabled: null);
    }

    internal static DateTime ReadCurrentTime(Dictionary<string, string> kv)
    {
        string value = kv.GetValueOrDefault("result",
            kv.GetValueOrDefault("time", kv.GetValueOrDefault("currentTime", "")));
        if (!TryParseCgiTime(value, out var wall))
            throw new NvrException($"unrecognized Dahua time value: '{value}'");
        return wall;
    }

    public async Task<TimeSourceStatus> GetTimeSourceAsync(CancellationToken ct = default)
    {
        var kv = await TryGetConfigAsync(NtpConfigName, ct);
        if (kv is null)
            return TimeSourceStatus.Unknown;
        var (servers, interval, enabled, _, _) = ParseNtp(kv);
        var detail = new DeviceConfiguration
        {
            Clock = new DeviceClock(default, null, null, null),
            NtpServers = servers,
            NtpEnabled = enabled,
            NtpInterval = interval,
            Scope = ConfigScope.Appliance,
        }.NtpSummary;
        return new TimeSourceStatus(enabled, detail);
    }

    public async Task<DeviceConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        var failures = new List<ConfigNote>();
        var notes = new List<ConfigNote>();

        // The clock first, and the only read allowed to throw: it is the part that matters.
        var timeKv = ParseKeyValues(await GetTextAsync(CurrentTimePath, ct));
        DateTime wall = ReadCurrentTime(timeKv);

        IReadOnlyList<NtpServer>? servers = null;
        TimeSpan? interval = null;
        bool? ntpEnabled = null;
        string? zoneLabel = null;
        var ntp = await TryGetConfigAsync(NtpConfigName, ct, failures, "NTP and time zone");
        if (ntp is not null)
        {
            (servers, interval, ntpEnabled, string? zoneIndex, string? zoneDesc) = ParseNtp(ntp);
            zoneLabel = (zoneIndex, zoneDesc) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{zoneIndex} ({zoneDesc})",
                ({ Length: > 0 }, _) => zoneIndex,
                (_, { Length: > 0 }) => zoneDesc,
                _ => null,
            };
        }

        bool? dst = null;
        var locales = await TryGetConfigAsync(LocalesConfigName, ct, failures, "DST");
        if (locales is not null)
        {
            if (TryReadBool(locales, "table.Locales.DSTEnable") is bool enabled)
                dst = enabled;
            // Absolute dates, not a recurring rule: a recorder left alone past New Year
            // carries stale DST dates, which is worth showing rather than interpreting.
            string start = DstDate(locales, "DSTStart");
            string end = DstDate(locales, "DSTEnd");
            if (start.Length > 0 || end.Length > 0)
                notes.Add(new ConfigNote("DST", "window", $"{start} → {end}"));
        }

        var ports = new List<ServicePort>();
        var rtsp = await TryGetConfigAsync(RtspConfigName, ct, failures, "the RTSP port");
        if (rtsp is not null)
            ports.Add(ParseRtspPort(rtsp));

        IReadOnlyList<NetworkInterfaceConfig>? interfaces = null;
        var network = await TryGetConfigAsync(NetworkConfigName, ct, failures, "the LAN address");
        if (network is not null)
        {
            var links = await ReadLinkStateAsync(ct);
            interfaces = ParseInterfaces(network, links);
            if (network.GetValueOrDefault("table.Network.Hostname") is { Length: > 0 } hostname)
                notes.Add(new ConfigNote("Network", "hostname", hostname));
        }

        string? name = null;
        var general = await TryGetConfigAsync(GeneralConfigName, ct, failures, "the device name");
        if (general is not null)
        {
            name = general.GetValueOrDefault("table.General.MachineName");

            // The lockout policy in plain sight — the 1800 s that the probe rules already
            // respect from folklore. Showing it makes the rule self-evident.
            if (TryReadBool(general, "table.General.LockLoginEnable") is bool locks)
                notes.Add(new ConfigNote("Login lockout", "enabled", locks ? "yes" : "no"));
            if (general.GetValueOrDefault("table.General.LockLoginTimes") is { Length: > 0 } times)
                notes.Add(new ConfigNote("Login lockout", "after", $"{times} failed logins"));
            if (general.GetValueOrDefault("table.General.LoginFailLockTime") is { Length: > 0 } secs)
                notes.Add(new ConfigNote("Login lockout", "for", $"{secs} s"));
        }

        await AddNotesAsync(notes, ct);

        var config = new DeviceConfiguration
        {
            Clock = new DeviceClock(wall, DeclaredOffset: null, zoneLabel, dst),
            DeviceName = name,
            NtpServers = servers,
            NtpEnabled = ntpEnabled,
            NtpInterval = interval,
            Interfaces = interfaces,
            Ports = ports,
            // Ports is in scope and the list holds one entry: the web port is not reachable by
            // name at all, which is a fact about this vendor rather than a failed read.
            Scope = ConfigScope.Appliance,
            Notes = notes,
            Failures = failures,
        };
        _lastConfig = config;
        return config;
    }

    // ----- parsing -----

    internal static (IReadOnlyList<NtpServer> Servers, TimeSpan? Interval, bool? Enabled,
        string? ZoneIndex, string? ZoneDesc) ParseNtp(Dictionary<string, string> kv)
    {
        var servers = new List<NtpServer>();
        string primary = kv.GetValueOrDefault("table.NTP.Address", "");
        int primaryPort = TryParseInt(kv.GetValueOrDefault("table.NTP.Port")) ?? 123;
        if (primary.Length > 0)
            servers.Add(new NtpServer(0, primary, primaryPort));

        foreach (int i in IndexesInOrder(kv.Keys, "table.NTP.ServerList["))
        {
            string address = kv.GetValueOrDefault($"table.NTP.ServerList[{i}].Address", "");
            if (address.Length == 0)
                continue;
            int port = TryParseInt(kv.GetValueOrDefault($"table.NTP.ServerList[{i}].Port")) ?? 123;
            bool enabled = TryReadBool(kv, $"table.NTP.ServerList[{i}].Enable") ?? true;
            // The primary is often repeated as the first list entry; one row per address.
            if (servers.Any(s => s.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
                continue;
            servers.Add(new NtpServer(i + 1, address, port, enabled));
        }

        TimeSpan? interval = TryParseInt(kv.GetValueOrDefault("table.NTP.UpdatePeriod")) is int min
                && min > 0
            ? TimeSpan.FromMinutes(min)
            : null;

        return (servers, interval, TryReadBool(kv, "table.NTP.Enable"),
            kv.GetValueOrDefault("table.NTP.TimeZone"),
            kv.GetValueOrDefault("table.NTP.TimeZoneDesc"));
    }

    /// <summary>RTSP is the one port this vendor answers for; its RTP range rides as extras.</summary>
    internal static ServicePort ParseRtspPort(Dictionary<string, string> kv)
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        if (kv.GetValueOrDefault("table.RTSP.RTP.StartPort") is { Length: > 0 } start)
            extras["RTP.StartPort"] = start;
        if (kv.GetValueOrDefault("table.RTSP.RTP.EndPort") is { Length: > 0 } end)
            extras["RTP.EndPort"] = end;

        return new ServicePort(
            Id: 1,
            Protocol: "RTSP",
            Kind: ServiceKind.Rtsp,
            Port: TryParseInt(kv.GetValueOrDefault("table.RTSP.Port")) ?? 554,
            Enabled: TryReadBool(kv, "table.RTSP.Enable") ?? true,
            // Null, not Fallback: the front end says whose bound it is showing, and this
            // vendor declares none.
            Range: null,
            Extras: extras);
    }

    /// <summary>
    /// Every interface in the <c>Network</c> document, with the default one marked. "The LAN
    /// address" is <c>Network.&lt;DefaultInterface&gt;.IPAddress</c> and never a fixed key —
    /// the 128-channel chassis lists six interfaces, four of them unconfigured bonds.
    /// </summary>
    internal static IReadOnlyList<NetworkInterfaceConfig> ParseInterfaces(
        Dictionary<string, string> kv, IReadOnlyDictionary<string, (int? Speed, bool? Up)> links)
    {
        string defaultName = kv.GetValueOrDefault("table.Network.DefaultInterface", "");

        // Interface blocks are named children of Network (eth0, bond0, …), so they are found
        // by looking for the one key every block has rather than by guessing names.
        const string prefix = "table.Network.";
        var names = new List<string>();
        foreach (string key in kv.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
                !key.EndsWith(".IPAddress", StringComparison.Ordinal))
                continue;
            string name = key[prefix.Length..^".IPAddress".Length];
            if (name.Length > 0 && !name.Contains('.') && !names.Contains(name))
                names.Add(name);
        }

        var list = new List<NetworkInterfaceConfig>();
        foreach (string name in names)
        {
            string F(string field) => kv.GetValueOrDefault($"{prefix}{name}.{field}", "");
            var dns = new List<string>();
            foreach (int i in IndexesInOrder(kv.Keys, $"{prefix}{name}.DnsServers["))
            {
                string address = kv.GetValueOrDefault($"{prefix}{name}.DnsServers[{i}]", "");
                if (address.Length > 0 && address != "0.0.0.0")
                    dns.Add(address);
            }

            bool dhcp = TryReadBool(kv, $"{prefix}{name}.DhcpEnable") ?? false;
            links.TryGetValue(name, out var link);

            list.Add(new NetworkInterfaceConfig
            {
                Id = name,
                Name = name,
                IsDefault = name.Equals(defaultName, StringComparison.OrdinalIgnoreCase),
                AddressingType = dhcp ? AddressingType.Dhcp : AddressingType.Static,
                IpAddress = F("IPAddress"),
                SubnetMask = F("SubnetMask"),
                Gateway = F("DefaultGateway"),
                Dns = dns,
                DnsAuto = TryReadBool(kv, $"{prefix}{name}.DnsAutoGet"),
                Mtu = TryParseInt(F("MTU")),
                MacAddress = F("PhysicalAddress"),
                LinkSpeedMbps = link.Speed,
                LinkUp = link.Up,
                // Dahua declares no bounds for any of this, so the choices are ours and the
                // front end must be able to tell that apart from a device-declared list.
                MtuRange = null,
                AddressingOptions = [],
            });
        }

        // The default interface first: it is the answer to "what is this recorder's address".
        return list.OrderByDescending(i => i.IsDefault).ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Link state and speed per NIC from <c>netApp.cgi</c>. Best effort and shape-tolerant:
    /// it is enrichment on the address block, and a firmware that words it differently should
    /// cost the address nothing.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, (int? Speed, bool? Up)>> ReadLinkStateAsync(
        CancellationToken ct)
    {
        var result = new Dictionary<string, (int? Speed, bool? Up)>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> kv;
        try
        {
            kv = ParseKeyValues(await GetTextAsync("/cgi-bin/netApp.cgi?action=getInterfaces", ct));
        }
        catch (Exception ex) when (ex is NvrException or HttpRequestException)
        {
            return result;
        }

        foreach (string key in kv.Keys.Where(k => k.EndsWith(".Name", StringComparison.Ordinal)))
        {
            string row = key[..^".Name".Length];
            string name = kv[key];
            if (name.Length == 0)
                continue;
            // 65535 is the sentinel an unconfigured bond reports on the live 608H — a
            // 65 Gbps NIC on a 2024 NVR is not a reading, it is "no link", and printing it as
            // a speed makes the whole column untrustworthy.
            int? speed = TryParseInt(kv.GetValueOrDefault($"{row}.Speed")) is int mbps &&
                mbps is > 0 and < 65535
                ? mbps
                : null;
            bool? up = TryReadBool(kv, $"{row}.Status") ?? TryReadBool(kv, $"{row}.Connected");
            if (up is null && kv.GetValueOrDefault($"{row}.Status") is { Length: > 0 } status)
                up = status.Equals("up", StringComparison.OrdinalIgnoreCase) ||
                     status.Equals("connect", StringComparison.OrdinalIgnoreCase);
            result[name] = (speed is > 0 ? speed : null, up);
        }
        return result;
    }

    /// <summary>
    /// The DST window as the device states it: the raw fields, verbatim. Dahua expresses it as
    /// absolute dates with a <c>Year</c> rather than a recurring rule, and the live 608H fills
    /// <c>Week</c> and <c>Day</c> in a combination its own documentation does not explain — so
    /// this reports the numbers and interprets none of them. A recorder left alone past New
    /// Year carries stale DST dates, and that is visible here without a guess.
    /// </summary>
    private static string DstDate(Dictionary<string, string> kv, string which)
    {
        var parts = new List<string>(5);
        foreach (string field in new[] { "Year", "Month", "Week", "Day", "Hour", "Minute" })
        {
            if (kv.GetValueOrDefault($"table.Locales.{which}.{field}") is { Length: > 0 } value)
                parts.Add($"{field}={value}");
        }
        return string.Join(" ", parts);
    }

    private async Task AddNotesAsync(List<ConfigNote> notes, CancellationToken ct)
    {
        var ddns = await TryGetConfigAsync("DDNS", ct);
        if (ddns is not null && TryReadBool(ddns, "table.DDNS[0].Enable") is bool ddnsOn)
            notes.Add(new ConfigNote("DDNS", "enabled", ddnsOn ? "yes" : "no"));

        var upnp = await TryGetConfigAsync("UPnP", ct);
        if (upnp is not null)
        {
            if (TryReadBool(upnp, "table.UPnP.Enable") is bool upnpOn)
                notes.Add(new ConfigNote("UPnP", "enabled", upnpOn ? "yes" : "no"));
            foreach (int i in IndexesInOrder(upnp.Keys, "table.UPnP.MapTable["))
            {
                string name = upnp.GetValueOrDefault($"table.UPnP.MapTable[{i}].ServiceName",
                    $"map {i}");
                string inner = upnp.GetValueOrDefault($"table.UPnP.MapTable[{i}].InnerPort", "?");
                string outer = upnp.GetValueOrDefault($"table.UPnP.MapTable[{i}].OuterPort", "?");
                notes.Add(new ConfigNote("UPnP", name, $"{inner} → {outer}"));
            }
        }

        var p2p = await TryGetConfigAsync("T2UServer", ct);
        if (p2p is not null && TryReadBool(p2p, "table.T2UServer.Enable") is bool p2pOn)
            notes.Add(new ConfigNote("P2P cloud", "enabled", p2pOn ? "yes" : "no"));
    }

    // ----- writes -----

    public async Task<ConfigChange> SetNtpAsync(NtpSettings requested,
        CancellationToken ct = default)
    {
        var before = RequireRead();
        var writes = new List<(string Key, string Value)>();
        if (requested.Address is { Length: > 0 } address)
            writes.Add(("NTP.Address", address));
        if (requested.Port is int port)
            writes.Add(("NTP.Port", port.ToString(CultureInfo.InvariantCulture)));
        if (requested.Interval is TimeSpan interval)
            writes.Add(("NTP.UpdatePeriod",
                ((int)Math.Round(interval.TotalMinutes)).ToString(CultureInfo.InvariantCulture)));
        if (requested.Enabled is bool enabled)
            writes.Add(("NTP.Enable", enabled ? "true" : "false"));
        if (writes.Count == 0)
            return Unchanged("NTP", before, "nothing was asked for");

        await CheckUnchangedAsync(NtpConfigName, "the NTP configuration", ct);
        await SetConfigAsync(writes, "NTP", ct);

        var after = await GetConfigurationAsync(ct);
        bool satisfied =
            (requested.Address is not { Length: > 0 } wantAddress ||
                after.NtpServers?.Any(s =>
                    s.Address.Equals(wantAddress, StringComparison.OrdinalIgnoreCase)) == true) &&
            (requested.Enabled is not bool wantEnabled || after.NtpEnabled == wantEnabled) &&
            (requested.Interval is not TimeSpan wantInterval ||
                after.NtpInterval is not TimeSpan held ||
                Math.Abs((held - wantInterval).TotalMinutes) <= 0.5);

        return new ConfigChange("NTP", before, after,
            Changed: before.NtpSummary != after.NtpSummary || before.NtpEnabled != after.NtpEnabled,
            Note: satisfied ? "" : "the recorder kept its own values")
        {
            Rejected = !satisfied,
        };
    }

    public async Task<ConfigChange> SetTimeAsync(TimeSettings requested,
        CancellationToken ct = default)
    {
        var before = RequireRead();
        var writes = new List<(string Key, string Value)>();

        // The zone is an index in the NTP document, and the label beside it is derived by the
        // device — so only the index is written, and the read-back reports the label it chose.
        if (requested.VendorZoneLabel is { Length: > 0 } zone)
        {
            string index = zone.Trim();
            if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return Unchanged("clock", before,
                    $"refused: Dahua's time zone is a vendor index (this device reads " +
                    $"\"{before.Clock.VendorZoneLabel}\"), not the text '{zone}'. Pass the number.");
            writes.Add(("NTP.TimeZone", index));
        }
        if (requested.DstEnabled is bool dst)
            writes.Add(("Locales.DSTEnable", dst ? "true" : "false"));
        if (requested.UseNtp is bool useNtp)
            writes.Add(("NTP.Enable", useNtp ? "true" : "false"));

        if (writes.Count == 0 && requested.WallClock is null)
            return Unchanged("clock", before, "nothing was asked for");

        if (writes.Any(w => w.Key.StartsWith("NTP.", StringComparison.Ordinal)))
            await CheckUnchangedAsync(NtpConfigName, "the NTP configuration", ct);
        if (writes.Any(w => w.Key.StartsWith("Locales.", StringComparison.Ordinal)))
            await CheckUnchangedAsync(LocalesConfigName, "the DST configuration", ct);
        if (writes.Count > 0)
            await SetConfigAsync(writes, "the clock configuration", ct);

        if (requested.WallClock is DateTime wall)
        {
            string reply = await GetTextAsync(
                "/cgi-bin/global.cgi?action=setCurrentTime&time=" +
                Uri.EscapeDataString(FormatCgiTime(wall)), ct);
            if (!reply.Contains("OK", StringComparison.OrdinalIgnoreCase))
                throw new NvrException("the device rejected the clock write", reply);
        }

        var after = await GetConfigurationAsync(ct);
        bool satisfied =
            (requested.DstEnabled is not bool wantDst || after.Clock.DstEnabled == wantDst) &&
            (requested.UseNtp is not bool wantNtp || after.NtpEnabled == wantNtp) &&
            (requested.VendorZoneLabel is not { Length: > 0 } wantZone ||
                (after.Clock.VendorZoneLabel ?? "").StartsWith(wantZone.Trim(),
                    StringComparison.Ordinal)) &&
            (requested.WallClock is not DateTime wantWall ||
                (after.Clock.WallClock - wantWall).Duration() <= TimeSpan.FromMinutes(2));

        return new ConfigChange("clock", before, after,
            Changed: before.Clock != after.Clock || before.NtpEnabled != after.NtpEnabled,
            Note: satisfied ? "" : "the recorder kept its own values")
        {
            Rejected = !satisfied,
        };
    }

    /// <inheritdoc/>
    public async Task<ConfigChange> SyncTimeNowAsync(CancellationToken ct = default)
    {
        var before = RequireRead();

        // The observed fault on this vendor was a recorder syncing NTP perfectly with DST
        // switched off. Stamping the right hour onto it by hand would be undone by the next
        // sync and would hide the cause, so the switch is what has to change.
        if (before.NtpEnabled is true)
            return Unchanged("clock", before,
                "this recorder syncs from NTP — a hand-set clock would be overwritten at the " +
                $"next sync. Its zone reads \"{before.Clock.VendorZoneLabel}\" with DST " +
                $"{(before.Clock.DstEnabled is true ? "on" : "off")}: fix that instead");
        return await SetTimeAsync(new TimeSettings(WallClock: DateTime.Now), ct);
    }

    public async Task<ConfigChange> SetDeviceNameAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var before = RequireRead();

        await CheckUnchangedAsync(GeneralConfigName, "the device name", ct);
        await SetConfigAsync([("General.MachineName", name.Trim())], "the device name", ct);

        var after = await GetConfigurationAsync(ct);
        bool satisfied = string.Equals(after.DeviceName?.Trim(), name.Trim(), StringComparison.Ordinal);
        return new ConfigChange("device name", before, after,
            Changed: before.DeviceName != after.DeviceName,
            Note: satisfied ? "" : "the recorder kept its own name")
        {
            Rejected = !satisfied,
        };
    }

    // ----- write plumbing -----

    private DeviceConfiguration RequireRead() =>
        _lastConfig ?? throw new NvrException(
            "read the configuration before writing it: a config write has to know what the " +
            "device currently holds, and there is no read behind this one");

    private static ConfigChange Unchanged(string field, DeviceConfiguration before, string note) =>
        new(field, before, before, Changed: false, Note: note);

    /// <summary>
    /// Refuses when the device's copy of a config document moved since the read. Dahua takes
    /// writes key by key rather than as a whole document, so this is not about preserving
    /// unknown fields — it is about not overwriting somebody else's edit made from a vendor
    /// console between the read and the write.
    /// </summary>
    private async Task CheckUnchangedAsync(string name, string what, CancellationToken ct)
    {
        if (!_configDocs.TryGetValue(name, out string? seen))
            throw new NvrException(
                $"read {what} before writing it: this client has never read name={name}");
        string now = await GetTextAsync(ConfigPath(name), ct);
        if (!SameDocument(seen, now))
            throw new NvrException(
                $"refusing to write {what}: name={name} changed on the device since it was " +
                "read (someone is editing this recorder from another client). Re-read and " +
                "try again.");
    }

    private static bool SameDocument(string a, string b) => string.Equals(
        string.Concat(a.Where(c => !char.IsWhiteSpace(c))),
        string.Concat(b.Where(c => !char.IsWhiteSpace(c))),
        StringComparison.Ordinal);

    /// <summary>
    /// One <c>setConfig</c> round trip for every key — the CGI takes several in one call, and
    /// a clock change that lands half-written is worse than one that fails.
    /// </summary>
    private async Task SetConfigAsync(IReadOnlyList<(string Key, string Value)> writes,
        string what, CancellationToken ct)
    {
        string path = "/cgi-bin/configManager.cgi?action=setConfig&" + string.Join("&",
            writes.Select(w => $"{w.Key}={Uri.EscapeDataString(w.Value)}"));
        string reply = await GetTextAsync(path, ct);
        if (!reply.Contains("OK", StringComparison.OrdinalIgnoreCase))
            throw new NvrException($"the device rejected the write to {what}", reply);
    }

    private async Task<Dictionary<string, string>?> TryGetConfigAsync(string name,
        CancellationToken ct, List<ConfigNote>? failures = null, string? what = null)
    {
        try
        {
            string text = await GetTextAsync(ConfigPath(name), ct);
            _configDocs[name] = text;
            return ParseKeyValues(text);
        }
        catch (NvrException ex) when (ex.StatusCode is not 401)
        {
            // A 403 "Authority:check failure." is a per-config permission denial and is
            // indistinguishable from an unknown config name — so it is reported as what it is
            // and never retried under another name.
            // The device's own words matter here: "Authority:check failure." is what an
            // operator has to be shown, and it is the whole reason this is not retried.
            if (failures is not null && what is not null)
                failures.Add(new ConfigNote("read", what, Shorten(ex.ResponseBody is
                    { Length: > 0 } body ? $"{ex.Message}: {body.Trim()}" : ex.Message)));
            return null;
        }
        catch (HttpRequestException ex)
        {
            if (failures is not null && what is not null)
                failures.Add(new ConfigNote("read", what, Shorten(ex.Message)));
            return null;
        }
    }

    /// <summary>
    /// A Dahua boolean, and null for anything that is not one. Strictness matters: the CGI
    /// answers link state as <c>up</c>/<c>down</c> in a field shaped like a boolean, and
    /// reading that as "false" would report a live NIC as down.
    /// </summary>
    private static bool? TryReadBool(Dictionary<string, string> kv, string key)
    {
        if (!kv.TryGetValue(key, out string? value))
            return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null,
        };
    }

    private static string Shorten(string text) => text.Length <= 240 ? text : text[..240] + "…";
}
