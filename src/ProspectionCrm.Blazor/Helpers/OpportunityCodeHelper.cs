using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Helpers;

public static class OpportunityCodeHelper
{
    public static string GetPipelineCode(PipelineType pipeline) => pipeline == PipelineType.DotNet
        ? "dotnet" : ToKebabCase(pipeline.ToString());
    public static string GetStatusCode(OpportunityStatus status) => ToKebabCase(status.ToString());
    public static string GetPriorityCode(OpportunityPriority priority) => ToKebabCase(priority.ToString());

    private static string ToKebabCase(string value) =>
        string.Concat(value.Select((c, i) => char.IsUpper(c) && i > 0
            ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    public static PipelineType? GetPipeline(string code) => Match<PipelineType>(code);
    public static OpportunityStatus? GetStatus(string code) => Match<OpportunityStatus>(code);
    public static OpportunityPriority? GetPriority(string code) => Match<OpportunityPriority>(code);

    public static string GetPipelineLabel(string code) => GetPipeline(code) is { } value
        ? OpportunityDisplayHelper.GetPipelineLabel(value) : code;
    public static string GetStatusLabel(string code) => GetStatus(code) is { } value
        ? OpportunityDisplayHelper.GetStatusLabel(value) : code;
    public static string GetPriorityLabel(string code) => GetPriority(code) is { } value
        ? OpportunityDisplayHelper.GetPriorityLabel(value) : code;

    public static string GetStatusKey(string code) => GetStatus(code)?.ToString() ?? code;

    public static string GetCompanyLabel(Guid? companyId) =>
        companyId.HasValue ? $"ID entreprise : {companyId.Value}" : "—";

    private static T? Match<T>(string code) where T : struct, Enum
    {
        var normalized = Normalize(code);
        foreach (var value in Enum.GetValues<T>())
        {
            if (string.Equals(Normalize(value.ToString()), normalized, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    private static string Normalize(string code) =>
        new(code.Where(c => c != '-' && c != '_' && !char.IsWhiteSpace(c)).ToArray());
}
