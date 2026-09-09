using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="IDeviceConfigWriter"/> face of the Hikvision client: the recorder's own
/// configuration — clock, NTP, service ports, LAN address, device name — as opposed to the
/// cameras on it.
/// </summary>
/// <remarks>
/// <para>
/// Every path here was observed answering on live firmware (<c>docs/device-config-discovery.md</c>,
/// an I-series DS-7716NI and an M-series DS-9632NI). Three findings shape the code:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Ports are one document</b>, <c>/ISAPI/Security/adminAccesses</c>, carrying all seven
/// protocols — and its <c>/capabilities</c> sibling spells the default attribute <b>two ways
/// in the same document</b>: <c>def=</c> on ids 1/2/5/6 and <c>default=</c> on 4/7. A parser
/// that reads one spelling silently loses the SDK ports' defaults.
/// </description></item>
/// <item><description>
/// <b><c>localTime</c> is a plain-text scalar</b> at <c>/ISAPI/System/time/localTime</c> —
/// the cheap read a fleet sweep wants, and a shape that crashes an XML-only reader. Its
/// offset is <em>wrong</em> on this firmware (it reported −05:00 while standing in −04:00, DST
/// rule and all), so the wall clock is kept and the offset is carried as a claim, never used
/// to build an instant. <c>ParseIsapiTime</c> already does exactly that and must not be
/// "fixed".
/// </description></item>
/// <item><description>
/// <b>Capabilities live at different depths from values.</b> Network field ranges exist only
/// at <c>/ISAPI/System/Network/interfaces/capabilities</c>; the per-interface
/// <c>/capabilities</c> sub-nodes are 403 <c>notSupport</c>, as are
/// <c>/ISAPI/System/Network</c> itself, <c>time/DSTMode</c>, <c>/serverPort</c>, <c>/http</c>,
/// <c>/https</c> and <c>/RTSP</c>. None of those are probed here.
/// </description></item>
/// </list>
/// <para>
/// Writes are read-modify-write and enforced as such: the M-series NTP document carries
/// <c>portType</c>, <c>customPortNo</c> and <c>hostNameExampleList</c> the I-series one does
/// not, so a hand-built minimal PUT drops them on every save. A writer therefore refuses to
/// write before it has read, re-reads the document immediately before writing, and refuses if
/// the device's copy moved in between.
/// </para>
/// </remarks>
public sealed partial class HikvisionClient : IDeviceConfigWriter
{
    private const string TimePath = "/ISAPI/System/time";
    private const string LocalTimePath = "/ISAPI/System/time/localTime";
    private const string NtpListPath = "/ISAPI/System/time/ntpServers";
    private const string NtpServerPath = "/ISAPI/System/time/ntpServers/1";
    private const string PortsPath = "/ISAPI/Security/adminAccesses";
    private const string PortCapsPath = "/ISAPI/Security/adminAccesses/capabilities";
    private const string InterfacesPath = "/ISAPI/System/Network/interfaces";
    private const string InterfaceCapsPath = "/ISAPI/System/Network/interfaces/capabilities";
    private const string DeviceInfoPath = "/ISAPI/System/deviceInfo";

    /// <summary>
    /// The documents the last read saw, kept verbatim so a write can be the device's own
    /// document with fields replaced — and so a write can tell that the device's copy changed
    /// underneath it. Keyed by path.
    /// </summary>
    private readonly Dictionary<string, string> _configDocs = new(StringComparer.Ordinal);

    /// <summary>The configuration the last read reported, the "before" of any write.</summary>
    private DeviceConfiguration? _lastConfig;

    // ----- reads -----

