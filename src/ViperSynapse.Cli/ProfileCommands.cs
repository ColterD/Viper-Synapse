using System.Text;
using Tmds.DBus.Protocol;

namespace ViperSynapse.Cli;

static class ProfileCommands
{
    const string BusName = "org.vipersynapse.Daemon";
    const string DaemonPath = "/org/vipersynapse/Daemon";
    const string Interface = "org.vipersynapse.Daemon1";

    public static async Task<int> RunAsync(string[] args)
    {
        string operation = args[0], device = args[1], file = args[2];
        string[] options = args[3..];
        bool export = operation == "export";
        if (device.Length != 4 || !device.All(Uri.IsHexDigit) || (export
            ? options.Length > 1 || options.Length == 1 && !Guid.TryParse(options[0], out _)
            : operation != "import" || options.Any(option => option is not ("--replace" or "--allow-launch"))
                || options.Distinct().Count() != options.Length))
        {
            Console.Error.WriteLine("Use profile export DEVICE FILE [PROFILE_ID] or profile import DEVICE FILE [--replace] [--allow-launch]. DEVICE is a four-digit hex settings key.");
            return 64;
        }

        try
        {
            using var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
            await connection.ConnectAsync().ConfigureAwait(false);
            if (export)
            {
                string json = await connection.CallMethodAsync(BuildExport(connection, device, options.FirstOrDefault() ?? ""),
                    static (Message message, object? _) => message.GetBodyReader().ReadString(), null).ConfigureAwait(false);
                await WriteArchiveAsync(file, json).ConfigureAwait(false);
            }
            else
            {
                string json = ReadArchive(file);
                await connection.CallMethodAsync(BuildImport(connection, device, json, options.Contains("--replace"),
                    options.Contains("--allow-launch"))).ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception e) when (e is DBusConnectionException or DBusErrorReplyException or InvalidOperationException
            or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static string ReadArchive(string path)
    {
        const int maxJsonChars = 8 * 1024 * 1024;
        using var reader = new StreamReader(path);
        var json = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            if (json.Length + read > maxJsonChars)
                throw new InvalidDataException("The profile archive exceeds the maximum size.");
            json.Append(buffer, 0, read);
        }
        return json.ToString();
    }

    static async Task WriteArchiveAsync(string path, string json)
    {
        path = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".viper-profile-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsLinux())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(temporary, options);
        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(json).ConfigureAwait(false);
            File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    static MessageBuffer BuildExport(DBusConnection connection, string device, string profileId)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: DaemonPath, @interface: Interface, member: "ExportProfile", signature: "ss");
        writer.WriteString(device);
        writer.WriteString(profileId);
        return writer.CreateMessage();
    }

    static MessageBuffer BuildImport(DBusConnection connection, string device, string json, bool replace, bool allowLaunch)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: DaemonPath, @interface: Interface, member: "ImportProfile", signature: "ssbb");
        writer.WriteString(device);
        writer.WriteString(json);
        writer.WriteBool(replace);
        writer.WriteBool(allowLaunch);
        return writer.CreateMessage();
    }
}
