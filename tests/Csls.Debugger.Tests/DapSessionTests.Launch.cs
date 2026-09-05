using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies launched debugger target activation and output forwarding.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves inherited loader settings when the adapter starts an application with or without debugging.
    /// </summary>
    /// <param name="variable">The inherited native loader environment variable.</param>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    [TestMethod]
    [DataRow("LD_PRELOAD", false)]
    [DataRow("LD_PRELOAD", true)]
    [DataRow("LD_LIBRARY_PATH", false)]
    [DataRow("LD_LIBRARY_PATH", true)]
    [DataRow("DYLD_INSERT_LIBRARIES", false)]
    [DataRow("DYLD_INSERT_LIBRARIES", true)]
    [DataRow("DYLD_LIBRARY_PATH", false)]
    [DataRow("DYLD_LIBRARY_PATH", true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchPreservesInheritedLoaderEnvironment(string variable, bool noDebug) =>
        AssertLaunchedEnvironmentAsync(variable, noDebug, Environment.GetEnvironmentVariable(variable));

    /// <summary>
    /// Preserves explicit empty and populated parent loader settings through worker activation.
    /// </summary>
    /// <param name="variable">The inherited native loader search variable.</param>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    /// <param name="empty">Whether the parent explicitly supplies an empty value.</param>
    [TestMethod]
    [DataRow("LD_LIBRARY_PATH", false, false)]
    [DataRow("LD_LIBRARY_PATH", true, false)]
    [DataRow("LD_LIBRARY_PATH", false, true)]
    [DataRow("LD_LIBRARY_PATH", true, true)]
    [DataRow("DYLD_LIBRARY_PATH", false, false)]
    [DataRow("DYLD_LIBRARY_PATH", true, false)]
    [DataRow("DYLD_LIBRARY_PATH", false, true)]
    [DataRow("DYLD_LIBRARY_PATH", true, true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchPreservesExplicitParentLoaderSetting(string variable, bool noDebug, bool empty)
    {
        string value = empty ? string.Empty : FindRepositoryRoot();
        return AssertLaunchedEnvironmentAsync(variable, noDebug, value,
            new Dictionary<string, string?> { [variable] = value });
    }

    /// <summary>
    /// Applies explicit target loader replacements and removals after restoring the caller's environment.
    /// </summary>
    /// <param name="variable">The inherited native loader search variable.</param>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    /// <param name="remove">Whether the target removes the inherited setting.</param>
    [TestMethod]
    [DataRow("LD_LIBRARY_PATH", false, false)]
    [DataRow("LD_LIBRARY_PATH", true, false)]
    [DataRow("LD_LIBRARY_PATH", false, true)]
    [DataRow("LD_LIBRARY_PATH", true, true)]
    [DataRow("DYLD_LIBRARY_PATH", false, false)]
    [DataRow("DYLD_LIBRARY_PATH", true, false)]
    [DataRow("DYLD_LIBRARY_PATH", false, true)]
    [DataRow("DYLD_LIBRARY_PATH", true, true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchAppliesTargetLoaderOverrides(string variable, bool noDebug, bool remove)
    {
        string? value = remove ? null : Path.Join(FindRepositoryRoot(), "test-assets");
        return AssertLaunchedEnvironmentAsync(variable, noDebug, value,
            new Dictionary<string, string?> { [variable] = FindRepositoryRoot() },
            new Dictionary<string, string?> { [variable] = value });
    }

    /// <summary>
    /// Keeps the worker's saved native loader configuration out of the target process.
    /// </summary>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchKeepsWorkerLoaderSnapshotPrivate(bool noDebug) => AssertLaunchedEnvironmentAsync(
        "CSLS_DEBUGGER_INHERITED_LOADER_ENVIRONMENT", noDebug, expected: null);

    /// <summary>
    /// Preserves a caller-supplied preload even when it names the library also required by the worker.
    /// </summary>
    /// <param name="noDebug">Whether to launch without attaching the managed debugger.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchPreservesCallerPreloadedLibrary(bool noDebug)
    {
        (string variable, string library) = GetCallerPreload();
        Assert.IsTrue(File.Exists(library), $"The real preload library is missing: {library}");
        return AssertLaunchedEnvironmentAsync(variable, noDebug, library,
            new Dictionary<string, string?> { [variable] = library });
    }

    private static (string Variable, string Library) GetCallerPreload()
    {
        string worker = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_WORKER_TEST_PATH")
            ?? Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");
        string directory = Path.GetDirectoryName(Path.GetFullPath(worker))
            ?? throw new InvalidOperationException("The debugger worker has no containing directory.");
        return OperatingSystem.IsMacOS()
            ? ("DYLD_INSERT_LIBRARIES", Path.Join(directory, "Csls.Debugger.UnixWait.dylib"))
            : ("LD_PRELOAD", Path.Join(directory, "Csls.Debugger.UnixWait.so"));
    }

    private async Task AssertLaunchedEnvironmentAsync(
        string variable,
        bool noDebug,
        string? expected,
        IReadOnlyDictionary<string, string?>? parentEnvironment = null,
        IReadOnlyDictionary<string, string?>? targetEnvironment = null,
        string? environmentFile = null,
        string? workingDirectory = null)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken, parentEnvironment)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        await AssertEnvironmentLaunchAsync(client, variable, noDebug, expected, targetEnvironment,
            environmentFile, workingDirectory).ConfigureAwait(false);
    }

    private async Task AssertEnvironmentLaunchAsync(
        DapTestClient client,
        string variable,
        bool noDebug,
        string? expected,
        IReadOnlyDictionary<string, string?>? targetEnvironment = null,
        string? environmentFile = null,
        string? workingDirectory = null)
    {
        int launch = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", ResolveTestProcessHost());
            writer.WriteBoolean("noDebug", noDebug);
            if (environmentFile is not null)
            {
                writer.WriteString("envFile", environmentFile);
            }

            if (workingDirectory is not null)
            {
                writer.WriteString("cwd", workingDirectory);
            }
            writer.WriteStartArray("args");
            writer.WriteStringValue("--print-environment-entry");
            writer.WriteStringValue(variable);
            writer.WriteEndArray();
            WriteDefaultSourceFileMap(writer);
            if (targetEnvironment is not null)
            {
                writer.WriteStartObject("env");
                foreach ((string name, string? value) in targetEnvironment)
                {
                    writer.WriteString(name, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
        }

        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, launch, "launch", success: true);
        }

        using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(process.RootElement, "process");
            Assert.IsGreaterThan(0, process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());
        }

        var output = new System.Text.StringBuilder();
        bool exited = false;
        while (true)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "terminated")
            {
                Assert.IsTrue(exited);
                break;
            }

            if (eventName == "exited")
            {
                Assert.IsFalse(exited);
                Assert.AreEqual(23, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                exited = true;
                continue;
            }

            AssertEvent(root, "output");
            Assert.AreEqual("stdout", root.GetProperty("body").GetProperty("category").GetString());
            _ = output.Append(root.GetProperty("body").GetProperty("output").GetString());
        }

        Assert.AreEqual(expected is null ? "unset" : $"set:{expected}", output.ToString(), variable);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsEmpty(client.Diagnostics.ToString());
    }

    /// <summary>
    /// Loads UTF-8 environment files through the managed and no-debug launch paths.
    /// </summary>
    /// <param name="noDebug">Whether to launch without managed debugging.</param>
    /// <param name="assignment">The file's assignment and comment syntax.</param>
    /// <param name="expected">The exact value observed inside the application.</param>
    [TestMethod]
    [DataRow(false, "CSLS_ENV_FILE_VALUE=plain", "plain")]
    [DataRow(true, "CSLS_ENV_FILE_VALUE=plain", "plain")]
    [DataRow(false, "\uFEFF# comment\r\nexport CSLS_ENV_FILE_VALUE = 'quoted π # literal'\r\n", "quoted π # literal")]
    [DataRow(true, "\uFEFF# comment\r\nexport CSLS_ENV_FILE_VALUE = 'quoted π # literal'\r\n", "quoted π # literal")]
    [DataRow(false, "CSLS_ENV_FILE_VALUE=first\nCSLS_ENV_FILE_VALUE=last # comment", "last")]
    [DataRow(true, "CSLS_ENV_FILE_VALUE=first\nCSLS_ENV_FILE_VALUE=last # comment", "last")]
    [DataRow(false, "CSLS_ENV_FILE_VALUE=\n", "")]
    [DataRow(true, "CSLS_ENV_FILE_VALUE=\n", "")]
    [DataRow(false, "CSLS_ENV_FILE_VALUE=\"line\\nnext\\t\\\"quote\\\"\"", "line\nnext\t\"quote\"")]
    [DataRow(true, "CSLS_ENV_FILE_VALUE=\"line\\nnext\\t\\\"quote\\\"\"", "line\nnext\t\"quote\"")]
    [DataRow(false, "CSLS_ENV_FILE_VALUE='${LITERAL}\\path=a#b'", "${LITERAL}\\path=a#b")]
    [DataRow(true, "CSLS_ENV_FILE_VALUE='${LITERAL}\\path=a#b'", "${LITERAL}\\path=a#b")]
    [DataRow(false, "CSLS_ENV_FILE_VALUE=\"line\nnext\" # comment", "line\nnext")]
    [DataRow(true, "CSLS_ENV_FILE_VALUE=\"line\nnext\" # comment", "line\nnext")]
    [DataRow(false, "CSLS_ENV_FILE_VALUE=\"line  \n  next  \"", "line  \n  next  ")]
    [DataRow(true, "CSLS_ENV_FILE_VALUE=\"line  \n  next  \"", "line  \n  next  ")]
    [DataRow(false, "", "inherited")]
    [DataRow(true, "", "inherited")]
    [DataRow(false, "# comments only\n \t", "inherited")]
    [DataRow(true, "# comments only\n \t", "inherited")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LaunchLoadsEnvironmentFile(bool noDebug, string assignment, string expected)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-environment-file-");
        string path = Path.Join(directory.FullName, ".env");
        try
        {
            await File.WriteAllTextAsync(path, assignment, TestContext.CancellationToken).ConfigureAwait(false);
            await AssertLaunchedEnvironmentAsync("CSLS_ENV_FILE_VALUE", noDebug, expected,
                new Dictionary<string, string?> { ["CSLS_ENV_FILE_VALUE"] = "inherited" },
                environmentFile: ".env", workingDirectory: directory.FullName).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Applies explicit environment replacements and removals after environment-file values.
    /// </summary>
    /// <param name="noDebug">Whether to launch without managed debugging.</param>
    /// <param name="replacement">The explicit replacement or null removal.</param>
    [TestMethod]
    [DataRow(false, "explicit")]
    [DataRow(true, "explicit")]
    [DataRow(false, "")]
    [DataRow(true, "")]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LaunchEnvironmentOverridesFile(bool noDebug, string? replacement)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-environment-file-");
        string path = Path.Join(directory.FullName, ".env");
        try
        {
            await File.WriteAllTextAsync(path, "CSLS_ENV_FILE_VALUE=file", TestContext.CancellationToken)
                .ConfigureAwait(false);
            await AssertLaunchedEnvironmentAsync("CSLS_ENV_FILE_VALUE", noDebug, replacement,
                new Dictionary<string, string?> { ["CSLS_ENV_FILE_VALUE"] = "inherited" },
                new Dictionary<string, string?> { ["CSLS_ENV_FILE_VALUE"] = replacement },
                environmentFile: path).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Rejects malformed environment files without exposing values and accepts a corrected file in the same session.
    /// </summary>
    /// <param name="noDebug">Whether to launch without managed debugging.</param>
    /// <param name="scenario">The invalid input at the real file boundary.</param>
    [TestMethod]
    [DataRow(false, "missing")]
    [DataRow(true, "missing")]
    [DataRow(false, "directory")]
    [DataRow(true, "directory")]
    [DataRow(false, "utf8")]
    [DataRow(true, "utf8")]
    [DataRow(false, "oversize")]
    [DataRow(true, "oversize")]
    [DataRow(false, "no-assignment")]
    [DataRow(true, "no-assignment")]
    [DataRow(false, "name")]
    [DataRow(true, "name")]
    [DataRow(false, "nul")]
    [DataRow(true, "nul")]
    [DataRow(false, "quote")]
    [DataRow(true, "quote")]
    [DataRow(false, "suffix")]
    [DataRow(true, "suffix")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LaunchRejectsInvalidEnvironmentFileAndRecovers(bool noDebug, string scenario)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-environment-file-");
        string path = Path.Join(directory.FullName, ".env");
        try
        {
            if (scenario == "directory")
            {
                Directory.CreateDirectory(path);
            }
            else if (scenario != "missing")
            {
                byte[] bytes = scenario switch
                {
                    "utf8" => [0xc0, 0xaf],
                    "oversize" => new byte[1024 * 1024 + 1],
                    _ => System.Text.Encoding.UTF8.GetBytes(scenario switch
                    {
                        "no-assignment" => "sensitive-value",
                        "name" => "INVALID NAME=sensitive-value",
                        "nul" => "VALUE=sensitive-value\0",
                        "quote" => "VALUE=\"sensitive-value",
                        "suffix" => "VALUE=\"sensitive-value\"junk",
                        _ => throw new ArgumentException("Unknown malformed envFile scenario.", nameof(scenario))
                    })
                };
                await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken).ConfigureAwait(false);
            }

            await AssertInvalidEnvironmentFileRecoveryAsync(path, noDebug).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            directory.Delete();
        }
    }

    /// <summary>
    /// Rejects a Unix FIFO immediately and keeps the adapter usable for a subsequent file-backed launch.
    /// </summary>
    /// <param name="noDebug">Whether to launch without managed debugging.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LaunchRejectsEnvironmentFifoAndRecovers(bool noDebug)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-environment-fifo-");
        string path = Path.Join(directory.FullName, ".env");
        try
        {
            var command = new System.Diagnostics.ProcessStartInfo("mkfifo") { ArgumentList = { path } };
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(command,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, output + error);
            await AssertInvalidEnvironmentFileRecoveryAsync(path, noDebug).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Accepts an environment file at the byte limit while preserving an inherited target value.
    /// </summary>
    /// <param name="noDebug">Whether to launch without managed debugging.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LaunchAcceptsEnvironmentFileAtByteLimit(bool noDebug)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-environment-file-");
        string path = Path.Join(directory.FullName, ".env");
        try
        {
            await File.WriteAllTextAsync(path, "#" + new string('x', 1024 * 1024 - 1), TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(1024 * 1024, new FileInfo(path).Length);
            await AssertLaunchedEnvironmentAsync("CSLS_ENV_FILE_VALUE", noDebug, "inherited",
                new Dictionary<string, string?> { ["CSLS_ENV_FILE_VALUE"] = "inherited" }, environmentFile: path)
                .ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private async Task AssertInvalidEnvironmentFileRecoveryAsync(string path, bool noDebug)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", ResolveTestProcessHost());
            writer.WriteBoolean("noDebug", noDebug);
            writer.WriteString("envFile", path);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        foreach ((int sequence, string command) in new[] { (configuration, "configurationDone"), (launch, "launch") })
        {
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, command, success: false);
            Assert.DoesNotContain("sensitive-value", response.RootElement.ToString(), StringComparison.Ordinal);
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }

        await File.WriteAllTextAsync(path, "CSLS_ENV_FILE_VALUE=recovered", TestContext.CancellationToken)
            .ConfigureAwait(false);
        await AssertEnvironmentLaunchAsync(client, "CSLS_ENV_FILE_VALUE", noDebug, "recovered", environmentFile: path)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects malformed entry-stop options before launching a target.
    /// </summary>
    [TestMethod]
    [DataRow("null")]
    [DataRow("\"true\"")]
    [DataRow("1")]
    [DataRow("{}")]
    [DataRow("[]")]
    public Task LaunchRejectsInvalidStopAtEntry(string option) => AssertInvalidLaunchOptionAsync("stopAtEntry", option);

    /// <summary>
    /// Rejects empty and non-string environment-file options before target configuration.
    /// </summary>
    /// <param name="option">The malformed JSON environment-file value.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("true")]
    [DataRow("1")]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("\"\"")]
    [DataRow("\" \"")]
    public Task LaunchRejectsInvalidEnvironmentFileOption(string option) => AssertInvalidLaunchOptionAsync("envFile", option);

    private async Task AssertInvalidLaunchOptionAsync(string name, string option)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int sequence = await client.SendRequestAsync(
            "launch",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WritePropertyName(name);
                writer.WriteRawValue(option);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "launch", success: false);
        Assert.Contains(
            name,
            response.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
        await client.CloseProtocolAsync().ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Launches a real managed process after configuration and forwards its output and exit.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NoDebugLaunchRunsOwnedProcessAfterConfiguration(bool? stopAtEntry)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        JsonElement capabilities = initialize.RootElement.GetProperty("body");
        Assert.IsTrue(capabilities.GetProperty("supportsConfigurationDoneRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsModulesRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsLoadedSourcesRequest").GetBoolean());
        Assert.IsTrue(
            capabilities.GetProperty("supportsBreakpointLocationsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsFunctionBreakpoints").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsConditionalBreakpoints").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsLogPoints").GetBoolean());
        Assert.HasCount(
            3,
            capabilities.GetProperty("exceptionBreakpointFilters").EnumerateArray().ToArray());
        Assert.IsTrue(capabilities.GetProperty("supportsExceptionInfoRequest").GetBoolean());
        Assert.IsFalse(capabilities.TryGetProperty("supportsVariablePaging", out _));
        Assert.IsTrue(capabilities.GetProperty("supportsEvaluateForHovers").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsCompletionsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsSetVariable").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsSetExpression").GetBoolean());
        Assert.IsFalse(capabilities.TryGetProperty("supportsInvalidatedEvent", out _));
        Assert.IsTrue(capabilities.GetProperty("supportsCancelRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsReadMemoryRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsDisassembleRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsInstructionBreakpoints").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsStepInTargetsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsGotoTargetsRequest").GetBoolean());
        Assert.IsTrue(capabilities.GetProperty("supportsRestartRequest").GetBoolean());
        Assert.HasCount(22, capabilities.EnumerateObject().ToArray());

        string processHost = ResolveTestProcessHost();
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                processHost,
                ["--print-environment", "CSLS_DEBUGGER_TEST_VALUE"],
                wait: false,
                stopAtEntry: stopAtEntry),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");

        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            configuration.RootElement,
            configurationSequence,
            "configurationDone",
            success: true);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(process.RootElement, "process");
        JsonElement processBody = process.RootElement.GetProperty("body");
        Assert.IsGreaterThan(0, processBody.GetProperty("systemProcessId").GetInt32());
        string runtimeHost = Environment.GetEnvironmentVariable("CSLS_RUNTIME_HOST_PATH") ??
            "dotnet";
        Assert.AreEqual(
            Path.GetFileNameWithoutExtension(runtimeHost),
            processBody.GetProperty("name").GetString());

        using JsonDocument output = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(output.RootElement, "output");
        Assert.AreEqual("stdout", output.RootElement.GetProperty("body").GetProperty("category").GetString());
        Assert.AreEqual(
            "transport-π-é",
            output.RootElement.GetProperty("body").GetProperty("output").GetString());
        using JsonDocument exited = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(exited.RootElement, "exited");
        Assert.AreEqual(0, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
        using JsonDocument terminated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(terminated.RootElement, "terminated");
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    /// <summary>
    /// Launches a real managed assembly through dbgshim and preserves DAP protocol output.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedLaunchActivatesCoreClrAndForwardsTargetOutput(bool? stopAtEntry)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--print-environment-and-exit", "CSLS_DEBUGGER_TEST_VALUE", "23"],
                wait: false,
                noDebug: false,
                stopAtEntry: stopAtEntry),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");

        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            configuration.RootElement,
            configurationSequence,
            "configurationDone",
            success: true);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(process.RootElement, "process");
        Assert.IsGreaterThan(
            0,
            process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());

        using JsonDocument output = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(output.RootElement, "output");
        Assert.AreEqual("stdout", output.RootElement.GetProperty("body").GetProperty("category").GetString());
        Assert.AreEqual(
            "transport-π-é",
            output.RootElement.GetProperty("body").GetProperty("output").GetString());
        using JsonDocument exited = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(exited.RootElement, "exited");
        Assert.AreEqual(23, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
        using JsonDocument terminated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(terminated.RootElement, "terminated");
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

}
