#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:package System.CommandLine
#:include Support/WindowsStackOverflowCapture.cs

using Csls.Support;
using System.CommandLine;

var command = new RootCommand("Captures native exceptions from a test-owned Windows stack-overflow fixture.");
var fixture = new Option<string>("--fixture") { Required = true, Description = "The compiled process-host assembly." };
var output = new Option<string>("--output") { Required = true, Description = "The parent directory for capture artifacts." };
command.Add(fixture);
command.Add(output);
command.SetAction((result, cancellationToken) => WindowsStackOverflowCapture.RunAsync(
    result.GetRequiredValue(fixture), result.GetRequiredValue(output), cancellationToken));
return await command.Parse(args).InvokeAsync().ConfigureAwait(false);
