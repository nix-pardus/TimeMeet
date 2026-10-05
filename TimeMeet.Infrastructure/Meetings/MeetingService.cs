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
            Phase = MeetingPhase.AvailabilityCollection, Deadline = request.Deadline?.ToUniversalTime(),
            AllowAnonymous = true, AllowRegistration = false, GridStartDate = request.GridStartDate, GridEndDate = request.GridEndDate,
            GridFrom = request.GridFrom, GridTo = request.GridTo, GridStepMinutes = request.GridStepMinutes, CreatedAt = now
        };

        for (var date = request.GridStartDate; date <= request.GridEndDate; date = date.AddDays(1))
        {
            for (var time = request.GridFrom; time < request.GridTo; time = time.AddMinutes(request.GridStepMinutes))
            {
                var localStart = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
                var localEnd = DateTime.SpecifyKind(date.ToDateTime(time.AddMinutes(request.GridStepMinutes)), DateTimeKind.Unspecified);
                if (localEnd > date.ToDateTime(request.GridTo)) break;
                meeting.GridCells.Add(new GridCell { Id = Guid.NewGuid(), MeetingId = meeting.Id, StartTime = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone)), EndTime = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone)) });
            }
        }

        dbContext.Meetings.Add(meeting);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreatedMeeting(meeting, meeting.OwnerToken);
    }

    public async Task<Meeting?> GetForOwnerAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Meetings.Include(x => x.GridCells).ThenInclude(x => x.Availabilities)
            .Include(x => x.TimeSlots).ThenInclude(x => x.Availabilities)
            .Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken);
    }

    public async Task<ParticipantMeeting?> GetForParticipantAsync(string shortCode, string? participantToken, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.Include(x => x.GridCells.OrderBy(x => x.StartTime)).Include(x => x.TimeSlots.OrderBy(x => x.StartTime))
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
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 100) throw new ArgumentException("Укажите имя длиной до 100 символов.", nameof(displayName));
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
        var meeting = await dbContext.Meetings.Include(x => x.GridCells).Include(x => x.TimeSlots).SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken) ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));
        ValidateVotingIsOpen(meeting);
        var participant = await dbContext.Participants.Include(x => x.Availabilities).SingleOrDefaultAsync(x => x.MeetingId == meeting.Id && x.ParticipantToken == participantToken, cancellationToken) ?? throw new UnauthorizedAccessException("Участник не найден.");
        var targetIds = meeting.Phase == MeetingPhase.AvailabilityCollection ? meeting.GridCells.Select(x => x.Id).ToHashSet() : meeting.TimeSlots.Select(x => x.Id).ToHashSet();
        if (selections.Any(x => !targetIds.Contains(x.TargetId)) || selections.GroupBy(x => x.TargetId).Any(x => x.Count() > 1) || selections.Any(x => !Enum.IsDefined(x.Status))) throw new ArgumentException("Некорректные отметки доступности.", nameof(selections));
        var old = participant.Availabilities.Where(x => meeting.Phase == MeetingPhase.AvailabilityCollection ? x.GridCellId is not null : x.TimeSlotId is not null);
        dbContext.Availabilities.RemoveRange(old);
        dbContext.Availabilities.AddRange(selections.Select(x => new Availability { Id = Guid.NewGuid(), ParticipantId = participant.Id, GridCellId = meeting.Phase == MeetingPhase.AvailabilityCollection ? x.TargetId : null, TimeSlotId = meeting.Phase == MeetingPhase.FinalSlotSelection ? x.TargetId : null, Status = x.Status, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }));
        participant.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<GridCellAnalysis>> AnalyzeAvailabilityAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.Include(x => x.GridCells).ThenInclude(x => x.Availabilities).ThenInclude(x => x.Participant).SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken) ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");
        return meeting.GridCells.OrderBy(x => x.StartTime).Select(cell => { var answers = cell.Availabilities; var available = answers.Where(x => x.Status == AvailabilityStatus.Available).ToArray(); var needed = answers.Count(x => x.Status == AvailabilityStatus.IfNeeded); var unavailable = answers.Count(x => x.Status == AvailabilityStatus.Unavailable); return new GridCellAnalysis(cell, available.Length, needed, unavailable, available.Length * 100 + needed * 20 - unavailable * 100, available.Select(x => x.Participant.DisplayName).ToArray(), answers.Where(x => x.Status == AvailabilityStatus.Unavailable).Select(x => x.Participant.DisplayName).ToArray()); }).OrderByDescending(x => x.Score).ThenBy(x => x.Cell.StartTime).ToArray();
    }

    public async Task StartFinalPhaseAsync(string shortCode, string ownerToken, FinalPhaseRequest request, CancellationToken cancellationToken = default)
    {
        await using var operationDbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await operationDbContext.Database.BeginTransactionAsync(cancellationToken);

        var meeting = await operationDbContext.Meetings
            .AsNoTracking()
            .Include(x => x.GridCells)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");
        if (meeting.Phase != MeetingPhase.AvailabilityCollection || meeting.Status != MeetingStatus.Active) throw new InvalidOperationException("Переход в финальную фазу недоступен.");
        if (request.GridCellIds.Count is < 2 or > 5 || request.GridCellIds.Distinct().Count() != request.GridCellIds.Count) throw new ArgumentException("Выберите от 2 до 5 разных интервалов.", nameof(request));
        var cells = meeting.GridCells.Where(x => request.GridCellIds.Contains(x.Id)).ToArray();
        if (cells.Length != request.GridCellIds.Count) throw new ArgumentException("Выбрана ячейка другой встречи.", nameof(request));
        if (request.FinalDeadline is not null && request.FinalDeadline <= DateTimeOffset.UtcNow) throw new ArgumentException("Финальный дедлайн должен быть в будущем.", nameof(request));

        var changedRows = await operationDbContext.Meetings
            .Where(x => x.Id == meeting.Id
                && x.ShortCode == shortCode
                && x.OwnerToken == ownerToken
                && x.Phase == MeetingPhase.AvailabilityCollection
                && x.Status == MeetingStatus.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Phase, MeetingPhase.FinalSlotSelection)
                .SetProperty(x => x.FinalDeadline, request.FinalDeadline?.ToUniversalTime()), cancellationToken);

        if (changedRows != 1)
        {
            throw new InvalidOperationException("Встреча уже была переведена в другую фазу или удалена.");
        }

        operationDbContext.TimeSlots.AddRange(cells.Select(cell => new TimeSlot
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            SourceGridCellId = cell.Id,
            StartTime = cell.StartTime,
            EndTime = cell.EndTime
        }));

        await operationDbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CloseAsync(string shortCode, string ownerToken, Guid selectedTimeSlotId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings.Include(x => x.TimeSlots).SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken) ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");
        if (meeting.Status != MeetingStatus.Active || meeting.Phase != MeetingPhase.FinalSlotSelection || meeting.TimeSlots.All(x => x.Id != selectedTimeSlotId)) throw new InvalidOperationException("Нельзя закрыть встречу без корректного финального слота.");
        meeting.SelectedTimeSlotId = selectedTimeSlotId; meeting.Status = MeetingStatus.Closed; meeting.ClosedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(CreateMeetingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200) throw new ArgumentException("Название встречи обязательно и не должно превышать 200 символов.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OrganizerName) || request.OrganizerName.Trim().Length > 100) throw new ArgumentException("Укажите имя организатора длиной до 100 символов.", nameof(request));
        ValidateTimeZone(request.TimeZone);
        if (request.GridStepMinutes is not (30 or 60)) throw new ArgumentException("Шаг сетки должен быть 30 или 60 минут.", nameof(request));
        if (request.GridEndDate < request.GridStartDate || request.GridTo <= request.GridFrom) throw new ArgumentException("Диапазон сетки указан некорректно.", nameof(request));
        if (request.Deadline is not null && request.Deadline <= DateTimeOffset.UtcNow) throw new ArgumentException("Дедлайн должен быть в будущем.", nameof(request));
    }

    private static void ValidateTimeZone(string timeZone) { if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _)) throw new ArgumentException("Укажите корректный часовой пояс.", nameof(timeZone)); }
    private static void ValidateVotingIsOpen(Meeting meeting) { if (meeting.Status != MeetingStatus.Active || (meeting.Phase == MeetingPhase.AvailabilityCollection ? meeting.Deadline : meeting.FinalDeadline) <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Голосование закрыто или срок голосования истёк."); }
    private static async Task<string> CreateUniqueShortCodeAsync(TimeMeetDbContext dbContext, CancellationToken ct) { for (var i = 0; i < 10; i++) { var code = CreateBase62Token(8); if (!await dbContext.Meetings.AnyAsync(x => x.ShortCode == code, ct)) return code; } throw new InvalidOperationException("Не удалось сгенерировать код встречи."); }
    private static string CreateToken(int byteCount) { Span<byte> bytes = stackalloc byte[byteCount]; RandomNumberGenerator.Fill(bytes); return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('='); }
    private static string CreateBase62Token(int length) { Span<byte> bytes = stackalloc byte[length]; RandomNumberGenerator.Fill(bytes); return string.Create(length, bytes.ToArray(), static (result, source) => { for (var i = 0; i < result.Length; i++) result[i] = Base62[source[i] % Base62.Length]; }); }
}
