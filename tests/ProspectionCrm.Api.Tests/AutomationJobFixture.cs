using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Automation;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

// One real PostgreSQL per test class. Each test gets its own workspace, avoiding shared business state.
public sealed class AutomationJobDatabase : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:18").Build();
    public async Task InitializeAsync()
    {
        await Postgres.StartAsync();
        await using var db = Db(); await db.Database.MigrateAsync();
    }
    public Task DisposeAsync() => Postgres.DisposeAsync().AsTask();
    public ProspectionCrmDbContext Db() => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(Postgres.GetConnectionString()).Options);
}

public abstract class AutomationJobFixture(AutomationJobDatabase database)
{
    protected ProspectionCrmDbContext Db() => database.Db();
    protected static AutomationJobQueue Queue(ProspectionCrmDbContext db, TimeSpan? duration = null) => new(db,
        Options.Create(new AutomationJobQueueOptions { LeaseDuration = duration ?? TimeSpan.FromMinutes(5) }));
    protected async Task<Guid> Workspace()
    {
        await using var db = Db();
        var workspace = new Workspace { Name = "Automation jobs", TimeZoneId = "UTC",
            OwnerUser = new UserAccount { Email = $"{Guid.NewGuid():N}@example.invalid" }, AutomationRuntimeSettings = new() };
        db.Add(workspace); await db.SaveChangesAsync(); return workspace.Id;
    }
    protected async Task<Guid> Rule(Guid workspace)
    {
        await using var db = Db();
        var rule = new AutomationRule { WorkspaceId = workspace, Name = "No execution", TriggerTypeCode = "manual", ActionTypeCode = "test" };
        db.Add(rule); await db.SaveChangesAsync(); return rule.Id;
    }
    protected static EnqueueAutomationJobRequest Request(string? key = null, int? priority = null,
        DateTimeOffset? available = null, Guid? rule = null, string category = AutomationCategories.General,
        string trigger = AutomationJobTriggers.Manual, string? context = null) => new()
        { TriggerTypeCode = trigger, TriggerKey = key, Priority = priority, AvailableAt = available,
            AutomationRuleId = rule, ActionCategoryCode = category, ContextJson = context };
    protected async Task<AutomationJob> Add(Guid workspace, EnqueueAutomationJobRequest? request = null)
    {
        await using var db = Db(); var result = await Queue(db).EnqueueAsync(workspace, request ?? Request(), default);
        Assert.Equal(201, result.Status); return Assert.IsType<AutomationJob>(result.Value);
    }
    protected WebApplicationFactory<Program> Factory(Guid? workspace) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", database.Postgres.GetConnectionString())
        .UseSetting("SourceCollectionWorker:Enabled", "false").UseSetting("SourceCollectionScheduler:Enabled", "false")
        .ConfigureServices(s => { if (workspace is { } id) { s.RemoveAll<ICurrentWorkspaceProvider>(); s.AddScoped<ICurrentWorkspaceProvider>(_ => new FixedWorkspace(id)); } }));
    protected static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new("https://localhost") });
    private sealed class FixedWorkspace(Guid id) : ICurrentWorkspaceProvider
    { public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id); }
}
