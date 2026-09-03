using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Discovers and applies CoreCLR-approved managed instruction-pointer destinations.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumGotoTargetCount = 256;

    /// <summary>
    /// Gets exact sequence points that CoreCLR approves for the active frame.
    /// </summary>
    /// <param name="request">The selected frame and source position.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <returns>The ordered safe destinations.</returns>
    internal IReadOnlyList<DebugGotoTargetInfo> GetGotoTargets(
        DebugGotoTargetsRequest request,
        DebugStopGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Line);
        if (request.Column is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A goto target column must be positive when provided.");
        }

        ManagedFrameHandle frame = GetFrame(request.FrameId, generation);
        ValidateActiveFrame(frame);
        if (frame.ModulePath is null || frame.MethodToken == 0)
        {
            return [];
        }

        nint ilFrame = 0;
        try
        {
            if (!ComAbi.TryQueryInterface(
                frame.Pointer,
                ICorDebugILFrameAbi.InterfaceId,
                out ilFrame))
            {
                return [];
            }

            _gotoTargets.Clear();
            IEnumerable<ManagedSequencePoint> points = PortablePdbMethodMap
                .Read(frame.ModulePath, frame.MethodToken)
                .Where(point => request.Line >= point.StartLine &&
                    request.Line <= point.EndLine &&
                    _sourceBreakpoints.PathsReferToSameSource(
                        point.SourcePath,
                        request.SourcePath));
            if (request.Column is int requestedColumn)
            {
                points = points.OrderBy(point => ColumnDistance(point, requestedColumn));
            }

            var result = new List<DebugGotoTargetInfo>();
            foreach (ManagedSequencePoint point in points)
            {
                uint ilOffset = checked((uint)point.IlOffset);
                int validation = new ICorDebugILFrameAbi(ilFrame).CanSetIP(ilOffset);
                if (ilOffset == frame.IlOffset || validation != 0)
                {
                    continue;
                }

                int id = checked(++_nextGotoTargetId);
                string instructionReference = $"csls-il-{Guid.NewGuid():N}";
                var info = new DebugGotoTargetInfo(
                    id,
                    CreateGotoLabel(frame.Name, point),
                    point.StartLine,
                    point.StartColumn,
                    point.EndLine,
                    point.EndColumn,
                    instructionReference);
                _gotoTargets.Add(id, new ManagedGotoTargetHandle
                {
                    Generation = generation,
                    FrameId = frame.Id,
                    ThreadId = frame.ThreadId,
                    IlOffset = ilOffset
                });
                _instructionFrames.Add(
                    instructionReference,
                    new ManagedInstructionReferenceHandle
                    {
                        Frame = frame,
                        IlOffset = ilOffset
                    });
                result.Add(info);
                if (result.Count == MaximumGotoTargetCount)
                {
                    break;
                }
            }

            return result;
        }
        finally
        {
            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }
        }
    }

    /// <summary>
    /// Moves the active managed instruction pointer to a previously approved destination.
    /// </summary>
    /// <param name="request">The selected managed thread and destination.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    internal void SetInstructionPointer(
        DebugGotoRequest request,
        DebugStopGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_gotoTargets.TryGetValue(request.TargetId, out ManagedGotoTargetHandle? target) ||
            target.Generation != generation)
        {
            throw new InvalidOperationException(
                $"Goto target {request.TargetId} is stale or unknown.");
        }

        if (target.ThreadId != request.ThreadId)
        {
            throw new InvalidOperationException(
                $"Goto target {request.TargetId} belongs to managed thread {target.ThreadId}.");
        }

        ManagedFrameHandle frame = GetFrame(target.FrameId, generation);
        ValidateActiveFrame(frame);
        nint ilFrame = ComAbi.QueryInterface(frame.Pointer, ICorDebugILFrameAbi.InterfaceId);
        try
        {
            var api = new ICorDebugILFrameAbi(ilFrame);
            int validation = api.CanSetIP(target.IlOffset);
            if (validation != 0)
            {
                throw new InvalidOperationException(
                    $"CoreCLR no longer approves goto target {request.TargetId} " +
                    $"(HRESULT 0x{validation:X8}).");
            }

            CorDebugHResult.ThrowIfFailed(api.SetIP(target.IlOffset), "ICorDebugILFrame.SetIP");
            ClearFrameHandles();
        }
        finally
        {
            _ = ComAbi.Release(ilFrame);
        }
    }

    private static int ColumnDistance(ManagedSequencePoint point, int column) =>
        column < point.StartColumn
            ? point.StartColumn - column
            : column > point.EndColumn
                ? column - point.EndColumn
                : 0;

    private static string CreateGotoLabel(
        string methodName,
        ManagedSequencePoint point) =>
        $"{methodName} — line {point.StartLine}, column {point.StartColumn}";
}
