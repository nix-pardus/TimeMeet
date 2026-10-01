using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Entities;
using TimeMeet.Domain.Enums;
using TimeMeet.Infrastructure.Data;

namespace TimeMeet.Infrastructure.Meetings;

public sealed class MeetingService(TimeMeetDbContext dbContext) : IMeetingService
{
    private const string Base62 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public async Task<CreatedMeeting> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var timeZone = FindTimeZone(request.TimeZone);
        var now = DateTimeOffset.UtcNow;
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            ShortCode = await CreateUniqueShortCodeAsync(cancellationToken),
            OwnerToken = CreateToken(32),
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            OrganizerName = request.OrganizerName.Trim(),
            TimeZone = request.TimeZone,
            RetentionMode = request.RetentionMode,
            Status = MeetingStatus.Active,
            AllowAnonymous = true,
            AllowRegistration = false,
            GridStartDate = request.GridStartDate,
            GridEndDate = request.GridEndDate,
            GridStepMinutes = request.GridStepMinutes,
            CreatedAt = now
        };

        for (var date = request.GridStartDate; date <= request.GridEndDate; date = date.AddDays(1))
        {
            var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var localEnd = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
            var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);

            for (var start = utcStart; start < utcEnd; start = start.AddMinutes(request.GridStepMinutes))
            {
                var end = start.AddMinutes(request.GridStepMinutes);
                if (end > utcEnd) break;
                meeting.GridCells.Add(new GridCell
                {
                    Id = Guid.NewGuid(),
                    MeetingId = meeting.Id,
                    StartTime = new DateTimeOffset(start),
                    EndTime = new DateTimeOffset(end)
                });
            }
        }

        dbContext.Meetings.Add(meeting);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreatedMeeting(meeting, meeting.OwnerToken);
    }

    public Task<Meeting?> GetForOwnerAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default) =>
        dbContext.Meetings
            .Include(x => x.GridCells).ThenInclude(x => x.Availabilities).ThenInclude(x => x.Participant)
            .Include(x => x.Participants).ThenInclude(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken);

    public async Task<ParticipantMeeting?> GetForParticipantAsync(string shortCode, string? participantToken, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings
            .Include(x => x.GridCells.OrderBy(x => x.StartTime))
            .Include(x => x.Participants).ThenInclude(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken);

        if (meeting is null)
        {
            meeting = await dbContext.Meetings
                .IgnoreQueryFilters()
                .Include(x => x.Participants)
                .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.Status == MeetingStatus.Deleted, cancellationToken);
        }

        if (meeting is null) return null;

        var participant = string.IsNullOrWhiteSpace(participantToken)
            ? null
            : meeting.Participants.SingleOrDefault(x => x.ParticipantToken == participantToken);
        return new ParticipantMeeting(meeting, participant);
    }

    public async Task<CreatedParticipant> CreateParticipantAsync(string shortCode, string displayName, string timeZone, string? ownerToken = null, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings.SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));
        EnsureVotingOpen(meeting);
        ValidateDisplayName(displayName);
        ValidateTimeZone(timeZone);

        var participantCount = await dbContext.Participants.CountAsync(x => x.MeetingId == meeting.Id, cancellationToken);
        if (participantCount >= 100) throw new InvalidOperationException("Достигнут лимит участников встречи.");

        var now = DateTimeOffset.UtcNow;
        var participant = new Participant
        {
            Id = Guid.NewGuid(), MeetingId = meeting.Id, DisplayName = displayName.Trim(),
            TimeZone = timeZone, ParticipantToken = CreateToken(32), CreatedAt = now, UpdatedAt = now,
            IsOrganizer = !string.IsNullOrWhiteSpace(ownerToken) && SecureEquals(ownerToken, meeting.OwnerToken)
        };
        dbContext.Participants.Add(participant);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreatedParticipant(participant, participant.ParticipantToken);
    }

    public async Task SaveAvailabilityAsync(string shortCode, string participantToken, IReadOnlyCollection<AvailabilitySelection> selections, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings.Include(x => x.GridCells)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));
        EnsureVotingOpen(meeting);
        var participant = await dbContext.Participants.Include(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.MeetingId == meeting.Id && x.ParticipantToken == participantToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Участник не найден.");

        var cellIds = meeting.GridCells.Select(x => x.Id).ToHashSet();
        if (selections.Count > cellIds.Count || selections.Any(x => !cellIds.Contains(x.GridCellId)) ||
            selections.GroupBy(x => x.GridCellId).Any(x => x.Count() > 1) || selections.Any(x => !Enum.IsDefined(x.Status)))
            throw new ArgumentException("Некорректные отметки доступности.", nameof(selections));

        dbContext.Availabilities.RemoveRange(participant.Availabilities);
        var now = DateTimeOffset.UtcNow;
        dbContext.Availabilities.AddRange(selections.Select(x => new Availability
        {
            Id = Guid.NewGuid(), ParticipantId = participant.Id, GridCellId = x.GridCellId,
            Status = x.Status, CreatedAt = now, UpdatedAt = now
        }));
        participant.HasNoSuitableTime = false;
        participant.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> RegenerateOwnerTokenAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings.SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Ссылка управления недействительна.");
        if (meeting.Status == MeetingStatus.Deleted) throw new InvalidOperationException("Встреча удалена.");

        meeting.OwnerToken = CreateToken(32);
        await dbContext.SaveChangesAsync(cancellationToken);
        return meeting.OwnerToken;
    }

    public async Task DeleteAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings
            .Include(x => x.GridCells).ThenInclude(x => x.Availabilities)
            .Include(x => x.Participants).ThenInclude(x => x.Availabilities)
            .Include(x => x.Invitations)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Ссылка управления недействительна.");

        if (meeting.Status == MeetingStatus.Deleted) return;
        dbContext.Availabilities.RemoveRange(meeting.GridCells.SelectMany(x => x.Availabilities).Concat(meeting.Participants.SelectMany(x => x.Availabilities)));
        dbContext.Invitations.RemoveRange(meeting.Invitations);
        dbContext.GridCells.RemoveRange(meeting.GridCells);
        dbContext.Participants.RemoveRange(meeting.Participants);
        meeting.Status = MeetingStatus.Deleted;
        meeting.DeletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetNoSuitableTimeAsync(string shortCode, string participantToken, bool value, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings.SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));
        EnsureVotingOpen(meeting);
        var participant = await dbContext.Participants.Include(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.MeetingId == meeting.Id && x.ParticipantToken == participantToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Участник не найден.");
        participant.HasNoSuitableTime = value;
        participant.UpdatedAt = DateTimeOffset.UtcNow;
        if (value) dbContext.Availabilities.RemoveRange(participant.Availabilities);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<GridCellAnalysis>> AnalyzeAvailabilityAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings
            .Include(x => x.GridCells).ThenInclude(x => x.Availabilities).ThenInclude(x => x.Participant)
            .Include(x => x.Participants).ThenInclude(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));

        var voters = meeting.Participants.Where(x => x.HasNoSuitableTime || x.Availabilities.Count > 0).ToArray();
        return meeting.GridCells.Select(cell =>
        {
            var available = cell.Availabilities.Where(x => x.Status == AvailabilityStatus.Available).Select(x => x.Participant.DisplayName).ToArray();
            var ifNeeded = cell.Availabilities.Where(x => x.Status == AvailabilityStatus.IfNeeded).Select(x => x.Participant.DisplayName).ToArray();
            var cannot = voters.Where(x => !cell.Availabilities.Any(a => a.ParticipantId == x.Id)).Select(x => x.DisplayName).ToArray();
            var score = available.Length * 100 + ifNeeded.Length * 20 - cannot.Length * 100;
            return new GridCellAnalysis(cell, available.Length, ifNeeded.Length, cannot.Length, score, available, ifNeeded, cannot);
        }).OrderByDescending(x => x.Score).ThenByDescending(x => x.AvailableCount).ThenBy(x => x.CannotCount).ThenByDescending(x => x.IfNeededCount).ThenBy(x => x.Cell.StartTime).ToArray();
    }

    public async Task CloseAsync(string shortCode, string ownerToken, DateTimeOffset? selectedStartTime, DateTimeOffset? selectedEndTime, CancellationToken cancellationToken = default)
    {
        var meeting = await dbContext.Meetings.SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Ссылка управления недействительна.");
        if (meeting.Status != MeetingStatus.Active) throw new InvalidOperationException("Встреча уже закрыта.");
        if ((selectedStartTime is null) != (selectedEndTime is null) || selectedStartTime >= selectedEndTime)
            throw new ArgumentException("Укажите корректное итоговое окно.");
        meeting.SelectedStartTime = selectedStartTime?.ToUniversalTime();
        meeting.SelectedEndTime = selectedEndTime?.ToUniversalTime();
        meeting.Status = MeetingStatus.Closed;
        meeting.ClosedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(CreateMeetingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 100) throw new ArgumentException("Название встречи обязательно и должно содержать не более 100 символов.");
        if (string.IsNullOrWhiteSpace(request.Description) is false && request.Description!.Length > 1000) throw new ArgumentException("Описание должно содержать не более 1000 символов.");
        ValidateDisplayName(request.OrganizerName);
        ValidateTimeZone(request.TimeZone);
        if (request.GridStartDate < DateOnly.FromDateTime(DateTime.UtcNow)) throw new ArgumentException("Дата начала не может быть в прошлом.");
        if (request.GridStartDate > request.GridEndDate) throw new ArgumentException("Дата начала не может быть позже даты окончания.");
        if (request.GridStepMinutes is not (15 or 30 or 60)) throw new ArgumentException("Шаг сетки должен быть 15, 30 или 60 минут.");
        var days = request.GridEndDate.DayNumber - request.GridStartDate.DayNumber + 1;
        if (days * (24 * 60 / request.GridStepMinutes) > 1500) throw new ArgumentException("Сетка не должна содержать более 1500 ячеек.");
    }

    private static void ValidateDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 50) throw new ArgumentException("Имя должно содержать от 1 до 50 символов.");
    }

    private static TimeZoneInfo FindTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Указан неизвестный часовой пояс.", nameof(id)); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("Указан некорректный часовой пояс.", nameof(id)); }
    }

    private static void ValidateTimeZone(string id) => _ = FindTimeZone(id);
    private static void EnsureVotingOpen(Meeting meeting) { if (meeting.Status != MeetingStatus.Active) throw new InvalidOperationException("Голосование закрыто."); }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private async Task<string> CreateUniqueShortCodeAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var code = CreateBase62Token(8);
            if (!await dbContext.Meetings.AnyAsync(x => x.ShortCode == code, ct)) return code;
        }
        throw new InvalidOperationException("Не удалось сгенерировать код встречи.");
    }

    private static string CreateBase62Token(int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(length, bytes.ToArray(), (result, source) =>
        {
            for (var i = 0; i < result.Length; i++) result[i] = Base62[source[i] % Base62.Length];
        });
    }

    private static string CreateToken(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
