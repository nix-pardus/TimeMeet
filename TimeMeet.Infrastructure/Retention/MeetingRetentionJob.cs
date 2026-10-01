using Microsoft.EntityFrameworkCore;
using TimeMeet.Domain.Enums;
using TimeMeet.Infrastructure.Data;

namespace TimeMeet.Infrastructure.Retention;

public sealed class MeetingRetentionJob(TimeMeetDbContext dbContext)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var meetings = await dbContext.Meetings
            .IgnoreQueryFilters()
            .Include(x => x.GridCells).ThenInclude(x => x.Availabilities)
            .Include(x => x.Participants).ThenInclude(x => x.Availabilities)
            .Include(x => x.Invitations)
            .ToListAsync(cancellationToken);

        foreach (var meeting in meetings)
        {
            if (meeting.Status == MeetingStatus.Deleted)
            {
                if (meeting.DeletedAt is not null && meeting.DeletedAt.Value <= now.AddDays(-30))
                    RemoveMeeting(meeting);
                continue;
            }

            if (meeting.RetentionMode == RetentionMode.DeleteAfterLastDay)
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(meeting.TimeZone);
                var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
                if (localDate > meeting.GridEndDate.AddDays(1))
                {
                    RemoveMeeting(meeting);
                    continue;
                }
            }
            else if (meeting.Status == MeetingStatus.Active || meeting.Status == MeetingStatus.Closed)
            {
                var lastActivity = meeting.ClosedAt ?? meeting.CreatedAt;
                if (lastActivity <= now.AddDays(-90))
                {
                    meeting.Status = MeetingStatus.Archived;
                    meeting.ArchivedAt = now;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void RemoveMeeting(Domain.Entities.Meeting meeting)
    {
        dbContext.Availabilities.RemoveRange(
            meeting.GridCells.SelectMany(x => x.Availabilities)
                .Concat(meeting.Participants.SelectMany(x => x.Availabilities)));
        dbContext.Invitations.RemoveRange(meeting.Invitations);
        dbContext.GridCells.RemoveRange(meeting.GridCells);
        dbContext.Participants.RemoveRange(meeting.Participants);
        dbContext.Meetings.Remove(meeting);
    }
}
