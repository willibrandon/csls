#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:package System.CommandLine
#:include Support/DebuggerDumpArchive.cs

using Csls.Support;
using System.CommandLine;

var results = new Option<DirectoryInfo>("--results")
{
    Description = "Completed debugger test results containing retained dumps and TRX attachments.",
    DefaultValueFactory = _ => new DirectoryInfo(Path.Join("artifacts", "test-results"))
};
var output = new Option<FileInfo>("--output")
{
    Description = "New gzip-compressed tar archive retaining every dump path.",
    DefaultValueFactory = _ => new FileInfo(Path.Join("artifacts", "debugger-dumps.tar.gz"))
};
var command = new RootCommand("Archives debugger dumps with one payload per distinct captured file.") { results, output };
command.SetAction(async (parseResult, cancellationToken) =>
{
    (int paths, int payloads, long bytes) = await DebuggerDumpArchive.CreateAsync(
        parseResult.GetRequiredValue(results).FullName, parseResult.GetRequiredValue(output).FullName,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Retained {paths} dump paths using {payloads} payloads ({bytes} bytes).");
});
return await command.Parse(args).InvokeAsync().ConfigureAwait(false);
