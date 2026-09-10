namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source-language conditions on real managed runtime breakpoints.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops a source breakpoint only when its C# local condition is true.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task SourceBreakpointHonorsLanguageCondition() =>
        ExerciseBreakpointPredicateAsync(
            useFunctionBreakpoint: false,
            condition: "observedHit == 2",
            hitCondition: null,
            expectedProgress: "2");

    /// <summary>
    /// Stops a function breakpoint only when its C# argument condition is true.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task FunctionBreakpointHonorsLanguageCondition() =>
        ExerciseBreakpointPredicateAsync(
            useFunctionBreakpoint: true,
            condition: "hit == 2",
            hitCondition: null,
            expectedProgress: "2");

    /// <summary>
    /// Advances a hit count only after the source-language condition matches.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task HitConditionCountsOnlyMatchingLanguageConditions() =>
        ExerciseBreakpointPredicateAsync(
            useFunctionBreakpoint: false,
            condition: "observedHit >= 2",
            hitCondition: "2",
            expectedProgress: "3");

    /// <summary>
    /// Exposes a stop when a condition does not produce a Boolean value.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task InvalidConditionStopsForCorrection() =>
        ExerciseBreakpointPredicateAsync(
            useFunctionBreakpoint: false,
            condition: "observedHit + 1",
            hitCondition: null,
            expectedProgress: "1");
}
