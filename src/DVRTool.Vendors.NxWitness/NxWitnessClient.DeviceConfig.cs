using System.Text.Json;
using DVRTool.Core;

namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// The <see cref="IDeviceConfigClient"/> face of the Nx client: a clock, a version, and a
/// clear "there is nothing else to read".
/// </summary>
/// <remarks>
/// <para>
/// Verified on DW Spectrum / Nx 6.1.1 (<c>docs/device-config-discovery.md</c>). A software VMS
/// has no device configuration in the sense the appliance vendors do:
/// <c>/rest/v3/system/settings</c>'s time keys configure a <b>distributed clock</b> —
/// <c>primaryTimeServer</c> names which server in the system is the clock master by GUID
/// (all-zero = follow the internet) — not an NTP client, and there is no time zone, no port
/// configuration and no LAN address, because those belong to Windows on the box.
/// <c>/rest/v3/system/time</c> and <c>/rest/v3/servers/this/time</c> are both 404 and are not
/// probed here.
/// </para>
/// <para>
/// So the scope is <see cref="ConfigScope.ClockOnly"/>, and that is a different cell from "the
/// read failed" — the distinction the whole feature turns on. There is deliberately <b>no
/// writer</b>: a distributed clock master is not an NTP server, and setting a zone means
/// logging into Windows.
/// </para>
/// </remarks>
public sealed partial class NxWitnessClient : IDeviceConfigClient
{
    private const string SystemInfoPath = "/rest/v3/system/info";
    private const string SettingsPath = "/rest/v3/system/settings";
    private const string ServersPath = "/rest/v3/servers";

    /// <summary>The VMS clock, rendered in the operator's zone like every other Nx time.</summary>
    public async Task<DeviceClock> GetClockAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(SystemInfoPath, ct);
        return ParseClock(doc.RootElement, FromUnixMs);
    }

    /// <summary>
    /// The payload, whether the server answered a bare v3 object or wrapped it in the older
    /// <c>reply</c> envelope the anonymous endpoints still use.
    /// </summary>
    internal static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reply", out var reply)
            ? reply
            : root;

    internal static DeviceClock ParseClock(JsonElement document, Func<long, DateTime> toWallClock)
    {
        var root = Unwrap(document);
        if (NxJson.Int64(root, "synchronizedTimeMs") is not long ms || ms <= 0)
            throw new NvrException(
                "the server did not report synchronizedTimeMs — its clock is unknown");

        // A UTC instant converted into the operator's zone: there is no declared offset to
        // report and no vendor zone label to show, which is not the same as those being wrong.
        return new DeviceClock(toWallClock(ms), DeclaredOffset: null, VendorZoneLabel: null,
            DstEnabled: null);
    }

    /// <summary>
    /// Nx has no NTP client, so <see cref="TimeSourceStatus.NtpEnabled"/> is null — "no NTP to
    /// have an opinion about", never "off". What it does have is reported as detail.
    /// </summary>
    public async Task<TimeSourceStatus> GetTimeSourceAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync(SettingsPath, ct);
            return new TimeSourceStatus(NtpEnabled: null, DescribeClockSync(Unwrap(doc.RootElement)));
        }
        catch (NvrException)
        {
            return TimeSourceStatus.Unknown;
        }
    }

    internal static string DescribeClockSync(JsonElement settings)
    {
        string? enabled = NxJson.Str(settings, "timeSynchronizationEnabled");
        string? master = NxJson.Str(settings, "primaryTimeServer");
        var parts = new List<string>(2);
        if (enabled is { Length: > 0 })
            parts.Add($"VMS clock sync {(IsTrue(enabled) ? "on" : "off")}");
        if (master is { Length: > 0 })
            parts.Add(IsAllZeroGuid(master)
                ? "no clock master (follows the internet)"
                : $"clock master {master}");
        return string.Join(", ", parts);
    }

    public async Task<DeviceConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        var notes = new List<ConfigNote>();
        var failures = new List<ConfigNote>();

        using var info = await GetJsonAsync(SystemInfoPath, ct);
        var clock = ParseClock(info.RootElement, FromUnixMs);
        var infoRoot = Unwrap(info.RootElement);
        string? version = NxJson.Str(infoRoot, "version");
        string? systemName = NxJson.Str(infoRoot, "systemName") ?? NxJson.Str(infoRoot, "name");

        // The distributed-clock settings, verbatim: they are what an operator can actually act
        // on here, and none of them is an NTP server.
        try
        {
            using var settings = await GetJsonAsync(SettingsPath, ct);
            var settingsRoot = Unwrap(settings.RootElement);
            foreach (string key in new[]
            {
                "timeSynchronizationEnabled", "primaryTimeServer", "syncTimeEpsilon",
                "syncTimeExchangePeriod", "maxDifferenceBetweenSynchronizedAndInternetTime",
                "maxDifferenceBetweenSynchronizedAndLocalTimeMs",
            })
            {
                if (NxJson.Str(settingsRoot, key) is { Length: > 0 } value)
                    notes.Add(new ConfigNote("VMS clock", key, value));
            }
        }
        catch (NvrException ex)
        {
            failures.Add(new ConfigNote("read", "VMS clock settings", ex.Message));
        }

        if (version is { Length: > 0 })
            notes.Add(new ConfigNote("System", "version", version));

        // The closest thing Nx has to a LAN address, and it is OBSERVED rather than
        // configured — which the note says, because presenting it as configuration would
        // invite an operator to try to change it here.
        try
        {
            using var servers = await GetJsonAsync(ServersPath, ct);
            foreach (var endpoint in Endpoints(Unwrap(servers.RootElement)))
                notes.Add(new ConfigNote("Observed addresses", "endpoint", endpoint));
        }
        catch (NvrException ex)
        {
            failures.Add(new ConfigNote("read", "server endpoints", ex.Message));
        }

        return new DeviceConfiguration
        {
            Clock = clock,
            DeviceName = systemName,
            FirmwareVersion = version,
            // Null, not empty: this recorder has no NTP client and no interfaces of its own,
            // which the front ends render as "n/a" with the reason rather than as a gap.
            NtpServers = null,
            NtpEnabled = null,
            Interfaces = null,
            Ports = [],
            Scope = ConfigScope.ClockOnly,
            Notes = notes,
            Failures = failures,
        };
    }

    internal static IReadOnlyList<string> Endpoints(JsonElement servers)
    {
        var list = new List<string>();
        if (servers.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var server in servers.EnumerateArray())
        {
            if (!server.TryGetProperty("endpoints", out var endpoints) ||
                endpoints.ValueKind != JsonValueKind.Array)
                continue;
            string name = NxJson.Str(server, "name") ?? "";
            foreach (var endpoint in endpoints.EnumerateArray())
            {
                string? text = endpoint.ValueKind == JsonValueKind.String
                    ? endpoint.GetString()
                    : NxJson.Str(endpoint, "address");
                if (text is { Length: > 0 })
                    list.Add(name.Length > 0 ? $"{name}: {text}" : text);
            }
        }
        return list;
    }

    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.Ordinal);

    private static bool IsAllZeroGuid(string value) =>
        value.Trim('{', '}').Replace("-", "").Trim('0').Length == 0;
}