    /// <summary>
    /// The clock alone, over the plain-text scalar — one small GET, for a sweep that reads a
    /// whole fleet. Zone and DST come with <see cref="GetConfigurationAsync"/>; this call
    /// deliberately asks for nothing but the digits.
    /// </summary>
    public async Task<DeviceClock> GetClockAsync(CancellationToken ct = default)
    {
        string text = (await GetTextAsync(LocalTimePath, ct)).Trim();

        // Firmware that answers XML here instead of a scalar is served too: the element is
        // the same value, and the difference must not be an exception.
        if (text.StartsWith('<'))
        {
            var doc = XDocument.Parse(text);
            text = (Descendant(doc.Root, "localTime") ?? doc.Root?.Value ?? "").Trim();
        }
        return ParseClock(text, zoneLabel: null);
    }

    /// <summary>
    /// Whether the recorder keeps its own clock from NTP — <c>timeMode</c> in the one
    /// <c>/ISAPI/System/time</c> document, so this costs a single GET.
    /// </summary>
    public async Task<TimeSourceStatus> GetTimeSourceAsync(CancellationToken ct = default)
    {
        var doc = await TryGetXmlAsync(TimePath, ct, null);
        if (doc?.Root is null)
            return TimeSourceStatus.Unknown;
        string mode = (Descendant(doc.Root, "timeMode") ?? "").Trim();
        if (mode.Length == 0)
            return TimeSourceStatus.Unknown;
        bool ntp = mode.Equals("NTP", StringComparison.OrdinalIgnoreCase);
        return new TimeSourceStatus(ntp, $"timeMode={mode}");
    }

    public async Task<DeviceConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        var failures = new List<ConfigNote>();
        var notes = new List<ConfigNote>();

        // The clock is the part that matters, so it is the one read allowed to throw.
        var timeDoc = await GetXmlAndRememberAsync(TimePath, ct);
        string? zoneLabel = Descendant(timeDoc.Root, "timeZone")?.Trim();
        string timeMode = (Descendant(timeDoc.Root, "timeMode") ?? "").Trim();
        var clock = ParseClock(Descendant(timeDoc.Root, "localTime"), zoneLabel);

        // NTP: the list for the values, the single node because that is the document a write
        // has to round-trip (portType / customPortNo exist only on some firmware).
        IReadOnlyList<NtpServer>? servers = null;
        TimeSpan? interval = null;
        var ntpDoc = await TryGetXmlAndRememberAsync(NtpListPath, ct, failures, "NTP servers");
        if (ntpDoc?.Root is not null)
        {
            (servers, interval) = ParseNtpServers(ntpDoc);
            await TryGetXmlAndRememberAsync(NtpServerPath, ct, null, null);
        }

        // Ports: values and their declared ranges, one document each.
        var ports = new List<ServicePort>();
        var portsDoc = await TryGetXmlAndRememberAsync(PortsPath, ct, failures, "service ports");
        if (portsDoc?.Root is not null)
        {
            var caps = await TryGetXmlAsync(PortCapsPath, ct, null);
            ports.AddRange(ParsePorts(portsDoc, caps));
        }

        // Interfaces: values from the list, ranges and opt= lists from the list's
        // capabilities — different URLs at different depths, and the per-interface
        // capabilities do not exist at all.
        IReadOnlyList<NetworkInterfaceConfig>? interfaces = null;
        var ifDoc = await TryGetXmlAsync(InterfacesPath, ct, null);
        if (ifDoc?.Root is null)
            failures.Add(new ConfigNote("Network", "interfaces", "not readable on this device"));
        else
        {
            var ifCaps = await TryGetXmlAsync(InterfaceCapsPath, ct, null);
            interfaces = ParseInterfaces(ifDoc, ifCaps);
        }

        string? name = null, model = null, firmware = null;
        var infoDoc = await TryGetXmlAndRememberAsync(DeviceInfoPath, ct, failures, "device name");
        if (infoDoc?.Root is not null)
        {
            name = Descendant(infoDoc.Root, "deviceName");
            model = Descendant(infoDoc.Root, "model");
            firmware = Descendant(infoDoc.Root, "firmwareVersion");
        }

