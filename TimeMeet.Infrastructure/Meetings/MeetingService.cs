using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Entities;
using TimeMeet.Domain.Enums;
using TimeMeet.Infrastructure.Data;

namespace TimeMeet.Infrastructure.Meetings;

public sealed class MeetingService(IDbContextFactory<TimeMeetDbContext> dbContextFactory) : IMeetingService
{
    private const string Base62 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int MaxParticipantCellCount = 50_000;
    private static readonly PasswordHasher<Meeting> MeetingPasswordHasher = new();
    private static readonly PasswordHasher<Participant> ParticipantPasswordHasher = new();

    public async Task<CreatedMeeting> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validate(request);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);
        var now = DateTimeOffset.UtcNow;
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            ShortCode = await CreateUniqueShortCodeAsync(dbContext, cancellationToken),
            OwnerToken = CreateToken(32),
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            OrganizerName = request.OrganizerName.Trim(),
            TimeZone = request.TimeZone,
            Status = MeetingStatus.Active,
            RetentionMode = request.RetentionMode,
            AllowAnonymous = true,
            AllowRegistration = false,
            GridStartDate = request.GridStartDate,
            GridEndDate = request.GridEndDate,
            GridStepMinutes = request.GridStepMinutes,
            CreatedAt = now
        };

        if (!string.IsNullOrWhiteSpace(request.OrganizerPassword))
            meeting.OwnerPasswordHash = MeetingPasswordHasher.HashPassword(meeting, request.OrganizerPassword);

        var organizerParticipant = new Participant
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            DisplayName = request.OrganizerName.Trim(),
            TimeZone = request.TimeZone,
            ParticipantToken = CreateToken(32),
            ParticipantPasswordHash = null,
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

        return await dbContext.Meetings
            .IgnoreQueryFilters()
            .Include(x => x.GridCells)
            .ThenInclude(x => x.Availabilities)
            .Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken);
    }

    public async Task<ParticipantMeeting?> GetForParticipantAsync(string shortCode, string? participantToken, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken);

        var meeting = await dbContext.Meetings
            .IgnoreQueryFilters()
            .Include(x => x.GridCells
            .OrderBy(x => x.StartTime))
            .Include(x => x.Participants)
            .ThenInclude(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken);

        if (meeting is null)
            return null;

        var participant = string.IsNullOrWhiteSpace(participantToken)
            ? null
            : meeting.Participants.SingleOrDefault(x => x.ParticipantToken == participantToken);

        return new ParticipantMeeting(meeting, participant);
    }

    public async Task<CreatedParticipant> CreateParticipantAsync(string shortCode, string displayName, string timeZone, string? password = null, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken);

        var meeting = await dbContext.Meetings
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));

        ValidateVotingIsOpen(meeting);

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 50)
            throw new ArgumentException("Укажите имя длиной до 50 символов.", nameof(displayName));

        ValidateOptionalPassword(password);

        ValidateTimeZone(timeZone);

        if (await dbContext.Participants.CountAsync(x => x.MeetingId == meeting.Id, cancellationToken) >= 100)
            throw new InvalidOperationException("Достигнут лимит участников встречи.");

        var cellCount = await dbContext.GridCells.CountAsync(x => x.MeetingId == meeting.Id, cancellationToken);
        var participantCount = await dbContext.Participants.CountAsync(x => x.MeetingId == meeting.Id, cancellationToken);
        if ((long)(participantCount + 1) * cellCount > MaxParticipantCellCount)
            throw new InvalidOperationException("Превышен лимит объёма голосования для этой встречи.");

        var normalizedDisplayName = displayName.Trim();
        var normalizedDisplayNameForComparison = normalizedDisplayName.ToUpperInvariant();
        if (await dbContext.Participants.AnyAsync(
                x => x.MeetingId == meeting.Id && x.DisplayName.ToUpper() == normalizedDisplayNameForComparison,
                cancellationToken))
        {
            throw new InvalidOperationException("Это имя уже используется. Добавьте фамилию или инициал.");
        }

        var now = DateTimeOffset.UtcNow;

        var participant = new Participant
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            DisplayName = normalizedDisplayName,
            TimeZone = timeZone,
            ParticipantToken = CreateToken(32),
            CreatedAt = now,
            UpdatedAt = now
        };

        if (!string.IsNullOrWhiteSpace(password))
            participant.ParticipantPasswordHash = ParticipantPasswordHasher.HashPassword(participant, password);

        dbContext.Participants.Add(participant);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Participants_MeetingId_DisplayName" })
        {
            throw new InvalidOperationException("Это имя уже используется. Добавьте фамилию или инициал.", exception);
        }
        return new CreatedParticipant(participant, participant.ParticipantToken);
    }

    public async Task<AuthenticatedParticipant> AuthenticateParticipantAsync(string shortCode, string displayName, string password, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));

        ValidateVotingIsOpen(meeting);

        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(password))
            throw new UnauthorizedAccessException("Укажите имя и пароль участника.");

        var participant = await dbContext.Participants
            .SingleOrDefaultAsync(x => x.MeetingId == meeting.Id && x.DisplayName == displayName.Trim(), cancellationToken)
            ?? throw new UnauthorizedAccessException("Неверное имя или пароль.");

        var passwordIsValid = participant.IsOrganizer
            ? meeting.OwnerPasswordHash is not null &&
              MeetingPasswordHasher.VerifyHashedPassword(meeting, meeting.OwnerPasswordHash, password) != PasswordVerificationResult.Failed
            : participant.ParticipantPasswordHash is not null &&
              ParticipantPasswordHasher.VerifyHashedPassword(participant, participant.ParticipantPasswordHash, password) != PasswordVerificationResult.Failed;

        if (!passwordIsValid)
        {
            throw new UnauthorizedAccessException("Неверное имя или пароль.");
        }

        return new AuthenticatedParticipant(participant, participant.ParticipantToken);
    }

    public async Task<AuthenticatedOwner> AuthenticateOwnerAsync(string shortCode, string password, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));

        if (string.IsNullOrWhiteSpace(password) || meeting.OwnerPasswordHash is null ||
            MeetingPasswordHasher.VerifyHashedPassword(meeting, meeting.OwnerPasswordHash, password) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedAccessException("Неверный пароль организатора.");
        }

        return new AuthenticatedOwner(meeting, meeting.OwnerToken);
    }

    public async Task SaveAvailabilityAsync(string shortCode, string participantToken, IReadOnlyCollection<AvailabilitySelection> selections, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken);

        var meeting = await dbContext.Meetings
            .Include(x => x.GridCells)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));

        ValidateVotingIsOpen(meeting);

        var participant = await dbContext.Participants
            .Include(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.MeetingId == meeting.Id && x.ParticipantToken == participantToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Участник не найден.");

        if (participant.HasNoSuitableTime)
            throw new InvalidOperationException("Участник отметил, что ему не подходит ни одно время.");

        var targetIds = meeting.GridCells
            .Select(x => x.Id)
            .ToHashSet();

        if (selections.Any(x => !targetIds.Contains(x.TargetId)) ||
            selections.GroupBy(x => x.TargetId).Any(x => x.Count() > 1) ||
            selections.Any(x => !Enum.IsDefined(x.Status)))
        {
            throw new ArgumentException("Некорректные отметки доступности.", nameof(selections));
        }

        var old = participant.Availabilities;

        dbContext.Availabilities.RemoveRange(old);

        dbContext.Availabilities.AddRange(selections
            .Select(x => new Availability
            {
                Id = Guid.NewGuid(),
                ParticipantId = participant.Id,
                GridCellId = x.TargetId,
                Status = x.Status,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            })
        );

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

    private async Task SetRetentionStatusAsync(string shortCode, string ownerToken, MeetingStatus status, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken);

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
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken);

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
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken);

        var meeting = await dbContext.Meetings
            .Include(x => x.GridCells)
            .ThenInclude(x => x.Availabilities)
            .ThenInclude(x => x.Participant)
            .Include(x => x.Participants)
            .ThenInclude(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");

        var voted = meeting.Participants
            .Where(x => x.HasNoSuitableTime || x.Availabilities.Count != 0)
            .ToArray();

        return meeting.GridCells
            .OrderBy(x => x.StartTime)
            .Select(cell =>
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
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Cell.StartTime)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<IntervalAnalysis>> AnalyzeIntervalsAsync(
        string shortCode,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await dbContext.Meetings
            .Include(x => x.GridCells)
            .ThenInclude(x => x.Availabilities)
            .Include(x => x.Participants)
            .ThenInclude(x => x.Availabilities)
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode, cancellationToken)
            ?? throw new ArgumentException("Встреча не найдена.", nameof(shortCode));

        var voted = meeting.Participants
            .Where(x => x.HasNoSuitableTime || x.Availabilities.Count > 0)
            .ToArray();
        if (voted.Length == 0)
            return [];

        var cells = meeting.GridCells.OrderBy(x => x.StartTime).ToArray();
        var commonIntervals = new List<(GridCell Start, GridCell End)>();
        GridCell? commonStart = null;
        GridCell? commonEnd = null;

        foreach (var cell in cells)
        {
            var isCommonlyAvailable = voted.All(participant =>
                participant.Availabilities.Any(x =>
                    x.GridCellId == cell.Id && x.Status == AvailabilityStatus.Available));

            if (isCommonlyAvailable)
            {
                commonStart ??= cell;
                commonEnd = cell;
            }
            else if (commonStart is not null)
            {
                commonIntervals.Add((commonStart, commonEnd!));
                commonStart = null;
                commonEnd = null;
            }
        }

        if (commonStart is not null)
            commonIntervals.Add((commonStart, commonEnd!));

        var alternativeIntervals = BuildAlternativeIntervals(cells, voted);

        var commonResults = commonIntervals
            .Select(interval => BuildIntervalAnalysis(interval.Start, interval.End, cells, voted, true));
        var alternativeResults = alternativeIntervals
            .Select(interval => BuildIntervalAnalysis(interval.Start, interval.End, cells, voted))
            .Where(x => !commonIntervals.Any(common => common.Start.StartTime == x.StartTime && common.End.EndTime == x.EndTime));

        var result = commonResults
            .Concat(alternativeResults)
            .Where(x => x.AvailableCount > 0 || x.IfNeededCount > 0)
            .Select(x => new { Analysis = x, Group = GetIntervalGroup(x) })
            .OrderByDescending(x => x.Analysis.Duration)
            .ThenBy(x => x.Analysis.IsCommonAvailability ? 0 : 1)
            .ThenBy(x => x.Group)
            .ThenBy(x => x.Group == 1 ? 0 : x.Analysis.IfNeededCount)
            .ThenBy(x => x.Group is 3 or 4 ? x.Analysis.CannotCount : 0)
            .ThenBy(x => x.Analysis.StartTime)
            .Select(x => x.Analysis)
            .ToArray();

        return result;
    }

    private static IReadOnlyCollection<(GridCell Start, GridCell End)> BuildAlternativeIntervals(
        IReadOnlyCollection<GridCell> cells,
        IReadOnlyCollection<Participant> voted)
    {
        var intervals = new List<(GridCell Start, GridCell End)>();
        GridCell? start = null;
        GridCell? end = null;
        string? activeParticipants = null;

        void CompleteInterval()
        {
            if (start is not null)
                intervals.Add((start, end!));

            start = null;
            end = null;
            activeParticipants = null;
        }

        foreach (var cell in cells.OrderBy(x => x.StartTime))
        {
            var currentActiveParticipants = string.Join(",", voted.Select(participant =>
                participant.Availabilities.Any(x =>
                    x.GridCellId == cell.Id &&
                    x.Status is AvailabilityStatus.Available or AvailabilityStatus.IfNeeded)
                    ? "1"
                    : "0"));

            if (currentActiveParticipants.All(value => value == '0'))
            {
                CompleteInterval();
                continue;
            }

            if (start is not null &&
                activeParticipants == currentActiveParticipants &&
                end!.EndTime == cell.StartTime)
            {
                end = cell;
                continue;
            }

            CompleteInterval();
            start = cell;
            end = cell;
            activeParticipants = currentActiveParticipants;
        }

        CompleteInterval();
        return intervals;
    }

    private static IntervalAnalysis BuildIntervalAnalysis(
        GridCell start,
        GridCell end,
        IReadOnlyCollection<GridCell> allCells,
        IReadOnlyCollection<Participant> voted,
        bool isCommonAvailability = false)
    {
        var cells = allCells.Where(x => x.StartTime >= start.StartTime && x.EndTime <= end.EndTime).ToArray();
        var available = new List<string>();
        var ifNeeded = new List<string>();
        var cannot = new List<string>();
        var breakdown = new List<ParticipantBreakdown>();

        foreach (var participant in voted)
        {
            var segments = new List<(string Status, DateTimeOffset From, DateTimeOffset To)>();
            foreach (var cell in cells)
            {
                var answer = participant.Availabilities.FirstOrDefault(x => x.GridCellId == cell.Id);
                var status = answer?.Status switch
                {
                    AvailabilityStatus.Available => "Available",
                    AvailabilityStatus.IfNeeded => "IfNeeded",
                    _ => "Cannot"
                };

                if (segments.Count > 0 && segments[^1].Status == status && segments[^1].To == cell.StartTime)
                {
                    var previous = segments[^1];
                    segments[^1] = (previous.Status, previous.From, cell.EndTime);
                }
                else
                {
                    segments.Add((status, cell.StartTime, cell.EndTime));
                }
            }

            var worstStatus = segments.Any(x => x.Status == "Cannot")
                ? "Cannot"
                : segments.Any(x => x.Status == "IfNeeded") ? "IfNeeded" : "Available";
            if (worstStatus == "Available") available.Add(participant.DisplayName);
            else if (worstStatus == "IfNeeded") ifNeeded.Add(participant.DisplayName);
            else cannot.Add(participant.DisplayName);

            breakdown.AddRange(segments.Select(x => new ParticipantBreakdown(participant.DisplayName, x.Status, x.From, x.To)));
        }

        return new IntervalAnalysis(
            start.StartTime,
            end.EndTime,
            end.EndTime - start.StartTime,
            available,
            ifNeeded,
            cannot,
            available.Count,
            ifNeeded.Count,
            cannot.Count,
            breakdown,
            isCommonAvailability);
    }

    private static int GetIntervalGroup(IntervalAnalysis analysis) =>
        analysis.CannotCount == 0
            ? (analysis.IfNeededCount == 0 ? 1 : 2)
            : (analysis.AvailableCount > 0 ? 3 : 4);

    public async Task CloseAsync(string shortCode, string ownerToken, DateTimeOffset? selectedStartTime, DateTimeOffset? selectedEndTime, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken);

        var meeting = await dbContext.Meetings
            .SingleOrDefaultAsync(x => x.ShortCode == shortCode && x.OwnerToken == ownerToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Доступ к встрече запрещён.");

        if (meeting.Status != MeetingStatus.Active)
            throw new InvalidOperationException("Встреча уже закрыта.");

        if ((selectedStartTime is null) != (selectedEndTime is null) ||
            (selectedStartTime is not null && selectedEndTime is not null && selectedStartTime.Value >= selectedEndTime.Value))
        {
            throw new ArgumentException("Итоговое окно указано некорректно.");
        }

        meeting.SelectedStartTime = selectedStartTime?.ToUniversalTime(); 
        meeting.SelectedEndTime = selectedEndTime?.ToUniversalTime(); 
        meeting.Status = MeetingStatus.Closed; 
        meeting.ClosedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(CreateMeetingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 100)
            throw new ArgumentException("Название встречи обязательно и не должно превышать 100 символов.", nameof(request));

        if (request.Description?.Length > 1000) 
            throw new ArgumentException("Описание не должно превышать 1000 символов.", nameof(request));

        ValidateOptionalPassword(request.OrganizerPassword);

        if (string.IsNullOrWhiteSpace(request.OrganizerName) || request.OrganizerName.Trim().Length > 50) 
            throw new ArgumentException("Укажите имя организатора длиной до 50 символов.", nameof(request));

        ValidateTimeZone(request.TimeZone);

        if (request.GridStepMinutes is not (15 or 30 or 60))
            throw new ArgumentException("Шаг сетки должен быть 15, 30 или 60 минут.", nameof(request));

        if (request.GridEndDate < request.GridStartDate)
            throw new ArgumentException("Диапазон дат сетки указан некорректно.", nameof(request));

        if (request.GridStartDate < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("Дата начала не может быть в прошлом.", nameof(request));

        var dayCount = request.GridEndDate.DayNumber - request.GridStartDate.DayNumber + 1;

        var maximumCellCount = dayCount * (24 * 60 / request.GridStepMinutes);

        if (maximumCellCount > 1500) 
            throw new ArgumentException("Сетка не должна содержать более 1500 ячеек.", nameof(request));
    }

    private static void ValidateOptionalPassword(string? password)
    {
        if (!string.IsNullOrEmpty(password) && password.Length < 4)
            throw new ArgumentException("Пароль должен содержать минимум 4 символа.", nameof(password));
    }

    private static void ValidateTimeZone(string timeZone) 
    {
        if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
            throw new ArgumentException("Укажите корректный часовой пояс.", nameof(timeZone)); 
    }

    private static void ValidateVotingIsOpen(Meeting meeting) 
    {
        if (meeting.Status != MeetingStatus.Active) 
            throw new InvalidOperationException("Голосование закрыто.");
    }

    private static async Task<string> CreateUniqueShortCodeAsync(TimeMeetDbContext dbContext, CancellationToken ct) 
    {
        for (var i = 0; i < 10; i++) 
        {
            var code = CreateBase62Token(8);
            if (!await dbContext.Meetings.AnyAsync(x => x.ShortCode == code, ct))
                return code;
        }

        throw new InvalidOperationException("Не удалось сгенерировать код встречи.");
    }

    private static string CreateToken(int byteCount) 
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string CreateBase62Token(int length)
    {
        Span<byte> bytes = stackalloc byte[length]; 
        RandomNumberGenerator.Fill(bytes); 
        return string.Create(
            length, 
            bytes.ToArray(), 
            static (result, source) => 
            {
                for (var i = 0; i < result.Length; i++)
                    result[i] = Base62[source[i] % Base62.Length];
            }); 
    }
}
