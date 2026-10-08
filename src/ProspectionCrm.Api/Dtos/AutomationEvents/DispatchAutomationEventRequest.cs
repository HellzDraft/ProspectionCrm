using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.AutomationEvents;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DispatchAutomationEventRequest
{
    [Required, MaxLength(50)]
    public string TriggerTypeCode { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string EventKey { get; set; } = string.Empty;
    public Guid? PipelineId { get; set; }
    public JsonElement Payload { get; set; } = JsonSerializer.SerializeToElement(new { });
}
