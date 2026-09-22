using System.Net;
using System.Net.Http.Json;
using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class OpportunityApiService(HttpClient httpClient)
{
    public async Task<List<OpportunityApiDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<OpportunityApiDto>>(
            "api/opportunities", cancellationToken) ?? [];

    public async Task<OpportunityApiDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/opportunities/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OpportunityApiDto>(cancellationToken);
    }

    public async Task<OpportunityApiDto> CreateAsync(
        OpportunityApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/opportunities", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OpportunityApiDto>(cancellationToken)
            ?? throw new InvalidOperationException("La réponse de création est vide.");
    }

    public async Task<bool> UpdateAsync(
        Guid id, OpportunityApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync($"api/opportunities/{id}", request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
