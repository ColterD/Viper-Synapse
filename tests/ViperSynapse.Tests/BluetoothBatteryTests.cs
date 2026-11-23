using Tmds.DBus.Protocol;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.Tests;

public sealed class BluetoothBatteryTests
{
    const string Port = "bluetooth:010203040506";

    [Fact]
    public void Connected_matching_peer_is_the_only_source_of_battery_percentage()
    {
        var sameAddressDisconnected = Peer("01:02:03:04:05:06", false, (byte)80);
        var otherPeer = Peer("01:02:03:04:05:07", true, (byte)42);
        var sameAddressConnected = Peer("01:02:03:04:05:06", true, (byte)0);

        Assert.Equal(0, BluetoothBattery.SelectPercentage(Port, [sameAddressDisconnected, otherPeer, sameAddressConnected]));
        Assert.Null(BluetoothBattery.SelectPercentage(Port, [sameAddressDisconnected, otherPeer]));
        Assert.Null(BluetoothBattery.SelectPercentage("usb-010203040506", [sameAddressConnected]));
    }

    [Fact]
    public void Missing_or_invalid_battery_is_unknown_and_ambiguous_connected_adapters_are_ignored()
    {
        const string address = "01:02:03:04:05:06";
        Assert.Equal(100, BluetoothBattery.SelectPercentage(Port, [Peer(address, true, (ushort)100)]));
        Assert.Null(BluetoothBattery.SelectPercentage(Port, [Peer(address, true, null)]));
        Assert.Null(BluetoothBattery.SelectPercentage(Port, [Peer(address, true, "42")]));
        Assert.Null(BluetoothBattery.SelectPercentage(Port, [Peer(address, true, (byte)255)]));
        Assert.Null(BluetoothBattery.SelectPercentage(Port, [Peer(address, true, (byte)27), Peer(address, true, (byte)28)]));
    }

    [Fact]
    public Task A_delayed_bluetooth_or_usb_battery_reply_cannot_leak_across_transport_changes() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var bluetooth = new DeviceInfo("00e3", "00e4", "Basilisk", "mouse", Port, true, false,
                null, null, null, [], false, null, Connection: "bluetooth");
            var wired = bluetooth with { ProductId = "00e2", Port = "usb-test", Connection = "wired" };
            var device = new DeviceViewModel(bluetooth, main);

            var pendingBluez = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bluetoothRefresh = device.RefreshBatteryAsync(_ => pendingBluez.Task);
            device.Info = wired;
            await device.RefreshBatteryAsync(readUsb: _ => Task.FromResult<DeviceState?>(
                new(null, 0, 0, 20, true)));
            pendingBluez.SetResult(75);
            await bluetoothRefresh;
            Assert.Equal(20, device.BatteryPercent);
            Assert.True(device.Charging);

            var pendingUsb = new TaskCompletionSource<DeviceState?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wiredRefresh = device.RefreshBatteryAsync(readUsb: _ => pendingUsb.Task);
            device.Info = bluetooth;
            Assert.Null(device.BatteryPercent);
            Assert.False(device.Charging);
            await device.RefreshBatteryAsync(_ => Task.FromResult<int?>(55));
            pendingUsb.SetResult(new(null, 0, 0, 20, true));
            await wiredRefresh;
            Assert.Equal(55, device.BatteryPercent);
            Assert.False(device.Charging);

            await device.RefreshBatteryAsync(_ => Task.FromResult<int?>(null),
                readUsb: _ => throw new InvalidOperationException("A Bluetooth mouse must not read USB battery state."));
            Assert.Null(device.BatteryPercent);
            Assert.False(device.BatteryKnown);
            device.Info = wired;
            return true;
        }, CancellationToken.None);

    static Dictionary<string, Dictionary<string, VariantValue>> Peer(string address, bool connected, VariantValue? percentage)
    {
        var result = new Dictionary<string, Dictionary<string, VariantValue>> {
            ["org.bluez.Device1"] = new() { ["Address"] = address, ["Connected"] = connected },
        };
        if (percentage is { } value)
            result["org.bluez.Battery1"] = new() { ["Percentage"] = value };
        return result;
    }
}
