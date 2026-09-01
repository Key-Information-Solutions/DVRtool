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
