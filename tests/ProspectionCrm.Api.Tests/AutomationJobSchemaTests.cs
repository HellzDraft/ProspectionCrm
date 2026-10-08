using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data.Configurations;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationJobSchemaTests(AutomationJobDatabase database) : AutomationJobFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("\"StatusCode\" = 'unknown'", "23514")]
    [InlineData("\"Priority\" = -1", "23514")]
    [InlineData("\"Priority\" = 101", "23514")]
    [InlineData("\"AttemptCount\" = -1", "23514")]
    [InlineData("\"TriggerTypeCode\" = ''", "23514")]
    [InlineData("\"TriggerTypeCode\" = 'unknown'", "23514")]
    [InlineData("\"ActionCategoryCode\" = ''", "23514")]
    [InlineData("\"ActionCategoryCode\" = 'unknown'", "23514")]
    [InlineData("\"StatusCode\" = 'leased'", "23514")]
    [InlineData("\"StatusCode\" = 'leased', \"LeaseOwner\" = 'owner'", "23514")]
    [InlineData("\"StatusCode\" = 'leased', \"LeaseExpiresAt\" = now()", "23514")]
    [InlineData("\"StatusCode\" = 'leased', \"LeaseOwner\" = ' ', \"LeaseExpiresAt\" = now()", "23514")]
    [InlineData("\"LeaseOwner\" = 'owner'", "23514")]
    [InlineData("\"LeaseExpiresAt\" = now()", "23514")]
    [InlineData("\"CompletedAt\" = now()", "23514")]
    [InlineData("\"StatusCode\" = 'completed'", "23514")]
    [InlineData("\"StatusCode\" = 'failed'", "23514")]
    [InlineData("\"StatusCode\" = 'cancelled'", "23514")]
    [InlineData("\"ContextJson\" = '[]'::jsonb", "23514")]
    [InlineData("\"ContextJson\" = 'null'::jsonb", "23514")]
    [InlineData("\"ContextJson\" = jsonb_build_object('x', repeat('x', 65536))", "23514")]
    [InlineData("\"LastError\" = 'secret'", "23514")]
    [InlineData("\"WorkspaceId\" = '00000000-0000-0000-0000-000000000001'", "23503")]
    [InlineData("\"AutomationRuleId\" = '00000000-0000-0000-0000-000000000001'", "23503")]
    public async Task PostgreSqlEnforcesConstraintsEvenWhenBypassingTheQueue(string assignment, string sqlState)
    {
        var workspace = await Workspace(); var job = await Add(workspace); await using var db = Db();
        // Only fixed test cases supply SQL syntax; the job identifier remains parameterized.
        var sql = "UPDATE \"AutomationJobs\" SET " + assignment + " WHERE \"Id\" = {0}";
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, job.Id));
        Assert.Equal(sqlState, error.SqlState);
    }

    [Fact]
    public async Task CompositeRuleForeignKeyPreventsCrossWorkspaceAndRestrictsDeletion()
    {
        var workspace = await Workspace(); var other = await Workspace(); var rule = await Rule(other);
        await using var db = Db();
        var job = new AutomationJob { WorkspaceId = workspace, AutomationRuleId = rule, TriggerTypeCode = "manual", ActionCategoryCode = "general" };
        db.Add(job); var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(error.InnerException).SqlState); db.ChangeTracker.Clear();
        await Add(other, Request(rule: rule));
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => db.AutomationRules.Where(x => x.Id == rule).ExecuteDeleteAsync());
        Assert.Equal("23001", deletion.SqlState);
    }

    [Fact]
    public async Task PartialUniqueIndexProtectsDirectInsertsAndHasExpectedDefinition()
    {
        var workspace = await Workspace(); await Add(workspace, Request("event")); await using var db = Db();
        db.Add(new AutomationJob { WorkspaceId = workspace, TriggerTypeCode = "manual", TriggerKey = "event", ActionCategoryCode = "general" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal("23505", pg.SqlState); Assert.Equal(AutomationJobConfiguration.DeduplicationIndex, pg.ConstraintName);
        var index = await db.Database.SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {AutomationJobConfiguration.DeduplicationIndex}").SingleAsync();
        Assert.Contains("UNIQUE", index); Assert.Contains("IS NOT NULL", index);
        Assert.Contains("(\"WorkspaceId\", \"TriggerTypeCode\", \"TriggerKey\")", index);
        var timestamps = await db.Database.SqlQuery<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'AutomationJobs' AND column_name IN ('CreatedAt','AvailableAt','UpdatedAt','CompletedAt','LeaseExpiresAt')").ToListAsync();
        Assert.Equal(5, timestamps.Count); Assert.All(timestamps, x => Assert.Equal("timestamp with time zone", x));
    }
}
