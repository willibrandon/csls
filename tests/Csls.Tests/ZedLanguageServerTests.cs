using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies csls through a real Zed process and the csls Zed extension.
/// </summary>
[TestClass]
public sealed class ZedLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opens a real C# document in Zed and completes a keyboard-triggered hover through csls.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public async Task ZedRequestsHoverFromCsls()
    {
        using ExternalWorkloadLease workloadLease = await ExternalWorkloadLease.AcquireAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string zedPath = EditorToolResolver.ResolveZed(repositoryRoot);
        string extensionPath = EditorToolResolver.ResolveCslsZedExtension(repositoryRoot);
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        Assert.IsTrue(File.Exists(launcherPath), $"Launcher not found at {launcherPath}.");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-zed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string workspacePath = Path.Join(fixturePath, "workspace");
            string documentPath = Path.Join(workspacePath, "Program.cs");
            string userDataPath = Path.Join(fixturePath, "zed-data");
            string configurationPath = Path.Join(userDataPath, "config", "settings.json");
            string installedExtensionPath = Path.Join(
                userDataPath,
                "extensions",
                "installed",
                "csls");
            string homePath = Path.Join(fixturePath, "home");
            string cachePath = Path.Join(fixturePath, "cache");
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(cachePath);
            CopyDirectory(extensionPath, installedExtensionPath);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                configurationPath,
                CreateConfiguration(launcherPath),
                TestContext.CancellationToken).ConfigureAwait(false);

            XDisplaySession display = await XDisplaySession.StartAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable displayCleanup =
                display.ConfigureAwait(false);
            string displayName = display.DisplayName;

            using Process zed = StartZed(
                zedPath,
                documentPath,
                userDataPath,
                homePath,
                cachePath,
                displayName,
                workspacePath,
                workerPath);
            Task<string> zedOutputTask = zed.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            Task<string> zedErrorTask = zed.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            int? serverProcessId = null;
            bool completed = false;
            try
            {
                ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                    workspacePath,
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
                serverProcessId = session.ProcessId;
                var control = new ControlRpcClient(session.SocketPath);
                await using ConfiguredAsyncDisposable controlCleanup =
                    control.ConfigureAwait(false);
                ControlDashboardSnapshot initialSnapshot = await WaitForOpenDocumentAsync(
                    control,
                    documentPath,
                    TimeSpan.FromSeconds(30),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(initialSnapshot.Documents.Single(document =>
                    PathComparer.Equals(document.FilePath, documentPath)).IsOpen);

                X11Input.FocusWindow(displayName, "Program.cs");
                await control.StartTraceAsync(TestContext.CancellationToken).ConfigureAwait(false);
                ControlTraceInfo trace;
                try
                {
                    X11Input.SendControlCharacter(displayName, 'k');
                    X11Input.SendControlCharacter(displayName, 'i');
                    await WaitForTraceEntryAsync(
                        control,
                        "textDocument/hover",
                        TimeSpan.FromSeconds(30),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    trace = await control.StopTraceAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                ControlTraceEntry hover = trace.Entries.Single(entry => string.Equals(
                    entry.Name,
                    "textDocument/hover",
                    StringComparison.Ordinal));
                Assert.AreEqual("Succeeded", hover.Status);
                Assert.IsNull(hover.ExceptionType);
                ControlHoverResult hoverResult = await control.GetHoverAsync(
                    new ControlHoverRequest
                    {
                        DocumentPath = documentPath,
                        Position = new Position(0, 2)
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(hoverResult.Found);
                Assert.IsNotNull(hoverResult.Hover);
                Assert.Contains("System.Console", hoverResult.Hover.Contents.Value);

                X11Input.SendControlCharacter(displayName, 'q');
                await zed.WaitForExitAsync(TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, zed.ExitCode);
                completed = true;
            }
            finally
            {
                if (!zed.HasExited)
                {
                    zed.Kill(entireProcessTree: true);
                    await zed.WaitForExitAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                string zedOutput = await zedOutputTask.ConfigureAwait(false);
                string zedError = await zedErrorTask.ConfigureAwait(false);
                TestContext.WriteLine(zedOutput);
                TestContext.WriteLine(zedError);
                string zedLogPath = Path.Join(userDataPath, "logs", "Zed.log");
                if (!completed && File.Exists(zedLogPath))
                {
                    TestContext.WriteLine(await File.ReadAllTextAsync(
                        zedLogPath,
                        TestContext.CancellationToken).ConfigureAwait(false));
                }

                if (serverProcessId is int processId)
                {
                    await ProcessExitWaiter.WaitAsync(
                        processId,
                        TimeSpan.FromSeconds(10),
                        TestContext.CancellationToken).ConfigureAwait(false);
                }

            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        Console.WriteLine("hello");
        """;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static Process StartZed(
        string zedPath,
        string documentPath,
        string userDataPath,
        string homePath,
        string cachePath,
        string displayName,
        string workspacePath,
        string workerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = zedPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath
        };
        startInfo.ArgumentList.Add("--foreground");
        startInfo.ArgumentList.Add("--user-data-dir");
        startInfo.ArgumentList.Add(userDataPath);
        startInfo.ArgumentList.Add(workspacePath);
        startInfo.ArgumentList.Add($"{documentPath}:1:3");
        startInfo.Environment["DISPLAY"] = displayName;
        startInfo.Environment["CSLS_WORKER_PATH"] = workerPath;
        startInfo.Environment["HOME"] = homePath;
        startInfo.Environment["NO_AT_BRIDGE"] = "1";
        startInfo.Environment.Remove("WAYLAND_DISPLAY");
        startInfo.Environment["XDG_CACHE_HOME"] = cachePath;
        startInfo.Environment["XDG_SESSION_TYPE"] = "x11";
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Zed did not start.");
    }

    private static async Task<ControlDashboardSnapshot> WaitForOpenDocumentAsync(
        ControlRpcClient control,
        string documentPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                ControlDashboardSnapshot snapshot =
                    await control.GetDashboardSnapshotAsync(
                        new ControlDashboardRequest { IncludeDiagnostics = false },
                        timeoutSource.Token).ConfigureAwait(false);
                if (snapshot.Documents.Any(document =>
                    document.IsOpen && PathComparer.Equals(document.FilePath, documentPath)))
                {
                    return snapshot;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Zed did not open {documentPath} through csls.");
        }

        throw new InvalidOperationException("The open-document polling loop ended unexpectedly.");
    }

    private static async Task<ControlTraceInfo> WaitForTraceEntryAsync(
        ControlRpcClient control,
        string requestName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                ControlDashboardSnapshot snapshot =
                    await control.GetDashboardSnapshotAsync(
                        new ControlDashboardRequest { IncludeDiagnostics = false },
                        timeoutSource.Token).ConfigureAwait(false);
                if (snapshot.Requests.Trace.Entries.Any(entry =>
                    string.Equals(entry.Name, requestName, StringComparison.Ordinal) &&
                    entry.CompletedAt.HasValue))
                {
                    return snapshot.Requests.Trace;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Zed did not complete {requestName} through csls.");
        }

        throw new InvalidOperationException("The trace polling loop ended unexpectedly.");
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (string directoryPath in Directory.EnumerateDirectories(
            sourcePath,
            "*",
            SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(
                destinationPath,
                Path.GetRelativePath(sourcePath, directoryPath)));
        }

        foreach (string filePath in Directory.EnumerateFiles(
            sourcePath,
            "*",
            SearchOption.AllDirectories))
        {
            File.Copy(
                filePath,
                Path.Join(destinationPath, Path.GetRelativePath(sourcePath, filePath)));
        }
    }

    private static string CreateConfiguration(string launcherPath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            {
              "auto_install_extensions": {
                "csls": false,
                "csharp": false,
                "html": false
              },
              "auto_update": false,
              "languages": {
                "CSharp": {
                  "language_servers": ["csls"]
                }
              },
              "lsp": {
                "csls": {
                  "binary": {
                    "path": {{ToJsonString(dotnetPath)}},
                    "arguments": [{{ToJsonString(launcherPath)}}, "lsp"]
                  },
                  "settings": {
                    "enableAnalyzers": true,
                    "configuration": "Debug"
                  }
                }
              },
              "session": {
                "trust_all_worktrees": true
              },
              "telemetry": {
                "diagnostics": false,
                "metrics": false
              }
            }
            """;
    }

    private static string ToJsonString(string value) =>
        $"\"{JsonEncodedText.Encode(value)}\"";
}
