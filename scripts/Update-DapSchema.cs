#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

const string DefaultSchemaUri =
    "https://raw.githubusercontent.com/microsoft/debug-adapter-protocol/main/debugAdapterProtocol.json";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Updates the checked-in Debug Adapter Protocol schema from an official URI or local file.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Update-DapSchema.cs [--source <URI-or-path>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 || args.Length == 2 && args[0] != "--source")
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Update-DapSchema.cs [--source <URI-or-path>]")
        .ConfigureAwait(false);
    return 2;
}

string source = args.Length == 2 ? args[1] : DefaultSchemaUri;
string schema;
if (Uri.TryCreate(source, UriKind.Absolute, out Uri? sourceUri) &&
    sourceUri.Scheme == Uri.UriSchemeHttps)
{
    using var client = new HttpClient();
    schema = await client.GetStringAsync(sourceUri).ConfigureAwait(false);
}
else if (sourceUri is not null && sourceUri.IsAbsoluteUri && !sourceUri.IsFile)
{
    await Console.Error.WriteLineAsync(
        "The DAP schema source URI must use HTTPS.").ConfigureAwait(false);
    return 2;
}
else
{
    string sourcePath = sourceUri?.IsFile == true ? sourceUri.LocalPath : source;
    schema = await File.ReadAllTextAsync(Path.GetFullPath(sourcePath)).ConfigureAwait(false);
}

using (var document = JsonDocument.Parse(schema))
{
    JsonElement root = document.RootElement;
    if (!root.TryGetProperty("title", out JsonElement title) ||
        title.GetString() != "Debug Adapter Protocol" ||
        !root.TryGetProperty("definitions", out JsonElement definitions) ||
        !definitions.TryGetProperty("ProtocolMessage", out _) ||
        !definitions.TryGetProperty("InitializeRequest", out _) ||
        !definitions.TryGetProperty("DisconnectRequest", out _))
    {
        await Console.Error.WriteLineAsync(
            "The source is not a complete Debug Adapter Protocol schema.").ConfigureAwait(false);
        return 1;
    }
}

string normalized = schema.Replace("\r\n", "\n", StringComparison.Ordinal);
if (!normalized.EndsWith('\n'))
{
    normalized += '\n';
}

string repositoryRoot = FindRepositoryRoot();
string destination = Path.Join(
    repositoryRoot,
    "src",
    "Csls.DebugAdapter",
    "Protocol",
    "debugAdapterProtocol.json");
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
generatorStartInfo.ArgumentList.Add(Path.Join("scripts", "Generate-DapProtocol.cs"));
using Process generator = Process.Start(generatorStartInfo)
    ?? throw new InvalidOperationException("The DAP contract generator did not start.");
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
