namespace DVRTool.Core;

/// <summary>
/// The per-camera recording switches that decide *what tracks* reach the disk, as opposed to
/// the schedule (when) and the bitrate (how much) that <see cref="IStorageClient"/> covers.
/// </summary>
/// <param name="Channel">1-based display channel, as everywhere else in Core.</param>
/// <param name="RecordSecondary">
/// Whether the recorder archives the camera's second (low-quality) stream alongside the
/// primary. Null when the recorder does not archive a second stream at all — the appliance
/// vendors, and an Nx camera with no second stream — which is not the same as "false", a
/// camera that *could* record one and has been told not to.
/// </param>
/// <param name="SecondaryAvailable">
/// Whether the camera has a second stream to record in the first place. False makes
/// <paramref name="RecordSecondary"/> moot, and a write that turns it off a no-op.
/// </param>
/// <param name="AudioEnabled">
/// Whether the recorder captures the camera's audio at all — DW Spectrum's General-tab
/// "Enable audio". Null when the recorder does not expose the switch.
/// </param>
/// <param name="AudioRecordingBlocked">
/// Whether audio is barred from the archive independently of capture — DW Spectrum's
/// Expert-tab "Do not record audio". This is a *second*, separate switch: it keeps audio off
/// the disk even if somebody later turns capture on, which is what makes it the durable
/// setting rather than <paramref name="AudioEnabled"/> being off today. Null when the recorder
/// has no such switch.
/// </param>
/// <param name="AudioSupported">
/// Whether the camera offers audio at all. Turning audio off on a camera that has none is
/// harmless but pointless, and worth reporting as such rather than as a change.
/// </param>
public sealed record CameraRecordingOptions(
    int Channel,
    string Name,
    bool? RecordSecondary,
    bool SecondaryAvailable,
    bool? AudioEnabled,
    bool AudioSupported,
    bool? AudioRecordingBlocked = null)
{
    /// <summary>
    /// Whether audio can reach the disk at all: capture on and not barred. Either switch alone
    /// is enough to keep it off, which is why "is audio recorded" is not a single field.
    /// </summary>
    public bool? AudioReachesDisk => (AudioEnabled, AudioRecordingBlocked) switch
    {
        (null, _) => null,
        (false, _) => false,
        (true, true) => false,
        (true, _) => true,
    };

    /// <summary>
    /// A short human summary of what this camera writes: the two switches in the words the
    /// recorder's own UI uses, so an operator can match a row against the console.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(2);
            if (RecordSecondary is bool secondary)
                parts.Add(secondary ? "records secondary" : "no secondary");
            else if (!SecondaryAvailable)
                parts.Add("no second stream");
            // The two audio switches are reported as one phrase, because what an operator
            // needs to know is whether audio lands on the disk — but "barred" is named
            // separately, since that is the setting that survives someone re-enabling capture.
            if (AudioRecordingBlocked is true)
                parts.Add(AudioEnabled is true ? "audio barred (capture on)" : "audio barred");
            else if (AudioEnabled is bool audio)
                parts.Add(audio ? "audio on" : "audio off");
            else if (!AudioSupported)
                parts.Add("no audio");
            return parts.Count > 0 ? string.Join(", ", parts) : "—";
        }
    }
}

/// <summary>
/// What one camera's switches were asked to become, and what actually happened to it.
/// </summary>
/// <param name="Changed">
/// The recorder's read-back differs from what it held before — the only evidence that a write
/// landed. A camera already in the requested state is <c>false</c> here and carries
/// <paramref name="Note"/> saying so; it is not a failure.
/// </param>
/// <param name="Note">
/// Why nothing changed, or what the recorder did instead of what was asked. Empty on a plain
/// successful change.
/// </param>
public sealed record RecordingOptionChange(
    int Channel,
    string Name,
    CameraRecordingOptions Before,
    CameraRecordingOptions After,
    bool Changed,
    string Note = "")
{
    /// <summary>A change the recorder refused or silently dropped: asked, but not applied.</summary>
    public bool Rejected { get; init; }
}

/// <summary>
/// Opt-in capability: reading and setting the per-camera "record the second stream" and
/// "record audio" switches. Implemented separately from <see cref="INvrClient"/> — like
/// <see cref="IStorageClient"/> — because only the recorders that actually have these
/// switches expose it. Nx Witness / DW Spectrum does; the Hikvision and Dahua modules do not,
/// since their sub-stream is a live/remote-view stream that is never archived and their audio
/// switch lives in the per-channel encoder config the bitrate path already owns.
/// </summary>
public interface IRecordingOptionsClient
{
    /// <summary>Both switches for every camera the recorder knows, ordered by channel.</summary>
    Task<IReadOnlyList<CameraRecordingOptions>> GetRecordingOptionsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Sets either switch on one channel and reports what the recorder holds afterwards.
    /// A null argument leaves that switch alone, so the two can be written independently or
    /// together in one round trip.
    /// </summary>
    /// <remarks>
    /// Implementations read the camera, write only when the requested state differs, then read
    /// back — a recorder that accepts a property it does not honour is the failure mode worth
    /// catching, and only the read-back catches it.
    /// </remarks>
    /// <param name="audioEnabled">The capture switch ("Enable audio"), or null to leave it.</param>
    /// <param name="blockAudioRecording">
    /// The archive bar ("Do not record audio"), or null to leave it. Independent of
    /// <paramref name="audioEnabled"/>: both can be written in the same call.
    /// </param>
    Task<RecordingOptionChange> SetRecordingOptionsAsync(
        int channel, bool? recordSecondary, bool? audioEnabled,
        bool? blockAudioRecording = null, CancellationToken ct = default);
}
