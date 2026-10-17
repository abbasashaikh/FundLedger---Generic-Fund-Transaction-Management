using Npgsql;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Accepts either an Npgsql key/value connection string or a
/// <c>postgresql://user:pass@host/db?sslmode=require</c> URL (the format Neon
/// hands out), and returns an Npgsql connection string.
/// </summary>
/// <remarks>
/// The URL is split by hand rather than with <see cref="Uri"/>: a password that contains <c>/ ? # : @</c>
/// (unescaped, as people paste it) makes <see cref="Uri"/> throw "Invalid port specified". The user name and
/// password are everything before the LAST <c>@</c>, split at the first <c>:</c>; both may be percent-encoded.
/// </remarks>
public static class ConnectionStrings
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();

        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var rest = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var at = rest.LastIndexOf('@');
        var userInfo = at >= 0 ? rest[..at] : string.Empty;
        var location = at >= 0 ? rest[(at + 1)..] : rest;

        var queryStart = location.IndexOf('?', StringComparison.Ordinal);
        var query = queryStart >= 0 ? location[(queryStart + 1)..] : string.Empty;
        var hostAndPath = queryStart >= 0 ? location[..queryStart] : location;

        var slash = hostAndPath.IndexOf('/', StringComparison.Ordinal);
        var authority = slash >= 0 ? hostAndPath[..slash] : hostAndPath;
        var database = slash >= 0 ? Uri.UnescapeDataString(hostAndPath[(slash + 1)..]) : string.Empty;

        var host = authority;
        var port = 5432;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0 && int.TryParse(authority[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            host = authority[..colon];
            port = parsed;
        }

        var credentials = userInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,
        };

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]).ToUpperInvariant();
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            switch (key)
            {
                case "SSLMODE":
                    builder.SslMode = Enum.Parse<SslMode>(val.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true);
                    break;
                case "CHANNEL_BINDING":
                    builder.ChannelBinding = Enum.Parse<ChannelBinding>(val, ignoreCase: true);
                    break;
                default:
                    break; // unknown URL options are ignored deliberately
            }
        }

        return builder.ConnectionString;
    }
}
