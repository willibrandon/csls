#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:package System.CommandLine
#:include Support/MacDebuggerTestAuthorization.cs

using Csls.Support;
using System.CommandLine;

var command = new RootCommand("Scopes unattended debugger authorization to a hosted macOS CI job.");
var enable = new Command("enable", "Save the original policy and authorize members of the developer group.");
enable.SetAction((_, cancellationToken) => MacDebuggerTestAuthorization.EnableAsync(cancellationToken));
var restore = new Command("restore", "Restore the saved debugger authorization policy.");
restore.SetAction((_, cancellationToken) => MacDebuggerTestAuthorization.RestoreAsync(cancellationToken));
command.Add(enable);
command.Add(restore);
return await command.Parse(args).InvokeAsync().ConfigureAwait(false);
