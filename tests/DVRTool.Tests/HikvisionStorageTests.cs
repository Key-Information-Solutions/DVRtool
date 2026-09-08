using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

public class HikvisionStorageTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss:word",
    };

    private const string Ns = "http://www.hikvision.com/ver20/XMLSchema";

    // The same firmware family serves the storage XML under a different namespace URI.
    private const string IsapiNs = "http://www.isapi.org/ver20/XMLSchema";

    private static string StorageXml(string ns) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <storage version="2.0" xmlns="{ns}">
        <hddList>
        <hdd>
        <id>1</id><hddName>hdd1</hddName><hddType>SATA</hddType><status>ok</status>
        <capacity>7630885</capacity><freeSpace>0</freeSpace><property>RW</property>
        <hddSerialNumber>WD-1F00ELDU</hddSerialNumber><hddModel>WDC WD85PURZ</hddModel>
        </hdd>
        <hdd>
        <id>4</id><hddName>hdd4</hddName><hddType>SATA</hddType><status>notexist</status>
        <capacity>0</capacity><freeSpace>0</freeSpace><property>RW</property>
        <hddSerialNumber>WD-WCC4N2CZ1XS1</hddSerialNumber><hddModel>WDC WD30PURX</hddModel>
        </hdd>
        <hdd>
        <id>5</id><hddName>hdd5</hddName><hddType>SATA</hddType><status>error</status>
        <capacity>7630885</capacity><freeSpace>120</freeSpace><property>RW</property>
        <hddSerialNumber>WD-1F00GLKU</hddSerialNumber><hddModel>WDC WD85PURZ</hddModel>
        </hdd>
        </hddList>
        <workMode opt="group,quota">quota</workMode>
        </storage>
        """;

    [Theory]
    [InlineData(Ns)]
    [InlineData(IsapiNs)]
    public async Task StorageInfo_ParsesHdds_UnderBothNamespaces(string ns)
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath == "/ISAPI/ContentMgmt/Storage"
                ? MockHttpHandler.Xml(StorageXml(ns))
                : MockHttpHandler.Xml($"""
                    <storage version="2.0" xmlns="{ns}">
                    <hddList size="16"></hddList>
                    <workMode opt="group,quota">quota</workMode>
                    </storage>
                    """));
        using var client = new HikvisionClient(Conn, handler);

        var info = await client.GetStorageInfoAsync();

        Assert.Equal("/ISAPI/ContentMgmt/Storage", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal(3, info.Hdds.Count);
        Assert.Equal("quota", info.WorkMode);
        Assert.Equal(16, info.MaxSupportedHdds);

        // The notexist row is a ghost of a removed disk: displayed, never counted.
        Assert.Equal(2, info.InstalledCount);
        Assert.Equal(1, info.GhostBayCount);
        Assert.Equal(2 * 7630885L, info.TotalCapacityMB);
        Assert.False(info.Hdds.Single(h => h.Id == 4).IsInstalled);

        // Installed-but-not-ok is a health problem, not an empty bay.
        var bad = Assert.Single(info.UnhealthyHdds);
        Assert.Equal(5, bad.Id);
        Assert.Equal("WD-1F00ELDU", info.Hdds.Single(h => h.Id == 1).SerialNumber);
    }

    [Fact]
    public async Task StorageInfo_SurvivesMissingCapabilitiesEndpoint()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath == "/ISAPI/ContentMgmt/Storage"
                ? MockHttpHandler.Xml(StorageXml(Ns))
                : MockHttpHandler.Text("Not Found", HttpStatusCode.NotFound));
        using var client = new HikvisionClient(Conn, handler);

        var info = await client.GetStorageInfoAsync();

        Assert.Equal(3, info.Hdds.Count);
        Assert.Null(info.MaxSupportedHdds);
    }

    [Theory]
    [InlineData(Ns)]
    [InlineData(IsapiNs)]
    public async Task MainStreams_KeepsMainDropsSubAndThirdStreams(string ns)
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Xml($"""
            <StreamingChannelList version="2.0" xmlns="{ns}">
            <StreamingChannel>
              <id>101</id><channelName>101</channelName><enabled>true</enabled>
              <Video>
                <enabled>true</enabled>
                <videoCodecType>H.265</videoCodecType>
                <videoResolutionWidth>4256</videoResolutionWidth>
                <videoResolutionHeight>1888</videoResolutionHeight>
                <videoQualityControlType>VBR</videoQualityControlType>
                <fixedQuality>90</fixedQuality>
                <vbrUpperCap>5120</vbrUpperCap>
                <vbrLowerCap>32</vbrLowerCap>
                <maxFrameRate>2000</maxFrameRate>
              </Video>
            </StreamingChannel>
            <StreamingChannel>
              <id>102</id><channelName>102</channelName><enabled>true</enabled>
              <Video><enabled>true</enabled><vbrUpperCap>2048</vbrUpperCap></Video>
            </StreamingChannel>
            <StreamingChannel>
              <id>104</id><channelName>104</channelName><enabled>true</enabled>
              <Video><enabled>true</enabled><vbrUpperCap>512</vbrUpperCap></Video>
            </StreamingChannel>
            <StreamingChannel>
              <id>201</id><channelName>201</channelName><enabled>true</enabled>
              <Video>
                <enabled>false</enabled>
                <videoCodecType>H.264</videoCodecType>
                <videoQualityControlType>CBR</videoQualityControlType>
                <constantBitRate>4096</constantBitRate>
                <maxFrameRate>1500</maxFrameRate>
              </Video>
            </StreamingChannel>
            </StreamingChannelList>
            """));
        using var client = new HikvisionClient(Conn, handler);

        var streams = await client.GetMainStreamsAsync();

        Assert.Equal("/ISAPI/Streaming/channels", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        // 102 (sub) and 104 (third) are recording nothing; only x01 tracks survive.
        Assert.Equal(2, streams.Count);

        var ch1 = streams[0];
        Assert.Equal(1, ch1.Channel);
        Assert.Equal(101, ch1.TrackId);
        Assert.True(ch1.Enabled);
        Assert.Equal("H.265", ch1.CodecType);
        Assert.Equal("4256x1888", ch1.Resolution);
        Assert.Equal(20.0, ch1.FrameRateFps);
        Assert.True(ch1.IsVbr);
        Assert.Equal(5120, ch1.MaxBitrateKbps);

        var ch2 = streams[1];
        Assert.Equal(2, ch2.Channel);
        Assert.False(ch2.Enabled); // Video disabled
        Assert.False(ch2.IsVbr);
        Assert.Equal(4096, ch2.MaxBitrateKbps); // CBR reads constantBitRate
        Assert.Equal(15.0, ch2.FrameRateFps);
        Assert.False(ch2.FrameRateIsFull);
        Assert.Equal("15.0", ch2.FrameRateText);
    }

    // The lab recorder, 2026-09-02: 6 of 9 cameras send maxFrameRate 0 — the web UI's "Full Frame
    // Rate" — and the channel capabilities' opt list says what that resolves to.
    private static string FullRateChannelsXml(string ns) => $"""
        <StreamingChannelList version="2.0" xmlns="{ns}">
        <StreamingChannel>
          <id>101</id><channelName>101</channelName><enabled>true</enabled>
          <Video>
            <enabled>true</enabled>
            <videoCodecType>H.265</videoCodecType>
            <videoResolutionWidth>2688</videoResolutionWidth>
            <videoResolutionHeight>1520</videoResolutionHeight>
            <videoQualityControlType>VBR</videoQualityControlType>
            <vbrUpperCap>8192</vbrUpperCap>
            <maxFrameRate>0</maxFrameRate>
          </Video>
        </StreamingChannel>
        <StreamingChannel>
          <id>201</id><channelName>201</channelName><enabled>true</enabled>
          <Video>
            <enabled>true</enabled>
            <videoCodecType>H.265</videoCodecType>
            <videoResolutionWidth>4096</videoResolutionWidth>
            <videoResolutionHeight>1840</videoResolutionHeight>
            <videoQualityControlType>VBR</videoQualityControlType>
            <vbrUpperCap>5120</vbrUpperCap>
            <maxFrameRate>0</maxFrameRate>
          </Video>
        </StreamingChannel>
        <StreamingChannel>
          <id>601</id><channelName>601</channelName><enabled>true</enabled>
          <Video>
            <enabled>true</enabled>
            <videoCodecType>H.265</videoCodecType>
            <videoResolutionWidth>2592</videoResolutionWidth>
            <videoResolutionHeight>1944</videoResolutionHeight>
            <videoQualityControlType>VBR</videoQualityControlType>
            <vbrUpperCap>6144</vbrUpperCap>
            <maxFrameRate>2500</maxFrameRate>
          </Video>
        </StreamingChannel>
        </StreamingChannelList>
        """;

    [Theory]
    [InlineData(Ns)]
    [InlineData(IsapiNs)]
    public async Task MainStreams_FullFrameRate_ResolvesFromChannelCapabilities(string ns)
    {
        var handler = new MockHttpHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/ISAPI/Streaming/channels" => MockHttpHandler.Xml(FullRateChannelsXml(ns)),
            "/ISAPI/ContentMgmt/record/tracks" => new HttpResponseMessage(HttpStatusCode.NotFound),
            // The opt list is resolution-aware: 2688-wide offers 30 fps, 4096-wide tops at 20.
            "/ISAPI/Streaming/channels/101/capabilities" => MockHttpHandler.Xml($"""
                <StreamingChannel version="2.0" xmlns="{ns}"><Video>
                <videoResolutionWidth opt="1280,1920,2304,2560,2688">2688</videoResolutionWidth>
                <maxFrameRate opt="0,3000,2500,2200,2000,1800,1600,1500,1200,1000,800,600,400,200,100,50,25,12,6">0</maxFrameRate>
                </Video></StreamingChannel>
                """),
            "/ISAPI/Streaming/channels/201/capabilities" => MockHttpHandler.Xml($"""
                <StreamingChannel version="2.0" xmlns="{ns}"><Video>
                <maxFrameRate opt="0,2000,1800,1600,1500,1200,1000,800,600,400,200,100,50,25,12,6">0</maxFrameRate>
                </Video></StreamingChannel>
                """),
            _ => throw new Xunit.Sdk.XunitException(
                $"unexpected request {req.RequestUri.AbsolutePath}"),
        });
        using var client = new HikvisionClient(Conn, handler);

        var streams = await client.GetMainStreamsAsync();

        Assert.Equal(3, streams.Count);
        Assert.Equal(30.0, streams[0].FrameRateFps);
        Assert.True(streams[0].FrameRateIsFull);
        Assert.Equal("30.0 (full)", streams[0].FrameRateText);
        Assert.Equal(20.0, streams[1].FrameRateFps);
        Assert.True(streams[1].FrameRateIsFull);
        // An explicit rate never costs a capabilities round trip.
        Assert.Equal(25.0, streams[2].FrameRateFps);
        Assert.False(streams[2].FrameRateIsFull);
        Assert.Equal(4, handler.Requests.Count); // channels, tracks, two capabilities
        Assert.DoesNotContain(handler.Requests,
            r => r.Request.RequestUri!.AbsolutePath == "/ISAPI/Streaming/channels/601/capabilities");
    }

    [Fact]
    public async Task MainStreams_FullFrameRate_CapabilitiesUnavailable_StaysUnknownButFlagged()
    {
        var handler = new MockHttpHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/ISAPI/Streaming/channels" => MockHttpHandler.Xml(FullRateChannelsXml(IsapiNs)),
            "/ISAPI/ContentMgmt/record/tracks" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/ISAPI/Streaming/channels/101/capabilities" => new HttpResponseMessage(
                HttpStatusCode.NotFound),
            // Capabilities without an opt list resolve nothing either.
            "/ISAPI/Streaming/channels/201/capabilities" => MockHttpHandler.Xml($"""
                <StreamingChannel version="2.0" xmlns="{IsapiNs}"><Video>
                <maxFrameRate>0</maxFrameRate>
                </Video></StreamingChannel>
                """),
            _ => throw new Xunit.Sdk.XunitException(
                $"unexpected request {req.RequestUri.AbsolutePath}"),
        });
        using var client = new HikvisionClient(Conn, handler);

        var streams = await client.GetMainStreamsAsync();

        Assert.Null(streams[0].FrameRateFps);
        Assert.True(streams[0].FrameRateIsFull);
        Assert.Equal("", streams[0].FrameRateText);
        Assert.Null(streams[1].FrameRateFps);
        Assert.True(streams[1].FrameRateIsFull);
        Assert.Equal(25.0, streams[2].FrameRateFps);
    }

    [Theory]
    [InlineData(Ns)]
    [InlineData(IsapiNs)]
    public async Task FindOldestRecording_AsksForOneResult_ParsesEarliestStart(string ns)
    {
        var handler = new MockHttpHandler((req, body) =>
        {
            Assert.Equal("/ISAPI/ContentMgmt/search", req.RequestUri!.AbsolutePath);
            Assert.Contains("<trackID>301</trackID>", body);
            Assert.Contains("<maxResults>1</maxResults>", body);
            Assert.Contains("<searchResultPostion>0</searchResultPostion>", body);
            return MockHttpHandler.Xml($"""
                <CMSearchResult version="2.0" xmlns="{ns}">
                <searchID>X</searchID>
                <responseStatus>true</responseStatus>
                <responseStatusStrg>MORE</responseStatusStrg>
                <totalMatches>276</totalMatches>
                <numOfMatches>276</numOfMatches>
                <matchList>
                <searchMatchItem>
                  <trackID>301</trackID>
                  <timeSpan>
                    <startTime>2026-08-08T12:48:44Z</startTime>
                    <endTime>2026-08-08T13:45:53Z</endTime>
                  </timeSpan>
                </searchMatchItem>
                </matchList>
                </CMSearchResult>
                """);
        });
        using var client = new HikvisionClient(Conn, handler);

        var oldest = await client.FindOldestRecordingAsync(3);

        Assert.Equal(new DateTime(2026, 8, 8, 12, 48, 44), oldest);
        // Wall-clock time as the device reports it, never coerced to UTC.
        Assert.Equal(DateTimeKind.Unspecified, oldest!.Value.Kind);
    }

    [Fact]
    public async Task FindOldestRecording_NoMatches_ReturnsNull()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Xml($"""
            <CMSearchResult version="2.0" xmlns="{Ns}">
            <searchID>X</searchID>
            <responseStatus>true</responseStatus>
            <responseStatusStrg>NO MATCHES</responseStatusStrg>
            <numOfMatches>0</numOfMatches>
            <matchList/>
            </CMSearchResult>
            """));
        using var client = new HikvisionClient(Conn, handler);

        Assert.Null(await client.FindOldestRecordingAsync(1));
    }

    // ----- calendar fallback (DS-7716NI-I4/16P V4.61.030 rejects the everything window) -----

    private const string WideWindowRejected = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ResponseStatus version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
        <statusCode>3</statusCode>
        <statusString>Device Error</statusString>
        <subStatusCode>deviceError</subStatusCode>
        <subStatusString>Tag 13 is invalid (two root tags)</subStatusString>
        </ResponseStatus>
        """;

    private static readonly DateTime FallbackNow = new(2026, 9, 2, 8, 0, 0);

    private static (int Year, int Month) CalendarMonth(string body)
    {
        var doc = System.Xml.Linq.XDocument.Parse(body);
        return (int.Parse(doc.Root!.Element("year")!.Value),
                int.Parse(doc.Root.Element("monthOfYear")!.Value));
    }

    private static HttpResponseMessage CalendarXml(int year, int month, params int[] recordedDays)
    {
        var days = string.Join("", Enumerable.Range(1, DateTime.DaysInMonth(year, month))
            .Select(d => recordedDays.Contains(d)
                ? $"<day><id>{d}</id><dayOfMonth>{d}</dayOfMonth><record>true</record><recordType>event</recordType></day>"
                : $"<day><id>{d}</id><dayOfMonth>{d}</dayOfMonth><record>false</record></day>"));
        return MockHttpHandler.Xml($"""
            <trackDailyDistribution version="2.0" xmlns="{IsapiNs}">
            <dayList>{days}</dayList>
            </trackDailyDistribution>
            """);
    }

    private static HttpResponseMessage SearchResult(string? firstStart) => MockHttpHandler.Xml(
        firstStart is null
            ? $"""
              <CMSearchResult version="2.0" xmlns="{IsapiNs}">
              <searchID>X</searchID><responseStatus>true</responseStatus>
              <responseStatusStrg>NO MATCHES</responseStatusStrg><numOfMatches>0</numOfMatches>
              <matchList/>
              </CMSearchResult>
              """
            : $"""
              <CMSearchResult version="2.0" xmlns="{IsapiNs}">
              <searchID>X</searchID><responseStatus>true</responseStatus>
              <responseStatusStrg>MORE</responseStatusStrg><numOfMatches>188</numOfMatches>
              <matchList><searchMatchItem><trackID>301</trackID>
              <timeSpan><startTime>{firstStart}</startTime><endTime>{firstStart}</endTime></timeSpan>
              </searchMatchItem></matchList>
              </CMSearchResult>
              """);

    private static bool IsCalendar(HttpRequestMessage req) =>
        req.RequestUri!.AbsolutePath.EndsWith("/dailyDistribution");

    private static bool IsSearch(HttpRequestMessage req) =>
        req.RequestUri!.AbsolutePath == "/ISAPI/ContentMgmt/search";

    [Fact]
    public async Task FindOldestRecording_WideWindowRejected_WalksCalendarThenSearchesThatDay()
    {
        var handler = new MockHttpHandler((req, body) =>
        {
            if (IsSearch(req))
            {
                if (body.Contains("2000-01-01T00:00:00Z"))
                    return MockHttpHandler.Xml(WideWindowRejected, HttpStatusCode.InternalServerError);
                Assert.Contains("<startTime>2025-12-31T00:00:00Z</startTime>", body);
                Assert.Contains("<endTime>2026-01-01T00:00:00Z</endTime>", body);
                Assert.Contains("<maxResults>1</maxResults>", body);
                return SearchResult("2025-12-31T11:10:42Z");
            }
            Assert.Equal("/ISAPI/ContentMgmt/record/tracks/301/dailyDistribution",
                req.RequestUri!.AbsolutePath);
            var (y, m) = CalendarMonth(body);
            // Footage from 31 Dec 2025 to today; nothing older.
            return (y, m) switch
            {
                (2026, _) => CalendarXml(y, m, 1, 2),
                (2025, 12) => CalendarXml(y, m, 31),
                _ => CalendarXml(y, m),
            };
        });
        using var client = new HikvisionClient(Conn, handler) { Clock = () => FallbackNow };

        var oldest = await client.FindOldestRecordingAsync(3);

        Assert.Equal(new DateTime(2025, 12, 31, 11, 10, 42), oldest);
        Assert.Equal(DateTimeKind.Unspecified, oldest!.Value.Kind);
        var calendar = handler.Requests.Where(r => IsCalendar(r.Request)).ToList();
        // Sep 2026 back to Dec 2025 (10 months) plus six empty months to be sure.
        Assert.Equal(16, calendar.Count);
        Assert.Equal((2026, 9), CalendarMonth(calendar[0].Body));
        Assert.Equal((2025, 6), CalendarMonth(calendar[^1].Body));
        Assert.Equal(2, handler.Requests.Count(r => IsSearch(r.Request)));
    }

    [Fact]
    public async Task FindOldestRecording_Fallback_ToleratesGaps_AndUsesMidnightWhenDaySearchIsEmpty()
    {
        var handler = new MockHttpHandler((req, body) =>
        {
            if (IsSearch(req))
                return body.Contains("2000-01-01T00:00:00Z")
                    ? MockHttpHandler.Xml(WideWindowRejected, HttpStatusCode.InternalServerError)
                    : SearchResult(null);
            var (y, m) = CalendarMonth(body);
            // Camera offline May–Aug 2026 (four empty months inside the held range), footage on
            // 15 Apr 2026 and nothing before.
            return (y, m) switch
            {
                (2026, 9) => CalendarXml(y, m, 1),
                (2026, 4) => CalendarXml(y, m, 15, 16),
                _ => CalendarXml(y, m),
            };
        });
        using var client = new HikvisionClient(Conn, handler) { Clock = () => FallbackNow };

        var oldest = await client.FindOldestRecordingAsync(1);

        Assert.Equal(new DateTime(2026, 4, 15), oldest);
        Assert.Equal(DateTimeKind.Unspecified, oldest!.Value.Kind);
    }

    [Fact]
    public async Task FindOldestRecording_Fallback_RetriesDaySearchOnce()
    {
        int daySearches = 0;
        var handler = new MockHttpHandler((req, body) =>
        {
            if (IsSearch(req))
            {
                if (body.Contains("2000-01-01T00:00:00Z"))
                    return MockHttpHandler.Xml(WideWindowRejected, HttpStatusCode.InternalServerError);
                return ++daySearches == 1
                    ? MockHttpHandler.Xml(WideWindowRejected, HttpStatusCode.InternalServerError)
                    : SearchResult("2026-09-01T06:00:00Z");
            }
            var (y, m) = CalendarMonth(body);
            return (y, m) == (2026, 9) ? CalendarXml(y, m, 1) : CalendarXml(y, m);
        });
        using var client = new HikvisionClient(Conn, handler) { Clock = () => FallbackNow };

        Assert.Equal(new DateTime(2026, 9, 1, 6, 0, 0), await client.FindOldestRecordingAsync(1));
        Assert.Equal(2, daySearches);
    }

    [Fact]
    public async Task FindOldestRecording_Fallback_NoRecordedDays_ReturnsNull()
    {
        var handler = new MockHttpHandler((req, body) =>
            IsSearch(req)
                ? MockHttpHandler.Xml(WideWindowRejected, HttpStatusCode.InternalServerError)
                : CalendarXml(CalendarMonth(body).Year, CalendarMonth(body).Month));
        using var client = new HikvisionClient(Conn, handler) { Clock = () => FallbackNow };

        Assert.Null(await client.FindOldestRecordingAsync(1));
        // 36 empty months from today, then give up; no second search.
        Assert.Equal(36, handler.Requests.Count(r => IsCalendar(r.Request)));
        Assert.Equal(1, handler.Requests.Count(r => IsSearch(r.Request)));
    }

    [Fact]
    public async Task FindOldestRecording_Fallback_CalendarUnsupported_ReportsBothFailures()
    {
        var handler = new MockHttpHandler((req, _) =>
            IsSearch(req)
                ? MockHttpHandler.Xml(WideWindowRejected, HttpStatusCode.InternalServerError)
                : MockHttpHandler.Text("Not Found", HttpStatusCode.NotFound));
        using var client = new HikvisionClient(Conn, handler) { Clock = () => FallbackNow };

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.FindOldestRecordingAsync(1));

        Assert.Contains("500", ex.Message);
        Assert.Contains("calendar fallback also failed", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task FindOldestRecording_Unauthorized_DoesNotFallBack()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Text("Unauthorized", HttpStatusCode.Unauthorized));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.FindOldestRecordingAsync(1));

        Assert.Equal(401, ex.StatusCode);
        Assert.Single(handler.Requests); // no calendar walk burning login attempts
    }

    [Fact]
    public async Task BitrateRange_ReadsMinMaxAttributes()
    {
        var handler = new MockHttpHandler((req, _) =>
        {
            Assert.Equal("/ISAPI/Streaming/channels/101/capabilities",
                req.RequestUri!.AbsolutePath);
            return MockHttpHandler.Xml($"""
                <StreamingChannel version="2.0" xmlns="{Ns}">
                <id>101</id>
                <Video>
                  <videoQualityControlType opt="CBR,VBR">VBR</videoQualityControlType>
                  <vbrUpperCap min="32" max="16384">5120</vbrUpperCap>
                </Video>
                </StreamingChannel>
                """);
        });
        using var client = new HikvisionClient(Conn, handler);

        var range = await client.GetBitrateRangeAsync(1);

        Assert.Equal(new BitrateRange(32, 16384), range);
    }

    [Fact]
    public async Task BitrateRange_UnsupportedEndpoint_ReturnsNull()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Text("Not Found", HttpStatusCode.NotFound));
        using var client = new HikvisionClient(Conn, handler);

        Assert.Null(await client.GetBitrateRangeAsync(1));
    }

    [Fact]
    public async Task SetMaxBitrate_RoundTripsDocument_AndReturnsReadBack()
    {
        string channelXml(int kbps) => $"""
            <StreamingChannel version="2.0" xmlns="{Ns}">
            <id>101</id><channelName>cam</channelName><enabled>true</enabled>
            <Video>
              <enabled>true</enabled>
              <videoQualityControlType>VBR</videoQualityControlType>
              <vbrUpperCap>{kbps}</vbrUpperCap>
              <maxFrameRate>2000</maxFrameRate>
            </Video>
            </StreamingChannel>
            """;

        int gets = 0;
        var handler = new MockHttpHandler((req, body) =>
        {
            Assert.Equal("/ISAPI/Streaming/channels/101", req.RequestUri!.AbsolutePath);
            if (req.Method == HttpMethod.Get)
            {
                gets++;
                // First GET: current config. Second GET: the read-back — the camera
                // snapped 4096 down to its own step, 4000.
                return MockHttpHandler.Xml(channelXml(gets == 1 ? 5120 : 4000));
            }

            Assert.Equal(HttpMethod.Put, req.Method);
            // The PUT round-trips the device's own document with only the cap changed.
            Assert.Contains("<vbrUpperCap>4096</vbrUpperCap>", body);
            Assert.Contains("<maxFrameRate>2000</maxFrameRate>", body);
            Assert.Contains(Ns, body);
            return MockHttpHandler.Xml($"""
                <ResponseStatus version="2.0" xmlns="{Ns}">
                <requestURL>/ISAPI/Streaming/channels/101</requestURL>
                <statusCode>1</statusCode>
                <statusString>OK</statusString>
                </ResponseStatus>
                """);
        });
        using var client = new HikvisionClient(Conn, handler);

        int actual = await client.SetMaxBitrateAsync(1, 4096);

        // The caller learns what stuck, not what was asked.
        Assert.Equal(4000, actual);
        Assert.Equal(3, handler.Requests.Count); // GET, PUT, verify GET
    }

    [Fact]
    public async Task SetMaxBitrate_RejectedStatusCode_Throws()
    {
        var handler = new MockHttpHandler((req, _) => req.Method == HttpMethod.Get
            ? MockHttpHandler.Xml($"""
                <StreamingChannel version="2.0" xmlns="{Ns}">
                <id>101</id>
                <Video>
                  <videoQualityControlType>VBR</videoQualityControlType>
                  <vbrUpperCap>5120</vbrUpperCap>
                </Video>
                </StreamingChannel>
                """)
            : MockHttpHandler.Xml($"""
                <ResponseStatus version="2.0" xmlns="{Ns}">
                <statusCode>4</statusCode>
                <statusString>Invalid Operation</statusString>
                </ResponseStatus>
                """));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.SetMaxBitrateAsync(1, 4096));
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task SetMaxBitrate_NoBitrateField_ThrowsWithoutWriting()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Xml($"""
            <StreamingChannel version="2.0" xmlns="{Ns}">
            <id>101</id>
            <Video><enabled>true</enabled></Video>
            </StreamingChannel>
            """));
        using var client = new HikvisionClient(Conn, handler);

        await Assert.ThrowsAsync<NvrException>(() => client.SetMaxBitrateAsync(1, 4096));
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Request.Method));
    }
}
