#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package System.CommandLine

using System.CommandLine;

var command = new RootCommand("Requires a workflow dependency to complete successfully.");
command.SetAction(_ =>
{
    string? result = Environment.GetEnvironmentVariable("CSLS_WORKFLOW_DEPENDENCY_RESULT");
    if (string.Equals(result, "success", StringComparison.Ordinal))
    {
        Console.WriteLine("Workflow dependency completed successfully.");
        return 0;
    }

    Console.Error.WriteLine($"Workflow dependency result was '{result ?? "missing"}'; expected 'success'.");
    return 1;
});
return command.Parse(args).Invoke();
