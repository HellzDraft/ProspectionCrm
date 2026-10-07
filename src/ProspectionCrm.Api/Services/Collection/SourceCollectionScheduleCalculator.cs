using System.Globalization;

namespace ProspectionCrm.Api.Services.Collection;

public static class SourceCollectionScheduleCalculator
{
    public static bool TryParse(string? value, out int minute)
    {
        minute = 0;
        if (value is null || value.Length != 5 ||
            !TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;
        minute = time.Hour * 60 + time.Minute;
        return true;
    }

    public static string Format(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    public static DateTimeOffset Next(DateTimeOffset now, int minute)
    {
        if (minute is < 0 or > 1439) throw new ArgumentOutOfRangeException(nameof(minute));
        var candidate = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(minute);
        return candidate > now ? candidate : candidate.AddDays(1);
    }
}