        notes.Add(new ConfigNote("Time", "timeMode", timeMode.Length > 0 ? timeMode : "?"));
        if (Descendant(timeDoc.Root, "timeType") is { Length: > 0 } timeType)
            notes.Add(new ConfigNote("Time", "timeType", timeType));
        await AddNotesAsync(notes, ct);

        var config = new DeviceConfiguration
        {
            Clock = clock,
            DeviceName = name,
            Model = model,
            FirmwareVersion = firmware,
            NtpServers = servers,
            NtpEnabled = timeMode.Length == 0
                ? null
                : timeMode.Equals("NTP", StringComparison.OrdinalIgnoreCase),
            NtpInterval = interval,
            Interfaces = interfaces,
            Ports = ports,
            Scope = ConfigScope.Appliance,
            Notes = notes,
            Failures = failures,
        };
        _lastConfig = config;
        return config;
    }

    // ----- parsing -----

    /// <summary>
    /// The wall clock, plus the offset the device *claims*. The claim is reported and never
    /// computed with: this firmware states −05:00 while standing in −04:00.
    /// </summary>
    internal static DeviceClock ParseClock(string? localTime, string? zoneLabel)
    {
        DateTime wall = ParseIsapiTime(localTime);
        TimeSpan? declared = null;
        if (localTime is { Length: > 0 } &&
            DateTimeOffset.TryParse(localTime, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto) &&
            (localTime.Contains('+') || localTime.LastIndexOf('-') > 7 ||
             localTime.EndsWith("Z", StringComparison.OrdinalIgnoreCase)))
            declared = dto.Offset;

        // DST on Hikvision is folded into the timeZone string rather than being its own
        // switch — /ISAPI/System/time/DSTMode is 403 notSupport even where
        // /ISAPI/System/capabilities claims isSupportDst — so there is no boolean to report.
        return new DeviceClock(wall, declared, zoneLabel is { Length: > 0 } ? zoneLabel : null,
            DstEnabled: null);
    }

    private static (IReadOnlyList<NtpServer> Servers, TimeSpan? Interval) ParseNtpServers(
        XDocument doc)
    {
        var servers = new List<NtpServer>();
        TimeSpan? interval = null;
        foreach (var el in ElementsNamed(doc.Root!, "NTPServer"))
        {
            int id = int.TryParse(Child(el, "id"), out int parsed) ? parsed : servers.Count + 1;
            string address = Child(el, "hostName") is { Length: > 0 } host
                ? host
                : Child(el, "ipAddress") ?? "";
            int port = int.TryParse(Child(el, "portNo"), out int p) ? p : 123;
            if (int.TryParse(Child(el, "synchronizeInterval"), out int minutes) && minutes > 0)
                interval ??= TimeSpan.FromMinutes(minutes);
            servers.Add(new NtpServer(id, address.Trim(), port));
        }
        // Some firmware carries the interval once for the list rather than per server.
        if (interval is null &&
            int.TryParse(Descendant(doc.Root, "synchronizeInterval"), out int listMinutes) &&
            listMinutes > 0)
            interval = TimeSpan.FromMinutes(listMinutes);
        return (servers, interval);
    }

    private static readonly string[] PortOwnElements = ["id", "enabled", "protocol", "portNo"];

    internal static IReadOnlyList<ServicePort> ParsePorts(XDocument doc, XDocument? caps)
    {
        var ranges = new Dictionary<int, ValueRange>();
        if (caps?.Root is not null)
        {
            foreach (var el in ElementsNamed(caps.Root, "AdminAccessProtocol"))
            {
                if (!int.TryParse(Child(el, "id"), out int id))
                    continue;
                var portEl = el.Elements().FirstOrDefault(e => e.Name.LocalName == "portNo");
                if (ReadRange(portEl) is { } range)
                    ranges[id] = range;
            }
        }

        var ports = new List<ServicePort>();
        foreach (var el in ElementsNamed(doc.Root!, "AdminAccessProtocol"))
        {
            if (!int.TryParse(Child(el, "id"), out int id))
                continue;
            string protocol = (Child(el, "protocol") ?? "").Trim();
            if (!int.TryParse(Child(el, "portNo"), out int port))
                continue;

            // Per protocol, and HTTPS ships false: without the switch beside the number an
            // operator sets 443 and watches nothing happen.
            bool enabled = !string.Equals(Child(el, "enabled"), "false",
                StringComparison.OrdinalIgnoreCase);

            var extras = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var child in el.Elements())
            {
                if (PortOwnElements.Contains(child.Name.LocalName))
                    continue;
                extras[child.Name.LocalName] = child.Value.Trim();
            }

            ports.Add(new ServicePort(id, protocol, MapServiceKind(protocol), port, enabled,
                ranges.GetValueOrDefault(id), extras));
        }
        return ports.OrderBy(p => p.Id).ToList();
    }

    /// <summary>
    /// <c>DEV_MANAGE</c> is the vendor SDK port — the one <see cref="VendorPorts.Sdk"/>
    /// defaults to 8000 and the one Hikvision live view actually rides — so mapping it is what
    /// lets a config view check the saved record against the device.
    /// </summary>
    internal static ServiceKind MapServiceKind(string protocol) => protocol.ToUpperInvariant() switch
    {
        "HTTP" => ServiceKind.Http,
        "HTTPS" => ServiceKind.Https,
        "RTSP" => ServiceKind.Rtsp,
        "DEV_MANAGE" => ServiceKind.Sdk,
        "SDK_OVER_TLS" => ServiceKind.SdkOverTls,
        "WEBSOCKET" => ServiceKind.WebSocket,
        "IOT" => ServiceKind.Iot,
        _ => ServiceKind.Other,
    };

    /// <summary>
    /// The min/max/default attributes of one element. <b>The default is spelled both
    /// <c>def=</c> and <c>default=</c> in the same capabilities document</b> — ids 1/2/5/6 one
    /// way, 4/7 the other — so both are read here, once, rather than in seven places.
    /// </summary>
    internal static ValueRange? ReadRange(XElement? el)
    {
        if (el is null)
            return null;
        if (!TryAttr(el, "min", out int min) || !TryAttr(el, "max", out int max) || max < min)
            return null;
        int? def = TryAttr(el, "def", out int d) || TryAttr(el, "default", out d) ? d : null;
        return new ValueRange(min, max, def);
    }

    private static bool TryAttr(XElement el, string name, out int value)
    {
        value = 0;
        var attr = el.Attributes().FirstOrDefault(a => a.Name.LocalName == name);
        return attr is not null && int.TryParse(attr.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value);
    }

    private static IReadOnlyList<string> ReadOptions(XElement? el)
    {
        var attr = el?.Attributes().FirstOrDefault(a => a.Name.LocalName == "opt");
        return attr is null
            ? []
            : attr.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static IReadOnlyList<NetworkInterfaceConfig> ParseInterfaces(
        XDocument doc, XDocument? caps)
    {
        // Field ranges and opt= lists exist only at the list level; a per-interface
        // capabilities node is 403 notSupport on this firmware.
        var capsIp = caps?.Root?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "IPAddress");
        var addressingOptions = ReadOptions(capsIp?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "addressingType"));
        var mtuRange = ReadRange(caps?.Root?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "MTU"));

        var list = new List<NetworkInterfaceConfig>();
        foreach (var el in ElementsNamed(doc.Root!, "NetworkInterface"))
        {
            string id = (Child(el, "id") ?? $"{list.Count + 1}").Trim();
            var ip = el.Descendants().FirstOrDefault(e => e.Name.LocalName == "IPAddress");
            var link = el.Descendants().FirstOrDefault(e => e.Name.LocalName == "Link");

            var dns = new List<string>();
            foreach (string name in new[] { "PrimaryDNS", "SecondaryDNS" })
            {
                string? address = ip?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == name)?
                    .Descendants().FirstOrDefault(e => e.Name.LocalName == "ipAddress")?.Value;
                if (address is { Length: > 0 })
                    dns.Add(address.Trim());
            }

            int? speed = int.TryParse(link is null ? null : Child(link, "speed"), out int s) && s > 0
                ? s
                : null;

            list.Add(new NetworkInterfaceConfig
            {
                Id = id,
                Name = Child(el, "ifName") is { Length: > 0 } ifName ? ifName : $"LAN{id}",
                // Hikvision recorders in the fleet have one interface, so the first is the
                // one "the LAN address" means; Dahua names its own default explicitly.
                IsDefault = list.Count == 0,
                AddressingType = MapAddressing(ip is null ? null : Child(ip, "addressingType")),
                IpAddress = (ip is null ? null : Child(ip, "ipAddress")) ?? "",
                SubnetMask = (ip is null ? null : Child(ip, "subnetMask")) ?? "",
                Gateway = ip?.Descendants().FirstOrDefault(e => e.Name.LocalName == "DefaultGateway")?
                    .Descendants().FirstOrDefault(e => e.Name.LocalName == "ipAddress")?.Value ?? "",
                Dns = dns,
                DnsAuto = (ip is null ? null : Child(ip, "DNSEnable")) is { Length: > 0 } dnsEnable
                    ? string.Equals(dnsEnable, "true", StringComparison.OrdinalIgnoreCase)
                    : null,
                Mtu = int.TryParse(link is null ? null : Child(link, "MTU"), out int mtu) && mtu > 0
                    ? mtu
                    : null,
                MacAddress = (link is null ? null : Child(link, "MACAddress")) ?? "",
                LinkSpeedMbps = speed,
                LinkUp = speed is > 0 ? true : null,
                MtuRange = mtuRange,
                AddressingOptions = addressingOptions,
            });
        }
        return list;
    }

    private static AddressingType MapAddressing(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "static" => AddressingType.Static,
        "dynamic" or "dhcp" => AddressingType.Dhcp,
        "apipa" => AddressingType.Apipa,
        _ => AddressingType.Unknown,
    };

    /// <summary>
    /// The extras worth showing beside the configuration proper: DDNS, the UPnP port map,
    /// PPPoE and uptime. Every one of these is optional — a device that does not answer one
    /// simply has no note about it, which is not a failure of the read.
    /// </summary>
    private async Task AddNotesAsync(List<ConfigNote> notes, CancellationToken ct)
    {
        var ddns = await TryGetXmlAsync("/ISAPI/System/Network/DDNS", ct, null);
        if (ddns?.Root is not null)
        {
            bool enabled = string.Equals(Descendant(ddns.Root, "enabled"), "true",
                StringComparison.OrdinalIgnoreCase);
            notes.Add(new ConfigNote("DDNS", "enabled", enabled ? "yes" : "no"));
            if (enabled && Descendant(ddns.Root, "domainName") is { Length: > 0 } domain)
                notes.Add(new ConfigNote("DDNS", "domain", domain));
        }

        var pppoe = await TryGetXmlAsync("/ISAPI/System/Network/PPPoE", ct, null);
        if (pppoe?.Root is not null && Descendant(pppoe.Root, "enabled") is { Length: > 0 } ppp)
            notes.Add(new ConfigNote("PPPoE", "enabled", ppp));

        var upnp = await TryGetXmlAsync("/ISAPI/System/Network/UPnP/ports", ct, null);
        if (upnp?.Root is not null)
        {
            foreach (var el in ElementsNamed(upnp.Root, "portMapping"))
            {
                string kind = Child(el, "portType") ?? Child(el, "protocolType") ?? "port";
                string inner = Child(el, "internalPort") ?? "?";
                string outer = Child(el, "externalPort") ?? "?";
                notes.Add(new ConfigNote("UPnP", kind, $"{inner} → {outer}"));
            }
        }

        var status = await TryGetXmlAsync("/ISAPI/System/status", ct, null);
        if (status?.Root is not null &&
            int.TryParse(Descendant(status.Root, "deviceUpTime"), out int uptime) && uptime > 0)
            notes.Add(new ConfigNote("System", "uptime",
                $"{TimeSpan.FromSeconds(uptime).TotalDays:0.0} days"));
    }

    // ----- writes -----

    public async Task<ConfigChange> SetNtpAsync(NtpSettings requested,
        CancellationToken ct = default)
    {
        var before = RequireRead();
        if (requested.Address is null && requested.Port is null &&
            requested.Interval is null && requested.Enabled is null)
            return Unchanged("NTP", before, "nothing was asked for");

        if (requested.Address is not null || requested.Port is not null ||
            requested.Interval is not null)
        {
            // The device's own document with fields replaced: the M-series carries portType
            // and customPortNo that a synthesized document would drop.
            var doc = await ReReadForWriteAsync(NtpServerPath, "the NTP configuration", ct);
            var server = doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NTPServer")
                ?? doc.Root
                ?? throw new NvrException($"GET {NtpServerPath} returned an empty document");

            if (requested.Address is { Length: > 0 } address)
            {
                bool isIp = System.Net.IPAddress.TryParse(address, out _);
                SetChild(server, "addressingFormatType", isIp ? "ipaddress" : "hostname");
                // Both fields exist on this firmware; the format type decides which is used,
                // and leaving the other stale is how a "successful" write points at the old
                // server.
                SetChild(server, isIp ? "ipAddress" : "hostName", address);
            }
            if (requested.Port is int port)
                SetChild(server, "portNo", port.ToString(CultureInfo.InvariantCulture));
            if (requested.Interval is TimeSpan interval)
                SetChild(server, "synchronizeInterval",
                    ((int)Math.Round(interval.TotalMinutes)).ToString(CultureInfo.InvariantCulture));

            await PutXmlAsync(NtpServerPath, doc, ct);
        }

        if (requested.Enabled is bool enabled)
            await WriteTimeDocumentAsync(new TimeSettings(UseNtp: enabled), ct);

        var after = await GetConfigurationAsync(ct);
        bool satisfied = Satisfies(after, requested);
        return new ConfigChange("NTP", before, after,
            Changed: before.NtpSummary != after.NtpSummary ||
                before.NtpEnabled != after.NtpEnabled,
            Note: satisfied ? "" : "the recorder kept its own values")
        {
            Rejected = !satisfied,
        };
    }

    private static bool Satisfies(DeviceConfiguration after, NtpSettings requested)
    {
        if (requested.Enabled is bool wanted && after.NtpEnabled is bool actual &&
            wanted != actual)
            return false;
        if (requested.Address is { Length: > 0 } address &&
            after.NtpServers?.Any(s =>
                s.Address.Equals(address, StringComparison.OrdinalIgnoreCase)) != true)
            return false;
        if (requested.Port is int port && after.NtpServers?.Any(s => s.Port == port) != true)
            return false;
        if (requested.Interval is TimeSpan interval && after.NtpInterval is TimeSpan held &&
            Math.Abs((held - interval).TotalMinutes) > 0.5)
            return false;
        return true;
    }

    public async Task<ConfigChange> SetTimeAsync(TimeSettings requested,
        CancellationToken ct = default)
    {
        var before = RequireRead();
        // Named before the "nothing was asked for" case: asking for DST here is a mistake
        // worth answering, not an empty request.
        if (requested.DstEnabled is not null)
            return Unchanged("clock", before,
                "Hikvision has no DST switch — daylight saving is part of the timeZone " +
                "string, and /ISAPI/System/time/DSTMode does not exist on this firmware");
        if (requested.VendorZoneLabel is null && requested.WallClock is null &&
            requested.UseNtp is null)
            return Unchanged("clock", before, "nothing was asked for");

        await WriteTimeDocumentAsync(requested, ct);
        var after = await GetConfigurationAsync(ct);

        bool satisfied =
            (requested.VendorZoneLabel is not { Length: > 0 } zone ||
                string.Equals(after.Clock.VendorZoneLabel?.Trim(), zone.Trim(),
                    StringComparison.Ordinal)) &&
            (requested.UseNtp is not bool ntp || after.NtpEnabled == ntp) &&
            (requested.WallClock is not DateTime wall ||
                (after.Clock.WallClock - wall).Duration() <= TimeSpan.FromMinutes(2));

        return new ConfigChange("clock", before, after,
            Changed: before.Clock != after.Clock || before.NtpEnabled != after.NtpEnabled,
            Note: satisfied ? "" : "the recorder kept its own values")
        {
            Rejected = !satisfied,
        };
    }

    /// <summary>
    /// Stamps this workstation's wall clock onto the recorder — and refuses to do it on a
    /// recorder that is syncing from NTP, because taking it off its time source to set the
    /// clock by hand trades a wrong hour today for a clock nobody is keeping.
    /// </summary>
    public async Task<ConfigChange> SyncTimeNowAsync(CancellationToken ct = default)
    {
        var before = RequireRead();
        if (before.NtpEnabled is true)
            return Unchanged("clock", before,
                "this recorder syncs from NTP — setting the clock by hand would switch it to " +
                "manual and leave it with no time source. Fix the zone or the NTP server " +
                "instead");
        return await SetTimeAsync(new TimeSettings(WallClock: DateTime.Now), ct);
    }

    public async Task<ConfigChange> SetDeviceNameAsync(string name,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var before = RequireRead();

        // deviceInfo/capabilities declares no length or character rule for this field — it
        // returns the same document with no opt=/min= on anything but telecontrolID — so the
        // bound here is ours, deliberately conservative, and the read-back is what decides.
        if (name.Trim().Length > 32)
            return Unchanged("device name", before,
                "refused: longer than 32 characters, and this firmware declares no length " +
                "rule to check against");

        var doc = await ReReadForWriteAsync(DeviceInfoPath, "the device information", ct);
        var root = doc.Root ?? throw new NvrException($"GET {DeviceInfoPath} returned no document");
        SetChild(root, "deviceName", name.Trim());
        await PutXmlAsync(DeviceInfoPath, doc, ct);

        var after = await GetConfigurationAsync(ct);
        bool satisfied = string.Equals(after.DeviceName?.Trim(), name.Trim(), StringComparison.Ordinal);
        return new ConfigChange("device name", before, after,
            Changed: before.DeviceName != after.DeviceName,
            Note: satisfied ? "" : "the recorder kept its own name")
        {
            Rejected = !satisfied,
        };
    }

    private async Task WriteTimeDocumentAsync(TimeSettings requested, CancellationToken ct)
    {
        var doc = await ReReadForWriteAsync(TimePath, "the time configuration", ct);
        var root = doc.Root ?? throw new NvrException($"GET {TimePath} returned no document");

        if (requested.UseNtp is bool ntp)
            SetChild(root, "timeMode", ntp ? "NTP" : "manual");
        if (requested.VendorZoneLabel is { Length: > 0 } zone)
            SetChild(root, "timeZone", zone.Trim());
        if (requested.WallClock is DateTime wall)
        {
            // Written in the shape the device sent it: ISAPI wants the "Z"-suffixed form and
            // treats it as its own local wall clock, which is the same lie in both directions
            // and the reason nothing here converts.
            SetChild(root, "timeMode", "manual");
            SetChild(root, "localTime", FormatIsapiTime(wall));
        }
        await PutXmlAsync(TimePath, doc, ct);
    }

    // ----- write plumbing -----

    /// <summary>
    /// The configuration this client last read, or a refusal. A writer with no read behind it
    /// would have to synthesize the document it PUTs, and the M-series NTP fields prove what
    /// that costs.
    /// </summary>
    private DeviceConfiguration RequireRead() =>
        _lastConfig ?? throw new NvrException(
            "read the configuration before writing it: a config write is the device's own " +
            "document with fields replaced, and there is no document yet");

    private static ConfigChange Unchanged(string field, DeviceConfiguration before, string note) =>
        new(field, before, before, Changed: false, Note: note);

    /// <summary>
    /// Re-reads a document immediately before writing it and refuses when the device's copy
    /// moved since the read. Schedules on a live site were observed changing under an operator
    /// mid-batch; a configuration edited from a vendor console is the same hazard, and
    /// clobbering it silently is worse than refusing.
    /// </summary>
    private async Task<XDocument> ReReadForWriteAsync(string path, string what,
        CancellationToken ct)
    {
        if (!_configDocs.TryGetValue(path, out string? seen))
            throw new NvrException(
                $"read {what} before writing it: this client has never read {path}");

        string now = await GetTextAsync(path, ct);
        if (!SameDocument(seen, now))
            throw new NvrException(
                $"refusing to write {what}: {path} changed on the device since it was read " +
                "(someone is editing this recorder from another client). Re-read and try again.");
        return XDocument.Parse(now);
    }

    /// <summary>
    /// Whether two readings of the same document say the same thing. Whitespace is ignored
    /// because firmware reformats freely between identical reads.
    /// </summary>
    private static bool SameDocument(string a, string b)
    {
        static string Normalize(string text)
        {
            try
            {
                return XDocument.Parse(text).ToString(SaveOptions.DisableFormatting);
            }
            catch (System.Xml.XmlException)
            {
                return string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
            }
        }
        return string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
    }

    /// <summary>Sets a child element's text, creating it only if the document has none.</summary>
    private static void SetChild(XElement parent, string localName, string value)
    {
        var el = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        if (el is null)
            parent.Add(new XElement(parent.Name.Namespace + localName, value));
        else
            el.Value = value;
    }

    private async Task PutXmlAsync(string path, XDocument doc, CancellationToken ct)
    {
        using var content = new StringContent(doc.ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8, "application/xml");
        using var resp = await _http.PutAsync(path, content, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException($"PUT {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}",
                text, (int)resp.StatusCode);

        // ISAPI answers a ResponseStatus document; statusCode 1 is OK, and anything else is a
        // rejection even under HTTP 200.
        if (TryParseStatusCode(text) is { } status && status != 1)
            throw new NvrException($"the device rejected the write to {path} (statusCode {status})",
                text);
    }

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException($"GET {path} → {(int)resp.StatusCode} {resp.ReasonPhrase}",
                text, (int)resp.StatusCode);
        return text;
    }

    private async Task<XDocument> GetXmlAndRememberAsync(string path, CancellationToken ct)
    {
        string text = await GetTextAsync(path, ct);
        _configDocs[path] = text;
        try
        {
            return XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            throw new NvrException(
                $"GET {path} returned a non-XML response (web login page? wrong port?)", text);
        }
    }

    private async Task<XDocument?> TryGetXmlAndRememberAsync(string path, CancellationToken ct,
        List<ConfigNote>? failures, string? what)
    {
        try
        {
            return await GetXmlAndRememberAsync(path, ct);
        }
        catch (NvrException ex) when (ex.StatusCode is not 401)
        {
            if (failures is not null && what is not null)
                failures.Add(new ConfigNote("read", what, Shorten(ex.Message)));
            return null;
        }
        catch (HttpRequestException ex)
        {
            if (failures is not null && what is not null)
                failures.Add(new ConfigNote("read", what, Shorten(ex.Message)));
            return null;
        }
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "…";
}
