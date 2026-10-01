namespace ProspectionCrm.Api.Dtos.Pipelines;

// Portable configuration only. Required members distinguish missing values from false/zero.
public sealed class PipelineTransferDocument
{
    public required int SchemaVersion { get; init; }
    public required PipelineTransferConfiguration? Pipeline { get; init; }
}

public sealed class PipelineTransferConfiguration
{
    public required string? Name { get; init; }
    public required string? TypeCode { get; init; }
    public string? Description { get; init; }
    public required bool IsVisible { get; init; }
    public required PipelineTransferStage?[]? Stages { get; init; }
}

public sealed class PipelineTransferStage
{
    public required string? Name { get; init; }
    public string? Description { get; init; }
    public required string? CategoryCode { get; init; }
}
