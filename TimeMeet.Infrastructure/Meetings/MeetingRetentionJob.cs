using Microsoft.EntityFrameworkCore;
using TimeMeet.Domain.Enums;
using TimeMeet.Infrastructure.Data;

namespace TimeMeet.Infrastructure.Meetings;

/// <summary>
/// Hangfire-задача обработки жизненного цикла встреч.
/// </summary>
public sealed class MeetingRetentionJob(
    IDbContextFactory<TimeMeetDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var todayByZone = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);

        var meetings = await dbContext.Meetings
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        foreach (var meeting in meetings)
        {
            if (meeting.RetentionMode == RetentionMode.DeleteAfterLastDay
                && IsDeletionDateReached(meeting, now, todayByZone))
            {
                // Сначала помечаем запись Deleted. Физическое удаление выполняется
                // ниже после истечения 30-дневного периода восстановления.
                if (meeting.Status != MeetingStatus.Deleted)
                {
                    meeting.Status = MeetingStatus.Deleted;
                    meeting.DeletedAt = now;
                    meeting.DeletionReason = MeetingDeletionReason.RetentionExpired;
                }

                continue;
            }

            if (meeting.RetentionMode == RetentionMode.KeepForever
                && meeting.Status is not (MeetingStatus.Archived or MeetingStatus.Deleted)
                && IsArchiveDateReached(meeting, now, todayByZone))
            {
                meeting.Status = MeetingStatus.Archived;
                meeting.ArchivedAt = now;
            }
        }

        var permanentlyDeleted = meetings
            .Where(x => x.Status == MeetingStatus.Deleted
                && x.DeletedAt <= now.AddDays(-30))
            .ToArray();

        if (permanentlyDeleted.Length > 0)
        {
            dbContext.Meetings.RemoveRange(permanentlyDeleted);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsDeletionDateReached(
        TimeMeet.Domain.Entities.Meeting meeting,
        DateTimeOffset now,
        IDictionary<string, DateOnly> todayByZone)
    {
        // По ТЗ удаление выполняется после окончания дня, следующего за GridEndDate.
        return GetLocalToday(meeting.TimeZone, now, todayByZone) >= meeting.GridEndDate.AddDays(2);
    }

    private static bool IsArchiveDateReached(
        TimeMeet.Domain.Entities.Meeting meeting,
        DateTimeOffset now,
        IDictionary<string, DateOnly> todayByZone)
    {
        // 90 дней отсчитываются от последней даты временной сетки.
        return GetLocalToday(meeting.TimeZone, now, todayByZone) >= meeting.GridEndDate.AddDays(90);
    }

    private static DateOnly GetLocalToday(
        string timeZoneId,
        DateTimeOffset now,
        IDictionary<string, DateOnly> todayByZone)
    {
        if (!todayByZone.TryGetValue(timeZoneId, out var localToday))
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
            todayByZone[timeZoneId] = localToday;
        }

        return localToday;
    }
}
