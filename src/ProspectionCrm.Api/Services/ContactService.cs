using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Contacts;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class ContactService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IContactService
{
    public async Task<IReadOnlyList<ContactDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var contacts = await dbContext.Contacts.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return contacts.Select(ToDto).ToList();
    }

    public async Task<ContactDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var contact = await dbContext.Contacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return contact is null ? null : ToDto(contact);
    }

    public async Task<(ContactDto? Contact, string? Error)> CreateAsync(
        CreateContactRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = await ValidateCompanyAsync(request.CompanyId, workspaceId, cancellationToken);
        if (error is not null)
            return (null, error);

        var contact = new Contact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            CompanyId = request.CompanyId,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Phone = request.Phone,
            JobTitle = request.JobTitle,
            LinkedInUrl = request.LinkedInUrl,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = null
        };

        dbContext.Contacts.Add(contact);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(contact), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(
        Guid id, UpdateContactRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var contact = await dbContext.Contacts
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (contact is null)
            return (false, null);

        var error = await ValidateCompanyAsync(request.CompanyId, workspaceId, cancellationToken);
        if (error is not null)
            return (true, error);

        contact.CompanyId = request.CompanyId;
        contact.FirstName = request.FirstName;
        contact.LastName = request.LastName;
        contact.Email = request.Email;
        contact.Phone = request.Phone;
        contact.JobTitle = request.JobTitle;
        contact.LinkedInUrl = request.LinkedInUrl;
        contact.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var contact = await dbContext.Contacts
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (contact is null)
            return false;

        dbContext.Contacts.Remove(contact);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<string?> ValidateCompanyAsync(Guid? companyId, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (companyId.HasValue &&
            !await dbContext.Companies.AnyAsync(x => x.Id == companyId.Value && x.WorkspaceId == workspaceId, cancellationToken))
            return "CompanyId does not reference a company in the current workspace.";

        return null;
    }

    private static ContactDto ToDto(Contact contact) => new()
    {
        Id = contact.Id,
        CompanyId = contact.CompanyId,
        FirstName = contact.FirstName,
        LastName = contact.LastName,
        Email = contact.Email,
        Phone = contact.Phone,
        JobTitle = contact.JobTitle,
        LinkedInUrl = contact.LinkedInUrl,
        CreatedAt = contact.CreatedAt,
        UpdatedAt = contact.UpdatedAt,
    };
}
