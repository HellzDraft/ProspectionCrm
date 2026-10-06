using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProspectionCrm.Api.Data.Migrations;
using ProspectionCrm.Api.Services;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class PersistentSourceIdentityMigrationTests : PersistentSourceIdentityFixture
{
    [Fact]
    public async Task UpgradeBackfillsPreservesLegacyAndSupportsDownUp()
    {
        var setup = await PrepareAsync(Previous);
        await using var db = Db();
        var first = await LegacySourceAsync(db, setup, " HTTPS://EXAMPLE.INVALID/Case?b=2&a=1#F ", setup.Configuration, setup.Search, setup.Execution, "Exact");
        var empty = await LegacySourceAsync(db, setup, null);
        var other = await SeedAsync(db, archived: true);
        await LegacySourceAsync(db, other, "https://example.invalid/Case?b=2&a=1#F", other.Configuration, external: "Exact");
        await db.Database.MigrateAsync();
        var rows = await db.OpportunitySources.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.Equal(setup.Workspace, rows.Single(x => x.Id == first).WorkspaceId);
        Assert.Equal("https://example.invalid/Case?b=2&a=1#F", rows.Single(x => x.Id == first).NormalizedSourceUrl);
        Assert.Equal(" HTTPS://EXAMPLE.INVALID/Case?b=2&a=1#F ", rows.Single(x => x.Id == first).SourceUrl);
        Assert.Null(rows.Single(x => x.Id == empty).ExternalId);
        Assert.Null(rows.Single(x => x.Id == empty).NormalizedSourceUrl);
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        await db.Database.MigrateAsync();
        var after = await db.OpportunitySources.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(rows.Select(x => (x.Id, x.WorkspaceId, x.SourceUrl, x.NormalizedSourceUrl, x.ExternalId, x.FirstSeenAt)),
            after.Select(x => (x.Id, x.WorkspaceId, x.SourceUrl, x.NormalizedSourceUrl, x.ExternalId, x.FirstSeenAt)));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("configuration", "InvalidSourceReferences")]
    [InlineData("search", "InvalidSourceReferences")]
    [InlineData("execution", "InvalidSourceReferences")]
    [InlineData("external-without-source", "InvalidSourceReferences")]
    [InlineData("url", "DuplicateSourceUrls")]
    [InlineData("invalid", "InvalidSourceUrls")]
    [InlineData("external", "DuplicateExternalIds")]
    public async Task InvalidLegacyAbortsAtomicallyWithControlledDiagnostics(string kind, string code)
    {
        var setup = await PrepareAsync(Previous);
        await using var db = Db();
        var other = await SeedAsync(db, archived: true);
        const string secret = "secret-that-must-not-leak";
        var url = kind == "invalid" ? $"https://user:{secret}@example.invalid/" : $"https://example.invalid/{secret}";
        var id = await LegacySourceAsync(db, setup, url,
            kind == "configuration" ? other.Configuration : kind == "external-without-source" ? null : setup.Configuration,
            kind == "search" ? other.Search : null, kind == "execution" ? other.Execution : null,
            kind is "external" or "external-without-source" ? secret : null);
        if (kind == "url") await LegacySourceAsync(db, setup, $"HTTPS://EXAMPLE.INVALID/{secret}");
        if (kind == "external")
        {
            await db.Database.ExecuteSqlRawAsync("""DROP INDEX "UX_OpportunitySources_SourceConfiguration_ExternalId" """);
            await LegacySourceAsync(db, setup, null, setup.Configuration, external: secret);
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains(code, error.MessageText);
        Assert.Contains(id.ToString(), error.MessageText);
        Assert.DoesNotContain(secret, error.MessageText);
        Assert.Equal(Previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var original = new NpgsqlCommand("""SELECT "SourceUrl" FROM "OpportunitySources" WHERE "Id" = @id""", connection);
        original.Parameters.AddWithValue("id", id);
        Assert.Equal(url, await original.ExecuteScalarAsync());
        await using var columns = new NpgsqlCommand("""
            SELECT count(*) FROM information_schema.columns WHERE table_name = 'OpportunitySources' AND column_name = 'WorkspaceId'
            """, connection);
        Assert.Equal(0L, await columns.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SqlBackfillNormalizationMatchesV1Corpus()
    {
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString());
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand(Phase622PersistentSourceIdentities.NormalizationSql, connection))
            await create.ExecuteNonQueryAsync();
        var corpus = new List<string?>
        {
            null, "", "  ", " HTTPS://EXAMPLE.INVALID/A?b=2&a=1#Top ", "https://example.invalid/a?b=2&a=1#Top",
            "https://example.invalid/A?a=1&b=2#Top", "https://example.invalid/A?b=2&a=1#Other",
            "https://example.invalid:443/a%2Fb", "http://example.invalid/%zz", "\t\r\nhttps://EXAMPLE.invalid/\u00a0",
            "/relative", "ftp://example.invalid/", "https://user:password@example.invalid/",
            "https://example.invalid/a\\b", "https://example.invalid/a b", "https://example.invalid/a\u0001b",
            "https://example.invalid/a\u007fb", "https://example.invalid/a\u0085b", "https://example.invalid/a\u2003b",
            "https://[::1]:443/A", "https://[::1%25eth0]/", "https://[bad]/", "https://host:65536/", "https://host:00080/",
            "https://host:/", "https://host:abc/", "https://@host/", "https://a..b/", "https://.host/",
            "https://éXAMPLE.org/A", "https://İ.EXAMPLE/A", "https://☃.org/", "https://a\u200db/", "https://_host/",
            "https://999.999.999.999/", "https://127.1/", "https://host%20bad/", "https://host~bad/",
            "https://a\u3002b/", "https://\u3002a/", "https://a\u3002\u3002b/", "https://a\uFF0Eb/",
            "https://a\u00adb/", "https://a\uFEFFb/", "https://a\u200Cb/", "https://[::1%]/", "https://[::1%abc:xyz]/",
            "https://[::ffff:127.1]/", "https://" + new string('a', 254) + "/",
            "https://host:00000000000000000000000000000000000000000000000000000080/"
        };
        foreach (var value in corpus)
        {
            await using var command = new NpgsqlCommand("SELECT pg_temp.phase622_url_key(@url)", connection);
            command.Parameters.AddWithValue("url", NpgsqlTypes.NpgsqlDbType.Text, (object?)value ?? DBNull.Value);
            var result = await command.ExecuteScalarAsync();
            var actual = result is DBNull ? null : (string?)result;
            Assert.True(IngestionNormalization.UrlKey(value) == actual, $"Parity mismatch for corpus value {System.Text.Json.JsonSerializer.Serialize(value)}");
        }
    }
}
