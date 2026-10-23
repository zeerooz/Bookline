using Bookline.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bookline.Tests.Integration;

/// <summary>
/// Boots the real pipeline — real Identity, real authorization policies, real
/// tenant-resolution middleware — against a throwaway SQLite database. Only
/// the database provider is swapped, so the tenant isolation these tests check
/// is the same code path production uses.
///
/// One caveat worth stating: the Postgres exclusion constraint that makes
/// concurrent double-booking impossible does not exist on SQLite. The
/// application-level check is covered here; the constraint itself is exercised
/// against real Postgres by the end-to-end step in CI.
/// </summary>
public class BooklineFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-chars-long");
        builder.UseSetting("Jwt:Issuer", "bookline-tests");
        builder.UseSetting("Jwt:Audience", "bookline-tests");

        // The Npgsql migrations cannot run on SQLite; the schema is created
        // from the model instead, in InitializeAsync below.
        builder.UseSetting("Database:MigrateOnStartup", "false");

        // TestServer requests have no remote IP, so every test would otherwise
        // share a single rate-limit partition and trip over each other.
        builder.UseSetting("RateLimits:AuthPerMinute", "10000");
        builder.UseSetting("RateLimits:ApiPerMinute", "10000");
        builder.UseSetting("RateLimits:PublicPerMinute", "10000");

        builder.ConfigureServices(services =>
        {
            // AddDbContext registers more than DbContextOptions<T>: EF 10 also
            // adds an IDbContextOptionsConfiguration<T> that re-applies
            // UseNpgsql. Leaving it behind registers two providers at once,
            // which EF refuses to resolve.
            var doomed = services
                .Where(d =>
                    d.ServiceType == typeof(AppDbContext) ||
                    (d.ServiceType.FullName?.Contains("DbContextOptions", StringComparison.Ordinal) ?? false))
                .ToList();

            foreach (var descriptor in doomed)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        // An in-memory SQLite database lives exactly as long as its connection.
        await _connection.OpenAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await base.DisposeAsync();
    }
}
