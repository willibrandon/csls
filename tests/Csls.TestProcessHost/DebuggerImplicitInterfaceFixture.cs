namespace Csls.TestProcessHost;

/// <summary>
/// Implements the debugger interface through an ordinary public method.
/// </summary>
internal sealed class DebuggerImplicitInterfaceFixture : IDebuggerInterfaceFixture<int>
{
    /// <summary>
    /// Transforms one integer through implicit interface dispatch.
    /// </summary>
    /// <param name="value">The input integer.</param>
    /// <returns>The transformed integer.</returns>
    public int Transform(int value) => value + 100;
}
