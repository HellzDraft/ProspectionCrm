using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

public sealed record CollectionPrecondition(Guid WorkspaceId, string Fingerprint, DateTimeOffset StartedAt,
    CollectionJobAttempt? Job = null);

public static class CollectionFingerprint
{
    // Explicit field order, invariant JSON primitives and UTC timestamps; no entity graph serialization.
    public static string Create(SavedSearch search, SourceConfiguration source, Pipeline pipeline, PipelineStage stage)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            search = new { search.Id, search.WorkspaceId, search.PipelineId, search.SourceConfigurationId,
                search.Name, search.SearchUrl, search.CriteriaJson, search.Enabled,
                UpdatedAt = search.UpdatedAt?.ToUniversalTime(), ArchivedAt = search.ArchivedAt?.ToUniversalTime() },
            source = new { source.Id, source.WorkspaceId, source.Name, source.SourceTypeCode, source.BaseUrl,
                ConfigurationHash = source.ConfigurationJson is null ? null : Hash(source.ConfigurationJson), source.Enabled,
                UpdatedAt = source.UpdatedAt?.ToUniversalTime(), ArchivedAt = source.ArchivedAt?.ToUniversalTime() },
            pipeline = new { pipeline.Id, pipeline.WorkspaceId, pipeline.Name, pipeline.TypeCode,
                UpdatedAt = pipeline.UpdatedAt?.ToUniversalTime(), ArchivedAt = pipeline.ArchivedAt?.ToUniversalTime() },
            stage = new { stage.Id, stage.PipelineId, stage.Name, stage.CategoryCode, stage.SortOrder,
                UpdatedAt = stage.UpdatedAt?.ToUniversalTime(), ArchivedAt = stage.ArchivedAt?.ToUniversalTime() }
        });
        return Hash(canonical);
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
