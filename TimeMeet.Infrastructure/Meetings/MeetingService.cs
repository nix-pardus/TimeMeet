using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Entities;
using TimeMeet.Domain.Enums;
using TimeMeet.Infrastructure.Data;

namespace TimeMeet.Infrastructure.Meetings;

public sealed class MeetingService(IDbContextFactory<TimeMeetDbContext> dbContextFactory) : IMeetingService
{
    private const string Base62 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public async Task<CreatedMeeting> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validate(request);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);
        var now = DateTimeOffset.UtcNow;
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(), ShortCode = await CreateUniqueShortCodeAsync(dbContext, cancellationToken), OwnerToken = CreateToken(32),
            Title = request.Title.Trim(), Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            OrganizerName = request.OrganizerName.Trim(), TimeZone = request.TimeZone, Status = MeetingStatus.Active,
            RetentionMode = request.RetentionMode,
            AllowAnonymous = true, AllowRegistration = false, GridStartDate = request.GridStartDate, GridEndDate = request.GridEndDate,
            GridStepMinutes = request.GridStepMinutes, CreatedAt = now
        };

        var organizerParticipant = new Participant
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            DisplayName = request.OrganizerName.Trim(),
            TimeZone = request.TimeZone,
            ParticipantToken = CreateToken(32),
            IsOrganizer = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        meeting.Participants.Add(organizerParticipant);

        for (var date = request.GridStartDate; date <= request.GridEndDate; date = date.AddDays(1))
        {
            var localDayStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var localDayEnd = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var utcStart = TimeZoneInfo.ConvertTimeToUtc(localDayStart, timeZone);
            var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localDayEnd, timeZone);

            for (var utcTime = utcStart; utcTime < utcEnd; utcTime = utcTime.AddMinutes(request.GridStepMinutes))
            {
                var utcNext = utcTime.AddMinutes(request.GridStepMinutes);
                var localStart = TimeZoneInfo.ConvertTimeFromUtc(utcTime, timeZone);
                var localEnd = TimeZoneInfo.ConvertTimeFromUtc(utcNext, timeZone);
                if (localStart.Date != date.ToDateTime(TimeOnly.MinValue).Date || utcNext > utcEnd) continue;

                meeting.GridCells.Add(new GridCell
                {
                    Id = Guid.NewGuid(),
                    MeetingId = meeting.Id,
                    StartTime = new DateTimeOffset(utcTime),
                    EndTime = new DateTimeOffset(utcNext)
                });
            }
        }

        dbContext.Meetings.Add(meeting);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreatedMeeting(meeting, meeting.OwnerToken, organizerParticipant.ParticipantToken);
    }

    public async Task<Meeting?> GetForOwnerAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Meetings.IgnoreQueryFilters().Include(x => x.GridCells).ThenInclude(x => x.Availabilities)
            .Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken);
    }

    public async Task<ParticipantMeeting?> GetForParticipantAsync(string shortCode, string? participantToken, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.IgnoreQueryFilters().Include(x => x.GridCells.OrderBy(x => x.StartTime))
            .Include(x => x.Participants).ThenInclude(x => x.Availabilities).SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken);
        if (meeting is null) return null;
        var participant = string.IsNullOrWhiteSpace(participantToken) ? null : meeting.Participants.SingleOrDefault(x => x.ParticipantToken == participantToken);
        return new ParticipantMeeting(meeting, participant);
    }

    public async Task<CreatedParticipant> CreateParticipantAsync(string shortCode, string displayName, string timeZone, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken) ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));
        ValidateVotingIsOpen(meeting);
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 50) throw new ArgumentException("Укажите имя длиной до 50 символов.", nameof(displayName));
        ValidateTimeZone(timeZone);
        if (await dbContext.Participants.CountAsync(x => x.MeetingId == meeting.Id, cancellationToken) >= 100) throw new InvalidOperationException("Достигнут лимит участников встречи.");
        var now = DateTimeOffset.UtcNow;
        var participant = new Participant { Id = Guid.NewGuid(), MeetingId = meeting.Id, DisplayName = displayName.Trim(), TimeZone = timeZone, ParticipantToken = CreateToken(32), CreatedAt = now, UpdatedAt = now };
        dbContext.Participants.Add(participant);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreatedParticipant(participant, participant.ParticipantToken);
    }

    public async Task SaveAvailabilityAsync(string shortCode, string participantToken, IReadOnlyCollection<AvailabilitySelection> selections, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.Include(x => x.GridCells).SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken) ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));
        ValidateVotingIsOpen(meeting);
        var participant = await dbContext.Participants.Include(x => x.Availabilities).SingleOrDefaultAsync(x => x.MeetingId == meeting.Id && x.ParticipantToken == participantToken, cancellationToken) ?? throw new UnauthorizedAccessException("Участник не найден.");
        if (participant.HasNoSuitableTime) throw new InvalidOperationException("Участник отметил, что ему не подходит ни одно время.");
        var targetIds = meeting.GridCells.Select(x => x.Id).ToHashSet();
        if (selections.Any(x => !targetIds.Contains(x.TargetId)) || selections.GroupBy(x => x.TargetId).Any(x => x.Count() > 1) || selections.Any(x => !Enum.IsDefined(x.Status))) throw new ArgumentException("Некорректные отметки доступности.", nameof(selections));
        var old = participant.Availabilities;
        dbContext.Availabilities.RemoveRange(old);
        dbContext.Availabilities.AddRange(selections.Select(x => new Availability { Id = Guid.NewGuid(), ParticipantId = participant.Id, GridCellId = x.TargetId, Status = x.Status, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }));
        participant.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        await SetRetentionStatusAsync(shortCode, ownerToken, MeetingStatus.Archived, cancellationToken);
    }

    public async Task DeleteAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        await SetRetentionStatusAsync(shortCode, ownerToken, MeetingStatus.Deleted, cancellationToken);
    }

    private async Task SetRetentionStatusAsync(
        string shortCode,
        string ownerToken,
        MeetingStatus status,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");

        if (meeting.Status is MeetingStatus.Archived or MeetingStatus.Deleted)
        {
            throw new InvalidOperationException("Встреча уже скрыта.");
        }

        var now = DateTimeOffset.UtcNow;
        meeting.Status = status;
        if (status == MeetingStatus.Archived)
        {
            meeting.ArchivedAt = now;
        }
        else
        {
            meeting.DeletedAt = now;
            meeting.DeletionReason = MeetingDeletionReason.Manual;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetNoSuitableTimeAsync(string shortCode, string participantToken, bool value, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));
        ValidateVotingIsOpen(meeting);

        var participant = await dbContext.Participants
            .Include(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.MeetingId == meeting.Id && x.ParticipantToken == participantToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Участник не найден.");

        participant.HasNoSuitableTime = value;
        participant.UpdatedAt = DateTimeOffset.UtcNow;

        if (value)
        {
            dbContext.Availabilities.RemoveRange(participant.Availabilities);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<GridCellAnalysis>> AnalyzeAvailabilityAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.Include(x => x.GridCells).ThenInclude(x => x.Availabilities).ThenInclude(x => x.Participant).Include(x => x.Participants).ThenInclude(x => x.Availabilities).SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken) ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");
        var voted = meeting.Participants.Where(x => x.HasNoSuitableTime || x.Availabilities.Count != 0).ToArray();
        return meeting.GridCells.OrderBy(x => x.StartTime).Select(cell =>
        {
            var available = cell.Availabilities.Where(x => x.Status == AvailabilityStatus.Available).ToArray();
            var needed = cell.Availabilities.Where(x => x.Status == AvailabilityStatus.IfNeeded).ToArray();
            var availableIds = available.Select(x => x.ParticipantId).ToHashSet();
            var neededIds = needed.Select(x => x.ParticipantId).ToHashSet();
            var cannot = voted.Where(x => !availableIds.Contains(x.Id) && !neededIds.Contains(x.Id)).ToArray();
            return new GridCellAnalysis(cell, available.Length, needed.Length, cannot.Length,
                available.Length * 100 + needed.Length * 20 - cannot.Length * 100,
                available.Select(x => x.Participant.DisplayName).ToArray(),
                needed.Select(x => x.Participant.DisplayName).ToArray(),
                cannot.Select(x => x.DisplayName).ToArray());
        }).OrderByDescending(x => x.Score).ThenBy(x => x.Cell.StartTime).ToArray();
    }

    public async Task CloseAsync(string shortCode, string ownerToken, DateTimeOffset? selectedStartTime, DateTimeOffset? selectedEndTime, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken) ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");
        if (meeting.Status != MeetingStatus.Active) throw new InvalidOperationException("Встреча уже закрыта.");
        if ((selectedStartTime is null) != (selectedEndTime is null)
            || (selectedStartTime is not null && selectedEndTime is not null && selectedStartTime.Value >= selectedEndTime.Value))
            throw new ArgumentException("Итоговое окно указано некорректно.");
        meeting.SelectedStartTime = selectedStartTime?.ToUniversalTime(); meeting.SelectedEndTime = selectedEndTime?.ToUniversalTime(); meeting.Status = MeetingStatus.Closed; meeting.ClosedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(CreateMeetingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 100) throw new ArgumentException("Название встречи обязательно и не должно превышать 100 символов.", nameof(request));
        if (request.Description?.Length > 1000) throw new ArgumentException("Описание не должно превышать 1000 символов.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OrganizerName) || request.OrganizerName.Trim().Length > 50) throw new ArgumentException("Укажите имя организатора длиной до 50 символов.", nameof(request));
        ValidateTimeZone(request.TimeZone);
        if (request.GridStepMinutes is not (15 or 30 or 60)) throw new ArgumentException("Шаг сетки должен быть 15, 30 или 60 минут.", nameof(request));
        if (request.GridEndDate < request.GridStartDate) throw new ArgumentException("Диапазон дат сетки указан некорректно.", nameof(request));
        if (request.GridStartDate < DateOnly.FromDateTime(DateTime.UtcNow)) throw new ArgumentException("Дата начала не может быть в прошлом.", nameof(request));
        var dayCount = request.GridEndDate.DayNumber - request.GridStartDate.DayNumber + 1;
        var maximumCellCount = dayCount * (24 * 60 / request.GridStepMinutes);
        if (maximumCellCount > 1500) throw new ArgumentException("Сетка не должна содержать более 1500 ячеек.", nameof(request));
    }

    private static void ValidateTimeZone(string timeZone) { if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _)) throw new ArgumentException("Укажите корректный часовой пояс.", nameof(timeZone)); }
    private static void ValidateVotingIsOpen(Meeting meeting) { if (meeting.Status != MeetingStatus.Active) throw new InvalidOperationException("Голосование закрыто."); }
    private static async Task<string> CreateUniqueShortCodeAsync(TimeMeetDbContext dbContext, CancellationToken ct) { for (var i = 0; i < 10; i++) { var code = CreateBase62Token(8); if (!await dbContext.Meetings.AnyAsync(x => x.ShortCode == code, ct)) return code; } throw new InvalidOperationException("Не удалось сгенерировать код встречи."); }
    private static string CreateToken(int byteCount) { Span<byte> bytes = stackalloc byte[byteCount]; RandomNumberGenerator.Fill(bytes); return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('='); }
    private static string CreateBase62Token(int length) { Span<byte> bytes = stackalloc byte[length]; RandomNumberGenerator.Fill(bytes); return string.Create(length, bytes.ToArray(), static (result, source) => { for (var i = 0; i < result.Length; i++) result[i] = Base62[source[i] % Base62.Length]; }); }
}
