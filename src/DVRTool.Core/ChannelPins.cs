using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVRTool.Core;

/// <summary>
/// One camera the retention planner is not allowed to decide for: it holds an exact bitrate,
/// or it holds whatever it is set to now, and the planner works around it.
/// </summary>
/// <param name="Channel">The channel as the vendor client numbers it — the same number the tables show.</param>
/// <param name="Kbps">
/// The rate to hold, or null for "leave it exactly as it is". The two are different promises:
/// a number survives someone changing the camera by hand (the next plan puts it back), while
/// null is resolved against whatever the recorder reports at plan time.
/// </param>
/// <param name="CameraName">
/// The camera's name when it was pinned, so a pin can tell whether it is still pointing at the
/// same camera. Empty when the recorder would not answer its channel list — the check is then
/// skipped rather than guessed at.
/// </param>
/// <param name="Reason">Why, in the operator's words. Optional, and shown wherever the pin is.</param>
public sealed record ChannelPin(
    [property: JsonPropertyName("channel")] int Channel,
    [property: JsonPropertyName("kbps")] int? Kbps,
    [property: JsonPropertyName("camera")] string CameraName,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("pinnedAt")] DateTimeOffset PinnedAt)
{
    /// <summary>True for "hold whatever it reads", false for a pin that names a rate.</summary>
    public bool HoldsCurrent => Kbps is null;

    /// <summary>"ch3 4096 kbps", "ch3 keep current" — with the name and reason when there are any.</summary>
    public string Describe()
    {
        string text = $"ch{Channel} " + (Kbps is int kbps ? $"{kbps} kbps" : "keep current");
        if (CameraName.Length > 0)
            text += $" ({CameraName})";
        if (Reason is { Length: > 0 } reason)
            text += $" — {reason}";
        return text;
    }
}

/// <summary>
/// The pins of one device matched against the cameras just read: what the planner must hold,
/// and the pins that could not be honoured.
/// </summary>
/// <param name="Cameras">
/// The planner's input with <see cref="PlanCamera.PinnedKbps"/> filled in — pass this to
/// <see cref="StorageEstimator.PlanUniform"/>.
/// </param>
/// <param name="Problems">
/// Pins that were <em>not</em> applied, each already worded for an operator. Never silently
/// empty: a pin that cannot be honoured has to be said out loud, because the operator's belief
/// is that this camera is protected.
/// </param>
public sealed record PinnedPlanInput(
    IReadOnlyList<PlanCamera> Cameras, IReadOnlyList<string> Problems)
{
    public int PinnedCount => Cameras.Count(c => c.PinnedKbps is not null);
}

