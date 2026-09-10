using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native file-owner diagnostics against actual Windows file handles.
/// </summary>
[TestClass]
public sealed class DebuggerFileLockDiagnosticsTests
{
    /// <summary>
    /// Observes the real process holding a file and its release for ordinary and Unicode names.
    /// </summary>
    /// <param name="name">The filename passed through the native UTF-16 path boundary.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    [DataRow("locked.dll")]
    [DataRow("锁定-Δ.dll")]
    public void FileOwnersTrackAcquisitionAndRelease(string name)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-file-owners-");
        string path = Path.Join(directory.FullName, name);
        try
        {
            File.WriteAllText(path, "Owned debugger diagnostic fixture.");
            Assert.DoesNotContain(Environment.ProcessId, DebuggerFileLockDiagnostics.FindOwners(path));
            using (FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsGreaterThan(0L, file.Length);
                Assert.Contains(Environment.ProcessId, DebuggerFileLockDiagnostics.FindOwners(path));
            }
            Assert.DoesNotContain(Environment.ProcessId, DebuggerFileLockDiagnostics.FindOwners(path));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Rejects malformed paths before registering native resources.
    /// </summary>
    /// <param name="path">The malformed path discriminator.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("relative.dll")]
    [DataRow("null-character")]
    public void FileOwnerQueryRejectsMalformedPaths(string path)
    {
        string input = path == "null-character" ? Path.Join(Path.GetTempPath(), "invalid\0.dll") : path;
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => DebuggerFileLockDiagnostics.FindOwners(input));
        Assert.AreEqual("path", error.ParamName);
    }

    /// <summary>
    /// Rejects a missing filename before acquiring a native session.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void FileOwnerQueryRejectsNull()
    {
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(() => DebuggerFileLockDiagnostics.FindOwners(null));
        Assert.AreEqual("path", error.ParamName);
    }

    /// <summary>
    /// Pins the native structure sizes and offsets declared by the Windows headers.
    /// </summary>
    [TestMethod]
    public void RestartManagerLayoutsMatchNativeHeaders()
    {
        Assert.AreEqual(12, Marshal.SizeOf<DebuggerRestartManagerProcess>());
        Assert.AreEqual((nint)4, Marshal.OffsetOf<DebuggerRestartManagerProcess>(nameof(DebuggerRestartManagerProcess._startTime)));
        Assert.AreEqual(668, Marshal.SizeOf<DebuggerRestartManagerProcessInfo>());
        Assert.AreEqual((nint)12, Marshal.OffsetOf<DebuggerRestartManagerProcessInfo>(nameof(DebuggerRestartManagerProcessInfo._applicationName)));
        Assert.AreEqual((nint)524, Marshal.OffsetOf<DebuggerRestartManagerProcessInfo>(nameof(DebuggerRestartManagerProcessInfo._serviceName)));
        Assert.AreEqual((nint)652, Marshal.OffsetOf<DebuggerRestartManagerProcessInfo>(nameof(DebuggerRestartManagerProcessInfo._applicationType)));
        Assert.AreEqual((nint)656, Marshal.OffsetOf<DebuggerRestartManagerProcessInfo>(nameof(DebuggerRestartManagerProcessInfo._status)));
        Assert.AreEqual((nint)660, Marshal.OffsetOf<DebuggerRestartManagerProcessInfo>(nameof(DebuggerRestartManagerProcessInfo._sessionId)));
        Assert.AreEqual((nint)664, Marshal.OffsetOf<DebuggerRestartManagerProcessInfo>(nameof(DebuggerRestartManagerProcessInfo._restartable)));
    }
}
