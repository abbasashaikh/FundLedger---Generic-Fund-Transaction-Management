using System.Globalization;
using System.Text.Json;
using FluentValidation;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Settings;

public sealed record OrganizationProfile(
    string Name, string? ContactMobile, string? ContactEmail, string? Address, string? RegistrationNumber,
    string CurrencyCode, string Timezone, string DateFormat);

public sealed record TransactionSettings(int EditWindowMinutes, int BackdateDaysMember, string MaxAmount);

public sealed record AuthSettings(int PinMaxFailures, int PinLockoutMinutes, int SessionIdleMinutes, int SessionAbsoluteDays);

public sealed record ReceiptSettings(bool Enabled, string FooterText, bool ShowRecordedBy);

/// <summary>
/// The Settings screen (App Flow §5.5, S26). Only settings the app already acts on are listed; attachment and
/// offline settings join in their phases. Currency (INR) and the numbering period (Indian FY, decision Q-08)
/// are fixed in V1 and therefore read-only.
/// </summary>
public sealed record SettingsDto(OrganizationProfile Organization, TransactionSettings Transactions, AuthSettings Auth, ReceiptSettings Receipts);

public sealed class SettingsDtoValidator : AbstractValidator<SettingsDto>
{
    public SettingsDtoValidator()
    {
        RuleFor(x => x.Organization).NotNull();
        RuleFor(x => x.Organization.Name).NotEmpty().MaximumLength(150).When(x => x.Organization is not null);
        RuleFor(x => x.Organization.ContactMobile).MaximumLength(15).When(x => x.Organization is not null);
        RuleFor(x => x.Organization.ContactEmail).EmailAddress().MaximumLength(120).When(x => !string.IsNullOrWhiteSpace(x.Organization?.ContactEmail));
        RuleFor(x => x.Organization.Address).MaximumLength(500).When(x => x.Organization is not null);
        RuleFor(x => x.Organization.RegistrationNumber).MaximumLength(60).When(x => x.Organization is not null);
        RuleFor(x => x.Organization.DateFormat).Must(f => f is "dd-MMM-yyyy" or "dd/MM/yyyy" or "yyyy-MM-dd")
            .WithMessage("Choose one of the supported date formats.").When(x => x.Organization is not null);
        RuleFor(x => x.Organization.Timezone).Must(BeKnownZone).WithMessage("Unknown time zone.").When(x => x.Organization is not null);

        RuleFor(x => x.Transactions).NotNull();
        RuleFor(x => x.Transactions.EditWindowMinutes).InclusiveBetween(0, 1440).When(x => x.Transactions is not null);
        RuleFor(x => x.Transactions.BackdateDaysMember).InclusiveBetween(0, 365).When(x => x.Transactions is not null);
        RuleFor(x => x.Transactions.MaxAmount).Must(BeAmount).WithMessage("Enter an amount between 1 and 99,99,99,99,999.99 with at most 2 decimals.")
            .When(x => x.Transactions is not null);

        RuleFor(x => x.Auth).NotNull();
        RuleFor(x => x.Auth.PinMaxFailures).InclusiveBetween(3, 10).When(x => x.Auth is not null);
        RuleFor(x => x.Auth.PinLockoutMinutes).InclusiveBetween(5, 1440).When(x => x.Auth is not null);
        RuleFor(x => x.Auth.SessionIdleMinutes).InclusiveBetween(15, 1440).When(x => x.Auth is not null);
        RuleFor(x => x.Auth.SessionAbsoluteDays).InclusiveBetween(1, 30).When(x => x.Auth is not null);

        RuleFor(x => x.Receipts).NotNull();
        RuleFor(x => x.Receipts.FooterText).NotNull().MaximumLength(200).When(x => x.Receipts is not null);
    }

    private static bool BeKnownZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return id == "Asia/Kolkata";
        }
    }

    private static bool BeAmount(string? s) =>
        decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
        && v >= 1 && v <= 99_999_999_999.99m && decimal.Round(v, 2) == v;
}

/// <summary>Reads and saves organization profile + settings. Admin only; every change is audited with old → new values.</summary>
public sealed class SettingsService(IFundLedgerDb db, ICurrentUser caller, ISettingsProvider provider, IAuditWriter audit, TimeProvider clock)
{
    public async Task<SettingsDto> GetAsync(CancellationToken ct)
    {
        RequireAdmin();
        var org = await db.Organizations.AsNoTracking().SingleAsync(ct).ConfigureAwait(false);
        var s = await provider.GetAsync(ct).ConfigureAwait(false);
        return new SettingsDto(
            new OrganizationProfile(org.Name, org.ContactMobile, org.ContactEmail, org.Address, org.RegistrationNumber, org.CurrencyCode, org.Timezone, org.DateFormat),
            new TransactionSettings(s.EditWindowMinutes, s.BackdateDaysMember, s.MaxAmount.ToString("0.00", CultureInfo.InvariantCulture)),
            new AuthSettings(s.PinMaxFailures, s.PinLockoutMinutes, s.SessionIdleMinutes, s.SessionAbsoluteDays),
            new ReceiptSettings(s.ReceiptEnabled, s.ReceiptFooterText, s.ReceiptShowRecordedBy));
    }

