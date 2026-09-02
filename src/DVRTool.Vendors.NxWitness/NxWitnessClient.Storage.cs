using System.Text.Json;
using System.Text.Json.Nodes;
using DVRTool.Core;

namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// The <see cref="IStorageClient"/> face of the Nx client: storage volumes, the recording
/// schedule of every camera read as a worst-case stream, oldest footage, and the
/// schedule-bitrate write.
/// </summary>
/// <remarks>
/// See <c>docs/nx-witness-storage.md</c> before changing endpoints or units here. Nx is a
/// software recorder, and three things differ from the appliance vendors: a "disk" is a
/// storage volume with a reserved slice the archive never uses; a camera's "max bitrate" is
/// whatever its busiest schedule cell asks for — an explicit preset, or a quality Nx converts
/// to kbps with its own formula (<see cref="NxBitrate"/>); and the server archives the
/// secondary (low-quality) stream alongside the primary unless told not to, so every camera
/// carries <see cref="CameraStream.SecondaryRecordedKbps"/> and the retention total is the sum.
/// </remarks>
public sealed partial class NxWitnessClient : IStorageClient
{
    /// <summary>The far end of the everything window (2100-01-01T00:00:00Z).</summary>
    private const long EverythingEndMs = 4_102_444_800_000;

    public async Task<StorageInfo> GetStorageInfoAsync(CancellationToken ct = default)
    {
        string serverId = await GetServerIdAsync(ct);
        using var storages = await GetJsonAsync($"/rest/v3/servers/{serverId}/storages", ct);

        // Sizes live in the legacy call; the REST list has the reserve, the role and the
        // path. Optional: a server that refuses it still yields the volume list, with the
        // capacity marked unknown rather than invented.
        JsonDocument? space = null;
        try
        {
            space = await GetJsonAsync("/api/storageSpace", ct);
        }
        catch (NvrException)
        {
        }
        using (space)
        {
            return ParseStorages(storages.RootElement, space?.RootElement);
        }
    }

    /// <summary>The media server that answered, from the anonymous module information.</summary>
    private async Task<string> GetServerIdAsync(CancellationToken ct)
    {
        if (_serverId is { Length: > 0 } known)
            return known;
        using var doc = await GetJsonAsync("/api/moduleInformation", ct, authenticated: false);
        var info = ParseModuleInformation(doc.RootElement);
        if (info.SerialNumber.Length == 0)
            throw new NvrException("moduleInformation carries no server id", doc.RootElement.GetRawText());
        _serverId = info.SerialNumber;
        return _serverId;
    }

