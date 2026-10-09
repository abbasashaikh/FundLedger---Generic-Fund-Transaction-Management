using System.Globalization;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Ledger;
using FundLedger.Application.Settings;
using FundLedger.Domain;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Reports;

/// <summary>
/// Money In receipts (ADR-0007, TR-065..068). Authorization is exactly that of <c>GET /transactions/{id}</c>
/// (an entry the caller can't see is a 404). Receipts are generated on demand from the current revision and
/// never stored; every generation is audited.
/// </summary>
public sealed class ReceiptService(
    LedgerService ledger, IFundLedgerDb db, ICurrentUser caller, ISettingsProvider settings, IAuditWriter audit, IReceiptCodeSigner signer,
    IDocumentRenderer renderer, ReceiptRateLimiter limiter, TimeProvider clock)
{
    public async Task<ReceiptFile> GenerateAsync(Guid transactionId, CancellationToken ct)
    {
        var detail = await ledger.GetAsync(transactionId, ct).ConfigureAwait(false);   // 404 for anything the caller can't see
        limiter.Take(caller.UserId, clock.GetUtcNow());
        var t = detail.Transaction;
        var s = await settings.GetAsync(ct).ConfigureAwait(false);
        if (t.Type != TxnType.Deposit)
        {
            throw new DomainException("RECEIPT_NOT_AVAILABLE", "Receipts are only available for Money In entries.");
        }

        if (!s.ReceiptEnabled)
        {
            throw new DomainException("RECEIPT_NOT_AVAILABLE", "Receipts are turned off for this organization.");
        }

        var org = await db.Organizations.AsNoTracking().SingleAsync(ct).ConfigureAwait(false);
        var fundName = await db.Funds.AsNoTracking().Where(f => f.Id == t.FundId).Select(f => f.Name).SingleAsync(ct).ConfigureAwait(false);
        var amount = decimal.Parse(t.Amount, CultureInfo.InvariantCulture);

        var data = new ReceiptData(
            org.Name, org.Address, org.RegistrationNumber, t.TxnNumber, t.TxnDate, t.ReceivedFrom, t.Amount, IndianAmountWords.ToWords(amount), fundName,
            t.Category?.Name, t.Purpose, t.PaymentMode?.Name, t.ReferenceNumber, s.ReceiptShowRecordedBy ? t.CreatedBy.Name : null, s.ReceiptFooterText,
            clock.GetUtcNow(), signer.Sign(t.Id, t.Revision), t.Revision, t.Status == TxnStatus.Cancelled, t.CancelledAt, org.Timezone);

        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.ReceiptGenerated, AuditEntities.Transaction, t.Id.ToString(),
            NewValue: new { t.TxnNumber, revision = t.Revision }, FundId: t.FundId), ct).ConfigureAwait(false);

        return new ReceiptFile($"receipt-{t.TxnNumber}.pdf", renderer.Receipt(data));
    }
}
