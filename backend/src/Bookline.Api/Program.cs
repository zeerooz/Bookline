using System.Text;
using System.Threading.RateLimiting;
using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Middleware;
using Bookline.Api.Security;
using Bookline.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    // Fails at startup rather than at the first login: a missing signing key is
    // a deployment mistake that must never degrade into a weak token.
    .ValidateOnStart();

builder.Services.AddOptions<PaymentOptions>().Bind(builder.Configuration.GetSection(PaymentOptions.SectionName));
builder.Services.AddOptions<NotificationOptions>().Bind(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.AddOptions<CorsOptions>().Bind(builder.Configuration.GetSection(CorsOptions.SectionName));

var corsOrigins = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>()?.AllowedOrigins ?? [];

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? "Host=localhost;Port=5432;Database=bookline;Username=bookline;Password=bookline";

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// ---------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------

builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;

        // Length does more for password strength than character classes, which
        // mostly teach people to end every password with "1!".
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireDigit = false;

        options.Lockout.MaxFailedAccessAttempts = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<INotificationQueue, NotificationQueue>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<IAuthorizationHandler, TenantRoleHandler>();

// ---------------------------------------------------------------------------
// Authentication and authorization
// ---------------------------------------------------------------------------

var signingKey = builder.Configuration[$"{JwtOptions.SectionName}:SigningKey"] ?? string.Empty;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration[$"{JwtOptions.SectionName}:Issuer"],
            ValidAudience = builder.Configuration[$"{JwtOptions.SectionName}:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                string.IsNullOrEmpty(signingKey) ? new string('0', 64) : signingKey)),
            // The default five-minute grace period quietly extends every token.
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.TenantMember, p => p.RequireAuthenticatedUser()
        .AddRequirements(new TenantRoleRequirement(TenantRole.Customer)))
    .AddPolicy(Policies.TenantStaff, p => p.RequireAuthenticatedUser()
        .AddRequirements(new TenantRoleRequirement(TenantRole.Staff)))
    .AddPolicy(Policies.TenantOwner, p => p.RequireAuthenticatedUser()
        .AddRequirements(new TenantRoleRequirement(TenantRole.Owner)));

// ---------------------------------------------------------------------------
// Rate limiting
// ---------------------------------------------------------------------------

var rateLimits = builder.Configuration.GetSection("RateLimits");

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    AddFixedWindow(options, RateLimitPolicies.Authentication, rateLimits.GetValue("AuthPerMinute", 10));
    AddFixedWindow(options, RateLimitPolicies.Api, rateLimits.GetValue("ApiPerMinute", 300));
    AddFixedWindow(options, RateLimitPolicies.PublicBooking, rateLimits.GetValue("PublicPerMinute", 60));

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "Slow down and try again in a minute.",
        }, ct);
    };

    static void AddFixedWindow(RateLimiterOptions options, string name, int permitLimit) =>
        options.AddPolicy(name, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                              ?? context.Connection.RemoteIpAddress?.ToString()
                              ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
});

// ---------------------------------------------------------------------------
// HTTP
// ---------------------------------------------------------------------------

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (corsOrigins.Length > 0)
    {
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
    }
}));

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    app.Logger.LogError(feature?.Error, "Unhandled exception on {Path}", context.Request.Path);

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";

    // Never echo exception text: stack traces leak file paths, library
    // versions and sometimes connection strings.
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = StatusCodes.Status500InternalServerError,
        Title = "Unexpected error",
        Detail = "The request could not be completed.",
    });
}));

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("Bookline API"));
}
else
{
    app.UseHsts();
}

// Explicit UseRouting so tenant resolution can read {tenantSlug} from the
// matched route, before authorization policies need to know the tenant.
app.UseRouting();

app.UseCors();
app.UseRateLimiter();

app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

await DatabaseStartup.MigrateAsync(app);

app.Run();

/// <summary>Exposed so the integration tests can boot the real pipeline.</summary>
public partial class Program;
