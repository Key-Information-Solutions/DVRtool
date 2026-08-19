using DVRTool.Core;
using DVRTool.Vendors.HikvisionAccess;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// Byte-level tests for the <c>NET_DVR_CARD_CFG_V50</c> codec.
/// </summary>
/// <remarks>
/// The SDK cannot be mocked, so these run against real records captured off a live
/// DS-K2604 panel (<c>Fixtures/card-records.b64</c>). A layout regression here is the
/// failure that would silently write the wrong door rights to a physical door.
/// </remarks>
public class AccessCardRecordTests
{
    private static byte[][] LoadFixtures()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "card-records.b64");
        Assert.True(File.Exists(path), $"fixture missing: {path}");
        return File.ReadAllLines(path)
            .Where(l => l.Trim().Length > 0)
            .Select(l => Convert.FromBase64String(l.Trim()))
            .ToArray();
    }

    [Fact]
    public void FixturesAreWholeRecords()
    {
        var fixtures = LoadFixtures();
        Assert.Equal(3, fixtures.Length);
        Assert.All(fixtures, f => Assert.Equal(CardRecord.Size, f.Length));
    }

    [Fact]
    public void DeviceAgreesWithOurStructSize()
    {
        // The device stamps sizeof(NET_DVR_CARD_CFG_V50) into dwSize. If that ever stops
        // matching our layout, every offset below is suspect and the driver must not write.
        foreach (byte[] raw in LoadFixtures())
            Assert.Equal((uint)CardRecord.Size, CardRecord.FromBytes(raw).DeclaredSize);
    }

    [Fact]
    public void ParsesRealRecordFields()
    {
        var record = CardRecord.FromBytes(LoadFixtures()[0]);

        Assert.Equal("2375", record.CardNo);
        Assert.True(record.Valid);
        Assert.Equal((byte)AccessCardType.Normal, record.CardType);
        Assert.False(record.IsLeaderCard);
        Assert.False(record.IsAdmin);
        Assert.Equal([1, 2, 3, 4], record.Doors);
        Assert.True(record.ValidPeriodEnabled);
        Assert.Equal(new DateTime(2025, 8, 15, 11, 2, 27), record.ValidFrom);
        Assert.Equal(new DateTime(2035, 8, 15, 11, 2, 27), record.ValidUntil);
    }

    [Fact]
    public void RealRecordsCarryNoCardholderIdentity()
    {
        // Not an accident of these three fobs: DS-K2604 V2.0 firmware stores no identity at
        // all, and the card->name channel answers NOSUPPORT. Anything that claims to resolve
        // a person from these panels alone is wrong.
        foreach (byte[] raw in LoadFixtures())
        {
            var record = CardRecord.FromBytes(raw);
            Assert.Equal("", record.Name);
            Assert.Equal(0u, record.EmployeeNo);
            Assert.Equal(0u, record.CardUserId);
        }
    }

    [Fact]
    public void GrantedDoorsCarryARightPlan()
    {
        // Door rights and right plans are two halves of one permission: a door granted with
        // plan 0 has no schedule and never opens. Real records pair them, and so must we.
        var record = CardRecord.FromBytes(LoadFixtures()[0]);
        foreach (int door in record.Doors)
            Assert.Equal(CardRecord.DefaultRightPlan, record.RightPlanFor(door));
        Assert.Equal(0, record.RightPlanFor(5));
    }

    [Fact]
    public void SetDoorsUpdatesRightsAndPlansTogether()
    {
        var record = CardRecord.FromBytes(LoadFixtures()[0]);
        record.SetDoors([2, 4]);

        Assert.Equal([2, 4], record.Doors);
        Assert.Equal(CardRecord.DefaultRightPlan, record.RightPlanFor(2));
        Assert.Equal(CardRecord.DefaultRightPlan, record.RightPlanFor(4));
        // Revoked doors must lose the plan too, or the panel keeps a stale schedule.
        Assert.Equal(0, record.RightPlanFor(1));
        Assert.Equal(0, record.RightPlanFor(3));
    }

    [Fact]
    public void EditingPreservesEveryByteItDoesNotOwn()
    {
        // The read-modify-write contract: changing door rights must not disturb week plans,
        // groups, card passwords, lock/room codes or the reserved tail.
        byte[] raw = LoadFixtures()[0];
        var record = CardRecord.FromBytes(raw);
        record.SetDoors([1, 2, 3, 4]);   // same doors the fixture already had
        record.Valid = true;

        byte[] after = record.ToArray();
        // Only dwModifyParamType is expected to differ, and we haven't set it yet.
        Assert.Equal(raw, after);
    }

    [Fact]
    public void ModifyParamsRoundTrip()
    {
        var record = CardRecord.CreateEmpty();
        Assert.Equal(CardModifyParam.None, record.ModifyParams);

        record.ModifyParams = CardModifyParam.CardValid | CardModifyParam.DoorRight;
        Assert.Equal(CardModifyParam.CardValid | CardModifyParam.DoorRight, record.ModifyParams);
    }

    [Fact]
    public void NewRecordDeclaresItsSize()
    {
        var record = CardRecord.CreateEmpty();
        Assert.Equal((uint)CardRecord.Size, record.DeclaredSize);
        Assert.Equal(CardRecord.Size, record.ToArray().Length);
    }

    [Fact]
    public void CardNoAndNameRoundTrip()
    {
        var record = CardRecord.CreateEmpty();
        record.CardNo = "0012345678";
        record.Name = "First.Last";

        Assert.Equal("0012345678", record.CardNo);
        Assert.Equal("First.Last", record.Name);
    }

    [Fact]
    public void OverlongFieldsAreRefusedNotTruncated()
    {
        var record = CardRecord.CreateEmpty();
        // Truncating a card number would provision a *different* credential.
        Assert.Throws<ArgumentException>(() => record.CardNo = new string('9', 33));
        Assert.Throws<ArgumentException>(() => record.Name = new string('x', 33));
    }

    [Fact]
    public void ShortBufferIsRejected()
    {
        var ex = Assert.Throws<NvrException>(() => CardRecord.FromBytes(new byte[100]));
        Assert.Contains("2708", ex.Message);
    }

    [Fact]
    public void ValidPeriodRoundTripsThroughTheWireFormat()
    {
        var record = CardRecord.CreateEmpty();
        var from = new DateTime(2026, 8, 19, 9, 30, 15);
        var until = new DateTime(2027, 1, 1, 0, 0, 0);
        record.SetValidPeriod(from, until);

        var reparsed = CardRecord.FromBytes(record.ToArray());
        Assert.True(reparsed.ValidPeriodEnabled);
        Assert.Equal(from, reparsed.ValidFrom);
        Assert.Equal(until, reparsed.ValidUntil);
    }

    [Fact]
    public void ValidPeriodRejectsAnInvertedWindow()
    {
        var record = CardRecord.CreateEmpty();
        Assert.Throws<ArgumentException>(() =>
            record.SetValidPeriod(new DateTime(2027, 1, 1), new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void DisabledValidPeriodReadsAsNoWindow()
    {
        var record = CardRecord.FromBytes(LoadFixtures()[0]);
        record.ValidPeriodEnabled = false;
        Assert.Null(record.ValidFrom);
        Assert.Null(record.ValidUntil);
    }

    [Fact]
    public void OutOfRangeDoorsAreRejected()
    {
        var record = CardRecord.CreateEmpty();
        Assert.Throws<ArgumentException>(() => record.SetDoors([0]));
        Assert.Throws<ArgumentException>(() => record.SetDoors([257]));
    }

    [Fact]
    public void ProjectsOntoTheVendorNeutralModel()
    {
        var card = CardRecord.FromBytes(LoadFixtures()[2]).ToAccessCard("192.0.2.223");

        Assert.Equal("4167", card.CardNo);
        Assert.Equal("192.0.2.223", card.PanelHost);
        Assert.Equal(AccessCardType.Normal, card.Type);
        Assert.True(card.Valid);
        Assert.Equal([1, 2, 3, 4], card.Doors);
        Assert.Null(card.Name);
        Assert.False(card.HasIdentity);
        Assert.Equal("1,2,3,4", card.DoorSummary);
        Assert.Equal(new DateTime(2025, 8, 15, 0, 0, 0), card.ValidFrom);
        Assert.Equal(new DateTime(2035, 8, 14, 23, 59, 59), card.ValidUntil);
    }

    [Fact]
    public void UnmappedCardTypeSurvivesAsUnknownWithRawValueKept()
    {
        var record = CardRecord.FromBytes(LoadFixtures()[0]);
        record.CardType = 99;
        var card = record.ToAccessCard("panel");

        Assert.Equal(AccessCardType.Unknown, card.Type);
        Assert.Equal(99, card.NativeCardType);
    }

    [Theory]
    [InlineData("OCB-K260420180706V020004ENC00000001", "DS-K2604 (OCB OEM)", "V2.0.4", 4)]
    [InlineData("OCB-K260420211028V020009ENC00000002", "DS-K2604 (OCB OEM)", "V2.0.9", 4)]
    public void SerialYieldsModelFirmwareAndDoorCount(
        string serial, string model, string firmware, int? doors)
    {
        Assert.Equal(model, PanelIdentity.ParseModel(serial));
        Assert.Equal(firmware, PanelIdentity.ParseFirmware(serial));
        Assert.Equal(doors, PanelIdentity.ParseDoorCount(serial));
    }

    [Fact]
    public void UnrecognizedSerialDegradesGracefully()
    {
        Assert.Equal("weird-device", PanelIdentity.ParseModel("weird-device"));
        Assert.Equal("", PanelIdentity.ParseFirmware("weird-device"));
        Assert.Null(PanelIdentity.ParseDoorCount("weird-device"));
    }
}