/// <summary>The pins recorded for one device address, as read off disk.</summary>
/// <param name="Serial">
/// The serial the pins were recorded against, or empty when the device reported none.
/// </param>
/// <param name="ForeignHardware">
/// True when the pins were recorded against a different serial than the caller expects — a
/// replaced recorder, or a forwarded port that now reaches somewhere else. The pins are then
/// carried but never applied: they were promises about other cameras.
/// </param>
public sealed record ChannelPinSet(
    string Address, string Serial, IReadOnlyList<ChannelPin> Pins, bool ForeignHardware = false)
{
    public static ChannelPinSet Empty(string address) => new(address, "", []);

    public int Count => Pins.Count;

    public ChannelPin? For(int channel) => Pins.FirstOrDefault(p => p.Channel == channel);

    /// <summary>"3 pinned: ch1 4096 kbps, ch4 keep current, ch9 8192 kbps", or "" for none.</summary>
    public string Summary
    {
        get
        {
            if (Pins.Count == 0)
                return "";
            var ordered = Pins.OrderBy(p => p.Channel).ToList();
            string text = $"{ordered.Count} pinned: " + string.Join(", ", ordered.Take(6)
                .Select(p => $"ch{p.Channel} " + (p.Kbps is int k ? $"{k} kbps" : "keep current")));
            if (ordered.Count > 6)
                text += $", … (+{ordered.Count - 6})";
            return text;
        }
    }

    /// <summary>
    /// Fills <see cref="PlanCamera.PinnedKbps"/> in from these pins, and reports every pin it
    /// could not apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pin is a promise about a <em>camera</em>, but it can only be stored against a channel
    /// number — and a channel number is not always the same thing twice. Nx Witness / DW
    /// Spectrum has no channel numbers at all: the client numbers the camera list sorted by
    /// name, so adding a camera called "Aaa" shifts every number after it. Honouring a pin by
    /// number alone would then quietly hold the wrong camera's bitrate, which is worse than not
    /// holding one. So the name recorded with the pin is checked, and a pin whose channel now
    /// answers to a different name is reported instead of applied.
    /// </para>
    /// <para>
    /// A rename is the false positive that costs one re-pin; a renumber is the true positive
    /// that would otherwise cost an operator their retention commitment, discovered months
    /// later. Where either name is unknown the check is skipped, because the recorder failing
    /// to answer its channel list is not evidence about anything.
    /// </para>
    /// </remarks>
    public PinnedPlanInput Apply(IReadOnlyList<PlanCamera> cameras)
    {
        var problems = new List<string>();
        if (Pins.Count == 0)
            return new PinnedPlanInput(cameras, problems);

        if (ForeignHardware)
        {
            problems.Add(
                $"{Count} pin(s) recorded for {Address} belong to serial " +
                $"{(Serial.Length > 0 ? Serial : "?")}, but this device is a different one — " +
                "none were applied. Clear them if the recorder was replaced.");
            return new PinnedPlanInput(cameras, problems);
        }

        var result = new List<PlanCamera>(cameras.Count);
        var byChannel = cameras.ToDictionary(c => c.Channel);
        foreach (var pin in Pins.OrderBy(p => p.Channel))
        {
            if (!byChannel.TryGetValue(pin.Channel, out var cam))
            {
                problems.Add($"ch{pin.Channel} is pinned ({pin.Describe()}) but is not among the " +
                    "cameras being planned — its recording stream is disabled, or the camera is gone.");
                continue;
            }
            if (Renamed(pin, cam))
            {
                problems.Add($"ch{pin.Channel} was pinned as \"{pin.CameraName}\" but that channel " +
                    $"is now \"{cam.Name}\" — the pin was IGNORED and the camera planned like any " +
                    "other. Re-pin it if this is still the camera you meant.");
                continue;
            }
            if (pin.Kbps is null && cam.CurrentKbps is null)
            {
                problems.Add($"ch{pin.Channel} is pinned to keep its current rate, but the device " +
                    "does not report one — the camera was planned like any other.");
                continue;
            }
            byChannel[pin.Channel] = cam with { PinnedKbps = pin.Kbps ?? cam.CurrentKbps };
        }

        // Input order is the display order; rebuilding from it keeps the tables stable.
        foreach (var cam in cameras)
            result.Add(byChannel[cam.Channel]);
        return new PinnedPlanInput(result, problems);
    }

    private static bool Renamed(ChannelPin pin, PlanCamera cam) =>
        pin.CameraName.Trim().Length > 0 && cam.Name.Trim().Length > 0 &&
        !string.Equals(pin.CameraName.Trim(), cam.Name.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Per-device retention preferences: which cameras the planner may not decide for. Lives in
/// <c>%APPDATA%\DVRTool\channel-pins.json</c>, keyed by the same <c>host:port</c> address as
/// the certificate and identity pins, and shared by both front ends.
/// </summary>
/// <remarks>
/// <para>
/// The planner's whole job is to trade quality across cameras until a retention target fits,
/// and it is uniform on purpose. Real sites are not uniform: one camera watches the till, one
/// watches a licence plate, one was set to 8 Mbps by the customer's insurer, and a plan that
/// evens all of them out is wrong in a way that is invisible until someone needs the footage.
/// A pin is how a site says "not this one" — so pins have to outlive the session that made
/// them, which is why they are a file and not a checkbox.
/// </para>
/// <para>
/// Failures here are deliberately loud, the opposite of <see cref="DeviceIdentityStore"/>'s
/// write policy. A pin the operator believes exists but that silently failed to save, or a file
/// that silently failed to load, both end the same way: the next plan writes over a bitrate
/// somebody promised a customer. So a read error on an existing file and a save error both
/// propagate, and the planner refuses rather than planning as if nothing were pinned.
/// </para>
/// </remarks>
public sealed class ChannelPinStore
{
    private static readonly Lazy<ChannelPinStore> Shared = new(() => new ChannelPinStore(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DVRTool", "channel-pins.json")));

    private readonly object _gate = new();

    public ChannelPinStore(string path)
    {
        FilePath = path;
    }

    /// <summary>The process-wide store both front ends read and write.</summary>
    public static ChannelPinStore Default => Shared.Value;

    /// <summary>Where the pins live; named wherever they are reported so they can be edited.</summary>
    public string FilePath { get; }

    /// <summary>One device's entry as stored.</summary>
    private sealed record Entry(
        [property: JsonPropertyName("serial")] string Serial,
        [property: JsonPropertyName("pins")] List<ChannelPin> Pins);

    /// <summary>
    /// The pins for <paramref name="address"/>, checked against the serial the caller expects.
    /// </summary>
    /// <param name="expectedSerial">
    /// The serial this device is known to answer with (a saved record's, or the identity pin
    /// for the same address). A stored serial that differs marks the set
    /// <see cref="ChannelPinSet.ForeignHardware"/>; null or empty on either side skips the
    /// check, since an unverifiable device is not evidence of a swap.
    /// </param>
    public ChannelPinSet Get(string address, string? expectedSerial = null)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(address, out var entry) || entry.Pins.Count == 0)
                return ChannelPinSet.Empty(address);
            bool foreign = DeviceFingerprint.Normalize(entry.Serial) is { Length: > 0 } stored &&
                           DeviceFingerprint.Normalize(expectedSerial) is { Length: > 0 } wanted &&
                           stored != wanted;
            return new ChannelPinSet(address, entry.Serial,
                entry.Pins.OrderBy(p => p.Channel).ToList(), foreign);
        }
    }

    /// <summary>Every device that has pins, for a fleet-wide look.</summary>
    public IReadOnlyList<ChannelPinSet> All()
    {
        lock (_gate)
            return Load()
                .Select(kv => new ChannelPinSet(kv.Key, kv.Value.Serial,
                    kv.Value.Pins.OrderBy(p => p.Channel).ToList()))
                .OrderBy(s => s.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>Pins a channel, replacing any pin it already had.</summary>
    /// <param name="serial">
    /// The device's serial, recorded so a later read can tell that these pins were made against
    /// this hardware. Empty is stored as empty and disables that check.
    /// </param>
    public void Pin(string address, string? serial, ChannelPin pin)
    {
        lock (_gate)
        {
            var all = Load();
            var entry = all.TryGetValue(address, out var existing)
                ? existing
                : new Entry("", []);
            entry.Pins.RemoveAll(p => p.Channel == pin.Channel);
            entry.Pins.Add(pin);
            entry.Pins.Sort((a, b) => a.Channel.CompareTo(b.Channel));
            // The serial is refreshed on every write: a device that only reports one later,
            // or an operator-confirmed replacement, should not leave a stale one behind.
            all[address] = entry with { Serial = (serial ?? "").Trim() };
            Save(all);
        }
    }

    /// <summary>Removes one channel's pin. False when it was not pinned.</summary>
    public bool Unpin(string address, int channel)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(address, out var entry) ||
                entry.Pins.RemoveAll(p => p.Channel == channel) == 0)
                return false;
            if (entry.Pins.Count == 0)
                all.Remove(address);
            Save(all);
            return true;
        }
    }

    /// <summary>Removes every pin for one device. Returns how many there were.</summary>
    public int Clear(string address)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(address, out var entry) || entry.Pins.Count == 0)
                return 0;
            int count = entry.Pins.Count;
            all.Remove(address);
            Save(all);
            return count;
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (!File.Exists(FilePath))
            return new(StringComparer.OrdinalIgnoreCase);

        // Both failure modes propagate on purpose — see the type remarks. Returning an empty
        // set would present "nothing is pinned" as a fact, and the next plan would act on it.
        // FileShare.ReadWrite tolerates the other front end writing concurrently.
        string json;
        using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
            json = reader.ReadToEnd();

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? [];
            return new Dictionary<string, Entry>(
                loaded.Select(kv => KeyValuePair.Create(kv.Key,
                    kv.Value with
                    {
                        // Hand-edited files are expected; a duplicated channel keeps the last
                        // one written rather than making the whole file unreadable.
                        Pins = kv.Value.Pins
                            .GroupBy(p => p.Channel)
                            .Select(g => g.Last())
                            .OrderBy(p => p.Channel)
                            .ToList(),
                    })),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{FilePath} is not readable as retention pins ({ex.Message}). Fix or delete it — " +
                "planning cannot continue, because it would treat pinned cameras as free to change.",
                ex);
        }
    }

    private void Save(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
