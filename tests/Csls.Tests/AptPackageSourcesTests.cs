using Csls.Support;
using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Verifies source isolation through real APT processes and private configuration files.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public sealed class AptPackageSourcesTests
{
    private static readonly string[] s_distributionSources =
    [
        "/etc/apt/sources.list.d/ubuntu.sources",
        "/etc/apt/sources.list.d/debian.sources",
        "/etc/apt/sources.list.d/0000debian.sources",
        "/etc/apt/sources.list"
    ];

    /// <summary>
    /// Gets the cancellation token for the owned APT processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reads the selected distribution sources while excluding an invalid additional feed.
    /// </summary>
    [TestMethod]
    [DataRow("apt-get")]
    [DataRow("apt-cache")]
    public async Task SelectedSourcesExcludeBrokenAdditionalFeed(string executable)
    {
        string root = Directory.CreateTempSubdirectory("csls-apt-sources-").FullName;
        try
        {
            string source = FindDistributionSources();
            string original = await File.ReadAllTextAsync(source, TestContext.CancellationToken).ConfigureAwait(false);
            string selected = Path.Join(root, "selected" + Path.GetExtension(source));
            File.Copy(source, selected);
            string parts = Directory.CreateDirectory(Path.Join(root, "sources.list.d")).FullName;
            string broken = Path.Join(parts, "broken.sources");
            const string MalformedSource = "This is not a valid APT source stanza.\n";
            await File.WriteAllTextAsync(broken, MalformedSource, TestContext.CancellationToken).ConfigureAwait(false);

            (int defaultExit, _, string defaultError) = await RunAptAsync(executable, root, selected, parts, null)
                .ConfigureAwait(false);
            Assert.AreEqual(100, defaultExit, defaultError);
            Assert.Contains("broken.sources", defaultError);

            (int selectedExit, string output, string error) = await RunAptAsync(executable, root, selected, parts, selected)
                .ConfigureAwait(false);
            Assert.AreEqual(0, selectedExit, error);
            Assert.IsNotEmpty(output);
            if (executable == "apt-get")
            {
                Assert.Contains("InRelease'", output);
            }

            Assert.DoesNotContain("broken.sources", error);
            Assert.AreEqual(original, await File.ReadAllTextAsync(source, TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(original, await File.ReadAllTextAsync(selected, TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(MalformedSource, await File.ReadAllTextAsync(broken, TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Preserves APT failure when the explicitly selected source file is malformed.
    /// </summary>
    [TestMethod]
    [DataRow("apt-get")]
    [DataRow("apt-cache")]
    public async Task MalformedSelectedSourceFails(string executable)
    {
        string root = Directory.CreateTempSubdirectory("csls-apt-invalid-").FullName;
        try
        {
            string selected = Path.Join(root, "invalid.sources");
            await File.WriteAllTextAsync(selected, "Invalid source stanza\n", TestContext.CancellationToken).ConfigureAwait(false);
            (int exitCode, _, string error) = await RunAptAsync(executable, root, selected, "-", selected).ConfigureAwait(false);
            Assert.AreEqual(100, exitCode, error);
            Assert.Contains("invalid.sources", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Rejects a removed source file before APT can interpret it as an empty repository list.
    /// </summary>
    [TestMethod]
    public void MissingSelectedSourceFails()
    {
        string root = Directory.CreateTempSubdirectory("csls-apt-missing-").FullName;
        try
        {
            string selected = Path.Join(root, "removed.sources");
            File.Copy(FindDistributionSources(), selected);
            File.Delete(selected);
            var startInfo = new ProcessStartInfo("apt-get");
            FileNotFoundException exception = Assert.ThrowsExactly<FileNotFoundException>(
                () => AptPackageSources.Configure(startInfo, "apt-get", selected));
            Assert.AreEqual(selected, exception.FileName);
            Assert.IsEmpty(startInfo.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAptAsync(
        string executable,
        string root,
        string source,
        string parts,
        string? selection)
    {
        string state = Directory.CreateDirectory(Path.Join(root, "state")).FullName;
        Directory.CreateDirectory(Path.Join(state, "lists", "partial"));
        string cache = Directory.CreateDirectory(Path.Join(root, "cache")).FullName;
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList =
            {
                "--option", $"Dir::Etc::sourcelist={source}",
                "--option", $"Dir::Etc::sourceparts={parts}",
                "--option", $"Dir::State={state}",
                "--option", $"Dir::State::status={Path.Join(state, "status")}",
                "--option", $"Dir::Cache={cache}",
                "--option", "Debug::NoLocking=true"
            }
        };
        startInfo.Environment.Remove("APT_CONFIG");
        startInfo.Environment["LC_ALL"] = "C";
        AptPackageSources.Configure(startInfo, executable, selection);
        if (executable == "apt-get")
        {
            startInfo.ArgumentList.Add("--print-uris");
            startInfo.ArgumentList.Add("update");
        }
        else
        {
            startInfo.ArgumentList.Add("policy");
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("APT did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string FindDistributionSources() =>
        s_distributionSources.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("The distribution APT source file was not found.");
}
