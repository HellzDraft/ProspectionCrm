using System.Text.Json.Nodes;
using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSyncContractTests
{
    internal static InboundEmail Message(string id = "message-1", string? body = "Plain text") => new(id, "thread-1", "sender@example.invalid",
        ["z@example.invalid", "a@example.invalid"], null, null, body, DateTimeOffset.Parse("2026-10-16T08:00:00.1234567+02:00"));
    private static EmailSyncResult Result() => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmailSyncContracts.Type,
        new(Guid.NewGuid(), "succeeded", "cursor-synthetic", [Message()]));

    [Fact]
    public void CanonicalizationIgnoresRecipientMessageOrderTimezoneAndOptionalNulls()
    {
        var original = Result() with { Payload = Result().Payload with { Messages = [Message("b"), Message("a")] } };
        var json = EmailSyncContracts.Serialize(original);
        var node = JsonNode.Parse(json)!; var messages = node["payload"]!["messages"]!.AsArray();
        var first = messages[0]!.DeepClone(); var second = messages[1]!.DeepClone(); messages.Clear(); messages.Add(second); messages.Add(first);
        foreach (var message in messages)
        {
            message!["toAddresses"] = new JsonArray("z@example.invalid", "a@example.invalid", "a@example.invalid");
            message.AsObject().Remove("ccAddresses"); message.AsObject().Remove("subject");
            message["occurredAt"] = "2026-10-16T08:00:00.1234567+02:00";
        }
        Assert.Equal(json, EmailSyncContracts.Serialize(EmailSyncContracts.ParseResult(node.ToJsonString())));
        Assert.Equal(0, EmailSyncContracts.ParseResult(json).Payload.Messages[0].OccurredAt.Ticks % 10);
    }

    [Theory]
    [InlineData("root-unknown")][InlineData("payload-unknown")][InlineData("message-unknown")][InlineData("version")]
    [InlineData("type")][InlineData("empty-id")][InlineData("empty-account")][InlineData("external-id")][InlineData("thread-id")]
    [InlineData("from")][InlineData("recipient-empty")][InlineData("recipient-many")][InlineData("combined-recipients")]
    [InlineData("subject")][InlineData("body")][InlineData("cursor")][InlineData("timestamp")][InlineData("no-timezone")]
    [InlineData("messages-many")][InlineData("messages-duplicate")][InlineData("failed-messages")][InlineData("failed-cursor")]
    [InlineData("outcome")][InlineData("nul")][InlineData("missing")][InlineData("wrong-case")]
    public void RejectsInvalidResultWithoutEchoingInput(string change)
    {
        var root = JsonNode.Parse(EmailSyncContracts.Serialize(Result()))!; var payload = root["payload"]!; var item = payload["messages"]![0]!;
        switch (change)
        {
            case "root-unknown": root["workspaceId"] = "sensitive-sentinel"; break;
            case "payload-unknown": payload["refreshToken"] = "sensitive-sentinel"; break;
            case "message-unknown": item["html"] = "sensitive-sentinel"; break;
            case "version": root["version"] = 2; break;
            case "type": root["type"] = "email.send"; break;
            case "empty-id": root["messageId"] = Guid.Empty.ToString(); break;
            case "empty-account": payload["accountRef"] = Guid.Empty.ToString(); break;
            case "external-id": item["externalMessageId"] = " "; break;
            case "thread-id": item["externalThreadId"] = new string('x', 501); break;
            case "from": item["fromAddress"] = "Display <sender@example.invalid>"; break;
            case "recipient-empty": item["toAddresses"] = new JsonArray(""); break;
            case "recipient-many": item["toAddresses"] = new JsonArray(Enumerable.Repeat("x@example.invalid", 101).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()); break;
            case "combined-recipients": item["ccAddresses"] = new JsonArray(Enumerable.Repeat("x@example.invalid", 99).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()); break;
            case "subject": item["subject"] = new string('x', 1001); break;
            case "body": item["bodyText"] = new string('x', EmailSyncContracts.MaxBodyLength + 1); break;
            case "cursor": payload["nextCursor"] = new string('x', 2049); break;
            case "timestamp": item["occurredAt"] = "yesterday"; break;
            case "no-timezone": item["occurredAt"] = "2026-10-16T08:00:00"; break;
            case "messages-many": payload["messages"] = new JsonArray(Enumerable.Range(0, 101).Select(_ => item.DeepClone()).ToArray()); break;
            case "messages-duplicate": payload["messages"]!.AsArray().Add(item.DeepClone()); break;
            case "failed-messages": payload["outcome"] = "failed"; break;
            case "failed-cursor": payload["outcome"] = "failed"; payload["messages"] = new JsonArray(); break;
            case "outcome": payload["outcome"] = "sensitive-sentinel"; break;
            case "nul": item["bodyText"] = "\0"; break;
            case "missing": item.AsObject().Remove("fromAddress"); break;
            case "wrong-case": item["FromAddress"] = item["fromAddress"]!.DeepClone(); item.AsObject().Remove("fromAddress"); break;
        }
        var error = Assert.Throws<IntegrationContractException>(() => EmailSyncContracts.ParseResult(root.ToJsonString()));
        Assert.Equal("invalid-integration-envelope", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void DuplicatePropertiesAndGlobalBytesAreRejectedAndEmptySuccessIsValid()
    {
        var json = EmailSyncContracts.Serialize(Result());
        Assert.Throws<IntegrationContractException>(() => EmailSyncContracts.ParseResult(json.Replace("\"version\":1", "\"version\":1,\"version\":1")));
        var oversized = Result() with { Payload = Result().Payload with { Messages = Enumerable.Range(0, 20).Select(x => Message(x.ToString(), new string('x', 32000))).ToArray() } };
        Assert.Throws<IntegrationContractException>(() => EmailSyncContracts.Serialize(oversized));
        foreach (var outcome in new[] { "succeeded", "failed" })
        {
            var result = Result() with { Payload = Result().Payload with { Outcome = outcome, Messages = [], NextCursor = null } };
            Assert.Empty(EmailSyncContracts.ParseResult(EmailSyncContracts.Serialize(result)).Payload.Messages);
        }
    }

    [Theory]
    [InlineData(0)][InlineData(101)]
    public void CommandLimitIsStrict(int max) => Assert.Throws<IntegrationContractException>(() => EmailSyncContracts.Serialize(
        new EmailSyncCommand(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmailSyncContracts.Type, new(Guid.NewGuid(), null, max))));

    [Fact]
    public void CommandHasOnlyOpaqueAccountCursorAndLimit()
    {
        var command = new EmailSyncCommand(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmailSyncContracts.Type, new(Guid.NewGuid(), "synthetic", 100));
        var json = EmailSyncContracts.Serialize(command); Assert.Equal(command, EmailSyncContracts.ParseCommand(json));
        foreach (var field in new[] { "workspaceId", "accessToken", "callbackUrl", "credential", "externalAccountId" })
        {
            var node = JsonNode.Parse(json)!; node["payload"]![field] = "sensitive-sentinel";
            Assert.Throws<IntegrationContractException>(() => EmailSyncContracts.ParseCommand(node.ToJsonString()));
        }
    }
}
