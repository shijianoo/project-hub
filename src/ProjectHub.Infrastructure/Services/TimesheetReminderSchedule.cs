namespace ProjectHub.Infrastructure.Services;

/// <summary>工时提醒时区和到期判断的纯函数，便于独立测试。</summary>
internal static class TimesheetReminderSchedule
{
    internal static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        string id = string.IsNullOrWhiteSpace(timeZoneId) ? "Asia/Shanghai" : timeZoneId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException) when (id.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }

    internal static DateTime GetLocalNow(DateTime utcNow, string? timeZoneId) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), ResolveTimeZone(timeZoneId));

    internal static bool IsWorkday(DateTime date) => date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;

    internal static bool IsDailyDue(DateTime localNow, bool enabled, int hour, int minute) =>
        enabled && IsWorkday(localNow) && localNow.TimeOfDay >= CreateTime(hour, minute);

    internal static bool IsWeeklyDue(DateTime localNow, bool enabled, int isoDayOfWeek, int hour, int minute)
    {
        if (!enabled)
        {
            return false;
        }

        int currentIsoDay = localNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localNow.DayOfWeek;
        int scheduledDay = Math.Clamp(isoDayOfWeek, 1, 7);
        return currentIsoDay > scheduledDay ||
            (currentIsoDay == scheduledDay && localNow.TimeOfDay >= CreateTime(hour, minute));
    }

    internal static DateTime GetWeekStart(DateTime date)
    {
        int offset = date.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)date.DayOfWeek - 1;
        return date.Date.AddDays(-offset);
    }

    private static TimeSpan CreateTime(int hour, int minute) =>
        new(Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), 0);
}
