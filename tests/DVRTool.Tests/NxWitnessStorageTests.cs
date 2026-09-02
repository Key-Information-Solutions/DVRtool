using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DVRTool.Core;
using DVRTool.Vendors.NxWitness;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The Nx Witness / DW Spectrum client against canned REST v3 replies. The module
/// information is the verbatim anonymous answer of the Site D server (a DW Blackjack E-Rack, DW Spectrum
/// 6.1.1); the authenticated shapes follow the Nx REST v3 documentation and the 2026-09-02
/// field notes.
/// </summary>
public class NxWitnessStorageTests
{
    private const string Token = "vms-0123456789abcdef";
    private const string ServerId = "11111111-2222-3333-4444-555555555555";

    // One host per test that exercises the write path: the client remembers the last camera
    // list it saw per address (process-wide) to refuse a write after the list changed, so two
    // tests sharing an address would see each other's lists.
    private static NvrConnection Conn(string host = "10.0.0.70") => new()
    {
        Host = host,
        HttpPort = 7001,
        RtspPort = 7001,
        SdkPort = 0,
        Username = "admin",
        Password = "secret",
        UseTls = true,
    };

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private const string LoginReply = """
        {"ageS":0,"expiresInS":2592000,"token":"vms-0123456789abcdef","username":"admin"}
        """;

    /// <summary>Verbatim from https://198.51.100.10:7001/api/moduleInformation on 2026-09-02.</summary>
    private const string ModuleInformation = """
        {"error":"0","errorId":"ok","errorString":"","reply":{"brand":"dwspectrum","cloudHost":"dwspectrum.digital-watchdog.com","cloudOwnerId":"{bbbbbbbb-cccc-dddd-eeee-ffffffffffff}","cloudSystemId":"66666666-7777-8888-9999-aaaaaaaaaaaa","customization":"digitalwatchdog","ecDbReadOnly":false,"hwPlatform":"unknown","id":"{11111111-2222-3333-4444-555555555555}","localSystemId":"{12121212-3434-5656-7878-909090909090}","name":"TESTRACK1","port":7001,"protoVersion":6113,"realm":"VMS","remoteAddresses":["198.51.100.10"],"runtimeId":"{21212121-4343-6565-8787-090909090909}","saasState":"uninitialized","serverFlags":"SF_HasPublicIP|SF_SupportsTranscoding","sslAllowed":true,"synchronizedTimeMs":"1788386183191","systemName":"Site D","type":"Media Server","version":"6.1.1.42624"}}
        """;

    private const string LobbyId = "2d2cd010-f38e-c6f7-e46b-927ce07789da";
    private const string AlleyId = "0292cb36-1f91-1313-a096-82ee83c6cec0";
    private const string ParkingId = "500e2728-cc49-0c91-d9f7-eeef2b29422c";

