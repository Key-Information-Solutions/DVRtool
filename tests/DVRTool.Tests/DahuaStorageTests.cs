using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Dahua;
using Xunit;

namespace DVRTool.Tests;

public class DahuaStorageTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "10.0.0.60",
        Username = "admin",
        Password = "secret",
    };

    private const string DeviceAllInfo = """
        list.info[0].Detail[0].IsError=false
        list.info[0].Detail[0].Path=/mnt/dvr/sda0
        list.info[0].Detail[0].TotalBytes=7999997870080
        list.info[0].Detail[0].Type=ReadWrite
        list.info[0].Detail[0].UsedBytes=7999997870080
        list.info[0].Name=/dev/sda
        list.info[0].State=Success
        list.info[1].Detail[0].IsError=true
        list.info[1].Detail[0].Path=/mnt/dvr/sdb0
        list.info[1].Detail[0].TotalBytes=3999997870080
        list.info[1].Detail[0].Type=ReadWrite
        list.info[1].Detail[0].UsedBytes=1000000000000
        list.info[1].Name=/dev/sdb
        list.info[1].State=Failure
        """;

    private const string EncodeConfig = """
        table.Encode[0].ExtraFormat[0].Video.BitRate=512
        table.Encode[0].ExtraFormat[0].Video.BitRateControl=CBR
        table.Encode[0].MainFormat[0].Video.BitRate=4096
        table.Encode[0].MainFormat[0].Video.BitRateControl=VBR
        table.Encode[0].MainFormat[0].Video.Compression=H.265
        table.Encode[0].MainFormat[0].Video.FPS=15
        table.Encode[0].MainFormat[0].Video.Height=1520
        table.Encode[0].MainFormat[0].Video.Quality=4
        table.Encode[0].MainFormat[0].Video.Width=2688
        table.Encode[0].MainFormat[0].Video.resolution=2688x1520
        table.Encode[0].MainFormat[0].VideoEnable=true
        table.Encode[0].MainFormat[1].Video.BitRate=6144
        table.Encode[0].MainFormat[1].Video.BitRateControl=VBR
        table.Encode[1].MainFormat[0].Video.BitRate=2048
        table.Encode[1].MainFormat[0].Video.BitRateControl=CBR
        table.Encode[1].MainFormat[0].Video.Compression=H.264
        table.Encode[1].MainFormat[0].Video.FPS=30
        table.Encode[1].MainFormat[0].Video.resolution=1920x1080
        table.Encode[1].MainFormat[0].VideoEnable=false
        """;

    [Fact]
    public async Task StorageInfo_ParsesDisks_BytesToDecimalMB_FailureIsUnhealthy()
    {
        var handler = new MockHttpHandler((req, _) =>
        {
            Assert.Contains("storageDevice.cgi?action=getDeviceAllInfo", req.RequestUri!.PathAndQuery);
            return MockHttpHandler.Text(DeviceAllInfo);
        });
        using var client = new DahuaClient(Conn, handler);

        var info = await client.GetStorageInfoAsync();

        Assert.Equal(2, info.Hdds.Count);
        Assert.Equal(2, info.InstalledCount);
        Assert.Equal(0, info.GhostBayCount);

        var sda = info.Hdds[0];
        Assert.Equal(1, sda.Id);
        Assert.Equal("/dev/sda", sda.Name);
        Assert.Equal("ok", sda.Status);
        Assert.Equal(7_999_997L, sda.CapacityMB); // bytes ÷ 10^6, like ISAPI's decimal MB
        Assert.Equal(0, sda.FreeSpaceMB);          // full disk in overwrite mode
        Assert.Equal("ReadWrite", sda.Property);

        var sdb = Assert.Single(info.UnhealthyHdds);
        Assert.Equal(2, sdb.Id);
        Assert.Equal("error", sdb.Status);
        Assert.Equal(2_999_997L, sdb.FreeSpaceMB);
        Assert.Equal(7_999_997L + 3_999_997L, info.TotalCapacityMB);
    }

    [Fact]
    public async Task MainStreams_ReadsGeneralRecordStream_MapsTableIndexToDisplayChannel()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.PathAndQuery.Contains("name=RecordMode")
                ? MockHttpHandler.Text("table.RecordMode[0].Mode=0\ntable.RecordMode[1].Mode=0\n")
                : MockHttpHandler.Text(EncodeConfig));
        using var client = new DahuaClient(Conn, handler);

        var streams = await client.GetMainStreamsAsync();

        Assert.Equal(2, streams.Count);
        var ch1 = streams[0];
        Assert.Equal(1, ch1.Channel);
        Assert.True(ch1.Enabled);
        Assert.Equal("H.265", ch1.CodecType);
        Assert.Equal("2688x1520", ch1.Resolution);
        Assert.Equal(15.0, ch1.FrameRateFps);
        Assert.True(ch1.IsVbr);
        Assert.Equal(6144, ch1.MaxBitrateKbps); // worst case over General [0] and Motion [1]
        Assert.Equal(4, ch1.FixedQuality);

        var ch2 = streams[1];
        Assert.Equal(2, ch2.Channel);
        Assert.False(ch2.Enabled);
        Assert.False(ch2.IsVbr);
        Assert.Equal(2048, ch2.MaxBitrateKbps);
        Assert.Equal("1920x1080", ch2.Resolution); // from the resolution string when W/H are absent
    }

    [Fact]
    public async Task MainStreams_RecordModeStop_DisablesTheChannel()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.PathAndQuery.Contains("name=RecordMode")
                ? MockHttpHandler.Text("table.RecordMode[0].Mode=2\ntable.RecordMode[1].Mode=0\n")
                : MockHttpHandler.Text(EncodeConfig));
        using var client = new DahuaClient(Conn, handler);

        var streams = await client.GetMainStreamsAsync();

        Assert.False(streams[0].Enabled); // VideoEnable=true but recording stopped
        Assert.Equal(6144, streams[0].MaxBitrateKbps); // the setting is still reported
    }

    [Fact]
    public async Task MainStreams_WithoutRecordModeTable_TrustsVideoEnable()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.PathAndQuery.Contains("name=RecordMode")
                ? MockHttpHandler.Text("Error\n", HttpStatusCode.BadRequest)
                : MockHttpHandler.Text(EncodeConfig));
        using var client = new DahuaClient(Conn, handler);

        var streams = await client.GetMainStreamsAsync();

        Assert.True(streams[0].Enabled);
    }

    [Fact]
    public async Task StorageInfo_DocumentedListShape_NoNames()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Text("""
            list[0].Detail[0].IsError=false
            list[0].Detail[0].Pointer=27023434
            list[0].Detail[0].TotalBytes=2000398934016
            list[0].Detail[0].Type=ReadWrite
            list[0].Detail[0].UsedBytes=2000398934016
            list[0].Pointer=22347602
            list[0].State=Success
            """));
        using var client = new DahuaClient(Conn, handler);

        var info = await client.GetStorageInfoAsync();

        var disk = Assert.Single(info.Hdds);
        Assert.Equal(1, disk.Id);
        Assert.Equal("disk1", disk.Name);
        Assert.Equal("ok", disk.Status);
        Assert.Equal(2_000_398L, disk.CapacityMB);
    }

    [Fact]
    public async Task BitrateRange_ReadsOwnCapsRow_WhenFirmwareAnswersEveryChannel()
    {
        var handler = new MockHttpHandler((req, _) =>
        {
            Assert.Contains("encode.cgi?action=getConfigCaps&channel=8", req.RequestUri!.PathAndQuery);
            // DH-NVR608H: the channel parameter is ignored; every channel comes back.
            return MockHttpHandler.Text("""
                caps[0].BitRateRange[0]=3
                caps[0].BitRateRange[1]=40960
                caps[0].MainFormat[0].Video.BitRateOptions=256,3584
                caps[0].ExtraFormat[0].Video.BitRateOptions=83,768
                caps[7].MainFormat[0].Video.BitRateOptions=1280,6144
                caps[7].ExtraFormat[0].Video.BitRateOptions=83,768
                """);
        });
        using var client = new DahuaClient(Conn, handler);

        Assert.Equal(new BitrateRange(1280, 6144), await client.GetBitrateRangeAsync(8));
    }

    [Fact]
    public void BitrateRange_FallsBackToHeadMain_ThenSingleRow()
    {
        var head = DahuaClient.ParseKeyValues(
            "headMain.Video.BitRateOptions=448,2560\nheadExtra.Video.BitRateOptions=80,448\n");
        Assert.Equal(new BitrateRange(448, 2560), DahuaClient.ParseBitrateRange(head, 3));

        var single = DahuaClient.ParseKeyValues("caps[0].MainFormat[0].Video.BitRateOptions=512,4096\n");
        Assert.Equal(new BitrateRange(512, 4096), DahuaClient.ParseBitrateRange(single, 3));

        var many = DahuaClient.ParseKeyValues(
            "caps[0].MainFormat[0].Video.BitRateOptions=512,4096\ncaps[1].MainFormat[0].Video.BitRateOptions=256,2048\n");
        Assert.Null(DahuaClient.ParseBitrateRange(many, 3)); // ambiguous: no row for channel 3
    }

    [Fact]
    public async Task StorageInfo_AcceptsFloatByteCounts_SumsPartitions()
    {
        // Verbatim shape from a DH-NVR608H-128-4KS3/I: one disk, four ReadWrite partitions.
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Text("""
            list.info[0].Detail[0].IsError=false
            list.info[0].Detail[0].Path=/dev/sda0
            list.info[0].Detail[0].TotalBytes=2495680086016.000000
            list.info[0].Detail[0].Type=ReadWrite
            list.info[0].Detail[0].UsedBytes=2495680086016.000000
            list.info[0].Detail[1].IsError=false
            list.info[0].Detail[1].Path=/dev/sda1
            list.info[0].Detail[1].TotalBytes=2495677988864.000000
            list.info[0].Detail[1].Type=ReadWrite
            list.info[0].Detail[1].UsedBytes=2495677988864.000000
            list.info[0].HealthDataFlag=0
            list.info[0].Name=/dev/sda
            list.info[0].State=Success
            """));
        using var client = new DahuaClient(Conn, handler);

        var info = await client.GetStorageInfoAsync();

        var disk = Assert.Single(info.Hdds);
        Assert.Equal(4_991_358L, disk.CapacityMB); // (2495680086016 + 2495677988864) / 1e6
        Assert.Equal(0, disk.FreeSpaceMB);
        Assert.Equal("ReadWrite", disk.Property);
    }

    [Fact]
    public async Task FindOldestRecording_AsksForOneItem_ReleasesFinder()
    {
        var paths = new List<string>();
        var handler = new MockHttpHandler((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            paths.Add(pq);
            if (pq.Contains("factory.create"))
                return MockHttpHandler.Text("result=777\r\n");
            if (pq.Contains("action=findFile"))
            {
                Assert.Contains("condition.Channel=3&", pq); // 1-based, as the recorder wants
                Assert.Contains("condition.StartTime=2000-01-01%2000%3A00%3A00", pq);
                return MockHttpHandler.Text("OK\r\n");
            }
            if (pq.Contains("findNextFile"))
            {
                Assert.Contains("count=1", pq);
                return MockHttpHandler.Text("""
                    found=1
                    items[0].Channel=2
                    items[0].StartTime=2026-08-08 12:48:44
                    items[0].EndTime=2026-08-08 13:00:00
                    items[0].FilePath=/mnt/dvr/2026-08-08/2/dav/12/12.48.44-13.00.00[R][0@0][0].dav
                    """);
            }
            return MockHttpHandler.Text("OK\r\n");
        });
        using var client = new DahuaClient(Conn, handler);

        var oldest = await client.FindOldestRecordingAsync(3);

        Assert.Equal(new DateTime(2026, 8, 8, 12, 48, 44), oldest);
        Assert.Equal(DateTimeKind.Unspecified, oldest!.Value.Kind);
        Assert.Contains(paths, p => p.Contains("action=close&object=777"));
        Assert.Contains(paths, p => p.Contains("action=destroy&object=777"));
    }

    [Fact]
    public async Task FindOldestRecording_RetriesOnceAfterTransportFailure()
    {
        int creates = 0;
        var handler = new MockHttpHandler((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (pq.Contains("factory.create"))
            {
                if (++creates == 1)
                    throw new HttpRequestException("connection reset");
                return MockHttpHandler.Text("result=9\r\n");
            }
            if (pq.Contains("action=findFile"))
                return MockHttpHandler.Text("OK\r\n");
            if (pq.Contains("findNextFile"))
                return MockHttpHandler.Text(
                    "found=1\nitems[0].StartTime=2026-08-12 17:00:00\nitems[0].EndTime=2026-08-12 18:00:00\n");
            return MockHttpHandler.Text("OK\r\n");
        });
        using var client = new DahuaClient(Conn, handler);

        Assert.Equal(new DateTime(2026, 8, 12, 17, 0, 0), await client.FindOldestRecordingAsync(1));
        Assert.Equal(2, creates);
    }

    [Fact]
    public async Task FindOldestRecording_SecondTransportFailure_Propagates()
    {
        var handler = new MockHttpHandler((_, _) => throw new HttpRequestException("down"));
        using var client = new DahuaClient(Conn, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindOldestRecordingAsync(1));
    }

    [Fact]
    public void PerDeviceFailure_ClassifiesTransportAndTimeout_NotRealCancellation()
    {
        var live = CancellationToken.None;
        Assert.True(NvrException.IsPerDeviceFailure(new NvrException("x"), live));
        Assert.True(NvrException.IsPerDeviceFailure(new HttpRequestException("x"), live));
        Assert.True(NvrException.IsPerDeviceFailure(new TaskCanceledException(), live));
        Assert.False(NvrException.IsPerDeviceFailure(new TaskCanceledException(), new CancellationToken(true)));
        Assert.False(NvrException.IsPerDeviceFailure(new InvalidOperationException(), live));
    }

    [Fact]
    public async Task FindOldestRecording_NoFootage_ReturnsNull()
    {
        var handler = new MockHttpHandler((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (pq.Contains("factory.create"))
                return MockHttpHandler.Text("result=1\r\n");
            if (pq.Contains("action=findFile"))
                return MockHttpHandler.Text("Error\r\n", HttpStatusCode.BadRequest);
            return MockHttpHandler.Text("OK\r\n");
        });
        using var client = new DahuaClient(Conn, handler);

        Assert.Null(await client.FindOldestRecordingAsync(1));
    }

    [Theory]
    [InlineData("32-8192", 32, 8192)]
    [InlineData("256,512,1024,2048,4096", 256, 4096)]
    public void BitrateOptions_ParseRangeAndList(string text, int min, int max)
    {
        Assert.True(DahuaClient.TryParseBitrateOptions(text, out var range));
        Assert.Equal(new BitrateRange(min, max), range);
    }

    [Fact]
    public void BitrateOptions_RejectsSingleOrEmpty()
    {
        Assert.False(DahuaClient.TryParseBitrateOptions("", out _));
        Assert.False(DahuaClient.TryParseBitrateOptions("4096", out _));
    }

    [Fact]
    public async Task SetMaxBitrate_WritesGeneralStream_ReturnsReadBack()
    {
        int bitrate = 4096;
        var handler = new MockHttpHandler((req, _) =>
        {
            string pq = req.RequestUri!.PathAndQuery;
            if (pq.Contains("action=setConfig"))
            {
                // Every record type the channel has, in one call; [2] is absent here.
                Assert.Contains("Encode[0].MainFormat[0].Video.BitRate=3072", pq);
                Assert.Contains("Encode[0].MainFormat[1].Video.BitRate=3072", pq);
                Assert.DoesNotContain("MainFormat[2]", pq);
                bitrate = 3072;
                return MockHttpHandler.Text("OK\r\n");
            }
            return MockHttpHandler.Text($"""
                table.Encode[0].MainFormat[0].Video.BitRate={bitrate}
                table.Encode[0].MainFormat[0].Video.BitRateControl=VBR
                table.Encode[0].MainFormat[1].Video.BitRate={bitrate}
                table.Encode[0].MainFormat[1].Video.BitRateControl=VBR
                """);
        });
        using var client = new DahuaClient(Conn, handler);

        Assert.Equal(3072, await client.SetMaxBitrateAsync(1, 3072));
    }

    [Fact]
    public async Task SetMaxBitrate_DeviceSaysError_Throws()
    {
        var handler = new MockHttpHandler((_, _) => MockHttpHandler.Text("Error\r\n"));
        using var client = new DahuaClient(Conn, handler);

        await Assert.ThrowsAsync<NvrException>(() => client.SetMaxBitrateAsync(1, 3072));
    }

    [Theory]
    [InlineData("/dev/sda", 1)]
    [InlineData("/dev/sdc", 3)]
    [InlineData("/dev/mmcblk0", 9)]
    public void BayNumber_FromDeviceLetter(string name, int expected) =>
        Assert.Equal(expected, DahuaClient.BayNumber(name, 9));
}
