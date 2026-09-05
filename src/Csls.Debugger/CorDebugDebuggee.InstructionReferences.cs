using Csls.Debugger.Contracts;
using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Resolves generation-owned managed-IL references used by debugger operations.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumInstructionReferenceLength = 256;

    /// <summary>
    /// Resolves an ordered instruction-breakpoint replacement set.
    /// </summary>
    /// <param name="requests">The protocol-neutral requested breakpoints.</param>
    /// <param name="generation">The active stop generation.</param>
    /// <returns>Resolved requests with per-breakpoint validation diagnostics.</returns>
    internal IReadOnlyList<ManagedInstructionBreakpointRequest>
        ResolveInstructionBreakpoints(
            IReadOnlyList<DebugInstructionBreakpointRequest> requests,
            DebugStopGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return requests.Select(request => ResolveInstructionBreakpoint(request, generation))
            .ToArray();
    }

    private ManagedInstructionBreakpointRequest ResolveInstructionBreakpoint(
        DebugInstructionBreakpointRequest request,
        DebugStopGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(request);
        string reference = request.InstructionReference ?? string.Empty;
        var result = new ManagedInstructionBreakpointRequest
        {
            InstructionReference = reference,
            Offset = request.Offset,
            Condition = request.Condition,
            HitCondition = request.HitCondition
        };
        try
        {
            ManagedInstructionReferenceHandle location = ResolveInstructionReference(
                reference,
                generation);
            ManagedFrameHandle frame = location.Frame;
            if (request.Offset < -(long)location.IlOffset ||
                request.Offset > (long)uint.MaxValue - location.IlOffset)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "The managed-IL breakpoint offset is outside the method body.");
            }

            long requestedOffset = location.IlOffset + request.Offset;
            ValidateInstructionBoundary(frame, checked((uint)requestedOffset));
            return new ManagedInstructionBreakpointRequest
            {
                InstructionReference = reference,
                Offset = request.Offset,
                Condition = request.Condition,
                HitCondition = request.HitCondition,
                ModulePath = frame.ModulePath,
                ModuleId = frame.ModuleId,
                MethodToken = frame.MethodToken,
                IlOffset = checked((uint)requestedOffset)
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException or
            OverflowException)
        {
            return new ManagedInstructionBreakpointRequest
            {
                InstructionReference = result.InstructionReference,
                Offset = result.Offset,
                Condition = result.Condition,
                HitCondition = result.HitCondition,
                ValidationMessage = exception.Message
            };
        }
    }

    private ManagedInstructionReferenceHandle ResolveInstructionReference(
        string reference,
        DebugStopGeneration generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.Length > MaximumInstructionReferenceLength)
        {
            throw new ArgumentException(
                $"An instruction reference cannot exceed {MaximumInstructionReferenceLength} characters.",
                nameof(reference));
        }

        if (_frames.TryGetInstruction(
            reference,
            out ManagedInstructionReferenceHandle? location))
        {
            ValidateGeneration(location.Frame.Id, location.Frame.Generation, generation);
            return location;
        }

        if (reference.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(
                reference.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong virtualAddress))
        {
            ulong ownerId = virtualAddress >> 32;
            if (ownerId == 0 || ownerId > int.MaxValue ||
                !_frames.TryGetByInstructionAddress((int)ownerId, out ManagedFrameHandle? frame))
            {
                throw new InvalidOperationException(
                    $"Instruction reference '{reference}' is stale or unknown.");
            }

            ValidateGeneration(frame.InstructionAddressId, frame.Generation, generation);
            return new ManagedInstructionReferenceHandle
            {
                Frame = frame,
                IlOffset = (uint)(virtualAddress & uint.MaxValue)
            };
        }

        throw new InvalidOperationException(
            $"Instruction reference '{reference}' is stale or unknown.");
    }
}
