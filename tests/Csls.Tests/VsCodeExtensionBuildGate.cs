namespace Csls.Tests;

/// <summary>
/// Serializes extension package builds that replace the shared Node.js dependency directory.
/// </summary>
internal static class VsCodeExtensionBuildGate
{
    private static readonly SemaphoreSlim s_semaphore = new(1, 1);

    /// <summary>
    /// Runs one extension package build without racing another package install.
    /// </summary>
    /// <typeparam name="T">The package build result type.</typeparam>
    /// <param name="operation">The package build operation.</param>
    /// <returns>The package build result.</returns>
    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await s_semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            s_semaphore.Release();
        }
    }
}
