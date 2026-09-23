namespace TimeMeet.Domain.Entities;

public sealed class Participant
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid? UserId { get; set; }
    public required string DisplayName { get; set; }
    public required string TimeZone { get; set; }
    public required string ParticipantToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Meeting Meeting { get; set; } = null!; public User? User { get; set; }
    public ICollection<Availability> Availabilities { get; set; } = []; 
    public ICollection<Invitation> Invitations { get; set; } = [];

}
