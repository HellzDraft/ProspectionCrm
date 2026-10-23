using System.Net;
using System.Net.Http.Json;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationSupervisionTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    private sealed class Workspace(Guid id) : ICurrentWorkspaceProvider
    { public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id); }
    internal const string Root = "/api/integration-supervision";
    internal static async Task<string> Fingerprint(ProspectionCrmDbContext db)
    {
        var tables = db.Model.GetEntityTypes().Select(x => x.GetTableName()).OfType<string>().Distinct().Order().ToArray();
        Assert.All(tables, t => Assert.Matches("^[A-Za-z0-9_]+$", t)); // Only trusted model identifiers enter this test query.
        var unions = tables.Select(t => $"SELECT '{t}' AS name, md5(coalesce(string_agg(to_jsonb(t)::text, '' ORDER BY to_jsonb(t)::text),'')) AS hash FROM \"{t}\" t");
#pragma warning disable EF1003 // Fixed, validated EF model table names; no request input.
        return await db.Database.SqlQueryRaw<string>("SELECT string_agg(name || ':' || hash, ',' ORDER BY name) AS \"Value\" FROM (" + string.Join(" UNION ALL ", unions) + ") fingerprints").SingleAsync();
#pragma warning restore EF1003
    }
    private static IntegrationOperation Operation(SecurityTestHost host, string status = "awaiting-result", string type = "technical.probe") => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = host.Operator.WorkspaceId, ConnectionId = host.Integration.ConnectionId,
        CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), ProbeId = type == "technical.probe" ? Guid.NewGuid() : Guid.Empty,
        Version = 1, Type = type, StatusCode = status, CreatedAt = host.Clock.Now.AddHours(-1),
        CompletedAt = status is "succeeded" or "failed" ? host.Clock.Now : null
    };
    private static IntegrationAttempt Attempt(IntegrationOperation op, string status, DateTimeOffset at, int number = 1) => new()
    { Id = Guid.NewGuid(), OperationId = op.Id, Number = number, StatusCode = status, StartedAt = at, FinishedAt = status == "started" ? null : at.AddSeconds(1) };

    private sealed class ReadOnlyAudit : DbCommandInterceptor
    {
        public int Reads;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            Assert.NotNull(command.Transaction); Assert.Equal(IsolationLevel.RepeatableRead, command.Transaction.IsolationLevel);
            Assert.DoesNotContain("CanonicalContent", command.CommandText); Assert.DoesNotContain("ContextSnapshotJson", command.CommandText);
            await using var check = command.Connection!.CreateCommand(); check.Transaction = command.Transaction;
            check.CommandText = "SHOW transaction_read_only"; Assert.Equal("on", await check.ExecuteScalarAsync(token));
            Reads++; return result;
        }
    }
    [Fact]
    public async Task EverySupervisionQueryRunsInsidePostgresReadOnlyRepeatableReadTransaction()
    {
        using var host = await SecurityTestHost.CreateAsync(database); var audit = new ReadOnlyAudit();
        await using var db = new ProspectionCrmDbContext(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
            .UseNpgsql(database.Postgres.GetConnectionString()).AddInterceptors(audit).Options);
        var service = new IntegrationSupervisionService(db, new Workspace(host.Operator.WorkspaceId), host.Clock);
        await service.SnapshotAsync(default); await service.ListAsync(0, 50, null, null, null, default);
        Assert.Equal(4, audit.Reads); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LegacyProbeWithNoConnectionIsVisibleEvenWithIntegrationDisabled()
    {
        using var host = await SecurityTestHost.CreateAsync(database); host.Integration.Enabled = false;
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); var legacy = Operation(host, "pending"); legacy.ConnectionId = null;
        db.Add(legacy); await db.SaveChangesAsync(); var before = await Fingerprint(db);
        var item = Assert.Single((await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations"))!.Items);
        Assert.Null(item.ConnectionId); Assert.Null(item.BusinessId); Assert.Equal("waiting", item.State);
        Assert.Equal(before, await Fingerprint(db));
    }

    [Theory]
    [InlineData(29, false)][InlineData(30, true)]
    public async Task ReceiptAgeWarningDoesNotConvertEvidenceIntoUncertainty(int age, bool warning)
    {
        using var host = await SecurityTestHost.CreateAsync(database);
        host.Clock.Now = new DateTimeOffset(host.Clock.Now.Ticks / 10 * 10, TimeSpan.Zero);
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); var op = Operation(host, "result-received"); db.Add(op); await db.SaveChangesAsync();
        db.IntegrationReceipts.Add(new() { MessageId = Guid.NewGuid(), OperationId = op.Id, CanonicalContent = "opaque",
            ContentSha256 = new string('b', 64), ReceivedAt = host.Clock.Now.AddMinutes(-age) }); await db.SaveChangesAsync();
        var snapshot = (await client.GetFromJsonAsync<IntegrationSupervisionSnapshot>(Root))!;
        Assert.Equal("evidence-pending", Assert.Single(snapshot.Groups).State);
        Assert.Equal(warning, snapshot.Alerts.Any(x => x.Code == "IntegrationEvidenceOld"));
        Assert.DoesNotContain(snapshot.Alerts, x => x.Code == "IntegrationResultUncertain");
    }

    [Fact]
    public async Task TerminalOperationWithUnprocessedLateReceiptStillAdvertisesEvidence()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); var op = Operation(host, "succeeded"); db.Add(op); await db.SaveChangesAsync();
        db.IntegrationReceipts.Add(new() { MessageId = Guid.NewGuid(), OperationId = op.Id, CanonicalContent = "opaque",
            ContentSha256 = new string('c', 64), ReceivedAt = host.Clock.Now }); await db.SaveChangesAsync();
        var before = await Fingerprint(db);
        var item = Assert.Single((await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations"))!.Items);
        Assert.Equal("succeeded", item.StatusCode); Assert.Equal("evidence-pending", item.State); Assert.NotNull(item.CompletedAt);
        Assert.Equal(before, await Fingerprint(db));
    }

    [Fact]
    public async Task EmptyWorkspaceDoesNotCreateSettingsOrAssessN8nHealth()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); var before = await Fingerprint(db);
        Assert.Empty((await new IntegrationSupervisionService(db, new Workspace(host.Operator.WorkspaceId), host.Clock).SnapshotAsync(default)).Groups);
        var snapshot = (await client.GetFromJsonAsync<IntegrationSupervisionSnapshot>(Root))!;
        Assert.Equal(host.Operator.WorkspaceId, snapshot.WorkspaceId); Assert.Equal("not-assessed", snapshot.N8nHealth);
        Assert.Equal(0, snapshot.OperationCount); Assert.Empty(snapshot.Groups); Assert.Empty(snapshot.Alerts);
        Assert.Empty((await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations"))!.Items);
        Assert.Equal(before, await Fingerprint(db));
    }

    [Fact]
    public async Task SnapshotCountsBoundariesLatestAttemptsAndEvidenceWithoutChangingAnyTable()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        host.Clock.Now = new DateTimeOffset(host.Clock.Now.Ticks / 10 * 10, TimeSpan.Zero);
        await using var db = database.Db(); var now = host.Clock.Now;
        var pending = Operation(host, "pending"); var old = Operation(host); var recent = Operation(host);
        var unknown = Operation(host); var failed = Operation(host, "failed"); var completed = Operation(host, "succeeded");
        var evidence = Operation(host, "result-received"); var retried = Operation(host); var missing = Operation(host, type: "collection.request");
        db.AddRange(pending, old, recent, unknown, failed, completed, evidence, retried, missing); await db.SaveChangesAsync();
        db.AddRange(Attempt(old, "started", now.AddMinutes(-5)), Attempt(recent, "started", now.AddMinutes(-5).AddTicks(10)),
            Attempt(unknown, "unknown", now.AddMinutes(-10)), Attempt(completed, "unknown", now.AddMinutes(-10)),
            Attempt(retried, "unknown", now.AddMinutes(-40)), Attempt(retried, "acknowledged", now.AddMinutes(-2), 2));
        db.IntegrationReceipts.Add(new() { MessageId = Guid.NewGuid(), OperationId = evidence.Id, CanonicalContent = "secret-mail@example.invalid body credential signature",
            ContentSha256 = new string('a', 64), ReceivedAt = now.AddMinutes(-20) });
        await db.SaveChangesAsync(); var before = await Fingerprint(db);
        for (var i = 0; i < 2; i++)
        {
            var snapshot = (await client.GetFromJsonAsync<IntegrationSupervisionSnapshot>(Root))!;
            Assert.Equal(9, snapshot.OperationCount); Assert.Equal(2, snapshot.Groups.Sum(x => x.ActiveAttemptCount));
            Assert.Equal(1, snapshot.Groups.Sum(x => x.OldActiveAttemptCount));
            Assert.Contains(snapshot.Alerts, x => x.Code == "IntegrationAttemptOld" && x.Count == 1 && x.OldestAt == now.AddMinutes(-5));
            Assert.Contains(snapshot.Alerts, x => x.Code == "IntegrationWaitingOld" && x.Count == 1 && x.OldestAt == pending.CreatedAt);
            Assert.Contains(snapshot.Alerts, x => x.Code == "IntegrationResultUncertain" && x.Count == 1);
            Assert.Contains(snapshot.Alerts, x => x.Code == "IntegrationEvidencePending" && x.Count == 1);
            var raw = await client.GetStringAsync(Root + "/operations");
            foreach (var secret in new[] { "secret-mail", "credential", "signature", "canonicalContent", "contentSha256" }) Assert.DoesNotContain(secret, raw);
            var page = (await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations"))!;
            string State(IntegrationOperation op) => page.Items.Single(x => x.Id == op.Id).State;
            Assert.Equal("waiting", State(old)); Assert.Equal("waiting", State(retried)); Assert.Equal("uncertain", State(unknown));
            Assert.Equal("completed", State(completed)); Assert.Equal("evidence-pending", State(evidence));
            Assert.Equal("failed", State(failed)); Assert.Equal("diagnostic-required", State(missing));
        }
        Assert.Equal(before, await Fingerprint(db));
    }

    [Fact]
    public async Task StablePaginationFilteringAndWorkspaceIsolation()
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var other = await SecurityTestHost.CreateAsync(database);
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); var own = Enumerable.Range(0, 5).Select(_ => Operation(host, "pending")).ToArray();
        var foreign = Operation(other, "failed"); db.AddRange(own); db.Add(foreign); await db.SaveChangesAsync();
        var first = (await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations?limit=2&type=technical.probe&status=pending&state=waiting"))!;
        var second = (await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations?offset=2&limit=2"))!;
        var last = (await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations?offset=4&limit=2"))!;
        Assert.Equal(5, first.Total); Assert.Equal(5, second.Total);
        Assert.Equal(5, first.Items.Concat(second.Items).Concat(last.Items).Select(x => x.Id).Distinct().Count());
        Assert.DoesNotContain(first.Items.Concat(second.Items).Concat(last.Items), x => x.Id == foreign.Id);
        Assert.Equal(first.Items.Select(x => x.Id), (await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations?limit=2"))!.Items.Select(x => x.Id));
        Assert.Empty((await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations?offset=99"))!.Items);
        Assert.Equal(0, (await client.GetFromJsonAsync<IntegrationSupervisionPage>(Root + "/operations?state=failed"))!.Total);
        Assert.Equal(5, (await client.GetFromJsonAsync<IntegrationSupervisionSnapshot>(Root + "?workspaceId=" + other.Operator.WorkspaceId))!.OperationCount);
    }

    [Theory]
    [InlineData("offset=-1")][InlineData("offset=1000001")][InlineData("limit=0")][InlineData("limit=101")]
    [InlineData("limit=secret@example.invalid")][InlineData("type=secret@example.invalid")]
    [InlineData("state=unrecognized")][InlineData("status=unknown")]
    public async Task InvalidQueriesDoNotEchoValues(string query)
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var response = await client.GetAsync(Root + "/operations?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret@example.invalid", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")][InlineData("/operations")]
    public async Task AnonymousAndMachineCannotReadHumanSupervision(string suffix)
    {
        using var host = await SecurityTestHost.CreateAsync(database); using var client = host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root + suffix)).StatusCode);
        using var signed = host.Signed([], target: Root + suffix); signed.Method = HttpMethod.Get;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(signed)).StatusCode);
        await SecurityTestHost.LoginAsync(client);
        await using var db = database.Db(); await db.Workspaces.Where(x => x.Id == host.Operator.WorkspaceId).ExecuteUpdateAsync(x => x.SetProperty(w => w.ArchivedAt, host.Clock.Now));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root + suffix)).StatusCode);
    }
}
