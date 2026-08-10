using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using Xunit;

namespace DVRTool.Tests;

public class DahuaClientTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "10.0.0.50",
        Username = "admin",
        Password = "secret",
    };

    [Fact]
    public async Task Search_DrivesFinderLifecycle_AndParsesItems()
    {
        var paths = new List<string>();
        int findNextCalls = 0;
        var handler = new MockHttpHandler((req, _) =>
        {
            string pathAndQuery = req.RequestUri!.PathAndQuery;
            paths.Add(pathAndQuery);
            if (pathAndQuery.Contains("factory.create"))
                return MockHttpHandler.Text("result=12345\r\n");
            if (pathAndQuery.Contains("action=findFile"))
            {
                // Display channel 2 → CGI 0-based channel 1.
                Assert.Contains("condition.Channel=1", pathAndQuery);
                Assert.Contains("condition.StartTime=2026-07-21%2000%3A00%3A00", pathAndQuery);
                return MockHttpHandler.Text("OK\r\n");
            }
            if (pathAndQuery.Contains("findNextFile"))
            {
                // A short page must NOT end pagination — only found=0 does.
                findNextCalls++;
                if (findNextCalls > 1)
                    return MockHttpHandler.Text("found=0\r\n");
                return MockHttpHandler.Text("""
                    found=3
                    items[0].Channel=1
                    items[0].StartTime=2026-07-21 08:00:00
                    items[0].EndTime=2026-07-21 09:00:00
                    items[0].FilePath=/mnt/dvr/2026-07-21/0/dav/08/08.00.00-09.00.00[R][0@0][0].dav
                    items[0].Length=734003200
                    items[0].Type=dav
                    items[0].Flags[0]=Timing
                    items[1].Channel=1
                    items[1].StartTime=2026-07-21 09:00:00
                    items[1].EndTime=2026-07-21 09:12:34
                    items[1].FilePath=/mnt/dvr/x2.dav
                    items[1].Length=1048576
                    items[1].Type=dav
                    items[1].Events[0]=VideoMotion
                    items[2].Channel=1
                    items[2].StartTime=2026-07-21 10:00:00
                    items[2].EndTime=2026-07-21 10:05:00
                    items[2].FilePath=/mnt/dvr/x3.dav
                    items[2].Length=2097152
                    items[2].Type=dav
                    items[2].Flags[0]=Manual
                    """);
            }
            return MockHttpHandler.Text("OK\r\n"); // close / destroy
        });
        using var client = new DahuaClient(Conn, handler);

        var segments = await client.SearchAsync(2,
            new DateTime(2026, 7, 21, 0, 0, 0), new DateTime(2026, 7, 22, 0, 0, 0));

        Assert.Equal(3, segments.Count);
        Assert.Equal(2, findNextCalls); // short page → one more call until found=0
        Assert.Equal(2, segments[0].Channel); // mapped back to display numbering
        Assert.Equal(new DateTime(2026, 7, 21, 8, 0, 0), segments[0].Start);
        Assert.Equal(734003200L, segments[0].SizeBytes);
        Assert.EndsWith(".dav", segments[0].NativeId);
        // Record modes: Flags=Timing → Continuous, Events=VideoMotion → Motion,
        // Flags=Manual (no Events — Manual lives in the Flags domain) → Manual.
        Assert.Equal(RecordingType.Continuous, segments[0].Type);
        Assert.Equal(RecordingType.Motion, segments[1].Type);
        Assert.Equal(RecordingType.Manual, segments[2].Type);
        // Finder lifecycle: create → findFile → findNextFile → close → destroy.
        Assert.Contains(paths, p => p.Contains("action=close&object=12345"));
        Assert.Contains(paths, p => p.Contains("action=destroy&object=12345"));
    }

    [Fact]
    public async Task Search_Canceled_StillClosesAndDestroysFinder()
    {
        using var cts = new CancellationTokenSource();
        var paths = new List<string>();
        var handler = new MockHttpHandler((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            paths.Add(pq);
            if (pq.Contains("factory.create"))
                return MockHttpHandler.Text("result=99\r\n");
            if (pq.Contains("action=findFile"))
            {
                cts.Cancel(); // user aborts mid-search
                return MockHttpHandler.Text("OK\r\n");
            }
            return MockHttpHandler.Text("OK\r\n");
        });
        using var client = new DahuaClient(Conn, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchAsync(1,
            new DateTime(2026, 7, 21, 0, 0, 0), new DateTime(2026, 7, 22, 0, 0, 0), cts.Token));

        // Cleanup must reach the device despite the canceled caller token —
        // otherwise finder objects leak until the NVR reboots.
        Assert.Contains(paths, p => p.Contains("action=close&object=99"));
        Assert.Contains(paths, p => p.Contains("action=destroy&object=99"));
    }

    [Fact]
    public async Task Search_ErrorOnLaterPage_KeepsParsedResults()
    {
        int findNextCalls = 0;
        var handler = new MockHttpHandler((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (pq.Contains("factory.create"))
                return MockHttpHandler.Text("result=7\r\n");
            if (pq.Contains("action=findFile"))
                return MockHttpHandler.Text("OK\r\n");
            if (pq.Contains("findNextFile"))
            {
                // Some firmware answers the post-exhaustion call with an HTTP error
                // instead of found=0; already-parsed segments must survive.
                findNextCalls++;
                if (findNextCalls > 1)
                    return MockHttpHandler.Text("Error", System.Net.HttpStatusCode.InternalServerError);
                return MockHttpHandler.Text("""
                    found=1
                    items[0].Channel=0
                    items[0].StartTime=2026-07-21 08:00:00
                    items[0].EndTime=2026-07-21 09:00:00
                    items[0].FilePath=/mnt/dvr/a.dav
                    """);
            }
            return MockHttpHandler.Text("OK\r\n");
        });
        using var client = new DahuaClient(Conn, handler);

        var segments = await client.SearchAsync(1,
            new DateTime(2026, 7, 21, 0, 0, 0), new DateTime(2026, 7, 22, 0, 0, 0));

        Assert.Single(segments);
    }

    [Fact]
    public async Task GetChannels_ParsesChannelTitleTable()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Text("""
            table.ChannelTitle[0].Name=Front Door
            table.ChannelTitle[1].Name=Warehouse
            table.ChannelTitle[2].Name=Loading Dock
            """));
        using var client = new DahuaClient(Conn, handler);

        var channels = await client.GetChannelsAsync();

        Assert.Equal(3, channels.Count);
        Assert.Equal(1, channels[0].Id);
        Assert.Equal("Front Door", channels[0].Name);
        Assert.Equal(3, channels[2].Id);
    }

    [Fact]
    public void Uris_UseDahuaConventions()
    {
        using var client = new DahuaClient(Conn, new MockHttpHandler((_, _) =>
            MockHttpHandler.Text("")));

        Assert.Equal("rtsp://10.0.0.50:554/cam/realmonitor?channel=4&subtype=0",
            client.GetLiveUri(4).ToString());
        Assert.Equal(
            "rtsp://10.0.0.50:554/cam/playback?channel=4&starttime=2026_07_21_08_00_00&endtime=2026_07_21_09_00_00",
            client.GetPlaybackUri(4,
                new DateTime(2026, 7, 21, 8, 0, 0), new DateTime(2026, 7, 21, 9, 0, 0)).ToString());
    }

    [Fact]
    public async Task Download_UsesLoadfileCgi_WithEncodedTimes()
    {
        byte[] payload = [0x44, 0x48, 0x41, 0x56, 1, 2, 3, 4]; // "DHAV"...
        var handler = new MockHttpHandler((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            Assert.Contains("/cgi-bin/loadfile.cgi?action=startLoad&channel=4", pq);
            Assert.Contains("startTime=2026-07-21%2008%3A00%3A00", pq);
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            ok.Content.Headers.ContentType = new("application/octet-stream");
            return ok;
        });
        using var client = new DahuaClient(Conn, handler);

        string dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await client.DownloadAsync(4,
                new DateTime(2026, 7, 21, 8, 0, 0), new DateTime(2026, 7, 21, 8, 5, 0), dest);
            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public void ParseKeyValues_HandlesCrLfAndJunk()
    {
        var kv = DahuaClient.ParseKeyValues("a=1\r\nnoequals\r\n b = spaced \r\n");
        Assert.Equal("1", kv["a"]);
        Assert.Equal("spaced", kv["b"]);
        Assert.Equal(2, kv.Count);
    }

    [Theory]
    [InlineData("2026-07-21 08:00:00")] // modern firmware: zero-padded
    [InlineData("2026-7-21 8:00:00")]   // spec's own example form: non-padded
    public void TryParseCgiTime_AcceptsPaddedAndUnpadded(string input)
    {
        Assert.True(DahuaClient.TryParseCgiTime(input, out var t));
        Assert.Equal(new DateTime(2026, 7, 21, 8, 0, 0), t);
        Assert.Equal(DateTimeKind.Unspecified, t.Kind);
    }

    [Fact]
    public async Task Download_EmptyStream_Throws_AndLeavesNoFile()
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
        using var client = new DahuaClient(Conn, handler);

        string dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await Assert.ThrowsAsync<NvrException>(() => client.DownloadAsync(4,
            new DateTime(2026, 7, 21, 8, 0, 0), new DateTime(2026, 7, 21, 8, 5, 0), dest));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }
}