    /// <summary>
    /// One <see cref="HddInfo"/> per storage volume. Capacity is the volume minus its reserve
    /// (<c>spaceLimitB</c>) — the space the archive may actually fill — and free space likewise,
    /// so a full recorder reads 0 free exactly as the appliance vendors do. A backup volume, or
    /// one not used for writing, is listed but does not count toward retention.
    /// </summary>
    internal static StorageInfo ParseStorages(JsonElement storages, JsonElement? storageSpace)
    {
        var sizes = ParseStorageSpace(storageSpace);
        var hdds = new List<HddInfo>();
        var list = storages.ValueKind == JsonValueKind.Array
            ? storages
            : NxJson.Prop(storages, "reply") ?? default;
        if (list.ValueKind != JsonValueKind.Array)
            return new StorageInfo(hdds, WorkMode: null, MaxSupportedHdds: null);

        int index = 0;
        foreach (var s in list.EnumerateArray())
        {
            index++;
            string id = NxJson.StripBraces(NxJson.Str(s, "id"));
            string path = NxJson.Str(s, "path") ?? NxJson.Str(s, "url") ?? NxJson.Str(s, "name")
                ?? $"storage {index}";
            bool usedForWriting = NxJson.Bool(s, "isUsedForWriting") ?? true;
            bool backup = NxJson.Bool(s, "isBackup") ?? false;
            long reserved = NxJson.Int64(s, "spaceLimitB") ?? NxJson.Int64(s, "spaceLimit") ?? 0;
            string type = NxJson.Str(s, "type") ?? NxJson.Str(s, "storageType") ?? "";
            // v3 carries the volume's size as parameters.space (verified live); the legacy
            // call below adds free space and overrides nothing that is already known.
            long? total = NxJson.Int64(s, "totalSpaceB") ?? NxJson.Int64(s, "totalSpace")
                ?? (NxJson.Prop(s, "parameters") is { } prm ? Positive(NxJson.Int64(prm, "space")) : null);
            long? free = NxJson.Int64(s, "freeSpaceB") ?? NxJson.Int64(s, "freeSpace");
            bool? online = NxJson.Bool(s, "isOnline");
            string flags = NxJson.Str(s, "status") ?? "";

            if ((id.Length > 0 && sizes.TryGetValue(id, out var sp)) || sizes.TryGetValue(path, out sp))
            {
                total ??= sp.TotalBytes;
                free ??= sp.FreeBytes;
                online ??= sp.Online;
                if (reserved == 0 && sp.ReservedBytes is long r)
                    reserved = r;
                if (type.Length == 0)
                    type = sp.Type;
                if (NxJson.Bool(s, "isUsedForWriting") is null && sp.UsedForWriting is bool u)
                    usedForWriting = u;
            }

            string status = MapStorageStatus(flags, online, total);
            long usable = Math.Max(0, (total ?? 0) - reserved);
            long usableFree = Math.Max(0, (free ?? 0) - reserved);
            string role = backup ? "backup" : usedForWriting ? "main" : "not used for writing";
            if (reserved > 0)
                role += $", {reserved / 1_000_000_000.0:F0} GB reserved";
            if (total is null)
                role += ", size unknown";

            hdds.Add(new HddInfo(
                Id: index,
                Name: path,
                HddType: type,
                Status: status,
                Property: role,
                CapacityMB: usable / 1_000_000,
                FreeSpaceMB: usableFree / 1_000_000,
                SerialNumber: "",
                Model: "",
                RecordsFootage: usedForWriting && !backup));
        }
        return new StorageInfo(hdds, WorkMode: null, MaxSupportedHdds: null);
    }

    /// <summary>
    /// <c>/api/storageSpace</c>: <c>reply.storages[]</c> with <c>storageId</c>, <c>url</c>,
    /// <c>totalSpace</c>, <c>freeSpace</c>, <c>reservedSpace</c>, <c>isOnline</c>,
    /// <c>isUsedForWriting</c>, <c>storageType</c> — the byte counts as strings. Keyed by id
    /// and by path so either side of the join works.
    /// </summary>
    internal static Dictionary<string, NxStorageSpace> ParseStorageSpace(JsonElement? root)
    {
        var result = new Dictionary<string, NxStorageSpace>(StringComparer.OrdinalIgnoreCase);
        if (root is not { } r)
            return result;
        var element = r;
        if (element.ValueKind == JsonValueKind.Object)
            element = NxJson.Prop(element, "reply") ?? element;
        if (element.ValueKind == JsonValueKind.Object)
            element = NxJson.Prop(element, "storages") ?? default;
        if (element.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var s in element.EnumerateArray())
        {
            var entry = new NxStorageSpace(
                NxJson.Int64(s, "totalSpace") ?? NxJson.Int64(s, "totalSpaceB"),
                NxJson.Int64(s, "freeSpace") ?? NxJson.Int64(s, "freeSpaceB"),
                NxJson.Int64(s, "reservedSpace") ?? NxJson.Int64(s, "spaceLimitB"),
                NxJson.Bool(s, "isOnline"),
                NxJson.Bool(s, "isUsedForWriting"),
                NxJson.Str(s, "storageType") ?? "");
            string id = NxJson.StripBraces(NxJson.Str(s, "storageId") ?? NxJson.Str(s, "id"));
            if (id.Length > 0)
                result[id] = entry;
            string url = NxJson.Str(s, "url") ?? NxJson.Str(s, "path") ?? "";
            if (url.Length > 0)
                result.TryAdd(url, entry);
        }
        return result;
    }

    private static long? Positive(long? value) => value is > 0 ? value : null;

