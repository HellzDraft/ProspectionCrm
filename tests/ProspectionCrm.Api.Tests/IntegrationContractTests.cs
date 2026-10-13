using System.Text.Json.Nodes;
using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationContractTests
{
    private static IntegrationResultEnvelope Result() => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), IntegrationContracts.ProbeType, new(Guid.NewGuid(), "succeeded"));

    [Fact]
    public void CommandsAndResultsRoundTripWithoutWorkspaceOrBusinessDestination()
    {
        var result = Result();
        Assert.Equal(result, IntegrationContracts.ParseResult(IntegrationContracts.Serialize(result)));
        var command = new IntegrationCommandEnvelope(1, result.CommandMessageId, result.OperationId,
            result.CorrelationId, result.Type, new(result.Payload.ProbeId));
        Assert.Equal(command, IntegrationContracts.ParseCommand(IntegrationContracts.Serialize(command)));
        Assert.Throws<IntegrationContractException>(() => IntegrationContracts.ParseResult(IntegrationContracts.Serialize(command)));
        Assert.Throws<IntegrationContractException>(() => IntegrationContracts.ParseCommand(IntegrationContracts.Serialize(result)));
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("version", "\"1\"")]
    [InlineData("version", "1.5")]
    [InlineData("type", "\"business.send\"")]
    [InlineData("messageId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("operationId", "\"invalid\"")]
    [InlineData("correlationId", "null")]
    [InlineData("commandMessageId", "42")]
    [InlineData("payload", "null")]
    [InlineData("payload", "[]")]
    [InlineData("payload", "{\"probeId\":null,\"outcome\":\"succeeded\"}")]
    public void RejectsUnsupportedValuesAndTypes(string field, string value)
    {
        var json = JsonNode.Parse(IntegrationContracts.Serialize(Result()))!;
        json[field] = JsonNode.Parse(value);
        Assert.Throws<IntegrationContractException>(() => IntegrationContracts.ParseResult(json.ToJsonString()));
    }

    [Fact]
    public void RejectsMissingUnknownDuplicateCaseVariantAndOversizeFieldsWithoutEchoingInput()
    {
        var valid = IntegrationContracts.Serialize(Result());
        var invalid = new[] { valid.Replace("\"version\":1,", ""), valid.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            valid.Replace("\"version\"", "\"Version\""), valid.Replace("\"version\":1", "\"version\":1,\"workspaceId\":null"),
            valid.Replace("\"outcome\":\"succeeded\"", "\"outcome\":\"succeeded\",\"destination\":\"arbitrary\""),
            valid.Replace("\"succeeded\"", "\"untrusted-free-text\""), valid.Replace("\"outcome\":", "\"probeId\":null,\"outcome\":"),
            valid + " {}", "null", "[]", "{", valid + new string(' ', IntegrationContracts.MaxEnvelopeBytes) };
        foreach (var json in invalid)
        {
            var error = Assert.Throws<IntegrationContractException>(() => IntegrationContracts.ParseResult(json));
            Assert.Equal("invalid-integration-envelope", error.Message);
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void CommandPayloadIsEquallyStrict()
    {
        var command = new IntegrationCommandEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            IntegrationContracts.ProbeType, new(Guid.NewGuid()));
        var valid = IntegrationContracts.Serialize(command);
        Assert.Throws<IntegrationContractException>(() => IntegrationContracts.ParseCommand(valid.Replace("\"probeId\":", "\"extra\":true,\"probeId\":")));
        Assert.Throws<IntegrationContractException>(() => IntegrationContracts.Serialize(command with { Payload = new(Guid.Empty) }));
        Assert.Throws<IntegrationContractException>(() => IntegrationContracts.Serialize(command with { Version = 2 }));
    }
}
