using TimeMeet.Domain.Entities;
using TimeMeet.Domain.Enums;

namespace TimeMeet.Application.Meetings;

public sealed class CreateMeetingRequest
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string OrganizerName { get; init; }
    public required string TimeZone { get; init; }
    public AvailabilityMode AvailabilityMode { get; init; }
    public ParticipationMode ParticipationMode { get; init; } = ParticipationMode.LinkOnly;
    public DateTimeOffset? Deadline { get; init; }
    public bool AllowAnonymous { get; init; } = true;
    public bool AllowRegistration { get; init; } = true;
    public IReadOnlyCollection<MeetingSlotRequest> Slots { get; init; } = [];
}

public sealed record MeetingSlotRequest(DateTimeOffset StartTime, DateTimeOffset EndTime);

public sealed record CreatedMeeting(Meeting Meeting, string OwnerToken);

public interface IMeetingService
{
    Task<CreatedMeeting> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default);
    Task<Meeting?> GetForOwnerAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default);
}
