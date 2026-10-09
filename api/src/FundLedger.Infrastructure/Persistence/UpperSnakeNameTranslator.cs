using System.Text;
using Npgsql;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Maps C# enum members to the schema's PostgreSQL enum labels:
/// <c>Admin → ADMIN</c>, <c>MoneyIn → MONEY_IN</c>. Keeps Npgsql attributes out of the Domain.
/// </summary>
public sealed class UpperSnakeNameTranslator : INpgsqlNameTranslator
{
    /// <summary>
    /// The one instance to use. EF Core keys its internal service-provider cache on the
    /// options, enum mappings included; a new translator per DbContext would make every
    /// request build a fresh internal provider (a memory and latency leak).
    /// </summary>
    public static UpperSnakeNameTranslator Instance { get; } = new();

    public string TranslateTypeName(string clrName) => ToUpperSnake(clrName).ToLowerInvariant();

    public string TranslateMemberName(string clrName) => ToUpperSnake(clrName);

    public static string ToUpperSnake(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                sb.Append('_');
            }

            sb.Append(char.ToUpperInvariant(name[i]));
        }

        return sb.ToString();
    }
}
