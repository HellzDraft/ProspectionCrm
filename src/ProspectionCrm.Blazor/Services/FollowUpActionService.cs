namespace ProspectionCrm.Blazor.Services;

using ProspectionCrm.Blazor.Models;

public class FollowUpActionService
{
	private readonly List<FollowUpActionDto> followUpActions =
		[
			new()
			{
				Id = 1,
				Title = "Test",
				DueDate = DateTime.Today.AddDays(-2),
				IsCompleted = false,
				OpportunityId = 1,
				OpportunityTitle = "Développeur .NET",
				Notes = "Demander où en est la candidature."
			},
			new()
			{
				Id = 2,
				Title = "Test2",
				DueDate =  DateTime.Today,
				IsCompleted = false,
				OpportunityId = 2,
				OpportunityTitle = "Développeur Unity",
				Notes = "Passer l'entretien"
			},
			new()
			{
				Id = 3,
				Title = "Test3",
				DueDate = DateTime.Today.AddDays(3),
				IsCompleted = false,
				OpportunityId = 3,
				OpportunityTitle = "Mission API ASP.NET Core",
				Notes = "Envoyer le projet"
			},
			new()
			{
				Id = 4,
				Title = "Test4",
				DueDate = DateTime.Today.AddDays(-5),
				IsCompleted = true,
				OpportunityId = 4,
				OpportunityTitle = "Présentation CrewRats",
				Notes = "Projet presenté"
			}
		];

	public async Task<List<FollowUpActionDto>> GetAllAsync()
	{
		await Task.Delay(500);

		return followUpActions;
	}

	public async Task UpdateCompletionAsync(int id, bool isCompleted)
	{
		await Task.Delay(500);

		var action = followUpActions.FirstOrDefault(action => action.Id == id);

		if (action is not null)
		{
			action.IsCompleted = isCompleted;
		}
	}

	public async Task AddAsync(FollowUpActionDto action)
	{
		await Task.Delay(500);

		action.Id = followUpActions.Count == 0
			? 1
			: followUpActions.Max(item => item.Id) + 1;

		followUpActions.Add(action);
	}

	public async Task<List<FollowUpActionDto>> GetByOpportunityIdAsync(int opportunityId)
	{
		await Task.Delay(500);

		return followUpActions
			.Where(action => action.OpportunityId == opportunityId)
			.ToList();
	}
}
