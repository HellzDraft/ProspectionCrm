namespace ProspectionCrm.Blazor.Models;

public class CompanyApiWriteRequest
{
    public string Name { get; set; } = string.Empty;

    public string? Website { get; set; }

    public string? Location { get; set; }

    public static CompanyApiWriteRequest FromForm(CompanyApiFormModel model) => new()
    {
        Name = model.Name,
        Website = model.Website,
        Location = model.Location
    };
}
