using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Services;

public class OpportunityService
{
	private readonly List<OpportunityDto> opportunities =
	[
		new()
		{
			Id = 1,
			 CompanyId = 1,
			Title = "Développeur .NET",
			CompanyName = "TechNova",
			Pipeline = PipelineType.DotNet,
			Status = OpportunityStatus.ToApply,
			Priority = OpportunityPriority.High,
			Location = "Bordeaux"
		},
		new()
		{
			Id = 2,
			 CompanyId = 2,
			Title = "Développeur Unity",
			CompanyName = "Red Fox Studio",
			Pipeline = PipelineType.GameJob,
			Status = OpportunityStatus.ApplicationSent,
			Priority = OpportunityPriority.Normal,
			Location = "Remote"
		},
		new()
		{
			Id = 3,
			 CompanyId = 3,
			Title = "Mission API ASP.NET Core",
			CompanyName = "Malt",
			Pipeline = PipelineType.MaltFreelance,
			Status = OpportunityStatus.ToContact,
			Priority = OpportunityPriority.High,
			Location = "Remote"
		},
		new()
		{
			Id = 4,
			 CompanyId = 4,
			Title = "Présentation CrewRats",
			CompanyName = "North Star Publishing",
			Pipeline = PipelineType.GameBusiness,
			Status = OpportunityStatus.TargetIdentified,
			Priority = OpportunityPriority.Normal,
			Location = "France"
		}
	];

	public async Task<List<OpportunityDto>> GetAllAsync()
	{
		await Task.Delay(500);

		return opportunities;
	}

	public async Task<OpportunityDto?> GetByIdAsync(int id)
	{
		await Task.Delay(500);

		return opportunities.FirstOrDefault(opportunity => opportunity.Id == id);
	}

	public async Task AddAsync(OpportunityFormModel model)
	{
		await Task.Delay(500);

		var newOpportunity = new OpportunityDto
		{
			Id = opportunities.Max(opportunity => opportunity.Id) + 1,
			Title = model.Title,
			CompanyName = model.CompanyName,
			Pipeline = model.Pipeline.Value,
			Status = model.Status.Value,
			Priority = model.Priority,
			Location = model.Location
		};

		opportunities.Add(newOpportunity);
	}

	public async Task UpdateAsync(int id, OpportunityFormModel model)
	{
		await Task.Delay(500);

		var opportunity = opportunities
			.FirstOrDefault(opportunity => opportunity.Id == id);

		if (opportunity is null)
		{
			return;
		}

		opportunity.Title = model.Title;
		opportunity.CompanyName = model.CompanyName;
		opportunity.Pipeline = model.Pipeline.Value;
		opportunity.Status = model.Status.Value;
		opportunity.Priority = model.Priority;
		opportunity.Location = model.Location;
	}

	public async Task<bool> DeleteAsync(int id)
	{
		await Task.Delay(500);

		var opportunity = opportunities
			.FirstOrDefault(opportunity => opportunity.Id == id);

		if (opportunity is null)
		{
			return false;
		}

		opportunities.Remove(opportunity);

		return true;
	}

	public async Task UpdateStatusAsync(int id, OpportunityStatus status)
	{
		await Task.Delay(500);

		var opportunity = opportunities
			.FirstOrDefault(opportunity => opportunity.Id == id);

		if (opportunity is null)
		{
			return;
		}

		opportunity.Status = status;
	}

	public async Task<List<OpportunityDto>> GetByCompanyIdAsync(int companyId)
{
    await Task.Delay(500);

    return opportunities
        .Where(opportunity => opportunity.CompanyId == companyId)
        .ToList();
}
}