    /// <summary>
    /// Four devices: two cameras with quality and preset schedules, one with recording
    /// switched off and "Keep camera stream and profile settings" on, and an I/O module.
    /// Names are deliberately out of id order so the sort is exercised.
    /// </summary>
    private const string Devices = """
        [
          {
            "id": "{2d2cd010-f38e-c6f7-e46b-927ce07789da}",
            "name": "Lobby",
            "physicalId": "00-0D-F1-AA-BB-CC",
            "mac": "00-0D-F1-AA-BB-CC",
            "url": "http://198.51.100.41:80/onvif/device_service",
            "serverId": "{11111111-2222-3333-4444-555555555555}",
            "deviceType": "Camera",
            "status": "Recording",
            "schedule": {
              "isEnabled": true,
              "tasks": [
                {"dayOfWeek": 1, "startTime": 0, "endTime": 86400, "recordingType": "always", "metadataTypes": "none", "streamQuality": "high", "fps": 15, "bitrateKbps": 0},
                {"dayOfWeek": 2, "startTime": 0, "endTime": 86400, "recordingType": "metadataOnly", "metadataTypes": "motion", "streamQuality": "highest", "fps": 30, "bitrateKbps": 0},
                {"dayOfWeek": 3, "startTime": 0, "endTime": 86400, "recordingType": "never", "metadataTypes": "none", "streamQuality": "highest", "fps": 30, "bitrateKbps": 0}
              ]
            },
            "options": {"dontRecordPrimaryStream": false, "dontRecordSecondaryStream": false, "isDualStreamingDisabled": false, "controlEnabled": true, "isAudioEnabled": false},
            "mediaStreams": {"streams": [
              {"codec": 27, "encoderIndex": 0, "resolution": "1920x1080", "transcodingRequired": false, "transports": ["rtsp"]},
              {"codec": 27, "encoderIndex": 1, "resolution": "704x480", "transcodingRequired": false, "transports": ["rtsp"]}
            ]}
          },
          {
            "id": "{0292cb36-1f91-1313-a096-82ee83c6cec0}",
            "name": "Alley",
            "physicalId": "D0-3B-F4-11-22-33",
            "mac": "D0-3B-F4-11-22-33",
            "url": "http://198.51.100.141:80",
            "serverId": "{11111111-2222-3333-4444-555555555555}",
            "deviceType": "Camera",
            "status": "Offline",
            "schedule": {
              "isEnabled": true,
              "tasks": [
                {"dayOfWeek": 1, "startTime": 0, "endTime": 86400, "recordingType": "always", "metadataTypes": "none", "streamQuality": "preset", "fps": 12, "bitrateKbps": 3072}
              ]
            },
            "options": {"dontRecordSecondaryStream": true, "controlEnabled": true},
            "mediaStreams": "{\"streams\":[{\"codec\":173,\"encoderIndex\":0,\"resolution\":\"2688x1520\"},{\"codec\":173,\"encoderIndex\":1,\"resolution\":\"704x480\"}]}"
          },
          {
            "id": "{7cacccd5-1435-09fe-4a01-6ed89d231131}",
            "name": "Warehouse Door IO",
            "deviceType": "IOModule",
            "status": "Online",
            "schedule": {"isEnabled": true, "tasks": []}
          },
          {
            "id": "{500e2728-cc49-0c91-d9f7-eeef2b29422c}",
            "name": "Parking",
            "deviceType": "Camera",
            "status": "Online",
            "schedule": {
              "isEnabled": false,
              "tasks": [
                {"dayOfWeek": 1, "startTime": 0, "endTime": 86400, "recordingType": "always", "streamQuality": "high", "fps": 15, "bitrateKbps": 0}
              ]
            },
            "options": {"controlEnabled": false},
            "mediaStreams": {"streams": [{"codec": "H265", "encoderIndex": 0, "resolution": "3840x2160"}]}
          }
        ]
        """;

    private const string Storages = """
        [
          {"id": "{11111111-1111-1111-1111-111111111111}", "name": "D:\\DW Spectrum Media", "path": "D:\\DW Spectrum Media", "serverId": "{11111111-2222-3333-4444-555555555555}", "spaceLimitB": 107374182400, "isUsedForWriting": true, "isBackup": false, "type": "local", "status": "online|dbReady"},
          {"id": "{22222222-2222-2222-2222-222222222222}", "path": "E:\\Backup", "serverId": "{11111111-2222-3333-4444-555555555555}", "spaceLimitB": 53687091200, "isUsedForWriting": true, "isBackup": true, "type": "local"},
          {"id": "{33333333-3333-3333-3333-333333333333}", "path": "C:\\", "serverId": "{11111111-2222-3333-4444-555555555555}", "spaceLimitB": 10737418240, "isUsedForWriting": false, "isBackup": false, "type": "local"}
        ]
        """;

    private const string StorageSpace = """
        {"error":"0","errorString":"","reply":{"storages":[
          {"storageId":"{11111111-1111-1111-1111-111111111111}","url":"D:\\DW Spectrum Media","totalSpace":"70004008468480","freeSpace":"402653184000","reservedSpace":"107374182400","isOnline":true,"isUsedForWriting":true,"isBackup":false,"isExternal":false,"isWritable":true,"storageType":"local"},
          {"storageId":"{22222222-2222-2222-2222-222222222222}","url":"E:\\Backup","totalSpace":"8001563222016","freeSpace":"1000000000000","reservedSpace":"53687091200","isOnline":true,"isUsedForWriting":true,"isBackup":true,"storageType":"local"},
          {"storageId":"{33333333-3333-3333-3333-333333333333}","url":"C:\\","totalSpace":"512110190592","freeSpace":"200000000000","reservedSpace":"10737418240","isOnline":true,"isUsedForWriting":false,"isBackup":false,"storageType":"local"}
        ]}}
        """;

