using System.Net;
using System.Net.Http.Json;
using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class CompanyApiService(HttpClient httpClient)
{
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"api/companies/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<List<CompanyApiDto>> GetAllAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<List<CompanyApiDto>>(
            "api/companies", cancellationToken) ?? [];

    public async Task<CompanyApiDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/companies/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CompanyApiDto>(cancellationToken);
    }

    public async Task<CompanyApiDto> CreateAsync(
        CompanyApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/companies", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CompanyApiDto>(cancellationToken)
            ?? throw new InvalidOperationException("La réponse de création est vide.");
    }

    public async Task<bool> UpdateAsync(
        Guid id, CompanyApiWriteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync($"api/companies/{id}", request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