    /// <summary>"ok" for an online volume; Nx's own runtime flags otherwise, in words the disk table can show.</summary>
    internal static string MapStorageStatus(string flags, bool? online, long? totalBytes)
    {
        string f = flags.ToLowerInvariant();
        if (online == false)
            return "offline";
        if (f.Contains("beingchecked", StringComparison.Ordinal))
            return "checking";
        if (f.Contains("beingrebuild", StringComparison.Ordinal))
            return "rebuilding";
        if (f.Contains("disabled", StringComparison.Ordinal))
            return "disabled";
        if (f.Contains("toosmall", StringComparison.Ordinal))
            return "too small";
        if (online == true || f.Contains("online", StringComparison.Ordinal) || (totalBytes ?? 0) > 0)
            return "ok";
        return f.Length > 0 ? f : "unknown";
    }

    public async Task<IReadOnlyList<CameraStream>> GetMainStreamsAsync(
        CancellationToken ct = default)
    {
        // Always a fresh read: schedules change, and this is also the list the write path
        // resolves channel numbers against.
        var cameras = await LoadCamerasAsync(ct);
        return cameras.Select((c, i) => ToCameraStream(c, i + 1)).ToList();
    }

    /// <summary>
    /// A camera's schedule as one worst-case stream. Nx schedules per hour per weekday; the
    /// cell that asks for the most is the cap, because over a whole day the disks pay for the
    /// busiest hour. Mode reads as the Nx quality word (KBPS for an explicit preset); the
    /// quality level 0–4 rides in FixedQuality for quality cells.
    /// </summary>
    internal static CameraStream ToCameraStream(NxCamera c, int channel)
    {
        var recording = c.RecordingTasks;
        bool enabled = c.ScheduleEnabled && recording.Count > 0;

        NxScheduleTask? worst = null;
        int worstKbps = 0;
        foreach (var task in recording)
        {
            int kbps = TaskKbps(task, c.Primary);
            if (worst is null || kbps > worstKbps)
            {
                worst = task;
                worstKbps = kbps;
            }
        }

        double? fps = recording.Count > 0 ? recording.Max(t => t.Fps) : null;
        if (fps is <= 0)
            fps = null;

        int? primaryKbps = c.DontRecordPrimary || worst is null || worstKbps <= 0 ? null : worstKbps;
        int? secondaryKbps = worst is null ? null : SecondaryKbps(c, fps ?? 0);

        return new CameraStream(
            Channel: channel,
            TrackId: channel,
            Enabled: enabled,
            CodecType: c.Primary?.Codec ?? "",
            Width: c.Primary?.Width ?? 0,
            Height: c.Primary?.Height ?? 0,
            FrameRateFps: fps,
            QualityControlType: worst is null ? "" : ModeLabel(worst.StreamQuality),
            VbrUpperCapKbps: primaryKbps,
            ConstantBitrateKbps: null,
            FixedQuality: worst is null || worst.IsPreset ? null : NxBitrate.QualityLevel(worst.StreamQuality),
            FrameRateIsFull: false,
            SecondaryRecordedKbps: secondaryKbps,
            ArchiveCapDays: c.MaxArchiveDays);
    }

    /// <summary>What one schedule cell costs: its preset, or Nx's rate for its quality at the primary stream's resolution.</summary>
    internal static int TaskKbps(NxScheduleTask task, NxMediaStream? primary)
    {
        if (task.IsPreset)
            return Math.Max(0, task.BitrateKbps);
        if (NxBitrate.QualityLevel(task.StreamQuality) is not int quality || primary is null)
            return 0;
        return NxBitrate.SuggestKbps(quality, primary.Width, primary.Height, task.Fps, primary.Codec);
    }

    /// <summary>
    /// The secondary stream Nx archives alongside the primary: asked for at "low" quality at
    /// the secondary's own resolution, at the schedule's frame rate. Null when the camera has
    /// no second stream, dual streaming is off, or recording it is switched off — or when its
    /// resolution is unknown, because there is no honest number without one.
    /// </summary>
    internal static int? SecondaryKbps(NxCamera c, double fps)
    {
        if (c.DontRecordSecondary || c.DualStreamingDisabled || c.Secondary is null)
            return null;
        int kbps = NxBitrate.SuggestKbps(1, c.Secondary.Width, c.Secondary.Height, fps, c.Secondary.Codec);
        return kbps > 0 ? kbps : null;
    }