    /// <summary>The common server: login, module information, the device list, one device by id.</summary>
    private static HttpResponseMessage? Common(HttpRequestMessage req)
    {
        string pq = req.RequestUri!.PathAndQuery;
        if (req.Method == HttpMethod.Post && pq == "/rest/v3/login/sessions")
            return MockHttpHandler.Text(LoginReply);
        if (req.Method == HttpMethod.Delete && pq.StartsWith("/rest/v3/login/sessions/", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK);
        if (pq == "/api/moduleInformation")
            return MockHttpHandler.Text(ModuleInformation);
        if (req.Method == HttpMethod.Get && pq == "/rest/v3/devices")
            return MockHttpHandler.Text(Devices);
        if (req.Method == HttpMethod.Get && pq.StartsWith("/rest/v3/devices/", StringComparison.Ordinal) &&
            !pq.Contains("/footage", StringComparison.Ordinal))
            return MockHttpHandler.Text(DeviceById(pq["/rest/v3/devices/".Length..]));
        return null;
    }

    private static string DeviceById(string id)
    {
        using var doc = JsonDocument.Parse(Devices);
        foreach (var d in doc.RootElement.EnumerateArray())
            if (d.GetProperty("id").GetString()!.Trim('{', '}') == id)
                return d.GetRawText();
        throw new Xunit.Sdk.XunitException($"no fixture device {id}");
    }

    private static MockHttpHandler Server(Func<HttpRequestMessage, string, HttpResponseMessage?>? extra = null) =>
        new((req, body) => extra?.Invoke(req, body) ?? Common(req)
            ?? MockHttpHandler.Text("""{"errorId":"notFound","errorString":"no such fixture"}""",
                HttpStatusCode.NotFound));

    // ----- session and identity -----

    [Fact]
    public async Task Login_SendsBearerOnLaterRequests_AndClosesTheSessionOnDispose()
    {
        var handler = Server();
        var client = new NxWitnessClient(Conn(), handler);

        var info = await client.GetDeviceInfoAsync();
        client.Dispose();

        var login = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, login.Request.Method);
        Assert.Equal("/rest/v3/login/sessions", login.Request.RequestUri!.PathAndQuery);
        Assert.Contains("\"username\":\"admin\"", login.Body);
        Assert.Contains("\"password\":\"secret\"", login.Body);
        Assert.Null(login.Request.Headers.Authorization);

        var read = handler.Requests[1];
        Assert.Equal("/api/moduleInformation", read.Request.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer", read.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, read.Request.Headers.Authorization.Parameter);

        var close = handler.Requests[^1];
        Assert.Equal(HttpMethod.Delete, close.Request.Method);
        Assert.Equal($"/rest/v3/login/sessions/{Token}", close.Request.RequestUri!.PathAndQuery);

        Assert.Equal("TESTRACK1 (Site D)", info.Name);
        Assert.Equal("DW Spectrum Media Server", info.Model);
        Assert.Equal(ServerId, info.SerialNumber); // the pin: server GUID without braces
        Assert.Equal("6.1.1.42624", info.FirmwareVersion);
    }

    [Fact]
    public async Task Login_Rejected_Throws401_SoTheProbeReportsBadCredentials()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.PathAndQuery == "/rest/v3/login/sessions"
                ? MockHttpHandler.Text("""{"error":"3","errorId":"unauthorized","errorString":"Unauthorized"}""",
                    HttpStatusCode.Unauthorized)
                : MockHttpHandler.Text(ModuleInformation));
        using var client = new NxWitnessClient(Conn(), handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.GetDeviceInfoAsync());

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("login rejected", ex.Message);
        Assert.Contains("Unauthorized", ex.Message);
        Assert.Single(handler.Requests); // moduleInformation is never read on a failed login
    }

    [Fact]
    public void DeviceInfo_BrandNames_CoverTheOemFamily()
    {
        Assert.Equal("DW Spectrum", NxWitnessClient.BrandName("dwspectrum", "digitalwatchdog"));
        Assert.Equal("Nx Witness", NxWitnessClient.BrandName("hdwitness", "default"));
        Assert.Equal("Nx Witness", NxWitnessClient.BrandName("", ""));
        Assert.Equal("Wisenet WAVE", NxWitnessClient.BrandName("wave", "hanwha"));
        Assert.Equal("acme", NxWitnessClient.BrandName("acme", "x"));
    }

    // ----- channels -----

    [Fact]
    public async Task Channels_SortedByName_NumberedFromOne_IoModulesExcluded_OnlineFromStatus()
    {
        using var client = new NxWitnessClient(Conn(), Server());

        var channels = await client.GetChannelsAsync();

        Assert.Equal(3, channels.Count);
        Assert.Equal((1, "Alley", (bool?)false), (channels[0].Id, channels[0].Name, channels[0].Online));
        Assert.Equal((2, "Lobby", (bool?)true), (channels[1].Id, channels[1].Name, channels[1].Online));
        Assert.Equal((3, "Parking", (bool?)true), (channels[2].Id, channels[2].Name, channels[2].Online));
    }

    [Fact]
    public async Task StreamUris_NeedTheCameraListFirst_ThenAddressCamerasById()
    {
        using var client = new NxWitnessClient(Conn("10.0.0.71"), Server()) { Zone = Utc };

        Assert.Throws<InvalidOperationException>(() => client.GetLiveUri(1));

        await client.GetChannelsAsync();

        Assert.Equal($"rtsp://10.0.0.71:7001/{AlleyId}?stream=0", client.GetLiveUri(1).ToString());
        Assert.Equal($"rtsp://10.0.0.71:7001/{LobbyId}?stream=1",
            client.GetLiveUri(2, StreamType.Sub).ToString());
        Assert.Equal($"rtsp://admin:secret@10.0.0.71:7001/{AlleyId}?stream=0",
            client.GetLiveUri(1, includeCredentials: true).ToString());
        Assert.Equal(
            $"rtsp://10.0.0.71:7001/{LobbyId}?pos=1755561600000&endPos=1755565200000&stream=0",
            client.GetPlaybackUri(2, new DateTime(2025, 8, 19, 0, 0, 0), new DateTime(2025, 8, 19, 1, 0, 0))
                .ToString());
        Assert.Throws<InvalidOperationException>(() => client.GetLiveUri(4));
    }

    // ----- main streams -----

    [Fact]
    public async Task MainStreams_PresetCell_IsTheCap_NoSecondaryWhenItIsNotRecorded()
    {
        using var client = new NxWitnessClient(Conn(), Server());

        var streams = await client.GetMainStreamsAsync();
        var alley = streams[0];

        Assert.Equal(1, alley.Channel);
        Assert.True(alley.Enabled);
        Assert.Equal("H.265", alley.CodecType);       // codec 173, from the string-encoded mediaStreams
        Assert.Equal("2688x1520", alley.Resolution);
        Assert.Equal(12.0, alley.FrameRateFps);
        Assert.Equal("KBPS", alley.QualityControlType);
        Assert.Equal(3072, alley.MaxBitrateKbps);
        Assert.Null(alley.FixedQuality);
        Assert.Null(alley.SecondaryRecordedKbps);     // dontRecordSecondaryStream
        Assert.Equal(3072, alley.RecordedBitrateKbps);
    }

    [Fact]
    public async Task MainStreams_QualityCells_UseNxsFormula_BusiestCellWins_SecondaryAdded()
    {
        using var client = new NxWitnessClient(Conn(), Server());

        var streams = await client.GetMainStreamsAsync();
        var lobby = streams[1];

        // The "never" cell is schedule white space; of the two recording cells, highest@30
        // costs more than high@15 and is the worst case the disks pay for.
        int expectedPrimary = NxBitrate.SuggestKbps(4, 1920, 1080, 30, "H.264");
        int expectedSecondary = NxBitrate.SuggestKbps(1, 704, 480, 30, "H.264");
        Assert.True(expectedPrimary > NxBitrate.SuggestKbps(3, 1920, 1080, 15, "H.264"));

        Assert.Equal(2, lobby.Channel);
        Assert.True(lobby.Enabled);
        Assert.Equal("H.264", lobby.CodecType);
        Assert.Equal("1920x1080", lobby.Resolution);
        Assert.Equal(30.0, lobby.FrameRateFps);
        Assert.Equal("BEST", lobby.QualityControlType);
        Assert.Equal(4, lobby.FixedQuality);
        Assert.Equal(expectedPrimary, lobby.MaxBitrateKbps);
        Assert.Equal(expectedSecondary, lobby.SecondaryRecordedKbps);
        Assert.Equal(expectedPrimary + expectedSecondary, lobby.RecordedBitrateKbps);
    }

    [Fact]
    public async Task MainStreams_DisabledSchedule_IsReportedButNotEnabled()
    {
        using var client = new NxWitnessClient(Conn(), Server());

        var parking = (await client.GetMainStreamsAsync())[2];

        Assert.Equal(3, parking.Channel);
        Assert.False(parking.Enabled);
        Assert.Equal("H.265", parking.CodecType);     // codec by name, not number
        Assert.Equal("3840x2160", parking.Resolution);
        Assert.Equal("HIGH", parking.QualityControlType);
        Assert.Equal(NxBitrate.SuggestKbps(3, 3840, 2160, 15, "H.265"), parking.MaxBitrateKbps);
        Assert.Null(parking.SecondaryRecordedKbps);   // single-stream camera
    }

    [Fact]
    public void BitrateFormula_MatchesTheFiguresNxShows()
    {
        // 1080p @ 15 fps "high" ≈ 2.8 Mbps; 4 MP @ 15 fps "highest" ≈ 5.8 Mbps; H.265 is 80 %.
        Assert.InRange(NxBitrate.SuggestKbps(3, 1920, 1080, 15, "H.264"), 2700, 2830);
        Assert.InRange(NxBitrate.SuggestKbps(4, 2688, 1520, 15, "H.264"), 5650, 5850);
        Assert.Equal(
            (int)Math.Round(NxBitrate.SuggestKbps(4, 2688, 1520, 15, "H.264") * 0.8),
            NxBitrate.SuggestKbps(4, 2688, 1520, 15, "H.265"), 1.0);
        Assert.Equal(NxBitrate.MinKbps, NxBitrate.SuggestKbps(0, 160, 120, 1, "H.264"));
        Assert.Equal(0, NxBitrate.SuggestKbps(2, 0, 0, 15, "H.264")); // unknown resolution: no number
        Assert.Equal(0, NxBitrate.SuggestKbps(2, 1920, 1080, 0, "H.264"));
    }

    [Theory]
    [InlineData("lowest", 0)]
    [InlineData("QualityLow", 1)]
    [InlineData("normal", 2)]
    [InlineData("QualityHigh", 3)]
    [InlineData("highest", 4)]
    public void QualityLevels_AcceptV3AndLegacySpellings(string quality, int level) =>
        Assert.Equal(level, NxBitrate.QualityLevel(quality));

    [Fact]
    public void QualityLevel_PresetHasNoLevel()
    {
        Assert.Null(NxBitrate.QualityLevel("preset"));
        Assert.Null(NxBitrate.QualityLevel("QualityPreSet"));
        Assert.Equal("preset", NxBitrate.NormalizeQuality("QualityPreSet"));
    }

    [Theory]
    [InlineData("RT_MotionOnly", "metadataonly", true)]
    [InlineData("metadataAndLowQuality", "metadataandlowquality", true)]
    [InlineData("RT_Never", "never", false)]
    [InlineData("always", "always", true)]
    public void RecordingTypes_AcceptV3AndLegacySpellings(string raw, string normalized, bool records)
    {
        Assert.Equal(normalized, NxScheduleTask.NormalizeRecordingType(raw));
        var task = new NxScheduleTask(1, 0, 86400, normalized, "high", 15, 0);
        Assert.Equal(records, task.Records);
    }

    // ----- storage -----

    [Fact]
    public async Task StorageInfo_JoinsTheRestListWithLegacySizes_ExcludesReserve_BackupAndUnusedDontCount()
    {
        var handler = Server((req, _) => req.RequestUri!.PathAndQuery switch
        {
            var pq when pq == $"/rest/v3/servers/{ServerId}/storages" => MockHttpHandler.Text(Storages),
            "/api/storageSpace" => MockHttpHandler.Text(StorageSpace),
            _ => null,
        });
        using var client = new NxWitnessClient(Conn(), handler);

        var info = await client.GetStorageInfoAsync();

        Assert.Equal(3, info.Hdds.Count);
        Assert.Equal(3, info.InstalledCount);
        Assert.Equal(0, info.GhostBayCount);

        var main = info.Hdds[0];
        Assert.Equal(1, main.Id);
        Assert.Equal(@"D:\DW Spectrum Media", main.Name);
        Assert.Equal("ok", main.Status);
        Assert.Equal("local", main.HddType);
        Assert.StartsWith("main, 107 GB reserved", main.Property);
        Assert.Equal(69_896_634L, main.CapacityMB);   // (70004008468480 − 107374182400) / 1e6
        Assert.Equal(295_279L, main.FreeSpaceMB);     // (402653184000 − 107374182400) / 1e6
        Assert.True(main.RecordsFootage);

        var backup = info.Hdds[1];
        Assert.StartsWith("backup", backup.Property);
        Assert.False(backup.RecordsFootage);
        Assert.Equal(7_947_876L, backup.CapacityMB);

        var system = info.Hdds[2];
        Assert.StartsWith("not used for writing", system.Property);
        Assert.False(system.RecordsFootage);

        // Retention math sees only the recording pool.
        Assert.Equal(69_896_634L, info.TotalCapacityMB);
        Assert.Empty(info.UnhealthyHdds);
    }

    [Fact]
    public async Task StorageInfo_WithoutTheLegacySizeCall_ListsVolumesWithUnknownSize()
    {
        var handler = Server((req, _) => req.RequestUri!.PathAndQuery switch
        {
            var pq when pq == $"/rest/v3/servers/{ServerId}/storages" => MockHttpHandler.Text(Storages),
            "/api/storageSpace" => MockHttpHandler.Text("Forbidden", HttpStatusCode.Forbidden),
            _ => null,
        });
        using var client = new NxWitnessClient(Conn(), handler);

        var info = await client.GetStorageInfoAsync();

        Assert.Equal(3, info.Hdds.Count);
        Assert.Equal(0, info.TotalCapacityMB);
        Assert.Contains("size unknown", info.Hdds[0].Property);
        Assert.Equal("ok", info.Hdds[0].Status);      // the REST flags say online
        Assert.Equal("unknown", info.Hdds[1].Status); // nothing says anything about this one
    }

    [Fact]
    public void StorageStatus_MapsNxFlags()
    {
        Assert.Equal("ok", NxWitnessClient.MapStorageStatus("online|dbReady", null, null));
        Assert.Equal("ok", NxWitnessClient.MapStorageStatus("", true, null));
        Assert.Equal("ok", NxWitnessClient.MapStorageStatus("", null, 5_000_000_000));
        Assert.Equal("offline", NxWitnessClient.MapStorageStatus("online", false, 5_000_000_000));
        Assert.Equal("checking", NxWitnessClient.MapStorageStatus("beingChecked|online", true, null));
        Assert.Equal("unknown", NxWitnessClient.MapStorageStatus("", null, null));
    }

    // ----- footage -----

    [Fact]
    public async Task FindOldestRecording_TakesTheEarliestStart_RenderedInTheZone()
    {
        string? footagePath = null;
        var handler = Server((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (!pq.Contains("/footage", StringComparison.Ordinal))
                return null;
            footagePath = pq;
            // Deliberately not oldest-first, with string-typed numbers and an open-ended period.
            return MockHttpHandler.Text("""
                [{"startTimeMs":"1755648000000","durationMs":"-1"},{"startTimeMs":1755561600000,"durationMs":3600000}]
                """);
        });
        using var client = new NxWitnessClient(Conn(), handler) { Zone = Utc };

        var oldest = await client.FindOldestRecordingAsync(2);

        Assert.Equal(new DateTime(2025, 8, 19, 0, 0, 0), oldest);
        Assert.Equal(DateTimeKind.Unspecified, oldest!.Value.Kind);
        Assert.StartsWith($"/rest/v3/devices/{LobbyId}/footage?startTimeMs=0&", footagePath);
        Assert.Contains("detailLevelMs=1", footagePath);
    }

    [Fact]
    public async Task FindOldestRecording_NoFootage_ReturnsNull()
    {
        var handler = Server((req, _) =>
            req.RequestUri!.PathAndQuery.Contains("/footage", StringComparison.Ordinal)
                ? MockHttpHandler.Text("[]")
                : null);
        using var client = new NxWitnessClient(Conn(), handler);

        Assert.Null(await client.FindOldestRecordingAsync(1));
    }

    [Fact]
    public async Task FindOldestRecording_UnknownChannel_Throws()
    {
        using var client = new NxWitnessClient(Conn(), Server());

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.FindOldestRecordingAsync(9));
        Assert.Contains("lists 3 camera(s)", ex.Message);
    }

    [Fact]
    public async Task Search_ConvertsTheWindowToUtcMs_AndPeriodsBackToWallClock()
    {
        string? footagePath = null;
        var handler = Server((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (!pq.Contains("/footage", StringComparison.Ordinal))
                return null;
            footagePath = pq;
            return MockHttpHandler.Text("""
                [{"startTimeMs":1755612000000,"durationMs":600000}]
                """);
        });
        var minusFour = TimeZoneInfo.CreateCustomTimeZone("UTC-4", TimeSpan.FromHours(-4), "UTC-4", "UTC-4");
        using var client = new NxWitnessClient(Conn(), handler) { Zone = minusFour };

        // 2025-08-19 10:00 at UTC−4 is 14:00Z = 1755612000000.
        var segments = await client.SearchAsync(2,
            new DateTime(2025, 8, 19, 10, 0, 0), new DateTime(2025, 8, 19, 11, 0, 0));

        Assert.Contains("startTimeMs=1755612000000", footagePath);
        Assert.Contains("endTimeMs=1755615600000", footagePath);
        var seg = Assert.Single(segments);
        Assert.Equal(new DateTime(2025, 8, 19, 10, 0, 0), seg.Start);
        Assert.Equal(new DateTime(2025, 8, 19, 10, 10, 0), seg.End);
        Assert.Equal(2, seg.Channel);
        Assert.Equal(LobbyId, seg.NativeId);
    }

    [Fact]
    public void ParseFootage_AcceptsWrappersAndPairs()
    {
        using var wrapped = JsonDocument.Parse("""{"reply":[{"startTimeMs":"10","durationMs":"20"}]}""");
        var a = NxWitnessClient.ParseFootage(wrapped.RootElement);
        Assert.Equal((10L, 20L), (a[0].StartMs, a[0].DurationMs));

        using var pairs = JsonDocument.Parse("[[30, 40], [50, -1]]");
        var b = NxWitnessClient.ParseFootage(pairs.RootElement);
        Assert.Equal(2, b.Count);
        Assert.True(b[1].OpenEnded);

        using var junk = JsonDocument.Parse("""{"nothing":true}""");
        Assert.Empty(NxWitnessClient.ParseFootage(junk.RootElement));
    }

    // ----- bitrate range and write -----

    [Fact]
    public async Task BitrateRange_IsWide_NxHasNoPerCameraBounds()
    {
        using var client = new NxWitnessClient(Conn(), Server());

        var range = await client.GetBitrateRangeAsync(1);

        Assert.Equal(new BitrateRange(192, 65_536), range);
    }

    [Fact]
    public async Task SetMaxBitrate_PatchesEveryRecordingCellAsPreset_LeavesNeverCells_ReadsBack()
    {
        string? patchBody = null;
        var handler = Server((req, body) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (pq == "/rest/v3/system/settings")
                return MockHttpHandler.Text("""{"cameraSettingsOptimization":true,"systemName":"Site D"}""");
            if (req.Method == HttpMethod.Patch && pq == $"/rest/v3/devices/{LobbyId}")
            {
                patchBody = body;
                return MockHttpHandler.Text("{}");
            }
            // The read-back after the PATCH: the device as the server would now hold it.
            if (req.Method == HttpMethod.Get && pq == $"/rest/v3/devices/{LobbyId}" && patchBody is not null)
            {
                var device = JsonNode.Parse(DeviceById(LobbyId))!.AsObject();
                device["schedule"] = JsonNode.Parse(patchBody)!["schedule"]!.DeepClone();
                return MockHttpHandler.Text(device.ToJsonString());
            }
            return null;
        });
        using var client = new NxWitnessClient(Conn("10.0.0.72"), handler);

        int actual = await client.SetMaxBitrateAsync(2, 4096);

        Assert.Equal(4096, actual);
        Assert.NotNull(patchBody);
        var schedule = JsonNode.Parse(patchBody!)!["schedule"]!.AsObject();
        Assert.True(schedule["isEnabled"]!.GetValue<bool>());
        var tasks = schedule["tasks"]!.AsArray();
        Assert.Equal(3, tasks.Count);
        foreach (var task in tasks.Take(2))
        {
            Assert.Equal("preset", task!["streamQuality"]!.GetValue<string>());
            Assert.Equal(4096, task["bitrateKbps"]!.GetValue<int>());
            Assert.Equal(1, task["dayOfWeek"]!.GetValue<int>() is 1 or 2 ? 1 : 0); // untouched fields survive
        }
        Assert.Equal("never", tasks[2]!["recordingType"]!.GetValue<string>());
        Assert.Equal("highest", tasks[2]!["streamQuality"]!.GetValue<string>()); // white space left alone
        Assert.Equal(0, tasks[2]!["bitrateKbps"]!.GetValue<int>());

        // The body is a PATCH of the schedule alone — nothing else about the device is sent.
        var root = JsonNode.Parse(patchBody!)!.AsObject();
        Assert.Single(root);
        Assert.Contains(handler.Requests, r => r.Request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SetMaxBitrate_RefusedWhenTheCameraKeepsItsOwnProfile_NothingWritten()
    {
        var handler = Server();
        using var client = new NxWitnessClient(Conn("10.0.0.73"), handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.SetMaxBitrateAsync(3, 2048));

        Assert.Contains("Keep camera stream and profile settings", ex.Message);
        Assert.Contains("Nothing was written", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SetMaxBitrate_RefusedWhenTheSiteDoesNotPushSettings_NothingWritten()
    {
        var handler = Server((req, _) => req.RequestUri!.PathAndQuery == "/rest/v3/system/settings"
            ? MockHttpHandler.Text("""{"cameraSettingsOptimization":false}""")
            : null);
        using var client = new NxWitnessClient(Conn("10.0.0.74"), handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.SetMaxBitrateAsync(2, 2048));

        Assert.Contains("cameraSettingsOptimization", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SetMaxBitrate_RefusedWhenTheCameraListChangedSinceItWasRead()
    {
        // The desktop app reads with one client and writes with another. Between the two, a
        // camera named "Aardvark" appears and shifts every number by one.
        var conn = Conn("10.0.0.75");
        using (var reader = new NxWitnessClient(conn, Server()))
            await reader.GetMainStreamsAsync();

        string shifted = Devices.Replace("\"name\": \"Parking\"", "\"name\": \"Aardvark\"");
        var handler = Server((req, _) =>
            req.Method == HttpMethod.Get && req.RequestUri!.PathAndQuery == "/rest/v3/devices"
                ? MockHttpHandler.Text(shifted)
                : null);
        using var writer = new NxWitnessClient(conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => writer.SetMaxBitrateAsync(1, 2048));

        Assert.Contains("camera list changed", ex.Message);
        Assert.Contains("'Alley'", ex.Message);
        Assert.Contains("'Aardvark'", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SetMaxBitrate_ServerRejectsThePatch_Throws()
    {
        var handler = Server((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (pq == "/rest/v3/system/settings")
                return MockHttpHandler.Text("""{"cameraSettingsOptimization":true}""");
            if (req.Method == HttpMethod.Patch)
                return MockHttpHandler.Text("""{"errorId":"forbidden","errorString":"Forbidden"}""",
                    HttpStatusCode.Forbidden);
            return null;
        });
        using var client = new NxWitnessClient(Conn("10.0.0.76"), handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.SetMaxBitrateAsync(2, 2048));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Forbidden", ex.Message);
    }

    // ----- parsing corners -----

    [Fact]
    public void Camera_ParsesLegacyStringMediaStreams_AndCodecIds()
    {
        using var doc = JsonDocument.Parse("""
            {"id":"{aaaaaaaa-0000-0000-0000-000000000001}","name":"X",
             "mediaStreams":"{\"streams\":[{\"codec\":7,\"encoderIndex\":0,\"resolution\":\"*\"},{\"codec\":27,\"encoderIndex\":1,\"resolution\":\"640x360\"}]}"}
            """);
        var cam = NxCamera.Parse(doc.RootElement)!;

        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", cam.Id);
        Assert.Equal("MJPEG", cam.Primary!.Codec);
        Assert.Equal((0, 0), (cam.Primary.Width, cam.Primary.Height)); // "*" is Nx for unknown
        Assert.Equal(("H.264", 640, 360), (cam.Secondary!.Codec, cam.Secondary.Width, cam.Secondary.Height));
        Assert.False(cam.ScheduleEnabled); // no schedule object at all
        Assert.False(cam.KeepCameraProfile);
    }

    [Fact]
    public void Json_ReadsNumbersAsStringsAndBracedIds()
    {
        using var doc = JsonDocument.Parse("""
            {"a":"2495680086016.000000","b":12,"c":"true","d":false,"id":"{abc}","e":null}
            """);
        var e = doc.RootElement;
        Assert.Equal(2_495_680_086_016L, NxJson.Int64(e, "a"));
        Assert.Equal(12, NxJson.Int32(e, "b"));
        Assert.True(NxJson.Bool(e, "c"));
        Assert.False(NxJson.Bool(e, "d"));
        Assert.Null(NxJson.Bool(e, "e"));
        Assert.Null(NxJson.Str(e, "missing"));
        Assert.Equal("abc", NxJson.StripBraces(NxJson.Str(e, "id")));
        Assert.Equal((1920, 1080), NxJson.ParseResolution("1920x1080"));
        Assert.Equal((0, 0), NxJson.ParseResolution("*"));
    }

    [Fact]
    public void CameraOptimizationSetting_ReadsBothShapes()
    {
        using var flat = JsonDocument.Parse("""{"cameraSettingsOptimization":false}""");
        Assert.False(NxWitnessClient.ReadCameraOptimization(flat.RootElement));
        using var wrapped = JsonDocument.Parse("""{"reply":{"cameraSettingsOptimization":{"value":true}}}""");
        Assert.True(NxWitnessClient.ReadCameraOptimization(wrapped.RootElement));
        using var absent = JsonDocument.Parse("""{"systemName":"x"}""");
        Assert.Null(NxWitnessClient.ReadCameraOptimization(absent.RootElement));
    }
}
