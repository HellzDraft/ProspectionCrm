using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CandidateProfiles;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class CandidateProfileService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : ICandidateProfileService
{
    private IQueryable<CandidateProfile> ReadProfiles(Guid workspaceId)
        => dbContext.CandidateProfiles.AsNoTracking().Where(x => x.WorkspaceId == workspaceId)
            .Include(x => x.Experiences.Where(link => link.Experience.WorkspaceId == workspaceId))
            .Include(x => x.Educations.Where(link => link.Education.WorkspaceId == workspaceId))
            .Include(x => x.Projects.Where(link => link.Project.WorkspaceId == workspaceId))
            .Include(x => x.Skills.Where(link => link.Skill.WorkspaceId == workspaceId))
            .AsSplitQuery();

    public async Task<IReadOnlyList<CandidateProfileDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profiles = await ReadProfiles(workspaceId).Where(x => includeArchived || x.ArchivedAt == null)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return profiles.Select(ToDto).ToList();
    }

    public async Task<CandidateProfileDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await ReadProfiles(workspaceId).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return profile is null ? null : ToDto(profile);
    }

    public async Task<(CandidateProfileDto? Profile, string? Error)> CreateAsync(CreateCandidateProfileRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = await ProfessionalReferenceValidation.DocumentAsync(dbContext, workspaceId, request.PrimaryCvDocumentId,
            "cv", true, nameof(request.PrimaryCvDocumentId), cancellationToken);
        if (error is not null)
            return (null, error);
        var profile = new CandidateProfile
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Description = request.Description,
            PrimaryCvDocumentId = request.PrimaryCvDocumentId,
            IsDefault = request.IsDefault
        };
        error = await SaveProfileAsync(profile, request.IsDefault, () => dbContext.CandidateProfiles.Add(profile), cancellationToken);
        return error is null ? (ToDto(profile), null) : (null, error);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateCandidateProfileRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await dbContext.CandidateProfiles.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (profile is null)
            return (false, null);
        var error = await ProfessionalReferenceValidation.DocumentAsync(dbContext, workspaceId, request.PrimaryCvDocumentId,
            "cv", request.PrimaryCvDocumentId != profile.PrimaryCvDocumentId, nameof(request.PrimaryCvDocumentId), cancellationToken);
        if (error is not null)
            return (true, error);
        error = await SaveProfileAsync(profile, request.IsDefault && profile.ArchivedAt == null, () =>
        {
            profile.Name = request.Name;
            profile.Description = request.Description;
            profile.PrimaryCvDocumentId = request.PrimaryCvDocumentId;
            profile.IsDefault = request.IsDefault;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        return (true, error);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await dbContext.CandidateProfiles.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (profile is null)
            return false;
        var now = DateTimeOffset.UtcNow;
        profile.ArchivedAt = now;
        profile.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await dbContext.CandidateProfiles.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (profile is null)
            return (false, null);
        var error = await ProfessionalReferenceValidation.DocumentAsync(dbContext, workspaceId, profile.PrimaryCvDocumentId,
            "cv", true, nameof(profile.PrimaryCvDocumentId), cancellationToken);
        if (error is not null)
            return (true, error);
        error = await SaveProfileAsync(profile, profile.IsDefault, () =>
        {
            profile.ArchivedAt = null;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        return (true, error);
    }

    private async Task<string?> SaveProfileAsync(CandidateProfile profile, bool makeActiveDefault,
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
            var otherDefaults = await dbContext.CandidateProfiles.Where(x => x.WorkspaceId == profile.WorkspaceId
                && x.Id != profile.Id && x.ArchivedAt == null && x.IsDefault).ToListAsync(cancellationToken);
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
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_CandidateProfiles_WorkspaceId" })
        {
            await transaction.RollbackAsync(cancellationToken);
            return "The default candidate profile changed concurrently. Please retry.";
        }
    }

    public async Task<(bool Found, string? Error)> PutExperienceAsync(Guid profileId, Guid experienceId, int sortOrder, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await dbContext.CandidateProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken);
        var target = await dbContext.Experiences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == experienceId && x.WorkspaceId == workspaceId, cancellationToken);
        if (profile is null || target is null)
            return (false, null);
        if (profile.ArchivedAt.HasValue)
            return (true, "Cannot change memberships of an archived candidate profile.");
        if (sortOrder < 0)
            return (true, "SortOrder must be greater than or equal to zero.");
        var link = await dbContext.CandidateProfileExperiences.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.ExperienceId == experienceId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Experience.WorkspaceId == workspaceId, cancellationToken);
        if (link is null)
        {
            link = new CandidateProfileExperience { CandidateProfileId = profileId, ExperienceId = experienceId, SortOrder = sortOrder };
            dbContext.CandidateProfileExperiences.Add(link);
        }
        else
        {
            link.SortOrder = sortOrder;
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_ProfileExperiences_Profile_Target" })
        {
            // Another request inserted the same membership; apply the requested order to that row.
            dbContext.Entry(link).State = EntityState.Detached;
            var existing = await dbContext.CandidateProfileExperiences.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
                && x.ExperienceId == experienceId && x.CandidateProfile.WorkspaceId == workspaceId
                && x.Experience.WorkspaceId == workspaceId, cancellationToken);
            if (existing is null)
                return (true, "The membership changed concurrently. Please retry.");
            if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId
                    && x.ArchivedAt == null, cancellationToken))
                return (true, "Cannot change memberships of an archived candidate profile.");
            existing.SortOrder = sortOrder;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return (true, null);
    }

    public async Task<bool> RemoveExperienceAsync(Guid profileId, Guid experienceId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken)
            || !await dbContext.Experiences.AnyAsync(x => x.Id == experienceId && x.WorkspaceId == workspaceId, cancellationToken))
            return false;
        var link = await dbContext.CandidateProfileExperiences.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.ExperienceId == experienceId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Experience.WorkspaceId == workspaceId, cancellationToken);
        if (link is not null)
        {
            dbContext.CandidateProfileExperiences.Remove(link);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<(bool Found, string? Error)> PutEducationAsync(Guid profileId, Guid educationId, int sortOrder, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await dbContext.CandidateProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken);
        var target = await dbContext.Educations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == educationId && x.WorkspaceId == workspaceId, cancellationToken);
        if (profile is null || target is null)
            return (false, null);
        if (profile.ArchivedAt.HasValue)
            return (true, "Cannot change memberships of an archived candidate profile.");
        if (sortOrder < 0)
            return (true, "SortOrder must be greater than or equal to zero.");
        var link = await dbContext.CandidateProfileEducations.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.EducationId == educationId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Education.WorkspaceId == workspaceId, cancellationToken);
        if (link is null)
        {
            link = new CandidateProfileEducation { CandidateProfileId = profileId, EducationId = educationId, SortOrder = sortOrder };
            dbContext.CandidateProfileEducations.Add(link);
        }
        else
        {
            link.SortOrder = sortOrder;
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_ProfileEducations_Profile_Target" })
        {
            // Another request inserted the same membership; apply the requested order to that row.
            dbContext.Entry(link).State = EntityState.Detached;
            var existing = await dbContext.CandidateProfileEducations.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
                && x.EducationId == educationId && x.CandidateProfile.WorkspaceId == workspaceId
                && x.Education.WorkspaceId == workspaceId, cancellationToken);
            if (existing is null)
                return (true, "The membership changed concurrently. Please retry.");
            if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId
                    && x.ArchivedAt == null, cancellationToken))
                return (true, "Cannot change memberships of an archived candidate profile.");
            existing.SortOrder = sortOrder;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return (true, null);
    }

    public async Task<bool> RemoveEducationAsync(Guid profileId, Guid educationId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken)
            || !await dbContext.Educations.AnyAsync(x => x.Id == educationId && x.WorkspaceId == workspaceId, cancellationToken))
            return false;
        var link = await dbContext.CandidateProfileEducations.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.EducationId == educationId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Education.WorkspaceId == workspaceId, cancellationToken);
        if (link is not null)
        {
            dbContext.CandidateProfileEducations.Remove(link);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<(bool Found, string? Error)> PutProjectAsync(Guid profileId, Guid projectId, int sortOrder, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await dbContext.CandidateProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken);
        var target = await dbContext.Projects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == projectId && x.WorkspaceId == workspaceId, cancellationToken);
        if (profile is null || target is null)
            return (false, null);
        if (profile.ArchivedAt.HasValue)
            return (true, "Cannot change memberships of an archived candidate profile.");
        if (sortOrder < 0)
            return (true, "SortOrder must be greater than or equal to zero.");
        var link = await dbContext.CandidateProfileProjects.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.ProjectId == projectId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Project.WorkspaceId == workspaceId, cancellationToken);
        if (link is null)
        {
            if (target.ArchivedAt.HasValue)
                return (true, "Cannot add an archived project to a candidate profile.");
            link = new CandidateProfileProject { CandidateProfileId = profileId, ProjectId = projectId, SortOrder = sortOrder };
            dbContext.CandidateProfileProjects.Add(link);
        }
        else
        {
            link.SortOrder = sortOrder;
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_ProfileProjects_Profile_Target" })
        {
            // Another request inserted the same membership; apply the requested order to that row.
            dbContext.Entry(link).State = EntityState.Detached;
            var existing = await dbContext.CandidateProfileProjects.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
                && x.ProjectId == projectId && x.CandidateProfile.WorkspaceId == workspaceId
                && x.Project.WorkspaceId == workspaceId, cancellationToken);
            if (existing is null)
                return (true, "The membership changed concurrently. Please retry.");
            if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId
                    && x.ArchivedAt == null, cancellationToken))
                return (true, "Cannot change memberships of an archived candidate profile.");
            existing.SortOrder = sortOrder;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return (true, null);
    }

    public async Task<bool> RemoveProjectAsync(Guid profileId, Guid projectId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken)
            || !await dbContext.Projects.AnyAsync(x => x.Id == projectId && x.WorkspaceId == workspaceId, cancellationToken))
            return false;
        var link = await dbContext.CandidateProfileProjects.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.ProjectId == projectId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Project.WorkspaceId == workspaceId, cancellationToken);
        if (link is not null)
        {
            dbContext.CandidateProfileProjects.Remove(link);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<(bool Found, string? Error)> PutSkillAsync(Guid profileId, Guid skillId, int sortOrder, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await dbContext.CandidateProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken);
        var target = await dbContext.Skills.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == skillId && x.WorkspaceId == workspaceId, cancellationToken);
        if (profile is null || target is null)
            return (false, null);
        if (profile.ArchivedAt.HasValue)
            return (true, "Cannot change memberships of an archived candidate profile.");
        if (sortOrder < 0)
            return (true, "SortOrder must be greater than or equal to zero.");
        var link = await dbContext.CandidateProfileSkills.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.SkillId == skillId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Skill.WorkspaceId == workspaceId, cancellationToken);
        if (link is null)
        {
            link = new CandidateProfileSkill { CandidateProfileId = profileId, SkillId = skillId, SortOrder = sortOrder };
            dbContext.CandidateProfileSkills.Add(link);
        }
        else
        {
            link.SortOrder = sortOrder;
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_ProfileSkills_Profile_Target" })
        {
            // Another request inserted the same membership; apply the requested order to that row.
            dbContext.Entry(link).State = EntityState.Detached;
            var existing = await dbContext.CandidateProfileSkills.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
                && x.SkillId == skillId && x.CandidateProfile.WorkspaceId == workspaceId
                && x.Skill.WorkspaceId == workspaceId, cancellationToken);
            if (existing is null)
                return (true, "The membership changed concurrently. Please retry.");
            if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId
                    && x.ArchivedAt == null, cancellationToken))
                return (true, "Cannot change memberships of an archived candidate profile.");
            existing.SortOrder = sortOrder;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return (true, null);
    }

    public async Task<bool> RemoveSkillAsync(Guid profileId, Guid skillId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId && x.WorkspaceId == workspaceId, cancellationToken)
            || !await dbContext.Skills.AnyAsync(x => x.Id == skillId && x.WorkspaceId == workspaceId, cancellationToken))
            return false;
        var link = await dbContext.CandidateProfileSkills.SingleOrDefaultAsync(x => x.CandidateProfileId == profileId
            && x.SkillId == skillId && x.CandidateProfile.WorkspaceId == workspaceId
            && x.Skill.WorkspaceId == workspaceId, cancellationToken);
        if (link is not null)
        {
            dbContext.CandidateProfileSkills.Remove(link);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private static CandidateProfileDto ToDto(CandidateProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Description = profile.Description,
        PrimaryCvDocumentId = profile.PrimaryCvDocumentId,
        IsDefault = profile.IsDefault,
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt,
        ArchivedAt = profile.ArchivedAt,
        Experiences = profile.Experiences.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new CandidateProfileExperienceDto { Id = x.Id, ExperienceId = x.ExperienceId, SortOrder = x.SortOrder }).ToList(),
        Educations = profile.Educations.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new CandidateProfileEducationDto { Id = x.Id, EducationId = x.EducationId, SortOrder = x.SortOrder }).ToList(),
        Projects = profile.Projects.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new CandidateProfileProjectDto { Id = x.Id, ProjectId = x.ProjectId, SortOrder = x.SortOrder }).ToList(),
        Skills = profile.Skills.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new CandidateProfileSkillDto { Id = x.Id, SkillId = x.SkillId, SortOrder = x.SortOrder }).ToList()
    };
}
