using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services;

public static class WorkspaceIdentityLock
{
    internal static long Key(Guid workspaceId) => BinaryPrimitives.ReadInt64BigEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes($"ProspectionCrm/manual-ingestion/{workspaceId:D}")));

    public static Task AcquireAsync(ProspectionCrmDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Workspace identity locking requires an active transaction.");
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({Key(workspaceId)})", cancellationToken);
    }
}
