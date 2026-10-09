using System.Globalization;
using FluentValidation;
using FundLedger.Domain;
using FundLedger.Domain.Ledger;

namespace FundLedger.Application.Ledger;

/// <summary>A person reference with the display name (never the mobile number).</summary>
public sealed record PersonRef(Guid Id, string Name);

public sealed record NamedRef(Guid Id, string Name);

public sealed record TransactionDto(
    Guid Id, string TxnNumber, TxnType Type, TxnStatus Status, string Amount, Guid FundId,
    DateOnly TxnDate, string TxnTime, NamedRef? Category, NamedRef? Account, NamedRef? FromAccount, NamedRef? ToAccount,
    NamedRef? PaymentMode, AdjustmentDirection? AdjustmentDirection, string? ReceivedFrom, string? PaidTo, string? Purpose,
    string? ReferenceNumber, string? Remarks, PersonRef CreatedBy, DateTimeOffset CreatedAt, int Revision, string Source,
    PersonRef? UpdatedBy, DateTimeOffset? UpdatedAt, PersonRef? CancelledBy, DateTimeOffset? CancelledAt, string? CancellationReason);

/// <summary>
/// A transaction plus what the CALLER may do with it right now (App Flow §4.6). The server is the
/// authority: the PWA only mirrors these flags to show or hide buttons.
/// </summary>
/// <param name="EditableUntil">End of the Member self-edit window; null for Admins and for entries that can't be edited.</param>
/// <param name="EditRequiresReason">True for Admin edits (every Admin change is explained) and for edits after the window.</param>
public sealed record TransactionDetail(TransactionDto Transaction, bool CanEdit, bool CanCancel, DateTimeOffset? EditableUntil, bool EditRequiresReason);

/// <summary>
/// Full replacement of the editable fields (PUT). Type, fund, number, creator and client id never change.
/// Which fields apply depends on the entry's type; the rest must be null.
/// </summary>
public sealed record UpdateTransactionRequest(
    string Amount, DateOnly TxnDate, string TxnTime, Guid? CategoryId, Guid? AccountId, Guid? FromAccountId, Guid? ToAccountId,
    Guid? PaymentModeId, string? ReceivedFrom, string? PaidTo, string? Purpose, string? ReferenceNumber, string? Remarks,
    AdjustmentDirection? AdjustmentDirection, string? Reason);

public sealed record CancelTransactionRequest(string Reason);

/// <summary>An Admin-only correction (PRD §11). <see cref="Reason"/> is mandatory and is stored as the entry's purpose.</summary>
public sealed record CreateAdjustmentRequest(
    Guid FundId, string Amount, DateOnly TxnDate, string TxnTime, Guid AccountId, AdjustmentDirection Direction,
    string Reason, string? Remarks, Guid? ClientTxnId);

public sealed record FieldChange(string Field, string? Old, string? New);

public enum HistoryKind
{
    Created,
    Edited,
    Cancelled,
}

public sealed record HistoryEntry(int Revision, HistoryKind Kind, PersonRef ChangedBy, DateTimeOffset ChangedAt, string? Reason, IReadOnlyList<FieldChange> Changes);

/// <param name="Duplicate">True when this clientTxnId was already recorded and the existing entry is returned (idempotent retry).</param>
public sealed record TransactionResult(TransactionDto Transaction, string FundClosingBalance, bool Duplicate);

public sealed record CreateDepositRequest(
    Guid FundId, string Amount, DateOnly TxnDate, string TxnTime, Guid CategoryId, Guid AccountId, Guid PaymentModeId,
    string? ReceivedFrom, string Purpose, string? ReferenceNumber, string? Remarks, Guid? ClientTxnId);

public sealed record CreateExpenseRequest(
    Guid FundId, string Amount, DateOnly TxnDate, string TxnTime, Guid CategoryId, Guid AccountId, Guid PaymentModeId,
    string? PaidTo, string Purpose, string? ReferenceNumber, string? Remarks, Guid? ClientTxnId);

public sealed record CreateTransferRequest(
    Guid FundId, string Amount, DateOnly TxnDate, string TxnTime, Guid FromAccountId, Guid ToAccountId, Guid? PaymentModeId,
    string Purpose, string? ReferenceNumber, string? Remarks, Guid? ClientTxnId);

public enum TxnSort
{
    Newest,
    Oldest,
    Highest,
    Lowest,
}

