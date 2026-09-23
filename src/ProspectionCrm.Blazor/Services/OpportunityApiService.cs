using System.Net;
using System.Net.Http.Json;
using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class OpportunityApiService(HttpClient httpClient)
{
    public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
        => SetArchivedAsync(id, "archive", cancellationToken);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default)
        => SetArchivedAsync(id, "restore", cancellationToken);

    private async Task<bool> SetArchivedAsync(Guid id, string operation, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/opportunities/{id}/{operation}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<List<OpportunityApiDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<OpportunityApiDto>>(
            $"api/opportunities?includeArchived={includeArchived.ToString().ToLowerInvariant()}", cancellationToken) ?? [];

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
