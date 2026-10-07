using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Collection;

// Dedicated non-pooled session, no transaction during network I/O. Closing it releases the lock.
// Token fencing in the history/business transaction remains necessary if this connection is lost.
public sealed class SourceCollectionJobGuard(ProspectionCrmDbContext db)
{
    public const string Prefix = "source-collection-job:";
    public static string Key(Guid id) => Prefix + id.ToString("D");
    public async Task<NpgsqlConnection?> TryAcquireAsync(SourceCollectionJob job, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", connection);
            command.Parameters.AddWithValue("key", Key(job.Id));
            if ((bool)(await command.ExecuteScalarAsync(token))! && await db.SourceCollectionJobs.AnyAsync(x =>
                x.Id == job.Id && x.WorkspaceId == job.WorkspaceId && x.StatusCode == "running" && x.LeaseToken == job.LeaseToken, token))
                return connection;
            await connection.DisposeAsync(); return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}
