using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;

namespace ViperSynapse.Service;

public sealed partial class DeviceControl
{
    // OpenRazer uses transaction FF for both device-mode requests on the BlackWidow Chroma V2.
    const byte BlackWidowModeTransaction = 0xFF;
    const byte RegularMode = 0x00;
    const byte DriverMode = 0x03;
    bool _nativeMacroModeLeased;
    bool _ownsBlackWidowMode;

    internal IDisposable EnableNativeMacroKeys()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (model?.ProductId != LegacyRemapControls.BlackWidowChromaV2)
                throw new NotSupportedException("Native macro mode is only supported on the BlackWidow Chroma V2.");
            if (_nativeMacroModeLeased)
                throw new IOException("BlackWidow native macro mode already has an active input session.");

            if (ReadBlackWidowMode() == RegularMode)
            {
                // Retain ownership until regular mode is confirmed, even if cleanup needs another attempt.
                _ownsBlackWidowMode = true;
                try
                {
                    SetBlackWidowMode(DriverMode);
                }
                catch (IOException enableError)
                {
                    // The write may have applied even if its acknowledgement or first readback was lost.
                    try { RestoreOwnedBlackWidowMode(); }
                    catch (IOException restoreError)
                    {
                        throw new IOException($"{enableError.Message} Mode recovery failed. {restoreError.Message}",
                            new AggregateException(enableError, restoreError));
                    }
                    throw;
                }
            }

            _nativeMacroModeLeased = true;
            return new MacroModeLease(EndBlackWidowMacroMode);
        }
    }

    byte ReadBlackWidowMode()
    {
        var request = Commands.Request(BlackWidowModeTransaction, Commands.GetDeviceMode);
        var reply = transport.Send(request);
        if (!RazerProbe.Accepts(request, reply, strict: true) || reply.RemainingPackets != 0 ||
            reply.ProtocolType != 0 || reply.DataSize != 2)
            throw new IOException("The BlackWidow did not return a valid device-mode report.");
        if (reply.Arguments[1] != 0 || reply.Arguments[0] is not (RegularMode or DriverMode))
            throw new IOException("The BlackWidow is in an unrecognized device mode.");
        return reply.Arguments[0];
    }

    void SetBlackWidowMode(byte mode)
    {
        IOException? writeError = null;
        try { transport.Send(RazerReport.Create(BlackWidowModeTransaction, 0x00, 0x04, mode, 0x00)); }
        catch (IOException error) { writeError = error; }

        // Mode readback is authoritative, including when firmware did not acknowledge the write.
        if (ReadBlackWidowMode() != mode)
            throw new IOException("The BlackWidow did not confirm its requested device mode.", writeError);
    }

    void RestoreOwnedBlackWidowMode()
    {
        if (!_ownsBlackWidowMode)
            return;
        if (ReadBlackWidowMode() == DriverMode)
            SetBlackWidowMode(RegularMode);
        _ownsBlackWidowMode = false;
    }

    void EndBlackWidowMacroMode()
    {
        lock (_gate)
        {
            _nativeMacroModeLeased = false;
            if (!_disposed)
                RestoreOwnedBlackWidowMode();
        }
    }

    void FinishBlackWidowMode()
    {
        try { RestoreOwnedBlackWidowMode(); }
        catch (IOException error)
        {
            LastProblem = $"Could not restore BlackWidow keyboard mode: {error.Message}";
            Console.Error.WriteLine(LastProblem);
        }
    }

    sealed class MacroModeLease(Action release) : IDisposable
    {
        Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
