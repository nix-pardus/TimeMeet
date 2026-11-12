using TimeMeet.Domain.Entities;
using TimeMeet.Domain.Enums;

namespace TimeMeet.Web.Components.Shared;

/// <summary>
/// Генератор демо-данных для отладочной страницы /heatmap-demo.
/// Не использовать в production-коде.
/// </summary>
public static class DemoMeetingFactory
{
    public static Meeting Create()
    {
        var startDate = new DateOnly(2026, 10, 12);
        var endDate = new DateOnly(2026, 10, 16);
        const int startHour = 8;
        const int endHour = 23;
        const int stepMinutes = 30;

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            ShortCode = "DEMO0001",
            OwnerToken = "demo-owner",
            Title = "Demo",
            OrganizerName = "Организатор",
            TimeZone = "UTC",
            GridStartDate = startDate,
            GridEndDate = endDate,
            GridStepMinutes = stepMinutes,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = MeetingStatus.Active
        };

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            for (var h = startHour; h < endHour; h++)
            {
                for (var s = 0; s < 60 / stepMinutes; s++)
                {
                    var t = date.ToDateTime(new TimeOnly(h, s * stepMinutes), DateTimeKind.Utc);
                    meeting.GridCells.Add(new GridCell
                    {
                        Id = Guid.NewGuid(),
                        MeetingId = meeting.Id,
                        StartTime = new DateTimeOffset(t),
                        EndTime = new DateTimeOffset(t.AddMinutes(stepMinutes))
                    });
                }
            }
        }

        AddParticipant(meeting, "Иван", (d, h) => h >= 9 && h < 13 ? 1 : 0);
        AddParticipant(meeting, "Мария", (d, h) =>
            (d <= 2 && h >= 14 && h < 18) || (d >= 3 && h >= 10 && h < 14) ? 1 : 0);
        AddParticipant(meeting, "Пётр", (d, h) =>
            d == 2 && h >= 15 && h < 20 ? 2 : h >= 15 && h < 20 ? 1 : 0);

        return meeting;
    }

    private static void AddParticipant(Meeting meeting, string name, Func<int, int, int> pattern)
    {
        var participant = new Participant
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            DisplayName = name,
            TimeZone = meeting.TimeZone,
            ParticipantToken = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var startDay = meeting.GridStartDate.DayNumber;

        foreach (var cell in meeting.GridCells)
        {
            var local = cell.StartTime.UtcDateTime;
            var dayIdx = (int)(DateOnly.FromDateTime(local).DayNumber - startDay);
            var status = pattern(dayIdx, local.Hour);
            if (status == 0) continue;

            participant.Availabilities.Add(new Availability
            {
                Id = Guid.NewGuid(),
                ParticipantId = participant.Id,
                GridCellId = cell.Id,
                Status = status == 1 ? AvailabilityStatus.Available : AvailabilityStatus.IfNeeded,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        meeting.Participants.Add(participant);
    }
}