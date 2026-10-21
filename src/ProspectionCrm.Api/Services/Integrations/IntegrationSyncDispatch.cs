namespace ProspectionCrm.Api.Services.Integrations;
internal static class IntegrationSyncDispatch
{
    internal static async Task RunAsync<T>(Func<CancellationToken, Task<(T Command, Guid Attempt)>> begin,
        Func<T, CancellationToken, Task<IntegrationTransportOutcome>> send,
        Func<Guid, IntegrationTransportOutcome, CancellationToken, Task> complete, CancellationToken token)
    {
        var (command, attempt) = await begin(token);
        // A caller disconnect after the durable start cannot skip recording the outcome.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IntegrationTransportOutcome outcome;
        try { outcome = await send(command, deadline.Token); }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
        { outcome = IntegrationTransportOutcome.Unknown; }
        using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await complete(attempt, outcome, finalize.Token);
    }
}
