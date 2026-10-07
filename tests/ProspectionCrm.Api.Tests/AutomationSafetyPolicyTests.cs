using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationSafetyPolicyTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var category in new[] { "general", "email", "application" })
        foreach (var mode in new[] { "manual", "assist", "automatic" })
        {
            yield return [false, category, mode, null!, "blocked", "automation-disabled"];
            var effective = category == "application" ? "manual" : category == "email" && mode == "automatic" ? "assist" : mode;
            var decision = effective == "assist" ? "approval-required" : effective;
            var reason = category == "application" ? "application-manual-cap" : category == "email" && mode == "automatic" ? "email-assist-cap" : "global-mode";
            yield return [true, category, mode, effective, decision, reason];
        }
    }
    [Theory, MemberData(nameof(Matrix))]
    public void CompleteMatrix(bool enabled, string category, string mode, string? effective, string decision, string reason)
    {
        var result = new AutomationSafetyPolicy().Evaluate(new() { IsEnabled = enabled, OperatingModeCode = mode }, category);
        Assert.Equal(new(category, mode, effective, decision, reason), result);
    }
    [Theory]
    [InlineData(true, "unknown")][InlineData(false, "unknown")][InlineData(true, "EMAIL")][InlineData(true, "")]
    public void UnknownCategoryIsExplicitlyBlocked(bool enabled, string category)
    {
        var result = new AutomationSafetyPolicy().Evaluate(new() { IsEnabled = enabled, OperatingModeCode = "automatic" }, category);
        Assert.Equal("blocked", result.DecisionCode); Assert.Equal("unknown-category", result.ReasonCode); Assert.Null(result.EffectiveModeCode);
    }
    [Fact]
    public void CorruptModeFailsClosed()
    {
        var result = new AutomationSafetyPolicy().Evaluate(new() { IsEnabled = true, OperatingModeCode = "UNKNOWN" }, "general");
        Assert.Equal("blocked", result.DecisionCode); Assert.Equal("invalid-mode", result.ReasonCode);
    }
}
