using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Provides generation-safe debugger expression completions from live runtime state.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Gets source-language completions for one selected managed frame and line.
    /// </summary>
    /// <param name="frameId">The generation-bound managed frame.</param>
    /// <param name="text">The selected source-language input line.</param>
    /// <param name="cursor">The zero-based UTF-16 cursor within <paramref name="text"/>.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The bounded runtime-backed completion candidates.</returns>
    public async Task<IReadOnlyList<DebugCompletionInfo>> GetCompletionsAsync(
        int frameId,
        string text,
        int cursor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameId);
        ArgumentNullException.ThrowIfNull(text);
        if ((uint)cursor > (uint)text.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cursor),
                "The completion cursor is outside the selected line.");
        }

        (string? receiver, string prefix, int replacementStart) =
            ParseCompletionInput(text, cursor);
        DebugExpressionLanguage language = default;
        DebugStopGeneration generation = default;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee debuggee = GetStoppedManagedDebuggee();
                generation = _stopGeneration;
                language = debuggee.GetExpressionLanguage(frameId, generation);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        DebugExpressionPlan? receiverPlan = receiver is null
            ? null
            : await CompileExpressionAsync(
                language,
                receiver,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DebugCompletionInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee debuggee = GetStoppedManagedDebuggee();
                if (_stopGeneration != generation)
                {
                    throw new InvalidOperationException(
                        $"Completion generation {generation.Value} is stale; the current " +
                        $"stopped generation is {_stopGeneration.Value}.");
                }

                result = receiverPlan is null
                    ? debuggee.GetRootCompletions(
                        frameId,
                        prefix,
                        replacementStart,
                        cursor - replacementStart,
                        generation)
                    : debuggee.GetMemberCompletions(
                        frameId,
                        receiverPlan,
                        prefix,
                        replacementStart,
                        cursor - replacementStart,
                        generation);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private static (string? Receiver, string Prefix, int ReplacementStart)
        ParseCompletionInput(string text, int cursor)
    {
        int replacementStart = cursor;
        while (replacementStart > 0 && IsCompletionIdentifierPart(text[replacementStart - 1]))
        {
            replacementStart--;
        }

        string prefix = text[replacementStart..cursor];
        int receiverEnd = replacementStart - 1;
        if (receiverEnd < 0 || text[receiverEnd] != '.')
        {
            return (null, prefix, replacementStart);
        }

        string receiver = text[..receiverEnd].Trim();
        if (receiver.Length == 0)
        {
            return (null, prefix, replacementStart);
        }

        return (receiver, prefix, replacementStart);
    }

    private static bool IsCompletionIdentifierPart(char character) =>
        character == '_' || char.IsLetterOrDigit(character);
}
