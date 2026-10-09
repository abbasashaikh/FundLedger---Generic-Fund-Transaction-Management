using System.Diagnostics;
using FundLedger.Api.IntegrationTests.Support;
using Xunit.Sdk;

namespace FundLedger.Api.IntegrationTests;

/// <summary>
/// Capacity check (plan P5-10, TRD §13): seeds a large fund and times the screens that read it. Opt-in, because it writes
/// hundreds of thousands of rows: set <c>FUNDLEDGER_PERF_ROWS</c> (for example 200000). Prints a timing table and the
/// query plans of the hot paths. Targets: CRUD reads p95 &lt; 300 ms, reports over one fund-year p95 &lt; 2 s.
/// Run it from a machine near the database: each request makes several round trips, so from a laptop to a remote database
/// the timings mostly measure the network, not the API.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class PerformanceTests(PostgresFixture db, ITestOutputHelper output) : IAsyncLifetime
{
    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static int Rows => int.TryParse(Environment.GetEnvironmentVariable("FUNDLEDGER_PERF_ROWS"), out var n) ? n : 0;

    [Fact]
    public async Task Screens_stay_fast_on_a_large_fund()
    {
        Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);
        Assert.SkipWhen(Rows == 0, "Set FUNDLEDGER_PERF_ROWS to run the capacity check.");

        var b = await Books.CreateAsync(_host, openingCash: 100000);
        var rows = Rows;
        var sw = Stopwatch.StartNew();
        // ~9 in 10 entries are Money In/Out, the rest transfers, spread over the last 12 months, by the Admin.
        await using (var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new Npgsql.NpgsqlCommand($"""
                INSERT INTO fl.transactions (id, organization_id, fund_id, txn_number, txn_type, amount, txn_date, txn_time, category_id, account_id, from_account_id, to_account_id,
                                             payment_mode_id, received_from, paid_to, purpose, created_by)
                SELECT gen_random_uuid(), '{b.Org.OrganizationId}', '{b.FundId}', 'PERF-' || lpad(i::text, 8, '0'),
                       (CASE WHEN i % 10 = 0 THEN 'TRANSFER' WHEN i % 3 = 0 THEN 'EXPENSE' ELSE 'DEPOSIT' END)::fl.txn_type,
                       ((i % 5000) + 1)::numeric(18,2), current_date - ((i % 360)::int), time '09:00' + ((i % 600) * interval '1 minute'),
                       CASE WHEN i % 10 = 0 THEN NULL WHEN i % 3 = 0 THEN '{b.CatOut}'::uuid ELSE '{b.CatIn}'::uuid END,
                       CASE WHEN i % 10 = 0 THEN NULL ELSE '{b.Cash}'::uuid END,
                       CASE WHEN i % 10 = 0 THEN '{b.Cash}'::uuid END, CASE WHEN i % 10 = 0 THEN '{b.Bank}'::uuid END,
                       CASE WHEN i % 10 = 0 THEN NULL ELSE '{b.ModeCash}'::uuid END,
                       CASE WHEN i % 3 <> 0 AND i % 10 <> 0 THEN 'Donor ' || (i % 977) END, CASE WHEN i % 3 = 0 AND i % 10 <> 0 THEN 'Vendor ' || (i % 311) END,
                       'Entry number ' || i, '{b.Org.AdminId}'
                FROM generate_series(1, {rows}) AS i
                """, conn) { CommandTimeout = 1800 };
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var analyze = new Npgsql.NpgsqlCommand("ANALYZE fl.transactions", conn);
            await analyze.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        output.WriteLine($"seeded {rows:N0} entries in {sw.Elapsed.TotalSeconds:F0} s");

        var from = Books.Today().AddDays(-365).ToString("yyyy-MM-dd");
        var to = Books.Today().ToString("yyyy-MM-dd");
        var cases = new (string Name, string Path, double TargetMs)[]
        {
            ("Ledger, newest 50", $"/api/v1/transactions?fundId={b.FundId}&limit=50", 300),
            ("Ledger, page 40 of the list", $"/api/v1/transactions?fundId={b.FundId}&limit=50&cursor={Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("o:2000")))}", 300),
            ("Ledger, text search", $"/api/v1/transactions?fundId={b.FundId}&q=Donor%20123&limit=50", 300),
            ("Ledger, one-month filter", $"/api/v1/transactions?fundId={b.FundId}&from={Books.Today().AddDays(-30):yyyy-MM-dd}&to={to}&limit=50", 300),
            ("Dashboard", $"/api/v1/dashboard?fundId={b.FundId}", 300),
            ("Account balances", $"/api/v1/accounts/balances?fundId={b.FundId}", 300),
            ("Report: fund summary, 1 year", $"/api/v1/reports/FUND_SUMMARY?fundId={b.FundId}&from={from}&to={to}", 2000),
            ("Report: daily, 1 year", $"/api/v1/reports/DAILY?fundId={b.FundId}&from={from}&to={to}", 2000),
            ("Report: category, 1 year", $"/api/v1/reports/CATEGORY?fundId={b.FundId}&from={from}&to={to}", 2000),
            ("Report: money in list, 1 year", $"/api/v1/reports/MONEY_IN?fundId={b.FundId}&from={from}&to={to}", 2000),
            ("Report: account balance", $"/api/v1/reports/ACCOUNT_BALANCE?fundId={b.FundId}&to={to}", 2000),
        };

        var slow = new List<string>();
        output.WriteLine($"{"screen",-34}{"p50 ms",8}{"max ms",8}   target");
        foreach (var (name, path, target) in cases)
        {
            await b.Admin.GetAsync(path);                                              // warm-up (JIT, connection, plan cache)
            var times = new List<double>();
            for (var i = 0; i < 7; i++)
            {
                var t = Stopwatch.StartNew();
                var response = await b.Admin.GetAsync(path);
                response.EnsureSuccessStatusCode();
                times.Add(t.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            output.WriteLine($"{name,-34}{times[3],8:F0}{times[^1],8:F0}   < {target:F0}");
            if (times[^1] > target)
            {
                slow.Add($"{name}: worst {times[^1]:F0} ms (target {target:F0})");
            }
        }

        await using (var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            foreach (var (title, sql) in new[]
            {
                ("ledger page", $"SELECT * FROM fl.transactions WHERE fund_id = '{b.FundId}' ORDER BY txn_date DESC, txn_time DESC, id DESC LIMIT 51"),
                ("fund balance view", $"SELECT * FROM fl.v_fund_balances WHERE fund_id = '{b.FundId}'"),
                ("report load (1 year)", $"SELECT id, txn_number, amount FROM fl.transactions WHERE fund_id = '{b.FundId}' AND txn_date <= current_date"),
            })
            {
                await using var plan = new Npgsql.NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, conn) { CommandTimeout = 300 };
                await using var reader = await plan.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                var lines = new List<string>();
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    lines.Add(reader.GetString(0));
                }

                output.WriteLine($"-- plan: {title}\n{string.Join('\n', lines.Take(14))}");
            }
        }

        if (slow.Count > 0)
        {
            throw new XunitException("Over target:\n" + string.Join('\n', slow));
        }
    }
}
