using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Documents;

public class CreateDocumentRequest
{
    [Required]
    [MaxLength(50)]
    public string KindCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string StorageKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [MaxLength(64)]
    public string? Sha256 { get; set; }
}
