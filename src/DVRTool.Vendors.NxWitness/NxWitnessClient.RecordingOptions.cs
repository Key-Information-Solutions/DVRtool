using System.Text.Json;
using System.Text.Json.Nodes;
using DVRTool.Core;

namespace DVRTool.Vendors.NxWitness;

/// <summary>
/// The <see cref="IRecordingOptionsClient"/> face of the Nx client: the per-camera switches
/// that decide which tracks reach the disk — "do not record the secondary stream", and the
/// two separate audio switches.
/// </summary>
/// <remarks>
/// <para>
/// See <c>docs/nx-witness-storage.md</c> §"Two settings bags" first. Three switches, in two
/// different places, behaving differently:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Secondary stream</b> — <c>dontRecordSecondaryStream</c>, a resource *property*, so a
/// camera recording both streams (the default) has no such key at all. Note this is **not**
/// <c>isDualStreamingDisabled</c>: that stops the server pulling the second stream, which on a
/// site whose cameras run <c>parameters.motionStream = "secondary"</c> would take motion
/// detection with it. Not *recording* the stream leaves it pulled and analysed.
/// </description></item>
/// <item><description>
/// <b>Audio capture</b> — <c>options.isAudioEnabled</c>, a typed bool: DW's General tab
/// "Enable audio". Off unless somebody turned it on.
/// </description></item>
/// <item><description>
/// <b>Audio archive bar</b> — <c>dontRecordAudio</c>, another property (DW's Expert tab "Do
/// not record audio"), absent by default. Independent of capture, and the *durable* one: it
/// keeps audio off the disk even if capture is later enabled. Confirmed 2026-09-08 by writing
/// the checkbox from the DW client on SiteD-Cashier and diffing the device document.
/// </description></item>
/// </list>
/// <para>
/// Both properties are written the way the DW client writes them — the strings <c>"1"</c> and
/// <c>"0"</c>, which is what appeared in the bag when the console set the checkbox. Which bag
/// a write has to land in is not documented, so the first write discovers it and the answer is
/// remembered for the life of the client. Everything goes through the same
/// read-modify-write-verify shape as <see cref="SetMaxBitrateAsync"/>: PATCH, read back, and
/// report what the server kept rather than what it was asked for. A recorder that accepts a
/// property it then ignores is the failure this guards against, and only the read-back sees it.
/// </para>
/// </remarks>
public sealed partial class NxWitnessClient : IRecordingOptionsClient
{
    /// <summary>
    /// The bag the "don't record" properties have been observed to stick in on *this* server,
    /// remembered so a batch of 60 cameras pays the discovery cost once. Null until a write has
    /// proven one.
    /// </summary>
    private string? _propertyBag;

    /// <summary>How the DW client spells a property-bag boolean.</summary>
    private static string PropertyFlag(bool value) => value ? "1" : "0";

    public async Task<IReadOnlyList<CameraRecordingOptions>> GetRecordingOptionsAsync(
        CancellationToken ct = default)
    {
        var cameras = await LoadCamerasAsync(ct);
        var rows = new List<CameraRecordingOptions>(cameras.Count);
        for (int i = 0; i < cameras.Count; i++)
            rows.Add(Describe(i + 1, cameras[i]));
        return rows;
    }

    /// <summary>
    /// One camera's switches in Core's terms. A camera with no second stream — or one whose
    /// dual streaming is disabled outright — reports <c>RecordSecondary = null</c>: there is
    /// no second stream to archive, which is a different fact from having been told not to
    /// archive one, and the two must not print the same.
    /// </summary>
    private static CameraRecordingOptions Describe(int channel, NxCamera camera)
    {
        bool available = camera.Secondary is not null && !camera.DualStreamingDisabled;
        return new CameraRecordingOptions(
            channel,
            camera.Name,
            RecordSecondary: available ? !camera.DontRecordSecondary : null,
            SecondaryAvailable: available,
            AudioEnabled: camera.AudioEnabled,
            AudioSupported: camera.AudioSupported,
            AudioRecordingBlocked: camera.DontRecordAudio);
    }

