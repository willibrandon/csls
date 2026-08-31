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
        "Captures verified terminal screenshots and rebuilds the csls documentation site.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
    return 2;
}

string? generatedHelixScreenshotPath = null;
string? generatedDashboardScreenshotPath = null;
try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string screenshotDirectory = Path.Join(
        repositoryRoot,
        "docs-site",
        "src",
        "assets",
        "screenshots");
    string helixScreenshotPath = Path.Join(screenshotDirectory, "helix-hover.svg");
    string dashboardScreenshotPath = Path.Join(screenshotDirectory, "dashboard.svg");
    generatedHelixScreenshotPath = Path.Join(
        screenshotDirectory,
        $"helix-hover-{Guid.NewGuid():N}.svg");
    generatedDashboardScreenshotPath = Path.Join(
        screenshotDirectory,
        $"dashboard-{Guid.NewGuid():N}.svg");
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
        generatedHelixScreenshotPath).ConfigureAwait(false);
    await WriteFramedScreenshotAsync(
        generatedHelixScreenshotPath,
        helixScreenshotPath,
        "Helix: Program.cs",
        "Helix").ConfigureAwait(false);
    generatedHelixScreenshotPath = null;

    await RunCheckedAsync(
        dotnetPath,
        [
            "test",
            "--project",
            Path.Join("tests", "Csls.Tests", "Csls.Tests.csproj"),
            "--filter",
            "FullyQualifiedName=Csls.Tests.DashboardLanguageServerTests.DashboardShowsAndRefreshesRealLanguageServerState"
        ],
        repositoryRoot,
        "CSLS_DASHBOARD_DOCS_SCREENSHOT_PATH",
        generatedDashboardScreenshotPath).ConfigureAwait(false);
    await WriteFramedScreenshotAsync(
        generatedDashboardScreenshotPath,
        dashboardScreenshotPath,
        "csls dashboard",
        "dashboard").ConfigureAwait(false);
    generatedDashboardScreenshotPath = null;

    await RunCheckedAsync(
        "npm",
        ["ci", "--prefix", "docs-site"],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        "npm",
        ["run", "build", "--prefix", "docs-site"],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        dotnetPath,
        ["run", "--file", Path.Join("scripts", "Verify-Docs.cs")],
        repositoryRoot).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Path.GetRelativePath(repositoryRoot, helixScreenshotPath))
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Path.GetRelativePath(repositoryRoot, dashboardScreenshotPath))
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
    DeleteGeneratedScreenshot(generatedHelixScreenshotPath);
    DeleteGeneratedScreenshot(generatedDashboardScreenshotPath);
}

static void DeleteGeneratedScreenshot(string? path)
{
    if (path is not null && File.Exists(path))
    {
        File.Delete(path);
    }
}

static async Task WriteFramedScreenshotAsync(
    string generatedPath,
    string destinationPath,
    string title,
    string screenshotName)
{
    if (!File.Exists(generatedPath) || new FileInfo(generatedPath).Length == 0)
    {
        throw new InvalidDataException($"The {screenshotName} screenshot was not generated.");
    }

    string terminalSvg = await File.ReadAllTextAsync(generatedPath).ConfigureAwait(false);
    string framedSvg = FrameTerminalSvg(terminalSvg, title);
    await File.WriteAllTextAsync(destinationPath, framedSvg).ConfigureAwait(false);
    File.Delete(generatedPath);
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

static string FrameTerminalSvg(string terminalSvg, string title)
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
    string encodedTitle = new XText(title).ToString(SaveOptions.DisableFormatting);
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
          <text x="{titleCenter}" y="37" text-anchor="middle" fill="#b1bac4" font-family="system-ui, sans-serif" font-size="14">{encodedTitle}</text>
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
