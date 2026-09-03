using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;

namespace Csls.DebugAdapter;

/// <summary>
/// Dispatches supported DAP requests to capability-specific handlers.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask HandleRequestAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "initialize":
                await InitializeAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "launch":
                await PrepareLaunchAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "attach":
                await PrepareAttachAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "configurationDone":
                await CompleteTargetStartAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "setBreakpoints":
                await SetBreakpointsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "setFunctionBreakpoints":
                await SetFunctionBreakpointsAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "setExceptionBreakpoints":
                await SetExceptionBreakpointsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "threads":
                await WriteThreadsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "modules":
                await WriteModulesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "loadedSources":
                await WriteLoadedSourcesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "source":
                await WriteSourceContentAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "breakpointLocations":
                await WriteBreakpointLocationsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "pause":
                await PauseAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "continue":
                await ContinueAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "next":
                await StepAsync(request, DebugStepKind.Over, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stepIn":
                await StepAsync(request, DebugStepKind.Into, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stepOut":
                await StepAsync(request, DebugStepKind.Out, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stepInTargets":
                await WriteStepInTargetsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "gotoTargets":
                await WriteGotoTargetsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "goto":
                await GotoAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "stackTrace":
                await WriteStackTraceAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "scopes":
                await WriteScopesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "variables":
                await WriteVariablesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "readMemory":
                await ReadMemoryAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "disassemble":
                await DisassembleAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "exceptionInfo":
                await WriteExceptionInfoAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "disconnect":
                await DisconnectAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "cancel":
                await _writer.WriteResponseAsync(
                    request,
                    success: true,
                    message: null,
                    writeBody: null,
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                await _writer.WriteResponseAsync(
                    request,
                    success: false,
                    $"The request '{request.Command}' is not supported by this debugger capability set.",
                    writeBody: null,
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

}
