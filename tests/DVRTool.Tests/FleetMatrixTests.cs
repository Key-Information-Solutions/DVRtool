using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The Users tab's fleet views: login accounts across recorders, credentials across
/// panels. The rule under test throughout: an unreadable device contributes nothing to a
/// row's status — "missing on X" from a failed read is the exact wrong answer.
/// </summary>
public class FleetMatrixTests
{
    private static DeviceUsersResult Device(string name, params NvrUser[] users) =>
        new() { DeviceName = name, Users = users };

    private static NvrUser User(string name, string level) =>
        new(name, name, UserRole.Custom, level);

    [Fact]
    public void AccountOnEveryDeviceReadsOnAll()
    {
        var matrix = UserMatrix.Build([
            Device("Store 1", User("admin", "Administrator"), User("gate", "Operator")),
            Device("Store 2", User("admin", "Administrator"), User("gate", "Operator")),
            Device("Store 3", User("admin", "Administrator"), User("gate", "Operator")),
        ]);

        Assert.All(matrix.Rows, row => Assert.Equal("On all (3)", row.Status));
        Assert.Equal(["admin", "gate"], matrix.Rows.Select(r => r.User));
        Assert.False(matrix.IsPartial);
    }

    [Fact]
    public void AccountMissingSomewhereNamesTheDevices()
    {
        var matrix = UserMatrix.Build([
            Device("A", User("viewer", "Viewer")),
            Device("B"),
            Device("C"),
        ]);

        var row = Assert.Single(matrix.Rows);
        Assert.Equal("Missing on B, C", row.Status);
        Assert.Equal(["Viewer", null, null], row.Cells);
    }

    [Fact]
    public void LevelDisagreementIsCalledOut()
    {
        var matrix = UserMatrix.Build([
            Device("A", User("gate", "Operator")),
            Device("B", User("gate", "Administrator")),
        ]);

        Assert.Equal("Level differs", Assert.Single(matrix.Rows).Status);
    }

    [Fact]
    public void AccountsPairByNameCaseInsensitivelyAcrossVendors()
    {
        // Hikvision and Dahua disagree on id shape, and installers disagree on casing.
        var matrix = UserMatrix.Build([
            Device("A", User("Admin", "Administrator")),
            Device("B", User("admin", "admin")),
        ]);

        var row = Assert.Single(matrix.Rows);
        Assert.Equal("Admin", row.User); // first-seen spelling is what gets displayed
        Assert.Equal(["Administrator", "admin"], row.Cells);
        Assert.Equal("Level differs", row.Status);
    }

    [Fact]
    public void UnreadableDeviceIsCarriedButNeverReadAsMissing()
    {
        var matrix = UserMatrix.Build([
            Device("A", User("gate", "Operator")),
            Device("B", User("gate", "Operator")),
            DeviceUsersResult.Failed("C", "no route to host"),
        ]);

        Assert.True(matrix.IsPartial);
        Assert.Equal("C", Assert.Single(matrix.FailedDevices).DeviceName);
        var row = Assert.Single(matrix.Rows);
        // Two readable devices agree; the dead one must not turn that into "missing on C".
        Assert.Equal("On all (2)", row.Status);
        Assert.Null(row.Cells[2]);
    }

    [Fact]
    public void SingleReadableDeviceIsAListingNotAComparison()
    {
        var matrix = UserMatrix.Build([Device("A", User("gate", "Operator"))]);
        Assert.Equal("", Assert.Single(matrix.Rows).Status);
    }

    // ----- access matrix -----

    private static AccessCard Card(string no, bool valid = true, params int[] doors) =>
        new() { CardNo = no, Doors = doors, Valid = valid };

    private static AccessPanelResult Panel(string host, params AccessCard[] cards) =>
        new() { PanelHost = host, Cards = cards };

    [Fact]
    public void FobPivotsIntoOneRowWithPerPanelDoorCells()
    {
        var matrix = AccessMatrix.Build(AccessRoster.Build([
            Panel("ocb1", Card("2375", valid: true, 1)),
            Panel("ocb2", Card("2375", valid: true, 1, 2, 3, 4)),
            Panel("ocb3"),
        ]));

        var row = Assert.Single(matrix.Rows);
        Assert.Equal(["1", "1,2,3,4", null], row.Cells);
        Assert.Equal("Missing on ocb3", row.Status);
    }

    [Fact]
    public void RevokedPresenceShowsInCellAndStatus()
    {
        var matrix = AccessMatrix.Build(AccessRoster.Build([
            Panel("ocb1", Card("2375", valid: true, 1)),
            Panel("ocb2", Card("2375", valid: false)),
        ]));

        var row = Assert.Single(matrix.Rows);
        Assert.Equal("REVOKED", row.Cells[1]);
        Assert.Equal("REVOKED on ocb2", row.Status);
    }

    [Fact]
    public void FullyRevokedFobReadsRevokedEverywhere()
    {
        var matrix = AccessMatrix.Build(AccessRoster.Build([
            Panel("ocb1", Card("2375", valid: false)),
            Panel("ocb2", Card("2375", valid: false)),
        ]));

        Assert.Equal("Revoked everywhere", Assert.Single(matrix.Rows).Status);
    }

    [Fact]
    public void UnreadablePanelIsCarriedButNeverReadAsMissing()
    {
        var matrix = AccessMatrix.Build(AccessRoster.Build([
            Panel("ocb1", Card("2375", valid: true, 1)),
            Panel("ocb2", Card("2375", valid: true, 1)),
            AccessPanelResult.Failed("ocb3", "login refused"),
        ]));

        Assert.True(matrix.IsPartial);
        var row = Assert.Single(matrix.Rows);
        Assert.Equal("On all (2)", row.Status);
        Assert.Null(row.Cells[2]);
    }

    [Fact]
    public void DisplayNamesRenameStatusProseButNotTheJoin()
    {
        var matrix = AccessMatrix.Build(
            AccessRoster.Build([
                Panel("10.0.0.5:8001", Card("2375", valid: true, 1)),
                Panel("10.0.0.5:8002"),
            ]),
            label => label == "10.0.0.5:8002" ? "OCB2" : label);

        Assert.Equal("Missing on OCB2", Assert.Single(matrix.Rows).Status);
    }

    [Fact]
    public void EnrichedNamesRideAlong()
    {
        var roster = AccessRoster.Build([Panel("ocb1", Card("2375", valid: true, 1))])
            .EnrichWith(IdentityMap.Build(
                [new CardholderIdentity { Fob = "2375", Name = "Jane Doe", Source = "test" }],
                "test"));

        Assert.Equal("Jane Doe", Assert.Single(AccessMatrix.Build(roster).Rows).Name);
    }
}
