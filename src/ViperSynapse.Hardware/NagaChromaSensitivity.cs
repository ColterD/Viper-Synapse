using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Hardware;

internal static class NagaChromaSensitivity
{
    internal const byte TransactionId = 0xFF;

    internal static RazerReport ReadRequest() => Commands.Request(TransactionId, Commands.GetDpi, DeviceCommands.NoStore);

    internal static DpiStage? Decode(RazerReport reply)
    {
        if (reply.DataSize < 5)
            return null;
        var args = reply.Arguments;
        int x = (args[1] << 8) | args[2], y = (args[3] << 8) | args[4];
        return IsValid(x, y) ? new DpiStage(x, y) : null;
    }

    internal static bool IsValid(int x, int y) => x is >= 100 and <= 16000 && y is >= 100 and <= 16000;
}
