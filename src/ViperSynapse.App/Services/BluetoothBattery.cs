using System.Net.NetworkInformation;
using Tmds.DBus.Protocol;

namespace ViperSynapse.App.Services;

/// <summary>Reads an optional Bluetooth battery percentage from BlueZ for a connected peer.</summary>
internal static class BluetoothBattery
{
    const string PortPrefix = "bluetooth:";
    const string DeviceInterface = "org.bluez.Device1", BatteryInterface = "org.bluez.Battery1";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<int?> ReadAsync(string port)
    {
        if (PortAddress(port) == null || DBusAddress.System is not { } busAddress)
            return null;
        try
        {
            using var connection = new DBusConnection(busAddress);
            await connection.ConnectAsync().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
            return await connection.CallMethodAsync(BuildRequest(connection),
                static (Message message, object? state) => ReadReply(message, (string)state!), port)
                .WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusExceptionBase or TimeoutException or IOException)
        {
            return null;
        }
    }

    static MessageBuffer BuildRequest(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.bluez", path: "/",
            @interface: "org.freedesktop.DBus.ObjectManager", member: "GetManagedObjects");
        return writer.CreateMessage();
    }

    static int? ReadReply(Message message, string port)
    {
        if (message.SignatureAsString != "a{oa{sa{sv}}}")
            return null;
        var reader = message.GetBodyReader();
        var managedObjects = new List<Dictionary<string, Dictionary<string, VariantValue>>>();
        var objects = reader.ReadDictionaryStart();
        while (reader.HasNext(objects))
        {
            _ = reader.ReadObjectPath();
            var propertiesByInterface = new Dictionary<string, Dictionary<string, VariantValue>>(StringComparer.Ordinal);
            var interfaces = reader.ReadDictionaryStart();
            while (reader.HasNext(interfaces))
            {
                string name = reader.ReadString();
                var properties = reader.ReadDictionaryOfStringToVariantValue();
                if (name is DeviceInterface or BatteryInterface)
                    propertiesByInterface[name] = properties;
            }
            managedObjects.Add(propertiesByInterface);
        }
        return SelectPercentage(port, managedObjects);
    }

    internal static int? SelectPercentage(string port,
        IEnumerable<Dictionary<string, Dictionary<string, VariantValue>>> managedObjects)
    {
        if (PortAddress(port) is not { } peer)
            return null;
        int? percentage = null;
        bool foundPeer = false;
        foreach (var interfaces in managedObjects)
        {
            if (!interfaces.TryGetValue(DeviceInterface, out var device) ||
                !device.TryGetValue("Connected", out var connected) || connected.Type != VariantValueType.Bool || !connected.GetBool() ||
                !device.TryGetValue("Address", out var address) || address.Type != VariantValueType.String ||
                !PhysicalAddress.TryParse(address.GetString(), out var current) || !peer.Equals(current))
                continue;
            // The Linux port gives us the remote address alone. Two matching connected adapters
            // make it ambiguous which reading belongs to this port.
            if (foundPeer)
                return null;
            foundPeer = true;
            if (!interfaces.TryGetValue(BatteryInterface, out var battery) ||
                !battery.TryGetValue("Percentage", out var charge))
                continue;
            int value = charge.Type switch
            {
                VariantValueType.Byte => charge.GetByte(),
                VariantValueType.UInt16 => charge.GetUInt16(),
                _ => -1,
            };
            if (value is >= 0 and <= 100)
                percentage = value;
        }
        return percentage;
    }

    static PhysicalAddress? PortAddress(string port)
    {
        if (!port.StartsWith(PortPrefix, StringComparison.Ordinal) ||
            !PhysicalAddress.TryParse(port.AsSpan(PortPrefix.Length), out var address) ||
            address.GetAddressBytes().Length != 6)
            return null;
        return address;
    }
}
