using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Applies explicitly authorized compiler deltas to enabled managed modules.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private const int MaximumCombinedHotReloadDeltaBytes = 3 * 1024 * 1024;
    private const int MaximumHotReloadActiveStatementCount = 65_536;
    private const int MaximumHotReloadBase64Characters =
        ((MaximumCombinedHotReloadDeltaBytes + 2) / 3) * 4;

    /// <summary>
    /// Applies one Hot Reload generation after validating authority, generations, and payload bounds.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="moduleId">The stable module identifier from module inspection.</param>
    /// <param name="expectedModuleGeneration">The generation used to compile the deltas.</param>
    /// <param name="metadataDeltaBase64">The base64 ECMA-335 metadata delta.</param>
    /// <param name="ilDeltaBase64">The base64 managed IL delta.</param>
    /// <param name="pdbDeltaBase64">The base64 minimal Portable PDB delta.</param>
    /// <param name="activeStatements">The compiler-produced active-statement remap set.</param>
    /// <param name="cancellationToken">Cancels validation before target mutation begins.</param>
    /// <returns>The committed module and replacement stopped generation.</returns>
    internal Task<McpDebugHotReloadResult> ApplyHotReloadAsync(
        string debugSession,
        long stopGeneration,
        int moduleId,
        int expectedModuleGeneration,
        string metadataDeltaBase64,
        string ilDeltaBase64,
        string pdbDeltaBase64,
        IReadOnlyList<McpDebugHotReloadActiveStatement> activeStatements,
        CancellationToken cancellationToken)
    {
        ValidatePositive(moduleId, nameof(moduleId));
        ArgumentNullException.ThrowIfNull(activeStatements);
        if (activeStatements.Count > MaximumHotReloadActiveStatementCount)
        {
            throw InvalidRequest(
                $"activeStatements cannot exceed " +
                $"{MaximumHotReloadActiveStatementCount} entries.");
        }

        if (expectedModuleGeneration < 0)
        {
            throw InvalidRequest("expectedModuleGeneration must be non-negative.");
        }

        byte[] metadataDelta = DecodeHotReloadDelta(
            metadataDeltaBase64,
            nameof(metadataDeltaBase64));
        byte[] ilDelta = DecodeHotReloadDelta(ilDeltaBase64, nameof(ilDeltaBase64));
        byte[] pdbDelta = DecodeHotReloadDelta(pdbDeltaBase64, nameof(pdbDeltaBase64));
        if ((long)metadataDelta.Length + ilDelta.Length + pdbDelta.Length >
            MaximumCombinedHotReloadDeltaBytes)
        {
            throw InvalidRequest(
                $"The decoded Hot Reload deltas must total at most " +
                $"{MaximumCombinedHotReloadDeltaBytes} bytes.");
        }

        var remaps = new List<DebugHotReloadActiveStatement>(activeStatements.Count);
        foreach (McpDebugHotReloadActiveStatement activeStatement in activeStatements)
        {
            remaps.Add(ConvertActiveStatement(activeStatement));
        }

        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selectedSession, client, token) =>
            {
                DebugHotReloadResult result = await client.ApplyHotReloadAsync(
                    new DebugHotReloadRequest(
                        stopGeneration,
                        moduleId,
                        expectedModuleGeneration,
                        metadataDelta,
                        ilDelta,
                        pdbDelta,
                        remaps),
                    token).ConfigureAwait(false);
                return new McpDebugHotReloadResult(
                    selectedSession.Id,
                    result.ModuleId,
                    result.ModuleGeneration,
                    result.StopGeneration,
                    result.UpdatedMethods);
            },
            cancellationToken);
    }

    private static DebugHotReloadActiveStatement ConvertActiveStatement(
        McpDebugHotReloadActiveStatement active)
    {
        if (active.MethodToken <= 0 || active.MethodVersion <= 0 || active.OldIlOffset < 0 ||
            active.StartLine < 0 || active.StartColumn < -1 || active.EndLine < 0 ||
            active.EndColumn < -1 || active.StartLine == int.MaxValue ||
            active.StartColumn == int.MaxValue || active.EndLine == int.MaxValue ||
            active.EndColumn == int.MaxValue ||
            (active.StartColumn == -1) != (active.EndColumn == -1) ||
            active.EndLine < active.StartLine ||
            active.EndLine == active.StartLine && active.StartColumn >= 0 &&
            active.EndColumn < active.StartColumn)
        {
            throw InvalidRequest(
                "Hot Reload active-statement tokens, versions, offsets, and spans are invalid.");
        }

        return new DebugHotReloadActiveStatement(
            checked((uint)active.MethodToken),
            active.MethodVersion,
            checked((uint)active.OldIlOffset),
            active.StartLine,
            active.StartColumn,
            active.EndLine,
            active.EndColumn);
    }

    private static byte[] DecodeHotReloadDelta(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidRequest($"{parameterName} must not be empty.");
        }

        if (value.Length > MaximumHotReloadBase64Characters)
        {
            throw InvalidRequest(
                $"{parameterName} exceeds the bounded Hot Reload payload size.");
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            return bytes.Length == 0
                ? throw InvalidRequest($"{parameterName} must not be empty.")
                : bytes;
        }
        catch (FormatException)
        {
            throw InvalidRequest($"{parameterName} must be valid base64.");
        }
    }
}
