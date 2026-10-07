using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Small ADO.NET reader over the context's connection and current transaction, for
/// queries that don't map to entities (function calls, aggregates). Positional
/// mapping avoids any dependence on EF's naming conventions for ad-hoc result types.
/// </summary>
internal static class RawSql
{
    public static async Task<List<T>> QueryAsync<T>(
        FundLedgerDbContext db, string sql, (string Name, object Value)[] parameters, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (name, value) in parameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.Value = value;
                command.Parameters.Add(p);
            }

            var results = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(map(reader));
            }

            return results;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
