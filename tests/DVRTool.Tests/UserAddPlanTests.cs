using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class UserAddPlanTests
{
    private static NvrUser User(string name, string level = "Operator") =>
        new(Id: "1", Name: name, Role: UserRole.Operator, NativeLevel: level);

    private static DeviceUsersResult Device(string name, params NvrUser[] users) =>
        new() { DeviceName = name, Users = users };

    [Fact]
    public void PlansAWriteForEveryDeviceWithoutTheAccount()
    {
        var plan = UserAddPlan.For(
            [Device("NVR 1", User("admin")), Device("NVR 2", User("admin"))],
            "jordan", UserRole.Operator);

        Assert.Equal(["NVR 1", "NVR 2"], plan.Creates);
        Assert.Empty(plan.AlreadyPresent);
        Assert.True(plan.HasWork);
        Assert.False(plan.IsPartial);
    }

    [Fact]
    public void LeavesADeviceThatAlreadyHasTheNameOutOfTheWrite()
    {
        var plan = UserAddPlan.For(
            [Device("NVR 1", User("jordan")), Device("NVR 2", User("admin"))],
            "jordan", UserRole.Operator);

        Assert.Equal(["NVR 2"], plan.Creates);
        Assert.Equal(["NVR 1 (Operator)"], plan.AlreadyPresent);
    }

    [Fact]
    public void AnExistingAccountAtADifferentLevelIsReportedAndNeverWritten()
    {
        // Add-only must not quietly become a promotion: the operator asked for an Operator,
        // the device holds a Viewer, and the answer is to say so, not to change it.
        var plan = UserAddPlan.For(
            [Device("NVR 1", User("riley", "Viewer"))], "riley", UserRole.Operator);

        Assert.Empty(plan.Creates);
        Assert.Equal(["NVR 1 (Viewer)"], plan.AlreadyPresent);
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void MatchesNamesWithoutRegardToCase()
    {
        var plan = UserAddPlan.For([Device("NVR 1", User("Riley"))], "riley", UserRole.Operator);

        Assert.Empty(plan.Creates);
        Assert.Single(plan.AlreadyPresent);
    }

    [Fact]
    public void AnUnreadableDeviceMakesThePlanPartialAndIsNeverWritten()
    {
        var plan = UserAddPlan.For(
            [Device("NVR 1", User("admin")), DeviceUsersResult.Failed("NVR 2", "timed out")],
            "jordan", UserRole.Operator);

        Assert.Equal(["NVR 1"], plan.Creates);
        Assert.True(plan.IsPartial);
        Assert.Equal("NVR 2", Assert.Single(plan.Unreadable).DeviceName);
    }

    [Fact]
    public void HasNoWorkWhenEveryReadableDeviceAlreadyHasTheAccount()
    {
        var plan = UserAddPlan.For(
            [Device("NVR 1", User("jordan")), Device("NVR 2", User("jordan"))],
            "jordan", UserRole.Operator);

        Assert.False(plan.HasWork);
        Assert.Equal(2, plan.AlreadyPresent.Count);
    }

    [Fact]
    public void KeepsTheNameTrimmedAsAskedFor()
    {
        var plan = UserAddPlan.For([Device("NVR 1")], "  jordan  ", UserRole.Viewer);

        Assert.Equal("jordan", plan.Name);
        Assert.Equal(UserRole.Viewer, plan.Role);
    }

    [Fact]
    public void RefusesABlankName()
    {
        Assert.Throws<ArgumentException>(
            () => UserAddPlan.For([Device("NVR 1")], "   ", UserRole.Operator));
    }
}
