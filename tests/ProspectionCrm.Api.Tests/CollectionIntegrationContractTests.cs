using System.Text.Json;
using System.Text.Json.Nodes;
using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class CollectionIntegrationContractTests
{
    private static CollectionCommandEnvelope Command() => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        CollectionIntegrationContracts.Type, new(Guid.NewGuid(), "developer", 100));
    private static CollectionResultEnvelope Result() => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        CollectionIntegrationContracts.Type, new(Guid.NewGuid(), "succeeded", [new("external-1", "Developer")]));

    [Fact]
    public void CommandsAndResultsRoundTripWithoutAnyCrmAuthorityOrCompanyFields()
    {
        var command = CollectionIntegrationContracts.Serialize(Command());
        Assert.Equal(command, CollectionIntegrationContracts.Serialize(CollectionIntegrationContracts.ParseCommand(command)));
        var result = CollectionIntegrationContracts.Serialize(Result());
        Assert.Equal(result, CollectionIntegrationContracts.Serialize(CollectionIntegrationContracts.ParseResult(result)));
        foreach (var forbidden in new[] { "workspace", "pipeline", "savedSearch", "configuration", "callback", "company" })
        { Assert.DoesNotContain(forbidden, command, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(forbidden, result, StringComparison.OrdinalIgnoreCase); }
        Assert.Equal(100, CollectionIntegrationContracts.Criteria("{}", Guid.NewGuid()).MaxItems);
    }

    [Theory]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("payload-unknown")][InlineData("payload-duplicate")]
    [InlineData("missing")][InlineData("uuid")][InlineData("empty-uuid")][InlineData("uuid-type")]
    [InlineData("version-type")][InlineData("version")][InlineData("type")][InlineData("payload-type")]
    [InlineData("large")][InlineData("query-large")][InlineData("query-type")][InlineData("limit-type")][InlineData("limit")]
    public void StrictCommandRejectsAmbiguityWrongTypesAndBounds(string change)
    {
        var json = CollectionIntegrationContracts.Serialize(Command()); var root = JsonNode.Parse(json)!;
        switch (change)
        {
            case "unknown": root["workspaceId"] = Guid.NewGuid(); break;
            case "duplicate": json = json.Replace("\"version\":1", "\"version\":1,\"version\":1"); break;
            case "payload-unknown": root["payload"]!["configuration"] = new JsonObject(); break;
            case "payload-duplicate": json = json.Replace("\"maxItems\":100", "\"maxItems\":100,\"maxItems\":100"); break;
            case "missing": root.AsObject().Remove("messageId"); break;
            case "uuid": root["messageId"] = "invalid"; break;
            case "empty-uuid": root["operationId"] = Guid.Empty; break;
            case "uuid-type": root["correlationId"] = 12; break;
            case "version-type": root["version"] = "1"; break;
            case "version": root["version"] = 2; break;
            case "type": root["type"] = "technical.probe"; break;
            case "payload-type": root["payload"] = new JsonArray(); break;
            case "large": json += new string(' ', 4096); break;
            case "query-large": root["payload"]!["query"] = new string('x', 201); break;
            case "query-type": root["payload"]!["query"] = true; break;
            case "limit-type": root["payload"]!["maxItems"] = 1.5; break;
            case "limit": root["payload"]!["maxItems"] = 101; break;
        }
        if (change is not ("duplicate" or "payload-duplicate" or "large")) json = root.ToJsonString();
        Assert.Throws<IntegrationContractException>(() => CollectionIntegrationContracts.ParseCommand(json));
    }

    [Theory]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("item-duplicate")][InlineData("item-unknown")]
    [InlineData("empty-external")][InlineData("missing-external")][InlineData("title-type")][InlineData("title-large")]
    [InlineData("external-large")][InlineData("location-large")][InlineData("description-large")][InlineData("url-large")]
    [InlineData("unsafe-url")][InlineData("null-option")][InlineData("uuid")][InlineData("request-uuid")]
    [InlineData("items-type")][InlineData("too-many")][InlineData("zero")][InlineData("failed-with-items")]
    [InlineData("repeated-external")][InlineData("large")]
    public void StrictResultRejectsAmbiguityWrongTypesAndBounds(string change)
    {
        var json = CollectionIntegrationContracts.Serialize(Result()); var root = JsonNode.Parse(json)!;
        var item = root["payload"]!["items"]![0]!;
        switch (change)
        {
            case "unknown": root["workspaceId"] = Guid.NewGuid(); break;
            case "duplicate": json = json.Replace("\"version\":1", "\"version\":1,\"version\":1"); break;
            case "item-duplicate": json = json.Replace("\"title\":\"Developer\"", "\"title\":\"Developer\",\"title\":\"Developer\""); break;
            case "item-unknown": item["companyName"] = "Invented"; break;
            case "empty-external": item["externalId"] = " "; break;
            case "missing-external": item.AsObject().Remove("externalId"); break;
            case "title-type": item["title"] = 1; break;
            case "title-large": item["title"] = new string('x', 201); break;
            case "external-large": item["externalId"] = new string('x', 501); break;
            case "location-large": item["location"] = new string('x', 201); break;
            case "description-large": item["description"] = new string('x', 10001); break;
            case "url-large": item["sourceUrl"] = "https://example.invalid/" + new string('x', 2048); break;
            case "unsafe-url": item["sourceUrl"] = "https://user:password@example.invalid/"; break;
            case "null-option": item["location"] = null; break;
            case "uuid": root["commandMessageId"] = "123"; break;
            case "request-uuid": root["payload"]!["requestId"] = Guid.Empty; break;
            case "items-type": root["payload"]!["items"] = new JsonObject(); break;
            case "too-many": root["payload"]!["items"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 101).Select(i => new { externalId = $"id-{i}", title = "Title" })); break;
            case "zero": root["payload"]!["items"] = new JsonArray(); break;
            case "failed-with-items": root["payload"]!["outcome"] = "failed"; break;
            case "repeated-external": root["payload"]!["items"]!.AsArray().Add(item.DeepClone()); break;
            case "large": json += new string(' ', CollectionIntegrationContracts.MaxResultBytes); break;
        }
        if (change is not ("duplicate" or "item-duplicate" or "large")) json = root.ToJsonString();
        Assert.Throws<IntegrationContractException>(() => CollectionIntegrationContracts.ParseResult(json));
    }

    [Theory]
    [InlineData("{\"query\":\"a\",\"query\":\"a\"}")][InlineData("{\"destination\":\"https://example.invalid\"}")]
    [InlineData("{\"maxItems\":0}")][InlineData("{\"maxItems\":\"2\"}")][InlineData("{\"query\":null}")][InlineData("[]")]
    public void CriteriaIsClosedAndBounded(string json) =>
        Assert.Throws<IntegrationContractException>(() => CollectionIntegrationContracts.Criteria(json, Guid.NewGuid()));

    [Fact]
    public void BoundaryValuesAndFailureWithEmptyItemsAreAccepted()
    {
        var result = Result();
        var items = Enumerable.Range(0, 100).Select(i => new CollectionCandidate($"{i}-" + new string('x', 495), new string('t', 200),
            "https://example.invalid/one", new string('l', 200))).ToArray();
        Assert.Equal(100, CollectionIntegrationContracts.ParseResult(CollectionIntegrationContracts.Serialize(result with
            { Payload = result.Payload with { Items = items } })).Payload.Items.Count);
        var failure = result with { Payload = result.Payload with { Outcome = "failed", Items = [] } };
        Assert.Empty(CollectionIntegrationContracts.ParseResult(CollectionIntegrationContracts.Serialize(failure)).Payload.Items);
    }
}
