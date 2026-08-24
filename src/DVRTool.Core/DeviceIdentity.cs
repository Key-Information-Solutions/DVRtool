using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVRTool.Core;

/// <summary>
/// One device's address, as both front ends and the identity store spell it.
/// </summary>
/// <remarks>
/// A site's systems routinely sit behind one public IP on different forwarded ports, so a
/// bare host is not an address — it names a NAT rule set, not a machine. Everything that
/// keys anything by "which device" keys it by <see cref="Format"/>.
/// </remarks>
public static class DeviceAddress
{
    /// <summary>
    /// The canonical <c>host:port</c> key. Hosts are case-insensitive, so they are lowered
    /// here rather than left to each dictionary's comparer.
    /// </summary>
    public static string Format(string host, int port) =>
        $"{host.Trim().ToLowerInvariant()}:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Splits an operator-typed <c>host[:port]</c> — the form the panel list and
    /// <c>--host</c> both accept — falling back to <paramref name="defaultPort"/>.
    /// </summary>
    /// <remarks>
    /// IPv6 literals have to be bracketed (<c>[fe80::1]:8000</c>) to be split at all: a bare
    /// <c>fe80::1</c> is all colons and no port. An unbracketed literal is therefore taken
    /// whole, on the default port, rather than truncated at its first colon.
    /// </remarks>
    public static bool TryParse(string text, int defaultPort,
        [NotNullWhen(true)] out string? host, out int port, [NotNullWhen(false)] out string? error)
    {
        host = null;
        port = defaultPort;
        error = null;
        string value = text.Trim();
        if (value.Length == 0)
        {
            error = "empty address";
            return false;
        }

        string portText = "";
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close < 0)
            {
                error = $"'{value}' is missing the closing bracket on its IPv6 literal";
                return false;
            }
            host = value[1..close];
            string rest = value[(close + 1)..];
            if (rest.StartsWith(':'))
                portText = rest[1..];
            else if (rest.Length > 0)
            {
                error = $"unexpected text after the IPv6 literal in '{value}'";
                return false;
            }
        }
        else if (value.Count(c => c == ':') == 1)
        {
            int colon = value.IndexOf(':');
            host = value[..colon];
            portText = value[(colon + 1)..];
        }
        else
        {
            // Either no port at all, or a bare IPv6 literal — which cannot carry one.
            host = value;
        }

        if (host.Length == 0)
        {
            error = $"'{value}' has no host";
            return false;
        }

        if (portText.Length > 0)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port is < 1 or > 65535)
            {
                error = $"invalid port in '{value}'";
                host = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>Brackets a bare IPv6 literal so it is legal inside a URL.</summary>
    public static string ForUrl(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
}

/// <summary>
/// What a device says it is, reduced to the part that identifies the hardware.
/// </summary>
/// <remarks>
/// The serial is the whole of the identity; the model is carried for the operator's benefit
/// and is never compared. Firmware upgrades, renames and re-addressing all leave a serial
/// alone, which is exactly the property a pin needs.
/// </remarks>
public sealed record DeviceFingerprint
{
    public required string Serial { get; init; }

    public string Model { get; init; } = "";

    /// <summary>
    /// The serial as it is compared: whitespace-collapsed and upper-cased. Hikvision pads
    /// serials in some responses and Dahua's CGI is inconsistent about case.
    /// </summary>
    public string NormalizedSerial => Normalize(Serial);

    /// <summary>
    /// False when the device reported no serial at all. Some OEM firmware answers with an
    /// empty <c>serialNumber</c>, and such a device simply cannot be pinned — which has to
    /// be said out loud rather than pinned as "" and silently matched against every other
    /// serial-less device on the network.
    /// </summary>
    public bool IsUsable => NormalizedSerial.Length > 0;

    public static DeviceFingerprint From(DeviceInfo info) =>
        new() { Serial = info.SerialNumber, Model = info.Model };

    public bool SameDevice(DeviceFingerprint other) =>
        IsUsable && other.IsUsable && NormalizedSerial == other.NormalizedSerial;

    /// <summary>Operator-facing one-liner: the serial, with the model for orientation.</summary>
    public string Describe() => Model.Length > 0
        ? $"{(IsUsable ? Serial.Trim() : "(no serial)")} ({Model})"
        : IsUsable ? Serial.Trim() : "(no serial)";

    internal static string Normalize(string? serial) =>
        serial is null ? "" : string.Concat(serial.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}

/// <summary>The outcome of comparing a device against what was expected of it.</summary>
public enum IdentityVerdict
{
    /// <summary>Nothing was pinned for this address before; the serial seen is now pinned.</summary>
    FirstContact,

    /// <summary>Same hardware as last time.</summary>
    Match,

    /// <summary>
    /// A different device answered this address. The one failure mode this whole mechanism
    /// exists for — and the one that authentication alone cannot catch, because a fleet with
    /// shared credentials authenticates just as happily against the wrong recorder.
    /// </summary>
    Mismatch,

    /// <summary>
    /// The device reported no serial, so nothing can be pinned or compared. Not a mismatch,
    /// and not a pass: the question was not answered.
    /// </summary>
    Unverifiable,
}

/// <summary>One identity verification, with the prose to report it.</summary>
/// <param name="Address">The <c>host:port</c> that was dialed.</param>
/// <param name="Seen">What answered.</param>
/// <param name="Pinned">What was expected, when anything was.</param>
/// <param name="ExpectedBy">
/// Who held the expectation — a saved device's name, or a <c>--expect-serial</c> flag — so a
/// mismatch can say which record is wrong rather than only that something is.
/// </param>
public sealed record IdentityCheck(
    IdentityVerdict Verdict,
    string Address,
    DeviceFingerprint Seen,
    DeviceFingerprint? Pinned,
    string? ExpectedBy = null)
{
    /// <summary>
    /// True only when this is provably the right device, or provably the first sight of one.
    /// <see cref="IdentityVerdict.Unverifiable"/> is deliberately not trusted-but-not-fatal
    /// here: callers decide whether an unpinnable device is acceptable, and they should have
    /// to decide rather than inherit a default.
    /// </summary>
    public bool IsTrusted => Verdict is IdentityVerdict.FirstContact or IdentityVerdict.Match;

    /// <summary>What to tell the operator. Empty for the unremarkable <see cref="IdentityVerdict.Match"/>.</summary>
    public string Message => Verdict switch
    {
        IdentityVerdict.Match => "",
        IdentityVerdict.FirstContact =>
            $"{Address} pinned to {Seen.Describe()} — later connections must present this serial.",
        IdentityVerdict.Unverifiable =>
            $"{Address} reports no serial number, so its identity cannot be pinned. " +
            "Credentials shared across a site mean a wrong port still logs in; nothing here " +
            "can tell you this is the system you meant.",
        _ =>
            $"WRONG DEVICE: {Address} is {Seen.Describe()}, but " +
            $"{(ExpectedBy is null ? "this address was pinned to" : $"{ExpectedBy} expects")} " +
            $"serial {Pinned?.Serial.Trim() ?? "?"}. " +
            "Two systems behind one address are told apart only by port, so the usual cause " +
            "is a port that now forwards somewhere else — and shared credentials mean the " +
            "login succeeded anyway.",
    };
}

/// <summary>
/// Thrown when a device is not the device it was supposed to be. Deliberately not an
/// <see cref="NvrException"/>: the paths that treat a device error as "this one is
/// unreachable, carry on with the rest" must not treat this that way, because carrying on
/// means acting on the wrong hardware.
/// </summary>
public sealed class DeviceIdentityException : Exception
{
    public DeviceIdentityException(IdentityCheck check)
        : base(check.Message)
    {
        Check = check;
    }

    public IdentityCheck Check { get; }
}

/// <summary>
/// Trust-on-first-use pinning of device serial numbers, keyed by <c>host:port</c> — the
/// sibling of <see cref="CertificatePins"/>, and for the same reason.
/// </summary>
/// <remarks>
/// <para>
/// Authentication answers "are these credentials good here", never "is this the system I
/// meant". On a fleet with one shared account — the normal case for an integrator — every
/// recorder accepts the same login, so a mistyped or re-forwarded port authenticates
/// cleanly against the wrong box and everything downstream looks healthy: channels list,
/// footage exports, the file lands under the right site's name holding another site's video.
/// Multiple systems behind one IP make that a routine typo rather than an exotic failure.
/// </para>
/// <para>
/// So the first successful connection to an address records the serial it answered with,
/// and every later one must match. Pins live in <c>%APPDATA%\DVRTool\identities.json</c>,
/// next to the certificate pins, and are plain enough to hand-edit — a legitimately
/// replaced recorder is one deleted line.
/// </para>
/// <para>
/// Verification is read-only except on first contact, so the common path never writes.
/// </para>
/// </remarks>
public sealed class DeviceIdentityStore
{
    private static readonly Lazy<DeviceIdentityStore> Shared = new(() => new DeviceIdentityStore(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DVRTool", "identities.json")));

    private readonly object _gate = new();

    public DeviceIdentityStore(string path)
    {
        FilePath = path;
    }

    /// <summary>The process-wide store both front ends pin into.</summary>
    public static DeviceIdentityStore Default => Shared.Value;

    /// <summary>Where the pins live; named in every mismatch message so it can be edited.</summary>
    public string FilePath { get; }

    /// <summary>One pinned device. <c>PinnedAt</c> is never rewritten — see the type remarks.</summary>
    private sealed record Pin(
        [property: JsonPropertyName("serial")] string Serial,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("pinnedAt")] DateTimeOffset PinnedAt);

    /// <summary>What is pinned for <paramref name="address"/>, or null.</summary>
    public DeviceFingerprint? Pinned(string address)
    {
        lock (_gate)
            return Load().TryGetValue(address, out var pin)
                ? new DeviceFingerprint { Serial = pin.Serial, Model = pin.Model }
                : null;
    }

    /// <summary>Every pin, for collision reporting across a fleet.</summary>
    public IReadOnlyDictionary<string, DeviceFingerprint> All()
    {
        lock (_gate)
            return Load().ToDictionary(
                kv => kv.Key,
                kv => new DeviceFingerprint { Serial = kv.Value.Serial, Model = kv.Value.Model },
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares <paramref name="seen"/> against the pin for <paramref name="address"/>,
    /// pinning it when there is none.
    /// </summary>
    /// <param name="expected">
    /// A serial the caller already expects — a saved device's own pin, or an explicit
    /// assertion. Checked first and reported as the authority, because it is the one the
    /// operator can see.
    /// </param>
    /// <param name="expectedBy">Who holds <paramref name="expected"/>, for the message.</param>
    public IdentityCheck Verify(string address, DeviceFingerprint seen,
        string? expected = null, string? expectedBy = null)
    {
        if (!seen.IsUsable)
            return new IdentityCheck(IdentityVerdict.Unverifiable, address, seen, null, expectedBy);

        // The caller's own expectation outranks the store: it is the one attached to a record
        // with a name on it, so it produces the message that names what is misconfigured.
        if (DeviceFingerprint.Normalize(expected) is { Length: > 0 } wanted)
        {
            var pinned = new DeviceFingerprint { Serial = expected!, Model = "" };
            if (wanted != seen.NormalizedSerial)
                return new IdentityCheck(IdentityVerdict.Mismatch, address, seen, pinned, expectedBy);
        }

        lock (_gate)
        {
            var pins = Load();
            if (pins.TryGetValue(address, out var pin))
            {
                var known = new DeviceFingerprint { Serial = pin.Serial, Model = pin.Model };
                return known.SameDevice(seen)
                    ? new IdentityCheck(IdentityVerdict.Match, address, seen, known, expectedBy)
                    : new IdentityCheck(IdentityVerdict.Mismatch, address, seen, known, expectedBy);
            }

            pins[address] = new Pin(seen.Serial.Trim(), seen.Model, DateTimeOffset.Now);
            Save(pins);
            return new IdentityCheck(IdentityVerdict.FirstContact, address, seen, null, expectedBy);
        }
    }

    /// <summary>
    /// Drops the pin for an address — how a legitimately replaced device is accepted, and
    /// what the mismatch message points an operator at.
    /// </summary>
    public void Forget(string address)
    {
        lock (_gate)
        {
            var pins = Load();
            if (pins.Remove(address))
                Save(pins);
        }
    }

    /// <summary>Replaces the pin for an address outright (an operator-confirmed swap).</summary>
    public void Repin(string address, DeviceFingerprint seen)
    {
        if (!seen.IsUsable)
            return;
        lock (_gate)
        {
            var pins = Load();
            pins[address] = new Pin(seen.Serial.Trim(), seen.Model, DateTimeOffset.Now);
            Save(pins);
        }
    }

    private Dictionary<string, Pin> Load()
    {
        if (!File.Exists(FilePath))
            return new(StringComparer.OrdinalIgnoreCase);

        // A read failure on an EXISTING file fails closed (propagates) rather than returning
        // an empty set: empty would route every pinned address into the first-contact branch
        // and re-pin whatever answered, which is the check disabling itself in silence.
        // FileShare.ReadWrite tolerates the other front end writing concurrently.
        string json;
        using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
            json = reader.ReadToEnd();

        try
        {
            return new Dictionary<string, Pin>(
                JsonSerializer.Deserialize<Dictionary<string, Pin>>(json) ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Corrupt or hand-edited beyond repair: starting fresh is acceptable, as with
            // the certificate pins.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(Dictionary<string, Pin> pins)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(pins,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Failing to persist a pin must not break the connection in progress; the next
            // successful connect pins again.
        }
    }
}
