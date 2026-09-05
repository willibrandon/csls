#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:package SharpCompress
#:package System.CommandLine
#:include ScriptSupport.cs
#:include Support/AptPackageCache.cs
#:include Support/ProcessOutputCapture.cs
#:include Support/GraphicalPrerequisiteCommand.cs
#:include Support/GraphicalPrerequisiteOptions.cs
#:include Support/GraphicalPrerequisiteInstaller.cs

using Csls.Support;
using System.CommandLine;

ParseResult result = GraphicalPrerequisiteCommand.Create().Parse(args);
int exitCode = await result.InvokeAsync().ConfigureAwait(false);
return result.Errors.Count == 0 ? exitCode : 2;
