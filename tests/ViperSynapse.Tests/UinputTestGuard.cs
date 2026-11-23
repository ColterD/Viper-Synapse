using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

internal static class UinputTestGuard
{
    const string OptInVariable = "VIPER_UINPUT_TESTS";

    internal static bool ProbeIfEnabled(string? optIn, Func<bool> probe) => optIn == "1" && probe();

    internal static void Require()
    {
        var optIn = Environment.GetEnvironmentVariable(OptInVariable);
        Assert.SkipUnless(ProbeIfEnabled(optIn, UinputAvailable),
            optIn == "1" ? "/dev/uinput is not available" : $"Enable kernel input tests with {OptInVariable}=1");
    }

    static bool UinputAvailable()
    {
        try
        {
            using var probe = UinputDevice.Create("viper-synapse-probe");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
