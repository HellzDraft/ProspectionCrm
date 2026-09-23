using System.Net.Http.Json;
using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class PipelineApiService(HttpClient httpClient)
{
    public async Task<List<PipelineApiDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<PipelineApiDto>>(
            $"api/pipelines?includeArchived={includeArchived.ToString().ToLowerInvariant()}", cancellationToken) ?? [];
}
