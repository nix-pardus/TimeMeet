using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using TimeMeet.Application.Meetings;
using TimeMeet.Infrastructure.Data;
using TimeMeet.Infrastructure.Meetings;
using TimeMeet.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IMeetingService, MeetingService>();
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

    return Results.Redirect($"/manage/{Uri.EscapeDataString(shortCode)}");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
