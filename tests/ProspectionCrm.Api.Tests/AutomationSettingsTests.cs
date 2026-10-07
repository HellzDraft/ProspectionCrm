using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationSettings;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationSettingsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
    private sealed class FixedWorkspace(Guid id) : ICurrentWorkspaceProvider
    { public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id); }
    private WebApplicationFactory<Program> Factory(Guid? workspace = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString())
        .UseSetting("SourceCollectionWorker:Enabled", "false").UseSetting("SourceCollectionScheduler:Enabled", "false")
        .ConfigureServices(s => { if (workspace is { } id) { s.RemoveAll<ICurrentWorkspaceProvider>(); s.AddScoped<ICurrentWorkspaceProvider>(_ => new FixedWorkspace(id)); } }));
    private static HttpClient Client(WebApplicationFactory<Program> f) => f.CreateClient(new() { BaseAddress = new("https://localhost") });
    private async Task<Guid> Setup()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var result = await new BootstrapService(db).BootstrapAsync(default);
        Assert.Null(result.Error); return result.Bootstrap!.WorkspaceId;
    }
    private static object Valid(bool enabled = true, string mode = "automatic", int minute = 20, int day = 200, int failures = 4)
        => new { isEnabled = enabled, operatingModeCode = mode, maxExecutionsPerMinute = minute, maxExecutionsPerDay = day, maxConsecutiveFailures = failures };
    private static void Defaults(AutomationRuntimeSettings s)
    {
        Assert.False(s.IsEnabled); Assert.Equal("manual", s.OperatingModeCode); Assert.Equal(10, s.MaxExecutionsPerMinute);
        Assert.Equal(100, s.MaxExecutionsPerDay); Assert.Equal(3, s.MaxConsecutiveFailures); Assert.Null(s.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, s.CreatedAt.Offset);
    }
    [Fact]
    public async Task BootstrapDefaultsGetPutAndRepeatedBootstrapPreserveCustomization()
    {
        var id = await Setup(); await using var db = Db();
        Defaults(await db.AutomationRuntimeSettings.AsNoTracking().SingleAsync());
        using var factory = Factory(); using var client = Client(factory);
        var original = await client.GetFromJsonAsync<AutomationSettingsDto>("/api/automation-settings");
        Assert.Equal(id, original!.WorkspaceId); Assert.All(original.Decisions, d => Assert.Equal("blocked", d.DecisionCode));
        Assert.Null(original.UpdatedAt);
        var before = DateTimeOffset.UtcNow;
        using var response = await client.PutAsJsonAsync("/api/automation-settings", Valid()); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<AutomationSettingsDto>())!;
        Assert.Equal(original.CreatedAt, saved.CreatedAt); Assert.InRange(saved.UpdatedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, saved.UpdatedAt.Value.Offset);
        Assert.Equal(new[] { "automatic", "approval-required", "manual" }, saved.Decisions.Select(x => x.DecisionCode));
        Assert.Equal(new[] { "automatic", "assist", "manual" }, saved.Decisions.Select(x => x.EffectiveModeCode));
        using var repeated = await client.PostAsync("/api/setup/bootstrap", null); Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var persisted = await db.AutomationRuntimeSettings.AsNoTracking().SingleAsync();
        Assert.True(persisted.IsEnabled); Assert.Equal("automatic", persisted.OperatingModeCode);
        Assert.Equal(20, persisted.MaxExecutionsPerMinute); Assert.Equal(200, persisted.MaxExecutionsPerDay); Assert.Equal(4, persisted.MaxConsecutiveFailures);
        Assert.Equal(saved.UpdatedAt, persisted.UpdatedAt);
        Assert.Empty(await db.AutomationExecutions.ToListAsync()); Assert.Empty(await db.AutomationRules.ToListAsync());
        using var disabled = await client.PutAsJsonAsync("/api/automation-settings", Valid(false));
        Assert.All((await disabled.Content.ReadFromJsonAsync<AutomationSettingsDto>())!.Decisions, x => Assert.Equal("blocked", x.DecisionCode));
    }
    [Theory]
    [InlineData("unknown",10,100,3)][InlineData("Automatic",10,100,3)]
    [InlineData("manual",0,100,3)][InlineData("manual",101,1000,3)]
    [InlineData("manual",10,0,3)][InlineData("manual",10,10001,3)]
    [InlineData("manual",10,100,0)][InlineData("manual",10,100,21)]
    [InlineData("manual",20,19,3)]
    public async Task InvalidSettingsReturn400WithoutWrites(string mode, int minute, int day, int failures)
    {
        await Setup(); using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PutAsJsonAsync("/api/automation-settings", Valid(mode: mode, minute: minute, day: day, failures: failures));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        await using var db = Db(); Defaults(await db.AutomationRuntimeSettings.SingleAsync());
    }
    [Theory]
    [InlineData(1,1,1)][InlineData(100,10000,20)]
    public async Task InclusiveLimitsAreAccepted(int minute, int day, int failures)
    {
        await Setup(); using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PutAsJsonAsync("/api/automation-settings", Valid(minute: minute, day: day, failures: failures));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    [Theory]
    [InlineData("workspaceId")][InlineData("emailMaximumMode")][InlineData("sensitive-marker")]
    public async Task UnknownFieldsAndWorkspaceInjectionAreRejectedWithoutEcho(string field)
    {
        await Setup(); using var factory = Factory(); using var client = Client(factory);
        var json = "{\"isEnabled\":true,\"operatingModeCode\":\"automatic\",\"maxExecutionsPerMinute\":10,\"maxExecutionsPerDay\":100,\"maxConsecutiveFailures\":3,\"" + field + "\":\"sensitive-marker\"}";
        using var response = await client.PutAsync("/api/automation-settings", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("sensitive-marker", await response.Content.ReadAsStringAsync());
        await using var db = Db(); Defaults(await db.AutomationRuntimeSettings.SingleAsync());
    }
    [Theory]
    [InlineData("{}")][InlineData("null")][InlineData("{\"isEnabled\":\"secret-marker\"}")]
    public async Task MissingOrMalformedBodyIsControlled(string json)
    {
        await Setup(); using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PutAsync("/api/automation-settings", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("secret-marker", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task MissingSettingsReturnsConflictWithoutRepairAndBootstrapRepairsExplicitly()
    {
        await Setup(); await using var db = Db(); await db.AutomationRuntimeSettings.ExecuteDeleteAsync();
        using var factory = Factory(); using var client = Client(factory);
        using var get = await client.GetAsync("/api/automation-settings"); using var put = await client.PutAsJsonAsync("/api/automation-settings", Valid());
        foreach (var response in new[] { get, put })
        { Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("AutomationSettingsMissing", await response.Content.ReadAsStringAsync()); }
        Assert.Empty(await db.AutomationRuntimeSettings.ToListAsync());
        using var bootstrap = await client.PostAsync("/api/setup/bootstrap", null); Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Defaults(await db.AutomationRuntimeSettings.SingleAsync());
    }
    [Fact]
    public async Task TwoWorkspaceIsolationAndAmbiguousSelection()
    {
        var first = await Setup(); await using var db = Db();
        var second = new Workspace { OwnerUserId = await db.UserAccounts.Select(x => x.Id).SingleAsync(), Name = "Other", TimeZoneId = "UTC", AutomationRuntimeSettings = new() };
        db.Workspaces.Add(second); await db.SaveChangesAsync();
        using var selected = Factory(first); using var client = Client(selected);
        using var put = await client.PutAsJsonAsync("/api/automation-settings", Valid()); Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var get = await client.GetFromJsonAsync<AutomationSettingsDto>($"/api/automation-settings?workspaceId={second.Id}"); Assert.Equal(first, get!.WorkspaceId);
        Defaults(await db.AutomationRuntimeSettings.AsNoTracking().SingleAsync(x => x.WorkspaceId == second.Id));
        using var other = Factory(second.Id); using var otherClient = Client(other);
        Assert.Equal(second.Id, (await otherClient.GetFromJsonAsync<AutomationSettingsDto>("/api/automation-settings"))!.WorkspaceId);
        using var ambiguous = Factory(); using var ambiguousClient = Client(ambiguous);
        using var conflict = await ambiguousClient.GetAsync("/api/automation-settings"); Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("WorkspaceUnavailable", await conflict.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task NoWorkspaceReturnsControlledConflict()
    {
        await using var db = Db(); await db.Database.MigrateAsync(); using var factory = Factory(); using var client = Client(factory);
        using var get = await client.GetAsync("/api/automation-settings"); Assert.Equal(HttpStatusCode.Conflict, get.StatusCode);
        using var put = await client.PutAsJsonAsync("/api/automation-settings", Valid()); Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Empty(await db.AutomationRuntimeSettings.ToListAsync());
    }
    [Fact]
    public async Task MigrationBackfillsEveryWorkspaceAndDownUpPreservesBusinessData()
    {
        await using var db = Db(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261007094120_Phase74CollectionScheduling");
        var owner = new UserAccount { Email = "migration@example.invalid" };
        var first = new Workspace { OwnerUser = owner, Name = "Before migration", TimeZoneId = "UTC" };
        var archived = new Workspace { OwnerUser = owner, Name = "Archived", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow };
        db.Workspaces.AddRange(first, archived); await db.SaveChangesAsync();
        await migrator.MigrateAsync();
        var settings = await db.AutomationRuntimeSettings.AsNoTracking().ToListAsync(); Assert.Equal(2, settings.Count); Assert.All(settings, Defaults);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        await migrator.MigrateAsync("20261007094120_Phase74CollectionScheduling");
        Assert.Equal(2, await db.Workspaces.CountAsync()); Assert.Equal("Before migration", (await db.Workspaces.FindAsync(first.Id))!.Name);
        await migrator.MigrateAsync(); Assert.Equal(2, await db.AutomationRuntimeSettings.CountAsync());
        Assert.Empty(await db.AutomationExecutions.ToListAsync()); Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
    }
    [Theory]
    [InlineData("\"OperatingModeCode\" = 'unknown'", "23514")]
    [InlineData("\"MaxExecutionsPerMinute\" = 0", "23514")]
    [InlineData("\"MaxExecutionsPerMinute\" = 101", "23514")]
    [InlineData("\"MaxExecutionsPerDay\" = 0", "23514")]
    [InlineData("\"MaxExecutionsPerDay\" = 10001", "23514")]
    [InlineData("\"MaxExecutionsPerDay\" = 9", "23514")]
    [InlineData("\"MaxConsecutiveFailures\" = 0", "23514")]
    [InlineData("\"MaxConsecutiveFailures\" = 21", "23514")]
    public async Task DatabaseRejectsInvalidValues(string assignment, string state)
    {
        await Setup(); await using var db = Db();
        // Assignment comes exclusively from the fixed InlineData cases above.
        var sql = "UPDATE \"AutomationRuntimeSettings\" SET " + assignment;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(state, ex.SqlState);
    }
    [Fact]
    public async Task SharedPrimaryKeyForeignKeyAndRestrictAreEnforced()
    {
        var id = await Setup(); await using var db = Db();
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AutomationRuntimeSettings\" (\"WorkspaceId\") VALUES ({id})"));
        Assert.Equal("23505", duplicate.SqlState);
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AutomationRuntimeSettings\" (\"WorkspaceId\") VALUES ({Guid.NewGuid()})"));
        Assert.Equal("23503", orphan.SqlState);
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Workspaces\" WHERE \"Id\" = {id}")); Assert.Equal("23001", deletion.SqlState);
    }
    [Fact]
    public async Task UnexpectedStorageFailureIsSanitizedAndDoesNotPersistChanges()
    {
        await Setup(); await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_settings_update() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN RAISE EXCEPTION 'sensitive-marker SELECT private_data'; END $body$;
            CREATE TRIGGER fail_settings_update BEFORE UPDATE ON "AutomationRuntimeSettings" FOR EACH ROW EXECUTE FUNCTION fail_settings_update();
            """);
        using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PutAsJsonAsync("/api/automation-settings", Valid());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("AutomationSettingsInternalError", body); Assert.DoesNotContain("sensitive-marker", body);
        Assert.DoesNotContain("SELECT", body); Assert.DoesNotContain("Exception", body);
        Defaults(await db.AutomationRuntimeSettings.SingleAsync());
    }
    [Fact]
    public async Task FailedSettingsInsertRollsBackEntireBootstrap()
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_settings() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN RAISE EXCEPTION 'test-only'; END $body$;
            CREATE TRIGGER reject_settings BEFORE INSERT ON "AutomationRuntimeSettings" FOR EACH ROW EXECUTE FUNCTION reject_settings();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => new BootstrapService(db).BootstrapAsync(default));
        Assert.Empty(await db.Workspaces.AsNoTracking().ToListAsync()); Assert.Empty(await db.UserAccounts.AsNoTracking().ToListAsync());
    }
}
