using System.Net;
using System.Net.Http.Json;
using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class ContactApiService(HttpClient httpClient)
{
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"api/contacts/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<List<ContactApiDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<ContactApiDto>>(
            "api/contacts", cancellationToken) ?? [];

    public async Task<ContactApiDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/contacts/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContactApiDto>(cancellationToken);
    }

    public async Task<ContactApiDto> CreateAsync(
        ContactApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/contacts", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContactApiDto>(cancellationToken)
            ?? throw new InvalidOperationException("La réponse de création est vide.");
    }

    public async Task<bool> UpdateAsync(
        Guid id, ContactApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync($"api/contacts/{id}", request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
