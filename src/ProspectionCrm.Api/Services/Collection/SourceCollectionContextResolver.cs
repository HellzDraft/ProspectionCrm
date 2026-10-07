using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

public interface ISourceCollectionContextResolver
{
    Task<SourceCollectionContextResult> ResolveAsync(Guid workspaceId, Guid savedSearchId, Guid pipelineStageId, CancellationToken token);
}
public sealed record SourceCollectionContext(SavedSearch Search, SourceConfiguration Source, Pipeline Pipeline,
    PipelineStage Stage, ISourceAdapter Adapter, SourceAdapterContext AdapterContext);
public sealed record SourceCollectionContextResult(SourceCollectionContext? Value, SourceAdapterError? Error = null);

public sealed class SourceCollectionContextResolver(ProspectionCrmDbContext db, SourceAdapterRegistry adapters)
    : ISourceCollectionContextResolver
{
    public async Task<SourceCollectionContextResult> ResolveAsync(Guid workspaceId, Guid savedSearchId, Guid pipelineStageId, CancellationToken token)
    {
        var search = await db.SavedSearches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == savedSearchId && x.WorkspaceId == workspaceId, token);
        if (search is null) return Fail("ResourceNotFound", 404);
        var source = await db.SourceConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == search.SourceConfigurationId && x.WorkspaceId == workspaceId, token);
        var pipeline = await db.Pipelines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == search.PipelineId && x.WorkspaceId == workspaceId, token);
        var stage = await db.PipelineStages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pipelineStageId && x.Pipeline.WorkspaceId == workspaceId, token);
        if (source is null || pipeline is null || stage is null) return Fail("ResourceNotFound", 404);
        if (!search.Enabled || search.ArchivedAt is not null || !source.Enabled || source.ArchivedAt is not null
            || pipeline.ArchivedAt is not null || stage.ArchivedAt is not null) return Fail("InactiveResource", 409);
        if (stage.PipelineId != pipeline.Id) return Fail("WrongPipeline", 409);
        var adapter = adapters.Find(source.SourceTypeCode);
        if (adapter is null) return Fail("UnsupportedSourceType", 409);
        var context = new SourceAdapterContext(search.SearchUrl, search.CriteriaJson);
        var invalid = adapter.Validate(context);
        if (invalid is not null) return new(null, invalid);
        return new(new(search, source, pipeline, stage, adapter, context));
    }
    private static SourceCollectionContextResult Fail(string code, int status) => new(null, new(code, status));
}
