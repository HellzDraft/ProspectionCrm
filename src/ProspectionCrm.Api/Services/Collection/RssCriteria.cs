using System.Text.Json;

namespace ProspectionCrm.Api.Services.Collection;

public sealed record RssCriteria(int MaxItems, string? DefaultCompanyName)
{
    public static RssCriteria Parse(string json, int defaultMaxItems)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var max = defaultMaxItems;
            string? company = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                switch (property.Name)
                {
                    case "maxItems":
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out max)
                            || max is < 1 or > 100) throw Invalid();
                        break;
                    case "defaultCompanyName":
                        if (property.Value.ValueKind != JsonValueKind.String) throw Invalid();
                        company = property.Value.GetString()!.Trim();
                        if (company.Length is 0 or > 200) throw Invalid();
                        break;
                    default: throw Invalid();
                }
            }
            return new(max, company);
        }
        catch (JsonException) { throw Invalid(); }
    }
    private static SourceCollectionException Invalid() => new(new("InvalidRssCriteria", 409));
}
