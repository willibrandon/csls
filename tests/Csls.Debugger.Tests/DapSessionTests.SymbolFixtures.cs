namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP sequencing and target ownership through production sessions.
/// </summary>
public sealed partial class DapSessionTests
{
    private static DebuggerSymbolFixtures? s_symbolFixtures;

    private static DebuggerSymbolFixtures SymbolFixtures =>
        Volatile.Read(ref s_symbolFixtures) ??
        throw new InvalidOperationException("Debugger symbol fixtures are not initialized.");

    /// <summary>
    /// Builds reusable real symbol fixtures before the debugger test methods start.
    /// </summary>
    /// <param name="testContext">The class initialization context.</param>
    /// <returns>A task that completes when every platform fixture is ready.</returns>
    [ClassInitialize]
    public static async Task InitializeSymbolFixturesAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        s_symbolFixtures = await DebuggerSymbolFixtures.CreateAsync(
            testContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the shared symbol servers and their isolated build directory.
    /// </summary>
    /// <returns>A task that completes when the shared fixtures are released.</returns>
    [ClassCleanup]
    public static async Task CleanupSymbolFixturesAsync()
    {
        DebuggerSymbolFixtures? fixtures = Interlocked.Exchange(ref s_symbolFixtures, null);
        if (fixtures is not null)
        {
            await fixtures.DisposeAsync().ConfigureAwait(false);
        }
    }
}
