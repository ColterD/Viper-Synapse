using System.Globalization;
using System.Text.Json;
using Tmds.DBus.Protocol;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Cli;

static class OnboardCommands
{
    const string BusName = "org.vipersynapse.Daemon";
    const string DaemonPath = "/org/vipersynapse/Daemon";
    const string Interface = "org.vipersynapse.Daemon1";

    const string Usage = """
        viper-cli onboard buttons
        viper-cli onboard [--serial SERIAL] list
        viper-cli onboard --serial SERIAL create SLOT
        viper-cli onboard --serial SERIAL select SLOT
        viper-cli onboard --serial SERIAL delete SLOT --confirm
        viper-cli onboard --serial SERIAL rename SLOT NAME
        viper-cli onboard [--serial SERIAL] read SLOT BUTTON LAYER
        viper-cli onboard --serial SERIAL key SLOT BUTTON LAYER USAGE [MODIFIERS]
        viper-cli onboard --serial SERIAL disable SLOT BUTTON LAYER
        viper-cli onboard --serial SERIAL restore SLOT BUTTON

        SLOT is 1–5. BUTTON, USB keyboard USAGE (04–73) and modifier mask are hex bytes.
        LAYER is normal or hypershift. Restore resets the normal layer to its documented factory binding.
        Rename saves a local label for this mouse and slot.
        Use wired USB or HyperSpeed and select the target slot before changing its bindings.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args is ["buttons"])
        {
            foreach (var button in BasiliskOnboard.Buttons)
                Console.WriteLine($"{button.Id:X2}  {button.Name}");
            return 0;
        }
        if (!TryParse(args, out var request))
        {
            Console.Error.WriteLine(Usage);
            return 64;
        }

        try
        {
            using var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
            await connection.ConnectAsync().ConfigureAwait(false);
            string json = await connection.CallMethodAsync(BuildRequest(connection, request),
                static (Message message, object? _) => message.GetBodyReader().ReadString(), null).ConfigureAwait(false);
            PrintResult(request, BasiliskOnboard.ReadResult(json));
            return 0;
        }
        catch (Exception e) when (e is DBusConnectionException or DBusErrorReplyException or InvalidOperationException
            or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static bool TryParse(string[] args, out OnboardRequest request)
    {
        request = new(OnboardOperation.List);
        string? serial = null;
        if (args is ["--serial", var target, .. var tail])
        {
            if (string.IsNullOrWhiteSpace(target) || target.Length > 22 || target.Any(char.IsControl))
                return false;
            serial = target;
            args = tail;
        }
        if (args is ["list"])
        {
            request = new(OnboardOperation.List, Serial: serial);
            return true;
        }
        if (args.Length < 2 || (serial == null && args[0] != "read") ||
            !byte.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte slot)
            || slot is < 1 or > 5)
            return false;

        switch (args)
        {
            case ["create", _]:
                request = new(OnboardOperation.Create, slot, Serial: serial);
                return true;
            case ["select", _]:
                request = new(OnboardOperation.Select, slot, Serial: serial);
                return true;
            case ["delete", _, "--confirm"] when slot != 1:
                request = new(OnboardOperation.Delete, slot, Serial: serial, ConfirmDelete: true);
                return true;
            case ["rename", _, var name] when ValidName(name):
                request = new(OnboardOperation.Rename, slot, Serial: serial, Name: name.Trim());
                return true;
        }

        if (args.Length < 3 || !TryHex(args[2], out byte button) || !BasiliskOnboard.Buttons.Any(b => b.Id == button))
            return false;
        if (args is ["restore", _, _])
        {
            request = new(OnboardOperation.Restore, slot, button, Serial: serial);
            return true;
        }
        if (args.Length < 4 || args[3] is not ("normal" or "hypershift"))
            return false;
        byte layer = args[3] == "hypershift" ? (byte)1 : (byte)0;
        switch (args)
        {
            case ["read", _, _, _]:
                request = new(OnboardOperation.ReadBinding, slot, button, layer, Serial: serial);
                return true;
            case ["disable", _, _, _]:
                request = new(OnboardOperation.Disable, slot, button, layer, Serial: serial);
                return true;
            case ["key", _, _, _, var usageText, .. var extras] when extras.Length <= 1:
                byte modifiers = 0;
                if (!TryHex(usageText, out byte usage) || usage is < 0x04 or > 0x73
                    || (extras.Length == 1 && !TryHex(extras[0], out modifiers)))
                    return false;
                request = new(OnboardOperation.SetKeyboard, slot, button, layer, usage, modifiers, serial);
                return true;
            default:
                return false;
        }
    }

    static bool ValidName(string name) => name.Trim().Length is >= 1 and <= 80 && !name.Any(char.IsControl);

    static bool TryHex(string value, out byte parsed)
    {
        var digits = value.AsSpan();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];
        parsed = 0;
        return digits.Length is 1 or 2 &&
            byte.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed);
    }

    static MessageBuffer BuildRequest(DBusConnection connection, OnboardRequest request)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: DaemonPath, @interface: Interface, member: "Onboard", signature: "s");
        writer.WriteString(BasiliskOnboard.WriteRequest(request));
        return writer.CreateMessage();
    }

    static void PrintResult(OnboardRequest request, OnboardResult result)
    {
        Console.WriteLine($"Basilisk V4 Pro {Printable(result.Serial)}");
        if (result.Binding is { } binding)
        {
            var button = BasiliskOnboard.Buttons.First(b => b.Id == request.Button);
            string layer = binding.Layer == 1 ? "hypershift" : "normal";
            if (request.Layer == 1 && binding.Layer == 0)
                layer += " (inherited by hypershift)";
            Console.WriteLine($"Slot {request.Slot} · {button.Name} ({button.Id:X2}) · {layer} · {Convert.ToHexString(binding.Block)}");
            return;
        }
        foreach (byte slot in result.Slots)
        {
            string name = result.Names?.TryGetValue(slot, out string? label) == true ? $" · {Printable(label)}" : "";
            Console.WriteLine($"{(slot == result.ActiveSlot ? '*' : ' ')} {slot}{name}");
        }
    }

    static string Printable(string? value) => value == null ? "" : string.Concat(value.Where(c => !char.IsControl(c)));
}
