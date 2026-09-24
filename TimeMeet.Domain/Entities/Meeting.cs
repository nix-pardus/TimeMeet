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
    public string? OrganizerName { get; set; }
    public required string TimeZone { get; set; }
    public ParticipationMode ParticipationMode { get; set; } = ParticipationMode.LinkOnly; 
    public AvailabilityMode AvailabilityMode { get; set; }
    public MeetingStatus Status { get; set; } = MeetingStatus.Draft; 
    public DateTimeOffset? Deadline { get; set; }
    public bool AllowAnonymous { get; set; } = true; public bool AllowRegistration { get; set; } = true; 
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public User? Organizer { get; set; }
    public ICollection<TimeSlot> TimeSlots { get; set; } = []; public ICollection<Participant> Participants { get; set; } = []; public ICollection<Invitation> Invitations { get; set; } = [];

}
