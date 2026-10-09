using System.Globalization;
using System.Text;
using System.Text.Json;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Funds;
using FundLedger.Application.Settings;
using FundLedger.Domain;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Ledger;
using FundLedger.Domain.Lookups;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FundLedger.Application.Ledger;

/// <summary>
/// Records and reads transactions (PRD §7–§13, §16). Every write follows the pipeline in
/// TRD §7.2 inside one DB transaction: authorize → fund Active → validate references and
/// dates → allocate number → insert → revision 1 → audit. The database repeats the critical
/// rules (shape CHECKs, closed-fund trigger, unique client id), so a bug here can't store
/// an invalid row. Edit and cancel arrive in Phase 3.
/// </summary>
public sealed class LedgerService(
    IFundLedgerDb db, ICurrentUser caller, FundAccessGuard guard, ISettingsProvider settings, IAuditWriter audit,
    IBalanceReader balances, TimeProvider clock)
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- create ---------------------------------------------------------------------
    public async Task<TransactionResult> CreateDepositAsync(CreateDepositRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        return await CreateAsync(r.FundId, FundCapability.MoneyIn, TxnType.Deposit, r.Amount, r.TxnDate, r.TxnTime, r.ClientTxnId, async (fund) =>
        {
            await RequireCategoryAsync(r.CategoryId, CategoryDirection.MoneyIn, fund.Id, ct).ConfigureAwait(false);
            await RequireAccountAsync(r.AccountId, "accountId", ct).ConfigureAwait(false);
            await RequirePaymentModeAsync(r.PaymentModeId, r.ReferenceNumber, ct).ConfigureAwait(false);
        }, t =>
        {
            (t.CategoryId, t.AccountId, t.PaymentModeId) = (r.CategoryId, r.AccountId, r.PaymentModeId);
            (t.ReceivedFrom, t.Purpose) = (Clean(r.ReceivedFrom), r.Purpose.Trim());
            (t.ReferenceNumber, t.Remarks) = (Clean(r.ReferenceNumber), Clean(r.Remarks));
        }, ct).ConfigureAwait(false);
    }

    public async Task<TransactionResult> CreateExpenseAsync(CreateExpenseRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        return await CreateAsync(r.FundId, FundCapability.MoneyOut, TxnType.Expense, r.Amount, r.TxnDate, r.TxnTime, r.ClientTxnId, async (fund) =>
        {
            await RequireCategoryAsync(r.CategoryId, CategoryDirection.MoneyOut, fund.Id, ct).ConfigureAwait(false);
            await RequireAccountAsync(r.AccountId, "accountId", ct).ConfigureAwait(false);
            await RequirePaymentModeAsync(r.PaymentModeId, r.ReferenceNumber, ct).ConfigureAwait(false);
        }, t =>
        {
            (t.CategoryId, t.AccountId, t.PaymentModeId) = (r.CategoryId, r.AccountId, r.PaymentModeId);
            (t.PaidTo, t.Purpose) = (Clean(r.PaidTo), r.Purpose.Trim());
            (t.ReferenceNumber, t.Remarks) = (Clean(r.ReferenceNumber), Clean(r.Remarks));
        }, ct).ConfigureAwait(false);
    }

    public async Task<TransactionResult> CreateTransferAsync(CreateTransferRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.FromAccountId == r.ToAccountId)
        {
            throw new DomainException("TRANSFER_SAME_ACCOUNT", "Choose two different accounts.");   // BR-010
        }

        return await CreateAsync(r.FundId, FundCapability.Transfer, TxnType.Transfer, r.Amount, r.TxnDate, r.TxnTime, r.ClientTxnId, async (_) =>
        {
            await RequireAccountAsync(r.FromAccountId, "fromAccountId", ct).ConfigureAwait(false);
            await RequireAccountAsync(r.ToAccountId, "toAccountId", ct).ConfigureAwait(false);
            if (r.PaymentModeId is { } pm)
            {
                await RequirePaymentModeAsync(pm, r.ReferenceNumber, ct).ConfigureAwait(false);
            }
        }, t =>
        {
            (t.FromAccountId, t.ToAccountId, t.PaymentModeId) = (r.FromAccountId, r.ToAccountId, r.PaymentModeId);
            (t.Purpose, t.ReferenceNumber, t.Remarks) = (r.Purpose.Trim(), Clean(r.ReferenceNumber), Clean(r.Remarks));
        }, ct).ConfigureAwait(false);
    }

    private async Task<TransactionResult> CreateAsync(
        Guid fundId, FundCapability capability, TxnType type, string amountText, DateOnly date, string timeText, Guid? clientTxnId,
        Func<Fund, Task> validateReferences, Action<Transaction> fill, CancellationToken ct)
    {
        var fund = await guard.RequireAsync(fundId, capability, ct).ConfigureAwait(false);

        // Idempotent retry (BR-018/019): the same client id returns the entry already recorded.
        if (clientTxnId is { } client)
        {
            var existing = await db.Transactions.AsNoTracking()
                .Where(t => t.ClientTxnId == client).Select(t => new { t.Id, t.CreatedBy, t.FundId }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.CreatedBy != caller.UserId || existing.FundId != fundId)
                {
                    throw new ConflictException("DUPLICATE_CLIENT_TXN", "This entry id was already used.");
                }

                return new TransactionResult(await GetDtoAsync(existing.Id, ct).ConfigureAwait(false), await ClosingAsync(fundId, ct).ConfigureAwait(false), Duplicate: true);
            }
        }

        if (fund.Status != FundStatus.Active)
        {
            throw new ConflictException("FUND_NOT_ACTIVE", "This fund is not active. New entries can't be added.");   // BR-017
        }

        var s = await settings.GetAsync(ct).ConfigureAwait(false);
        var amount = Money.Parse(amountText);
        if (amount.Amount <= 0m || amount.Amount > s.MaxAmount)
        {
            throw new DomainException("AMOUNT_OUT_OF_RANGE", $"Amount must be between 0.01 and {IndianNumberFormat.Format(s.MaxAmount, includeDecimals: false)}.");
        }

        var (today, now) = await OrgNowAsync(ct).ConfigureAwait(false);
        if (!TxnRules.TryParseTime(timeText, out var time))
        {
            throw ValidationFailedException.For("txnTime", "Enter the time as HH:mm.");
        }

        if (date > today || (date == today && time > TimeOnly.FromDateTime(now).AddMinutes(5)))
        {
            throw new DomainException("DATE_IN_FUTURE", "The date and time can't be in the future.");
        }

        if (!caller.IsAdmin && today.DayNumber - date.DayNumber > s.BackdateDaysMember)
        {
            throw new DomainException("BACKDATE_LIMIT_EXCEEDED", $"Entries can be dated at most {s.BackdateDaysMember} days back. Ask an Admin.");
        }

        await validateReferences(fund).ConfigureAwait(false);

        var period = PeriodKey(date);
        var seq = await NextNumberAsync(fund, period, ct).ConfigureAwait(false);
        var txn = new Transaction
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = caller.OrganizationId,
            FundId = fund.Id,
            TxnNumber = string.Create(CultureInfo.InvariantCulture, $"{fund.Code}-{period}-{seq:D6}"),
            TxnType = type,
            Amount = amount.Amount,
            TxnDate = date,
            TxnTime = time,
            ClientTxnId = clientTxnId,
            CreatedBy = caller.UserId,
        };
        fill(txn);
        db.Transactions.Add(txn);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { ConstraintName: "uq_txn_client_id" })
        {
            throw new ConflictException("DUPLICATE_CLIENT_TXN", "This entry was already submitted. Refresh to see it.");
        }

        var dto = await GetDtoAsync(txn.Id, ct).ConfigureAwait(false);
        await WriteRevisionAsync(dto, null, ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.TxnCreated, AuditEntities.Transaction,
            txn.Id.ToString(), NewValue: new { dto.TxnNumber, type = type.ToString(), dto.Amount, account = dto.Account?.Name ?? dto.FromAccount?.Name }, FundId: fund.Id), ct)
            .ConfigureAwait(false);

        return new TransactionResult(dto, await ClosingAsync(fund.Id, ct).ConfigureAwait(false), Duplicate: false);
    }

    // ---- read -----------------------------------------------------------------------
    public async Task<TransactionDto> GetAsync(Guid id, CancellationToken ct)
    {
        var head = await db.Transactions.AsNoTracking().Where(t => t.Id == id)
            .Select(t => new { t.FundId, t.CreatedBy }).SingleOrDefaultAsync(ct).ConfigureAwait(false) ?? throw new NotFoundException();

        // Unknown id, other tenant, and a fund the caller can't see are all indistinguishable (404).
        await guard.RequireAsync(head.FundId, FundCapability.View, ct).ConfigureAwait(false);
        if (await OwnOnlyAsync(head.FundId, ct).ConfigureAwait(false) && head.CreatedBy != caller.UserId)
        {
            throw new NotFoundException();
        }

        return await GetDtoAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<TransactionPage> ListAsync(TransactionQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        await guard.RequireAsync(q.FundId, FundCapability.View, ct).ConfigureAwait(false);
        var ownOnly = await OwnOnlyAsync(q.FundId, ct).ConfigureAwait(false);

        var filtered = Filter(q, ownOnly);
        var sorted = q.Sort switch
        {
            TxnSort.Oldest => filtered.OrderBy(x => x.t.TxnDate).ThenBy(x => x.t.TxnTime).ThenBy(x => x.t.Id),
            TxnSort.Highest => filtered.OrderByDescending(x => x.t.Amount).ThenByDescending(x => x.t.Id),
            TxnSort.Lowest => filtered.OrderBy(x => x.t.Amount).ThenBy(x => x.t.Id),
            _ => filtered.OrderByDescending(x => x.t.TxnDate).ThenByDescending(x => x.t.TxnTime).ThenByDescending(x => x.t.Id),
        };

        var limit = Math.Clamp(q.Limit ?? DefaultPageSize, 1, MaxPageSize);
        var offset = DecodeCursor(q.Cursor);
        var rows = await sorted.Skip(offset).Take(limit + 1).ToListAsync(ct).ConfigureAwait(false);
        var hasMore = rows.Count > limit;

        var totals = await TotalsAsync(Filter(q with { Status = TxnStatus.Active }, ownOnly), ct).ConfigureAwait(false);
        return new TransactionPage(rows.Take(limit).Select(ToDto).ToList(), hasMore ? EncodeCursor(offset + limit) : null, totals, ownOnly);
    }

    public async Task<DashboardDto> DashboardAsync(Guid fundId, CancellationToken ct)
    {
        var fund = await guard.RequireAsync(fundId, FundCapability.View, ct).ConfigureAwait(false);
        var ownOnly = await OwnOnlyAsync(fundId, ct).ConfigureAwait(false);
        var (today, _) = await OrgNowAsync(ct).ConfigureAwait(false);

        var bal = await balances.GetFundBalanceAsync(fundId, ct).ConfigureAwait(false);
        var todayRows = await db.Transactions.AsNoTracking()
            .Where(t => t.FundId == fundId && t.Status == TxnStatus.Active && t.TxnDate == today && (!ownOnly || t.CreatedBy == caller.UserId))
            .GroupBy(t => t.TxnType).Select(g => new { Type = g.Key, Sum = g.Sum(t => t.Amount) }).ToListAsync(ct).ConfigureAwait(false);

        var recent = await ListAsync(new TransactionQuery(fundId, null, null, null, null, null, null, null, null, null, TxnSort.Newest, 10, null), ct)
            .ConfigureAwait(false);
        return new DashboardDto(fund.Id, fund.Name, fund.Status.ToString().ToUpperInvariant(), M(bal.Closing), M(bal.MoneyIn), M(bal.MoneyOut),
            M(todayRows.FirstOrDefault(x => x.Type == TxnType.Deposit)?.Sum ?? 0m), M(todayRows.FirstOrDefault(x => x.Type == TxnType.Expense)?.Sum ?? 0m),
            await AccountBalancesAsync(fundId, ct).ConfigureAwait(false), recent.Items, ownOnly);
    }

    /// <summary>Balance of every active account for the fund (zero rows included), for transfer pickers and the dashboard.</summary>
    public async Task<IReadOnlyList<AccountBalanceDto>> AccountBalancesAsync(Guid fundId, CancellationToken ct)
    {
        await guard.RequireAsync(fundId, FundCapability.View, ct).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var computed = (await balances.GetAccountBalancesAsync(fundId, ct).ConfigureAwait(false)).ToDictionary(b => b.AccountId);
        return accounts.Select(a =>
        {
            var b = computed.GetValueOrDefault(a.Id);
            return new AccountBalanceDto(a.Id, a.Name, a.Kind.ToString().ToUpperInvariant(), M(b?.Opening ?? 0), M(b?.MoneyIn ?? 0), M(b?.MoneyOut ?? 0),
                M(b?.TransfersIn ?? 0), M(b?.TransfersOut ?? 0), M(b?.AdjustmentsNet ?? 0), M(b?.Closing ?? 0));
        }).ToList();
    }

    // ---- internals --------------------------------------------------------------------
    private IQueryable<Row> Joined() =>
        from t in db.Transactions.AsNoTracking()
        join u in db.Users.AsNoTracking() on t.CreatedBy equals u.Id
        join c in db.Categories.AsNoTracking() on t.CategoryId equals c.Id into cg
        from c in cg.DefaultIfEmpty()
        join a in db.Accounts.AsNoTracking() on t.AccountId equals a.Id into ag
        from a in ag.DefaultIfEmpty()
        join fa in db.Accounts.AsNoTracking() on t.FromAccountId equals fa.Id into fag
        from fa in fag.DefaultIfEmpty()
        join ta in db.Accounts.AsNoTracking() on t.ToAccountId equals ta.Id into tag
        from ta in tag.DefaultIfEmpty()
        join pm in db.PaymentModes.AsNoTracking() on t.PaymentModeId equals pm.Id into pmg
        from pm in pmg.DefaultIfEmpty()
        select new Row { t = t, UserName = u.FullName, CategoryName = c.Name, AccountName = a.Name, FromName = fa.Name, ToName = ta.Name, ModeName = pm.Name };

    private IQueryable<Row> Filter(TransactionQuery q, bool ownOnly)
    {
        var rows = Joined().Where(x => x.t.FundId == q.FundId);
        if (ownOnly)
        {
            rows = rows.Where(x => x.t.CreatedBy == caller.UserId);
        }

        if (q.From is { } from)
        {
            rows = rows.Where(x => x.t.TxnDate >= from);
        }

        if (q.To is { } to)
        {
            rows = rows.Where(x => x.t.TxnDate <= to);
        }

        if (q.Type is { } type)
        {
            rows = rows.Where(x => x.t.TxnType == type);
        }

        if (q.CategoryId is { } cat)
        {
            rows = rows.Where(x => x.t.CategoryId == cat);
        }

        if (q.AccountId is { } acc)
        {
            rows = rows.Where(x => x.t.AccountId == acc || x.t.FromAccountId == acc || x.t.ToAccountId == acc);
        }

        if (q.UserId is { } user)
        {
            rows = rows.Where(x => x.t.CreatedBy == user);
        }

        if (q.PaymentModeId is { } mode)
        {
            rows = rows.Where(x => x.t.PaymentModeId == mode);
        }

        if (q.Status is { } status)
        {
            rows = rows.Where(x => x.t.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var like = $"%{q.Q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            rows = rows.Where(x => EF.Functions.ILike(x.t.TxnNumber, like) || EF.Functions.ILike(x.t.Purpose ?? "", like)
                || EF.Functions.ILike(x.t.ReferenceNumber ?? "", like) || EF.Functions.ILike(x.t.ReceivedFrom ?? "", like)
                || EF.Functions.ILike(x.t.PaidTo ?? "", like) || EF.Functions.ILike(x.t.Remarks ?? "", like)
                || EF.Functions.ILike(x.CategoryName ?? "", like) || EF.Functions.ILike(x.UserName, like));
        }

        return rows;
    }

    private static async Task<LedgerTotals> TotalsAsync(IQueryable<Row> activeRows, CancellationToken ct)
    {
        var sums = await activeRows.Where(x => x.t.TxnType == TxnType.Deposit || x.t.TxnType == TxnType.Expense)
            .GroupBy(x => x.t.TxnType).Select(g => new { Type = g.Key, Sum = g.Sum(x => x.t.Amount) }).ToListAsync(ct).ConfigureAwait(false);
        var money = sums.FirstOrDefault(s => s.Type == TxnType.Deposit)?.Sum ?? 0m;
        var spent = sums.FirstOrDefault(s => s.Type == TxnType.Expense)?.Sum ?? 0m;
        return new LedgerTotals(M(money), M(spent), M(money - spent));
    }

    private async Task<TransactionDto> GetDtoAsync(Guid id, CancellationToken ct) =>
        ToDto(await Joined().Where(x => x.t.Id == id).SingleAsync(ct).ConfigureAwait(false));

    private static TransactionDto ToDto(Row x)
    {
        var t = x.t;
        return new TransactionDto(t.Id, t.TxnNumber, t.TxnType, t.Status, M(t.Amount), t.FundId, t.TxnDate,
            t.TxnTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            Ref(t.CategoryId, x.CategoryName), Ref(t.AccountId, x.AccountName), Ref(t.FromAccountId, x.FromName), Ref(t.ToAccountId, x.ToName),
            Ref(t.PaymentModeId, x.ModeName), t.AdjustmentDirection, t.ReceivedFrom, t.PaidTo, t.Purpose, t.ReferenceNumber, t.Remarks,
            new PersonRef(t.CreatedBy, x.UserName), t.CreatedAt, t.Revision, t.Source);
    }

    private static NamedRef? Ref(Guid? id, string? name) => id is { } i && name is not null ? new NamedRef(i, name) : null;

    private async Task<bool> OwnOnlyAsync(Guid fundId, CancellationToken ct)
    {
        if (caller.IsAdmin)
        {
            return false;
        }

        var all = await db.UserFundAccess.AsNoTracking().Where(a => a.UserId == caller.UserId && a.FundId == fundId)
            .Select(a => a.CanViewAllTxns).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return !all;
    }

    private async Task<int> NextNumberAsync(Fund fund, string period, CancellationToken ct)
    {
        // Row-locked, gap-free per (fund, period); rolls back with the transaction (TRD §7.3).
        var next = await db.Database.SqlQuery<int>($"""
            INSERT INTO fl.txn_number_sequences (organization_id, fund_id, period_key, last_value)
            VALUES ({caller.OrganizationId}, {fund.Id}, {period}, 1)
            ON CONFLICT (fund_id, period_key) DO UPDATE SET last_value = fl.txn_number_sequences.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(ct).ConfigureAwait(false);
        return next.Single();
    }

    private async Task WriteRevisionAsync(TransactionDto dto, string? reason, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Serialize(dto, Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO fl.transaction_revisions (id, organization_id, transaction_id, revision, snapshot, change_reason, changed_by, changed_at)
            VALUES ({Guid.CreateVersion7()}, {caller.OrganizationId}, {dto.Id}, {dto.Revision}, {snapshot}::jsonb, {reason}, {caller.UserId}, {clock.GetUtcNow()})
            """, ct).ConfigureAwait(false);
    }

    private async Task RequireCategoryAsync(Guid id, CategoryDirection direction, Guid fundId, CancellationToken ct)
    {
        var c = await db.Categories.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.IsActive, x.Direction, x.FundId }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (c is null || (c.FundId is not null && c.FundId != fundId))
        {
            throw ValidationFailedException.For("categoryId", "Choose a category that is available for this fund.");
        }

        if (c.Direction != direction)
        {
            throw new DomainException("CATEGORY_DIRECTION_MISMATCH", "This category can't be used for this type of entry.");
        }

        if (!c.IsActive)
        {
            throw new DomainException("CATEGORY_INACTIVE", "This category was turned off by an Admin. Choose another.");
        }
    }

    private async Task RequireAccountAsync(Guid id, string field, CancellationToken ct)
    {
        var a = await db.Accounts.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.IsActive }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (a is null)
        {
            throw ValidationFailedException.For(field, "Choose an account.");
        }

        if (!a.IsActive)
        {
            throw new DomainException("ACCOUNT_INACTIVE", "This account was turned off by an Admin. Choose another.");
        }
    }

    private async Task RequirePaymentModeAsync(Guid id, string? reference, CancellationToken ct)
    {
        var m = await db.PaymentModes.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.IsActive, x.RequiresReference, x.Name }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (m is null)
        {
            throw ValidationFailedException.For("paymentModeId", "Choose a payment mode.");
        }

        if (!m.IsActive)
        {
            throw new DomainException("PAYMENT_MODE_INACTIVE", "This payment mode was turned off by an Admin. Choose another.");
        }

        if (m.RequiresReference && string.IsNullOrWhiteSpace(reference))
        {
            throw ValidationFailedException.For("referenceNumber", $"{m.Name} needs a reference number.");
        }
    }

    /// <summary>Today's date and the current local time in the organization's timezone (never the device's).</summary>
    private async Task<(DateOnly Today, DateTime Now)> OrgNowAsync(CancellationToken ct)
    {
        var tzId = await db.Organizations.AsNoTracking().Select(o => o.Timezone).SingleAsync(ct).ConfigureAwait(false);
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
        }

        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).DateTime;
        return (DateOnly.FromDateTime(local), local);
    }

    /// <summary>Indian financial year (Apr–Mar), the default numbering period (decision Q-08).</summary>
    internal static string PeriodKey(DateOnly date)
    {
        var start = date.Month >= 4 ? date.Year : date.Year - 1;
        return string.Create(CultureInfo.InvariantCulture, $"{start}-{(start + 1) % 100:D2}");
    }

    private async Task<string> ClosingAsync(Guid fundId, CancellationToken ct) =>
        M((await balances.GetFundBalanceAsync(fundId, ct).ConfigureAwait(false)).Closing);

    private static string M(decimal v) => Money.Of(v).ToString();

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"o:{offset}")));

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    /// <summary>A transaction with the display names the screens need, produced by one joined query (no N+1).</summary>
    private sealed class Row
    {
        public Transaction t { get; init; } = null!;

        public string UserName { get; init; } = string.Empty;

        public string? CategoryName { get; init; }

        public string? AccountName { get; init; }

        public string? FromName { get; init; }

        public string? ToName { get; init; }

        public string? ModeName { get; init; }
    }
}
