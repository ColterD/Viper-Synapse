using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Service;

public sealed partial class DeviceControl
{
    internal OnboardResult ManageOnboard(string serial, OnboardRequest request)
    {
        ValidateOnboardRequest(request);
        lock (_gate)
        {
            if (_disposed || !HasOnboardProfiles)
                throw new NotSupportedException("An online wired or HyperSpeed Basilisk V4 Pro is required.");

            LastProblem = null;
            var state = ReadOnboardSlots(serial);
            if (request.Operation == OnboardOperation.List)
                return state;

            if (request.Operation is OnboardOperation.Create or OnboardOperation.Select or OnboardOperation.Delete)
                return ChangeOnboardSlot(state, request);

            if (!state.Slots.Contains(request.Slot))
                throw new InvalidDataException($"Onboard slot {request.Slot} does not exist.");
            if (request.Operation == OnboardOperation.Rename)
                return state;

            var button = BasiliskOnboard.Buttons.SingleOrDefault(button => button.Id == request.Button)
                ?? throw new InvalidDataException("This button has no verified Basilisk V4 Pro assignment.");
            bool reading = request.Operation == OnboardOperation.ReadBinding;
            if (!reading && request.Slot != state.ActiveSlot)
                throw new InvalidDataException($"Select onboard slot {request.Slot} before editing its button assignments.");

            var before = ReadOnboardBinding(request, allowInherited: true);
            if (reading)
                return state with { Binding = before };

            var expected = request.Operation switch
            {
                OnboardOperation.SetKeyboard => BasiliskOnboard.Keyboard(request.Usage, request.Modifiers),
                OnboardOperation.Disable => BasiliskOnboard.Disabled(),
                OnboardOperation.Restore => button.Factory.ToArray(),
                _ => throw new InvalidDataException("Invalid onboard button operation."),
            };
            if (expected.Length != 7)
                throw new InvalidDataException("No verified factory assignment is available for this button.");
            if (before.Layer == request.Layer && before.Block.AsSpan().SequenceEqual(expected))
                return state with { Binding = before };

            if (ReadOnboardProfile() != request.Slot)
                throw new IOException("The active onboard slot changed before the assignment could be written.");
            if (!Send(BasiliskOnboard.WriteBinding(ControlTransactionId, request.Slot, request.Button, request.Layer, expected),
                "the onboard button assignment"))
                throw new IOException(LastProblem);

            var after = ReadOnboardBinding(request, allowInherited: false, expected: expected);
            var confirmed = ReadOnboardSlots(serial);
            if (confirmed.ActiveSlot != request.Slot || !confirmed.Slots.SequenceEqual(state.Slots))
                throw new IOException("The onboard profile state changed while confirming the button assignment.");
            return confirmed with { Binding = after };
        }
    }

    static void ValidateOnboardRequest(OnboardRequest request)
    {
        if (!Enum.IsDefined(request.Operation))
            throw new InvalidDataException("Unknown onboard operation.");
        if (request.Operation == OnboardOperation.List ? request.Slot != 0 : request.Slot is < 1 or > 5)
            throw new InvalidDataException("Onboard slots are numbered 1 through 5.");
        if (request.ConfirmDelete != (request.Operation == OnboardOperation.Delete))
            throw new InvalidDataException("Removing an onboard slot requires explicit confirmation.");
        if (request.Operation == OnboardOperation.Rename)
        {
            if (request.Name is not { Length: > 0 and <= 80 } name || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                throw new InvalidDataException("Enter an onboard slot name from 1 to 80 characters.");
        }
        else if (request.Name != null)
            throw new InvalidDataException("A slot name is only accepted when renaming.");

        bool buttonOperation = request.Operation is OnboardOperation.ReadBinding or OnboardOperation.SetKeyboard
            or OnboardOperation.Disable or OnboardOperation.Restore;
        if (buttonOperation ? request.Layer > 1 : request.Button != 0 || request.Layer != 0)
            throw new InvalidDataException("A button assignment must use the normal or HyperShift layer.");
        if (request.Operation == OnboardOperation.Restore && request.Layer != 0)
            throw new InvalidDataException("Verified factory assignments are only available for the normal layer.");
        if (request.Operation == OnboardOperation.SetKeyboard ? request.Usage is < 0x04 or > 0x73
            : request.Usage != 0 || request.Modifiers != 0)
            throw new InvalidDataException("A keyboard assignment requires a supported keyboard HID usage.");
    }

    OnboardResult ReadOnboardSlots(string serial) =>
        TryReadOnboardSlots(serial) ?? throw new IOException("The mouse did not return a valid onboard slot list and active slot.");

    OnboardResult? TryReadOnboardSlots(string serial)
    {
        var reply = ReadReport(BasiliskOnboard.ListSlots(ControlTransactionId));
        if (reply == null || BasiliskOnboard.ParseSlots(reply) is not { } slots)
            return null;
        if (ReadOnboardProfile() is not byte active || !slots.Contains(active))
            return null;
        return new OnboardResult(serial, active, slots);
    }

    OnboardResult ChangeOnboardSlot(OnboardResult before, OnboardRequest request)
    {
        byte slot = request.Slot;
        bool exists = before.Slots.Contains(slot);
        if (request.Operation == OnboardOperation.Create ? exists : !exists)
            throw new InvalidDataException(exists ? $"Onboard slot {slot} already exists." : $"Onboard slot {slot} does not exist.");
        if (request.Operation == OnboardOperation.Delete && (slot == 1 || slot == before.ActiveSlot))
            throw new InvalidDataException("The default slot and the active slot cannot be removed.");
        if (request.Operation == OnboardOperation.Select && slot == before.ActiveSlot)
            return before;
        if (request.Operation == OnboardOperation.Delete && ReadOnboardProfile() != before.ActiveSlot)
            throw new IOException("The active onboard slot changed before the requested slot could be removed.");

        if (!Send(BasiliskOnboard.ChangeSlot(ControlTransactionId, request.Operation, slot), "the onboard slot change"))
            throw new IOException(LastProblem);

        IEnumerable<byte> expected = request.Operation switch
        {
            OnboardOperation.Create => before.Slots.Append(slot).Order(),
            OnboardOperation.Delete => before.Slots.Where(id => id != slot),
            _ => before.Slots,
        };
        byte active = request.Operation == OnboardOperation.Select ? slot : before.ActiveSlot;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(25);
            if (TryReadOnboardSlots(before.Serial) is { } after &&
                after.ActiveSlot == active && after.Slots.SequenceEqual(expected))
                return after;
        }
        throw new IOException("The mouse did not confirm the requested onboard slot change.");
    }

    OnboardBinding ReadOnboardBinding(OnboardRequest request, bool allowInherited, byte[]? expected = null)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(25);
            var reply = ReadReport(BasiliskOnboard.ReadBinding(ControlTransactionId, request.Slot, request.Button, request.Layer));
            if (reply != null && BasiliskOnboard.ParseBinding(reply, request.Slot, request.Button, request.Layer, allowInherited) is { } binding &&
                (expected == null || binding.Block.AsSpan().SequenceEqual(expected)))
                return binding;
        }
        throw new IOException(expected == null ? "The mouse did not return a matching onboard button assignment."
            : "The mouse did not confirm the requested onboard button assignment.");
    }
}