    public async Task<SettingsDto> SaveAsync(SettingsDto next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(next);
        RequireAdmin();
        var before = await GetAsync(ct).ConfigureAwait(false);
        if (next.Organization.CurrencyCode != before.Organization.CurrencyCode)
        {
            throw ValidationFailedException.For("organization.currencyCode", "The currency can't be changed in this version.");
        }

        var changes = new Dictionary<string, (object? Old, object? New)>(StringComparer.Ordinal);
        void Track(string key, object? a, object? b)
        {
            if (!Equals(a, b))
            {
                changes[key] = (a, b);
            }
        }

        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var o = next.Organization;
        var org = await db.Organizations.SingleAsync(ct).ConfigureAwait(false);
        Track("organization.name", org.Name, o.Name.Trim());
        Track("organization.contactMobile", org.ContactMobile, Clean(o.ContactMobile));
        Track("organization.contactEmail", org.ContactEmail, Clean(o.ContactEmail));
        Track("organization.address", org.Address, Clean(o.Address));
        Track("organization.registrationNumber", org.RegistrationNumber, Clean(o.RegistrationNumber));
        Track("organization.timezone", org.Timezone, o.Timezone);
        Track("organization.dateFormat", org.DateFormat, o.DateFormat);
        org.Name = o.Name.Trim();
        org.ContactMobile = Clean(o.ContactMobile);
        org.ContactEmail = Clean(o.ContactEmail);
        org.Address = Clean(o.Address);
        org.RegistrationNumber = Clean(o.RegistrationNumber);
        org.Timezone = o.Timezone;
        org.DateFormat = o.DateFormat;
        org.UpdatedAt = clock.GetUtcNow();

        var maxAmount = decimal.Parse(next.Transactions.MaxAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture);
        var values = new (string Key, object Old, object New)[]
        {
            ("txn.edit_window_minutes", before.Transactions.EditWindowMinutes, next.Transactions.EditWindowMinutes),
            ("txn.backdate_days_member", before.Transactions.BackdateDaysMember, next.Transactions.BackdateDaysMember),
            ("txn.max_amount", before.Transactions.MaxAmount, maxAmount),
            ("auth.pin_max_failures", before.Auth.PinMaxFailures, next.Auth.PinMaxFailures),
            ("auth.pin_lockout_minutes", before.Auth.PinLockoutMinutes, next.Auth.PinLockoutMinutes),
            ("auth.session_idle_minutes", before.Auth.SessionIdleMinutes, next.Auth.SessionIdleMinutes),
            ("auth.session_absolute_days", before.Auth.SessionAbsoluteDays, next.Auth.SessionAbsoluteDays),
            ("receipt.enabled", before.Receipts.Enabled, next.Receipts.Enabled),
            ("receipt.footer_text", before.Receipts.FooterText, next.Receipts.FooterText.Trim()),
            ("receipt.show_recorded_by", before.Receipts.ShowRecordedBy, next.Receipts.ShowRecordedBy),
        };

        var now = clock.GetUtcNow();
        foreach (var (key, old, value) in values.Where(v => !Equals(v.Old, v.New)))
        {
            changes[key] = (old, value);
            var json = JsonSerializer.Serialize(value);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO fl.settings (organization_id, key, value, updated_by, updated_at)
                VALUES ({caller.OrganizationId}, {key}, {json}::jsonb, {caller.UserId}, {now})
                ON CONFLICT (organization_id, key) DO UPDATE SET value = EXCLUDED.value, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
                """, ct).ConfigureAwait(false);
        }

        if (changes.Count == 0)
        {
            return before;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.SettingsChanged, AuditEntities.Organization,
            caller.OrganizationId.ToString(),
            OldValue: changes.ToDictionary(c => c.Key, c => c.Value.Old),
            NewValue: changes.ToDictionary(c => c.Key, c => c.Value.New)), ct).ConfigureAwait(false);

        provider.Invalidate();
        return await GetAsync(ct).ConfigureAwait(false);
    }

    private void RequireAdmin()
    {
        if (!caller.IsAdmin)
        {
            throw new ForbiddenException();
        }
    }
}
