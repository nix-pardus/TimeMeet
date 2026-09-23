namespace TimeMeet.Domain.Entities;

public sealed class TimeSlot
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public Meeting Meeting { get; set; } = null!; 
    public ICollection<Availability> Availabilities { get; set; } = [];

}
