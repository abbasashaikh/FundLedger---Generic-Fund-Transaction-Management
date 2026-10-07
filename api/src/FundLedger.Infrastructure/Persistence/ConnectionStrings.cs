using Npgsql;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Accepts either an Npgsql key/value connection string or a
/// <c>postgresql://user:pass@host/db?sslmode=require</c> URL (the format Neon
/// hands out), and returns an Npgsql connection string.
/// </summary>
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

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
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
