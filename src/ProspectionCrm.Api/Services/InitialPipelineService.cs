using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public sealed class InitialPipelineService(
    ProspectionCrmDbContext dbContext,
    ICurrentWorkspaceProvider currentWorkspaceProvider,
    IPipelineService pipelineService) : IInitialPipelineService
{
    private const string InitialName = "Emploi .NET";

    public async Task<InitialPipelineResult> InitializeAsync(CancellationToken cancellationToken)
    {
        Guid workspaceId;
        try
        {
            workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return new(Error: "Initial pipelines require exactly one active workspace. Run the V1 bootstrap first on an empty installation.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Same lock/order as archive/default. Compatible with the FK KEY SHARE taken by clones.
        var workspace = await dbContext.Workspaces.FromSqlInterpolated(
            $"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR NO KEY UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.ArchivedAt is not null)
            return new(Error: "The current workspace is no longer active.");

        var pipelineId = GetEmploymentPipelineId(workspaceId);
        var existing = await dbContext.Pipelines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pipelineId, cancellationToken);
        if (existing is not null)
        {
            if (existing.WorkspaceId != workspaceId)
                return new(Error: "The reserved initial pipeline identifier is already in use.");
            // Identity, not mutable template fields, records initialization. Never repair or reset it.
            var dto = await pipelineService.GetByIdAsync(pipelineId, includeArchivedStages: true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(dto);
        }
        if (await dbContext.Pipelines.AnyAsync(x => x.WorkspaceId == workspaceId && x.Name == InitialName, cancellationToken))
            return new(Error: "A pipeline named 'Emploi .NET' already exists but was not created by this initializer. Rename it explicitly before initializing; no existing pipeline was adopted or changed.");

        var stages = new (string Name, string Category)[]
        {
            ("À analyser", "active"), ("À candidater", "active"), ("Candidature envoyée", "active"),
            ("Entretien", "active"), ("Offre", "success"), ("Refusé", "failure"), ("Abandonné", "failure")
        };
        dbContext.Pipelines.Add(new Pipeline
        {
            Id = pipelineId, WorkspaceId = workspaceId, Name = InitialName, TypeCode = "employment", IsVisible = true,
            Description = "Pipeline de prospection pour les offres d'emploi .NET / C#, principalement autour de Bordeaux et en remote France/Europe.",
            Stages = stages.Select((stage, position) => new PipelineStage
            {
                Name = stage.Name, CategoryCode = stage.Category, SortOrder = position
            }).ToList()
        });
        // Persist the principal first: the workspace's composite default FK is immediate.
        await dbContext.SaveChangesAsync(cancellationToken);
        if (workspace.DefaultPipelineId is null)
        {
            workspace.DefaultPipelineId = pipelineId;
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        var created = await pipelineService.GetByIdAsync(pipelineId, includeArchivedStages: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(created, Created: true);
    }

    // Permanent UUIDv8 identity convention. Never change this key when editing the initial template.
    // Names, archive flags, stages and the selected default are deliberately not part of the key.
    private static Guid GetEmploymentPipelineId(Guid workspaceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"HellzDraft/ProspectionCrm/initial-pipelines/employment-dotnet/{workspaceId:D}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
