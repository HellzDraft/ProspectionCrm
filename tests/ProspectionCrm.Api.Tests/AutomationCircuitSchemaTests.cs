using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationCircuitSchemaTests(AutomationJobDatabase database)
    : AutomationSupervisionFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(0)][InlineData(-1)]
    public async Task ResetSequenceMustBePositive(long sequence)
    {
        var ws = await Workspace(); await using var db = Db();
        var owner = (await db.Workspaces.SingleAsync(x => x.Id == ws)).OwnerUserId;
        db.Add(new AutomationCircuitReset { WorkspaceId=ws,RequestedByUserId=owner,
            RequestedAt=Clock.Now,ResetAfterOutcomeSequence=sequence });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514",Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
    [Theory]
    [InlineData("workspace")][InlineData("user")][InlineData("note")]
    public async Task InvalidReferencesAndOversizedNotesAreRejectedByPostgres(string invalid)
    {
        var ws = await Workspace(); await using var db = Db();
        var owner = (await db.Workspaces.SingleAsync(x => x.Id == ws)).OwnerUserId;
        db.Add(new AutomationCircuitReset { WorkspaceId=invalid=="workspace"?Guid.NewGuid():ws,
            RequestedByUserId=invalid=="user"?Guid.NewGuid():owner,RequestedAt=Clock.Now,ResetAfterOutcomeSequence=1,
            Note=invalid=="note"?new string('x',2001):null });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(invalid=="note"?"22001":"23503",Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
    [Fact]
    public async Task UniqueIndexArbitratesDirectConcurrentWritersAndDeletesAreRestricted()
    {
        var ws = await Workspace(); Guid owner;
        await using (var db = Db()) owner=(await db.Workspaces.SingleAsync(x=>x.Id==ws)).OwnerUserId;
        async Task<string?> Insert()
        {
            await using var db=Db();
            db.Add(new AutomationCircuitReset { WorkspaceId=ws,RequestedByUserId=owner,RequestedAt=Clock.Now,ResetAfterOutcomeSequence=1 });
            try { await db.SaveChangesAsync(); return null; }
            catch(DbUpdateException e) { return Assert.IsType<PostgresException>(e.InnerException).SqlState; }
        }
        var results=await Task.WhenAll(Insert(),Insert()); Assert.Single(results,x=>x is null); Assert.Single(results,x=>x=="23505");
        await using var read=Db();
        var definitions=await read.Database.SqlQuery<string>($"""
            SELECT pg_get_constraintdef(oid) AS "Value" FROM pg_constraint
            WHERE conrelid = '"AutomationCircuitResets"'::regclass AND contype='f'
            """).ToListAsync();
        Assert.Equal(2,definitions.Count); Assert.All(definitions,x=>Assert.Contains("ON DELETE RESTRICT",x));
        Assert.Equal("23001",(await Assert.ThrowsAsync<PostgresException>(()=>read.UserAccounts.Where(x=>x.Id==owner).ExecuteDeleteAsync())).SqlState);
        Assert.Equal("23001",(await Assert.ThrowsAsync<PostgresException>(()=>read.Workspaces.Where(x=>x.Id==ws).ExecuteDeleteAsync())).SqlState);
    }
}
