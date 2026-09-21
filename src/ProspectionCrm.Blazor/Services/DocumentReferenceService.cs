namespace ProspectionCrm.Blazor.Services;

using ProspectionCrm.Blazor.Models;

public class DocumentReferenceService
{
	private readonly List<DocumentReferenceDto> documents =
		[
			new()
			{
				Id = 1,
				Name = "Foo",
				Type = "CV",
				Location = "F:\\Documents\\CV.NET.pdf",
				OpportunityId = 1,
				OpportunityTitle = "Développeur .NET",
				Notes = "test"
			},
			new()
			{
				Id = 2,
				Name = "Foo2",
				Type = "CV",
				Location = "F:\\Documents\\CV.Unity.pdf",
				OpportunityId = 2,
				OpportunityTitle = "Développeur Unity",
				Notes = "test"
			},
			new()
			{
				Id = 3,
				Name = "Foo3",
				Type = "Présentation",
				Location = "Google Drive",
				OpportunityId = 3,
				OpportunityTitle = "Mission API ASP.NET Core",
				Notes = "test"
			},
			new()
			{
				Id = 4,
				Name = "Foo4",
				Type = "Présentation",
				Location = "Google Drive",
				OpportunityId = 4,
				OpportunityTitle = "Présentation CrewRats",
				Notes = "test"
			}
		];

	public async Task<List<DocumentReferenceDto>> GetAllAsync()
	{
		await Task.Delay(500);

		return documents;
	}
}
