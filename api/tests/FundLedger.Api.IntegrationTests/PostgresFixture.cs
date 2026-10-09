using System.Security.Cryptography;
using FundLedger.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FundLedger.Api.IntegrationTests;

/// <summary>
/// A throwaway PostgreSQL database with all migrations applied and a dedicated
/// runtime login role (member of <c>fundledger_app</c>, no BYPASSRLS).
///
/// Source, in order:
///  1. Docker available → PostgreSQL 17 container (CI and local).
///  2. <c>FUNDLEDGER_TEST_ADMIN_URL</c> set → a fresh database created on that
///     server (e.g. a temporary Neon branch), dropped afterwards.
///  3. Neither → <see cref="SkipReason"/> is set and DB tests are skipped.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _serverAdminConnection;   // connection to the server's maintenance DB
    private string? _databaseName;

    public string? SkipReason { get; private set; }

    /// <summary>Owner/superuser connection to the test database.</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Runtime-role connection to the test database (RLS enforced).</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public string AppRoleName { get; } = "fl_test_app_" + RandomNumberGenerator.GetHexString(8, lowercase: true);

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("FUNDLEDGER_TEST_ADMIN_URL");
        if (await TryStartContainerAsync().ConfigureAwait(false) is { } containerConnection)
        {
            AdminConnectionString = containerConnection;
        }
        else if (!string.IsNullOrWhiteSpace(external))
        {
            _serverAdminConnection = Infrastructure.Persistence.ConnectionStrings.Normalize(external);
            _databaseName = "fl_test_" + RandomNumberGenerator.GetHexString(8, lowercase: true);
            await ExecuteAsync(_serverAdminConnection, $"CREATE DATABASE {_databaseName}").ConfigureAwait(false);
            AdminConnectionString = new NpgsqlConnectionStringBuilder(_serverAdminConnection) { Database = _databaseName }.ConnectionString;
        }
        else if (Environment.GetEnvironmentVariable("FUNDLEDGER_REQUIRE_DB_TESTS") == "true")
        {
            // CI sets this so database tests can never be skipped silently.
            throw new InvalidOperationException("Database tests are required but no PostgreSQL is available.");
        }
        else
        {
            SkipReason = "No Docker and no FUNDLEDGER_TEST_ADMIN_URL: database tests skipped.";
            return;
        }

        await using (var db = DependencyInjection.CreateMigrationContext(AdminConnectionString))
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
        }

        // Strong random password: managed hosts (Neon) reject weak ones.
        var password = RandomNumberGenerator.GetHexString(24) + "Aa1-";
        await ExecuteAsync(AdminConnectionString,
            $"CREATE ROLE {AppRoleName} LOGIN PASSWORD '{password}' NOBYPASSRLS IN ROLE fundledger_app").ConfigureAwait(false);

        AppConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Username = AppRoleName,
            Password = password,
        }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (_serverAdminConnection is not null && _databaseName is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(_serverAdminConnection, $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)").ConfigureAwait(false);
            await ExecuteAsync(_serverAdminConnection, $"DROP ROLE IF EXISTS {AppRoleName}").ConfigureAwait(false);
        }
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<string?> TryStartContainerAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17").Build();
            await _container.StartAsync().ConfigureAwait(false);
            return _container.GetConnectionString();
        }
#pragma warning disable CA1031 // any failure here simply means "no Docker"
        catch (Exception)
#pragma warning restore CA1031
        {
            _container = null;
            return null;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
