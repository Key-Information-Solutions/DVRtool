using System.Globalization;
using System.Text;
using DVRTool.Cli;
using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using DVRTool.Vendors.Hikvision;
using DVRTool.Vendors.HikvisionSdk;

const string Usage = """
    dvrtool — multi-vendor NVR footage tool (Key Information Solutions)

    Usage:
      dvrtool <command> [options]

    Commands:
      info            Device model / serial / firmware
      test            Probe the web, RTSP and SDK ports and say what each one costs
      channels        List channels
      users           List the accounts configured on the device
      access          Door-access panels (see: dvrtool access --help)
      search          List recordings for a channel in a window
      download        Export footage for a time span to a file
      live            Record live video over the SDK port (Hikvision) — the transport
                      that works when RTSP is closed
      live-url        Print the RTSP live URI (paste into VLC)
      playback-url    Print the RTSP playback-by-time URI

    Options:
      --vendor <hikvision|dahua>   default: hikvision
      --host <ip[:port]>           or DVR_HOST (HTTP port; default 80, 443 with --tls —
                                   many NVRs serve HTTPS on 8443: --host 10.0.0.5:8443)
      --user <name>                or DVR_USER
      --pass <password>            or DVR_PASS; omit both to be prompted (avoid the
                                   flag: it persists in shell history and audit logs)
      --rtsp-port <n>              default: 554
      --sdk-port <n>               vendor SDK port, or DVR_SDK_PORT (default 8000 on
                                   Hikvision, 37777 on Dahua — changeable from the
                                   recorder, so don't assume it)
      --tls                        HTTPS to the NVR (self-signed cert pinned on first use)
      --expect-serial <s>          refuse to act unless the device answers with this serial
      --trust-new-device           accept a device whose serial differs from the pinned
                                   one, and re-pin it (a replaced recorder — not a
                                   mistyped port)
      --channel <n>                1-based display channel
      --start / --end              "yyyy-MM-dd HH:mm[:ss]" (NVR-local time)
      --stream <main|sub>          default: main
      --seconds <n>                how long `live` records (default 10, max 3600)
      --sdk-dir <path>             folder holding HCNetSDK.dll, or DVR_SDK_DIR — only
                                   `live` needs it (default: the iVMS-4200 /
                                   HikCentral install)
      --out <file>                 download target (default: auto-named after the
                                   container the device actually sent)
      --remux [mp4|mkv]            stream-copy the download into a real container via
                                   ffmpeg (default mp4). Hikvision exports are MPEG
                                   program streams whatever they are named, and Dahua
                                   sends .dav — most players reject both.
      --force                      replace an existing file at the download target
                                   (without it, an export that would overwrite one is
                                   refused — before the download starts, and again
                                   before a remux is moved onto the target)
      --with-creds                 embed user:pass into printed RTSP URIs
      --env <path>                 .env file to load (default: .env in the working dir)

    Identity: the first successful login to a host:port records the device's serial,
    and every later one must match it. Sites routinely put several systems behind one
    IP on different forwarded ports, and a fleet sharing one account authenticates
    just as happily against the wrong one — so a mistyped or re-forwarded port is
    caught here rather than in an export that holds the wrong site's footage. Pins
    live in %APPDATA%\DVRTool\identities.json.

    `live` does not use the web or RTSP port at all: Hikvision's private protocol
    carries login and media together over the SDK port, which is how iVMS-4200 shows
    video on sites where only 8000 is forwarded. Hikvision/OEM only, Windows x64 only,
    and it needs HCNetSDK.dll — see docs/hikvision-sdk-live.md.

    Door-access panels are a separate device class with their own credentials
    (OCB_PANELS / OCB_USER / OCB_PASS) — run `dvrtool access --help`.

    Credentials can live in a .env file (DVR_HOST / DVR_USER / DVR_PASS). The
    password is stored there in PLAINTEXT — never keep .env inside footage/export
    folders that get zipped up and shared.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

string command = args[0].ToLowerInvariant();

// `access` is a command group: `dvrtool access <subcommand> [options]`, so its
// subcommand must be pulled off before the rest is parsed as options.
bool isAccess = command == "access";
string accessSubcommand = isAccess && args.Length > 1 &&
        !args[1].StartsWith("--", StringComparison.Ordinal)
    ? args[1].ToLowerInvariant()
    : "";

Dictionary<string, string> opts;
try
{
    opts = ParseOptions(args.Skip(isAccess && accessSubcommand.Length > 0 ? 2 : 1).ToArray());
    LoadDotEnv(opts.GetValueOrDefault("env"));
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Access panels are not NVRs: different protocol, port and credentials, so they
    // are dispatched before any INvrClient is built.
    if (isAccess)
        return await AccessCommands.RunAsync(accessSubcommand, opts, cts.Token);

    // `test` probes ports instead of driving a client, and ConnectivityProbe disposes every
    // client its factory hands it — so it gets the connection plus a factory rather than the
    // single shared client below, which the other commands own for the whole call.
    if (command == "test")
    {
        var (testConn, testVendor) = BuildConnection(opts);
        return await RunTestAsync(testConn, testVendor, opts.GetValueOrDefault("expect-serial"),
            cts.Token);
    }

    // `live` speaks the SDK protocol, not HTTP, and verifies identity from the SDK
    // login itself — so it builds no INvrClient and skips VerifyIdentityAsync, whose
    // check it performs for itself against the same pin.
    if (command == "live")
    {
        var (liveConn, liveVendor) = BuildConnection(opts);
        return await RunLiveAsync(liveConn, liveVendor, opts, cts.Token);
    }

    // URL-only commands don't authenticate; only demand a password when it is
    // actually used (so `dvrtool live-url` never blocks on a prompt).
    bool needsPassword = command is not ("live-url" or "playback-url")
        || opts.ContainsKey("with-creds");
    using INvrClient client = BuildClient(opts, needsPassword);

    // Before anything acts on the device. Authentication only proves the credentials are
    // good *somewhere*; on a fleet with one shared account, a wrong port logs in cleanly and
    // every command downstream reports another system's channels, footage and accounts as if
    // they were this one's.
    await VerifyIdentityAsync(client, command, opts, cts.Token);

    switch (command)
    {
        case "info":
        {
            var info = await client.GetDeviceInfoAsync(cts.Token);
            Console.WriteLine($"Vendor:    {client.Vendor}");
            Console.WriteLine($"Name:      {info.Name}");
            Console.WriteLine($"Model:     {info.Model}");
            Console.WriteLine($"Serial:    {info.SerialNumber}");
            Console.WriteLine($"Firmware:  {info.FirmwareVersion}");
            return 0;
        }
        case "channels":
        {
            var channels = await client.GetChannelsAsync(cts.Token);
            if (channels.Count == 0)
            {
                Console.WriteLine("No channels reported.");
                return 0;
            }
            foreach (var ch in channels)
            {
                string online = ch.Online switch
                {
                    true => "online",
                    false => "OFFLINE",
                    null => "",
                };
                Console.WriteLine($"{ch.Id,4}  {ch.Name,-32} {online}");
            }
            return 0;
        }
        case "users":
        {
            if (client is not IUserManagementClient userClient)
            {
                Console.Error.WriteLine(
                    $"error: user management isn't implemented for {client.Vendor} devices.");
                return 2;
            }
            var users = await userClient.GetUsersAsync(cts.Token);
            if (users.Count == 0)
            {
                Console.WriteLine("No users reported.");
                return 0;
            }
            Console.WriteLine(
                $"{"ID",-6}  {"NAME",-20}  {"LEVEL",-14}  {"ROLE",-9}  {"RESERVED",-8}  MEMO");
            foreach (var u in users)
                Console.WriteLine(
                    $"{u.Id,-6}  {u.Name,-20}  {u.NativeLevel,-14}  {u.Role,-9}  " +
                    $"{(u.Reserved ? "yes" : ""),-8}  {u.Memo}");
            return 0;
        }
        case "search":
        {
            int channel = RequireChannel(opts);
            var (start, end) = RequireWindow(opts);
            var segments = await client.SearchAsync(channel, start, end, cts.Token);
            if (segments.Count == 0)
            {
                Console.WriteLine("No recordings found in that window.");
                return 0;
            }
            Console.WriteLine($"{"#",4}  {"Start",-19}  {"End",-19}  {"Duration",-10}  {"Type",-10}  Size");
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                string size = s.SizeBytes is long b ? $"{b / 1048576.0:F1} MB" : "";
                Console.WriteLine(
                    $"{i + 1,4}  {s.Start:yyyy-MM-dd HH:mm:ss}  {s.End:yyyy-MM-dd HH:mm:ss}  " +
                    $"{FormatDuration(s.Duration),-10}  {s.Type,-10}  {size}");
            }
            TimeSpan total = TimeSpan.FromSeconds(segments.Sum(s => s.Duration.TotalSeconds));
            Console.WriteLine($"\n{segments.Count} segment(s), {FormatDuration(total)} of footage.");
            return 0;
        }
        case "download":
        {
            int channel = RequireChannel(opts);
            var (start, end) = RequireWindow(opts);
            bool force = opts.ContainsKey("force");
            string? requestedOut = opts.GetValueOrDefault("out") is { Length: > 0 } o ? o : null;
            RemuxContainer? container = opts.TryGetValue("remux", out var remuxValue)
                ? DownloadPaths.ResolveContainer(remuxValue, requestedOut)
                : null;
            var plan = DownloadPaths.Plan(requestedOut,
                $"ch{channel}_{start:yyyyMMdd_HHmmss}-{end:HHmmss}", container);

            // Before a download that can run for minutes, not after it.
            DownloadPaths.EnsureNotOverwriting(plan.FinalPath, force,
                DownloadPaths.CliOverwriteAdvice);

            string outDir = Path.GetDirectoryName(Path.GetFullPath(plan.FinalPath))!;
            if (File.Exists(Path.Combine(outDir, ".env")))
                Console.Error.WriteLine(
                    "warning: a .env (plaintext credentials) sits in the export directory — " +
                    "don't zip it up with the footage.");

            Console.WriteLine($"Downloading ch{channel} {start:yyyy-MM-dd HH:mm:ss} → {end:HH:mm:ss} …");
            long lastShown = 0;
            var progress = new Progress<long>(total =>
            {
                if (total - lastShown < 2_000_000)
                    return;
                lastShown = total;
                Console.Write($"\r  {total / 1048576.0:F1} MB");
            });
            await client.DownloadAsync(channel, start, end, plan.DownloadPath, progress, cts.Token);
            long size = new FileInfo(plan.DownloadPath).Length;

            if (plan.Container is RemuxContainer target)
            {
                Console.WriteLine($"\r  {size / 1048576.0:F1} MB downloaded.");
                Console.WriteLine($"Remuxing → {plan.FinalPath} …");
                var remux = await Remux.RemuxAsync(
                    plan.DownloadPath, plan.FinalPath, target, force, cts.Token);
                if (remux.RefusedOverwrite)
                {
                    // ffmpeg did its job; only the promotion was refused. Reporting that
                    // as an ffmpeg failure would send the operator after the wrong problem.
                    Console.Error.WriteLine($"error: {remux.Output}");
                    Console.Error.WriteLine(
                        "  it appeared while this export was downloading, so the remux — " +
                        "which succeeded — was discarded instead of replacing it.");
                    Console.Error.WriteLine(
                        $"  the raw download is kept at {plan.DownloadPath} — VLC plays it as-is.");
                    Console.Error.WriteLine($"  {DownloadPaths.CliOverwriteAdvice}");
                    return 2;
                }
                if (!remux.Success)
                {
                    Console.Error.WriteLine($"  ffmpeg failed:\n{remux.Output}");
                    Console.Error.WriteLine(
                        $"  the raw download is kept at {plan.DownloadPath} — VLC plays it as-is.");
                    return 1;
                }
                TryDelete(plan.DownloadPath);
                long muxed = new FileInfo(plan.FinalPath).Length;
                Console.WriteLine($"  {muxed / 1048576.0:F1} MB — saved {plan.FinalPath}");
                // The operator named this file, so it is never silently renamed — but
                // shipping evidence whose extension lies about its bytes is the exact
                // defect --remux exists to fix, and it must not come from our own tool.
                if (DownloadPaths.ContainerContradictsName(requestedOut, target))
                    Console.Error.WriteLine(
                        "warning: the remux wrote " +
                        $"{ContainerSniffer.DisplayName(DownloadPaths.MediaContainerFor(target))} " +
                        $"data into a file you named {Path.GetExtension(plan.FinalPath)} — most " +
                        "players trust the extension and will refuse it. Rename it, or give " +
                        "--out a name ending .mp4 or .mkv.");
                return 0;
            }

            // No remux: name the file after what the device actually sent. Hikvision
            // exports are MPEG program streams however they are labeled — but that is
            // one firmware's behavior, so the bytes decide, not the vendor.
            var sniffed = ContainerSniffer.SniffFile(plan.DownloadPath);
            string finalPath = plan.DownloadPath;
            if (requestedOut is null)
            {
                finalPath += ContainerSniffer.ExtensionFor(sniffed) ?? ".bin";
                // The auto-name's extension comes from the bytes, so this collision is
                // the one that genuinely cannot be checked before the download.
                if (!force && File.Exists(finalPath))
                {
                    Console.Error.WriteLine($"\nerror: {DownloadPaths.OverwriteRefusalMessage(finalPath)} " +
                        DownloadPaths.CliOverwriteAdvice);
                    Console.Error.WriteLine($"  this download is kept at {plan.DownloadPath}.");
                    return 2;
                }
                File.Move(plan.DownloadPath, finalPath, overwrite: force);
            }
            Console.WriteLine($"\r  {size / 1048576.0:F1} MB — saved {finalPath}");
            if (ContainerSniffer.ExtensionContradicts(finalPath, sniffed))
                Console.Error.WriteLine(
                    $"warning: that file holds {ContainerSniffer.DisplayName(sniffed)} data, " +
                    $"not {Path.GetExtension(finalPath)} — most players will refuse it. " +
                    "Re-run with --remux to get a real container.");
            else if (sniffed is MediaContainer.MpegProgramStream or MediaContainer.Dhav)
                Console.WriteLine(
                    $"  ({ContainerSniffer.DisplayName(sniffed)} straight off the NVR; " +
                    "pass --remux for a file Windows plays by default)");
            else if (sniffed is MediaContainer.Unknown)
                Console.WriteLine("  (unrecognized container — try --remux, or open it in VLC)");
            return 0;
        }
        case "live-url":
        {
            int channel = RequireChannel(opts);
            Console.WriteLine(client.GetLiveUri(channel, ParseStream(opts),
                opts.ContainsKey("with-creds")));
            return 0;
        }
        case "playback-url":
        {
            int channel = RequireChannel(opts);
            var (start, end) = RequireWindow(opts);
            Console.WriteLine(client.GetPlaybackUri(channel, start, end, ParseStream(opts),
                opts.ContainsKey("with-creds")));
            return 0;
        }
        default:
            Console.Error.WriteLine($"error: unknown command '{command}'\n");
            Console.WriteLine(Usage);
            return 2;
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("\ncanceled.");
    return 1;
}
catch (OperationCanceledException ex)
{
    // HttpClient's 30s timeout surfaces as TaskCanceledException too — without
    // this distinction a hung/unreachable NVR would be reported as a user abort.
    Console.Error.WriteLine(ex.InnerException is TimeoutException
        ? "Timed out talking to the device (30s). Check host/port and that the NVR is reachable."
        : $"Request canceled unexpectedly: {ex.Message}");
    return 1;
}
catch (DeviceIdentityException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine(
        "  refusing to go on: every command past this point would report, export or change " +
        "the wrong system.");
    Console.Error.WriteLine(
        "  if this recorder was legitimately replaced, re-run once with --trust-new-device " +
        $"(or delete its line from {DeviceIdentityStore.Default.FilePath}).");
    return 2;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (NvrException ex)
{
    Console.Error.WriteLine($"NVR error: {ex.Message}");
    if (!string.IsNullOrWhiteSpace(ex.ResponseBody))
        Console.Error.WriteLine(ex.ResponseBody.Length > 500
            ? ex.ResponseBody[..500] + "…"
            : ex.ResponseBody);
    return 1;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Connection error: {ex.Message}" +
        (ex.InnerException is not null ? $" — {ex.InnerException.Message}" : ""));
    return 1;
}

// ----- helpers -----

/// <summary>
/// `dvrtool live` — records live video over the vendor SDK port instead of RTSP.
/// </summary>
/// <remarks>
/// <para>
/// This exists because RTSP is the port sites do not forward. Across our installed base 554
/// is reachable at a handful of recorders and the SDK port at nearly all of them, so on most
/// systems this is not the fallback transport — it is the only one that works remotely
/// without a router change. Hikvision's private protocol multiplexes the media back over the
/// same TCP session the login authenticated on, which is why one open port is enough.
/// </para>
/// <para>
/// It writes a file rather than streaming to stdout on purpose: the SDK's preview plugins
/// print their own banners to stdout as they initialize, so a piped stream would arrive with
/// "Load HCPreview.dll success!" spliced into the video.
/// </para>
/// </remarks>
static async Task<int> RunLiveAsync(NvrConnection conn, Vendor vendor,
    Dictionary<string, string> opts, CancellationToken ct)
{
    if (vendor == Vendor.Dahua)
    {
        Console.Error.WriteLine(
            "error: `live` is Hikvision-only. Dahua's equivalent is CLIENT_RealPlayEx in " +
            "dhnetsdk.dll on port 37777, which DVRTool does not link — see " +
            "docs/device-ports.md. On Dahua, use `dvrtool live-url` and RTSP.");
        return 2;
    }

    // Checked here rather than left to SdkRuntime so the message names the command, and so
    // the platform analyzer can see that everything below is Windows-only.
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine(
            "error: `live` needs Hikvision's HCNetSDK, which is Windows x64 only. Every " +
            "other command in this tool is cross-platform; this one is the exception.");
        return 2;
    }

    int channel = RequireChannel(opts);
    var stream = ParseStream(opts);

    int seconds = 10;
    if (opts.TryGetValue("seconds", out var secondsText))
    {
        if (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture,
                out seconds) || seconds is < 1 or > 3600)
            throw new ArgumentException("--seconds must be a whole number of seconds, 1-3600");
    }

    bool force = opts.ContainsKey("force");
    string? requestedOut = opts.GetValueOrDefault("out") is { Length: > 0 } o ? o : null;
    RemuxContainer? container = opts.TryGetValue("remux", out var remuxValue)
        ? DownloadPaths.ResolveContainer(remuxValue, requestedOut)
        : null;
    var plan = DownloadPaths.Plan(requestedOut,
        $"live_ch{channel}_{DateTime.Now:yyyyMMdd_HHmmss}", container);

    // Before the recording, not after it.
    DownloadPaths.EnsureNotOverwriting(plan.FinalPath, force, DownloadPaths.CliOverwriteAdvice);

    if (opts.TryGetValue("expect-serial", out var expectRaw) && expectRaw.Length == 0)
        throw new ArgumentException("--expect-serial needs the serial to expect");
    string? expect = expectRaw is { Length: > 0 } ? expectRaw : null;

    if (opts.ContainsKey("trust-new-device"))
    {
        Console.Error.WriteLine(
            "error: --trust-new-device is not accepted here. Re-pinning a replaced recorder " +
            "is a deliberate act and belongs on a command whose job is identity — run " +
            "`dvrtool info --trust-new-device` first, then `live`.");
        return 2;
    }

    string? sdkDir = opts.GetValueOrDefault("sdk-dir") is { Length: > 0 } d
        ? d
        : Environment.GetEnvironmentVariable("DVR_SDK_DIR");

    using var session = HikvisionSdkSession.Open(conn, expect,
        expect is null ? null : "--expect-serial", sdkDir,
        onIdentityChecked: check => Console.Error.WriteLine(
            (check.Verdict == IdentityVerdict.Unverifiable ? "warning: " : "note: ") +
            check.Message +
            (check.Verdict == IdentityVerdict.FirstContact
                ? " It was the SDK port that answered, not the web port this pin is named for."
                : "")));

    Console.WriteLine($"Serial:    {session.DeviceInfo.SerialNumber}");
    Console.WriteLine($"Channels:  {session.Channels.Describe()}");

    using var live = session.StartLive(channel, stream);
    Console.WriteLine(
        $"Live:      channel {live.DisplayChannel} -> device channel {live.SdkChannel}, " +
        $"{stream.ToString().ToLowerInvariant()} stream, {seconds}s");

    // The device can accept a preview request and then send nothing — an offline camera, or
    // a channel number the firmware tolerates but has no video for. That silence has to
    // become a finite, explained failure rather than a recording of zero bytes.
    if (!await live.Media.WaitForDataAsync(TimeSpan.FromSeconds(15), ct))
    {
        Console.Error.WriteLine(
            $"error: the device accepted the request but sent no video for channel {channel} " +
            "within 15s. An offline camera is the usual cause — check `dvrtool channels`.");
        return 1;
    }

    long written = await WriteLiveAsync(live.Media, plan.DownloadPath,
        TimeSpan.FromSeconds(seconds), ct);

    if (written == 0)
    {
        Console.Error.WriteLine("error: the stream ended before any video was written.");
        TryDelete(plan.DownloadPath);
        return 1;
    }

    Console.WriteLine($"  {written / 1048576.0:F1} MB received.");
    if (live.Media.BytesDropped > 0)
        Console.Error.WriteLine(
            $"warning: {live.Media.BytesDropped / 1048576.0:F1} MB was dropped because the " +
            "writer could not keep up, so the recording has gaps.");
    if (live.Media.Stalled)
        Console.Error.WriteLine(
            "warning: the stream went quiet before the time was up, so the recording is short.");

    if (plan.Container is RemuxContainer target)
    {
        Console.WriteLine($"Remuxing -> {plan.FinalPath} …");
        var remux = await Remux.RemuxAsync(plan.DownloadPath, plan.FinalPath, target, force, ct);
        if (!remux.Success)
        {
            Console.Error.WriteLine(remux.RefusedOverwrite
                ? $"error: {remux.Output}"
                : $"  ffmpeg failed:\n{remux.Output}");
            Console.Error.WriteLine(
                $"  the raw stream is kept at {plan.DownloadPath} — VLC plays it as-is.");
            return remux.RefusedOverwrite ? 2 : 1;
        }
        TryDelete(plan.DownloadPath);
        Console.WriteLine(
            $"  {new FileInfo(plan.FinalPath).Length / 1048576.0:F1} MB — saved {plan.FinalPath}");
        if (DownloadPaths.ContainerContradictsName(requestedOut, target))
            Console.Error.WriteLine(
                "warning: the remux wrote " +
                $"{ContainerSniffer.DisplayName(DownloadPaths.MediaContainerFor(target))} data " +
                $"into a file you named {Path.GetExtension(plan.FinalPath)} — most players " +
                "trust the extension and will refuse it. Rename it, or give --out a name " +
                "ending .mp4 or .mkv.");
        return 0;
    }

    // No remux: name the file after what the SDK actually delivered. The callback stream is
    // an MPEG program stream, but that is this firmware's behavior rather than a promise, so
    // the bytes decide — exactly as they do for a download.
    var sniffed = ContainerSniffer.SniffFile(plan.DownloadPath);
    string finalPath = plan.DownloadPath;
    if (requestedOut is null)
    {
        finalPath += ContainerSniffer.ExtensionFor(sniffed) ?? ".bin";
        if (!force && File.Exists(finalPath))
        {
            Console.Error.WriteLine($"error: {DownloadPaths.OverwriteRefusalMessage(finalPath)} " +
                DownloadPaths.CliOverwriteAdvice);
            Console.Error.WriteLine($"  this recording is kept at {plan.DownloadPath}.");
            return 2;
        }
        File.Move(plan.DownloadPath, finalPath, overwrite: force);
    }
    Console.WriteLine($"  saved {finalPath}");
    if (ContainerSniffer.ExtensionContradicts(finalPath, sniffed))
        Console.Error.WriteLine(
            $"warning: that file holds {ContainerSniffer.DisplayName(sniffed)} data, not " +
            $"{Path.GetExtension(finalPath)} — most players will refuse it. Re-run with " +
            "--remux to get a real container.");
    else if (sniffed is MediaContainer.MpegProgramStream)
        Console.WriteLine(
            $"  ({ContainerSniffer.DisplayName(sniffed)} straight off the SDK; pass --remux " +
            "for a file Windows plays by default)");
    else if (sniffed is MediaContainer.Unknown)
        Console.WriteLine("  (unrecognized container — try --remux, or open it in VLC)");
    return 0;
}

/// <summary>
/// Copies the live stream to disk for <paramref name="duration"/>, reporting as it goes.
/// </summary>
/// <remarks>
/// The deadline is checked between reads rather than imposed on them: a read on a healthy
/// live stream returns as fast as the video arrives, which is real time by definition. A
/// stream that goes silent is bounded by <see cref="SdkMediaStream.StallTimeout"/>, which
/// ends the read with 0 instead of hanging.
/// </remarks>
static async Task<long> WriteLiveAsync(SdkMediaStream media, string path, TimeSpan duration,
    CancellationToken ct)
{
    var deadline = DateTime.UtcNow + duration;
    long total = 0;
    long lastShown = 0;
    var buffer = new byte[64 * 1024];

    await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    while (DateTime.UtcNow < deadline)
    {
        ct.ThrowIfCancellationRequested();
        int read = media.Read(buffer, 0, buffer.Length);
        if (read == 0)
            break; // completed, or quiet for longer than the stall timeout
        await file.WriteAsync(buffer.AsMemory(0, read), ct);
        total += read;
        if (total - lastShown >= 1_000_000)
        {
            lastShown = total;
            Console.Write($"\r  {total / 1048576.0:F1} MB");
        }
    }
    if (lastShown > 0)
        Console.Write("\r");
    return total;
}

/// <summary>
/// The CLI half of the desktop app's <b>Test connection</b>: the same
/// <see cref="ConnectivityProbe"/> over the same three ports, so an installer on a phone SSH
/// session gets the answer the dialog would have given.
/// </summary>
/// <remarks>
/// Rows print in a fixed order even though the probes run concurrently — interleaved console
/// output as each lands is harder to read than a stable table, and the slowest port is a
/// 5-second timeout, not a wait worth optimising.
/// <para>
/// The exit code reports only what an export needs: the web and RTSP ports. A dead SDK port
/// still exits 0 even though <c>dvrtool live</c> now dials it on Hikvision — a script gating
/// a download on this command should not fail over a port no download touches — but the
/// consequence line no longer says nothing needs it. The one non-port verdict — the web port
/// answering as a <em>different</em> device — exits 2, since no firewall rule fixes it.
/// </para>
/// </remarks>
static async Task<int> RunTestAsync(NvrConnection conn, Vendor vendor, string? expectSerial,
    CancellationToken ct)
{
    // Padded so the three verdicts line up under each other.
    string webLabel = $"{(conn.UseTls ? "HTTPS" : "HTTP "),-5} {conn.HttpPort,-5}";
    string rtspLabel = $"{"RTSP",-5} {conn.RtspPort,-5}";
    // Named the way the vendor's own network page names it, matching the desktop dialog.
    string sdkLabel = $"{(vendor == Vendor.Dahua ? "TCP" : "SDK"),-5} {conn.SdkPort,-5}";

    var (web, rtsp, sdk) = ConnectivityProbe.StartAll(conn, () => ClientFor(conn, vendor), ct,
        expectSerial, expectSerial is { Length: > 0 } ? "--expect-serial" : null);

    var results = new List<ProbeResult>();
    foreach (var (label, probe) in new[] { (webLabel, web), (rtspLabel, rtsp), (sdkLabel, sdk) })
    {
        var result = await probe;
        results.Add(result);
        string glyph = result.Severity switch
        {
            ProbeSeverity.Pass => "ok  ",
            ProbeSeverity.Caution => "warn",
            _ => "FAIL",
        };
        Console.WriteLine($"{glyph}  {label}  {result.Detail} ({result.Elapsed.TotalSeconds:0.0}s)");
    }

    // A wrong device outranks every port verdict: all three ports can be perfect and the
    // answer still worthless. An installer told "all three ports reachable" here would go on
    // to export another site's footage.
    if (results.FirstOrDefault(r => r.Status == ProbeStatus.WrongDevice) is { } wrong)
    {
        Console.WriteLine();
        Console.WriteLine(wrong.Detail);
        Console.WriteLine(
            "The ports are open — this is a port-forward or wiring problem, not a firewall " +
            "one. If the recorder was legitimately replaced, re-run once with " +
            "--trust-new-device.");
        return 2;
    }

    // Which port fell short decides which feature breaks, so name the consequence rather
    // than leaving three lines for the reader to interpret.
    var dead = results.Where(r => r.Severity == ProbeSeverity.Fail).Select(r => r.Target).ToList();
    if (dead.Count == 0 && results.All(r => r.Severity == ProbeSeverity.Pass))
    {
        Console.WriteLine("All three ports reachable.");
        return 0;
    }

    Console.WriteLine();
    foreach (var target in dead)
        Console.WriteLine(target switch
        {
            ProbeTarget.Web => "The web port is the one nothing works without — every command needs it.",
            ProbeTarget.RtspPort when vendor == Vendor.Hikvision =>
                "RTSP is dead: RTSP live view and playback-by-time need it. Live view has a " +
                $"second route on Hikvision — `dvrtool live` streams over the SDK port " +
                $"({conn.SdkPort}) instead, so the SDK row above is the one to read next.",
            ProbeTarget.RtspPort => "RTSP is dead: live view, playback and export all need it.",
            _ when vendor == Vendor.Hikvision =>
                "The SDK port is dead. That costs SDK live view — `dvrtool live` and the " +
                "desktop app's SDK transport, the route that works when RTSP is closed — and " +
                "means iVMS-4200 / HikCentral cannot reach this recorder from here either.",
            _ => "The vendor SDK port is dead. No DVRTool feature needs it on Dahua — this " +
                 "only means SmartPSS / DSS cannot reach this recorder from here.",
        });
    foreach (var result in results.Where(r => r.Severity == ProbeSeverity.Caution))
        Console.WriteLine($"{result.Target}: answered, but not as expected — the port is open, " +
            "so the fix is on the device rather than the firewall.");

    // The common shape of a remote site: nobody forwarded 554, everybody forwarded the SDK
    // port for iVMS. Worth saying, because it turns a "live view is broken here" reading
    // into a working command.
    if (vendor == Vendor.Hikvision &&
        dead.Contains(ProbeTarget.RtspPort) &&
        results.Any(r => r.Target == ProbeTarget.SdkPort && r.Severity == ProbeSeverity.Pass))
        Console.WriteLine(
            $"Live view still works here: `dvrtool live --channel N` goes over {conn.SdkPort}, " +
            "and the desktop app's Live tab has the same transport in its dropdown.");

    // Only the ports DVRTool actually drives decide the exit code.
    return dead.Any(t => t is ProbeTarget.Web or ProbeTarget.RtspPort) ? 1 : 0;
}

/// <summary>
/// Confirms the device on the far end is the one this invocation meant, before any command
/// reads or writes a thing.
/// </summary>
/// <remarks>
/// <para>
/// The mistake this catches cannot be caught by authenticating. A site with several systems
/// behind one IP tells them apart by forwarded port alone, and one shared account across the
/// fleet means a mistyped or re-forwarded port produces a clean login, a full channel list,
/// and an export of the wrong building. So the serial is pinned per host:port on first
/// contact and compared on every later run.
/// </para>
/// <para>
/// The URL-printing commands are exempt, even when a password happens to be in the
/// environment: they build a string and contact nothing, which is the point of them — they
/// work offline, and on a site where RTSP is reachable and the web port is not. Verifying
/// would mean an HTTP round trip, and a timeout on it would turn a working command into a
/// failing one. Instead, when the address is one we have seen before, they report what it
/// was, rather than implying the URL was checked.
/// </para>
/// </remarks>
static async Task VerifyIdentityAsync(INvrClient client, string command,
    Dictionary<string, string> opts, CancellationToken ct)
{
    // A bare --expect-serial asserts nothing; treating it as "no expectation" would turn a
    // safety flag into a silent no-op.
    if (opts.TryGetValue("expect-serial", out var expectRaw) && expectRaw.Length == 0)
        throw new ArgumentException("--expect-serial needs the serial to expect");
    string? expect = expectRaw is { Length: > 0 } ? expectRaw : null;

    var store = DeviceIdentityStore.Default;
    string address = DeviceIdentityGuard.AddressOf(client.Connection);

    if (command is "live-url" or "playback-url")
    {
        if (expect is not null)
            throw new ArgumentException(
                $"--expect-serial cannot be checked by `{command}`: it prints a URL without " +
                "contacting the device. Check the device with `dvrtool info` instead.");
        if (store.Pinned(address) is { } known)
            Console.Error.WriteLine(
                $"note: {address} was last seen as {known.Describe()}, but this command prints " +
                "a URL without contacting it — nothing here confirms that is still what " +
                "answers on that port.");
        return;
    }

    // Nothing to verify with, and nothing that could have been fooled either: without a
    // password no request was made.
    if (client.Connection.Password.Length == 0)
        return;

    if (opts.ContainsKey("trust-new-device"))
    {
        var info = await client.GetDeviceInfoAsync(ct);
        var seen = DeviceFingerprint.From(info);

        // An explicit --expect-serial still decides. The two flags answer different
        // questions, and "accept whatever is there now" must not overrule "it must be this
        // one" — otherwise a script that passes both silently loses its assertion.
        if (expect is not null)
            DeviceIdentityGuard.Ensure(store.Verify(address, seen, expect, "--expect-serial"));

        if (!seen.IsUsable)
        {
            Console.Error.WriteLine(
                $"warning: {address} reports no serial number — there is nothing to pin, so " +
                "--trust-new-device changed nothing.");
            return;
        }
        var previous = store.Pinned(address);
        store.Repin(address, seen);
        Console.Error.WriteLine(previous is null
            ? $"pinned {address} to {seen.Describe()}."
            : $"re-pinned {address}: was serial {previous.Serial.Trim()}, now {seen.Describe()}.");
        return;
    }

    var check = await DeviceIdentityGuard.CheckAsync(client, expect,
        expect is null ? null : "--expect-serial", store, ct);
    DeviceIdentityGuard.Ensure(check);
    if (check.Message.Length > 0)
        Console.Error.WriteLine(
            (check.Verdict == IdentityVerdict.Unverifiable ? "warning: " : "note: ") + check.Message);
}

static INvrClient BuildClient(Dictionary<string, string> opts, bool needsPassword = true)
{
    var (conn, vendor) = BuildConnection(opts, needsPassword);
    return ClientFor(conn, vendor);
}

static INvrClient ClientFor(NvrConnection conn, Vendor vendor) => vendor switch
{
    Vendor.Dahua => new DahuaClient(conn),
    _ => new HikvisionClient(conn),
};

/// <summary>
/// Resolves the connection and its vendor without building a client, so a caller that needs
/// several clients (the port probe disposes each one it opens) resolves the password — and
/// any interactive prompt for it — exactly once.
/// </summary>
static (NvrConnection Conn, Vendor Vendor) BuildConnection(
    Dictionary<string, string> opts, bool needsPassword = true)
{
    string host = Require(opts, "host", "DVR_HOST");
    bool useTls = opts.ContainsKey("tls");
    int httpPort = useTls ? 443 : 80;
    if (host.Contains(':'))
    {
        var parts = host.Split(':', 2);
        host = parts[0];
        httpPort = ParsePort(parts[1], $"--host '{parts[1]}'");
    }

    string user = Require(opts, "user", "DVR_USER");

    // Vendor is resolved before the connection, not after, because the SDK port's default
    // depends on it: 8000 is Hikvision's and 37777 is Dahua's. Defaulting a Dahua recorder
    // to 8000 would make `test` call a perfectly healthy unit's SDK port dead.
    string vendorName = opts.GetValueOrDefault("vendor", "hikvision").ToLowerInvariant();
    Vendor vendor = vendorName switch
    {
        "hikvision" or "hik" => Vendor.Hikvision,
        "dahua" or "amcrest" => Vendor.Dahua,
        _ => throw new ArgumentException($"unknown vendor '{vendorName}' (use hikvision or dahua)"),
    };

    var conn = new NvrConnection
    {
        Host = host,
        HttpPort = httpPort,
        RtspPort = opts.TryGetValue("rtsp-port", out var rp)
            ? ParsePort(rp, "--rtsp-port") : 554,
        SdkPort = opts.TryGetValue("sdk-port", out var sp)
            ? ParsePort(sp, "--sdk-port")
            : Environment.GetEnvironmentVariable("DVR_SDK_PORT") is { Length: > 0 } envSdk
                ? ParsePort(envSdk, "DVR_SDK_PORT")
                : VendorPorts.Sdk(vendor),
        Username = user,
        Password = GetPassword(opts, user, host, needsPassword),
        UseTls = useTls,
    };

    return (conn, vendor);
}

static string Require(Dictionary<string, string> opts, string flag, string envVar)
{
    if (opts.TryGetValue(flag, out var v) && !string.IsNullOrEmpty(v))
        return v;
    return Environment.GetEnvironmentVariable(envVar)
        ?? throw new ArgumentException($"missing --{flag} (or {envVar} in env/.env)");
}

static int ParsePort(string value, string what)
{
    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
        port is < 1 or > 65535)
        throw new ArgumentException($"invalid port in {what}");
    return port;
}

static string GetPassword(Dictionary<string, string> opts, string user, string host,
    bool required)
{
    if (opts.TryGetValue("pass", out var v) && !string.IsNullOrEmpty(v))
        return v;
    if (Environment.GetEnvironmentVariable("DVR_PASS") is { Length: > 0 } env)
        return env;
    if (!required)
        return "";
    // No password anywhere: prompt with echo suppressed rather than failing, so
    // ad-hoc site visits never need the password on the command line at all.
    if (Console.IsInputRedirected)
        throw new ArgumentException("missing --pass (or DVR_PASS in env/.env)");
    Console.Error.Write($"Password for {user}@{host}: ");
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
                sb.Length--;
            continue;
        }
        if (key.KeyChar != '\0')
            sb.Append(key.KeyChar);
    }
    Console.Error.WriteLine();
    return sb.ToString();
}

static void TryDelete(string path)
{
    try { File.Delete(path); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static string FormatDuration(TimeSpan t) =>
    // TimeSpan's "h" specifier drops whole days (40h renders as "16:00:00").
    $"{(long)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

static int RequireChannel(Dictionary<string, string> opts)
{
    if (opts.TryGetValue("channel", out var v) && int.TryParse(v, out int ch) && ch >= 1)
        return ch;
    throw new ArgumentException("missing or invalid --channel");
}

static (DateTime Start, DateTime End) RequireWindow(Dictionary<string, string> opts)
{
    DateTime start = ParseTime(opts.GetValueOrDefault("start")
        ?? throw new ArgumentException("missing --start"));
    DateTime end = ParseTime(opts.GetValueOrDefault("end")
        ?? throw new ArgumentException("missing --end"));
    if (end <= start)
        throw new ArgumentException("--end must be after --start");
    return (start, end);
}

static DateTime ParseTime(string value)
{
    string[] formats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
    ];
    if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t))
        return DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
    throw new ArgumentException($"can't parse time '{value}' (use \"yyyy-MM-dd HH:mm[:ss]\")");
}

static StreamType ParseStream(Dictionary<string, string> opts) =>
    opts.GetValueOrDefault("stream", "main").ToLowerInvariant() switch
    {
        "main" or "0" => StreamType.Main,
        "sub" or "1" => StreamType.Sub,
        var s => throw new ArgumentException($"unknown stream '{s}' (use main or sub)"),
    };

static Dictionary<string, string> ParseOptions(string[] args)
{
    // Flags without a value (or followed by another --flag) are stored as "".
    // --remux is deliberately absent: it takes an optional container name, and bare
    // "--remux" still lands here as "" via the lookahead below.
    string[] boolFlags = ["with-creds", "tls", "force", "trust-new-device", "dry-run"];
    // Flags that may be given more than once (e.g. `access onboard --group A --group B`).
    // Repeats accumulate, joined by an ASCII unit separator the caller splits back out; a
    // plain dictionary would otherwise keep only the last one.
    string[] multiFlags = ["group"];
    var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"unexpected argument '{args[i]}'");
        string key = args[i][2..];
        if (boolFlags.Contains(key) || i + 1 >= args.Length ||
            args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            opts[key] = "";
        }
        else
        {
            string value = args[++i];
            if (multiFlags.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                opts.TryGetValue(key, out var previous) && previous.Length > 0)
                opts[key] = previous + '\u001f' + value;
            else
                opts[key] = value;
        }
    }
    return opts;
}

static void LoadDotEnv(string? explicitPath)
{
    string path = explicitPath ?? Path.Combine(Environment.CurrentDirectory, ".env");
    if (!File.Exists(path))
    {
        if (explicitPath is not null)
            throw new ArgumentException($"--env file not found: {explicitPath}");
        return;
    }
    // Only the keys this tool understands — a planted .env must not be able to
    // inject arbitrary environment variables (inherited by the ffmpeg child).
    string[] allowed =
    [
        "DVR_HOST", "DVR_USER", "DVR_PASS", "DVR_SDK_PORT", "DVR_SDK_DIR",
        // Door-access panels (see AccessCommands). OCB_POLICY = default --policy path for the
        // reconcile/onboard provisioning verbs.
        "OCB_PANELS", "OCB_USER", "OCB_PASS", "OCB_SDK_PORT", "OCB_SDK_DIR", "OCB_POLICY",
    ];
    var applied = new List<string>();
    foreach (string raw in File.ReadAllLines(path))
    {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            continue;
        int eq = line.IndexOf('=');
        if (eq <= 0)
            continue;
        string key = line[..eq].Trim();
        string value = line[(eq + 1)..].Trim().Trim('"');
        if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase))
            continue;
        if (Environment.GetEnvironmentVariable(key) is null && value.Length > 0)
        {
            Environment.SetEnvironmentVariable(key, value);
            applied.Add(key);
        }
    }
    // Never load credentials silently — the operator should know where they came from.
    if (applied.Count > 0)
        Console.Error.WriteLine($"loaded {Path.GetFullPath(path)} (set: {string.Join(", ", applied)})");
}
