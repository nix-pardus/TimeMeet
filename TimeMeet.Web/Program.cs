using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using TimeMeet.Application.Meetings;
using TimeMeet.Infrastructure.Data;
using TimeMeet.Infrastructure.Meetings;
using TimeMeet.Infrastructure.Export;
using TimeMeet.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IMeetingService, MeetingService>();
builder.Services.AddScoped<ICalendarExporter, CalendarExporter>();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Host=localhost;Port=5432;Database=timemeetapp;Username=postgres;Password=postgress";
builder.Services.AddDbContext<TimeMeetDbContext>(options => options.UseNpgsql(connectionString));

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

app.MapGet("/manage-access/{shortCode}", (HttpContext httpContext, string shortCode, string token) =>
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.BadRequest("Токен владельца не указан.");
    }

    httpContext.Response.Cookies.Append($"timemeet-owner-{shortCode}", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = !app.Environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(90),
        IsEssential = true
    });

app.MapGet("/export/{shortCode}/{format}", async (HttpContext httpContext, string shortCode, string format, IMeetingService meetingService, ICalendarExporter calendarExporter) =>
{
    var token = httpContext.Request.Cookies[$"timemeet-owner-{shortCode}"];
    var meeting = string.IsNullOrWhiteSpace(token) ? null : await meetingService.GetForOwnerAsync(shortCode, token);
    if (meeting?.Status != TimeMeet.Domain.Enums.MeetingStatus.Closed || meeting.SelectedTimeSlotId is null) return Results.NotFound();
    var slot = meeting.TimeSlots.SingleOrDefault(x => x.Id == meeting.SelectedTimeSlotId);
    if (slot is null) return Results.NotFound();
    return format.ToLowerInvariant() switch
    {
        "ics" => Results.File(await calendarExporter.ExportAsync(meeting, slot), "text/calendar", $"{shortCode}.ics"),
        "json" => Results.Json(new { meeting.Title, meeting.Description, meeting.OrganizerName, slot.StartTime, slot.EndTime }),
        "csv" => Results.File(System.Text.Encoding.UTF8.GetBytes($"Title,Organizer,StartUtc,EndUtc\r\n\"{meeting.Title.Replace("\"", "\"\"")}\",\"{meeting.OrganizerName.Replace("\"", "\"\"")}\",{slot.StartTime.UtcDateTime:O},{slot.EndTime.UtcDateTime:O}\r\n"), "text/csv", $"{shortCode}.csv"),
        _ => Results.BadRequest("Поддерживаются форматы ics, csv и json.")
    };
});

    return Results.Redirect($"/manage/{Uri.EscapeDataString(shortCode)}");
});

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
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
