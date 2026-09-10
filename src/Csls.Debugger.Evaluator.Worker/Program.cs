using Csls.Debugger.Evaluation;
using Csls.Debugger.Evaluator.Worker;
using System.Runtime.CompilerServices;

Stream input = Console.OpenStandardInput();
await using ConfiguredAsyncDisposable inputCleanup = input.ConfigureAwait(false);
Stream output = Console.OpenStandardOutput();
await using ConfiguredAsyncDisposable outputCleanup = output.ConfigureAwait(false);
var target = new DebuggerEvaluatorTarget();
await DebuggerEvaluatorStreamServer.RunAsync(
    input,
    output,
    target,
    CancellationToken.None).ConfigureAwait(false);
return 0;
