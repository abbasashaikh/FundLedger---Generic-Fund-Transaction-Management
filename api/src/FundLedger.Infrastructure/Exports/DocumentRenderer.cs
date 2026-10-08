using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using FundLedger.Application.Reports;
using FundLedger.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FundLedger.Infrastructure.Exports;

/// <summary>
/// Draws reports (CSV, Excel, PDF) and Money In receipts (PDF) from already-computed results (TRD §10.2, §10.3).
/// Amounts use Indian digit grouping in PDFs and an equivalent number format in Excel; spreadsheet cells that
/// could run as formulas are neutralised. PDFs print "Rs." rather than the rupee sign: the sign is missing from many
/// fonts and would render as an empty box.
/// </summary>
public sealed class DocumentRenderer : IDocumentRenderer
{
    // QuestPDF Community licence. Eligibility (small organizations and non-profits) must be confirmed by the owner;
    // see docs/05-Implementation-Plan.md, Phase 4 status.
    static DocumentRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // Names and purposes are often in Hindi, Gujarati or Urdu, but the bundled font (Lato) is Latin only. Use the
        // machine's fonts for those glyphs (the Docker image installs Noto), and never fail a whole report or receipt
        // over one character a font lacks: it prints as an empty box instead.
        QuestPDF.Settings.UseSystemFonts = true;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
    }

    private const string IndianMoneyFormat = "[>=10000000]##\\,##\\,##\\,##0.00;[>=100000]##\\,##\\,##0.00;##,##0.00";

    public byte[] Report(ReportResult report, ExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);
        return format switch
        {
            ExportFormat.Csv => Csv(report),
            ExportFormat.Xlsx => Xlsx(report),
            ExportFormat.Pdf => ReportPdf(report),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    // ---- CSV ----------------------------------------------------------------------------------
    private static byte[] Csv(ReportResult r)
    {
        var sb = new StringBuilder();
        sb.Append(CsvCell.Quote(r.OrganizationName)).Append("\r\n");
        sb.Append(CsvCell.Quote($"{r.Title} — {r.FundName}")).Append("\r\n");
        sb.Append(CsvCell.Quote($"{r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}{(r.OwnOnly ? " — My transactions only" : string.Empty)}")).Append("\r\n\r\n");
        foreach (var f in r.Summary)
        {
            sb.Append(CsvCell.Quote(f.Label)).Append(',').Append(f.Value).Append("\r\n");
        }

        sb.Append("\r\n").AppendJoin(',', r.Columns.Select(c => CsvCell.Quote(c.Label))).Append("\r\n");
        foreach (var row in r.Rows)
        {
            sb.AppendJoin(',', r.Columns.Select(c => Cell(c, row))).Append("\r\n");
        }

        if (r.Totals is { } totals)
        {
            sb.AppendJoin(',', r.Columns.Select(c => Cell(c, totals))).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];

        static string Cell(ReportColumn c, IReadOnlyDictionary<string, string?> row)
        {
            var v = row.GetValueOrDefault(c.Key);
            // Numbers are written bare (a negative money value starts with '-' but is not a formula).
            return c.Kind is ReportValueKind.Money or ReportValueKind.Number && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? v! : CsvCell.Quote(v);
        }
    }

    // ---- Excel --------------------------------------------------------------------------------
    private static byte[] Xlsx(ReportResult r)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(Truncate(r.Title, 31));
        var row = 1;
        ws.Cell(row, 1).SetValue(Text(r.OrganizationName)).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(++row, 1).SetValue(Text($"{r.Title} — {r.FundName}")).Style.Font.SetBold();
        ws.Cell(++row, 1).SetValue($"{r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}" + (r.OwnOnly ? " — My transactions only" : string.Empty));
        row++;
        foreach (var f in r.Summary)
        {
            ws.Cell(++row, 1).SetValue(Text(f.Label));
            Write(ws.Cell(row, 2), f.Kind, f.Value);
            ws.Cell(row, 2).Style.Font.SetBold();
        }

        row += 2;
        for (var i = 0; i < r.Columns.Count; i++)
        {
            var h = ws.Cell(row, i + 1);
            h.SetValue(Text(r.Columns[i].Label));
            h.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E8EEF0"));
            h.Style.Alignment.Horizontal = r.Columns[i].Kind is ReportValueKind.Money or ReportValueKind.Number ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
        }

        var firstData = row + 1;
        foreach (var data in r.Rows.Append(r.Totals!).Where(x => x is not null))
        {
            row++;
            for (var i = 0; i < r.Columns.Count; i++)
            {
                Write(ws.Cell(row, i + 1), r.Columns[i].Kind, data.GetValueOrDefault(r.Columns[i].Key));
            }

            if (ReferenceEquals(data, r.Totals))
            {
                ws.Range(row, 1, row, r.Columns.Count).Style.Font.SetBold();
            }
        }

        ws.SheetView.FreezeRows(firstData - 1);
        ws.Columns().AdjustToContents(1, row, 8, 60);
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();

        static void Write(IXLCell cell, ReportValueKind kind, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (kind is ReportValueKind.Money or ReportValueKind.Number && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
            {
                cell.SetValue(n);
                cell.Style.NumberFormat.Format = kind == ReportValueKind.Money ? IndianMoneyFormat : "0.##";
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            else if (kind == ReportValueKind.Date && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                cell.SetValue(d.ToDateTime(TimeOnly.MinValue));
                cell.Style.DateFormat.Format = "dd-mmm-yyyy";
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            }
            else
            {
                cell.SetValue(Text(value));
            }
        }

        static string Text(string v) => CsvCell.Neutralise(v);
        static string Truncate(string v, int max) => v.Length <= max ? v : v[..max];
    }

    // ---- Report PDF ---------------------------------------------------------------------------
    private static byte[] ReportPdf(ReportResult r) => ReportDocument(r).GeneratePdf();

    /// <summary>The report layout as a QuestPDF document (also used by the layout snapshot tests).</summary>
    internal static Document ReportDocument(ReportResult r) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(r.Columns.Count > 6 ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontSize(9));

            page.Header().Column(col =>
            {
                col.Item().Text(r.OrganizationName).Bold().FontSize(14);
                col.Item().Text($"{r.Title} — {r.FundName}").SemiBold().FontSize(11);
                col.Item().Text($"{r.From:dd-MMM-yyyy} to {r.To:dd-MMM-yyyy}" + (r.OwnOnly ? "  ·  My transactions only" : string.Empty)).FontColor(Colors.Grey.Darken2);
                col.Item().PaddingTop(4).LineHorizontal(0.6f).LineColor(Colors.Grey.Medium);
            });

            page.Content().PaddingVertical(8).Column(col =>
            {
                col.Spacing(8);
                if (r.Summary.Count > 0)
                {
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); });
                        foreach (var f in r.Summary)
                        {
                            t.Cell().PaddingVertical(1.5f).Text(f.Label);
                            t.Cell().PaddingVertical(1.5f).AlignRight().Text(Show(f.Kind, f.Value)).SemiBold();
                        }
                    });
                }

                if (r.Rows.Count > 0)
                {
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            foreach (var column in r.Columns)
                            {
                                if (column.Key is "purpose" or "reason") c.RelativeColumn(3);
                                else if (column.Kind == ReportValueKind.Money) c.RelativeColumn(1.6f);
                                else c.RelativeColumn(2);
                            }
                        });
                        t.Header(h =>
                        {
                            foreach (var column in r.Columns)
                            {
                                var cell = h.Cell().Background(Colors.Grey.Lighten3).Padding(3);
                                (IsNumeric(column) ? cell.AlignRight() : cell).Text(column.Label).SemiBold();
                            }
                        });
                        foreach (var row in r.Rows.Append(r.Totals!).Where(x => x is not null))
                        {
                            var total = ReferenceEquals(row, r.Totals);
                            foreach (var column in r.Columns)
                            {
                                var cell = t.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                                var text = (IsNumeric(column) ? cell.AlignRight() : cell).Text(Show(column.Kind, row.GetValueOrDefault(column.Key)));
                                if (total) text.Bold();
                            }
                        }
                    });
                }
                else
                {
                    col.Item().Text("No entries for this period.").FontColor(Colors.Grey.Darken1);
                }

                if (r.Truncated)
                {
                    col.Item().Text("Only the first rows are shown. Narrow the period or filters to see the rest.").Italic();
                }
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{r.OrganizationName} · {r.FundName} · {r.From:dd-MMM-yyyy} to {r.To:dd-MMM-yyyy} · Generated by {r.GeneratedBy} at {Local(r.GeneratedAt)}")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
                row.AutoItem().Text(x =>
                {
                    x.DefaultTextStyle(s => s.FontSize(7));
                    x.Span("Page ").FontColor(Colors.Grey.Darken1);
                    x.CurrentPageNumber();
                    x.Span(" of ").FontColor(Colors.Grey.Darken1);
                    x.TotalPages();
                });
            });
        }));

    private static bool IsNumeric(ReportColumn c) => c.Kind is ReportValueKind.Money or ReportValueKind.Number;

    private static string Show(ReportValueKind kind, string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return kind switch
        {
            ReportValueKind.Money when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) => IndianNumberFormat.Format(m),
            ReportValueKind.Date when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) =>
                d.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    private static string Local(DateTimeOffset t) => t.ToOffset(TimeSpan.FromMinutes(330)).ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture) + " IST";

    // ---- Receipt PDF ----------------------------------------------------------------------------
    public byte[] Receipt(ReceiptData receipt) => ReceiptDocument(receipt).GeneratePdf();

    /// <summary>The receipt layout as a QuestPDF document (also used by the layout snapshot tests).</summary>
    internal static Document ReceiptDocument(ReceiptData receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var d = receipt;
        var amount = decimal.Parse(d.Amount, CultureInfo.InvariantCulture);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(26);
            page.DefaultTextStyle(t => t.FontSize(10));

            if (d.Cancelled)
            {
                page.Foreground().AlignCenter().AlignMiddle().Rotate(-30).Column(c =>
                {
                    c.Item().AlignCenter().Text("CANCELLED").FontSize(64).Bold().FontColor(Colors.Red.Medium.WithAlpha(110));
                    if (d.CancelledAt is { } at)
                    {
                        c.Item().AlignCenter().Text($"on {at.ToOffset(TimeSpan.FromMinutes(330)):dd-MMM-yyyy}").FontSize(14).FontColor(Colors.Red.Medium.WithAlpha(140));
                    }
                });
            }

            page.Header().Column(col =>
            {
                col.Item().AlignCenter().Text(d.OrganizationName).Bold().FontSize(15);
                if (!string.IsNullOrWhiteSpace(d.Address))
                {
                    col.Item().AlignCenter().Text(d.Address).FontSize(9).FontColor(Colors.Grey.Darken2);
                }

                if (!string.IsNullOrWhiteSpace(d.RegistrationNumber))
                {
                    col.Item().AlignCenter().Text($"Registration no. {d.RegistrationNumber}").FontSize(9).FontColor(Colors.Grey.Darken2);
                }

                col.Item().PaddingTop(6).AlignCenter().Text("RECEIPT").SemiBold().FontSize(13).LetterSpacing(0.15f);
                col.Item().PaddingTop(4).LineHorizontal(0.8f);
            });

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Spacing(8);
                col.Item().Row(r =>
                {
                    r.RelativeItem().Text(t => { t.Span("Receipt no.  ").FontColor(Colors.Grey.Darken1); t.Span(d.ReceiptNumber).SemiBold(); });
                    r.AutoItem().Text(t => { t.Span("Date  ").FontColor(Colors.Grey.Darken1); t.Span(d.Date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)).SemiBold(); });
                });
                if (d.Revision > 1)
                {
                    col.Item().Text($"Revised (rev {d.Revision})").Italic().FontColor(Colors.Orange.Darken3);
                }

                col.Item().Text(t => { t.Span("Received with thanks from  ").FontColor(Colors.Grey.Darken1); t.Span(string.IsNullOrWhiteSpace(d.ReceivedFrom) ? "—" : d.ReceivedFrom).SemiBold(); });

                col.Item().Background(Colors.Grey.Lighten4).Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(c =>
                {
                    c.Item().Text($"Rs. {IndianNumberFormat.Format(amount)}").Bold().FontSize(20);
                    c.Item().Text(d.AmountInWords).FontSize(9).Italic();
                });

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(90); c.RelativeColumn(); });
                    void Line(string label, string? value)
                    {
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            return;
                        }

                        t.Cell().PaddingVertical(2).Text(label).FontColor(Colors.Grey.Darken1);
                        t.Cell().PaddingVertical(2).Text(value);
                    }

                    Line("Fund", d.FundName);
                    Line("Category", d.Category);
                    Line("Purpose", d.Purpose);
                    Line("Payment mode", d.PaymentMode);
                    Line("Reference", d.Reference);
                    Line("Recorded by", d.RecordedBy);
                });
            });

            page.Footer().Column(col =>
            {
                col.Item().LineHorizontal(0.4f).LineColor(Colors.Grey.Medium);
                col.Item().PaddingTop(3).Text(d.FooterText).FontSize(8).FontColor(Colors.Grey.Darken1);
                col.Item().Row(r =>
                {
                    r.RelativeItem().Text($"Generated {Local(d.GeneratedAt)}").FontSize(7).FontColor(Colors.Grey.Darken1);
                    r.AutoItem().Text($"Verification code {d.VerificationCode}").FontSize(7).FontColor(Colors.Grey.Darken1);
                });
            });
        }));
    }
}
