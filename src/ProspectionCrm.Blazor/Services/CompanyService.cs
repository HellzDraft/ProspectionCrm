namespace ProspectionCrm.Blazor.Services;

using ProspectionCrm.Blazor.Models;

public class CompanyService
{
	private readonly List<CompanyDto> companies =
	[
		new()
		{
			Id = 1,
			Name = "TechNova",
			Type = "tech",
			Industry = "tech",
			Location = "Bordeaux",
			Website = "TechNova.com",
			Notes = "secteur tech"
		},
		new()
		{
			Id = 2,
			Name = "Red Fox Studio",
			Type = "tech",
			Industry = "tech",
			Location = "Bordeaux",
			Website = "RFS.com",
			Notes = "secteur tech"
		},
		new()
		{
			Id = 3,
			Name = "Malt",
			Type = "tech",
			Industry = "tech",
			Location = "Bordeaux",
			Website = "Malt.com",
			Notes = "secteur tech"
		},
		new()
		{
			Id = 4,
			Name = "North Star Publishing",
			Type = "tech",
			Industry = "tech",
			Location = "Bordeaux",
			Website = "North Star Publishing.com",
			Notes = "secteur tech"
		}
	];

	public async Task<List<CompanyDto>> GetAllAsync()
	{
		await Task.Delay(500);

		return companies;
	}

	public async Task<CompanyDto?> GetByIdAsync(int id)
	{
		await Task.Delay(500);

		return companies.FirstOrDefault(company => company.Id  == id);
	}
}
