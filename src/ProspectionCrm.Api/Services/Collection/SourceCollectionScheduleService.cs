using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.SavedSearches;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

public sealed class SourceCollectionScheduleService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspaceProvider)
{
    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateCollectionScheduleRequest request, CancellationToken token)
    {
        Guid workspace;
        try { workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return (true, "Exactly one active workspace is required to configure a schedule."); }
        await using var tx = await db.Database.BeginTransactionAsync(token);
        // All schedule mutations and scheduling decisions serialize on the parent search.
        var search = (await db.SavedSearches.FromSqlInterpolated($"""
            SELECT * FROM "SavedSearches" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR NO KEY UPDATE
            """).AsNoTracking().ToListAsync(token)).SingleOrDefault();
        if (search is null) return (false, null);
        var schedule = await db.SourceCollectionSchedules.SingleOrDefaultAsync(x => x.SavedSearchId == id && x.WorkspaceId == workspace, token);
        if (request.Enabled is null) return (true, "Enabled is required.");
        if (request.Enabled == false)
        {
            if (request.DailyUtcTime is not null || request.PipelineStageId is not null)
                return (true, "Disabling accepts only Enabled=false; the existing time and target are retained.");
            if (schedule is not null) { schedule.Enabled = false; schedule.NextCollectionAt = null; }
        }
        else
        {
            if (!SourceCollectionScheduleCalculator.TryParse(request.DailyUtcTime, out var minute))
                return (true, "DailyUtcTime must use HH:mm in UTC.");
            if (request.PipelineStageId is null || !await IsTargetValidAsync(db, search, request.PipelineStageId.Value, token))
                return (true, "Scheduling requires an active workspace, search, pipeline, source and a target stage in that pipeline.");
            var unchanged = schedule is { Enabled: true } && schedule.DailyUtcMinute == minute
                && schedule.PipelineStageId == request.PipelineStageId;
            if (schedule is null)
            {
                schedule = new() { SavedSearchId = id, WorkspaceId = workspace, PipelineId = search.PipelineId };
                db.SourceCollectionSchedules.Add(schedule);
            }
            schedule.Enabled = true;
            schedule.DailyUtcMinute = minute;
            schedule.PipelineStageId = request.PipelineStageId.Value;
            // Repeating the same PUT cannot postpone a due occurrence.
            if (!unchanged)
            {
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
                schedule.NextCollectionAt = SourceCollectionScheduleCalculator.Next(now, minute);
            }
        }
        try
        {
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return (true, null);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return (true, "The scheduling references changed concurrently; reload the search and retry configuration.");
        }
    }

    internal static async Task<bool> IsTargetValidAsync(ProspectionCrmDbContext db, SavedSearch search, Guid stage, CancellationToken token) =>
        search.Enabled && search.ArchivedAt is null
        && await db.Workspaces.AnyAsync(x => x.Id == search.WorkspaceId && x.ArchivedAt == null, token)
        && await db.Pipelines.AnyAsync(x => x.Id == search.PipelineId && x.WorkspaceId == search.WorkspaceId && x.ArchivedAt == null, token)
        && await db.PipelineStages.AnyAsync(x => x.Id == stage && x.PipelineId == search.PipelineId && x.ArchivedAt == null, token)
        && await db.SourceConfigurations.AnyAsync(x => x.Id == search.SourceConfigurationId && x.WorkspaceId == search.WorkspaceId
            && x.Enabled && x.ArchivedAt == null, token);
}
