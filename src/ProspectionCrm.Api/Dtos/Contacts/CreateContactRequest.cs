using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Contacts;

public class CreateContactRequest
{
    public Guid? CompanyId { get; set; }

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(254)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? JobTitle { get; set; }

    [MaxLength(2048)]
    public string? LinkedInUrl { get; set; }
}
