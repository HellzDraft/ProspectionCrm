using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.CandidateProfiles;

public class ProfileItemOrderRequest
{
    [Range(0, int.MaxValue)]
    public int SortOrder { get; set; }
}
