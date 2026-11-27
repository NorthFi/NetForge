// root/selfhost/src/NetForge.Api/Program.cs
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using NetForge.Api;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["Database:ConnectionString"]
    ?? throw new InvalidOperationException("Database:ConnectionString is required");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "NetForge.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        // Server-side sessions expire absolutely; a stolen cookie cannot renew indefinitely.
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = StaffSecurity.ValidatePrincipal;
        options.LoginPath = "/login";
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddCookie(CustomerPortal.Scheme,CustomerPortal.ConfigureCookie);
builder.Services.AddAuthorization();
builder.Services.AddHostedService<BillingEnforcementWorker>();
builder.Services.AddHostedService<MonthlyBillingWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<MonitoringWorker>();
builder.Services.AddHostedService<AssuranceCpeProbeWorker>();
builder.Services.AddHostedService<SnmpTelemetryWorker>();
builder.Services.AddHostedService<TelemetryRetentionWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddConcurrencyLimiter("probe-ingest", limiter => { limiter.PermitLimit=32; limiter.QueueLimit=0; });
    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
var keyDir = builder.Configuration["NETFORGE_KEY_DIRECTORY"]
    ?? Path.Combine(AppContext.BaseDirectory, "keys");
Directory.CreateDirectory(keyDir);
builder.Services.AddDataProtection().SetApplicationName("NetForge.Selfhost")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDir));
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
await Store.Initialize(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Configuration);

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("NetForge.Api");
    if (error is PostgresException pg && pg.SqlState is "23503" or "23505" or "23514")
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "Related records or uniqueness constraints prevent this operation" });
        return;
    }
    logger.LogError(error, "API request failed");
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new { error = "Internal server error" });
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Cookies are strict, and every authenticated mutating request also needs an
// unpredictable token bound to the signed authentication ticket.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") &&
        !ctx.Request.Path.StartsWithSegments("/api/portal") &&
        !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) &&
        ctx.User.Identity?.IsAuthenticated == true)
    {
        var expected = ctx.User.FindFirst("csrf")?.Value ?? "";
        var supplied = ctx.Request.Headers["X-CSRF-Token"].ToString();
        if (expected.Length != 64 || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "CSRF token required" });
            return;
        }
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", runtime = "net11.0" }));

app.MapPost("/api/login", async (HttpContext ctx, NpgsqlDataSource pool, LoginInput input) =>
{
    if (string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrWhiteSpace(input.Password) ||
        input.Username.Length > 80 || input.Password.Length > 2048)
        return Results.BadRequest(new { error = "Invalid credentials" });
    var users = await Store.Rows(pool, "SELECT id,username,password_hash,role,enabled,session_generation FROM app_users WHERE lower(username)=lower(@u)",
        Store.P("u", input.Username.Trim()));
    var user = users.Count == 1 ? users[0] : null;
    if (user is null || user["enabled"] is not true ||
        !Passwords.Verify(input.Password, (string)user["password_hash"]!))
        return Results.Unauthorized();

    // A credential change racing login makes the conditional insert fail.
    var session = await StaffSecurity.OpenSession(pool,(Guid)user["id"]!,
        (Guid)user["session_generation"]!);
    if(session is null) return Results.Unauthorized();
    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user["id"]!.ToString()!),
        new(ClaimTypes.Name, (string)user["username"]!),
        new(ClaimTypes.Role, (string)user["role"]!),
        new("csrf", csrf),
        new(StaffSecurity.SessionClaim, session.Value.ToString())
    };
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    await Store.Audit(pool, (string)user["username"]!, "login", "staff", user["id"]!.ToString()!);
    return Results.Ok(new { username = user["username"], role = user["role"], csrf });
}).RequireRateLimiting("login");

var authorized = app.MapGroup("/api").RequireAuthorization();
authorized.MapGet("/status", (ClaimsPrincipal user) =>
    Results.Ok(new { authenticated = true, username = user.Identity?.Name,
        role = user.FindFirst(ClaimTypes.Role)?.Value, csrf = user.FindFirst("csrf")?.Value }));
authorized.MapPost("/logout", async (HttpContext ctx,NpgsqlDataSource db) =>
{
    await StaffSecurity.CloseSession(ctx,db);
    await ctx.SignOutAsync();
    return Results.Ok(new { ok = true });
});

Endpoints.Map(authorized);
StaffSecurity.Map(authorized);
Ipam.Map(authorized);
NetworkManagement.Map(authorized);
ServiceLifecycle.Map(authorized);
CustomerPortal.MapStaff(authorized);
FinancialAccounting.Map(authorized);
BillingScheduler.Map(authorized);
Notifications.Map(authorized);
NetworkMonitoring.Map(authorized);
SiteImpact.Map(authorized);
NocIncidents.Map(authorized);
ServiceAssurance.Map(authorized);
AssuranceAgreements.Map(authorized);
AssuranceCpeProbes.Map(authorized);
SubscriberProbes.MapStaff(authorized);
SubscriberProbes.MapIngest(app);
SnmpTelemetry.Map(authorized);
RfTelemetry.Map(authorized);
TelemetryRetention.Map(authorized);
UsageAccounting.MapStaff(authorized);
CustomerPortal.MapPortal(app);
app.MapFallbackToFile("index.html");
await app.RunAsync();
