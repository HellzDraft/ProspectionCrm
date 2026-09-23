using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Companies;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class CompanyService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : ICompanyService
{
    public async Task<IReadOnlyList<CompanyDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var companies = await dbContext.Companies.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return companies.Select(ToDto).ToList();
    }

    public async Task<CompanyDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var company = await dbContext.Companies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return company is null ? null : ToDto(company);
    }

    public async Task<CompanyDto> CreateAsync(
        CreateCompanyRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var company = new Company
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Name = request.Name,
            Website = request.Website,
            Location = request.Location,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = null
        };

        dbContext.Companies.Add(company);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(company);
    }

    public async Task<bool> UpdateAsync(
        Guid id, UpdateCompanyRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var company = await dbContext.Companies
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (company is null)
            return false;

        company.Name = request.Name;
        company.Website = request.Website;
        company.Location = request.Location;
        company.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var company = await dbContext.Companies
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (company is null)
            return false;

        dbContext.Companies.Remove(company);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static CompanyDto ToDto(Company company) => new()
    {
        Id = company.Id,
        Name = company.Name,
        Website = company.Website,
        Location = company.Location,
        CreatedAt = company.CreatedAt,
        UpdatedAt = company.UpdatedAt,
    };
}
