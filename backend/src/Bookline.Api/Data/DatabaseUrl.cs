namespace Bookline.Api.Data;

public static class DatabaseUrl
{
    /// <summary>
    /// Resolves the connection string, preferring an explicit configuration
    /// entry and falling back to a DATABASE_URL environment variable.
    ///
    /// Most hosting platforms hand out a URL of the form
    /// postgres://user:password@host:port/database. Npgsql does not accept
    /// that shape, so it is translated here rather than asking whoever deploys
    /// this to hand-convert a value the platform generated for them.
    /// </summary>
    public static string Resolve(IConfiguration configuration)
    {
        var explicitValue = configuration.GetConnectionString("Postgres");

        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            return explicitValue;
        }

        var url = configuration["DATABASE_URL"];

        if (!string.IsNullOrWhiteSpace(url))
        {
            return FromUrl(url);
        }

        return "Host=localhost;Port=5432;Database=bookline;Username=bookline;Password=bookline";
    }

    internal static string FromUrl(string url)
    {
        var parsed = new Uri(url);
        var credentials = parsed.UserInfo.Split(':', 2);

        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = parsed.Host,
            Port = parsed.IsDefaultPort ? 5432 : parsed.Port,
            Database = parsed.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : string.Empty,
        };

        // SSL has to adapt to where the database actually is. Managed Postgres
        // insists on it; a container on a private compose network offers none
        // and refuses the handshake outright. Prefer negotiates TLS when the
        // server supports it, which covers both without a separate build.
        //
        // A host that enforces SSL still gets SSL, because it is the server
        // that refuses the plaintext connection. An explicit sslmode in the
        // URL always wins, so a deployment can demand Require.
        var query = System.Web.HttpUtility.ParseQueryString(parsed.Query);
        var requestedMode = query["sslmode"];

        builder.SslMode = requestedMode switch
        {
            "disable" => Npgsql.SslMode.Disable,
            "allow" => Npgsql.SslMode.Allow,
            "require" => Npgsql.SslMode.Require,
            "verify-ca" => Npgsql.SslMode.VerifyCA,
            "verify-full" => Npgsql.SslMode.VerifyFull,
            _ => Npgsql.SslMode.Prefer,
        };

        // No TrustServerCertificate here: from Npgsql 8 onwards Require
        // encrypts without validating the chain, and the verify- modes are
        // the ones that check it. Setting it would be a no-op.
        return builder.ConnectionString;
    }
}
