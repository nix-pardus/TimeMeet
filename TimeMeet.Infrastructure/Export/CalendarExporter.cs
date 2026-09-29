using System.Text;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Entities;

namespace TimeMeet.Infrastructure.Export;

public sealed class CalendarExporter : ICalendarExporter
{
    public Task<byte[]> ExportAsync(Meeting meeting, TimeSlot slot, CancellationToken cancellationToken = default)
    {
        static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//TimeMeet//RU\r\nBEGIN:VEVENT\r\nUID:{slot.Id}@timemeet\r\nDTSTAMP:{Format(DateTimeOffset.UtcNow)}\r\nDTSTART:{Format(slot.StartTime)}\r\nDTEND:{Format(slot.EndTime)}\r\nSUMMARY:{Escape(meeting.Title)}\r\nDESCRIPTION:{Escape(meeting.Description ?? string.Empty)}\r\nORGANIZER;CN={Escape(meeting.OrganizerName)}:MAILTO:noreply@timemeet.local\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        return Task.FromResult(Encoding.UTF8.GetBytes(content));
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", string.Empty).Replace("\n", "\\n");
}