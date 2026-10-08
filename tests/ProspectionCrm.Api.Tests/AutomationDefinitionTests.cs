using System.Globalization;
using System.Text.Json;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationDefinitionTests
{
    [Theory]
    [InlineData("manual", true)]
    [InlineData("Manual", false)]
    [InlineData("manual ", false)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void TriggerCatalogueIsExact(string? code, bool valid) => Assert.Equal(valid, AutomationJobTriggers.IsValid(code));

    [Fact]
    public void ActionCatalogueDefinesCategoryConfigurationAndPlan()
    {
        var action = Assert.IsType<AutomationActionDefinition>(AutomationActionCatalog.Find("create-crm-task"));
        Assert.Equal("general", action.CategoryCode); Assert.Equal(typeof(CreateCrmTaskPlan), action.PlanType);
        foreach (var code in new[] { "unknown", "Create-crm-task", "email", "application", null }) Assert.Null(AutomationActionCatalog.Find(code));
        var config = action.ParseConfiguration("""{"title":"Relancer","description":"Texte fixe","dueInDays":3}""");
        Assert.Equal(new CreateCrmTaskConfiguration("Relancer", "Texte fixe", 3), config);
        Assert.Equal(new CreateCrmTaskConfiguration("Title", null, null), action.ParseConfiguration("""{"title":"Title"}"""));
        foreach (var days in new[] { 0, 365 }) Assert.Equal(days, action.ParseConfiguration($$"""{"title":"T","dueInDays":{{days}}}""")!.DueInDays);
        Assert.NotNull(action.ParseConfiguration(JsonSerializer.Serialize(new { title = new string('x', 200), description = new string('x', 10000) })));
    }

    [Theory]
    [InlineData(null)] [InlineData("{}")] [InlineData("null")] [InlineData("[]")]
    [InlineData("{bad}")]
    [InlineData("""{"title":""}""")] [InlineData("""{"title":"  "}""")]
    [InlineData("""{"title":null}""")] [InlineData("""{"title":1}""")]
    [InlineData("""{"title":"T","dueInDays":-1}""")]
    [InlineData("""{"title":"T","dueInDays":366}""")]
    [InlineData("""{"title":"T","dueInDays":1.5}""")]
    [InlineData("""{"title":"T","dueInDays":"1"}""")]
    [InlineData("""{"title":"T","dueInDays":null}""")]
    [InlineData("""{"title":"T","description":{}}""")]
    [InlineData("""{"title":"T","extra":true}""")]
    [InlineData("""{"title":"T","title":"Other"}""")]
    [InlineData("""{"title":"{{company.name}}"}""")]
    [InlineData("""{"title":"T","description":"{{script}}"}""")]
    [InlineData("""{"title":"\u0000"}""")]
    [InlineData("""{"title":"\ud800"}""")]
    public void InvalidConfigurationIsRejected(string? json)
        => Assert.Null(AutomationActionCatalog.Find("create-crm-task")!.ParseConfiguration(json));

    [Fact]
    public void ConfigurationLengthsAreBounded()
    {
        var parse = AutomationActionCatalog.Find("create-crm-task")!.ParseConfiguration;
        Assert.Null(parse(JsonSerializer.Serialize(new { title = new string('x', 201) })));
        Assert.Null(parse(JsonSerializer.Serialize(new { title = "T", description = new string('x', 10001) })));
    }

    [Theory]
    [InlineData(null)] [InlineData("{}")] [InlineData("""{"type":"always"}""")]
    public void AlwaysConditionsMatch(string? json)
    {
        Assert.True(AutomationConditions.TryParse(json, out var condition));
        Assert.True(condition.Matches(JsonSerializer.SerializeToElement(new { })));
    }

    [Theory]
    [InlineData("\"manual\"", "\"manual\"", true)]
    [InlineData("\"manual\"", "\"Manual\"", false)]
    [InlineData("true", "true", true)] [InlineData("true", "false", false)]
    [InlineData("null", "null", true)] [InlineData("null", "\"null\"", false)]
    [InlineData("1", "1.0", true)] [InlineData("1", "1e0", true)]
    [InlineData("-0", "0.0", true)] [InlineData("1", "\"1\"", false)]
    [InlineData("1", "2", false)]
    [InlineData("9007199254740993", "9007199254740992", false)]
    [InlineData("0.123456789012345678901234567891", "0.123456789012345678901234567890", false)]
    public void ContextEqualsUsesExactJsonTypesAndValues(string expected, string actual, bool matches)
    {
        Assert.True(AutomationConditions.TryParse($$"""{"type":"context-equals","property":"source","value":{{expected}}}""", out var condition));
        using var payload = JsonDocument.Parse($$"""{"source":{{actual}}}""");
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(matches, condition.Matches(payload.RootElement)); }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.False(condition.Matches(JsonSerializer.SerializeToElement(new { Source = actual })));
        Assert.False(condition.Matches(JsonSerializer.SerializeToElement(new { })));
    }

    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("")]
    [InlineData("""{"type":"unknown"}""")]
    [InlineData("""{"type":"always","extra":0}""")]
    [InlineData("""{"type":"context-equals","property":"x"}""")]
    [InlineData("""{"type":"context-equals","property":"x","value":{}}""")]
    [InlineData("""{"type":"context-equals","property":"x","value":[]}""")]
    [InlineData("""{"type":"context-equals","property":"x.y","value":1}""")]
    [InlineData("""{"type":"context-equals","property":"x[0]","value":1}""")]
    [InlineData("""{"type":"context-equals","property":" ","value":1}""")]
    [InlineData("""{"type":"context-equals","property":"x","value":1,"caseInsensitive":true}""")]
    [InlineData("""{"type":"always","type":"always"}""")]
    [InlineData("""{"type":"context-equals","property":"x","value":1e999999999}""")]
    public void InvalidConditionsAreRejected(string json) => Assert.False(AutomationConditions.TryParse(json, out _));

    [Fact]
    public void EnvelopeIsCanonicalBoundedAndKeepsReservedMetadataSeparate()
    {
        var pipeline = Guid.NewGuid();
        var first = AutomationEventContext.Create("key", pipeline, """{"source":"manual","eventKey":"spoof","pipelineId":null,"a":1e1}""", out var error);
        Assert.Null(error);
        Assert.Equal(first, AutomationEventContext.Create("key", pipeline, """{"a":10.0,"pipelineId":null,"eventKey":"spoof","source":"manual"}""", out _));
        Assert.True(AutomationEventContext.TryParse(first, out var parsed)); Assert.Equal("key", parsed.EventKey); Assert.Equal(pipeline, parsed.PipelineId);
        Assert.Equal("spoof", parsed.Payload.GetProperty("eventKey").GetString());
        Assert.NotNull(AutomationEventContext.Create(new string('x', 100), null, null, out _));
        Assert.True(AutomationEventContext.TriggerKey(new string('x', 100), Guid.NewGuid()).Length <= AutomationJobLimits.TriggerKeyLength);
        Assert.Null(AutomationEventContext.Create("key", null, JsonSerializer.Serialize(new { value = new string('é', 8192) }), out _));
        Assert.Null(AutomationEventContext.Create("key", null, """{"n":1e16000,"m":1e16000}""", out _));
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("key\n")]
    [InlineData("key\0")] [InlineData(null)]
    public void InvalidEventKeysAreRejected(string? key) => Assert.Null(AutomationEventContext.Create(key, null, "{}", out _));

    [Theory]
    [InlineData("null")] [InlineData("[]")]
    [InlineData("""{"x":1,"x":2}""")]
    [InlineData("""{"x":"\u0000"}""")]
    [InlineData("""{"x":1e20000}""")]
    [InlineData("""{"x":1e-20000}""")]
    public void UnsafePayloadIsRejectedBeforePostgreSql(string payload) => Assert.Null(AutomationEventContext.Create("key", null, payload, out _));
}
