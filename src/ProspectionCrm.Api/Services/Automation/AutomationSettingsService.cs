using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationSettings;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record AutomationSettingsResult(AutomationSettingsDto? Value, string? Code = null, string? Detail = null, int Status = 200);

public sealed class AutomationSettingsService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspaceProvider,
    IAutomationSafetyPolicy policy, ILogger<AutomationSettingsService> logger)
{
    public async Task<AutomationSettingsResult> GetAsync(CancellationToken token) => await ReadOrUpdateAsync(null, token);
    public async Task<AutomationSettingsResult> UpdateAsync(UpdateAutomationSettingsRequest request, CancellationToken token)
    {
        if (!AutomationModes.IsValid(request.OperatingModeCode))
            return new(null, "InvalidOperatingMode", "OperatingModeCode must be manual, assist or automatic.", 400);
        if (request.MaxExecutionsPerMinute is < 1 or > 100 || request.MaxExecutionsPerDay is < 1 or > 10000
            || request.MaxConsecutiveFailures is < 1 or > 20 || request.MaxExecutionsPerDay < request.MaxExecutionsPerMinute)
            return new(null, "InvalidAutomationLimits", "Limits must be within bounds and the daily limit must be at least the per-minute limit.", 400);
        return await ReadOrUpdateAsync(request, token);
    }
    private async Task<AutomationSettingsResult> ReadOrUpdateAsync(UpdateAutomationSettingsRequest? request, CancellationToken token)
    {
        Guid workspace;
        try { workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return new(null, "WorkspaceUnavailable", "Exactly one active workspace is required.", 409); }
        var query = db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == workspace);
        var settings = await (request is null ? query.AsNoTracking() : query).SingleOrDefaultAsync(token);
        if (settings is null)
        {
            logger.LogError("Automation runtime settings missing for workspace {WorkspaceId}", workspace);
            return new(null, "AutomationSettingsMissing", "The current workspace automation settings are missing.", 409);
        }
        if (request is not null)
        {
            settings.IsEnabled = request.IsEnabled;
            settings.OperatingModeCode = request.OperatingModeCode;
            settings.MaxExecutionsPerMinute = request.MaxExecutionsPerMinute;
            settings.MaxExecutionsPerDay = request.MaxExecutionsPerDay;
            settings.MaxConsecutiveFailures = request.MaxConsecutiveFailures;
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await db.Entry(settings).ReloadAsync(token);
        }
        return new(ToDto(settings));
    }
    private AutomationSettingsDto ToDto(AutomationRuntimeSettings s) => new(s.WorkspaceId, s.IsEnabled, s.OperatingModeCode,
        s.MaxExecutionsPerMinute, s.MaxExecutionsPerDay, s.MaxConsecutiveFailures, s.CreatedAt, s.UpdatedAt,
        [policy.Evaluate(s, AutomationCategories.General), policy.Evaluate(s, AutomationCategories.Email), policy.Evaluate(s, AutomationCategories.Application)]);
}
