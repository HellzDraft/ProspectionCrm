using System.Net.Http.Json;
using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class OpportunityApiService(HttpClient httpClient)
{
    public async Task<List<OpportunityApiDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<OpportunityApiDto>>(
            "api/opportunities", cancellationToken) ?? [];
}