public sealed record TransactionQuery(
    Guid FundId, DateOnly? From, DateOnly? To, TxnType? Type, Guid? CategoryId, Guid? AccountId, Guid? UserId,
    Guid? PaymentModeId, TxnStatus? Status, string? Q, TxnSort Sort, int? Limit, string? Cursor);

public sealed record LedgerTotals(string MoneyIn, string MoneyOut, string Net);

public sealed record TransactionPage(IReadOnlyList<TransactionDto> Items, string? NextCursor, LedgerTotals Totals, bool OwnOnly);

public sealed record AccountBalanceDto(Guid AccountId, string Name, string Kind, string Opening, string MoneyIn, string MoneyOut,
    string TransfersIn, string TransfersOut, string Adjustments, string Closing);

public sealed record DashboardDto(
    Guid FundId, string FundName, string FundStatus, string Balance, string MoneyIn, string MoneyOut, string TodayIn, string TodayOut,
    IReadOnlyList<AccountBalanceDto> Accounts, IReadOnlyList<TransactionDto> Recent, bool OwnOnly);

/// <summary>Shared field rules; business rules that need the database live in <see cref="LedgerService"/>.</summary>
internal static class TxnRules
{
    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static IRuleBuilderOptions<T, string> ValidAmount<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(a => decimal.TryParse(a, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
                       && v > 0 && decimal.Round(v, 2) == v && v <= Money.MaxTransactionAmount)
            .WithMessage("Enter an amount greater than zero with at most 2 decimals.");

    public static IRuleBuilderOptions<T, string> ValidTime<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(t => TryParseTime(t, out _)).WithMessage("Enter the time as HH:mm.");
}

public sealed class CreateDepositValidator : AbstractValidator<CreateDepositRequest>
{
    public CreateDepositValidator()
    {
        RuleFor(x => x.Amount).ValidAmount();
        RuleFor(x => x.TxnTime).ValidTime();
        RuleFor(x => x.Purpose).NotEmpty().MaximumLength(300);
        RuleFor(x => x.ReceivedFrom).MaximumLength(120);
        RuleFor(x => x.ReferenceNumber).MaximumLength(80);
    }
}

public sealed class CreateExpenseValidator : AbstractValidator<CreateExpenseRequest>
{
    public CreateExpenseValidator()
    {
        RuleFor(x => x.Amount).ValidAmount();
        RuleFor(x => x.TxnTime).ValidTime();
        RuleFor(x => x.Purpose).NotEmpty().MaximumLength(300);      // BR-008
        RuleFor(x => x.PaidTo).MaximumLength(120);
        RuleFor(x => x.ReferenceNumber).MaximumLength(80);
    }
}

public sealed class UpdateTransactionValidator : AbstractValidator<UpdateTransactionRequest>
{
    public UpdateTransactionValidator()
    {
        RuleFor(x => x.Amount).ValidAmount();
        RuleFor(x => x.TxnTime).ValidTime();
        RuleFor(x => x.Purpose).MaximumLength(300);
        RuleFor(x => x.ReceivedFrom).MaximumLength(120);
        RuleFor(x => x.PaidTo).MaximumLength(120);
        RuleFor(x => x.ReferenceNumber).MaximumLength(80);
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}

public sealed class CancelTransactionValidator : AbstractValidator<CancelTransactionRequest>
{
    public CancelTransactionValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).WithMessage("Give a reason of at least 5 characters.").MaximumLength(300);
}

public sealed class CreateAdjustmentValidator : AbstractValidator<CreateAdjustmentRequest>
{
    public CreateAdjustmentValidator()
    {
        RuleFor(x => x.Amount).ValidAmount();
        RuleFor(x => x.TxnTime).ValidTime();
        RuleFor(x => x.Direction).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).WithMessage("Explain the adjustment in at least 10 characters.").MaximumLength(300);
    }
}

public sealed class CreateTransferValidator : AbstractValidator<CreateTransferRequest>
{
    public CreateTransferValidator()
    {
        RuleFor(x => x.Amount).ValidAmount();
        RuleFor(x => x.TxnTime).ValidTime();
        RuleFor(x => x.Purpose).NotEmpty().MaximumLength(300);
        RuleFor(x => x.ReferenceNumber).MaximumLength(80);
        RuleFor(x => x.ToAccountId).NotEqual(x => x.FromAccountId).WithMessage("Choose two different accounts.");   // BR-010
    }
}
