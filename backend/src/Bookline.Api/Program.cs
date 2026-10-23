using System.Text;
using System.Threading.RateLimiting;
using Bookline.Api.Data;
using Bookline.Api.Domain;
using Bookline.Api.Middleware;
using Bookline.Api.Security;
using Bookline.Api.Services;
using Bookline.Api.Services.Notifications;
using Bookline.Api.Services.Payments;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
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

var connectionString = DatabaseUrl.Resolve(builder.Configuration);

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

// Payments and email are chosen by configuration rather than by compilation,
// so the same build runs with a real gateway or with the local stand-ins.
var paymentOptions = builder.Configuration.GetSection(PaymentOptions.SectionName).Get<PaymentOptions>()
                     ?? new PaymentOptions();

if (paymentOptions.Provider.Equals("stripe", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(paymentOptions.StripeSecretKey))
    {
        throw new InvalidOperationException(
            "Payments:Provider is 'stripe' but Payments:StripeSecretKey is empty. "
            + "Set the key, or use the 'fake' provider for local development.");
    }

    builder.Services.AddScoped<IPaymentProvider, StripePaymentProvider>();
}
else
{
    builder.Services.AddScoped<IPaymentProvider, FakePaymentProvider>();
}

var notificationOptions = builder.Configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()
                          ?? new NotificationOptions();

if (notificationOptions.EmailProvider.Equals("smtp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
}
else
{
    builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();
}

builder.Services.AddHostedService<OutboxDispatcher>();

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

// Behind a reverse proxy (any managed platform, or nginx in docker compose)
// the client address on the socket is the proxy's, not the visitor's. Without
// this, every caller shares one rate-limit bucket and the audit log records
// the proxy for every action.
//
// KnownProxies and KnownNetworks are cleared because a managed platform does
// not publish a stable proxy address. That means trusting X-Forwarded-For, so
// it is only safe while the app is reachable solely through that proxy, which
// is exactly how it is deployed.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};

forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();

app.UseForwardedHeaders(forwardedHeaders);

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
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

    // Two different policies, because this process can serve two very
    // different things. The API returns JSON and needs nothing at all; the
    // single-page app needs its own scripts, styles and fonts. Sending the
    // API's policy to the browser for an HTML page would break the page.
    headers["Content-Security-Policy"] = context.Request.Path.StartsWithSegments("/api")
        ? "default-src 'none'; frame-ancestors 'none'; base-uri 'none'"
        : "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; "
          + "font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; "
          + "frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    await next();
});

// Present only in the single-image deployment, where the built frontend is
// copied into wwwroot. Under docker compose nginx serves it instead, and
// these simply find nothing to do.
app.UseDefaultFiles();
app.UseStaticFiles();

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

// Client-side routes such as /book/some-clinic are not files on disk, so any
// unmatched request hands back the app shell and lets the router decide.
if (Directory.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "assets")))
{
    // An unmatched path under /api must not fall through to the shell. A
    // caller that mistypes an endpoint deserves a 404, not a 200 and a page
    // of HTML their JSON parser will choke on. This is a real route, so it
    // outranks the fallback, which deliberately sits at the lowest priority.
    app.Map("/api/{**rest}", () => Results.NotFound());

    app.MapFallbackToFile("index.html");
}

await DatabaseStartup.MigrateAsync(app);

app.Run();

/// <summary>Exposed so the integration tests can boot the real pipeline.</summary>
public partial class Program;
