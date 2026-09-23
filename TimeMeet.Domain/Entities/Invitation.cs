using TimeMeet.Domain.Enums;

namespace TimeMeet.Domain.Entities;

public sealed class Invitation
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public InvitationChannel Channel { get; set; }
    public string? Contact { get; set; }
    public string? DisplayName { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending; public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public Guid? ParticipantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Meeting Meeting { get; set; } = null!; public Participant? Participant { get; set; }
}
