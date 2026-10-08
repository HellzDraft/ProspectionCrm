using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Automation;

public interface IAutomationJobRecovery
{
    Task<int> RecoverAsync(Guid workspace, CancellationToken token);
}

public sealed class AutomationJobRecovery(ProspectionCrmDbContext db, IAutomationJobQueue queue,
    AutomationRuntimeStore runtime) : IAutomationJobRecovery
{
    public async Task<int> RecoverAsync(Guid workspace, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await runtime.LockAsync(workspace, token);
        var recovered = await queue.RecoverAsync(workspace, token);
        await runtime.AbandonAsync(workspace, token);
        await transaction.CommitAsync(token);
        return recovered;
    }
}
