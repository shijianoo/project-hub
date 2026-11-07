using SqlSugar;
using System.Globalization;
using ProjectHub.Application.Abstractions;
using ProjectHub.Application.Common;
using ProjectHub.Application.Contracts;
using ProjectHub.Domain.Common;
using ProjectHub.Domain.Entities;

namespace ProjectHub.Infrastructure.Services;

/// <summary>聚合员工、项目负责人和管理员工作台所需数据。</summary>
public sealed class DashboardService(
    ISqlSugarClient database,
    ICurrentUserAccessor currentUserAccessor,
    IProjectAccessService projectAccessService,
    ITimeEntryService timeEntryService) : IDashboardService
{
    public async Task<DashboardDto> GetAsync()
    {
        CurrentUser current = currentUserAccessor.GetRequiredUser();
        DateTime today = DateTime.Today;
        DateTime weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        DateTime monthStart = new(today.Year, today.Month, 1);
        long[] projectIds = await projectAccessService.GetCurrentAccessibleProjectIdsAsync();
        long[] managedProjectIds = await projectAccessService.GetCurrentManagedProjectIdsAsync();
        ISugarQueryable<TimeEntry> baseQuery = database.Queryable<TimeEntry>().Where(item => !item.IsDeleted);
        if (!current.IsInRole("admin"))
        {
            baseQuery = baseQuery.Where(item => item.UserId == current.UserId || managedProjectIds.Contains(item.ProjectId));
        }

        int todayMinutes = await baseQuery.Clone().Where(item => item.WorkDate == today).SumAsync(item => item.Minutes);
        int weekMinutes = await baseQuery.Clone().Where(item => item.WorkDate >= weekStart && item.WorkDate < weekStart.AddDays(7)).SumAsync(item => item.Minutes);
        int monthMinutes = await baseQuery.Clone().Where(item => item.WorkDate >= monthStart && item.WorkDate < monthStart.AddMonths(1)).SumAsync(item => item.Minutes);
        List<TimeEntry> recentMonth = await baseQuery.Clone().Where(item => item.WorkDate >= monthStart).ToListAsync();
        List<Project> projects = projectIds.Length == 0
            ? []
            : await database.Queryable<Project>().Where(item => projectIds.Contains(item.Id) && !item.IsDeleted).ToListAsync();
        List<ProjectMember> members = projectIds.Length == 0
            ? []
            : await database.Queryable<ProjectMember>().Where(item => projectIds.Contains(item.ProjectId)).ToListAsync();
        IReadOnlyList<TimeEntryDto> recentEntries = await timeEntryService.QueryAsync(new TimeEntryQuery
        {
            StartDate = today.AddDays(-30),
            EndDate = today
        });

        List<ChartPointDto> trend = [];
        for (int offset = 6; offset >= 0; offset--)
        {
            DateTime day = today.AddDays(-offset);
            int minutes = await baseQuery.Clone().Where(item => item.WorkDate == day).SumAsync(item => item.Minutes);
            trend.Add(new ChartPointDto(day.ToString("MM-dd", CultureInfo.InvariantCulture), minutes));
        }

        int reporterCount = (await baseQuery.Clone()
            .Where(item => item.WorkDate == today)
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync()).Count;
        return new DashboardDto
        {
            TodayMinutes = todayMinutes,
            WeekMinutes = weekMinutes,
            MonthMinutes = monthMinutes,
            ActiveProjectCount = projects.Count(item => item.Status == ProjectStatus.Active),
            TodayReporterCount = reporterCount,
            WeeklyTrend = trend,
            ProjectDistribution = recentMonth.GroupBy(item => item.ProjectId)
                .Select(group =>
                {
                    Project? project = projects.FirstOrDefault(item => item.Id == group.Key);
                    return new ProjectTimeSummaryDto(group.Key, project?.Name ?? "未知项目", project?.Color ?? "#64748B",
                        group.Sum(item => item.Minutes), members.Count(item => item.ProjectId == group.Key));
                })
                .OrderByDescending(item => item.Minutes)
                .Take(8)
                .ToArray(),
            RecentEntries = recentEntries.Take(8).ToArray()
        };
    }

}
