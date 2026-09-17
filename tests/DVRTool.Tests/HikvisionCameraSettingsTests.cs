using System.Net;
using System.Xml.Linq;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The Hikvision encoder read and write. The cases worth having are the ones where the device
/// lies or the document is not what a naive writer assumes: a rejection under HTTP 200, a
/// document that moved between the read and the write, and fields the PUT must carry through
/// untouched.
/// </summary>
public class HikvisionCameraSettingsTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss",
    };

    private const string Ns = "http://www.hikvision.com/ver20/XMLSchema";
    private const string IsapiNs = "http://www.isapi.org/ver20/XMLSchema";

    /// <summary>
    /// One channel document. <c>customField</c> stands in for the per-firmware fields a
    /// hand-built PUT would drop — the whole reason the write round-trips the device's own
    /// document rather than composing a new one.
    /// </summary>
    private static string ChannelXml(string ns, int trackId, int maxFrameRate = 2000,
        int vbrUpperCap = 4096, string codec = "H.265", string quality = "VBR",
        int width = 2688, int height = 1520, int gov = 50, bool audio = false) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <StreamingChannel version="2.0" xmlns="{ns}">
        <id>{trackId}</id>
        <channelName>Front Door</channelName>
        <enabled>true</enabled>
        <customField>keep-me</customField>
        <Transport><rtspPortNo>554</rtspPortNo></Transport>
        <Video>
        <enabled>true</enabled>
        <videoInputChannelID>{trackId / 100}</videoInputChannelID>
        <videoCodecType>{codec}</videoCodecType>
        <videoResolutionWidth>{width}</videoResolutionWidth>
        <videoResolutionHeight>{height}</videoResolutionHeight>
        <videoQualityControlType>{quality}</videoQualityControlType>
        <constantBitRate>4096</constantBitRate>
        <fixedQuality>60</fixedQuality>
        <vbrUpperCap>{vbrUpperCap}</vbrUpperCap>
        <maxFrameRate>{maxFrameRate}</maxFrameRate>
        <GovLength>{gov}</GovLength>
        <smartCodec>open</smartCodec>
        </Video>
        <Audio><enabled>{(audio ? "true" : "false")}</enabled></Audio>
        </StreamingChannel>
        """;

    private static string ChannelListXml(string ns) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <StreamingChannelList version="2.0" xmlns="{ns}">
        {Strip(ChannelXml(ns, 101))}
        {Strip(ChannelXml(ns, 102, maxFrameRate: 1200, vbrUpperCap: 512, width: 704, height: 480))}
        </StreamingChannelList>
        """;

    private static string Strip(string doc) =>
        string.Join("\n", doc.Split('\n').Where(l => !l.TrimStart().StartsWith("<?xml")));

    private static string CapabilitiesXml(string ns) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <StreamingChannel version="2.0" xmlns="{ns}">
        <Video>
        <videoCodecType opt="H.264,H.265">H.265</videoCodecType>
        <videoResolutionWidth opt="1280,1920,2688">2688</videoResolutionWidth>
        <videoResolutionHeight opt="720,1080,1520">1520</videoResolutionHeight>
        <videoQualityControlType opt="CBR,VBR">VBR</videoQualityControlType>
        <vbrUpperCap min="32" max="16384" def="4096">4096</vbrUpperCap>
        <maxFrameRate opt="0,3000,2500,2000,1200">2000</maxFrameRate>
        <GovLength min="1" max="400" default="50">50</GovLength>
        </Video>
        </StreamingChannel>
        """;

    private static string Ok() => """
        <?xml version="1.0" encoding="UTF-8" ?>
        <ResponseStatus><statusCode>1</statusCode><statusString>OK</statusString></ResponseStatus>
        """;

    [Theory]
    [InlineData(Ns)]
    [InlineData(IsapiNs)]
    public async Task Every_track_is_read_including_the_sub_stream(string ns)
    {
        var handler = new MockHttpHandler((req, _) =>
            MockHttpHandler.Xml(ChannelListXml(ns)));
        var client = new HikvisionClient(Conn, handler);

        var tracks = await client.GetEncodingAsync();

        // The storage read drops sub streams on purpose; this one must not — a sub stream is a
        // setting to edit here, not a bitrate to add to a retention total.
        Assert.Equal(2, tracks.Count);
        Assert.Equal(StreamType.Main, tracks[0].Stream);
        Assert.Equal(StreamType.Sub, tracks[1].Stream);
        Assert.Equal(1, tracks[0].Channel);
        Assert.Equal("2688x1520", tracks[0].Resolution);
        Assert.Equal(20.0, tracks[0].FrameRateFps);
        Assert.Equal(50, tracks[0].GovLength);
        Assert.False(tracks[0].AudioEnabled);
        Assert.Equal("704x480", tracks[1].Resolution);
    }

    [Fact]
    public async Task A_zero_frame_rate_is_full_frame_rate_not_unknown()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/capabilities")
                ? MockHttpHandler.Xml(CapabilitiesXml(Ns))
                : MockHttpHandler.Xml(ChannelListXml(Ns)
                    .Replace("<maxFrameRate>2000</maxFrameRate>",
                        "<maxFrameRate>0</maxFrameRate>")));
        var client = new HikvisionClient(Conn, handler);

        var tracks = await client.GetEncodingAsync();

        Assert.True(tracks[0].FrameRateIsFull);
        // Resolved from the capabilities' opt list, not invented: 3000 is 30 fps.
        Assert.Equal(30.0, tracks[0].FrameRateFps);
        Assert.Contains("full", tracks[0].FrameRateText);
    }

    [Fact]
    public async Task Declared_options_are_read_from_the_capabilities_document()
    {
        var handler = new MockHttpHandler((req, _) =>
            MockHttpHandler.Xml(CapabilitiesXml(Ns)));
        var client = new HikvisionClient(Conn, handler);

        var options = await client.GetEncodingOptionsAsync(1, StreamType.Main);

        Assert.NotNull(options);
        Assert.Equal(["H.264", "H.265"], options.Codecs);
        Assert.Contains(new Resolution(1920, 1080), options.Resolutions);
        Assert.Equal(3, options.Resolutions.Count);
        Assert.Equal([30.0, 25.0, 20.0, 12.0], options.FrameRates);
        Assert.True(options.SupportsFullFrameRate);
        Assert.Equal(new BitrateRange(32, 16384), options.Bitrate);
    }

    [Fact]
    public async Task Both_spellings_of_the_default_attribute_are_read()
    {
        // One capabilities document on this firmware writes def= on some elements and default=
        // on others. A parser that reads one spelling loses the other silently.
        var handler = new MockHttpHandler((req, _) =>
            MockHttpHandler.Xml(CapabilitiesXml(Ns)));
        var client = new HikvisionClient(Conn, handler);

        var options = await client.GetEncodingOptionsAsync(1, StreamType.Main);

        // GovLength spells it "default="; the bitrate element spells it "def=".
        Assert.Equal(50, options!.GovLength!.Default);
        Assert.Equal(1, options.GovLength.Min);
        Assert.Equal(400, options.GovLength.Max);
    }

    [Fact]
    public async Task A_capabilities_document_that_declares_nothing_is_empty_not_null()
    {
        // "The device declared nothing" and "the device has no capabilities document" are
        // different facts, and a front end renders them differently.
        var handler = new MockHttpHandler((req, _) => MockHttpHandler.Xml($"""
            <StreamingChannel version="2.0" xmlns="{Ns}"><Video /></StreamingChannel>
            """));
        var client = new HikvisionClient(Conn, handler);

        var options = await client.GetEncodingOptionsAsync(1, StreamType.Main);

        Assert.NotNull(options);
        Assert.True(options.IsEmpty);
    }

    [Fact]
    public async Task A_missing_capabilities_document_is_null()
    {
        var handler = new MockHttpHandler((req, _) =>
            MockHttpHandler.Xml("<ResponseStatus />", HttpStatusCode.Forbidden));
        var client = new HikvisionClient(Conn, handler);

        Assert.Null(await client.GetEncodingOptionsAsync(1, StreamType.Main));
    }

    [Fact]
    public async Task The_write_round_trips_the_devices_own_document()
    {
        string current = ChannelXml(Ns, 101);
        var handler = new MockHttpHandler((req, _) =>
            req.Method == HttpMethod.Put
                ? MockHttpHandler.Xml(Ok())
                : MockHttpHandler.Xml(current));
        var client = new HikvisionClient(Conn, handler);

        await client.SetEncodingAsync(1, StreamType.Main,
            new EncodingSettings(FrameRateFps: 15.0));

        var put = handler.Requests.Single(r => r.Request.Method == HttpMethod.Put);
        Assert.Equal("/ISAPI/Streaming/channels/101", put.Request.RequestUri!.AbsolutePath);

        var doc = XDocument.Parse(put.Body);
        // Fields nobody asked about survive — the reason the write is a round trip and not a
        // composed minimal document.
        Assert.Equal("keep-me",
            doc.Descendants().First(e => e.Name.LocalName == "customField").Value);
        Assert.Equal("554",
            doc.Descendants().First(e => e.Name.LocalName == "rtspPortNo").Value);
        // fps is written in ISAPI's x100 encoding.
        Assert.Equal("1500",
            doc.Descendants().First(e => e.Name.LocalName == "maxFrameRate").Value);
    }

    [Fact]
    public async Task Full_frame_rate_is_written_as_the_literal_zero()
    {
        string current = ChannelXml(Ns, 101);
        var handler = new MockHttpHandler((req, _) =>
            req.Method == HttpMethod.Put ? MockHttpHandler.Xml(Ok())
            : req.RequestUri!.AbsolutePath.EndsWith("/capabilities")
                ? MockHttpHandler.Xml(CapabilitiesXml(Ns))
                : MockHttpHandler.Xml(current));
        var client = new HikvisionClient(Conn, handler);

        await client.SetEncodingAsync(1, StreamType.Main,
            new EncodingSettings(FullFrameRate: true));

        var put = handler.Requests.Single(r => r.Request.Method == HttpMethod.Put);
        var doc = XDocument.Parse(put.Body);
        Assert.Equal("0", doc.Descendants().First(e => e.Name.LocalName == "maxFrameRate").Value);
    }

    [Fact]
    public async Task A_rate_and_full_frame_rate_together_are_refused_before_any_request()
    {
        var handler = new MockHttpHandler((req, _) => MockHttpHandler.Xml(ChannelXml(Ns, 101)));
        var client = new HikvisionClient(Conn, handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SetEncodingAsync(1, StreamType.Main,
                new EncodingSettings(FrameRateFps: 15.0, FullFrameRate: true)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Both_rate_fields_are_written_so_a_mode_switch_stays_consistent()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.Method == HttpMethod.Put
                ? MockHttpHandler.Xml(Ok())
                : MockHttpHandler.Xml(ChannelXml(Ns, 101)));
        var client = new HikvisionClient(Conn, handler);

        await client.SetEncodingAsync(1, StreamType.Main,
            new EncodingSettings(BitrateKbps: 2048, QualityControlType: "CBR"));

        var doc = XDocument.Parse(
            handler.Requests.Single(r => r.Request.Method == HttpMethod.Put).Body);
        Assert.Equal("2048", doc.Descendants().First(e => e.Name.LocalName == "vbrUpperCap").Value);
        Assert.Equal("2048",
            doc.Descendants().First(e => e.Name.LocalName == "constantBitRate").Value);
        Assert.Equal("CBR",
            doc.Descendants().First(e => e.Name.LocalName == "videoQualityControlType").Value);
    }

    [Fact]
    public async Task A_rejection_arrives_as_HTTP_200_and_is_not_success()
    {
        // The trap this whole codebase keeps re-learning: ISAPI answers 200 and says no in the
        // body. statusCode 1 is the only success.
        var handler = new MockHttpHandler((req, _) =>
            req.Method == HttpMethod.Put
                ? MockHttpHandler.Xml("""
                    <ResponseStatus><statusCode>4</statusCode>
                    <statusString>Invalid Operation</statusString></ResponseStatus>
                    """)
                : MockHttpHandler.Xml(ChannelXml(Ns, 101)));
        var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetEncodingAsync(1, StreamType.Main, new EncodingSettings(FrameRateFps: 15.0)));

        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task A_document_that_moved_between_the_read_and_the_write_is_refused()
    {
        // Somebody editing the same recorder from its own web UI. Clobbering their change
        // silently is worse than refusing.
        int reads = 0;
        var handler = new MockHttpHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Put)
                return MockHttpHandler.Xml(Ok());
            // The second read of the channel document reports a different bitrate.
            reads++;
            return MockHttpHandler.Xml(reads <= 1
                ? ChannelXml(Ns, 101)
                : ChannelXml(Ns, 101, vbrUpperCap: 8192));
        });
        var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetEncodingAsync(1, StreamType.Main, new EncodingSettings(FrameRateFps: 15.0)));

        Assert.Contains("changed on the device", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Whitespace_only_reformatting_is_not_a_change()
    {
        // Firmware reformats freely between two identical reads; treating that as a concurrent
        // edit would make every write fail on some recorders.
        int reads = 0;
        bool written = false;
        var handler = new MockHttpHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Put)
            {
                written = true;
                return MockHttpHandler.Xml(Ok());
            }
            reads++;
            string doc = ChannelXml(Ns, 101, maxFrameRate: written ? 1500 : 2000);
            // The re-read comes back reformatted but saying the same thing.
            return MockHttpHandler.Xml(reads <= 1 ? doc : doc.Replace("\n", "\n  "));
        });
        var client = new HikvisionClient(Conn, handler);

        var change = await client.SetEncodingAsync(1, StreamType.Main,
            new EncodingSettings(FrameRateFps: 15.0));

        Assert.Contains(handler.Requests, r => r.Request.Method == HttpMethod.Put);
        Assert.True(change.Changed);
        Assert.False(change.Rejected);
    }

    [Fact]
    public async Task A_field_the_channel_does_not_expose_is_refused_rather_than_invented()
    {
        // Adding an element the firmware does not expect is how a whole PUT gets rejected for
        // one field — and the operator is owed the reason, not a generic failure.
        string withoutGov = ChannelXml(Ns, 101).Replace("<GovLength>50</GovLength>", "");
        var handler = new MockHttpHandler((req, _) =>
            req.Method == HttpMethod.Put
                ? MockHttpHandler.Xml(Ok())
                : MockHttpHandler.Xml(withoutGov));
        var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetEncodingAsync(1, StreamType.Main, new EncodingSettings(GovLength: 40)));

        Assert.Contains("I-frame interval", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task A_value_the_recorder_silently_kept_is_reported_as_rejected()
    {
        // The recorder accepts the PUT, answers statusCode 1, and holds its own value anyway.
        // Only the read-back catches it, which is the entire reason there is one.
        var handler = new MockHttpHandler((req, _) =>
            req.Method == HttpMethod.Put
                ? MockHttpHandler.Xml(Ok())
                : MockHttpHandler.Xml(ChannelXml(Ns, 101)));
        var client = new HikvisionClient(Conn, handler);

        var change = await client.SetEncodingAsync(1, StreamType.Main,
            new EncodingSettings(BitrateKbps: 2048));

        Assert.True(change.Rejected);
        Assert.False(change.Changed);
        Assert.Contains("kept its own values", change.Note);
        Assert.Contains("4096", change.Note);
    }

    [Fact]
    public async Task An_empty_request_writes_nothing()
    {
        var handler = new MockHttpHandler((req, _) => MockHttpHandler.Xml(ChannelXml(Ns, 101)));
        var client = new HikvisionClient(Conn, handler);

        var change = await client.SetEncodingAsync(1, StreamType.Main, new EncodingSettings());

        Assert.False(change.Changed);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task The_storage_bitrate_write_goes_through_the_same_guarded_path()
    {
        // One document, one writer. Two write paths to /ISAPI/Streaming/channels/{track} is how
        // one of them quietly loses the guard the other has.
        string doc = ChannelXml(Ns, 101);
        var handler = new MockHttpHandler((req, body) =>
        {
            if (req.Method != HttpMethod.Put)
                return MockHttpHandler.Xml(doc);
            doc = ChannelXml(Ns, 101, vbrUpperCap: 2048);
            return MockHttpHandler.Xml(Ok());
        });
        var client = new HikvisionClient(Conn, handler);

        int actual = await client.SetMaxBitrateAsync(1, 2048);

        Assert.Equal(2048, actual);
        var put = handler.Requests.Single(r => r.Request.Method == HttpMethod.Put);
        Assert.Equal("/ISAPI/Streaming/channels/101", put.Request.RequestUri!.AbsolutePath);
        // Still a full-document round trip, unknown fields intact.
        Assert.Contains("keep-me", put.Body);
    }

    [Fact]
    public async Task A_name_is_found_on_the_IP_camera_list_first()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath.Contains("InputProxy")
                ? MockHttpHandler.Xml($"""
                    <InputProxyChannel version="2.0" xmlns="{Ns}">
                    <id>1</id><name>Front Door</name>
                    </InputProxyChannel>
                    """)
                : MockHttpHandler.Xml("<ResponseStatus />", HttpStatusCode.Forbidden));
        var client = new HikvisionClient(Conn, handler);

        var identity = await client.GetChannelIdentityAsync(1);

        Assert.NotNull(identity);
        Assert.Equal("Front Door", identity.Name);
        Assert.Equal(ChannelNameSource.InputProxy, identity.Source);
    }

    [Fact]
    public async Task A_name_falls_back_to_the_analog_input_list()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath.Contains("InputProxy")
                ? MockHttpHandler.Xml("<ResponseStatus />", HttpStatusCode.Forbidden)
                : MockHttpHandler.Xml($"""
                    <VideoInputChannel version="2.0" xmlns="{Ns}">
                    <id>1</id><name>Camera 01</name>
                    </VideoInputChannel>
                    """));
        var client = new HikvisionClient(Conn, handler);

        var identity = await client.GetChannelIdentityAsync(1);

        Assert.Equal(ChannelNameSource.VideoInput, identity!.Source);
        Assert.Equal("Camera 01", identity.Name);
    }

    [Fact]
    public async Task A_rename_writes_to_the_document_the_name_was_found_in()
    {
        // Aiming a rename at the wrong list is accepted and silently does nothing, so which
        // document holds the name is part of the read's answer rather than a guess.
        string name = "Front Door";
        var handler = new MockHttpHandler((req, body) =>
        {
            if (!req.RequestUri!.AbsolutePath.Contains("InputProxy"))
                return MockHttpHandler.Xml("<ResponseStatus />", HttpStatusCode.Forbidden);
            if (req.Method == HttpMethod.Put)
            {
                name = XDocument.Parse(body).Descendants()
                    .First(e => e.Name.LocalName == "name").Value;
                return MockHttpHandler.Xml(Ok());
            }
            return MockHttpHandler.Xml($"""
                <InputProxyChannel version="2.0" xmlns="{Ns}">
                <id>1</id><name>{name}</name><sourceInputPortDescriptor><ipAddress>10.0.0.5</ipAddress></sourceInputPortDescriptor>
                </InputProxyChannel>
                """);
        });
        var client = new HikvisionClient(Conn, handler);

        var change = await client.SetChannelNameAsync(1, "Loading Dock");

        Assert.True(change.Changed);
        Assert.False(change.Rejected);
        var put = handler.Requests.Single(r => r.Request.Method == HttpMethod.Put);
        Assert.Contains("/ISAPI/ContentMgmt/InputProxy/channels/1",
            put.Request.RequestUri!.AbsolutePath);
        // The camera's own address rides through the rename untouched.
        Assert.Contains("10.0.0.5", put.Body);
    }

    [Fact]
    public async Task Renaming_a_channel_to_what_it_already_is_writes_nothing()
    {
        var handler = new MockHttpHandler((req, _) =>
            req.RequestUri!.AbsolutePath.Contains("InputProxy")
                ? MockHttpHandler.Xml($"""
                    <InputProxyChannel version="2.0" xmlns="{Ns}">
                    <id>1</id><name>Front Door</name>
                    </InputProxyChannel>
                    """)
                : MockHttpHandler.Xml("<ResponseStatus />", HttpStatusCode.Forbidden));
        var client = new HikvisionClient(Conn, handler);

        var change = await client.SetChannelNameAsync(1, "Front Door");

        Assert.False(change.Changed);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task A_channel_with_no_name_anywhere_is_an_error_not_a_silent_no_op()
    {
        var handler = new MockHttpHandler((req, _) =>
            MockHttpHandler.Xml("<ResponseStatus />", HttpStatusCode.Forbidden));
        var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(() =>
            client.SetChannelNameAsync(1, "Loading Dock"));

        Assert.Contains("nothing to rename", ex.Message);
    }

    [Theory]
    [InlineData(101, StreamType.Main)]
    [InlineData(102, StreamType.Sub)]
    [InlineData(103, StreamType.Third)]
    [InlineData(3301, StreamType.Main)]
    public void Track_ids_map_to_their_stream(int trackId, StreamType expected) =>
        Assert.Equal(expected, HikvisionClient.ParseStreamType(trackId));

    [Theory]
    [InlineData(104)]
    [InlineData(100)]
    public void A_track_outside_the_three_we_model_is_skipped_not_guessed(int trackId) =>
        Assert.Null(HikvisionClient.ParseStreamType(trackId));
}
