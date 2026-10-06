using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using TimeMeet.Application.Meetings;
using TimeMeet.Infrastructure.Data;
using TimeMeet.Infrastructure.Meetings;
using TimeMeet.Infrastructure.Export;
using TimeMeet.Web;
using TimeMeet.Web.Components;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=timemeetapp;Username=postgres;Password=postgres";
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("public", limiterOptions =>
    {
        limiterOptions.PermitLimit = 30;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IMeetingService, MeetingService>();
builder.Services.AddScoped<ICalendarExporter, CalendarExporter>();
builder.Services.AddSingleton<IMeetingQrCodeGenerator, QrCodeGenerator>();
builder.Services.AddDbContextFactory<TimeMeetDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();
app.UseRateLimiter();

app.Use(async (httpContext, next) =>
{
    if (httpContext.Request.Path.StartsWithSegments("/manage"))
    {
        httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";

        var segments = httpContext.Request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments is { Length: 2 } && segments[0].Equals("manage", StringComparison.OrdinalIgnoreCase) &&
            httpContext.Request.Query.TryGetValue("key", out var key) &&
            !string.IsNullOrWhiteSpace(key))
        {
            var meetingService = httpContext.RequestServices.GetRequiredService<IMeetingService>();
            var shortCode = segments[1];

            if (await meetingService.GetForOwnerAsync(shortCode, key.ToString()) is not null)
            {
                httpContext.Response.Cookies.Append($"timemeet-owner-{shortCode}", key.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = !app.Environment.IsDevelopment(),
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromDays(90),
                    IsEssential = true
                });

                httpContext.Response.Redirect($"/manage/{Uri.EscapeDataString(shortCode)}");
                return;
            }
        }
    }

    await next();
});

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new LocalRequestsOnlyDashboardAuthorizationFilter()]
});

RecurringJob.AddOrUpdate<MeetingRetentionJob>(
    "meeting-retention",
    job => job.ExecuteAsync(CancellationToken.None),
    "0 3 * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

app.MapGet("/manage-access/{shortCode}", async (
    HttpContext httpContext,
    string shortCode,
    string token,
    string? participantToken,
    IMeetingService meetingService) =>
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.BadRequest("Токен владельца не указан.");
    }

    if (await meetingService.GetForOwnerAsync(shortCode, token) is null)
    {
        return Results.BadRequest("Ссылка управления недействительна.");
    }

    httpContext.Response.Cookies.Append($"timemeet-owner-{shortCode}", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = !app.Environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(90),
        IsEssential = true
    });

    if (!string.IsNullOrWhiteSpace(participantToken))
    {
        httpContext.Response.Cookies.Append($"timemeet-participant-{shortCode}", participantToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = !app.Environment.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(90),
            IsEssential = true
        });
    }

    return Results.Redirect(string.IsNullOrWhiteSpace(participantToken)
        ? $"/manage/{Uri.EscapeDataString(shortCode)}"
        : $"/m/{Uri.EscapeDataString(shortCode)}");
}).RequireRateLimiting("public");

app.MapGet("/export/{shortCode}/{format}", async (HttpContext httpContext, string shortCode, string format, IMeetingService meetingService, ICalendarExporter calendarExporter) =>
{
    var token = httpContext.Request.Cookies[$"timemeet-owner-{shortCode}"];
    var meeting = string.IsNullOrWhiteSpace(token) ? null : await meetingService.GetForOwnerAsync(shortCode, token);
    if (meeting?.Status != TimeMeet.Domain.Enums.MeetingStatus.Closed || meeting.SelectedStartTime is null || meeting.SelectedEndTime is null) return Results.NotFound();
    return format.ToLowerInvariant() switch
    {
        "ics" => Results.File(await calendarExporter.ExportAsync(meeting), "text/calendar", $"{shortCode}.ics"),
        "json" => Results.Json(new { meeting.Title, meeting.Description, meeting.OrganizerName, StartTime = meeting.SelectedStartTime, EndTime = meeting.SelectedEndTime }),
        "csv" => Results.File(System.Text.Encoding.UTF8.GetBytes($"Title,Organizer,StartUtc,EndUtc\r\n\"{meeting.Title.Replace("\"", "\"\"")}\",\"{meeting.OrganizerName.Replace("\"", "\"\"")}\",{meeting.SelectedStartTime.Value.UtcDateTime:O},{meeting.SelectedEndTime.Value.UtcDateTime:O}\r\n"), "text/csv", $"{shortCode}.csv"),
        _ => Results.BadRequest("Поддерживаются форматы ics, csv и json.")
    };
}).RequireRateLimiting("public");

app.MapGet("/participant-access/{shortCode}", async (
    HttpContext httpContext,
    string shortCode,
    string token,
    IMeetingService meetingService) =>
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.BadRequest("Токен участника не указан.");
    }

    var access = await meetingService.GetForParticipantAsync(shortCode, token);
    if (access?.Participant is null)
    {
        return Results.BadRequest("Ссылка участника недействительна.");
    }

    httpContext.Response.Cookies.Append($"timemeet-participant-{shortCode}", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = !app.Environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(90),
        IsEssential = true
    });

    return Results.Redirect($"/m/{Uri.EscapeDataString(shortCode)}");
}).RequireRateLimiting("public");

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
