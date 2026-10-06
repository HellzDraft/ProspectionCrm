using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProspectionCrm.Api.Tests;

internal sealed class PersistentSourceIdentityGate(Func<DbContext, bool> predicate) : SaveChangesInterceptor
{
    private bool used;
    internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!used && predicate(eventData.Context!))
        {
            used = true;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }
}
