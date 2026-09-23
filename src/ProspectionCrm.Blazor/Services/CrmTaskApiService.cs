using System.Net;
using System.Net.Http.Json;
using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class CrmTaskApiService(HttpClient httpClient)
{
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"api/crm-tasks/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<List<CrmTaskApiDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<CrmTaskApiDto>>(
            "api/crm-tasks", cancellationToken) ?? [];

    public async Task<CrmTaskApiDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/crm-tasks/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CrmTaskApiDto>(cancellationToken);
    }

    public async Task<CrmTaskApiDto> CreateAsync(
        CrmTaskApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/crm-tasks", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CrmTaskApiDto>(cancellationToken)
            ?? throw new InvalidOperationException("La réponse de création est vide.");
    }

    public async Task<bool> UpdateAsync(
        Guid id, CrmTaskApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync($"api/crm-tasks/{id}", request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<List<CrmTaskApiDto>> GetDueAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<CrmTaskApiDto>>(
            "api/crm-tasks/due", cancellationToken) ?? [];

    public Task<bool> CompleteAsync(Guid id, CancellationToken cancellationToken = default)
        => SetCompletionAsync(id, "complete", cancellationToken);

    public Task<bool> ReopenAsync(Guid id, CancellationToken cancellationToken = default)
        => SetCompletionAsync(id, "reopen", cancellationToken);

    private async Task<bool> SetCompletionAsync(Guid id, string operation, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"api/crm-tasks/{id}/{operation}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
