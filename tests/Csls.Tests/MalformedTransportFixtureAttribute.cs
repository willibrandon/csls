namespace Csls.Tests;

/// <summary>
/// Marks a test type that implements a transport target solely to exercise malformed RPC input.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class MalformedTransportFixtureAttribute : Attribute;
