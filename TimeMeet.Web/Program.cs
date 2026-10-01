using System.Security.Cryptography;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using TimeMeet.Application.Meetings;
using TimeMeet.Domain.Enums;
using TimeMeet.Infrastructure.Data;
using TimeMeet.Infrastructure.Export;
using TimeMeet.Infrastructure.Meetings;
using TimeMeet.Infrastructure.Retention;
using TimeMeet.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IMeetingService, MeetingService>();
builder.Services.AddScoped<ICalendarExporter, CalendarExporter>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=timemeetapp;Username=postgres;Password=postgress";
builder.Services.AddDbContext<TimeMeetDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<MeetingRetentionJob>();
builder.Services.AddHangfire(configuration => configuration
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/manage"))
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        var key = context.Request.Query["key"].ToString();
        var shortCode = GetManageShortCode(context.Request.Path);
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(shortCode))
        {
            var db = context.RequestServices.GetRequiredService<TimeMeetDbContext>();
            var meeting = await db.Meetings.IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.ShortCode == shortCode, context.RequestAborted);

            if (meeting is not null && meeting.Status != MeetingStatus.Deleted && SecureEquals(key, meeting.OwnerToken))
            {
                context.Response.Cookies.Append($"timemeet-owner-{shortCode}", meeting.OwnerToken, CreateOwnerCookieOptions(app.Environment.IsDevelopment()));
                context.Response.Redirect($"/manage/{Uri.EscapeDataString(shortCode)}");
                return;
            }
        }
    }

    await next(context);
});

app.UseAntiforgery();

var recurringJobManager = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobManager.AddOrUpdate<MeetingRetentionJob>(
    "meeting-retention",
    job => job.ExecuteAsync(CancellationToken.None),
    Cron.Daily(3));

app.MapGet("/export/{shortCode}/{format}", async (
    string shortCode,
    string format,
    IMeetingService meetingService,
    ICalendarExporter calendarExporter,
    CancellationToken cancellationToken) =>
{
    var access = await meetingService.GetForParticipantAsync(shortCode, null, cancellationToken);
    var meeting = access?.Meeting;
    if (meeting?.Status != MeetingStatus.Closed || meeting.SelectedStartTime is null || meeting.SelectedEndTime is null)
        return Results.NotFound();

    return format.ToLowerInvariant() switch
    {
        "ics" => Results.File(await calendarExporter.ExportAsync(meeting, cancellationToken), "text/calendar", $"{shortCode}.ics"),
        "json" => Results.Json(new
        {
            meeting.Title,
            meeting.Description,
            meeting.OrganizerName,
            StartTime = meeting.SelectedStartTime,
            EndTime = meeting.SelectedEndTime
        }),
        "csv" => Results.File(
            System.Text.Encoding.UTF8.GetBytes(
                $"Title,Organizer,StartUtc,EndUtc\r\n\"{Csv(meeting.Title)}\",\"{Csv(meeting.OrganizerName)}\",{meeting.SelectedStartTime.Value.UtcDateTime:O},{meeting.SelectedEndTime.Value.UtcDateTime:O}\r\n"),
            "text/csv",
            $"{shortCode}.csv"),
        _ => Results.BadRequest("Поддерживаются форматы ics, csv и json.")
    };
});

app.MapGet("/participant-access/{shortCode}", async (
    HttpContext httpContext,
    string shortCode,
    string token,
    IMeetingService meetingService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(token))
        return Results.BadRequest("Токен участника не указан.");

    var access = await meetingService.GetForParticipantAsync(shortCode, token, cancellationToken);
    if (access?.Participant is null)
        return Results.BadRequest("Ссылка участника недействительна.");

    httpContext.Response.Cookies.Append(
        $"timemeet-participant-{shortCode}",
        token,
        CreateParticipantCookieOptions(app.Environment.IsDevelopment()));

    return Results.Redirect($"/m/{Uri.EscapeDataString(shortCode)}");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string? GetManageShortCode(PathString path)
{
    var value = path.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    return value is { Length: 2 } && value[0].Equals("manage", StringComparison.OrdinalIgnoreCase)
        ? value[1]
        : null;
}

static bool SecureEquals(string left, string right)
{
    var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
    var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static CookieOptions CreateOwnerCookieOptions(bool isDevelopment) => new()
{
    HttpOnly = true,
    Secure = !isDevelopment,
    SameSite = SameSiteMode.Lax,
    MaxAge = TimeSpan.FromDays(90),
    IsEssential = true
};

static CookieOptions CreateParticipantCookieOptions(bool isDevelopment) => new()
{
    HttpOnly = true,
    Secure = !isDevelopment,
    SameSite = SameSiteMode.Lax,
    MaxAge = TimeSpan.FromDays(90),
    IsEssential = true
};

static string Csv(string value) => value.Replace("\"", "\"\"");
