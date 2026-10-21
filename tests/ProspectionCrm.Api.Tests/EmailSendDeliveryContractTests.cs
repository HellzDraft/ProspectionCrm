using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendDeliveryContractTests
{
    private static EmailSendResult Result() { var id=Guid.NewGuid(); return new(1,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"email.send",id,$"email-send-{id:D}","accepted","synthetic","provider-proof",DateTimeOffset.UtcNow); }
    [Fact] public void RoundTrip() { var r=Result(); Assert.Equal(r,EmailSendContracts.ParseResult(EmailSendContracts.Serialize(r))); }
    [Theory]
    [InlineData("unknown-field")][InlineData("duplicate")][InlineData("version")][InlineData("key")][InlineData("provider")][InlineData("time")][InlineData("oversize")]
    public void StrictResultRejectsInvalidEvidence(string kind)
    {
        var r=Result(); var json=EmailSendContracts.Serialize(r);
        json=kind switch { "unknown-field"=>json.Insert(1,"\"credential\":\"not-allowed\","), "duplicate"=>json.Insert(1,"\"version\":1,"),
            "version"=>json.Replace("\"version\":1","\"version\":2"), "key"=>json.Replace(r.IdempotencyKey,"different"),
            "provider"=>json.Replace("\"provider-proof\"","null"), "time"=>json.Replace("\"acceptedAt\":\""+r.AcceptedAt!.Value.ToString("O")+"\"","\"acceptedAt\":null"),
            _=>new string(' ',5000)+json };
        if(kind=="time") json=System.Text.RegularExpressions.Regex.Replace(json,"\"acceptedAt\":\"[^\"]+\"","\"acceptedAt\":null");
        Assert.Throws<IntegrationContractException>(()=>EmailSendContracts.ParseResult(json));
    }
}
