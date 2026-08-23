#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

const string Usage = "Usage: dotnet run --file scripts/Capture-Docs.cs";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Captures verified editor screenshots and rebuilds the csls documentation site.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
    return 2;
}

string? generatedScreenshotPath = null;
try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string screenshotDirectory = Path.Join(
        repositoryRoot,
        "docs-site",
        "src",
        "assets",
        "screenshots");
    string screenshotPath = Path.Join(screenshotDirectory, "helix-hover.svg");
    generatedScreenshotPath = Path.Join(
        screenshotDirectory,
        $"helix-hover-{Guid.NewGuid():N}.svg");
    Directory.CreateDirectory(screenshotDirectory);

    string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    await RunCheckedAsync(
        dotnetPath,
        ["run", "--file", Path.Join("scripts", "Provision-Helix.cs")],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        dotnetPath,
        [
            "test",
            "--project",
            Path.Join("tests", "Csls.Tests", "Csls.Tests.csproj"),
            "--filter",
            "FullyQualifiedName=Csls.Tests.HelixLanguageServerTests.HelixDisplaysHoverFromCsls"
        ],
        repositoryRoot,
        "CSLS_DOCS_SCREENSHOT_PATH",
        generatedScreenshotPath).ConfigureAwait(false);

    if (!File.Exists(generatedScreenshotPath) ||
        new FileInfo(generatedScreenshotPath).Length == 0)
    {
        throw new InvalidDataException("The Helix screenshot was not generated.");
    }

    string terminalSvg = await File.ReadAllTextAsync(generatedScreenshotPath)
        .ConfigureAwait(false);
    string framedSvg = FrameTerminalSvg(terminalSvg);
    await File.WriteAllTextAsync(screenshotPath, framedSvg).ConfigureAwait(false);
    File.Delete(generatedScreenshotPath);
    generatedScreenshotPath = null;

    await RunCheckedAsync(
        "npx",
        ["--yes", "npm@12.0.2", "ci", "--prefix", "docs-site"],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        "npx",
        ["--yes", "npm@12.0.2", "run", "build", "--prefix", "docs-site"],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        dotnetPath,
        ["run", "--file", Path.Join("scripts", "Verify-Docs.cs")],
        repositoryRoot).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Path.GetRelativePath(repositoryRoot, screenshotPath))
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}
finally
{
    if (generatedScreenshotPath is not null)
    {
        File.Delete(generatedScreenshotPath);
    }
}

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    string? environmentVariableName = null,
    string? environmentVariableValue = null)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    if (environmentVariableName is not null)
    {
        startInfo.Environment[environmentVariableName] = environmentVariableValue;
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    await Console.Out.WriteAsync(standardOutput).ConfigureAwait(false);
    await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}.");
    }
}

static string FrameTerminalSvg(string terminalSvg)
{
    var document = XDocument.Parse(terminalSvg);
    XElement root = document.Root
        ?? throw new InvalidDataException("The terminal SVG has no root element.");
    int terminalWidth = ReadSvgDimension(root, "width");
    int terminalHeight = ReadSvgDimension(root, "height");
    int openingTagEnd = terminalSvg.IndexOf('>', StringComparison.Ordinal);
    int definitionsEnd = terminalSvg.IndexOf("</defs>", StringComparison.Ordinal);
    int closingTagStart = terminalSvg.LastIndexOf("</svg>", StringComparison.Ordinal);
    if (openingTagEnd < 0 || definitionsEnd < 0 || closingTagStart <= definitionsEnd)
    {
        throw new InvalidDataException("The terminal SVG structure is invalid.");
    }

    definitionsEnd += "</defs>".Length;
    string definitions = terminalSvg[(openingTagEnd + 1)..definitionsEnd];
    string terminalContent = terminalSvg[definitionsEnd..closingTagStart];
    int imageWidth = terminalWidth + 48;
    int imageHeight = terminalHeight + 64;
    int windowWidth = terminalWidth + 24;
    int windowHeight = terminalHeight + 40;
    int titleCenter = imageWidth / 2;
    return FormattableString.Invariant($"""
        <svg xmlns="http://www.w3.org/2000/svg" width="{imageWidth}" height="{imageHeight}" viewBox="0 0 {imageWidth} {imageHeight}">
        {definitions}
          <rect width="{imageWidth}" height="{imageHeight}" rx="12" fill="#0d1117"/>
          <rect x="12" y="12" width="{windowWidth}" height="{windowHeight}" rx="8" fill="#161b22" stroke="#30363d"/>
          <rect x="12" y="12" width="{windowWidth}" height="40" rx="8" fill="#21262d"/>
          <path d="M12 52H{imageWidth - 12}" stroke="#30363d"/>
          <circle cx="32" cy="32" r="6" fill="#ff5f57"/>
          <circle cx="52" cy="32" r="6" fill="#febc2e"/>
          <circle cx="72" cy="32" r="6" fill="#28c840"/>
          <text x="{titleCenter}" y="37" text-anchor="middle" fill="#b1bac4" font-family="system-ui, sans-serif" font-size="14">Helix: Program.cs</text>
          <g transform="translate(24 52)">{terminalContent}
          </g>
        </svg>
        """);
}

static int ReadSvgDimension(XElement root, string attributeName)
{
    string? value = root.Attribute(attributeName)?.Value;
    if (!int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int dimension) ||
        dimension <= 0)
    {
        throw new InvalidDataException(
            $"The terminal SVG has an invalid {attributeName} attribute.");
    }

    return dimension;
}
