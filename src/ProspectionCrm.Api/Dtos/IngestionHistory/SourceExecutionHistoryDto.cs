using System.Text.Json;
using ProspectionCrm.Api.Dtos.SourceExecutions;

namespace ProspectionCrm.Api.Dtos.IngestionHistory;

public sealed record SourceExecutionHistoryDto(SourceExecutionDto Execution, JsonElement? ContextSnapshot, string? UnavailableReason);
