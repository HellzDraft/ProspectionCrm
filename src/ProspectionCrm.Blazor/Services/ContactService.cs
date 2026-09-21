namespace ProspectionCrm.Blazor.Services;

using ProspectionCrm.Blazor.Models;

public class ContactService
{
	private readonly List<ContactDto> contacts =
		[
			new()
			{
				Id = 1,
				FirstName = "Test1",
				LastName = "Test1",
				Role = "Test",
				CompanyId = 1,
				CompanyName = "TechNova",
				Email = "Test",
				Phone = "Test",
				LinkedInUrl = "Test",
				Notes = "Test",
			},
			new()
			{
				Id = 2,
				FirstName = "Test",
				LastName = "Test",
				Role = "Test",
				CompanyId = 2,
				CompanyName = "Red Fox Studio",
				Email = "Test",
				Phone = "Test",
				LinkedInUrl = "Test",
				Notes = "Test",
			},
			new()
			{
				Id = 3,
				FirstName = "Test",
				LastName = "Test",
				Role = "Test",
				CompanyId = 3,
				CompanyName = "Malt",
				Email = "Test",
				Phone = "Test",
				LinkedInUrl = "Test",
				Notes = "Test",
			},
			new()
			{
				Id = 4,
				FirstName = "Test",
				LastName = "Test",
				Role = "Test",
				CompanyId = 4,
				CompanyName = "North Star Publishing",
				Email = "Test",
				Phone = "Test",
				LinkedInUrl = "Test",
				Notes = "Test",
			},
		];

	public async Task<List<ContactDto>> GetAllAsync()
	{
		await Task.Delay(500);

		return contacts;
	}

	public async Task<ContactDto?> GetByIdAsync(int id)
	{
		await Task.Delay(500);

		return contacts.FirstOrDefault(contact => contact.Id  == id);
	}

	public async Task<List<ContactDto>> GetByCompanyIdAsync(int companyId)
	{
	 await Task.Delay(500);

		return contacts
		 .Where(contact  => contact.CompanyId == companyId)
			.ToList();
	}
}
