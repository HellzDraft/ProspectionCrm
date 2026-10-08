using System.Text.Json;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationRuleEvaluatorTests
{
    private static (AutomationJob Job, AutomationRule Rule, AutomationRuntimeSettings Settings) Input()
    {
        var workspace = Guid.NewGuid();
        var rule = new AutomationRule { WorkspaceId = workspace, Name = "Rule", TriggerTypeCode = "manual",
            ActionTypeCode = "create-crm-task", ActionConfigurationJson = """{"title":"Relancer","description":"Texte fixe","dueInDays":3}""" };
        var job = new AutomationJob { WorkspaceId = workspace, AutomationRuleId = rule.Id, TriggerTypeCode = "manual",
            ActionCategoryCode = "general", ContextJson = Context() };
        return (job, rule, new() { WorkspaceId = workspace, IsEnabled = true, OperatingModeCode = "automatic" });
    }
    private static string Context(string? payload = null, Guid? pipeline = null) => AutomationEventContext.Create("event", pipeline,
        payload ?? """{"opportunityId":"00000000-0000-0000-0000-000000000001","source":"manual"}""", out _)!;
    private sealed class CountingPolicy : IAutomationSafetyPolicy
    {
        public int Calls { get; private set; }
        public AutomationSafetyDecision Evaluate(AutomationRuntimeSettings settings, string categoryCode)
        { Calls++; Assert.Equal("general", categoryCode); return new AutomationSafetyPolicy().Evaluate(settings, categoryCode); }
    }

    [Theory]
    [InlineData("workspace", "workspace-mismatch", false)]
    [InlineData("settings-workspace", "workspace-mismatch", false)]
    [InlineData("no-rule", "automation-rule-required", false)]
    [InlineData("wrong-rule", "rule-mismatch", false)]
    [InlineData("disabled", "rule-disabled", true)]
    [InlineData("archived", "rule-archived", true)]
    [InlineData("trigger-mismatch", "trigger-mismatch", false)]
    [InlineData("unknown-trigger", "unsupported-trigger", false)]
    [InlineData("unknown-action", "unsupported-action", false)]
    [InlineData("category", "action-category-mismatch", false)]
    [InlineData("pipeline", "pipeline-mismatch", true)]
    [InlineData("context", "invalid-context", false)]
    [InlineData("condition", "invalid-condition", false)]
    [InlineData("config", "invalid-action-configuration", false)]
    [InlineData("condition-false", "condition-not-matched", true)]
    public void IncoherenceAndNonMatchesNeverReachPolicy(string scenario, string reason, bool valid)
    {
        var (job, rule, settings) = Input();
        switch (scenario)
        {
            case "workspace": rule.WorkspaceId = Guid.NewGuid(); break;
            case "settings-workspace": settings.WorkspaceId = Guid.NewGuid(); break;
            case "no-rule": job.AutomationRuleId = null; break;
            case "wrong-rule": job.AutomationRuleId = Guid.NewGuid(); break;
            case "disabled": rule.Enabled = false; break;
            case "archived": rule.ArchivedAt = DateTimeOffset.UnixEpoch; break;
            case "trigger-mismatch": job.TriggerTypeCode = "other"; break;
            case "unknown-trigger": rule.TriggerTypeCode = job.TriggerTypeCode = "unknown"; break;
            case "unknown-action": rule.ActionTypeCode = "legacy"; break;
            case "category": job.ActionCategoryCode = "email"; break;
            case "pipeline": rule.PipelineId = Guid.NewGuid(); break;
            case "context": job.ContextJson = "{}"; break;
            case "condition": rule.ConditionJson = """{"type":"legacy"}"""; break;
            case "config": rule.ActionConfigurationJson = "{}"; break;
            case "condition-false": rule.ConditionJson = """{"type":"context-equals","property":"source","value":"other"}"""; break;
        }
        var policy = new CountingPolicy(); var result = new AutomationRuleEvaluator(policy).Evaluate(job, rule, settings);
        Assert.Equal(reason, result.ReasonCode); Assert.Equal(valid, result.IsValid); Assert.False(result.IsMatched);
        Assert.Equal(scenario == "condition-false" ? false : (bool?)null, result.ConditionMatched);
        Assert.Null(result.ActionPlan); Assert.Null(result.SafetyDecision); Assert.Equal(0, policy.Calls);
    }

    [Theory]
    [InlineData(false, "automatic", "blocked", "automation-disabled")]
    [InlineData(true, "manual", "manual", "eligible-manual")]
    [InlineData(true, "assist", "approval-required", "eligible-approval-required")]
    [InlineData(true, "automatic", "automatic", "eligible-automatic")]
    [InlineData(true, "invalid", "blocked", "invalid-mode")]
    public void PolicyDecidesAndOnlyUnblockedDiagnosticsContainPlans(bool enabled, string mode, string decision, string reason)
    {
        var (job, rule, settings) = Input(); settings.IsEnabled = enabled; settings.OperatingModeCode = mode;
        var policy = new CountingPolicy(); var evaluator = new AutomationRuleEvaluator(policy);
        var before = JsonSerializer.Serialize(new { job, rule, settings });
        var result = evaluator.Evaluate(job, rule, settings);
        Assert.True(result.IsValid); Assert.True(result.IsMatched); Assert.True(result.ConditionMatched);
        Assert.Equal(decision, result.SafetyDecision!.DecisionCode); Assert.Equal(reason, result.ReasonCode);
        if (decision == "blocked") Assert.Null(result.ActionPlan);
        else
        {
            var plan = Assert.IsType<CreateCrmTaskPlan>(result.ActionPlan);
            Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), plan.OpportunityId);
            Assert.Equal("Relancer", plan.Title); Assert.Equal("Texte fixe", plan.Description); Assert.Equal(3, plan.DueInDays);
            Assert.Equal("create-crm-task", plan.ActionTypeCode); Assert.Equal("general", plan.ActionCategoryCode);
        }
        Assert.Equal(1, policy.Calls); Assert.Equal(result, evaluator.Evaluate(job, rule, settings));
        Assert.Equal(before, JsonSerializer.Serialize(new { job, rule, settings }));
    }

    [Theory]
    [InlineData("{}")] [InlineData("""{"opportunityId":null}""")]
    [InlineData("""{"opportunityId":"invalid"}""")]
    [InlineData("""{"opportunityId":1}""")]
    [InlineData("""{"opportunityId":"00000000-0000-0000-0000-000000000000"}""")]
    public void MissingOrInvalidOpportunityIsInvalidWithoutLookup(string payload)
    {
        var (job, rule, settings) = Input(); job.ContextJson = Context(payload);
        var policy = new CountingPolicy(); var result = new AutomationRuleEvaluator(policy).Evaluate(job, rule, settings);
        Assert.False(result.IsValid); Assert.True(result.ConditionMatched); Assert.Null(result.ActionPlan);
        Assert.Equal("invalid-opportunity-id", result.ReasonCode); Assert.Equal(0, policy.Calls);
    }

    [Theory]
    [InlineData(null)] [InlineData("null")] [InlineData("[]")] [InlineData("{}")]
    [InlineData("""{"eventKey":"key","pipelineId":null,"payload":null}""")]
    [InlineData("""{"eventKey":"key","pipelineId":"wrong","payload":{}}""")]
    [InlineData("""{"eventKey":"key","pipelineId":null,"payload":{},"extra":1}""")]
    [InlineData("""{"eventKey":"key","payload":{}}""")]
    public void InvalidEnvelopesAreRejected(string? json) => Assert.False(AutomationEventContext.TryParse(json, out _));

    [Fact]
    public void ScopePayloadOnlyConditionsAndCurrentDefinitionsControlEvaluation()
    {
        var (job, rule, settings) = Input(); var evaluator = new AutomationRuleEvaluator(new AutomationSafetyPolicy());
        var pipeline = Guid.NewGuid(); job.ContextJson = Context(pipeline: pipeline);
        Assert.NotNull(evaluator.Evaluate(job, rule, settings).ActionPlan); // global
        rule.PipelineId = pipeline;
        Assert.NotNull(evaluator.Evaluate(job, rule, settings).ActionPlan);
        rule.PipelineId = Guid.NewGuid(); Assert.Equal("pipeline-mismatch", evaluator.Evaluate(job, rule, settings).ReasonCode);
        rule.PipelineId = pipeline;
        rule.ConditionJson = """{"type":"context-equals","property":"eventKey","value":"event"}""";
        Assert.Equal("condition-not-matched", evaluator.Evaluate(job, rule, settings).ReasonCode);
        rule.ConditionJson = """{"type":"context-equals","property":"source","value":"manual"}""";
        rule.ActionConfigurationJson = """{"title":"Updated"}""";
        Assert.Equal("Updated", evaluator.Evaluate(job, rule, settings).ActionPlan!.Title);
        Assert.Null(evaluator.Evaluate(job, rule, settings).ActionPlan!.DueInDays);
        rule.Enabled = false; Assert.Null(evaluator.Evaluate(job, rule, settings).ActionPlan);
    }
}
