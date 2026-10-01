using TimeMeet.Domain.Enums;

namespace TimeMeet.Domain.Entities;

public sealed class Availability
{
    public Guid Id { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid GridCellId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public AvailabilityStatus Status { get; set; }
    public Participant Participant { get; set; } = null!;
    public GridCell GridCell { get; set; } = null!;

}
