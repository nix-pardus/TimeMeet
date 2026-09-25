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

    public async Task<CreatedMeeting> CreateAsync(
        CreateMeetingRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

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
            ParticipationMode = request.ParticipationMode,
            AvailabilityMode = request.AvailabilityMode,
            Status = MeetingStatus.Active,
            Deadline = request.Deadline?.ToUniversalTime(),
            AllowAnonymous = request.AllowAnonymous,
            AllowRegistration = request.AllowRegistration,
            CreatedAt = now
        };

        foreach (var slot in request.Slots)
        {
            meeting.TimeSlots.Add(new TimeSlot
            {
                Id = Guid.NewGuid(),
                MeetingId = meeting.Id,
                StartTime = slot.StartTime.ToUniversalTime(),
                EndTime = slot.EndTime.ToUniversalTime()
            });
        }

        dbContext.Meetings.Add(meeting);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreatedMeeting(meeting, meeting.OwnerToken);
    }

    public Task<Meeting?> GetForOwnerAsync(
        string shortCode,
        string ownerToken,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Meetings
            .Include(x => x.TimeSlots)
            .Include(x => x.Participants)
            .SingleOrDefaultAsync(
                x => x.ShortCode == shortCode && x.OwnerToken == ownerToken,
                cancellationToken);
    }

    private async Task<string> CreateUniqueShortCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var shortCode = CreateBase62Token(8);
            if (!await dbContext.Meetings.AnyAsync(x => x.ShortCode == shortCode, cancellationToken))
            {
                return shortCode;
            }
        }

        throw new InvalidOperationException("Не удалось сгенерировать уникальный код встречи.");
    }

    private static void Validate(CreateMeetingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
        {
            throw new ArgumentException("Название встречи обязательно и не должно превышать 200 символов.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.OrganizerName) || request.OrganizerName.Trim().Length > 100)
        {
            throw new ArgumentException("Укажите имя организатора длиной до 100 символов.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.TimeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out _))
        {
            throw new ArgumentException("Укажите корректный часовой пояс.", nameof(request));
        }

        if (request.ParticipationMode != ParticipationMode.LinkOnly)
        {
            throw new ArgumentException("В MVP доступно только голосование по ссылке.", nameof(request));
        }

        if (request.Deadline is not null && request.Deadline <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Дедлайн должен быть в будущем.", nameof(request));
        }

        if (request.Slots.Count == 0)
        {
            throw new ArgumentException("Добавьте хотя бы один временной интервал.", nameof(request));
        }

        if (request.Slots.Any(x => x.EndTime <= x.StartTime || x.StartTime < DateTimeOffset.UtcNow.AddMinutes(-1)))
        {
            throw new ArgumentException("Время слотов указано некорректно.", nameof(request));
        }
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
        return string.Create(length, bytes.ToArray(), static (result, source) =>
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = Base62[source[i] % Base62.Length];
            }
        });
    }
}