    /// <summary>Four-letter mode column: MIN LOW NORM HIGH BEST for the qualities, KBPS for a preset.</summary>
    internal static string ModeLabel(string normalizedQuality) => normalizedQuality switch
    {
        "lowest" => "MIN",
        "low" => "LOW",
        "normal" => "NORM",
        "high" => "HIGH",
        "highest" => "BEST",
        "preset" => "KBPS",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>Chunks closer than this merge into one period in the coarse pass.</summary>
    private const long CoarseDetailMs = 3_600_000;

    public async Task<DateTime?> FindOldestRecordingAsync(int channel,
        CancellationToken ct = default)
    {
        var camera = await ResolveAsync(channel, ct);

        // Two passes. The exact list (detailLevelMs=1) is one object per continuous run,
        // which on a motion-recorded camera is ~150 KB per camera — 10 MB for a 64-camera site
        // through the cloud relay. So first a coarse pass merging anything closer than an
        // hour (a few hundred bytes; verified live to keep the same first start), then an
        // exact pass only over what lies before it, because a coarse detail level also drops
        // chunks shorter than itself and a lone old clip is exactly what must not be lost.
        // Note `limit=1` is not the answer: on 6.1 it returns an empty list.
        var coarse = await GetFootageAsync(camera, 0, EverythingEndMs, CoarseDetailMs, ct);
        if (coarse.Count == 0)
        {
            var everything = await GetFootageAsync(camera, 0, EverythingEndMs, detailLevelMs: 1, ct);
            return everything.Count == 0 ? null : FromUnixMs(everything.Min(p => p.StartMs));
        }
        long oldest = coarse.Min(p => p.StartMs);
        var before = await GetFootageAsync(camera, 0, oldest, detailLevelMs: 1, ct);
        if (before.Count > 0)
            oldest = Math.Min(oldest, before.Min(p => p.StartMs));
        return FromUnixMs(oldest);
    }

    public async Task<BitrateRange?> GetBitrateRangeAsync(int channel, CancellationToken ct = default)
    {
        // Nx's own bounds for this camera's schedule bitrate (mediaCapabilities, verified live:
        // 192–10666 kbps on a 2560×1440 unit). A camera Nx has not probed yet gets the widest
        // range the schedule accepts; the read-back reports what the schedule then holds.
        var camera = await ResolveAsync(channel, ct);
        if (camera.PrimaryMinKbps is int min && camera.PrimaryMaxKbps is int max && max >= min)
            return new BitrateRange(min, max);
        return new BitrateRange(NxBitrate.MinKbps, NxBitrate.MaxKbps);
    }

    public async Task<int> SetMaxBitrateAsync(int channel, int kbps, CancellationToken ct = default)
    {
        if (kbps <= 0)
            throw new ArgumentOutOfRangeException(nameof(kbps));

        var camera = await ResolveAsync(channel, ct);
        GuardCameraListUnchanged(channel, camera);

        // A schedule bitrate only reaches the camera when the site may push settings and the
        // camera has not opted out. Writing it anyway would report success for a change the
        // camera never sees, so both are checked first and the write is refused with the
        // reason — nothing half-done, nothing silently ignored.
        if (camera.KeepCameraProfile)
            throw new NvrException(
                $"channel {channel} ({camera.Name}): \"Keep camera stream and profile settings\" " +
                "is on for this camera, so Nx would store the bitrate in its schedule and never " +
                "send it to the camera. Nothing was written — clear that Expert setting first.");
        if (await ReadCameraOptimizationAsync(ct) == false)
            throw new NvrException(
                "the site setting \"Allow Site to optimize device settings\" " +
                "(cameraSettingsOptimization) is off, so Nx would store the bitrate in the " +
                "schedule and never send it to any camera. Nothing was written — enable it in " +
                "Site Administration first.");

        // Read-modify-write of the device's own schedule document so every task field Nx knows
        // about survives, then PATCH the whole schedule back. Every recording cell gets the
        // preset quality and this bitrate: a cell left at "high" would keep recording at Nx's
        // computed rate, and the retention math would be wrong for that hour.
        string path = $"{DevicesPath}/{camera.Id}";
        var device = JsonNode.Parse(await GetTextAsync(path, ct)) as JsonObject
            ?? throw new NvrException($"channel {channel}: GET {path} did not return a device object");
        var schedule = device["schedule"] as JsonObject
            ?? throw new NvrException($"channel {channel}: GET {path} returned no schedule");
        var tasks = schedule["tasks"] as JsonArray
            ?? throw new NvrException($"channel {channel}: the schedule has no tasks array");

        int touched = 0;
        foreach (var node in tasks)
        {
            if (node is not JsonObject task)
                continue;
            string type = NxScheduleTask.NormalizeRecordingType(
                task["recordingType"] is JsonValue v && v.TryGetValue(out string? raw) ? raw : null);
            if (type == "never")
                continue;
            task["streamQuality"] = "preset";
            task["bitrateKbps"] = kbps;
            touched++;
        }
        if (touched == 0)
            throw new NvrException(
                $"channel {channel} ({camera.Name}): the schedule has no recording cells to set " +
                "a bitrate on — the camera is not scheduled to record.");

        var body = new JsonObject { ["schedule"] = schedule.DeepClone() };
        await PatchJsonAsync(path, body, ct);

        // Read back what the schedule now holds. Nx does not report what the camera did with
        // it; the number here is the one every later read of this system will show.
        using var verify = await GetJsonAsync(path, ct);
        var after = NxCamera.Parse(verify.RootElement)
            ?? throw new NvrException($"channel {channel}: the read-back was not a device object");
        int readBack = after.RecordingTasks.Where(t => t.IsPreset).Select(t => t.BitrateKbps)
            .DefaultIfEmpty(0).Max();
        return readBack > 0
            ? readBack
            : throw new NvrException(
                $"channel {channel}: the write was accepted but the read-back shows no preset " +
                "bitrate — treat the camera's setting as unknown");
    }

    /// <summary>
    /// The desktop app reads with one client and writes with a fresh one. Channel numbers are
    /// list positions, so a camera added or renamed in between would shift them and the write
    /// would land on a different camera than the one previewed — refused when the list this
    /// process last saw for the address disagrees about which camera the number means.
    /// </summary>
    private void GuardCameraListUnchanged(int channel, NxCamera camera)
    {
        if (_camerasBefore is not { Count: > 0 } before)
            return;
        string? wasName = channel <= before.Count ? before[channel - 1].Name : null;
        string? wasId = channel <= before.Count ? before[channel - 1].Id : null;
        if (wasId != camera.Id)
            throw new NvrException(
                $"channel {channel}: the camera list changed since it was read (channel " +
                $"{channel} was {(wasName is null ? "beyond the end of the list" : $"'{wasName}'")}, " +
                $"now '{camera.Name}'). Nothing was written — reload and preview again.");
    }

    /// <summary>
    /// The site setting that lets Nx push schedule quality/bitrate to cameras. v3 lists the
    /// settings as one object; v4 moved them. Unreadable (a non-admin gets 403) means unknown,
    /// and unknown does not block a write — the PATCH itself refuses a user who cannot write.
    /// </summary>
    private async Task<bool?> ReadCameraOptimizationAsync(CancellationToken ct)
    {
        foreach (string path in new[] { "/rest/v3/system/settings", "/rest/v4/site/settings" })
        {
            try
            {
                using var doc = await GetJsonAsync(path, ct);
                if (ReadCameraOptimization(doc.RootElement) is bool value)
                    return value;
            }
            catch (NvrException ex) when (ex.StatusCode is 400 or 403 or 404)
            {
            }
        }
        return null;
    }

    internal static bool? ReadCameraOptimization(JsonElement settings)
    {
        var root = NxJson.Prop(settings, "reply") ?? settings;
        if (NxJson.Bool(root, "cameraSettingsOptimization") is bool direct)
            return direct;
        // Some builds describe each setting as an object with its value inside.
        if (NxJson.Prop(root, "cameraSettingsOptimization") is { ValueKind: JsonValueKind.Object } wrapped)
            return NxJson.Bool(wrapped, "value");
        return null;
    }
}
