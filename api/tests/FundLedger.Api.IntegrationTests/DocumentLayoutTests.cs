using FundLedger.Application.Reports;
using FundLedger.Infrastructure.Exports;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace FundLedger.Api.IntegrationTests;

/// <summary>
/// Layout snapshots for receipts and report PDFs (plan P4-05). Each document is rendered to PNG pages in
/// <c>TestResults/layout</c> for a human to look at, and a few structural facts are asserted: the receipt is one
/// A5 page, a long report spills onto numbered pages, and nothing throws for empty or very large content.
/// No database is needed.
/// </summary>
public sealed class DocumentLayoutTests
{
    private static readonly string Out = Path.Combine(AppContext.BaseDirectory, "layout");

    private static ReceiptData Receipt(bool cancelled = false, int revision = 1, string from = "Yusuf Bhai (Area 4)") => new(
        "Al Madad Trust", "12 Station Road, Mumbai 400001", "E-1234 (Mumbai)", "IJT26-2026-27-000123", new DateOnly(2026, 10, 8), from,
        "125000.50", FundLedger.Domain.IndianAmountWords.ToWords(125000.50m), "Ijtema 2026", "Collection", "Area collection, Saturday", "UPI", "UTR 4567 8910",
        "Asha Admin", "Computer-generated receipt. No signature required.", new DateTimeOffset(2026, 10, 9, 5, 0, 0, TimeSpan.Zero), "9F3A12CC", revision, cancelled,
        cancelled ? new DateTimeOffset(2026, 10, 9, 5, 0, 0, TimeSpan.Zero) : null, "Asia/Kolkata");

    private static ReportResult Report(int rows) => new(
        "MONEY_IN", "Money in", "Every money-in entry.", Guid.NewGuid(), "Ijtema 2026", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), false,
        new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero), "Asha Admin", "Al Madad Trust",
        [new ReportFigure("total", "Total money in", "12512345.50", ReportValueKind.Money), new ReportFigure("count", "Entries", rows.ToString(System.Globalization.CultureInfo.InvariantCulture), ReportValueKind.Number)],
        [new ReportColumn("date", "Date", ReportValueKind.Date), new ReportColumn("number", "Number", ReportValueKind.Text), new ReportColumn("purpose", "Purpose", ReportValueKind.Text),
         new ReportColumn("amount", "Amount", ReportValueKind.Money)],
        Enumerable.Range(1, rows).Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
        {
            ["date"] = "2026-10-08", ["number"] = $"IJT26-2026-27-{i:000000}", ["purpose"] = "Area collection with a longer description that wraps onto a second line", ["amount"] = (i * 1234.5m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        }).ToList(),
        new Dictionary<string, string?> { ["date"] = "Total", ["amount"] = "12512345.50" }, false);

    private static List<byte[]> Render(Document doc, string name)
    {
        Directory.CreateDirectory(Out);
        var pages = doc.GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 110 }).ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            File.WriteAllBytes(Path.Combine(Out, $"{name}-{i + 1}.png"), pages[i]);
        }

        return pages;
    }

    [Fact]
    public void A_receipt_is_one_a5_page_in_every_state()
    {
        Assert.Single(Render(DocumentRenderer.ReceiptDocument(Receipt()), "receipt"));
        Assert.Single(Render(DocumentRenderer.ReceiptDocument(Receipt(revision: 3)), "receipt-revised"));
        Assert.Single(Render(DocumentRenderer.ReceiptDocument(Receipt(cancelled: true, revision: 2)), "receipt-cancelled"));
    }

    [Fact]
    public void Names_in_other_scripts_render_without_throwing()
    {
        Assert.Single(Render(DocumentRenderer.ReceiptDocument(Receipt(from: "युसुफ भाई · محمد یوسف · યુસુફ")), "receipt-scripts"));
    }

    [Fact]
    public void A_long_report_spills_onto_further_pages_and_a_short_one_fits_on_one()
    {
        Assert.Single(Render(DocumentRenderer.ReportDocument(Report(8)), "report-short"));
        Assert.True(Render(DocumentRenderer.ReportDocument(Report(140)), "report-long").Count > 1);
    }

    [Fact]
    public void An_empty_report_still_renders()
    {
        var empty = Report(0) with { Rows = [], Totals = null };
        Assert.Single(Render(DocumentRenderer.ReportDocument(empty), "report-empty"));
    }
}
