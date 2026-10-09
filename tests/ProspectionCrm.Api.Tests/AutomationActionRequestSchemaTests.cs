using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

#pragma warning disable EF1003 // SQL assignments below are fixed InlineData literals; IDs remain parameterized.

public sealed class AutomationActionRequestSchemaTests(AutomationJobDatabase database)
    : AutomationActionRequestFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("\"DecisionRequirementCode\" = 'automatic'")]
    [InlineData("\"StatusCode\" = 'executed'")]
    [InlineData("\"StatusCode\" = 'approved'")]
    [InlineData("\"StatusCode\" = 'rejected'")]
    [InlineData("\"StatusCode\" = 'cancelled'")]
    [InlineData("\"RuleFingerprint\" = 'aaaaaaaa'")]
    [InlineData("\"RuleFingerprint\" = repeat('A',64)")]
    [InlineData("\"ActionPlanJson\" = '[]'::jsonb")]
    [InlineData("\"ActionPlanJson\" = jsonb_build_object('x',repeat('x',65536))")]
    [InlineData("\"RequestedReasonCode\" = ' '")]
    [InlineData("\"DecidedAt\" = now()")]
    public async Task RequestChecksRejectInconsistentDirectWrites(string assignment)
    {
        var setup = await Prepare("manual"); await Run(setup); var request = await RequestFor(setup.Job.Id);
        await using var db = Db();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"AutomationActionRequests\" SET " + assignment + " WHERE \"Id\" = {0}", request.Id));
        Assert.Equal("23514", error.SqlState);
    }

    [Theory]
    [InlineData("\"IsAutomaticAttempt\" = true")]
    [InlineData("\"IsHumanApprovedAttempt\" = false")]
    [InlineData("\"AutomationActionRequestId\" = NULL")]
    [InlineData("\"OutcomeSequence\" = 100")]
    public async Task HumanExecutionChecksRejectInconsistentOrigins(string assignment)
    {
        var setup = await Approved(); await Run(setup); var execution = Assert.Single(await Executions(setup.Job.Id));
        await using var db = Db();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"AutomationExecutions\" SET " + assignment + " WHERE \"Id\" = {0}", execution.Id));
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task ExecutionCannotReferenceAnotherRequestsJobOrWorkspace()
    {
        var setup = await Approved(); await Run(setup);
        var sameWorkspace = await Another(setup); var second = setup with { Job = sameWorkspace };
        await Run(second); await Decide(second); var request = await RequestFor(second.Job.Id);
        var execution = Assert.Single(await Executions(setup.Job.Id));
        await using var db = Db();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationExecutions" SET "AutomationActionRequestId" = {request.Id} WHERE "Id" = {execution.Id}
            """)); Assert.Equal("23503", error.SqlState);
        var other = await Approved(); request = await RequestFor(other.Job.Id);
        error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationExecutions" SET "AutomationActionRequestId" = {request.Id} WHERE "Id" = {execution.Id}
            """)); Assert.Equal("23503", error.SqlState);
    }
}
