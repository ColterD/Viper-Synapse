using System.Reflection;

namespace ViperSynapse.App.Services;

internal static class AppBranding
{
    public const string Name = "Viper Relay";

    public static string Version { get; } = ReadVersion();

    static string ReadVersion()
    {
        var assembly = typeof(AppBranding).Assembly;
        // The SDK appends the source revision as SemVer build metadata.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
