namespace ViperSynapse.Tests;

public sealed class UinputTestGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void A_default_kernel_test_cannot_probe_uinput(string? optIn)
    {
        bool probed = false;
        bool available = UinputTestGuard.ProbeIfEnabled(optIn, () => probed = true);

        Assert.False(available);
        Assert.False(probed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_opt_in_checks_whether_uinput_is_available(bool expected)
    {
        int probes = 0;
        bool available = UinputTestGuard.ProbeIfEnabled("1", () =>
        {
            probes++;
            return expected;
        });

        Assert.Equal(expected, available);
        Assert.Equal(1, probes);
    }
}
