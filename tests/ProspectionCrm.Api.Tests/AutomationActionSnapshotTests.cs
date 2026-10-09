using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationActionSnapshotTests
{
    private static AutomationRule Rule() => new() { Name = "Rule", TriggerTypeCode = "manual", ActionTypeCode = "create-crm-task",
        ConditionJson = """{"value":1.0,"property":"score","type":"context-equals"}""",
        ActionConfigurationJson = """{"title":"Task","dueInDays":3}""" };
    [Fact]
    public void FingerprintCanonicalizesJsonAndIgnoresDisplayAndLifecycleMetadata()
    {
        var rule = Rule(); var hash = AutomationActionSnapshot.Fingerprint(rule);
        rule.Name = "Renamed"; rule.Description = "Changed"; rule.Enabled = false;
        rule.ArchivedAt = DateTimeOffset.UtcNow; rule.UpdatedAt = DateTimeOffset.UtcNow;
        rule.ConditionJson = """{ "type":"context-equals", "property":"score", "value":1e0 }""";
        rule.ActionConfigurationJson = """{"dueInDays":3,"title":"Task"}""";
        Assert.Equal(hash, AutomationActionSnapshot.Fingerprint(rule)); Assert.Matches("^[0-9a-f]{64}$", hash!);
    }
    [Theory]
    [InlineData("trigger")][InlineData("pipeline")][InlineData("condition")][InlineData("action")][InlineData("configuration")]
    public void EveryBusinessDefinitionFieldChangesFingerprint(string field)
    {
        var rule = Rule(); var before = AutomationActionSnapshot.Fingerprint(rule);
        switch (field)
        {
            case "trigger": rule.TriggerTypeCode = "changed"; break;
            case "pipeline": rule.PipelineId = Guid.NewGuid(); break;
            case "condition": rule.ConditionJson = null; break;
            case "action": rule.ActionTypeCode = "changed"; break;
            default: rule.ActionConfigurationJson = "{}"; break;
        }
        Assert.NotEqual(before, AutomationActionSnapshot.Fingerprint(rule));
    }
    [Theory]
    [InlineData(null)][InlineData(0)][InlineData(365)]
    public void SnapshotRoundTripsDeterministically(int? days)
    {
        var plan = new CreateCrmTaskPlan(Guid.NewGuid(), "Literal é", null, days);
        var json = AutomationActionSnapshot.Serialize(plan);
        Assert.Equal(plan, AutomationActionSnapshot.Parse(json));
        Assert.Equal(json, AutomationActionSnapshot.Serialize(AutomationActionSnapshot.Parse(json)!));
        Assert.DoesNotContain("actionTypeCode", json);
    }
    [Theory]
    [InlineData("[]")][InlineData("{}")][InlineData("null")]
    [InlineData("{\"opportunityId\":\"00000000-0000-0000-0000-000000000000\",\"title\":\"Task\"}")]
    [InlineData("{\"opportunityId\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"{{name}}\"}")]
    [InlineData("{\"opportunityId\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"Task\",\"dueInDays\":366}")]
    [InlineData("{\"opportunityId\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"Task\",\"dueInDays\":1.5}")]
    [InlineData("{\"opportunityId\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"Task\",\"script\":\"secret\"}")]
    [InlineData("{\"opportunityId\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"Task\",\"title\":\"Other\"}")]
    public void InvalidSnapshotsAreRejected(string json) => Assert.Null(AutomationActionSnapshot.Parse(json));
}
