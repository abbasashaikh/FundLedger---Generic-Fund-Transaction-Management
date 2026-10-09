using FundLedger.Application.Settings;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>Reads <c>fl.settings</c> for the current tenant (RLS-scoped), once per request.</summary>
public sealed class SettingsProvider(FundLedgerDbContext db) : ISettingsProvider
{
    private OrganizationSettings? _cached;

    public void Invalidate() => _cached = null;

    public async Task<OrganizationSettings> GetAsync(CancellationToken ct)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var rows = await RawSql.QueryAsync(db, "SELECT key, value::text FROM fl.settings", [],
            r => (Key: r.GetString(0), Value: r.GetString(1)), ct).ConfigureAwait(false);
        _cached = OrganizationSettings.From(rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal));
        return _cached;
    }
}
