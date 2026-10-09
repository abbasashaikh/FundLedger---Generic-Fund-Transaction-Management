using FundLedger.Domain.Ledger;

namespace FundLedger.Application.Reports;

public enum ExportFormat
{
    Csv,
    Xlsx,
    Pdf,
}

/// <summary>Everything a Money In receipt prints (TRD TR-066). Built by <see cref="ReceiptService"/>, drawn by an <see cref="IDocumentRenderer"/>.</summary>
public sealed record ReceiptData(
    string OrganizationName, string? Address, string? RegistrationNumber, string ReceiptNumber, DateOnly Date, string? ReceivedFrom,
    string Amount, string AmountInWords, string FundName, string? Category, string? Purpose, string? PaymentMode, string? Reference,
    string? RecordedBy, string FooterText, DateTimeOffset GeneratedAt, string VerificationCode, int Revision, bool Cancelled, DateTimeOffset? CancelledAt,
    string TimeZone);

/// <summary>Turns a computed report or a receipt into file bytes. The implementation lives in Infrastructure (QuestPDF, ClosedXML).</summary>
public interface IDocumentRenderer
{
    byte[] Report(ReportResult report, ExportFormat format);

    byte[] Receipt(ReceiptData receipt);
}

/// <summary>The 8-character receipt verification code: an HMAC of the transaction id and revision (TR-066).</summary>
public interface IReceiptCodeSigner
{
    string Sign(Guid transactionId, int revision);
}

/// <summary>Spreadsheet formula injection guard shared by every CSV/Excel writer (see <c>AuditService.Csv</c>).</summary>
public static class CsvCell
{
    public static string Neutralise(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    /// <summary>A quoted CSV cell; cells beginning with = + - @ are prefixed with an apostrophe.</summary>
    public static string Quote(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : "\"" + Neutralise(value).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

public sealed record ReceiptFile(string FileName, byte[] Content);

public static class TxnTypeLabels
{
    public static bool IsDeposit(TxnType t) => t == TxnType.Deposit;
}
