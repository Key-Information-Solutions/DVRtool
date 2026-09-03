using System.IO;
using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The pin store and how pins resolve against the cameras a recorder actually reports.
/// The planner's own arithmetic is in <see cref="StorageEstimatorTests"/>.
/// </summary>
public class ChannelPinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "dvrtool-pin-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "channel-pins.json");

    private ChannelPinStore Store() => new(Path_);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static PlanCamera Cam(int ch, string name, int? current = 4096) =>
        new(ch, name, current, 32, 16384);

    // ----- the store -----

    [Fact]
    public void Pin_RoundTrips_PerDevice_AndSurvivesANewStoreOverTheSameFile()
    {
        var store = Store();
        store.Pin("10.0.0.1:80", "SERIAL-A",
            new ChannelPin(3, 8192, "Lot South", "insurer asked for it", DateTimeOffset.Now));
        store.Pin("10.0.0.1:80", "SERIAL-A",
            new ChannelPin(1, null, "Front Door", null, DateTimeOffset.Now));
        store.Pin("10.0.0.2:80", "SERIAL-B",
            new ChannelPin(7, 2048, "Till", null, DateTimeOffset.Now));

        var reread = Store().Get("10.0.0.1:80", "SERIAL-A");
        Assert.Equal(2, reread.Count);
        Assert.Equal([1, 3], reread.Pins.Select(p => p.Channel));   // stored in channel order
        Assert.Null(reread.For(1)!.Kbps);                            // "keep what it reads"
        Assert.True(reread.For(1)!.HoldsCurrent);
        Assert.Equal(8192, reread.For(3)!.Kbps);
        Assert.Equal("insurer asked for it", reread.For(3)!.Reason);
        Assert.False(reread.ForeignHardware);

        // The other device is a separate keyspace, not a shared one.
        Assert.Equal(1, Store().Get("10.0.0.2:80", "SERIAL-B").Count);
        Assert.Equal(0, Store().Get("10.0.0.9:80").Count);
        Assert.Equal(2, Store().All().Count);
    }

    [Fact]
    public void Pin_ReplacesTheSameChannelsPin_AndUnpinAndClearRemoveThem()
    {
        var store = Store();
        store.Pin("h:80", "S", new ChannelPin(2, 1024, "Cam", null, DateTimeOffset.Now));
        store.Pin("h:80", "S", new ChannelPin(2, 6000, "Cam", null, DateTimeOffset.Now));
        Assert.Equal(6000, Assert.Single(store.Get("h:80", "S").Pins).Kbps);

        Assert.False(store.Unpin("h:80", 5));   // was not pinned
        Assert.True(store.Unpin("h:80", 2));
        Assert.Equal(0, store.Get("h:80", "S").Count);

        store.Pin("h:80", "S", new ChannelPin(1, 100, "a", null, DateTimeOffset.Now));
        store.Pin("h:80", "S", new ChannelPin(2, 200, "b", null, DateTimeOffset.Now));
        Assert.Equal(2, store.Clear("h:80"));
        Assert.Equal(0, store.Clear("h:80"));
    }

    [Fact]
    public void Get_PinsRecordedAgainstAnotherSerial_AreCarriedButNeverApplied()
    {
        var store = Store();
        store.Pin("10.0.0.1:80", "OLD-RECORDER",
            new ChannelPin(1, 2048, "Front Door", null, DateTimeOffset.Now));

        // The recorder at this address was replaced: same address, different hardware. The
        // pins were promises about the old box's cameras and must not bind the new one's.
        var pins = store.Get("10.0.0.1:80", "NEW-RECORDER");
        Assert.True(pins.ForeignHardware);
        Assert.Equal(1, pins.Count);

        var result = pins.Apply([Cam(1, "Front Door")]);
        Assert.Null(result.Cameras[0].PinnedKbps);
        Assert.Equal(0, result.PinnedCount);
        Assert.Contains("different one", Assert.Single(result.Problems));

        // An unverifiable device (no serial either side) is not evidence of a swap.
        Assert.False(store.Get("10.0.0.1:80").ForeignHardware);
        Assert.False(new ChannelPinStore(Path_).Get("10.0.0.1:80", "").ForeignHardware);
    }

    [Fact]
    public void CorruptFile_Throws_RatherThanReportingNothingIsPinned()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");

        // Reporting "no pins" here would let the next plan overwrite every pinned camera —
        // the one outcome a pin exists to prevent — so this fails closed and says where.
        var ex = Assert.Throws<InvalidDataException>(() => Store().Get("h:80", "S"));
        Assert.Contains(Path_, ex.Message);
    }

    [Fact]
    public void DuplicateChannelInAHandEditedFile_KeepsTheLastOne()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            {
              "h:80": {
                "serial": "S",
                "pins": [
                  { "channel": 4, "kbps": 1000, "camera": "", "reason": null, "pinnedAt": "2026-09-01T00:00:00+00:00" },
                  { "channel": 4, "kbps": 2000, "camera": "", "reason": null, "pinnedAt": "2026-09-02T00:00:00+00:00" }
                ]
              }
            }
            """);

        Assert.Equal(2000, Assert.Single(Store().Get("h:80", "S").Pins).Kbps);
    }

    // ----- resolving against the cameras that were read -----

    [Fact]
    public void Apply_FillsPinnedKbps_ResolvingKeepCurrentAgainstWhatTheDeviceReports()
    {
        var store = Store();
        store.Pin("h:80", "S", new ChannelPin(1, 8192, "Front Door", null, DateTimeOffset.Now));
        store.Pin("h:80", "S", new ChannelPin(2, null, "Lot South", null, DateTimeOffset.Now));

        var result = store.Get("h:80", "S").Apply(
            [Cam(1, "Front Door", 4096), Cam(2, "Lot South", 6144), Cam(3, "Alley", 4096)]);

        Assert.Equal(8192, result.Cameras[0].PinnedKbps);   // the pinned number
        Assert.Equal(6144, result.Cameras[1].PinnedKbps);   // resolved from the device
        Assert.Null(result.Cameras[2].PinnedKbps);          // free for the planner
        Assert.Equal(2, result.PinnedCount);
        Assert.Empty(result.Problems);
        // Order is the display order it was given, not pin order.
        Assert.Equal([1, 2, 3], result.Cameras.Select(c => c.Channel));
    }

    [Fact]
    public void Apply_ChannelNowAnsweringToADifferentName_IsReportedNotApplied()
    {
        // Nx has no channel numbers: the client numbers cameras sorted by name, so adding one
        // shifts every number after it. Honouring this pin by number would hold the wrong
        // camera's bitrate and never say so.
        var store = Store();
        store.Pin("h:7001", "S", new ChannelPin(2, 8192, "Lot South", null, DateTimeOffset.Now));

        var result = store.Get("h:7001", "S").Apply([Cam(1, "Alley"), Cam(2, "Bay Door")]);

        Assert.All(result.Cameras, c => Assert.Null(c.PinnedKbps));
        string problem = Assert.Single(result.Problems);
        Assert.Contains("Lot South", problem);
        Assert.Contains("Bay Door", problem);
        Assert.Contains("IGNORED", problem);
    }

    [Fact]
    public void Apply_UnknownNameOnEitherSide_SkipsTheRenameCheck()
    {
        // A recorder that will not list its channels is not evidence about anything, so a pin
        // must still bind — the alternative is pins that quietly stop working on exactly the
        // firmware that is already being difficult.
        var store = Store();
        store.Pin("h:80", "S", new ChannelPin(1, 8192, "", null, DateTimeOffset.Now));
        store.Pin("h:80", "S", new ChannelPin(2, 4096, "Lot South", null, DateTimeOffset.Now));

        var result = store.Get("h:80", "S").Apply([Cam(1, "Front Door"), Cam(2, "")]);

        Assert.Equal(8192, result.Cameras[0].PinnedKbps);
        Assert.Equal(4096, result.Cameras[1].PinnedKbps);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Apply_PinForAChannelThatIsNotBeingPlanned_IsReported()
    {
        var store = Store();
        store.Pin("h:80", "S", new ChannelPin(9, 4096, "Removed Cam", null, DateTimeOffset.Now));

        var result = store.Get("h:80", "S").Apply([Cam(1, "Front Door")]);

        Assert.Contains("not among the cameras being planned", Assert.Single(result.Problems));
        Assert.Equal(0, result.PinnedCount);
    }

    [Fact]
    public void Apply_KeepCurrentAgainstACameraWithNoReportedRate_IsReported()
    {
        var store = Store();
        store.Pin("h:80", "S", new ChannelPin(1, null, "Front Door", null, DateTimeOffset.Now));

        var result = store.Get("h:80", "S").Apply([Cam(1, "Front Door", current: null)]);

        Assert.Null(result.Cameras[0].PinnedKbps);
        Assert.Contains("does not report one", Assert.Single(result.Problems));
    }

    [Fact]
    public void Summary_AndDescribe_ReadForAnOperator()
    {
        var pins = new ChannelPinSet("h:80", "S",
        [
            new ChannelPin(1, 8192, "Front Door", "plates", DateTimeOffset.Now),
            new ChannelPin(4, null, "Till", null, DateTimeOffset.Now),
        ]);

        Assert.Equal("2 pinned: ch1 8192 kbps, ch4 keep current", pins.Summary);
        Assert.Equal("ch1 8192 kbps (Front Door) — plates", pins.Pins[0].Describe());
        Assert.Equal("ch4 keep current (Till)", pins.Pins[1].Describe());
        Assert.Equal("", ChannelPinSet.Empty("h:80").Summary);
    }
}
