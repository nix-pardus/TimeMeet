using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Enums;
using TimeMeet.Infrastructure.Data;
using TimeMeet.Infrastructure.Meetings;

namespace TimeMeet.IntegrationTests;

public sealed class MeetingServicePostgreSqlTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder()
        .WithDatabase("timemeet_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private IDbContextFactory<TimeMeetDbContext> dbContextFactory = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<TimeMeetDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options;

        dbContextFactory = new TestDbContextFactory(options);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task CreateMeeting_CreatesFullGridAndOrganizerParticipant()
    {
        var service = new MeetingService(dbContextFactory);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

        var result = await service.CreateAsync(new CreateMeetingRequest
        {
            Title = "Интеграционная встреча",
            OrganizerName = "Организатор",
            TimeZone = "Europe/Moscow",
            GridStartDate = tomorrow,
            GridEndDate = tomorrow,
            GridStepMinutes = 60
        });

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var meeting = await dbContext.Meetings
            .Include(x => x.GridCells)
            .Include(x => x.Participants)
            .SingleAsync(x => x.Id == result.Meeting.Id);

        Assert.Equal(24, meeting.GridCells.Count);
        Assert.Contains(meeting.Participants, x =>
            x.IsOrganizer && x.DisplayName == "Организатор" &&
            x.ParticipantToken == result.OrganizerParticipantToken);
        Assert.Equal(result.OwnerToken, meeting.OwnerToken);
    }

    [Fact]
    public async Task CreateMeeting_GeneratesExpectedCellsOnDstDays()
    {
        var springDstDay = new DateOnly(2027, 3, 28);
        var fallDstDay = new DateOnly(2027, 10, 31);
        var service = new MeetingService(dbContextFactory);

        var spring = await service.CreateAsync(CreateRequest(springDstDay, springDstDay, "Europe/Berlin", 15));
        var fall = await service.CreateAsync(CreateRequest(fallDstDay, fallDstDay, "Europe/Berlin", 15));

        Assert.Equal(92, spring.Meeting.GridCells.Count);
        Assert.Equal(100, fall.Meeting.GridCells.Count);
    }

    [Fact]
    public async Task CreateMeeting_RejectsGridLargerThan1500Cells()
    {
        var service = new MeetingService(dbContextFactory);
        var start = new DateOnly(2027, 1, 1);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(CreateRequest(start, start.AddDays(15), "Europe/Moscow", 15)));

        Assert.Contains("1500", exception.Message);
    }

    [Fact]
    public async Task Voting_AnalyzesAvailability_NoSuitableTime_AndClosesMeeting()
    {
        var service = new MeetingService(dbContextFactory);
        var date = new DateOnly(2027, 5, 1);
        var created = await service.CreateAsync(CreateRequest(date, date, "Europe/Moscow", 60));
        var participant = await service.CreateParticipantAsync(created.Meeting.ShortCode, "Участник", "Europe/Moscow");
        var cannotAttend = await service.CreateParticipantAsync(created.Meeting.ShortCode, "Не может", "Europe/Moscow");
        var cell = created.Meeting.GridCells.OrderBy(x => x.StartTime).First();

        await service.SaveAvailabilityAsync(
            created.Meeting.ShortCode,
            created.OrganizerParticipantToken,
            [new AvailabilitySelection(cell.Id, AvailabilityStatus.Available)]);
        await service.SaveAvailabilityAsync(
            created.Meeting.ShortCode,
            participant.ParticipantToken,
            [new AvailabilitySelection(cell.Id, AvailabilityStatus.IfNeeded)]);
        await service.SetNoSuitableTimeAsync(created.Meeting.ShortCode, cannotAttend.ParticipantToken, true);

        var analysis = await service.AnalyzeAvailabilityAsync(created.Meeting.ShortCode, created.OwnerToken);
        var analyzedCell = Assert.Single(analysis, x => x.Cell.Id == cell.Id);

        Assert.Equal(1, analyzedCell.AvailableCount);
        Assert.Equal(1, analyzedCell.IfNeededCount);
        Assert.Equal(1, analyzedCell.CannotCount);
        Assert.Contains("Организатор", analyzedCell.AvailableParticipants);
        Assert.Contains("Участник", analyzedCell.IfNeededParticipants);
        Assert.Contains("Не может", analyzedCell.CannotParticipants);

        await service.CloseAsync(
            created.Meeting.ShortCode,
            created.OwnerToken,
            cell.StartTime,
            cell.EndTime);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var closed = await dbContext.Meetings.SingleAsync(x => x.Id == created.Meeting.Id);
        Assert.Equal(MeetingStatus.Closed, closed.Status);
        Assert.Equal(cell.StartTime, closed.SelectedStartTime);
        Assert.Equal(cell.EndTime, closed.SelectedEndTime);
    }

    [Fact]
    public async Task Retention_ArchivesKeepForeverMeeting_OnGridEndDatePlus90LocalDays()
    {
        var gridEndDate = new DateOnly(2027, 1, 1);
        var created = await new MeetingService(dbContextFactory)
            .CreateAsync(CreateRequest(gridEndDate, gridEndDate, "Europe/Berlin", 60));
        var now = new DateTimeOffset(2027, 4, 1, 12, 0, 0, TimeSpan.Zero);

        await new MeetingRetentionJob(dbContextFactory, new FixedTimeProvider(now)).ExecuteAsync();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var meeting = await dbContext.Meetings
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == created.Meeting.Id);

        Assert.Equal(MeetingStatus.Archived, meeting.Status);
        Assert.Equal(now, meeting.ArchivedAt);
    }

    [Fact]
    public async Task Retention_MarksDeleteAfterLastDayMeetingDeleted_OnGridEndDatePlusTwoLocalDays()
    {
        var gridEndDate = new DateOnly(2027, 1, 1);
        var created = await new MeetingService(dbContextFactory)
            .CreateAsync(CreateRequest(gridEndDate, gridEndDate, "Europe/Berlin", 60, RetentionMode.DeleteAfterLastDay));
        var now = new DateTimeOffset(2027, 1, 3, 0, 30, 0, TimeSpan.Zero);

        await new MeetingRetentionJob(dbContextFactory, new FixedTimeProvider(now)).ExecuteAsync();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var meeting = await dbContext.Meetings
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == created.Meeting.Id);

        Assert.Equal(MeetingStatus.Deleted, meeting.Status);
        Assert.Equal(now, meeting.DeletedAt);
        Assert.Equal(MeetingDeletionReason.RetentionExpired, meeting.DeletionReason);
    }

    [Fact]
    public async Task ManualDelete_SetsManualDeletionReason()
    {
        var date = new DateOnly(2027, 5, 1);
        var created = await new MeetingService(dbContextFactory)
            .CreateAsync(CreateRequest(date, date, "Europe/Moscow", 60));

        await new MeetingService(dbContextFactory)
            .DeleteAsync(created.Meeting.ShortCode, created.OwnerToken);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var meeting = await dbContext.Meetings
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == created.Meeting.Id);

        Assert.Equal(MeetingStatus.Deleted, meeting.Status);
        Assert.Equal(MeetingDeletionReason.Manual, meeting.DeletionReason);
    }

    [Fact]
    public async Task Retention_PhysicallyDeletesDeletedMeetingAndRelatedRowsAfter30Days()
    {
        var gridEndDate = new DateOnly(2027, 1, 1);
        var created = await new MeetingService(dbContextFactory)
            .CreateAsync(CreateRequest(gridEndDate, gridEndDate, "Europe/Moscow", 60, RetentionMode.DeleteAfterLastDay));
        var now = new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero);

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
        {
            var meeting = await dbContext.Meetings
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == created.Meeting.Id);
            meeting.Status = MeetingStatus.Deleted;
            meeting.DeletedAt = now.AddDays(-31);
            await dbContext.SaveChangesAsync();
        }

        await new MeetingRetentionJob(dbContextFactory, new FixedTimeProvider(now)).ExecuteAsync();

        await using var verificationContext = await dbContextFactory.CreateDbContextAsync();
        Assert.False(await verificationContext.Meetings.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == created.Meeting.Id));
        Assert.False(await verificationContext.GridCells.AnyAsync(x => x.MeetingId == created.Meeting.Id));
        Assert.False(await verificationContext.Participants.AnyAsync(x => x.MeetingId == created.Meeting.Id));
    }

    private static CreateMeetingRequest CreateRequest(
        DateOnly start,
        DateOnly end,
        string timeZone,
        int step,
        RetentionMode retentionMode = RetentionMode.KeepForever) => new()
        {
            Title = "Тестовая встреча",
            OrganizerName = "Организатор",
            TimeZone = timeZone,
            GridStartDate = start,
            GridEndDate = end,
            GridStepMinutes = step,
            RetentionMode = retentionMode
        };

    private sealed class TestDbContextFactory(DbContextOptions<TimeMeetDbContext> options)
        : IDbContextFactory<TimeMeetDbContext>
    {
        public TimeMeetDbContext CreateDbContext() => new(options);

        public Task<TimeMeetDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TimeMeetDbContext>(new(options));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}