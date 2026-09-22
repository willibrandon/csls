namespace Csls.TestProcessHost;

/// <summary>
/// Implements a method inherited through a derived interface declaration.
/// </summary>
internal sealed class DebuggerDerivedInterfaceFixture : IDebuggerDerivedInterfaceFixture
{
    int IDebuggerInterfaceFixture<int>.Transform(int value) => value + 400;
}
