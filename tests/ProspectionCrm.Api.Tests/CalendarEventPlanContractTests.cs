using System.Text.Json;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Calendar;
using ProspectionCrm.Api.Services.Automation;
using Xunit;
namespace ProspectionCrm.Api.Tests;
public sealed class CalendarEventPlanContractTests
{
    private static CalendarEventContent Content => new(Guid.NewGuid(), "create", null, "Title", null, null,
        "2026-10-21T10:00:00+02:00", "2026-10-21T11:00:00+02:00", false, "Europe/Paris");
    [Theory]
    [InlineData("manual")][InlineData("assist")][InlineData("automatic")]
    public void CalendarAlwaysRequiresHumanAndCannotBeAnAutomationRule(string mode)
    {
        var policy = new AutomationSafetyPolicy();
        var settings = new AutomationRuntimeSettings { IsEnabled = true, OperatingModeCode = mode };
        Assert.Equal("manual", policy.Evaluate(settings, AutomationCategories.Calendar).DecisionCode);
        settings.IsEnabled = false; Assert.Equal("blocked", policy.Evaluate(settings, AutomationCategories.Calendar).DecisionCode);
        foreach (var action in new[] { CalendarEventSnapshots.CreateAction, CalendarEventSnapshots.UpdateAction })
            Assert.Equal(AutomationEvaluationReasons.UnsupportedAction, AutomationRuleValidation.Validate("manual", action, "{}", "{}"));
    }
    [Theory]
    [InlineData("2026-03-29T00:00:00+01:00", "2026-03-30T00:00:00+02:00", 23)]
    [InlineData("2026-10-25T00:00:00+02:00", "2026-10-26T00:00:00+01:00", 25)]
    public void AllDayUsesLocalMidnightsAndExclusiveEndAcrossDst(string start, string end, int hours)
    {
        var c = CalendarEventSnapshots.Normalize(Content with { StartsAt = start, EndsAt = end, IsAllDay = true });
        Assert.Equal(hours, (DateTimeOffset.Parse(c.EndsAt) - DateTimeOffset.Parse(c.StartsAt)).TotalHours);
        Assert.Equal(c, CalendarEventSnapshots.Normalize(c));
    }
    [Theory]
    [InlineData("naive")][InlineData("zone")][InlineData("windows")][InlineData("offset")][InlineData("zero")]
    [InlineData("negative")][InlineData("all-day-time")][InlineData("all-day-zone")][InlineData("operation")]
    [InlineData("missing-target")][InlineData("create-target")][InlineData("title")][InlineData("description")]
    public void InvalidContentIsRejected(string kind)
    {
        var c = Content;
        c = kind switch {
            "naive" => c with { StartsAt = "2026-10-21T10:00:00" },
            "zone" => c with { TimeZoneId = "Mars/Unknown" }, "windows" => c with { TimeZoneId = "Romance Standard Time" },
            "offset" => c with { StartsAt = "2026-10-21T10:00:00+01:00" },
            "zero" => c with { EndsAt = c.StartsAt }, "negative" => c with { EndsAt = "2026-10-20T10:00:00Z" },
            "all-day-time" => c with { IsAllDay = true }, "all-day-zone" => c with { IsAllDay = true, TimeZoneId = null },
            "operation" => c with { OperationCode = "series-update" }, "missing-target" => c with { OperationCode = "update" },
            "create-target" => c with { TargetCalendarEventId = Guid.NewGuid() }, "title" => c with { Title = "" },
            _ => c with { Description = new string('x', 10001) }
        };
        Assert.Equal(400, Assert.Throws<CalendarSyncException>(() => CalendarEventSnapshots.Normalize(c)).Status);
    }
    [Fact]
    public void ClosedVersionedSnapshotAndFingerprintIncludeEveryIdentity()
    {
        var s = new CalendarEventSnapshot(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0,
            CalendarEventSnapshots.Normalize(Content), null);
        var json = CalendarEventSnapshots.Serialize(s); Assert.NotNull(CalendarEventSnapshots.Parse(json));
        Assert.Null(CalendarEventSnapshots.Parse(json[..^1] + ",\"unknown\":true}"));
        Assert.Null(CalendarEventSnapshots.Parse(json.Replace("\"version\":1", "\"version\":2")));
        Assert.Null(CalendarEventSnapshots.Parse(json.Replace("\"version\":1", "\"version\":1,\"version\":1")));
        Assert.Null(CalendarEventSnapshots.Parse(json.Replace("\"title\":", "\"rrule\":null,\"title\":")));
        foreach (var changed in new[] { s with { Revision = 1 }, s with { ConnectionId = Guid.NewGuid() },
            s with { WorkspaceId = Guid.NewGuid() }, s with { Content = s.Content with { Title = "Other" } } })
            Assert.NotEqual(CalendarEventSnapshots.Hash(s), CalendarEventSnapshots.Hash(changed));
    }
}
