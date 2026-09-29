using TimeMeet.Domain.Enums;

namespace TimeMeet.Domain.Entities;

public sealed class Meeting
{
    public Guid Id { get; set; }
    public required string ShortCode { get; set; }
    public required string OwnerToken { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public Guid? OrganizerId { get; set; }
    public required string OrganizerName { get; set; }
    public required string TimeZone { get; set; }
    public ParticipationMode ParticipationMode { get; set; } = ParticipationMode.LinkOnly; 
    public MeetingPhase Phase { get; set; } = MeetingPhase.AvailabilityCollection;
    public MeetingStatus Status { get; set; } = MeetingStatus.Draft; 
    public DateTimeOffset? Deadline { get; set; }
    public DateTimeOffset? FinalDeadline { get; set; }
    public bool AllowAnonymous { get; set; } = true;
    public bool AllowRegistration { get; set; }
    public DateOnly GridStartDate { get; set; }
    public DateOnly GridEndDate { get; set; }
    public TimeOnly GridFrom { get; set; }
    public TimeOnly GridTo { get; set; }
    public int GridStepMinutes { get; set; }
    public Guid? SelectedTimeSlotId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public User? Organizer { get; set; }
    public ICollection<GridCell> GridCells { get; set; } = [];
    public ICollection<TimeSlot> TimeSlots { get; set; } = [];
    public ICollection<Participant> Participants { get; set; } = [];
    public ICollection<Invitation> Invitations { get; set; } = [];

}
