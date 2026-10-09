using System.IO.Compression;
using System.Xml.Linq;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
using AccountingSystem.Core.Receipts;
using Xunit;

namespace AccountingSystem.Core.Tests;

public class ExportAndReceiptTests
{
    private static readonly DateTime Occurred = new(2026, 10, 9, 10, 30, 0);

    [Fact]
    public void Trades_workbook_is_a_valid_package_with_well_formed_parts()
    {
        var bytes = WorkbookBuilder.Trades(new[] { SampleTrade(1, "علی <&> \"تست\"") });

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("xl/workbook.xml", names);
        Assert.Contains("xl/_rels/workbook.xml.rels", names);
        Assert.Contains("xl/styles.xml", names);
        Assert.Contains("xl/worksheets/sheet1.xml", names);

        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            Assert.NotNull(document.Root);
        }

        using var sheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = sheet.Descendants(ns + "row").ToList();
        Assert.Equal(2, rows.Count);
        var dataText = string.Concat(rows[1].Descendants(ns + "t").Select(t => t.Value));
        Assert.Contains("علی <&> \"تست\"", dataText);
        // مبلغ ریالی نمونه (۱۰۰ × ۱٬۰۰۰٬۰۰۰) به‌صورت عدد ذخیره می‌شود، نه متن.
        Assert.Contains(rows[1].Descendants(ns + "v"), v => v.Value == "100000000");
    }

    [Fact]
    public void Journal_workbook_writes_one_row_per_line()
    {
        var entries = new[]
        {
            new JournalEntryInfo(5, Occurred, "خرید", SourceTypes.Trade, 1, "مرکزی", new[]
            {
                new JournalLineInfo(1, "1101-USD", "موجودی ارز", 100m, 0m),
                new JournalLineInfo(2, "1001", "صندوق ریال", 0m, 100m),
            }),
        };

        var bytes = WorkbookBuilder.Journal(entries);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var sheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal(3, XDocument.Load(sheetStream).Descendants(ns + "row").Count());
    }

    [Fact]
    public void Workbook_builder_rejects_an_empty_sheet_list()
    {
        Assert.Throws<ArgumentException>(() => XlsxWriter.Build(Array.Empty<WorkbookSheet>()));
    }

    [Fact]
    public void Receipt_escapes_user_text_and_shows_the_fee()
    {
        var trade = SampleTrade(42, "<script>alert(1)</script>") with { FeeIrr = 50_000m };

        var html = ReceiptHtml.Render(new ReceiptData(trade, "دلار آمریکا", 2));

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("MAIN-000042", html);
        Assert.Contains("50,000 ریال", html);
        Assert.DoesNotContain("این معامله باطل شده است", html);
    }

    [Fact]
    public void Voided_trade_receipt_is_marked_as_voided()
    {
        var trade = SampleTrade(43, "علی") with
        {
            IsVoided = true,
            VoidedAt = Occurred.AddHours(2),
            VoidedBy = "admin",
            VoidReason = "خطا در مبلغ",
        };

        var html = ReceiptHtml.Render(new ReceiptData(trade, "دلار آمریکا", 2));

        Assert.Contains("این معامله باطل شده است", html);
        Assert.Contains("خطا در مبلغ", html);
    }

    private static TradeInfo SampleTrade(long id, string customer) =>
        new(id, 1, "MAIN", "شعبه‌ی مرکزی", TradeType.Buy, "USD", 100m, 1_000_000m, 100_000_000m, 100_000_000m, 0m, 0m,
            customer, "0012345678", null, Occurred, "cashier", false, null, null, null, CustomerId: 1);
}
