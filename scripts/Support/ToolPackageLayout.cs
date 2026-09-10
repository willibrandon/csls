using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace Csls.Support;

/// <summary>
/// Validates the contents and execution model of packaged csls tools.
/// </summary>
internal static class ToolPackageLayout
{
    /// <summary>
    /// Validates a tool manifest and its complete platform package list.
    /// </summary>
    internal static void ValidateManifestPackage(
        string packagePath,
        string packageId,
        string version)
    {
        using ZipArchive archive = OpenRequiredPackage(packagePath);
        RequireEntry(archive, "README.md");
        RequireEntry(archive, "LICENSE");
        ZipArchiveEntry settingsEntry = RequireEntry(
            archive,
            "tools/any/any/DotnetToolSettings.xml");
        XDocument settings = LoadXml(settingsEntry);
        string[] packageIds =
        [
            .. settings
                .Descendants("RuntimeIdentifierPackage")
                .Select(static element =>
                    (string?)element.Attribute("Id") ?? string.Empty)
        ];
        string[] expectedPackageIds =
        [
            $"{packageId}.win-x64",
            $"{packageId}.win-arm64",
            $"{packageId}.win-x86",
            $"{packageId}.linux-x64",
            $"{packageId}.linux-arm64",
            $"{packageId}.linux-musl-x64",
            $"{packageId}.linux-musl-arm64",
            $"{packageId}.osx-x64",
            $"{packageId}.osx-arm64",
            $"{packageId}.any"
        ];
        if (!packageIds.SequenceEqual(expectedPackageIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{packagePath} does not declare the exact supported RID package set.");
        }

        if (string.Equals(packageId, "csls-mcp", StringComparison.Ordinal))
        {
            ValidatePackageTypes(archive, "DotnetTool", "McpServer");
            ValidateMcpServerManifest(
                RequireEntry(archive, ".mcp/server.json"),
                packageId,
                version);
        }
        else
        {
            ValidatePackageTypes(archive, "DotnetTool");
        }

        ValidateNoForbiddenEntries(archive);
    }

    /// <summary>
    /// Validates a platform package and its bundled worker payloads.
    /// </summary>
    internal static void ValidateImplementationPackage(
        string packagePath,
        string commandName,
        string runtimeIdentifier,
        IReadOnlyList<string> workerPaths,
        bool native)
    {
        using ZipArchive archive = OpenRequiredPackage(packagePath);
        string root = native
            ? $"tools/any/{runtimeIdentifier}"
            : "tools/net10.0/any";
        string executableExtension = native && runtimeIdentifier.StartsWith(
            "win-",
            StringComparison.Ordinal)
            ? ".exe"
            : string.Empty;
        string launcherName = native
            ? commandName + executableExtension
            : commandName + ".dll";
        RequireEntry(archive, $"{root}/{launcherName}");
        foreach (string workerName in workerPaths.Select(workerPath => native
            ? workerPath + executableExtension
            : workerPath + ".dll"))
        {
            RequireEntry(archive, $"{root}/{workerName}");
        }

        if (native && !string.Equals(runtimeIdentifier, "win-x86", StringComparison.Ordinal))
        {
            ValidateNativeAotPayload(archive, root, commandName);
            ValidateNativeAotPayload(archive, $"{root}/workers/debugger", "csls-debugger-worker");
        }

        if (!(native && runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)) &&
            !(string.Equals(runtimeIdentifier, "any", StringComparison.Ordinal) &&
                OperatingSystem.IsWindows()))
        {
            string interposerExtension =
                runtimeIdentifier.StartsWith("osx-", StringComparison.Ordinal) ||
                string.Equals(runtimeIdentifier, "any", StringComparison.Ordinal) &&
                OperatingSystem.IsMacOS()
                    ? ".dylib"
                    : ".so";
            RequireEntry(
                archive,
                $"{root}/workers/debugger/Csls.Debugger.UnixWait{interposerExtension}");
        }

        XDocument settings = LoadXml(RequireEntry(
            archive,
            $"{root}/DotnetToolSettings.xml"));
        string expectedRunner = native ? "executable" : "dotnet";
        string? runner = (string?)settings.Descendants("Command").Single().Attribute("Runner");
        if (!string.Equals(runner, expectedRunner, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{packagePath} uses runner '{runner}' instead of '{expectedRunner}'.");
        }

        ValidatePackageTypes(archive, "DotnetToolRidPackage");
        ValidateNoForbiddenEntries(archive);
    }

    private static void ValidateNativeAotPayload(ZipArchive archive, string directory, string assemblyName)
    {
        string[] managedArtifacts =
        [
            assemblyName + ".dll",
            assemblyName + ".deps.json",
            assemblyName + ".runtimeconfig.json",
            "coreclr.dll",
            "libcoreclr.so",
            "libcoreclr.dylib",
            "hostpolicy.dll",
            "libhostpolicy.so",
            "libhostpolicy.dylib"
        ];
        string? entryName = managedArtifacts
            .Select(artifact => $"{directory}/{artifact}")
            .FirstOrDefault(name => archive.GetEntry(name) is not null);
        if (entryName is not null)
        {
            throw new InvalidDataException(
                $"The Native AOT payload contains a managed hosting artifact: {entryName}");
        }
    }

    private static ZipArchive OpenRequiredPackage(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("The expected tool package was not produced.", packagePath);
        }

        return ZipFile.OpenRead(packagePath);
    }

    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string entryName) =>
        archive.GetEntry(entryName) ?? throw new InvalidDataException(
            $"{archive.Comment} is missing package entry '{entryName}'.");

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static void ValidatePackageTypes(ZipArchive archive, params string[] expectedTypes)
    {
        ZipArchiveEntry nuspec = archive.Entries.Single(static entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        XDocument document = LoadXml(nuspec);
        string[] actualTypes =
        [
            .. document
            .Descendants()
            .Where(static element => element.Name.LocalName == "packageType")
            .Select(static element => element.Attribute("name")?.Value ?? string.Empty)
        ];
        if (!actualTypes.SequenceEqual(expectedTypes, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{nuspec.FullName} uses package types " +
                $"'{string.Join(", ", actualTypes)}' instead of " +
                $"'{string.Join(", ", expectedTypes)}'.");
        }
    }

    private static void ValidateMcpServerManifest(
        ZipArchiveEntry entry,
        string packageId,
        string version)
    {
        using Stream stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        RequireJsonString(
            root,
            "$schema",
            "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json");
        RequireJsonString(root, "name", "io.github.willibrandon/csls-mcp");
        RequireJsonString(root, "version", version);
        JsonElement package = root.GetProperty("packages").EnumerateArray().Single();
        RequireJsonString(package, "registryType", "nuget");
        RequireJsonString(package, "identifier", packageId);
        RequireJsonString(package, "version", version);
        RequireJsonString(package.GetProperty("transport"), "type", "stdio");
        if (package.GetProperty("packageArguments").GetArrayLength() != 0 ||
            package.GetProperty("environmentVariables").GetArrayLength() != 0)
        {
            throw new InvalidDataException(
                "The csls MCP package must not require arguments or environment variables.");
        }
    }

    private static void RequireJsonString(
        JsonElement element,
        string propertyName,
        string expectedValue)
    {
        string? actualValue = element.GetProperty(propertyName).GetString();
        if (!string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"MCP manifest property '{propertyName}' is '{actualValue}' instead of " +
                $"'{expectedValue}'.");
        }
    }

    private static void ValidateNoForbiddenEntries(ZipArchive archive)
    {
        string[] forbiddenEntries =
        [
            .. archive.Entries
                .Select(static entry => entry.FullName.Replace('\\', '/'))
                .Where(static entry =>
                    entry.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                    entry.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) ||
                    entry.EndsWith(".dwarf", StringComparison.OrdinalIgnoreCase) ||
                    entry.Contains(".dSYM/", StringComparison.OrdinalIgnoreCase) ||
                    entry.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !entry.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) &&
                    !entry.EndsWith("/DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase))
        ];
        if (forbiddenEntries.Length != 0)
        {
            throw new InvalidDataException(
                $"A tool package contains forbidden artifacts: {string.Join(", ", forbiddenEntries)}");
        }
    }
}
