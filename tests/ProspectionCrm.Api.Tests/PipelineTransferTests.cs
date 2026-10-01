using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class PipelineTransferTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private Guid workspaceId;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        workspaceId = (await new BootstrapService(db).BootstrapAsync(default)).Bootstrap!.WorkspaceId;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private ProspectionCrmDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);

    private static JsonObject Document() => JsonNode.Parse("""
        {"schemaVersion":1,"pipeline":{"name":"  Portable  ","typeCode":"employment",
        "description":"Configuration","isVisible":false,"stages":[
        {"name":"  First  ","description":null,"categoryCode":"active"},
        {"name":"Second","description":"Won","categoryCode":"success"},
        {"name":"Third","description":"Lost","categoryCode":"failure"}]}}
        """)!.AsObject();

    private Task<HttpResponseMessage> PostAsync(string json) => client.PostAsync("/api/pipelines/import",
        new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<PipelineDto> ImportAsync(JsonNode document)
    {
        using var response = await PostAsync(document.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PipelineDto>())!;
        Assert.EndsWith($"/api/pipelines/{result.Id}", response.Headers.Location?.ToString());
        var fetched = await client.GetFromJsonAsync<PipelineDto>(response.Headers.Location);
        Assert.NotNull(fetched);
        Assert.Equal((result.Id, result.Name, result.TypeCode, result.Description, result.IsVisible, result.IsDefault,
                result.PreferredCandidateProfileId, result.ArchivedAt, result.UpdatedAt),
            (fetched.Id, fetched.Name, fetched.TypeCode, fetched.Description, fetched.IsVisible, fetched.IsDefault,
                fetched.PreferredCandidateProfileId, fetched.ArchivedAt, fetched.UpdatedAt));
        Assert.Equal(result.Stages.Select(x => (x.Id, x.PipelineId, x.Name, x.Description, x.CategoryCode, x.SortOrder, x.ArchivedAt, x.UpdatedAt)),
            fetched.Stages.Select(x => (x.Id, x.PipelineId, x.Name, x.Description, x.CategoryCode, x.SortOrder, x.ArchivedAt, x.UpdatedAt)));
        // PostgreSQL stores microseconds; the immediate DTO still has the original .NET ticks.
        Assert.InRange((result.CreatedAt - fetched.CreatedAt).Ticks, 0, 9);
        for (var i = 0; i < result.Stages.Count; i++)
            Assert.InRange((result.Stages[i].CreatedAt - fetched.Stages[i].CreatedAt).Ticks, 0, 9);
        return result;
    }

    private async Task<JsonNode> ExportAsync(Guid id)
    {
        using var response = await client.GetAsync($"/api/pipelines/{id}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private async Task<Pipeline> SeedAsync(bool archived = false)
    {
        await using var db = CreateContext();
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var pipeline = new Pipeline
        {
            WorkspaceId = workspaceId, Name = "Source", TypeCode = "business", Description = "Portable description",
            IsVisible = false, CreatedAt = old, UpdatedAt = old, ArchivedAt = archived ? old : null,
            PreferredCandidateProfile = new CandidateProfile { WorkspaceId = workspaceId, Name = "Local profile" },
            Stages = new List<PipelineStage>
            {
                new() { Name = "Last", Description = "Failure", CategoryCode = "failure", SortOrder = 9 },
                new() { Name = "Hidden", CategoryCode = "active", SortOrder = 1, ArchivedAt = old },
                new() { Name = "First", CategoryCode = "active", SortOrder = 0 },
                new() { Name = "Middle", Description = "Success", CategoryCode = "success", SortOrder = 4 }
            }
        };
        db.Pipelines.Add(pipeline);
        var opportunity = new Opportunity
        {
            WorkspaceId = workspaceId, PipelineStage = pipeline.Stages.First(), Title = "Operational",
            PriorityCode = "high", Notes = "Unchanged", Score = 50
        };
        db.Opportunities.Add(opportunity);
        db.CrmTasks.Add(new CrmTask { Opportunity = opportunity, Title = "Task" });
        db.Applications.Add(new Application { Opportunity = opportunity, StatusCode = "draft" });
        db.Proposals.Add(new Proposal { Opportunity = opportunity, StatusCode = "draft" });
        db.EmailMessages.Add(new EmailMessage
        {
            WorkspaceId = workspaceId, Opportunity = opportunity, ProviderCode = "manual", DirectionCode = "inbound",
            FromAddress = "test@example.com", ToAddressesJson = "[]", OccurredAt = old
        });
        db.CalendarEvents.Add(new CalendarEvent
        {
            WorkspaceId = workspaceId, Opportunity = opportunity, ProviderCode = "manual", Title = "Meeting",
            StartsAt = old, EndsAt = old.AddHours(1)
        });
        db.ActivityEntries.Add(new ActivityEntry
        {
            WorkspaceId = workspaceId, RelatedOpportunity = opportunity, EntityTypeCode = "opportunity", EntityId = opportunity.Id,
            EventTypeCode = "created", ActorTypeCode = "user", OccurredAt = old
        });
        db.AutomationExecutions.Add(new AutomationExecution
        {
            WorkspaceId = workspaceId, StatusCode = "succeeded", TriggeredAt = old,
            AutomationRule = new AutomationRule
            {
                WorkspaceId = workspaceId, PipelineId = pipeline.Id, Name = "Rule", TriggerTypeCode = "manual", ActionTypeCode = "test"
            }
        });
        await db.SaveChangesAsync();
        if (!archived)
        {
            (await db.Workspaces.SingleAsync()).DefaultPipelineId = pipeline.Id;
            await db.SaveChangesAsync();
        }
        return pipeline;
    }

    // Preserve all persisted fields, including operational tables. Optionally exclude only new configuration.
    private async Task<string> SnapshotAsync(Guid? sourceId = null)
    {
        await using var db = CreateContext();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var snapshot = new SortedDictionary<string, string>();
        foreach (var table in db.Model.GetEntityTypes().Select(x => x.GetTableName()!).Distinct().Order())
        {
            var filter = sourceId.HasValue ? table switch
            {
                "Pipelines" => " WHERE t.\"Id\" = @source",
                "PipelineStages" => " WHERE t.\"PipelineId\" = @source",
                _ => ""
            } : "";
            var quoted = table.Replace("\"", "\"\"");
            await using var command = new NpgsqlCommand(
                $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM \"{quoted}\" t{filter}", connection);
            if (sourceId.HasValue) command.Parameters.AddWithValue("source", sourceId.Value);
            snapshot[table] = (string)(await command.ExecuteScalarAsync())!;
        }
        return JsonSerializer.Serialize(snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportIsPortableDeterministicReadOnlyAndRoundTrips(bool archived)
    {
        var source = await SeedAsync(archived);
        var before = await SnapshotAsync();
        var exported = await ExportAsync(source.Id);
        Assert.Equal(new[] { "pipeline", "schemaVersion" }, exported.AsObject().Select(x => x.Key).Order());
        Assert.Equal(1, exported["schemaVersion"]!.GetValue<int>());
        var configuration = exported["pipeline"]!.AsObject();
        Assert.Equal(new[] { "description", "isVisible", "name", "stages", "typeCode" }, configuration.Select(x => x.Key).Order());
        Assert.Equal(source.Name, configuration["name"]!.GetValue<string>());
        Assert.Equal(source.TypeCode, configuration["typeCode"]!.GetValue<string>());
        Assert.Equal(source.Description, configuration["description"]!.GetValue<string>());
        Assert.False(configuration["isVisible"]!.GetValue<bool>());
        var stages = configuration["stages"]!.AsArray();
        Assert.Equal(new[] { "First", "Middle", "Last" }, stages.Select(x => x!["name"]!.GetValue<string>()));
        foreach (var stage in stages)
            Assert.Equal(new[] { "categoryCode", "description", "name" }, stage!.AsObject().Select(x => x.Key).Order());
        Assert.True(JsonNode.DeepEquals(exported, await ExportAsync(source.Id)));
        Assert.Equal(before, await SnapshotAsync());

        var preserved = await SnapshotAsync(source.Id);
        var imported = await ImportAsync(exported);
        Assert.NotEqual(source.Id, imported.Id);
        Assert.Empty(source.Stages.Select(x => x.Id).Intersect(imported.Stages.Select(x => x.Id)));
        Assert.Null(imported.PreferredCandidateProfileId);
        Assert.Null(imported.ArchivedAt);
        Assert.False(imported.IsDefault);
        Assert.Equal(new[] { 0, 1, 2 }, imported.Stages.Select(x => x.SortOrder));
        Assert.True(JsonNode.DeepEquals(exported, await ExportAsync(imported.Id)));
        Assert.Equal(preserved, await SnapshotAsync(source.Id));
    }

    [Fact]
    public async Task ExportUsesIdAsDeterministicTieBreaker()
    {
        var source = await SeedAsync();
        await using var db = CreateContext();
        // Ties are normally prevented by the unique index. Exercise the specified fallback only in this disposable DB.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"UX_PipelineStages_Pipeline_SortOrder\"");
        await db.PipelineStages.Where(x => x.PipelineId == source.Id && x.ArchivedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(stage => stage.SortOrder, 5));
        var expected = await db.PipelineStages.Where(x => x.PipelineId == source.Id && x.ArchivedAt == null)
            .OrderBy(x => x.Id).Select(x => x.Name).ToArrayAsync();
        var exported = await ExportAsync(source.Id);
        Assert.Equal(expected, exported["pipeline"]!["stages"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportWithoutActiveStagesProducesEmptyArray(bool archivedOnly)
    {
        await using var db = CreateContext();
        var pipeline = new Pipeline { WorkspaceId = workspaceId, Name = "Empty", TypeCode = "custom" };
        if (archivedOnly)
            pipeline.Stages.Add(new PipelineStage
            {
                Name = "Archived", CategoryCode = "active", SortOrder = 0, ArchivedAt = DateTimeOffset.UtcNow
            });
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        var exported = await ExportAsync(pipeline.Id);
        Assert.Empty(exported["pipeline"]!["stages"]!.AsArray());
        Assert.True(exported["pipeline"]!["isVisible"]!.GetValue<bool>());
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task ExportMissingAndForeignPipelineReturns404WithoutWrites()
    {
        await using var db = CreateContext();
        var foreign = new Pipeline
        {
            Name = "Foreign", TypeCode = "custom", Workspace = new Workspace
            {
                OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Other", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
            }
        };
        db.Pipelines.Add(foreign);
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        foreach (var id in new[] { foreign.Id, Guid.NewGuid() })
        {
            using var response = await client.GetAsync($"/api/pipelines/{id}/export");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("employment", true)]
    [InlineData("freelance", false)]
    [InlineData("business", true)]
    [InlineData("custom", false)]
    public async Task ImportCreatesFreshConfigurationAndPreservesDefaultAndOperationalData(string type, bool visible)
    {
        var source = await SeedAsync();
        var before = await SnapshotAsync(source.Id);
        var document = Document();
        document["pipeline"]!["typeCode"] = type;
        document["pipeline"]!["isVisible"] = visible;
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        var first = await ImportAsync(document);
        var second = await ImportAsync(document);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Portable", first.Name);
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(type, first.TypeCode);
        Assert.Equal("Configuration", first.Description);
        Assert.Equal(visible, first.IsVisible);
        Assert.Null(first.PreferredCandidateProfileId);
        Assert.False(first.IsDefault);
        Assert.Null(first.ArchivedAt);
        Assert.Null(first.UpdatedAt);
        Assert.InRange(first.CreatedAt, start, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "First", "Second", "Third" }, first.Stages.Select(x => x.Name));
        Assert.Equal(new[] { "active", "success", "failure" }, first.Stages.Select(x => x.CategoryCode));
        Assert.Equal(new string?[] { null, "Won", "Lost" }, first.Stages.Select(x => x.Description));
        Assert.Equal(new[] { 0, 1, 2 }, first.Stages.Select(x => x.SortOrder));
        Assert.Equal(6, first.Stages.Concat(second.Stages).Select(x => x.Id).Distinct().Count());
        Assert.All(first.Stages, stage =>
        {
            Assert.NotEqual(Guid.Empty, stage.Id);
            Assert.Equal(first.Id, stage.PipelineId);
            Assert.Null(stage.ArchivedAt);
            Assert.Null(stage.UpdatedAt);
            Assert.InRange(stage.CreatedAt, start, DateTimeOffset.UtcNow);
        });
        Assert.Equal(before, await SnapshotAsync(source.Id));
        await using var db = CreateContext();
        Assert.Equal(workspaceId, (await db.Pipelines.SingleAsync(x => x.Id == first.Id)).WorkspaceId);
        Assert.Equal(source.Id, (await db.Workspaces.SingleAsync()).DefaultPipelineId);
        Assert.Equal(3, await db.Pipelines.CountAsync());
        Assert.Equal(10, await db.PipelineStages.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task ImportAcceptsEmptyAndMaximumStagesWithoutSelectingDefault(int count)
    {
        var document = Document();
        document["pipeline"]!["stages"] = new JsonArray(Enumerable.Range(0, count)
            .Select(i => (JsonNode)new JsonObject { ["name"] = $"Stage {i}", ["categoryCode"] = "active" }).ToArray());
        document["pipeline"]!.AsObject().Remove("description");
        var imported = await ImportAsync(document);
        Assert.Null(imported.Description);
        Assert.Equal(count, imported.Stages.Count);
        Assert.Equal(Enumerable.Range(0, count), imported.Stages.Select(x => x.SortOrder));
        Assert.Equal(count, imported.Stages.Select(x => x.Id).Distinct().Count());
        Assert.False(imported.IsDefault);
        await using var db = CreateContext();
        Assert.Null((await db.Workspaces.SingleAsync()).DefaultPipelineId);
        Assert.Empty(await db.Opportunities.ToArrayAsync());
    }

    [Fact]
    public async Task ImportAcceptsLengthBoundariesAfterTrimming()
    {
        var document = Document();
        document["pipeline"]!["name"] = "  " + new string('n', 200) + "  ";
        document["pipeline"]!["description"] = new string('d', 2000);
        document["pipeline"]!["stages"]![0]!["name"] = "  " + new string('s', 200) + "  ";
        document["pipeline"]!["stages"]![0]!["description"] = new string('e', 2000);
        var imported = await ImportAsync(document);
        Assert.Equal(new string('n', 200), imported.Name);
        Assert.Equal(new string('d', 2000), imported.Description);
        Assert.Equal(new string('s', 200), imported.Stages[0].Name);
        Assert.Equal(new string('e', 2000), imported.Stages[0].Description);
    }

    private async Task AssertRejectedAsync(string json, string before)
    {
        using var response = await PostAsync(json);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for {json[..Math.Min(json.Length, 300)]}; got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem!["status"]!.GetValue<int>());
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task ImportRejectsMissingRequiredMembersAndNullsWithoutWrites()
    {
        await SeedAsync();
        var before = await SnapshotAsync();
        foreach (var level in new[] { "root", "pipeline", "stage" })
        {
            var properties = level switch
            {
                "root" => new[] { "schemaVersion", "pipeline" },
                "pipeline" => new[] { "name", "typeCode", "isVisible", "stages" },
                _ => new[] { "name", "categoryCode" }
            };
            foreach (var property in properties)
            foreach (var remove in new[] { true, false })
            {
                var document = Document();
                var target = level switch
                {
                    "root" => document,
                    "pipeline" => document["pipeline"]!.AsObject(),
                    _ => document["pipeline"]!["stages"]![0]!.AsObject()
                };
                if (remove) target.Remove(property); else target[property] = null;
                await AssertRejectedAsync(document.ToJsonString(), before);
            }
        }
        var nullStage = Document();
        nullStage["pipeline"]!["stages"]![0] = null;
        await AssertRejectedAsync(nullStage.ToJsonString(), before);
    }

    [Fact]
    public async Task ImportRejectsUnknownPropertiesAtEveryLevelWithoutWrites()
    {
        await SeedAsync();
        var before = await SnapshotAsync();
        foreach (var level in new[] { "root", "pipeline", "stage" })
        foreach (var property in new[] { "futureSetting", "Name", "id", "workspaceId", "sortOrder", "archivedAt", "createdAt",
            "updatedAt", "isDefault", "preferredCandidateProfileId", "opportunities" })
        {
            var document = Document();
            var target = level switch
            {
                "root" => document,
                "pipeline" => document["pipeline"]!.AsObject(),
                _ => document["pipeline"]!["stages"]![0]!.AsObject()
            };
            target[property] = "unrecognized";
            await AssertRejectedAsync(document.ToJsonString(), before);
        }
    }

    [Fact]
    public async Task ImportRejectsIncorrectJsonTypesAndMalformedBodiesWithoutWrites()
    {
        var before = await SnapshotAsync();
        foreach (var raw in new[] { "", "{", "null", "[]", "1", "true", "\"text\"", "{\"schemaVersion\":1,}" })
            await AssertRejectedAsync(raw, before);
        foreach (var (level, property, value) in new[]
        {
            ("root", "schemaVersion", "\"1\""), ("root", "schemaVersion", "1.5"), ("root", "schemaVersion", "true"),
            ("root", "schemaVersion", "2147483648"), ("root", "pipeline", "[]"),
            ("pipeline", "name", "1"), ("pipeline", "typeCode", "true"), ("pipeline", "description", "{}"),
            ("pipeline", "isVisible", "\"false\""), ("pipeline", "isVisible", "0"), ("pipeline", "stages", "{}"),
            ("pipeline", "stages", "[1]"), ("stage", "name", "false"), ("stage", "description", "[]"),
            ("stage", "categoryCode", "1")
        })
        {
            var document = Document();
            var target = level switch
            {
                "root" => document,
                "pipeline" => document["pipeline"]!.AsObject(),
                _ => document["pipeline"]!["stages"]![0]!.AsObject()
            };
            target[property] = JsonNode.Parse(value);
            await AssertRejectedAsync(document.ToJsonString(), before);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task ImportRejectsUnsupportedVersionWithExplicitProblem(int version)
    {
        var document = Document();
        document["schemaVersion"] = version;
        var before = await SnapshotAsync();
        using var response = await PostAsync(document.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("schemaVersion", problem["detail"]!.GetValue<string>());
        Assert.Contains("supported", problem["detail"]!.GetValue<string>());
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task ImportRejectsInvalidBusinessValuesAndExcessiveStagesWithoutWrites()
    {
        await SeedAsync();
        var before = await SnapshotAsync();
        foreach (var level in new[] { "pipeline", "stage" })
        foreach (var (property, value) in new[]
        {
            ("name", ""), ("name", " \t "), ("name", new string('n', 201)), ("description", new string('d', 2001)),
            ("code", ""), ("code", "invalid"), ("code", level == "pipeline" ? "Employment" : "Active"),
            ("code", level == "pipeline" ? "employment " : "active ")
        })
        {
            var document = Document();
            var target = level == "pipeline" ? document["pipeline"]!.AsObject() : document["pipeline"]!["stages"]![2]!.AsObject();
            target[property == "code" ? level == "pipeline" ? "typeCode" : "categoryCode" : property] = value;
            await AssertRejectedAsync(document.ToJsonString(), before);
        }
        var oversized = Document();
        oversized["pipeline"]!["stages"] = new JsonArray(Enumerable.Range(0, 1001)
            .Select(i => (JsonNode)new JsonObject { ["name"] = $"Stage {i}", ["categoryCode"] = "active" }).ToArray());
        await AssertRejectedAsync(oversized.ToJsonString(), before);
    }

    [Fact]
    public async Task FailedStageInsertRollsBackEntireImport()
    {
        await SeedAsync();
        var before = await SnapshotAsync();
        await using var db = CreateContext();
        // Seed slots are 0/1/4/9. Reject only the last imported stage, after valid preceding inserts.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PipelineStages\" ADD CONSTRAINT reject_import_slot CHECK (\"SortOrder\" <> 2)");
        var document = Document().Deserialize<PipelineTransferDocument>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var service = new PipelineService(db, new CurrentWorkspaceProvider(db));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => service.ImportAsync(document, default));
        Assert.Equal("reject_import_slot", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        Assert.Equal(before, await SnapshotAsync());
    }
}
