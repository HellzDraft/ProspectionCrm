using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public sealed class InitialPipelineService(
    ProspectionCrmDbContext dbContext,
    ICurrentWorkspaceProvider currentWorkspaceProvider,
    IPipelineService pipelineService) : IInitialPipelineService
{
    private sealed record Template(string Key, string Name, string TypeCode, string Description,
        (string Name, string Category)[] Stages);

    // Permanent identity keys, independent of mutable template content. Employment stays first.
    private static readonly Template[] Templates =
    [
        new("employment-dotnet", "Emploi .NET", "employment",
            "Pipeline de prospection pour les offres d'emploi .NET / C#, principalement autour de Bordeaux et en remote France/Europe.",
            [("À analyser", "active"), ("À candidater", "active"), ("Candidature envoyée", "active"),
             ("Entretien", "active"), ("Offre", "success"), ("Refusé", "failure"), ("Abandonné", "failure")]),
        new("freelance-malt", "Freelance / Malt", "freelance",
            "Pipeline de prospection pour les missions freelance C# / .NET / ASP.NET Core et Unity, principalement en remote ou autour de Bordeaux.",
            [("À analyser", "active"), ("À contacter", "active"), ("Proposition envoyée", "active"),
             ("Échange client", "active"), ("Mission gagnée", "success"), ("Refusée / perdue", "failure"), ("Abandonnée", "failure")]),
        new("employment-game-dev", "Emploi Jeu Vidéo", "employment",
            "Pipeline de prospection pour les offres d'emploi jeu vidéo Unity / C#, principalement en France ou en remote Europe.",
            [("À analyser", "active"), ("À candidater", "active"), ("Candidature envoyée", "active"),
             ("Entretien", "active"), ("Offre", "success"), ("Refusé", "failure"), ("Abandonné", "failure")]),
        new("business-game-dev", "Business Jeu Vidéo", "business",
            "Pipeline de prospection business pour les studios, éditeurs, partenaires et structures d'accompagnement du jeu vidéo, notamment autour de CrewRats et des outils développés.",
            [("Cible identifiée", "active"), ("À contacter", "active"), ("Contacté", "active"),
             ("Échange en cours", "active"), ("Opportunité concrète", "active"), ("Accord / partenariat", "success"), ("Sans suite", "failure")])
    ];

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

        var initial = Templates.Select(template => (Template: template, Id: GetPipelineId(workspaceId, template.Key))).ToArray();
        var missing = new List<(Template Template, Guid Id)>();
        // Validate all identities and conflicts before tracking or inserting any new configuration.
        foreach (var item in initial)
        {
            var existing = await dbContext.Pipelines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
            if (existing is not null)
            {
                if (existing.WorkspaceId != workspaceId)
                    return new(Error: "The reserved initial pipeline identifier is already in use.");
                // Identity records initialization. Never repair mutable fields, stages or archives.
                continue;
            }
            if (await dbContext.Pipelines.AnyAsync(x => x.WorkspaceId == workspaceId && x.Name == item.Template.Name, cancellationToken))
                return new(Error: $"A pipeline named '{item.Template.Name}' already exists but was not created by this initializer. Rename it explicitly before initializing; no existing pipeline was adopted or changed.");
            missing.Add(item);
        }

        foreach (var item in missing)
        {
            dbContext.Pipelines.Add(new Pipeline
            {
                Id = item.Id, WorkspaceId = workspaceId, Name = item.Template.Name,
                TypeCode = item.Template.TypeCode, IsVisible = true, Description = item.Template.Description,
                Stages = item.Template.Stages.Select((stage, position) => new PipelineStage
                {
                    Name = stage.Name, CategoryCode = stage.Category, SortOrder = position
                }).ToList()
            });
        }
        if (missing.Count > 0)
        {
            // Persist principals first: the workspace's composite default FK is immediate.
            await dbContext.SaveChangesAsync(cancellationToken);
            // A previous initialization (including Phase 5.2) permanently leaves default choice to the user.
            if (missing.Count == initial.Length && workspace.DefaultPipelineId is null)
            {
                workspace.DefaultPipelineId = initial[0].Id;
                workspace.UpdatedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        var pipelines = new List<PipelineDto>();
        foreach (var item in initial)
            pipelines.Add((await pipelineService.GetByIdAsync(item.Id, includeArchivedStages: true, cancellationToken))!);
        await transaction.CommitAsync(cancellationToken);
        return new(new(pipelines, missing.Select(item => item.Id).ToArray()));
    }

    // Permanent UUIDv8 identity convention. Never change this key when editing the initial template.
    // Names, archive flags, stages and the selected default are deliberately not part of the key.
    private static Guid GetPipelineId(Guid workspaceId, string templateKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"HellzDraft/ProspectionCrm/initial-pipelines/{templateKey}/{workspaceId:D}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
