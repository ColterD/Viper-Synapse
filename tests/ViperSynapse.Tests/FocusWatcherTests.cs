using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class FocusWatcherTests
{
    [Theory]
    [InlineData("instance_123", "/tmp/runtime", "/tmp/runtime/hypr/instance_123/.socket2.sock")]
    [InlineData("abc-def.ghi", "/run/user/1000", "/run/user/1000/hypr/abc-def.ghi/.socket2.sock")]
    public void Hyprland_socket_uses_the_runtime_and_one_signature_component(
        string signature, string runtime, string expected)
    {
        Assert.Equal(expected, FocusWatcher.ResolveHyprlandSocketPath(signature, runtime, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("parent/child")]
    [InlineData("parent\\child")]
    [InlineData("name\0suffix")]
    public void Hyprland_socket_rejects_blank_or_noncomponent_signatures(string? signature)
    {
        Assert.Null(FocusWatcher.ResolveHyprlandSocketPath(signature, "/tmp/runtime", null));
    }

    [Fact]
    public void Hyprland_socket_falls_back_to_the_numeric_user_runtime()
    {
        Assert.Equal("/run/user/1000/hypr/instance/.socket2.sock",
            FocusWatcher.ResolveHyprlandSocketPath("instance", null, "1000"));
    }

    [Theory]
    [InlineData("relative/runtime", "1000")]
    [InlineData("/tmp/run\0time", "1000")]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "user")]
    [InlineData(null, "10a0")]
    public void Hyprland_socket_rejects_invalid_runtime_configuration(string? runtime, string? uid)
    {
        Assert.Null(FocusWatcher.ResolveHyprlandSocketPath("instance", runtime, uid));
    }
}
