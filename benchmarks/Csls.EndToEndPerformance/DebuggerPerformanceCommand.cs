using System.CommandLine;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Builds independent debugger measurement and target commands for the performance executable.
/// </summary>
internal static class DebuggerPerformanceCommand
{
    /// <summary>
    /// Creates the real-process debugger measurement command.
    /// </summary>
    /// <returns>The validated measurement command.</returns>
    internal static Command Create()
    {
        var server = new Option<string>("--server") { Required = true, Description = "csls executable or launcher assembly." };
        var source = new Option<string>("--fixture-source") { Required = true, Description = "Original DebuggerPerformanceTarget.cs document." };
        var target = new Option<string>("--target")
        {
            Description = "Compiled performance target assembly.",
            DefaultValueFactory = static _ => typeof(DebuggerPerformanceTarget).Assembly.Location
        };
        var output = new Option<string>("--output")
        {
            Description = "New JSON report path.",
            DefaultValueFactory = static _ => Path.Join(Environment.CurrentDirectory,
                "artifacts", "end-to-end-performance", $"debugger-{Guid.NewGuid():N}.json")
        };
        Option<int> iterations = PositiveInteger("--iterations", "Number of fresh debugger sessions.", 3);
        Option<int> samples = PositiveInteger("--samples", "Repeated samples after each first stopped-state request.", 5);
        Option<int> timeout = PositiveInteger("--timeout-seconds", "Deadline for each session.", 60);
        var budget = new Option<double>("--operation-budget-ms")
        {
            Description = "Maximum median operation duration.",
            DefaultValueFactory = static _ => 10_000
        };
        budget.Validators.Add(static result =>
        {
            double value = result.GetValueOrDefault<double>();
            if (!double.IsFinite(value) || value <= 0)
            {
                result.AddError("--operation-budget-ms must be finite and greater than zero.");
            }
        });
        var command = new Command("debugger", "Measure real DAP sessions and debugger process resources.")
        {
            server, source, target, output, iterations, samples, timeout, budget
        };
        command.SetAction(async (parsed, cancellationToken) =>
        {
            try
            {
                return await DebuggerPerformanceRunner.RunAsync(new DebuggerPerformanceOptions(
                    Path.GetFullPath(parsed.GetRequiredValue(server)), Path.GetFullPath(parsed.GetRequiredValue(target)),
                    Path.GetFullPath(parsed.GetRequiredValue(source)), Path.GetFullPath(parsed.GetRequiredValue(output)),
                    parsed.GetValue(iterations), parsed.GetValue(samples), TimeSpan.FromSeconds(parsed.GetValue(timeout)),
                    parsed.GetValue(budget)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or
                ArgumentException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                await Console.Error.WriteLineAsync($"Debugger measurement failed: {exception.Message}").ConfigureAwait(false);
                return 1;
            }
        });
        return command;
    }

    /// <summary>
    /// Creates the checked-in target entry point used by debugger measurements.
    /// </summary>
    /// <returns>The target command.</returns>
    internal static Command CreateTarget()
    {
        var command = new Command("debugger-target", "Run the debugger measurement target.") { Hidden = true };
        command.SetAction(static _ => DebuggerPerformanceTarget.Run(40));
        return command;
    }

    private static Option<int> PositiveInteger(string name, string description, int defaultValue)
    {
        var option = new Option<int>(name) { Description = description, DefaultValueFactory = _ => defaultValue };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.AddError($"{name} must be greater than zero.");
            }
        });
        return option;
    }
}
