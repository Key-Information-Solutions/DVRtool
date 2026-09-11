using DVRTool.Core.Updates;
using Xunit;

namespace DVRTool.Tests;

public class UpdateDecisionTests
{
    private static readonly Uri Msi = new("https://github.com/x/y/releases/download/v1.1.0/DVRTool-1.1.0.msi");
    private static readonly Uri Release = new("https://github.com/x/y/releases/tag/v1.1.0");

    private static UpdateManifest Manifest(string version, string? min = null) => new(
        version, $"DVRTool-{version}.msi", 1000, new string('a', 64), null, null, min, null);

    [Fact]
    public void Unversioned_legacy_install_is_offered_the_first_release()
    {
        var r = UpdateDecision.Decide(new Version(1, 0, 0), Manifest("1.1.0"), Msi, Release, null);
        var available = Assert.IsType<UpdateCheck.Available>(r);
        Assert.Equal(new Version(1, 1, 0), available.Manifest.Version);
    }

    [Fact]
    public void Same_or_newer_install_is_up_to_date()
    {
        Assert.IsType<UpdateCheck.UpToDate>(
            UpdateDecision.Decide(new Version(1, 1, 0), Manifest("1.1.0"), Msi, Release, null));
        Assert.IsType<UpdateCheck.UpToDate>(
            UpdateDecision.Decide(new Version(1, 2, 0), Manifest("1.1.0"), Msi, Release, null));
    }

    [Fact]
    public void Skipped_version_is_reported_as_skipped_but_a_later_one_is_offered()
    {
        Assert.IsType<UpdateCheck.Skipped>(
            UpdateDecision.Decide(new Version(1, 0, 0), Manifest("1.1.0"), Msi, Release, new Version(1, 1, 0)));
        Assert.IsType<UpdateCheck.Available>(
            UpdateDecision.Decide(new Version(1, 0, 0), Manifest("1.1.1"), Msi, Release, new Version(1, 1, 0)));
    }

    [Fact]
    public void Release_that_cannot_upgrade_this_install_fails_with_a_reason()
    {
        var r = UpdateDecision.Decide(new Version(1, 0, 0), Manifest("2.0.0", min: "1.5.0"), Msi, Release, null);
        var failed = Assert.IsType<UpdateCheck.Failed>(r);
        Assert.Contains("1.5.0", failed.Reason);
        Assert.False(failed.SignatureRejected);
    }

    [Fact]
    public void Policy_refuses_a_copy_outside_the_install_dir()
    {
        Assert.True(UpdatePolicy.IsUnder(@"C:\Program Files\DVRTool\app\DVRTool.exe", @"C:\Program Files\DVRTool\"));
        Assert.True(UpdatePolicy.IsUnder(@"C:\Program Files\DVRTool\cli\dvrtool.exe", @"C:\Program Files\DVRTool"));
        Assert.False(UpdatePolicy.IsUnder(@"E:\DVRtool\src\DVRTool.App\bin\Debug\DVRTool.exe", @"C:\Program Files\DVRTool\"));
        Assert.False(UpdatePolicy.IsUnder(@"C:\Program Files\DVRTool2\app\DVRTool.exe", @"C:\Program Files\DVRTool\"));
        Assert.False(UpdatePolicy.IsUnder(@"C:\x\DVRTool.exe", null));

        var notInstalled = new UpdatePolicy(false, true, null, null, null, null);
        Assert.Contains("Not an installed copy", notInstalled.DisabledReason);
        var disabled = new UpdatePolicy(true, false, @"C:\Program Files\DVRTool\", null, null, "1.1.0");
        Assert.Contains("policy", disabled.DisabledReason);
        Assert.Null(new UpdatePolicy(true, true, @"C:\Program Files\DVRTool\", null, null, "1.1.0").DisabledReason);
    }

    [Fact]
    public void Settings_decide_when_a_check_is_due()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(new UpdateSettings().IsCheckDue(now));
        Assert.False(new UpdateSettings { LastCheck = now.AddHours(-2) }.IsCheckDue(now));
        Assert.True(new UpdateSettings { LastCheck = now.AddHours(-25) }.IsCheckDue(now));
        Assert.True(new UpdateSettings { LastCheck = now.AddDays(3) }.IsCheckDue(now), "a clock that moved back");
        Assert.False(new UpdateSettings { AutoCheck = false }.IsCheckDue(now));
    }

    [Fact]
    public void Settings_round_trip_and_a_corrupt_file_reads_as_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "dvrtool-tests", Guid.NewGuid().ToString("N"), "updates.json");
        try
        {
            var s = new UpdateSettings { SkippedVersionText = "1.1.0", LastCheck = DateTimeOffset.UnixEpoch };
            Assert.True(s.TrySave(path));
            var back = UpdateSettings.Load(path);
            Assert.Equal(new Version(1, 1, 0), back.SkippedVersion);
            Assert.Equal(DateTimeOffset.UnixEpoch, back.LastCheck);

            File.WriteAllText(path, "{ not json");
            Assert.Null(UpdateSettings.Load(path).SkippedVersion);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Installer_command_is_msiexec_then_relaunch()
    {
        string cmd = UpdateInstaller.BuildCommand(@"C:\u\DVRTool-1.1.0.msi", @"C:\Program Files\DVRTool\app\DVRTool.exe");
        Assert.Equal(
            "msiexec /i \"C:\\u\\DVRTool-1.1.0.msi\" /passive /norestart && start \"\" \"C:\\Program Files\\DVRTool\\app\\DVRTool.exe\"",
            cmd);
        Assert.Equal("msiexec /i \"C:\\u\\x.msi\" /qn /norestart", UpdateInstaller.BuildCommand(@"C:\u\x.msi", null, passive: false));
    }
}
