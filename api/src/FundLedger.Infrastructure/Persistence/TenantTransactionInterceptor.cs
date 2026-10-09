using System.Data.Common;
using FundLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Copies the caller's tenant context into PostgreSQL at the start of every DB
/// transaction using <c>set_config(name, value, is_local =&gt; true)</c>.
///
/// Why transaction-local, not session-level: production connects through
/// Neon's PgBouncer in <b>transaction</b> pooling mode (ADR-0006). A session-level
/// setting could stay on a server connection that PgBouncer then hands to
/// another client — a cross-tenant leak. Transaction-local settings vanish at
/// COMMIT/ROLLBACK, and PgBouncer pins one server connection per transaction.
///
/// Consequence (fail-closed): a query that runs outside an explicit transaction
/// has no tenant context, so RLS returns no rows. All tenant data access must go
/// through a transaction (request filter or <see cref="AutoTransactionBehavior"/>).
/// </summary>
public sealed class TenantTransactionInterceptor(ITenantContext tenant) : DbTransactionInterceptor
{
    private const string SetContextSql =
        "SELECT set_config('app.org_id', @org, true), set_config('app.user_id', @usr, true), set_config('app.is_admin', @adm, true)";

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = CreateCommand(connection, result);
        command.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, result);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private DbCommand CreateCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SetContextSql;
        AddParameter(command, "org", tenant.OrganizationId?.ToString() ?? string.Empty);
        AddParameter(command, "usr", tenant.UserId?.ToString() ?? string.Empty);
        AddParameter(command, "adm", tenant.IsAdmin ? "true" : "false");
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
