using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AiModelConfigurations;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class AiModelConfigurationService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IAiModelConfigurationService
{
    public async Task<IReadOnlyList<AiModelConfigurationDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.AiModelConfigurations.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<AiModelConfigurationDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiModelConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(AiModelConfigurationDto? Item, string? Error)> CreateAsync(CreateAiModelConfigurationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);

        var entity = new AiModelConfiguration
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            ProviderCode = request.ProviderCode,
            ModelName = request.ModelName,
            IsDefault = request.IsDefault,
            Enabled = request.Enabled,
        };
        var saveError = await SaveModelAsync(entity, request.IsDefault,
            () => dbContext.AiModelConfigurations.Add(entity), cancellationToken);
        return saveError is null ? (ToDto(entity), null) : (null, saveError);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateAiModelConfigurationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiModelConfigurations.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);

        var saveError = await SaveModelAsync(entity, request.IsDefault && entity.ArchivedAt == null, () =>
        {
            entity.Name = request.Name;
            entity.ProviderCode = request.ProviderCode;
            entity.ModelName = request.ModelName;
            entity.IsDefault = request.IsDefault;
            entity.Enabled = request.Enabled;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        return (true, saveError);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiModelConfigurations.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return false;
        var now = DateTimeOffset.UtcNow;
        entity.ArchivedAt = now;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.AiModelConfigurations.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var saveError = await SaveModelAsync(entity, entity.IsDefault, () =>
        {
            entity.ArchivedAt = null;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        return (true, saveError);
    }

    private async Task<string?> SaveModelAsync(AiModelConfiguration model, bool makeActiveDefault,
        Action applyChanges, CancellationToken cancellationToken)
    {
        if (!makeActiveDefault)
        {
            applyChanges();
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }

        // Release the filtered unique key before promoting the new default.
        // Both SaveChanges calls share one transaction: no partial default switch is committed.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var otherDefaults = await dbContext.AiModelConfigurations.Where(x => x.WorkspaceId == model.WorkspaceId
                && x.Id != model.Id && x.ArchivedAt == null && x.IsDefault).ToListAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            foreach (var other in otherDefaults)
            {
                other.IsDefault = false;
                other.UpdatedAt = now;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            applyChanges();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_AiModelConfigurations_ActiveDefault" })
        {
            await transaction.RollbackAsync(cancellationToken);
            return "The default AI model changed concurrently. Please retry.";
        }
    }

    private static AiModelConfigurationDto ToDto(AiModelConfiguration entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        ProviderCode = entity.ProviderCode,
        ModelName = entity.ModelName,
        IsDefault = entity.IsDefault,
        Enabled = entity.Enabled,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt
    };
}
