namespace Csls.TestProcessHost;

/// <summary>
/// Implements the debugger interface through an explicit method.
/// </summary>
internal sealed class DebuggerExplicitInterfaceFixture : IDebuggerInterfaceFixture<int>
{
    int IDebuggerInterfaceFixture<int>.Transform(int value) => value + 200;
}
