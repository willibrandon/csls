namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP sequencing and target ownership through production sessions.
/// </summary>
public sealed partial class DapSessionTests
{
    private static DebuggerLanguageFixtures? s_languageFixtures;
    private static DebuggerSymbolFixtures? s_symbolFixtures;

    private static DebuggerLanguageFixtures LanguageFixtures =>
        Volatile.Read(ref s_languageFixtures) ??
        throw new InvalidOperationException("Debugger language fixtures are not initialized.");

    private static DebuggerSymbolFixtures SymbolFixtures =>
        Volatile.Read(ref s_symbolFixtures) ??
        throw new InvalidOperationException("Debugger symbol fixtures are not initialized.");

    /// <summary>
    /// Builds reusable real language and symbol fixtures before the debugger tests start.
    /// </summary>
    /// <param name="testContext">The class initialization context.</param>
    /// <returns>A task that completes when every platform fixture is ready.</returns>
    [ClassInitialize]
    public static async Task InitializeFixturesAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        s_symbolFixtures = await DebuggerSymbolFixtures.CreateAsync(
            testContext.CancellationToken).ConfigureAwait(false);
        try
        {
            s_languageFixtures = await DebuggerLanguageFixtures.CreateAsync(
                testContext.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DebuggerSymbolFixtures? fixtures =
                Interlocked.Exchange(ref s_symbolFixtures, null);
            if (fixtures is not null)
            {
                await fixtures.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Releases the shared fixture programs, servers, and isolated build directories.
    /// </summary>
    /// <returns>A task that completes when the shared fixtures are released.</returns>
    [ClassCleanup]
    public static async Task CleanupFixturesAsync()
    {
        DebuggerLanguageFixtures? languageFixtures =
            Interlocked.Exchange(ref s_languageFixtures, null);
        DebuggerSymbolFixtures? fixtures = Interlocked.Exchange(ref s_symbolFixtures, null);
        try
        {
            if (languageFixtures is not null)
            {
                await languageFixtures.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (fixtures is not null)
            {
                await fixtures.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