    public async Task<RecordingOptionChange> SetRecordingOptionsAsync(
        int channel, bool? recordSecondary, bool? audioEnabled,
        bool? blockAudioRecording = null, CancellationToken ct = default)
    {
        if (recordSecondary is null && audioEnabled is null && blockAudioRecording is null)
            throw new ArgumentException(
                "nothing to set: every switch was left alone.", nameof(recordSecondary));

        var camera = await ResolveAsync(channel, ct);
        GuardCameraListUnchanged(channel, camera);
        var before = Describe(channel, camera);

        var notes = new List<string>();

        // Asking a camera that has no second stream not to record one is not an error and not
        // a change — say so and drop that half of the write rather than PATCHing a property
        // the server has nothing to apply it to.
        bool? wantSecondary = recordSecondary;
        if (wantSecondary is not null && !before.SecondaryAvailable)
        {
            notes.Add(camera.DualStreamingDisabled
                ? "dual streaming is disabled on this camera, so there is no secondary stream to record"
                : "this camera has no secondary stream");
            wantSecondary = null;
        }
        else if (wantSecondary == before.RecordSecondary)
        {
            notes.Add($"secondary stream was already {(wantSecondary is true ? "recorded" : "not recorded")}");
            wantSecondary = null;
        }

        bool? wantAudio = audioEnabled;
        if (wantAudio == before.AudioEnabled)
        {
            notes.Add($"audio capture was already {(wantAudio is true ? "on" : "off")}");
            wantAudio = null;
        }
        else if (wantAudio is true && !before.AudioSupported)
        {
            notes.Add("this camera reports no audio support — enabling capture will not produce a track");
        }

        bool? wantBar = blockAudioRecording;
        if (wantBar == before.AudioRecordingBlocked)
        {
            notes.Add($"\"do not record audio\" was already {(wantBar is true ? "set" : "clear")}");
            wantBar = null;
        }

        if (wantSecondary is null && wantAudio is null && wantBar is null)
            return new RecordingOptionChange(
                channel, camera.Name, before, before, Changed: false,
                Note: string.Join("; ", notes));

        string path = $"{DevicesPath}/{camera.Id}";
        var after = await ApplyAsync(path, channel, camera.Name, wantSecondary, wantAudio, wantBar, ct);
        var result = Describe(channel, after);

        // What the server kept, switch by switch. Anything asked for and not reflected is
        // reported as rejected — never smoothed over into a success.
        var missed = new List<string>();
        if (wantSecondary is bool ws && result.RecordSecondary != ws)
            missed.Add($"\"record secondary stream\" is still {Word(result.RecordSecondary)} after asking for {Word(ws)}");
        if (wantAudio is bool wa && result.AudioEnabled != wa)
            missed.Add($"audio capture is still {Word(result.AudioEnabled)} after asking for {Word(wa)}");
        if (wantBar is bool wb && result.AudioRecordingBlocked != wb)
            missed.Add($"\"do not record audio\" is still {Word(result.AudioRecordingBlocked)} after asking for {Word(wb)}");
        if (missed.Count > 0)
            notes.Add("the server accepted the write but " + string.Join(" and ", missed));

        return new RecordingOptionChange(
            channel, camera.Name, before, result,
            Changed: result != before,
            Note: string.Join("; ", notes))
        {
            Rejected = missed.Count > 0,
        };

        static string Word(bool? value) => value switch
        {
            true => "on", false => "off", null => "unset",
        };
    }

    /// <summary>
    /// PATCHes the requested switches and returns the camera as the server reports it after.
    /// </summary>
    /// <remarks>
    /// The capture switch is a typed field in <c>options</c> and goes in unconditionally. The
    /// two "don't record" properties are the awkward ones: absent on a default camera, and
    /// which bag a write sticks in varies by build. The first write on a client that needs one
    /// tries <c>parameters</c> (where the read path has seen these switches live, and where the
    /// DW client itself puts them), verifies, and falls back to <c>options</c> — then remembers
    /// the answer, so only the first camera in a batch pays for two round trips.
    /// </remarks>
    private async Task<NxCamera> ApplyAsync(
        string path, int channel, string name,
        bool? recordSecondary, bool? audioEnabled, bool? blockAudioRecording,
        CancellationToken ct)
    {
        bool needsBag = recordSecondary is not null || blockAudioRecording is not null;
        var bags = !needsBag
            ? [null]
            : _propertyBag is { } known
                ? new string?[] { known }
                : ["parameters", "options"];

        NxCamera? last = null;
        foreach (string? bag in bags)
        {
            var body = new JsonObject();
            if (audioEnabled is bool audio)
                body["options"] = new JsonObject { ["isAudioEnabled"] = audio };
            if (bag is not null)
            {
                var target = body[bag] as JsonObject ?? new JsonObject();
                // Both properties are the *negative*: "do not record". In the typed bag a real
                // bool is expected; in the property bag, the DW client's own "1"/"0".
                if (recordSecondary is bool secondary)
                    target["dontRecordSecondaryStream"] = bag == "parameters"
                        ? PropertyFlag(!secondary)
                        : !secondary;
                if (blockAudioRecording is bool bar)
                    target["dontRecordAudio"] = bag == "parameters" ? PropertyFlag(bar) : bar;
                body[bag] = target;
            }

            await PatchJsonAsync(path, body, ct);

            using var verify = await GetJsonAsync(path, ct);
            last = NxCamera.Parse(verify.RootElement)
                ?? throw new NvrException(
                    $"channel {channel} ({name}): the read-back was not a device object");

            if (bag is null)
                return last;

            // The bag is proven only if every property asked for actually landed in it.
            bool stuck =
                (recordSecondary is not bool want || !last.DontRecordSecondary == want) &&
                (blockAudioRecording is not bool bar2 || last.DontRecordAudio == bar2);
            if (stuck)
            {
                _propertyBag = bag;
                return last;
            }
        }

        // Every bag tried and a property never stuck. The caller reports this as a rejected
        // write; returning the camera as it really is keeps that report honest.
        return last ?? throw new NvrException(
            $"channel {channel} ({name}): the device could not be read back after the write");
    }
}
