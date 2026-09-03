using Csls.Debugger.Contracts;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Evaluates managed breakpoint conditions and interpolated logpoint messages.
/// </summary>
public sealed partial class DebuggerSession
{
    private const int MaximumCachedBreakpointExpressions = 512;
    private const int MaximumCachedLogMessageTemplates = 256;
    private const int MaximumBreakpointExpressionLength = 4096;
    private const int MaximumDiagnosticExpressionLength = 1024;
    private const int MaximumDiagnosticMessageLength = 2048;
    private readonly Dictionary<(DebugExpressionLanguage Language, string Expression),
        DebugExpressionPlan> _breakpointExpressionPlans = [];
    private readonly Dictionary<string, DebugLogMessageTemplate> _logMessageTemplates =
        new(StringComparer.Ordinal);

    private async ValueTask<bool> HandleRunningBreakpointAsync(
        int threadId,
        ManagedBreakpointHit hit,
        CancellationToken cancellationToken)
    {
        if (_debuggee is not CorDebugDebuggee managedDebuggee)
        {
            return false;
        }

        string? condition = hit.Definition.Condition;
        string? logMessage = hit.Definition.LogMessage;
        if (condition is null && logMessage is null)
        {
            if (!hit.Definition.RegisterHit())
            {
                return true;
            }

            await StopAtBreakpointAsync(
                threadId,
                hit.Kind,
                generation: null,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        DebugStopGeneration generation = NextStopGeneration();
        int frameId = 0;
        DebugExpressionLanguage language = default;
        if (condition is not null)
        {
            try
            {
                (frameId, language) = PrepareBreakpointFrame(
                    managedDebuggee,
                    threadId,
                    generation);
                DebugExpressionPlan plan = await GetBreakpointExpressionPlanAsync(
                    language,
                    condition,
                    cancellationToken).ConfigureAwait(false);
                if (!managedDebuggee.EvaluateCondition(frameId, plan, generation))
                {
                    managedDebuggee.DiscardBreakpointInspection();
                    return true;
                }
            }
            catch (Exception exception) when (IsBreakpointExpressionFailure(exception))
            {
                await ReportBreakpointExpressionFailureAsync(
                    "condition",
                    condition,
                    exception,
                    cancellationToken).ConfigureAwait(false);
                await StopAtBreakpointAsync(
                    threadId,
                    hit.Kind,
                    generation,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        if (!hit.Definition.RegisterHit())
        {
            if (frameId != 0)
            {
                managedDebuggee.DiscardBreakpointInspection();
            }

            return true;
        }

        if (logMessage is not null)
        {
            try
            {
                DebugLogMessageTemplate template = GetLogMessageTemplate(logMessage);
                if (template.Segments.Any(static segment => segment.IsExpression) && frameId == 0)
                {
                    (frameId, language) = PrepareBreakpointFrame(
                        managedDebuggee,
                        threadId,
                        generation);
                }

                string output = await EvaluateLogMessageAsync(
                    managedDebuggee,
                    frameId,
                    language,
                    generation,
                    template,
                    cancellationToken).ConfigureAwait(false);
                await _observer.OnOutputAsync(
                    DebugOutputCategory.Console,
                    output,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsBreakpointExpressionFailure(exception))
            {
                await ReportBreakpointExpressionFailureAsync(
                    "logpoint message",
                    logMessage,
                    exception,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (frameId != 0)
                {
                    managedDebuggee.DiscardBreakpointInspection();
                }
            }

            return true;
        }

        await StopAtBreakpointAsync(
            threadId,
            hit.Kind,
            generation,
            cancellationToken).ConfigureAwait(false);
        return false;
    }

    private DebugStopGeneration NextStopGeneration() => _stopGeneration.Value == 0
        ? DebugStopGeneration.First
        : _stopGeneration.Next();

    private static (int FrameId, DebugExpressionLanguage Language) PrepareBreakpointFrame(
        CorDebugDebuggee managedDebuggee,
        int threadId,
        DebugStopGeneration generation)
    {
        DebugStackTrace stack = managedDebuggee.GetStackTrace(
            threadId,
            generation,
            startFrame: 0,
            levels: 1);
        if (stack.StackFrames.Count == 0)
        {
            throw new InvalidOperationException(
                "The breakpoint thread has no managed frame available for evaluation.");
        }

        DebugStackFrameInfo frame = stack.StackFrames[0];
        return (
            frame.Id,
            managedDebuggee.GetExpressionLanguage(frame.Id, generation));
    }

    private async Task<DebugExpressionPlan> GetBreakpointExpressionPlanAsync(
        DebugExpressionLanguage language,
        string expression,
        CancellationToken cancellationToken)
    {
        if (expression.Length > MaximumBreakpointExpressionLength)
        {
            throw new ArgumentException(
                $"A breakpoint expression cannot exceed " +
                $"{MaximumBreakpointExpressionLength} characters.",
                nameof(expression));
        }

        (DebugExpressionLanguage Language, string Expression) key = (language, expression);
        if (_breakpointExpressionPlans.TryGetValue(key, out DebugExpressionPlan? plan))
        {
            return plan;
        }

        plan = await CompileExpressionAsync(language, expression, cancellationToken)
            .ConfigureAwait(false);
        if (_breakpointExpressionPlans.Count >= MaximumCachedBreakpointExpressions)
        {
            _breakpointExpressionPlans.Clear();
        }

        _breakpointExpressionPlans.Add(key, plan);
        return plan;
    }

    private DebugLogMessageTemplate GetLogMessageTemplate(string message)
    {
        if (_logMessageTemplates.TryGetValue(message, out DebugLogMessageTemplate? template))
        {
            return template;
        }

        template = DebugLogMessageTemplate.Parse(message);
        if (_logMessageTemplates.Count >= MaximumCachedLogMessageTemplates)
        {
            _logMessageTemplates.Clear();
        }

        _logMessageTemplates.Add(message, template);
        return template;
    }

    private async Task<string> EvaluateLogMessageAsync(
        CorDebugDebuggee managedDebuggee,
        int frameId,
        DebugExpressionLanguage language,
        DebugStopGeneration generation,
        DebugLogMessageTemplate template,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        foreach (DebugLogMessageSegment segment in template.Segments)
        {
            if (!segment.IsExpression)
            {
                _ = result.Append(segment.Text);
                continue;
            }

            DebugExpressionPlan plan = await GetBreakpointExpressionPlanAsync(
                language,
                segment.Text,
                cancellationToken).ConfigureAwait(false);
            _ = result.Append(managedDebuggee.Evaluate(frameId, plan, generation).Result);
        }

        if (result.Length == 0 || result[^1] != '\n')
        {
            _ = result.Append('\n');
        }

        return result.ToString();
    }

    private async ValueTask ReportBreakpointExpressionFailureAsync(
        string operation,
        string expression,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await _observer.OnOutputAsync(
            DebugOutputCategory.Console,
            $"Breakpoint {operation} '{Truncate(expression, MaximumDiagnosticExpressionLength)}' " +
                $"could not be evaluated: " +
                $"{Truncate(exception.Message, MaximumDiagnosticMessageLength)}" +
                '\n',
            cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : string.Concat(value.AsSpan(0, maximumLength), "…");

    private static bool IsBreakpointExpressionFailure(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or BadImageFormatException or ArithmeticException;
}
