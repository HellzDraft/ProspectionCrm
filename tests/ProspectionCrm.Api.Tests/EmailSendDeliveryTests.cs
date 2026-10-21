using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Email;
using ProspectionCrm.Api.Services.Integrations;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendDeliveryTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    // No socket exists: the shared HTTP/HMAC transport terminates in this in-memory provider.
    private sealed class Provider(SecurityTestHost host) : HttpMessageHandler, IEmailSendTransport
    {
        public bool IsAvailable => true;
        public int Calls;
        public string Mode = "ack";
        public EmailSendCommand? Command;
        public readonly Dictionary<string, string> Accepted = [];
        public Func<Task>? DuringSend;
        public Task<EmailSendTransportOutcome> DispatchAsync(EmailSendCommand command, CancellationToken token) => Dispatch(command, token);
        private async Task<EmailSendTransportOutcome> Dispatch(EmailSendCommand command, CancellationToken token)
        {
            if (Mode == "not-sent") { Command = command; return EmailSendTransportOutcome.NotSent; }
            using var client = new HttpClient(this, false);
            var outcome = await new IntegrationTransport(client, host.Clock).SendAsync(host.Integration,
                Encoding.UTF8.GetBytes(EmailSendContracts.Serialize(command)), token);
            return outcome == IntegrationTransportOutcome.Acknowledged ? EmailSendTransportOutcome.Acknowledged : EmailSendTransportOutcome.Unknown;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            var timestamp = request.Headers.GetValues(IntegrationSignature.TimeHeader).Single();
            Assert.Equal(IntegrationSignature.Sign(Convert.FromBase64String(host.Integration.OutgoingKey.SecretBase64),
                IntegrationSignature.OutgoingAudience, host.Integration.OutgoingKey.Id, timestamp, "POST", request.RequestUri!.PathAndQuery, bytes),
                request.Headers.GetValues(IntegrationSignature.SignatureHeader).Single());
            Command = EmailSendContracts.ParseCommand(Encoding.UTF8.GetString(bytes));
            if (!Accepted.ContainsKey(Command.IdempotencyKey)) Accepted[Command.IdempotencyKey] = $"synthetic-provider-{Guid.NewGuid():N}";
            if (DuringSend is not null) await DuringSend();
            if (Mode == "timeout") throw new TaskCanceledException();
            if (Mode == "disconnect") throw new IOException();
            return new(Mode == "http-failed" ? HttpStatusCode.BadRequest : HttpStatusCode.Accepted);
        }
        public EmailSendResult Proof(string outcome = "accepted") => new(1, Guid.NewGuid(), Command!.OperationId,
            Command.CorrelationId, Command.MessageId, "email.send", Command.DeliveryId, Command.IdempotencyKey, outcome,
            outcome == "accepted" ? "synthetic" : null, outcome == "accepted" ? Accepted[Command.IdempotencyKey] : null,
            outcome == "accepted" ? DateTimeOffset.UtcNow : null);
    }
    private sealed record Scenario(SecurityTestHost Host, HttpClient Client, Provider Provider, EmailSendPlanDto Plan, EmailAccountDto Account) : IDisposable
    { public Guid Workspace => Host.Operator.WorkspaceId; public void Dispose() { Client.Dispose(); Host.Dispose(); Provider.Dispose(); } }
    private async Task<Scenario> Prepare(bool approve = true, bool transport = true)
    {
        var host = await SecurityTestHost.CreateAsync(database); var provider = new Provider(host);
        if (transport) host.EmailSendTransport = provider;
        var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); db.AutomationRuntimeSettings.Add(new() { WorkspaceId = host.Operator.WorkspaceId, IsEnabled = true, OperatingModeCode = "automatic" });
        await db.SaveChangesAsync();
        var account = await (await client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(host.Integration.ConnectionId,
            "synthetic", "send-test", "sender@example.invalid"))).Content.ReadFromJsonAsync<EmailAccountDto>();
        var plan = await (await client.PostAsJsonAsync("/api/email-send-plans", new EmailSendContent(account!.Id,
            ["recipient@example.invalid"], ["copy@example.invalid"], "Approved", "Approved body"))).Content.ReadFromJsonAsync<EmailSendPlanDto>();
        plan = await (await client.PostAsJsonAsync($"/api/email-send-plans/{plan!.Id}/submit", new { revision = plan.Revision })).Content.ReadFromJsonAsync<EmailSendPlanDto>();
        if (approve) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/automation-action-requests/{plan!.AutomationActionRequestId}/approve", new { })).StatusCode);
        return new(host, client, provider, plan!, account);
    }
    private static string Route(Guid id, string verb) => $"/api/email-send-deliveries/{id}/{verb}";
    private static Task<HttpResponseMessage> Create(Scenario s) => s.Client.PostAsync($"/api/email-send-deliveries/from-plan/{s.Plan.Id}", null);
    private static async Task<EmailSendDelivery> Delivery(Scenario s)
    { var response = await Create(s); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<EmailSendDelivery>())!; }
    private static async Task<EmailSendDelivery> Read(Scenario s, Guid id) => (await s.Client.GetFromJsonAsync<EmailSendDelivery>($"/api/email-send-deliveries/{id}"))!;
    private static Task<HttpResponseMessage> Receive(Scenario s, EmailSendResult proof, string key = "in-1") =>
        s.Client.SendAsync(s.Host.Signed(Encoding.UTF8.GetBytes(EmailSendContracts.Serialize(proof)), key));
    [Fact]
    public async Task UniqueIntentAckIsNotSentAndProviderEvidenceCommitsAtomicallyAndReplays()
    {
        using var s = await Prepare(); var d = await Delivery(s);
        var duplicate = await Delivery(s); Assert.Equal(d.Id, duplicate.Id); Assert.Equal(d.IdempotencyKey, duplicate.IdempotencyKey);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync(Route(d.Id,"dispatch"), null)).StatusCode);
        Assert.Equal("awaiting-result", (await Read(s,d.Id)).StatusCode);
        await using var db = database.Db(); Assert.Empty(await db.EmailMessages.Where(x=>x.WorkspaceId==s.Workspace).ToListAsync());
        var proof=s.Provider.Proof(); Assert.Equal(HttpStatusCode.Accepted,(await Receive(s,proof)).StatusCode);
        Assert.Equal("result-received",(await Read(s,d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,(await Receive(s,proof)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        var done=await Read(s,d.Id); Assert.Equal("sent",done.StatusCode); Assert.Equal(proof.ExternalMessageId,done.ExternalMessageId);
        var message=Assert.Single(await db.EmailMessages.Where(x=>x.WorkspaceId==s.Workspace).ToListAsync());
        Assert.Equal("outbound",message.DirectionCode); Assert.Equal(s.Plan.Content.BodyText,message.BodyText);
        Assert.Equal(s.Plan.Content.Subject,message.Subject); Assert.Equal(s.Account.Id,message.EmailAccountId);
        Assert.Equal(proof.ExternalMessageId,message.ExternalMessageId);
        Assert.Equal("succeeded",(await db.IntegrationOperations.FindAsync(d.IntegrationOperationId))!.StatusCode);
        Assert.NotNull((await db.IntegrationReceipts.FindAsync(proof.MessageId))!.ProcessedAt);
        Assert.Equal("approved",(await db.EmailSendPlans.FindAsync(s.Plan.Id))!.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode); Assert.Equal(1,s.Provider.Calls);
    }
    [Theory]
    [InlineData("timeout")][InlineData("disconnect")][InlineData("http-failed")]
    public async Task AmbiguousDispatchNeverRetriesAndLateEvidenceReconciles(string mode)
    {
        using var s=await Prepare(); s.Provider.Mode=mode; var d=await Delivery(s);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        Assert.Equal("send-unknown",(await Read(s,d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        Assert.Equal("send-unknown",(await Read(s,d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,(await Receive(s,s.Provider.Proof())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        Assert.Equal("sent",(await Read(s,d.Id)).StatusCode); Assert.Equal(1,s.Provider.Calls);
    }
    [Theory]
    [InlineData("failed")][InlineData("unknown")]
    public async Task InconclusiveCallbackRetainedThenLaterAuthenticatedAcceptance(string outcome)
    {
        using var s=await Prepare(); var d=await Delivery(s); await s.Client.PostAsync(Route(d.Id,"dispatch"),null);
        Assert.Equal(HttpStatusCode.Accepted,(await Receive(s,s.Provider.Proof(outcome))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        Assert.Equal("send-unknown",(await Read(s,d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,(await Receive(s,s.Provider.Proof())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        await using var db=database.Db(); Assert.Equal(2,await db.IntegrationReceipts.CountAsync(x=>x.OperationId==d.IntegrationOperationId && x.ProcessedAt!=null));
        Assert.Equal("sent",(await Read(s,d.Id)).StatusCode);
    }
    [Fact]
    public async Task CertainFailureBeforeProviderCreatesNoOutbound()
    {
        using var s=await Prepare(); s.Provider.Mode="not-sent"; var d=await Delivery(s);
        await s.Client.PostAsync(Route(d.Id,"dispatch"),null); Assert.Equal("failed",(await Read(s,d.Id)).StatusCode);
        Assert.Equal(0,s.Provider.Calls); await using var db=database.Db(); Assert.Empty(await db.EmailMessages.Where(x=>x.WorkspaceId==s.Workspace).ToListAsync());
    }
    [Fact]
    public async Task ConcurrentCreateAndDispatchHaveOneWinnerAndNoTransactionAcrossTransport()
    {
        using var s=await Prepare(); var responses=await Task.WhenAll(Create(s),Create(s));
        var one=(await responses[0].Content.ReadFromJsonAsync<EmailSendDelivery>())!;
        Assert.Equal(one.Id,(await responses[1].Content.ReadFromJsonAsync<EmailSendDelivery>())!.Id);
        s.Provider.DuringSend=async()=> { await using var db=database.Db();
            // This transaction must acquire the dispatch locks while the HTTP handler is still active.
            await using var tx=await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM \"EmailAccounts\" WHERE \"Id\"={s.Account.Id} FOR UPDATE");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT * FROM \"EmailSendDeliveries\" WHERE \"Id\"={one.Id} FOR UPDATE");
        };
        var sends=await Task.WhenAll(s.Client.PostAsync(Route(one.Id,"dispatch"),null),s.Client.PostAsync(Route(one.Id,"dispatch"),null));
        Assert.Single(sends,x=>x.StatusCode==HttpStatusCode.NoContent); Assert.Single(sends,x=>x.StatusCode==HttpStatusCode.Conflict); Assert.Equal(1,s.Provider.Calls);
        Assert.Equal("awaiting-result",(await Read(s,one.Id)).StatusCode);
    }
    [Fact]
    public async Task CallbackBeforeAckAndConcurrentReplayDoNotOverwriteSuccess()
    {
        using var s=await Prepare(); var d=await Delivery(s);
        s.Provider.DuringSend=async()=> { var proof=s.Provider.Proof(); var responses=await Task.WhenAll(Receive(s,proof),Receive(s,proof));
            Assert.All(responses,x=>Assert.Equal(HttpStatusCode.Accepted,x.StatusCode));
            Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode); };
        await s.Client.PostAsync(Route(d.Id,"dispatch"),null); Assert.Equal("sent",(await Read(s,d.Id)).StatusCode);
    }
    [Theory]
    [InlineData("account")][InlineData("from")][InlineData("settings")][InlineData("decision")][InlineData("owner")][InlineData("snapshot")]
    public async Task AuthorizationIsRecheckedBeforeDispatch(string change)
    {
        using var s=await Prepare(); var d=await Delivery(s); await using var db=database.Db();
        if(change=="account") await db.EmailAccounts.Where(x=>x.Id==s.Account.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.StatusCode,"disabled"));
        if(change=="from") await db.EmailAccounts.Where(x=>x.Id==s.Account.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.EmailAddress,"changed@example.invalid"));
        if(change=="settings") await db.AutomationRuntimeSettings.Where(x=>x.WorkspaceId==s.Workspace).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.IsEnabled,false));
        if(change=="decision") await db.AutomationActionRequests.Where(x=>x.Id==s.Plan.AutomationActionRequestId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.StatusCode,"rejected"));
        if(change=="owner") { var outsider=new UserAccount {Email="outsider@example.invalid"}; db.Add(outsider); await db.SaveChangesAsync();
            await db.AutomationActionRequests.Where(x=>x.Id==s.Plan.AutomationActionRequestId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.DecidedByUserId,(Guid?)outsider.Id)); }
        if(change=="snapshot") await db.AutomationActionRequests.Where(x=>x.Id==s.Plan.AutomationActionRequestId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ContentFingerprint,new string('0',64)));
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode); Assert.Equal(0,s.Provider.Calls);
    }
    [Fact]
    public async Task PendingApprovalCannotCreateDeliveryAndDefaultTransportIsDisabled()
    {
        using var pending=await Prepare(false); Assert.Equal(HttpStatusCode.Conflict,(await Create(pending)).StatusCode);
        using var disabled=await Prepare(transport:false); var d=await Delivery(disabled);
        Assert.Equal(HttpStatusCode.Conflict,(await disabled.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        Assert.Equal("pending",(await Read(disabled,d.Id)).StatusCode);
    }
    [Theory]
    [InlineData("wrong-workspace")][InlineData("wrong-connection")][InlineData("revoked")]
    public async Task CallbackHmacScopeAndRevocationEnforced(string key)
    {
        using var s=await Prepare(); var d=await Delivery(s); await s.Client.PostAsync(Route(d.Id,"dispatch"),null);
        Assert.Contains((await Receive(s,s.Provider.Proof(),key)).StatusCode,new[]{HttpStatusCode.Forbidden,HttpStatusCode.Unauthorized});
        Assert.Equal("awaiting-result",(await Read(s,d.Id)).StatusCode);
    }
    [Fact]
    public async Task HumanMutationsRequireCookieAndCsrfAndNeverAcceptArbitraryContent()
    {
        using var s=await Prepare(); using var anonymous=s.Host.Client(); var route=$"/api/email-send-deliveries/from-plan/{s.Plan.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsync(route,null)).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); Assert.Equal(HttpStatusCode.BadRequest,(await s.Client.PostAsync(route,null)).StatusCode);
    }
    [Fact]
    public async Task ProviderIdentityConflictRollsBackAllProcessingAndPreservesReceipt()
    {
        using var s=await Prepare(); var d=await Delivery(s); await s.Client.PostAsync(Route(d.Id,"dispatch"),null); var proof=s.Provider.Proof();
        await using var db=database.Db(); db.EmailMessages.Add(new(){WorkspaceId=s.Workspace,EmailAccountId=s.Account.Id,ProviderCode="synthetic",ExternalMessageId=proof.ExternalMessageId,
            DirectionCode="outbound",FromAddress="sender@example.invalid",ToAddressesJson="[]",OccurredAt=DateTimeOffset.UtcNow}); await db.SaveChangesAsync();
        await Receive(s,proof); Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        Assert.Equal("result-received",(await Read(s,d.Id)).StatusCode); Assert.Null((await db.IntegrationReceipts.FindAsync(proof.MessageId))!.ProcessedAt);
        Assert.Equal(1,await db.EmailMessages.CountAsync(x=>x.WorkspaceId==s.Workspace));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task PersistedCrashBeforeOrAfterProviderCallRecoversUnknownWithoutResending(bool providerAccepted)
    {
        using var s=await Prepare(); var d=await Delivery(s);
        await using(var db=database.Db())
        {
            // Reconstruct a durable pre-crash checkpoint with timestamps older than the recovery grace period.
            var op=await db.IntegrationOperations.SingleAsync(x=>x.Id==d.IntegrationOperationId);
            await db.EmailSendDeliveries.Where(x=>x.Id==d.Id).ExecuteDeleteAsync();
            d.CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-4); d.StartedAt=d.CreatedAt.AddSeconds(1); d.StatusCode="dispatching";
            op.CreatedAt=d.CreatedAt; op.StatusCode="awaiting-result"; db.Add(d);
            db.IntegrationAttempts.Add(new(){Id=Guid.NewGuid(),OperationId=op.Id,Number=1,StatusCode="started",StartedAt=d.StartedAt.Value});
            await db.SaveChangesAsync();
            if(providerAccepted)
            {
                var plan=await db.EmailSendPlans.AsNoTracking().SingleAsync(x=>x.Id==s.Plan.Id);
                await s.Provider.DispatchAsync(new(1,op.CommandMessageId,op.Id,op.CorrelationId,"email.send",d.Id,d.IdempotencyKey,EmailSendSnapshots.From(plan)),default);
                // Intentionally no transport finalization, as after a process crash.
            }
        }
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        Assert.Equal("send-unknown",(await Read(s,d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode);
        if(providerAccepted)
        {
            Assert.True(s.Provider.Accepted.ContainsKey(d.IdempotencyKey)); // Simulated provider lookup by stable key.
            await Receive(s,s.Provider.Proof()); await s.Client.PostAsync(Route(d.Id,"reconcile"),null);
            Assert.Equal("sent",(await Read(s,d.Id)).StatusCode);
        }
        Assert.Equal(providerAccepted?1:0,s.Provider.Calls);
    }
    [Fact]
    public async Task ProviderSuccessThenDatabaseWriteFailureRollsBackAndCanReconcileWithoutSend()
    {
        using var s=await Prepare(); var d=await Delivery(s); await s.Client.PostAsync(Route(d.Id,"dispatch"),null);
        var proof=s.Provider.Proof(); await Receive(s,proof);
        await using var db=database.Db();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_send_test() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic commit failure'; END $$;
            CREATE CONSTRAINT TRIGGER fail_send_test AFTER INSERT ON "EmailMessages" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_send_test();
            """);
        try
        {
            var failed=await s.Client.PostAsync(Route(d.Id,"reconcile"),null); Assert.False(failed.IsSuccessStatusCode);
            Assert.Empty(await db.EmailMessages.Where(x=>x.WorkspaceId==s.Workspace).ToListAsync());
            Assert.Null((await db.IntegrationReceipts.AsNoTracking().SingleAsync(x=>x.MessageId==proof.MessageId)).ProcessedAt);
            Assert.Equal("result-received",(await Read(s,d.Id)).StatusCode);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_send_test ON \"EmailMessages\"; DROP FUNCTION fail_send_test();"); }
        Assert.Equal(HttpStatusCode.NoContent,(await s.Client.PostAsync(Route(d.Id,"reconcile"),null)).StatusCode);
        Assert.Equal("sent",(await Read(s,d.Id)).StatusCode); Assert.Equal(1,s.Provider.Calls);
    }
    [Fact]
    public async Task OriginatingAutomationRuleIsRevalidatedAfterHumanApproval()
    {
        using var s=await Prepare(); await using var db=database.Db();
        var rule=new AutomationRule {WorkspaceId=s.Workspace,Name="Prepare",Enabled=true,TriggerTypeCode="manual",ActionTypeCode="email.send",
            ActionConfigurationJson=JsonSerializer.Serialize(new EmailSendContent(s.Account.Id,["recipient@example.invalid"],[],"Automation","Body"),new JsonSerializerOptions(JsonSerializerDefaults.Web))};
        db.Add(rule); await db.SaveChangesAsync();
        await using var scope=s.Host.Services.CreateAsyncScope(); var queue=scope.ServiceProvider.GetRequiredService<IAutomationJobQueue>();
        var queued=await queue.EnqueueAsync(s.Workspace,new(){AutomationRuleId=rule.Id,TriggerTypeCode="manual",ActionCategoryCode="email",
            ContextJson="""{"eventKey":"delivery-test","pipelineId":null,"payload":{}}"""},default); Assert.Equal(201,queued.Status);
        var job=(await queue.ClaimAsync(s.Workspace,default))!;
        await scope.ServiceProvider.GetRequiredService<IAutomationJobProcessor>().ProcessAsync(job,job.LeaseOwner!,default);
        var request=await db.AutomationActionRequests.AsNoTracking().SingleAsync(x=>x.AutomationJobId==job.Id);
        var plan=await db.EmailSendPlans.AsNoTracking().SingleAsync(x=>x.AutomationActionRequestId==request.Id);
        Assert.Equal(HttpStatusCode.OK,(await s.Client.PostAsJsonAsync($"/api/automation-action-requests/{request.Id}/approve",new{})).StatusCode);
        var response=await s.Client.PostAsync($"/api/email-send-deliveries/from-plan/{plan.Id}",null); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var d=(await response.Content.ReadFromJsonAsync<EmailSendDelivery>())!;
        await db.AutomationRules.Where(x=>x.Id==rule.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Enabled,false));
        Assert.Equal(HttpStatusCode.Conflict,(await s.Client.PostAsync(Route(d.Id,"dispatch"),null)).StatusCode); Assert.Equal(0,s.Provider.Calls);
    }
    [Fact]
    public async Task CallbackIdentifiersCannotSelectAnotherOperation()
    {
        using var s=await Prepare(); var d=await Delivery(s); await s.Client.PostAsync(Route(d.Id,"dispatch"),null);
        var proof=s.Provider.Proof() with {CommandMessageId=Guid.NewGuid()};
        Assert.Equal(HttpStatusCode.Conflict,(await Receive(s,proof)).StatusCode);
        await using var db=database.Db(); Assert.Empty(await db.IntegrationReceipts.Where(x=>x.OperationId==d.IntegrationOperationId).ToListAsync());
    }
}
