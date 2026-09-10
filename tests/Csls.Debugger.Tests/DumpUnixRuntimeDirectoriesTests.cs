using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies local Unix runtime discovery through actual installations, host files, and isolated processes.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
public sealed class DumpUnixRuntimeDirectoriesTests : DapTestContext
{
    private const string ProbeVariable = "CSLS_DUMP_RUNTIME_DISCOVERY_PROBE";

    /// <summary>
    /// Preserves host root precedence and reads each duplicated framework installation once.
    /// </summary>
    [TestMethod]
    public void HostSettingsPreserveRootOrderAndDeduplicateVersions()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This test exercises Unix installation discovery.");
        }
        string root = Directory.CreateTempSubdirectory("csls-dump-installations-").FullName;
        try
        {
            string current = AddInstallation(root, "current");
            string architecture = AddInstallation(root, "architecture");
            string registered = AddInstallation(root, "registered");
            string defaultVersion = AddInstallation(root, "default");
            string registration = Directory.CreateDirectory(Path.Join(root, "registration")).FullName;
            File.WriteAllText(ArchitectureRegistration(registration), Path.Join(root, "registered") + "\n");

            IReadOnlyList<string> found = DumpUnixRuntimeDirectories.GetDirectories(current,
                Path.Join(root, "architecture"), Path.Join(root, "architecture", "."), registration, Path.Join(root, "default"));

            Assert.AreSequenceEqual(new[] { current, architecture, registered, defaultVersion }, found);
            foreach (string directory in found)
            {
                Assert.IsTrue(File.Exists(Path.Join(directory, Path.GetFileName(typeof(Enumerable).Assembly.Location))));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Reads the legacy registration only when the architecture-specific file is absent.
    /// </summary>
    /// <param name="architectureSpecific">Whether the host has an architecture-specific registration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RegistrationSelectsTheHostInstallation(bool architectureSpecific)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This test exercises Unix installation discovery.");
        }
        string root = Directory.CreateTempSubdirectory("csls-dump-registration-").FullName;
        try
        {
            string architecture = AddInstallation(root, "architecture");
            string legacy = AddInstallation(root, "legacy");
            File.WriteAllText(Path.Join(root, "install_location"), Path.Join(root, "legacy"));
            if (architectureSpecific)
            {
                File.WriteAllText(ArchitectureRegistration(root), Path.Join(root, "architecture") + "\nignored second line");
            }

            IReadOnlyList<string> found = DumpUnixRuntimeDirectories.GetDirectories(
                Path.Join(root, "standalone"), null, "relative-root", root, Path.Join(root, "missing"));

            Assert.AreSequenceEqual(new[] { Path.Join(root, "standalone"), architectureSpecific ? architecture : legacy }, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Bounds malformed registration reads, retains independent roots, and discovers a repaired registration.
    /// </summary>
    /// <param name="malformation">The actual file or device boundary to reject.</param>
    [TestMethod]
    [DataRow("empty")]
    [DataRow("relative")]
    [DataRow("utf8")]
    [DataRow("nul")]
    [DataRow("oversized")]
    [DataRow("directory")]
    [DataRow("fifo")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MalformedRegistrationPreservesOtherInstallationsAndRecovers(string malformation)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This test exercises Unix installation discovery.");
        }
        string root = Directory.CreateTempSubdirectory("csls-dump-invalid-registration-").FullName;
        try
        {
            string generic = AddInstallation(root, "generic");
            string registered = AddInstallation(root, "registered");
            string legacy = AddInstallation(root, "legacy");
            string path = ArchitectureRegistration(root);
            await File.WriteAllTextAsync(Path.Join(root, "install_location"), Path.Join(root, "legacy"),
                TestContext.CancellationToken).ConfigureAwait(false);
            if (malformation == "fifo")
            {
                var command = new ProcessStartInfo("mkfifo") { ArgumentList = { path } };
                (int ExitCode, string Output, string Error) result = await DebuggerTestProcess.RunAsync(
                    command, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
            }
            else if (malformation == "directory")
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                byte[] content = malformation switch
                {
                    "empty" => [],
                    "relative" => "relative-root"u8.ToArray(),
                    "utf8" => [0x2f, 0xff],
                    "nul" => Encoding.UTF8.GetBytes(Path.Join(root, "registered") + '\0'),
                    "oversized" => Encoding.UTF8.GetBytes(Path.Join(root, "registered") + "\n" + new string('x', 4096)),
                    _ => throw new AssertFailedException("Unknown registration malformation.")
                };
                await File.WriteAllBytesAsync(path, content, TestContext.CancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<string> found = Discover();
            Assert.AreSequenceEqual(new[] { Path.Join(root, "standalone"), generic }, found);
            Assert.DoesNotContain(legacy, found);
            if (malformation == "directory")
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
            await File.WriteAllTextAsync(path, Path.Join(root, "registered"), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreSequenceEqual(new[] { Path.Join(root, "standalone"), generic, registered }, Discover());

            IReadOnlyList<string> Discover() => DumpUnixRuntimeDirectories.GetDirectories(Path.Join(root, "standalone"),
                null, Path.Join(root, "generic"), root, Path.Join(root, "missing"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Accepts bounded installation files and rejects the immediately oversized file before discovery.
    /// </summary>
    /// <param name="bytes">The exact registration-file length.</param>
    /// <param name="accepted">Whether the bounded file supplies an installation.</param>
    [TestMethod]
    [DataRow(4095, true)]
    [DataRow(4096, true)]
    [DataRow(4097, false)]
    public void RegistrationByteBudgetIsExact(int bytes, bool accepted)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This test exercises Unix installation discovery.");
        }
        string root = Directory.CreateTempSubdirectory("csls-dump-registration-bytes-").FullName;
        try
        {
            string installed = AddInstallation(root, "registered");
            byte[] content = new byte[bytes];
            content.AsSpan().Fill((byte)'x');
            _ = Encoding.UTF8.GetBytes(Path.Join(root, "registered") + '\n', content);
            File.WriteAllBytes(ArchitectureRegistration(root), content);
            IReadOnlyList<string> found = DumpUnixRuntimeDirectories.GetDirectories(
                Path.Join(root, "standalone"), null, null, root, Path.Join(root, "missing"));
            string[] expected = accepted ? [Path.Join(root, "standalone"), installed] : [Path.Join(root, "standalone")];
            Assert.AreSequenceEqual(expected, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Applies the same directory budget across distinct installation roots and duplicated settings.
    /// </summary>
    [TestMethod]
    public void DirectoryBudgetIsSharedAcrossInstallations()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This test exercises Unix installation discovery.");
        }
        string root = Directory.CreateTempSubdirectory("csls-dump-installation-budget-").FullName;
        try
        {
            for (int index = 0; index < 1023; index++)
            {
                Directory.CreateDirectory(Path.Join(root, index % 2 == 0 ? "first" : "second", "shared",
                    "Microsoft.NETCore.App", index.ToString(CultureInfo.InvariantCulture)));
            }
            Assert.HasCount(1024, Discover());
            Directory.CreateDirectory(Path.Join(root, "second", "shared", "Microsoft.NETCore.App", "overflow"));
            InvalidDataException failure = Assert.ThrowsExactly<InvalidDataException>(() => Discover());
            Assert.AreEqual("Local runtime discovery exceeds the 1024-directory limit.", failure.Message);
            Directory.Delete(Path.Join(root, "second", "shared", "Microsoft.NETCore.App", "overflow"));
            Assert.HasCount(1024, Discover());

            IReadOnlyList<string> Discover() => DumpUnixRuntimeDirectories.GetDirectories(Path.Join(root, "standalone"),
                Path.Join(root, "first"), Path.Join(root, "second"), root, Path.Join(root, "first"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Resolves actual metadata through both host environment roots without changing the parent test environment.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HostEnvironmentRootsReachCapturedMetadata()
    {
        string? isolatedRoot = Environment.GetEnvironmentVariable(ProbeVariable);
        if (isolatedRoot is not null)
        {
            await VerifyCapturedMetadataAsync(isolatedRoot).ConfigureAwait(false);
            return;
        }
        string root = Directory.CreateTempSubdirectory("csls-dump-host-environment-").FullName;
        try
        {
            foreach (string name in new[] { "architecture", "generic" })
            {
                string version = AddInstallation(root, name);
                File.Copy(typeof(Enumerable).Assembly.Location, Path.Join(version, Path.GetFileName(root) + '-' + name + ".dll"));
            }
            string results = Directory.CreateDirectory(Path.Join(root, "results")).FullName;
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = FindRepositoryRoot() };
            foreach (string argument in new[] { "test", "--root-directory", AppContext.BaseDirectory, "--test-modules",
                Path.GetFileName(typeof(DumpUnixRuntimeDirectoriesTests).Assembly.Location), "--filter",
                $"FullyQualifiedName={typeof(DumpUnixRuntimeDirectoriesTests).FullName}.{nameof(HostEnvironmentRootsReachCapturedMetadata)}",
                "--report-trx", "--results-directory", results })
            {
                start.ArgumentList.Add(argument);
            }
            string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
            start.Environment[$"DOTNET_ROOT_{architecture}"] = Path.Join(root, "architecture");
            start.Environment["DOTNET_ROOT"] = Path.Join(root, "generic");
            start.Environment[ProbeVariable] = root;
            (int ExitCode, string Output, string Error) result = await DebuggerTestProcess.RunAsync(
                start, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
            string report = Assert.ContainsSingle(Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            XElement test = Assert.ContainsSingle(XDocument.Load(report).Descendants(ns + "UnitTestResult"));
            Assert.AreEqual(nameof(HostEnvironmentRootsReachCapturedMetadata), (string?)test.Attribute("testName"));
            Assert.AreEqual("Passed", (string?)test.Attribute("outcome"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task VerifyCapturedMetadataAsync(string root)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        using var target = DataTarget.LoadDump(fixture.DumpPath, new DataTargetOptions { SymbolPaths = [] });
        var source = new DumpCorDebugSource(Assert.ContainsSingle(target.ClrVersions), null);
        foreach (string name in new[] { "architecture", "generic" })
        {
            string file = Path.GetFileName(root) + '-' + name + ".dll";
            string expected = Path.Join(InstallationVersion(root, name), file);
            string found = Assert.ContainsSingle(source.FindMetadataImages(file).Where(File.Exists));
            Assert.AreEqual(expected, found);
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(typeof(Enumerable).Assembly.Location,
                TestContext.CancellationToken).ConfigureAwait(false), await File.ReadAllBytesAsync(found,
                TestContext.CancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Rejects a FIFO library candidate without requiring a writer and permits subsequent regular-file access.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeLibraryIdentityRejectsFifo()
    {
        string root = Directory.CreateTempSubdirectory("csls-dump-library-fifo-").FullName;
        try
        {
            string path = Path.Join(root, "library");
            var command = new ProcessStartInfo("mkfifo") { ArgumentList = { path } };
            (int ExitCode, string Output, string Error) result = await DebuggerTestProcess.RunAsync(
                command, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
            Assert.IsFalse(DumpNativeImageIdentity.Matches(path, OSPlatform.Linux, RuntimeInformation.ProcessArchitecture, [1]));
            Assert.IsFalse(DumpNativeImageIdentity.Matches(path, OSPlatform.OSX, RuntimeInformation.ProcessArchitecture, [1]));
            File.Delete(path);
            File.Copy(typeof(Enumerable).Assembly.Location, path);
            using FileStream released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsGreaterThan(0L, released.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string AddInstallation(string root, string name)
    {
        string version = InstallationVersion(root, name);
        Directory.CreateDirectory(version);
        File.Copy(typeof(Enumerable).Assembly.Location, Path.Join(version, Path.GetFileName(typeof(Enumerable).Assembly.Location)));
        return version;
    }

    private static string InstallationVersion(string root, string name) =>
        Path.Join(root, name, "shared", "Microsoft.NETCore.App", Environment.Version.ToString());

    private static string ArchitectureRegistration(string root) =>
        Path.Join(root, RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "install_location_x64",
            Architecture.Arm64 => "install_location_arm64",
            Architecture.X86 => "install_location_x86",
            Architecture.Arm => "install_location_arm",
            _ => throw new AssertFailedException("The test requires a supported Unix host architecture.")
        });
}
