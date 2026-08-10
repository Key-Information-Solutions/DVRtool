using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

public class HikvisionClientTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss:word",
    };

    private const string Ns = "http://www.hikvision.com/ver20/XMLSchema";

    [Fact]
    public async Task DeviceInfo_ParsesNamespacedXml()
    {
        var handler = new MockHttpHandler((req, _) => MockHttpHandler.Xml($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <DeviceInfo xmlns="{Ns}" version="2.0">
              <deviceName>Front Office NVR</deviceName>
              <model>DS-7616NI-Q2</model>
              <serialNumber>DS-7616NI-Q216202301</serialNumber>
              <firmwareVersion>V4.62.210</firmwareVersion>
            </DeviceInfo>
            """));
        using var client = new HikvisionClient(Conn, handler);

        var info = await client.GetDeviceInfoAsync();

        Assert.Equal("Front Office NVR", info.Name);
        Assert.Equal("DS-7616NI-Q2", info.Model);
        Assert.Equal("DS-7616NI-Q216202301", info.SerialNumber);
        Assert.Equal("V4.62.210", info.FirmwareVersion);
        Assert.Equal("/ISAPI/System/deviceInfo", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Search_PaginatesOnMore_AndParsesSegments()
    {
        int searchCalls = 0;
        var handler = new MockHttpHandler((req, body) =>
        {
            Assert.Equal("/ISAPI/ContentMgmt/search", req.RequestUri!.AbsolutePath);
            Assert.Contains("<trackID>301</trackID>", body);
            Assert.Contains("searchResultPostion", body); // Hikvision's own misspelling
            searchCalls++;
            if (searchCalls == 1)
            {
                Assert.Contains("<searchResultPostion>0</searchResultPostion>", body);
                return MockHttpHandler.Xml($"""
                    <CMSearchResult xmlns="{Ns}" version="2.0">
                      <searchID>X</searchID>
                      <responseStatus>true</responseStatus>
                      <responseStatusStrg>MORE</responseStatusStrg>
                      <numOfMatches>2</numOfMatches>
                      <matchList>
                        <searchMatchItem>
                          <sourceID>S1</sourceID><trackID>301</trackID>
                          <timeSpan>
                            <startTime>2026-07-21T08:00:00Z</startTime>
                            <endTime>2026-07-21T08:59:59Z</endTime>
                          </timeSpan>
                          <mediaSegmentDescriptor>
                            <contentType>video</contentType>
                            <playbackURI>rtsp://192.0.2.10/Streaming/tracks/301?starttime=20260721T080000Z&amp;endtime=20260721T085959Z&amp;name=00000001&amp;size=1048576</playbackURI>
                          </mediaSegmentDescriptor>
                          <metadataMatches>
                            <metadataDescriptor>recordType.meta.std-cgi.com/timing</metadataDescriptor>
                          </metadataMatches>
                        </searchMatchItem>
                        <searchMatchItem>
                          <sourceID>S1</sourceID><trackID>301</trackID>
                          <timeSpan>
                            <startTime>2026-07-21T09:00:00Z</startTime>
                            <endTime>2026-07-21T09:30:00Z</endTime>
                          </timeSpan>
                          <mediaSegmentDescriptor>
                            <contentType>video</contentType>
                            <playbackURI>rtsp://x/2</playbackURI>
                          </mediaSegmentDescriptor>
                          <metadataMatches>
                            <metadataDescriptor>recordType.meta.std-cgi.com/motion</metadataDescriptor>
                          </metadataMatches>
                        </searchMatchItem>
                      </matchList>
                    </CMSearchResult>
                    """);
            }
            Assert.Contains("<searchResultPostion>2</searchResultPostion>", body);
            return MockHttpHandler.Xml($"""
                <CMSearchResult xmlns="{Ns}" version="2.0">
                  <searchID>X</searchID>
                  <responseStatus>true</responseStatus>
                  <responseStatusStrg>OK</responseStatusStrg>
                  <numOfMatches>1</numOfMatches>
                  <matchList>
                    <searchMatchItem>
                      <sourceID>S1</sourceID><trackID>301</trackID>
                      <timeSpan>
                        <startTime>2026-07-21T09:30:00Z</startTime>
                        <endTime>2026-07-21T10:00:00Z</endTime>
                      </timeSpan>
                      <mediaSegmentDescriptor>
                        <contentType>video</contentType>
                        <playbackURI>rtsp://x/3</playbackURI>
                      </mediaSegmentDescriptor>
                    </searchMatchItem>
                  </matchList>
                </CMSearchResult>
                """);
        });
        using var client = new HikvisionClient(Conn, handler);

        var segments = await client.SearchAsync(3,
            new DateTime(2026, 7, 21, 0, 0, 0), new DateTime(2026, 7, 22, 0, 0, 0));

        Assert.Equal(2, searchCalls);
        Assert.Equal(3, segments.Count);
        Assert.Equal(new DateTime(2026, 7, 21, 8, 0, 0), segments[0].Start);
        Assert.Equal(new DateTime(2026, 7, 21, 8, 59, 59), segments[0].End);
        Assert.Equal(RecordingType.Continuous, segments[0].Type);
        Assert.Equal(RecordingType.Motion, segments[1].Type);
        Assert.StartsWith("rtsp://", segments[0].NativeId);
        Assert.All(segments, s => Assert.Equal(3, s.Channel));
        Assert.All(segments, s => Assert.Equal(DateTimeKind.Unspecified, s.Start.Kind));
    }

    [Fact]
    public void LiveAndPlaybackUris_UseIsapiConventions()
    {
        using var client = new HikvisionClient(Conn, new MockHttpHandler((_, _) =>
            MockHttpHandler.Text("")));

        Assert.Equal("rtsp://192.0.2.10:554/Streaming/Channels/501",
            client.GetLiveUri(5).ToString());
        Assert.Equal("rtsp://192.0.2.10:554/Streaming/Channels/502",
            client.GetLiveUri(5, StreamType.Sub).ToString());

        var uri = client.GetPlaybackUri(5,
            new DateTime(2026, 7, 21, 8, 0, 0), new DateTime(2026, 7, 21, 9, 0, 0));
        Assert.Equal(
            "rtsp://192.0.2.10:554/Streaming/tracks/501?starttime=20260721T080000Z&endtime=20260721T090000Z",
            uri.ToString());

        // Credentials must be URL-escaped when embedded.
        var withCreds = client.GetLiveUri(1, includeCredentials: true);
        Assert.Contains("admin:p%40ss%3Aword@", withCreds.ToString());
    }

    [Fact]
    public async Task Download_FallsBackFromGetToPost()
    {
        byte[] payload = new byte[128];
        Random.Shared.NextBytes(payload);
        var handler = new MockHttpHandler((req, body) =>
        {
            Assert.Contains("&amp;", body); // playbackURI must be XML-escaped
            if (req.Method == HttpMethod.Get)
                return MockHttpHandler.Xml("<ResponseStatus><statusCode>4</statusCode></ResponseStatus>",
                    System.Net.HttpStatusCode.BadRequest);
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            ok.Content.Headers.ContentType = new("application/octet-stream");
            return ok;
        });
        using var client = new HikvisionClient(Conn, handler);

        string dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            long reported = 0;
            await client.DownloadAsync(3,
                new DateTime(2026, 7, 21, 8, 0, 0), new DateTime(2026, 7, 21, 8, 5, 0),
                dest, new SynchronousProgress(v => reported = v));

            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
            Assert.Equal(payload.Length, reported);
            Assert.Equal(2, handler.Requests.Count); // GET failed → POST succeeded
            Assert.False(File.Exists(dest + ".part")); // temp promoted, not left behind
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task Download_EmptyGetStream_FallsBackToPost()
    {
        byte[] payload = new byte[64];
        Random.Shared.NextBytes(payload);
        var handler = new MockHttpHandler((req, _) =>
        {
            // Firmware that ignores GET-with-body: 200 with an empty video stream.
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(req.Method == HttpMethod.Get ? [] : payload),
            };
            ok.Content.Headers.ContentType = new("application/octet-stream");
            return ok;
        });
        using var client = new HikvisionClient(Conn, handler);

        string dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await client.DownloadAsync(3,
                new DateTime(2026, 7, 21, 8, 0, 0), new DateTime(2026, 7, 21, 8, 5, 0), dest);

            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
            Assert.Equal(2, handler.Requests.Count); // empty GET → POST retried
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task Download_Failure_LeavesNoPartialFile()
    {
        var handler = new MockHttpHandler((_, _) =>
        {
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            ok.Content.Headers.ContentType = new("application/octet-stream");
            return ok;
        });
        using var client = new HikvisionClient(Conn, handler);

        string dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ex = await Assert.ThrowsAsync<NvrException>(() => client.DownloadAsync(3,
            new DateTime(2026, 7, 21, 8, 0, 0), new DateTime(2026, 7, 21, 8, 5, 0), dest));

        Assert.Contains("empty stream", ex.Message);
        Assert.False(File.Exists(dest));           // never truncate/clobber the destination
        Assert.False(File.Exists(dest + ".part")); // temp cleaned up
    }

    [Fact]
    public async Task Search_PaginationCapWithMorePending_Throws()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Xml($"""
            <CMSearchResult xmlns="{Ns}" version="2.0">
              <searchID>X</searchID>
              <responseStatus>true</responseStatus>
              <responseStatusStrg>MORE</responseStatusStrg>
              <numOfMatches>1</numOfMatches>
              <matchList>
                <searchMatchItem>
                  <sourceID>S1</sourceID><trackID>301</trackID>
                  <timeSpan>
                    <startTime>2026-07-21T08:00:00Z</startTime>
                    <endTime>2026-07-21T08:05:00Z</endTime>
                  </timeSpan>
                </searchMatchItem>
              </matchList>
            </CMSearchResult>
            """));
        using var client = new HikvisionClient(Conn, handler);

        // Firmware that answers MORE forever must fail loudly, not return a
        // silently truncated list.
        var ex = await Assert.ThrowsAsync<NvrException>(() => client.SearchAsync(3,
            new DateTime(2026, 7, 21, 0, 0, 0), new DateTime(2026, 7, 22, 0, 0, 0)));
        Assert.Contains("pagination safety cap", ex.Message);
    }

    [Fact]
    public async Task GetChannels_AuthFailure_Throws()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Xml("<ResponseStatus><statusCode>4</statusCode></ResponseStatus>",
                System.Net.HttpStatusCode.Unauthorized));
        using var client = new HikvisionClient(Conn, handler);

        // A wrong password must surface as 401, not an empty channel list.
        var ex = await Assert.ThrowsAsync<NvrException>(() => client.GetChannelsAsync());
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task GetChannels_NonIsapiDevice_Throws()
    {
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Text("this is not xml — some other vendor's login page"));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() => client.GetChannelsAsync());
        Assert.Contains("non-XML", ex.Message);
    }

    [Fact]
    public async Task GetChannels_UnsupportedEndpoints_ReturnsEmpty()
    {
        // 404 on every probe = a device that simply lacks these endpoints; that is
        // a legitimate empty result, not an error.
        var handler = new MockHttpHandler((_, _) =>
            MockHttpHandler.Xml("<ResponseStatus/>", System.Net.HttpStatusCode.NotFound));
        using var client = new HikvisionClient(Conn, handler);

        var channels = await client.GetChannelsAsync();
        Assert.Empty(channels);
    }

    [Theory]
    [InlineData("2026-07-21T08:00:00Z", 2026, 7, 21, 8, 0, 0)]
    [InlineData("2026-07-21T08:00:00+02:00", 2026, 7, 21, 8, 0, 0)] // wall clock preserved
    public void ParseIsapiTime_KeepsWallClock(string input, int y, int mo, int d, int h, int mi, int s)
    {
        var t = HikvisionClient.ParseIsapiTime(input);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), t);
        Assert.Equal(DateTimeKind.Unspecified, t.Kind);
    }

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
