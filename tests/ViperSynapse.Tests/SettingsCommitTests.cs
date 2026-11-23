using ViperSynapse.Core.Bindings;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public sealed class SettingsCommitTests : IDisposable
{
    readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("viper-settings-commit-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_legacy_write_invalidates_a_snapshot_and_rejects_a_stale_commit()
    {
        var store = new SettingsStore(_directory.FullName);
        var snapshot = store.DeviceSnapshot("02eb");
        var original = SettingsJson.ReadSettings(snapshot.Json);
        var legacy = original with { Profiles = [original.Active with { Name = "Legacy edit" }] };
        store.SaveDevice("02eb", legacy);

        var stale = store.CommitDevice("02eb", snapshot.Revision, original);
        Assert.False(stale.Saved);
        Assert.Equal("Legacy edit", store.LoadDevice("02eb").Active.Name);
        var next = store.CommitDevice("02eb", stale.Revision, original);
        Assert.True(next.Saved);
        Assert.NotEqual(stale.Revision, next.Revision);
    }

    [Fact]
    public void Revisions_are_scoped_to_a_resource_and_do_not_survive_service_restarts()
    {
        var store = new SettingsStore(_directory.FullName);
        var macros = store.MacrosSnapshot();
        var shortcuts = store.ShortcutsSnapshot();
        Assert.True(store.CommitMacros(macros.Revision, MacroLibrary.Empty).Saved);
        Assert.False(store.CommitMacros(macros.Revision, MacroLibrary.Empty).Saved);
        Assert.True(store.CommitShortcuts(shortcuts.Revision, ShortcutList.Empty).Saved);
        var replacement = new SettingsStore(_directory.FullName);
        Assert.False(replacement.CommitShortcuts(store.ShortcutsSnapshot().Revision, ShortcutList.Empty).Saved);
    }

    [Fact]
    public void Rebase_keeps_an_automatic_profile_switch_but_rejects_competing_edits()
    {
        var original = DeviceSettings.Create();
        var alternate = Profile.Create("Game");
        original = original with { Profiles = [original.Active, alternate] };
        var edited = original with { Profiles = [original.Active with { Name = "Edited" }, alternate] };
        var switched = original with { ActiveProfile = alternate.Id };
        var rebased = DaemonClient.RebaseOverProfileSelection(SettingsJson.Write(switched), original, edited);
        Assert.NotNull(rebased);
        Assert.Equal(alternate.Id, rebased.ActiveProfile);
        Assert.Equal("Edited", rebased.Profiles[0].Name);
        Assert.Null(DaemonClient.RebaseOverProfileSelection(SettingsJson.Write(edited), original, edited));
    }

    [Fact]
    public async Task A_stale_owner_that_will_not_quit_is_never_treated_as_ready()
    {
        int quits = 0, starts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => DaemonClient.EnsureMatchingBuildAsync("current",
            () => Task.FromResult(true), () => Task.FromResult<string?>("old"),
            () => { quits++; return Task.CompletedTask; }, () => starts++, () => null, TimeSpan.Zero, TimeSpan.Zero,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, quits);
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData("old", false)]
    [InlineData("current", true)]
    public async Task A_replacement_must_report_the_matching_build(string build, bool matches)
    {
        bool running = true;
        var startup = DaemonClient.EnsureMatchingBuildAsync("current", () => Task.FromResult(running),
            () => Task.FromResult<string?>(running ? "old" : build), () => { running = false; return Task.CompletedTask; },
            () => Assert.False(running), () => null, TimeSpan.Zero, TimeSpan.Zero, TestContext.Current.CancellationToken);
        if (matches)
            await startup;
        else
            await Assert.ThrowsAsync<TimeoutException>(() => startup);
    }

    [Fact]
    public void A_rejected_keymap_is_reported_and_retried_on_the_next_apply()
    {
        var firmware = new KeymapFirmware();
        using var control = new DeviceControl(new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero }, 0x1F,
            DeviceCatalogue.Default.Find(0x02eb));
        var profile = Profile.Create("Default");
        var first = control.ApplyWithResult("usb-1", "02eb", "KEYPAD", profile, 1, refreshing: false);
        Assert.Equal(UnitApplyStatus.Failed, first.Status);
        Assert.Contains("keymap", first.Detail);
        firmware.Reject = false;
        var second = control.ApplyWithResult("usb-1", "02eb", "KEYPAD", profile, 1, refreshing: false);
        Assert.Equal(UnitApplyStatus.Acknowledged, second.Status);
        Assert.Equal(1, second.AcknowledgedCommands);
        Assert.Equal(2, firmware.Requests);
    }

    sealed class KeymapFirmware : IHidFeatureDevice
    {
        byte[] _pending = [];
        public string Path => "/emulated/keymap";
        public bool Reject { get; set; } = true;
        public int Requests { get; private set; }

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            Requests++;
            _pending = report.ToArray();
        }

        public int ReceiveFeature(Span<byte> report)
        {
            _pending.CopyTo(report);
            report[1] = (byte)(Reject ? ReportStatus.Failure : ReportStatus.Successful);
            return report.Length;
        }

        public void Dispose() { }
    }
}
