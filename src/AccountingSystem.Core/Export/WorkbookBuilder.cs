using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Export;

/// <summary>خروجی Excel گزارش‌ها. تاریخ‌ها به شمسی و مبلغ‌ها به‌صورت عدد نوشته می‌شوند.</summary>
public static class WorkbookBuilder
{
    public static byte[] Trades(IReadOnlyList<TradeInfo> trades)
    {
        var headers = new[]
        {
            "شماره معامله", "کد شعبه", "شعبه", "زمان (شمسی)", "نوع معامله", "ارز", "مقدار ارز", "نرخ (ریال)",
            "مبلغ ریالی", "کارمزد (ریال)", "بهای تمام‌شده (ریال)", "سود معامله (ریال)", "نام مشتری", "کد ملی / شناسه",
            "توضیحات", "ثبت‌کننده", "وضعیت", "زمان ابطال (شمسی)", "دلیل ابطال",
        };
        var rows = trades.Select(t => (IReadOnlyList<object?>)new object?[]
        {
            t.Id,
            t.BranchCode,
            t.BranchName,
            PersianDate.FormatDateTime(t.OccurredAt),
            t.Type == TradeType.Buy ? "خرید از مشتری" : "فروش به مشتری",
            t.CurrencyCode,
            t.Amount,
            t.Rate,
            t.IrrAmount,
            t.FeeIrr,
            t.CostIrr,
            t.ProfitIrr,
            t.CustomerName,
            t.NationalCode,
            t.Note,
            t.CreatedBy,
            t.IsVoided ? "باطل شده" : "فعال",
            t.VoidedAt.HasValue ? PersianDate.FormatDateTime(t.VoidedAt.Value) : null,
            t.VoidReason,
        }).ToList();
        return XlsxWriter.Build(new[] { new WorkbookSheet("معاملات", headers, rows) });
    }

    public static byte[] Journal(IReadOnlyList<JournalEntryInfo> entries)
    {
        var headers = new[]
        {
            "شماره سند", "تاریخ (شمسی)", "شعبه", "شرح سند", "منشأ سند", "ردیف", "کد حساب", "نام حساب", "بدهکار (ریال)", "بستانکار (ریال)",
        };
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines)
            {
                rows.Add(new object?[]
                {
                    entry.Id,
                    PersianDate.FormatDate(entry.OccurredAt),
                    entry.BranchName,
                    entry.Description,
                    entry.SourceType,
                    line.LineNo,
                    line.AccountCode,
                    line.AccountName,
                    line.Debit,
                    line.Credit,
                });
            }
        }
        return XlsxWriter.Build(new[] { new WorkbookSheet("اسناد", headers, rows) });
    }

    public static byte[] CashBoxes(IReadOnlyList<CashBoxInfo> boxes)
    {
        var headers = new[] { "شعبه", "صندوق", "ارز", "موجودی", "آخرین تغییر (شمسی)" };
        var rows = boxes.Select(b => (IReadOnlyList<object?>)new object?[]
        {
            b.BranchName,
            b.Name,
            b.CurrencyCode,
            b.Balance,
            PersianDate.FormatDateTime(b.UpdatedAt),
        }).ToList();
        return XlsxWriter.Build(new[] { new WorkbookSheet("صندوق‌ها", headers, rows) });
    }
}
