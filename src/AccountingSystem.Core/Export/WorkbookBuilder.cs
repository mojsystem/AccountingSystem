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
            "مبلغ ریالی", "روش دریافت/پرداخت", "ساختار تسویه", "روش نرخ", "ارز مقابل", "نرخ جفت‌ارز", "تهاتر مانده (ریال)", "ریز دریافت/پرداخت",
            "کارمزد (ریال)", "بهای تمام‌شده (ریال)", "سود معامله (ریال)", "نام مشتری", "کد ملی / شناسه",
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
            PaymentMethodText(t.PaymentMethod),
            SettlementModeText(t.SettlementMode),
            t.RateMode == TradeRateMode.Direct ? "نرخ مستقیم جفت‌ارز" : "مشتق از نرخ ریالی روز",
            t.SettlementCurrencyCode,
            t.CrossRate > 0m ? t.CrossRate : null,
            t.CustomerOffsetIrr,
            SettlementSummary(t),
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

    private static string PaymentMethodText(TradePaymentMethod method) => method switch
    {
        TradePaymentMethod.Cash => "نقد",
        TradePaymentMethod.Credit => "نسیه",
        TradePaymentMethod.Cheque => "چک",
        TradePaymentMethod.Pos => "کارتخوان",
        TradePaymentMethod.BankTransfer => "حواله",
        _ => "نامشخص",
    };

    private static string SettlementModeText(TradeSettlementMode mode) => mode switch
    {
        TradeSettlementMode.Direct => "مستقیم",
        TradeSettlementMode.Split => "چندبخشی",
        TradeSettlementMode.CustomerAccount => "حساب مشتری",
        _ => "نامشخص",
    };

    private static string SettlementSummary(TradeInfo trade)
    {
        var lines = trade.Settlements ?? Array.Empty<TradeSettlementInfo>();
        var parts = lines.Select(line =>
            $"{(line.Direction == TradeSettlementDirection.Payment ? "پرداخت" : "دریافت")} {MoneyMath.FormatAmount(line.Amount, 4)} {line.CurrencyCode}" +
            (line.BankAccountName is { Length: > 0 } bankName ? $" · {bankName}" : string.Empty) +
            $" ({MoneyMath.FormatAmount(line.IrrAmount, 0)} IRR)");
        var result = string.Join("; ", parts);
        if (trade.CustomerOffsetIrr > 0m)
        {
            result += (result.Length == 0 ? string.Empty : "; ") + "تهاتر " + MoneyMath.FormatAmount(trade.CustomerOffsetIrr, 0) + " IRR";
        }
        if (result.Length == 0 && trade.SettlementMode == TradeSettlementMode.CustomerAccount)
        {
            result = "بدون وجه نقد؛ حساب مشتری";
        }
        return result;
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

    public static byte[] CustomerBalances(IReadOnlyList<CustomerBalanceReportRow> balances)
    {
        var headers = new[]
        {
            "کد شخص", "نام شخص", "مانده بدهکار (ریال)", "مانده بستانکار (ریال)", "وضعیت مانده",
        };
        var rows = balances.Select(customer => (IReadOnlyList<object?>)new object?[]
        {
            customer.CustomerCode,
            customer.FullName,
            customer.DebitBalanceIrr,
            customer.CreditBalanceIrr,
            customer.BalanceSide,
        }).ToList();
        return XlsxWriter.Build(new[] { new WorkbookSheet("مانده اشخاص", headers, rows) });
    }

    public static byte[] CustomerLedger(CustomerLedgerReport report)
    {
        var headers = new[]
        {
            "زمان (شمسی)", "شعبه", "شماره سند", "منشأ", "شرح", "حساب تفصیلی", "بدهکار (ریال)", "بستانکار (ریال)", "مانده (بدهکار + / بستانکار -)",
        };
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[]
            {
                "شخص", $"{report.Customer.FullName} · {report.Customer.CustomerCode}", null, null, null, null, null, null, null,
            },
            new object?[]
            {
                null, null, null, null, "مانده‌ی ابتدای دوره", null,
                Math.Max(0m, report.OpeningBalanceIrr), Math.Max(0m, -report.OpeningBalanceIrr), report.OpeningBalanceIrr,
            },
        };
        foreach (var line in report.Lines)
        {
            rows.Add(new object?[]
            {
                PersianDate.FormatDateTime(line.OccurredAt),
                line.BranchName,
                line.JournalEntryId,
                line.SourceType,
                line.IsVoided ? "[باطل‌شده] " + line.Description : line.Description,
                line.AccountCode,
                line.Debit,
                line.Credit,
                line.BalanceIrr,
            });
        }
        return XlsxWriter.Build(new[] { new WorkbookSheet("معین شخص", headers, rows) });
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
