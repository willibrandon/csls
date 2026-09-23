#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

const string DefaultIdlUri =
    "https://raw.githubusercontent.com/dotnet/runtime/main/src/coreclr/inc/cordebug.idl";
const string Usage =
    "Usage: dotnet run --file scripts/Update-CorDebugIdl.cs [--source <URI-or-path>]";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Updates the checked-in public ICorDebug IDL and regenerates its ABI projections.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 || args.Length == 2 && args[0] != "--source")
{
    await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
    return 2;
}

string source = args.Length == 2 ? args[1] : DefaultIdlUri;
string idl;
if (Uri.TryCreate(source, UriKind.Absolute, out Uri? sourceUri) &&
    sourceUri.Scheme == Uri.UriSchemeHttps)
{
    using var client = new HttpClient();
    idl = await client.GetStringAsync(sourceUri).ConfigureAwait(false);
}
else if (sourceUri is not null && sourceUri.IsAbsoluteUri && !sourceUri.IsFile)
{
    await Console.Error.WriteLineAsync(
        "The ICorDebug IDL source URI must use HTTPS.").ConfigureAwait(false);
    return 2;
}
else
{
    string sourcePath = sourceUri?.IsFile == true ? sourceUri.LocalPath : source;
    idl = await File.ReadAllTextAsync(Path.GetFullPath(sourcePath)).ConfigureAwait(false);
}

if (!idl.Contains("interface ICorDebug : IUnknown", StringComparison.Ordinal) ||
    !idl.Contains("interface ICorDebugProcess : ICorDebugController", StringComparison.Ordinal) ||
    !idl.Contains("interface ICorDebugManagedCallback : IUnknown", StringComparison.Ordinal) ||
    !idl.Contains(
        "uuid(3d6f5f61-7538-11d3-8d5b-00104b35e7ef)",
        StringComparison.OrdinalIgnoreCase))
{
    await Console.Error.WriteLineAsync(
        "The source is not a complete public ICorDebug runtime IDL.").ConfigureAwait(false);
    return 1;
}

string normalized = idl.Replace("\r\n", "\n", StringComparison.Ordinal);
if (!normalized.EndsWith('\n'))
{
    normalized += '\n';
}

string repositoryRoot = FindRepositoryRoot();
string destination = Path.Join(
    repositoryRoot,
    "src",
    "Csls.Debugger",
    "Interop",
    "cordebug.idl");
await File.WriteAllTextAsync(destination, normalized, new UTF8Encoding(false)).ConfigureAwait(false);
string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
await Console.Out.WriteLineAsync($"Updated {destination} (SHA-256 {digest}).").ConfigureAwait(false);

var generatorStartInfo = new ProcessStartInfo
{
    FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
    WorkingDirectory = repositoryRoot,
    RedirectStandardError = true,
    RedirectStandardOutput = true,
    UseShellExecute = false
};
generatorStartInfo.ArgumentList.Add("run");
generatorStartInfo.ArgumentList.Add("--file");
generatorStartInfo.ArgumentList.Add(Path.Join("scripts", "Generate-CorDebugInterop.cs"));
using Process generator = Process.Start(generatorStartInfo)
    ?? throw new InvalidOperationException("The ICorDebug projection generator did not start.");
Task<string> generatorOutputTask = generator.StandardOutput.ReadToEndAsync();
Task<string> generatorErrorTask = generator.StandardError.ReadToEndAsync();
await generator.WaitForExitAsync().ConfigureAwait(false);
string generatorOutput = await generatorOutputTask.ConfigureAwait(false);
string generatorError = await generatorErrorTask.ConfigureAwait(false);
if (generator.ExitCode != 0)
{
    await Console.Error.WriteLineAsync(generatorError).ConfigureAwait(false);
    return 1;
}

await Console.Out.WriteAsync(generatorOutput).ConfigureAwait(false);
return 0;

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}
