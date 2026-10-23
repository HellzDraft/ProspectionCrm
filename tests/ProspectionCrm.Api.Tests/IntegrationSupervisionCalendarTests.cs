using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationSupervisionCalendarTests(AutomationJobDatabase database)
    : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CalendarDeliveryIsObservedAcrossUnknownEvidenceCompletionAndConflict(bool update)
    {
        var provider = new Provider { Mode = "timeout" }; using var s = await Prepare(provider);
        var target = update ? await Target(s, provider) : null;
        var d = await Delivery(s, await Approved(s, target is null ? null : UpdateContent(s, target)));
        async Task AssertView(string state, long receipts)
        {
            await using var db = Db(); var before = await IntegrationSupervisionTests.Fingerprint(db); var calls = provider.Calls;
            var page = (await s.Client.GetFromJsonAsync<IntegrationSupervisionPage>(IntegrationSupervisionTests.Root + "/operations"))!;
            var item = Assert.Single(page.Items); Assert.Equal(d.Id, item.BusinessId); Assert.Equal(d.IntegrationOperationId, item.Id);
            Assert.Equal(state, item.State); Assert.Equal(receipts, item.UnprocessedReceiptCount);
            Assert.Equal(update ? "calendar.event.update" : "calendar.event.create", item.Type);
            var snapshot = (await s.Client.GetFromJsonAsync<IntegrationSupervisionSnapshot>(IntegrationSupervisionTests.Root))!;
            Assert.Equal(1, snapshot.OperationCount); Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
            Assert.Equal(calls, provider.Calls);
        }
        await AssertView("waiting", 0); await Dispatch(s, d.Id); await AssertView("uncertain", 0);
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof)).StatusCode); await AssertView("evidence-pending", 1);
        await Reconcile(s, d.Id); await AssertView("completed", 0);
        // Contradictory late evidence keeps the durable conflict, including its unprocessed receipt.
        Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, provider.Proof with { MessageId = Guid.NewGuid(), Outcome = "not-applied", Event = null })).StatusCode);
        await Reconcile(s, d.Id); await AssertView("conflict", 1);
    }
}

public sealed class IntegrationSupervisionCalendarSyncTests(AutomationJobDatabase database)
    : CalendarSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HistoricalCalendarSyncMetadataAndFreeTextDiagnosticAreSafe()
    {
        using var s = await Prepare();
        var item = Assert.Single((await s.Client.GetFromJsonAsync<IntegrationSupervisionPage>(IntegrationSupervisionTests.Root + "/operations"))!.Items);
        Assert.Equal(s.Job.Id, item.BusinessId); Assert.Equal("evidence-pending", item.State);
        await Process(s);
        await using var db = Db();
        // An untrusted diagnostic stored by historical/custom code must never be returned verbatim.
        await db.CalendarSyncJobs.Where(x => x.Id == s.Job.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.StatusCode, "failed").SetProperty(j => j.ErrorCode, "private-address@example.invalid"));
        var before = await IntegrationSupervisionTests.Fingerprint(db);
        item = Assert.Single((await s.Client.GetFromJsonAsync<IntegrationSupervisionPage>(IntegrationSupervisionTests.Root + "/operations"))!.Items);
        Assert.Equal("failed", item.State); Assert.Equal("RecordedDiagnostic", item.DiagnosticCode); Assert.True(item.HasRecordedDiagnostic);
        Assert.DoesNotContain("private-address", await s.Client.GetStringAsync(IntegrationSupervisionTests.Root));
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
    }
}

public sealed class IntegrationSupervisionEmailSyncTests(AutomationJobDatabase database)
    : EmailSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HistoricalSyncConflictRemainsAConflictEvenWhenOperationIsTerminal()
    {
        using var s = await Prepare(); await Process(s); await using var db = Db();
        await db.EmailSyncJobs.Where(x => x.Id == s.Job.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.StatusCode, "failed").SetProperty(j => j.ErrorCode, "EmailMessageContentConflict"));
        var before = await IntegrationSupervisionTests.Fingerprint(db);
        var item = Assert.Single((await s.Client.GetFromJsonAsync<IntegrationSupervisionPage>(IntegrationSupervisionTests.Root + "/operations"))!.Items);
        Assert.Equal(s.Job.Id, item.BusinessId); Assert.Equal("conflict", item.State); Assert.Equal("EmailMessageContentConflict", item.DiagnosticCode);
        Assert.Contains((await s.Client.GetFromJsonAsync<IntegrationSupervisionSnapshot>(IntegrationSupervisionTests.Root))!.Alerts, x => x.Code == "IntegrationConflict");
        Assert.Equal(before, await IntegrationSupervisionTests.Fingerprint(db));
    }
}
