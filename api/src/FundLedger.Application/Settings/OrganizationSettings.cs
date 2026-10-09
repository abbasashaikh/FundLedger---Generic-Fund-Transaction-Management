using System.Globalization;
using System.Text.Json;

namespace FundLedger.Application.Settings;

/// <summary>
/// Typed view of <c>fl.settings</c> (Backend Schema §5). Missing keys fall back to the
/// documented defaults, which reflect the product owner's decisions of 07-Oct-2026.
/// </summary>
public sealed record OrganizationSettings
{
    public int PinMaxFailures { get; init; } = 5;

    public int PinLockoutMinutes { get; init; } = 15;

    public int SessionIdleMinutes { get; init; } = 480;

    public int SessionAbsoluteDays { get; init; } = 7;

    public int EditWindowMinutes { get; init; } = 15;

    public int BackdateDaysMember { get; init; } = 7;

    public decimal MaxAmount { get; init; } = 1_000_000.00m;

    public bool ReceiptEnabled { get; init; } = true;

    public string ReceiptFooterText { get; init; } = "Computer-generated receipt. No signature required.";

    public bool ReceiptShowRecordedBy { get; init; } = true;

    public bool OfflineEnabled { get; init; } = true;

    public int OfflineMaxQueueAgeHours { get; init; } = 72;

    public static OrganizationSettings Defaults { get; } = new();

    /// <summary>Builds settings from raw key → JSON value pairs, clamping to documented ranges.</summary>
    public static OrganizationSettings From(IReadOnlyDictionary<string, string> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var d = Defaults;
        return new OrganizationSettings
        {
            PinMaxFailures = Int(raw, "auth.pin_max_failures", d.PinMaxFailures, 3, 10),
            PinLockoutMinutes = Int(raw, "auth.pin_lockout_minutes", d.PinLockoutMinutes, 5, 1440),
            SessionIdleMinutes = Int(raw, "auth.session_idle_minutes", d.SessionIdleMinutes, 15, 1440),
            SessionAbsoluteDays = Int(raw, "auth.session_absolute_days", d.SessionAbsoluteDays, 1, 30),
            EditWindowMinutes = Int(raw, "txn.edit_window_minutes", d.EditWindowMinutes, 0, 1440),
            BackdateDaysMember = Int(raw, "txn.backdate_days_member", d.BackdateDaysMember, 0, 365),
            MaxAmount = Dec(raw, "txn.max_amount", d.MaxAmount),
            ReceiptEnabled = Bool(raw, "receipt.enabled", d.ReceiptEnabled),
            ReceiptFooterText = Str(raw, "receipt.footer_text", d.ReceiptFooterText, 200),
            ReceiptShowRecordedBy = Bool(raw, "receipt.show_recorded_by", d.ReceiptShowRecordedBy),
            OfflineEnabled = Bool(raw, "offline.enabled", d.OfflineEnabled),
            OfflineMaxQueueAgeHours = Int(raw, "offline.max_queue_age_hours", d.OfflineMaxQueueAgeHours, 1, 720),
        };
    }

    private static int Int(IReadOnlyDictionary<string, string> raw, string key, int fallback, int min, int max)
    {
        if (!raw.TryGetValue(key, out var json))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetInt32(out var v)
                ? Math.Clamp(v, min, max)
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static bool Bool(IReadOnlyDictionary<string, string> raw, string key, bool fallback)
    {
        if (!raw.TryGetValue(key, out var json))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            };
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static string Str(IReadOnlyDictionary<string, string> raw, string key, string fallback, int maxLength)
    {
        if (!raw.TryGetValue(key, out var json))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var v = doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
            return v is null ? fallback : v.Length > maxLength ? v[..maxLength] : v;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static decimal Dec(IReadOnlyDictionary<string, string> raw, string key, decimal fallback)
    {
        if (!raw.TryGetValue(key, out var json))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var s = doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : doc.RootElement.GetRawText();
            return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

/// <summary>Reads the current tenant's settings (must run inside a tenant transaction).</summary>
public interface ISettingsProvider
{
    Task<OrganizationSettings> GetAsync(CancellationToken ct);

    /// <summary>Drops the per-request cache after settings were written, so the next read sees them.</summary>
    void Invalidate();
}
