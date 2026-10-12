using System.Text;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Entities;

namespace TimeMeet.Infrastructure.Export;

public sealed class CalendarExporter : ICalendarExporter
{
    public Task<byte[]> ExportAsync(Meeting meeting, CancellationToken cancellationToken = default)
    {
        if (meeting.SelectedStartTime is null || meeting.SelectedEndTime is null) 
            throw new InvalidOperationException("У встречи нет выбранного времени.");

        static string Format(DateTimeOffset value) => 
            value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

        var content = $"BEGIN:VCALENDAR\r\n"+
            $"VERSION:2.0\r\n"+
            $"PRODID:-//TimeMeet//RU\r\n"+
            $"BEGIN:VEVENT\r\n"+
            $"UID:{meeting.Id}@timemeet\r\n"+
            $"DTSTAMP:{Format(DateTimeOffset.UtcNow)}\r\n"+
            $"DTSTART:{Format(meeting.SelectedStartTime.Value)}\r\n"+
            $"DTEND:{Format(meeting.SelectedEndTime.Value)}\r\n"+
            $"SUMMARY:{Escape(meeting.Title)}\r\n"+
            $"DESCRIPTION:{Escape(meeting.Description ?? string.Empty)}\r\n"+
            $"ORGANIZER;CN={Escape(meeting.OrganizerName)}:MAILTO:noreply@timemeet.local\r\n"+
            $"END:VEVENT\r\n"+
            $"END:VCALENDAR\r\n";

        return Task.FromResult(Encoding.UTF8.GetBytes(content));
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r", string.Empty)
        .Replace("\n", "\\n");
}