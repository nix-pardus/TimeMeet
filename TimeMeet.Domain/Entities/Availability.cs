using TimeMeet.Domain.Enums;

namespace TimeMeet.Domain.Entities;

public sealed class Availability
{
    public Guid Id { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid? TimeSlotId { get; set; }
    public DateTimeOffset? FreeFrom { get; set; }
    public DateTimeOffset? FreeTo { get; set; }
    public AvailabilityStatus Status { get; set; }
    public Participant Participant { get; set; } = null!; public TimeSlot? TimeSlot { get; set; }

}
