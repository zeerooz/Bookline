using Microsoft.EntityFrameworkCore;

namespace Bookline.Api.Data;

public static class DatabaseStartup
{
    public static async Task MigrateAsync(WebApplication app)
    {
        // Migrating on startup suits a single-instance deployment. With several
        // replicas this belongs in a release step instead, or two instances
        // race each other applying the same migration.
        if (!app.Configuration.GetValue("Database:MigrateOnStartup", true))
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Database migration failed on startup.");
            throw;
        }
    }
}
