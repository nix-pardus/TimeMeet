using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Entities;

namespace TimeMeet.IntegrationTests;

public sealed class RateLimitingTests : IClassFixture<RateLimitingTests.TestFactory>
{
    private readonly HttpClient client;

    public RateLimitingTests(TestFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task OwnerLogin_AfterFiveFailedAttempts_Returns429()
    {
        var code = UniqueCode();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/manage/{code}/login", new { password = "invalid" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync(
            $"/api/manage/{code}/login", new { password = "invalid" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task OwnerLogin_DifferentShortCodes_HaveSeparateLimits()
    {
        var codeA = UniqueCode();
        var codeB = UniqueCode();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/manage/{codeA}/login", new { password = "invalid" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync(
            $"/api/manage/{codeA}/login", new { password = "invalid" });
        var separate = await client.PostAsJsonAsync(
            $"/api/manage/{codeB}/login", new { password = "invalid" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, separate.StatusCode);
    }

    [Fact]
    public async Task ParticipantRestore_AfterTenFailedAttempts_Returns429()
    {
        var code = UniqueCode();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/m/{code}/restore",
                new { displayName = "Participant", password = "invalid" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync(
            $"/api/m/{code}/restore",
            new { displayName = "Participant", password = "invalid" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static string UniqueCode() => Guid.NewGuid().ToString("N")[..8];

    public sealed class TestFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMeetingService>();
                services.AddScoped<IMeetingService, RejectingMeetingService>();
            });
        }
    }

    private sealed class RejectingMeetingService : IMeetingService
    {
        public Task<CreatedMeeting> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Meeting?> GetByShortCodeAsync(string shortCode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Meeting?> GetForOwnerAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ParticipantMeeting?> GetForParticipantAsync(string shortCode, string? participantToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CreatedParticipant> CreateParticipantAsync(string shortCode, string displayName, string timeZone, string? password = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthenticatedParticipant> AuthenticateParticipantAsync(string shortCode, string displayName, string password, CancellationToken cancellationToken = default) => throw new UnauthorizedAccessException();
        public Task<AuthenticatedOwner> AuthenticateOwnerAsync(string shortCode, string password, CancellationToken cancellationToken = default) => throw new UnauthorizedAccessException();
        public Task SaveAvailabilityAsync(string shortCode, string participantToken, IReadOnlyCollection<AvailabilitySelection> selections, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SetNoSuitableTimeAsync(string shortCode, string participantToken, bool value, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyCollection<IntervalAnalysis>> AnalyzeIntervalsAsync(string shortCode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyCollection<GridCellAnalysis>> AnalyzeAvailabilityAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task CloseAsync(string shortCode, string ownerToken, DateTimeOffset? selectedStartTime, DateTimeOffset? selectedEndTime, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ArchiveAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(string shortCode, string ownerToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}