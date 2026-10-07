using FundLedger.Application.Abstractions;
using FundLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Api.IntegrationTests;

/// <summary>
/// Proves the runtime data-access path enforces tenant isolation through RLS
/// (TRD TR-001/TR-002, ADR-0003), including the PgBouncer-safety property:
/// context never survives past its transaction.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class TenantIsolationTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly Guid OrgA = Guid.Parse("01900000-0000-7000-8000-00000000aaaa");
    private static readonly Guid OrgB = Guid.Parse("01900000-0000-7000-8000-00000000bbbb");
    private static readonly Guid AnyUser = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    public async ValueTask InitializeAsync()
    {
        if (db.SkipReason is not null)
        {
            return;
        }

        // Seeded as owner/superuser (bypasses RLS), idempotent across tests.
        await PostgresFixture.ExecuteAsync(db.AdminConnectionString, $"""
            INSERT INTO fl.organizations (id, name, short_code) VALUES
              ('{OrgA}', 'Org A', 'ORGA'), ('{OrgB}', 'Org B', 'ORGB')
            ON CONFLICT (id) DO NOTHING
            """);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Migrations_record_the_baseline()
    {
        Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);
        await using var owner = FundLedger.Infrastructure.DependencyInjection.CreateMigrationContext(db.AdminConnectionString);
        var applied = await owner.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.Contains("20261007033113_InitialSchema", applied);
    }

    [Fact]
    public async Task Tenant_sees_only_its_own_organization_inside_a_transaction()
    {
        Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        await using var ctx = CreateAppContext(OrgA);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        var visible = await ctx.Organizations.Select(o => o.Id).ToListAsync(ct);
        await tx.CommitAsync(ct);

        Assert.Equal([OrgA], visible);
    }

    [Fact]
    public async Task Other_tenant_cannot_see_first_tenant()
    {
        Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        await using var ctx = CreateAppContext(OrgB);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        var visible = await ctx.Organizations.Select(o => o.Id).ToListAsync(ct);

        Assert.Equal([OrgB], visible);
    }

    [Fact]
    public async Task Queries_outside_a_transaction_fail_closed()
    {
        Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        await using var ctx = CreateAppContext(OrgA);
        Assert.Equal(0, await ctx.Organizations.CountAsync(ct));
    }

    [Fact]
    public async Task Context_does_not_survive_the_transaction_on_a_reused_connection()
    {
        Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        await using var ctx = CreateAppContext(OrgA);
        await ctx.Database.OpenConnectionAsync(ct);           // same physical connection throughout
        await using (var tx = await ctx.Database.BeginTransactionAsync(ct))
        {
            Assert.Equal(1, await ctx.Organizations.CountAsync(ct));
            await tx.CommitAsync(ct);
        }

        // After COMMIT the transaction-local settings are gone: nothing is visible.
        Assert.Equal(0, await ctx.Organizations.CountAsync(ct));
    }

    [Fact]
    public async Task Runtime_role_cannot_delete_financial_rows()
    {
        Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        await using var ctx = CreateAppContext(OrgA);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ctx.Database.ExecuteSqlRawAsync("DELETE FROM fl.transactions", ct));
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege (BR-012)
    }

    private FundLedgerDbContext CreateAppContext(Guid org)
    {
        var tenant = new TenantContext();
        tenant.Set(org, AnyUser, isAdmin: true);
        var options = new DbContextOptionsBuilder<FundLedgerDbContext>()
            .UseNpgsql(db.AppConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantTransactionInterceptor(tenant))
            .Options;
        return new FundLedgerDbContext(options);
    }
}
