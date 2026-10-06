using TimeMeet.Domain.Entities;
using TimeMeet.Domain.Enums;

namespace TimeMeet.Application.Meetings;

public sealed class CreateMeetingRequest
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string OrganizerName { get; init; }
    public required string TimeZone { get; init; }
    public DateOnly GridStartDate { get; init; }
    public DateOnly GridEndDate { get; init; }
    public int GridStepMinutes { get; init; }
    public RetentionMode RetentionMode { get; init; } = RetentionMode.KeepForever;
}

public sealed record CreatedMeeting(Meeting Meeting, string OwnerToken, string OrganizerParticipantToken);
public sealed record ParticipantMeeting(Meeting Meeting, Participant? Participant);
public sealed record CreatedParticipant(Participant Participant, string ParticipantToken);
public sealed record AvailabilitySelection(Guid TargetId, AvailabilityStatus Status);

public sealed record GridCellAnalysis(
    GridCell Cell,
    int AvailableCount,
    int IfNeededCount,
    int CannotCount,
    int Score,
    IReadOnlyCollection<string> AvailableParticipants,
    IReadOnlyCollection<string> IfNeededParticipants,
    IReadOnlyCollection<string> CannotParticipants);

public interface ICalendarExporter
{
    Task<byte[]> ExportAsync(Meeting meeting, CancellationToken cancellationToken = default);
}

public interface IMeetingQrCodeGenerator
{
    byte[] Generate(string content);
}

public interface IMeetingService
{
    Task<CreatedMeeting> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default);
    Task<Meeting?> GetForOwnerAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default);
    Task<ParticipantMeeting?> GetForParticipantAsync(string shortCode, string? participantToken, CancellationToken cancellationToken = default);
    Task<CreatedParticipant> CreateParticipantAsync(string shortCode, string displayName, string timeZone, CancellationToken cancellationToken = default);
    Task SaveAvailabilityAsync(string shortCode, string participantToken, IReadOnlyCollection<AvailabilitySelection> selections, CancellationToken cancellationToken = default);
    Task SetNoSuitableTimeAsync(string shortCode, string participantToken, bool value, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<GridCellAnalysis>> AnalyzeAvailabilityAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default);
    Task CloseAsync(string shortCode, string ownerToken, DateTimeOffset? selectedStartTime, DateTimeOffset? selectedEndTime, CancellationToken cancellationToken = default);
    Task ArchiveAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default);
    Task DeleteAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default);
}
