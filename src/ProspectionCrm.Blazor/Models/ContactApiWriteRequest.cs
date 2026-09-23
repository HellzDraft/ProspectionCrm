namespace ProspectionCrm.Blazor.Models;

public class ContactApiWriteRequest
{
    public Guid? CompanyId { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? JobTitle { get; set; }

    public string? LinkedInUrl { get; set; }

    public static ContactApiWriteRequest FromForm(ContactApiFormModel model) => new()
    {
        CompanyId = model.CompanyId,
        FirstName = model.FirstName,
        LastName = model.LastName,
        Email = model.Email,
        Phone = model.Phone,
        JobTitle = model.JobTitle,
        LinkedInUrl = model.LinkedInUrl
    };
}
