using Csls.Control.Contracts;
using Csls.Rpc;
using System.Reflection;

namespace Csls.Tests;

/// <summary>
/// Enforces the boundary between production service contracts and real-behavior tests.
/// </summary>
[TestClass]
public sealed class ProductionInterfaceArchitectureTests
{
    /// <summary>
    /// Rejects test types that replace production services outside malformed transport coverage.
    /// </summary>
    [TestMethod]
    public void TestAssemblyDoesNotImplementProductionServiceInterfaces()
    {
        string[] violations =
        [
            .. typeof(ProductionInterfaceArchitectureTests)
                .Assembly
                .GetTypes()
                .SelectMany(type => type
                    .GetInterfaces()
                    .Where(IsProductionServiceInterface)
                    .Where(serviceInterface => !IsAllowedMalformedTransportFixture(
                        type,
                        serviceInterface))
                    .Select(serviceInterface => $"{type.FullName} -> {serviceInterface.FullName}"))
                .Order(StringComparer.Ordinal),
        ];

        Assert.IsEmpty(
            violations,
            "Tests must exercise production services through real boundaries instead of implementing their interfaces.");
    }

    private static bool IsAllowedMalformedTransportFixture(Type type, Type serviceInterface)
    {
        return type.GetCustomAttribute<MalformedTransportFixtureAttribute>() is not null
            && (serviceInterface == typeof(ILspRpcTarget)
                || serviceInterface == typeof(IControlRpcTarget));
    }

    private static bool IsProductionServiceInterface(Type interfaceType)
    {
        if (interfaceType.Assembly == typeof(ProductionInterfaceArchitectureTests).Assembly)
        {
            return false;
        }

        string? assemblyName = interfaceType.Assembly.GetName().Name;
        return assemblyName?.StartsWith("Csls.", StringComparison.Ordinal) is true;
    }
}
