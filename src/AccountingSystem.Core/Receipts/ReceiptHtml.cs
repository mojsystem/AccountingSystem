using System.Globalization;
using System.Net;
using System.Text;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Receipts;

/// <summary>اطلاعات لازم برای چاپ رسید یک معامله.</summary>
public sealed record ReceiptData(
    TradeInfo Trade,
    string CurrencyName,
    int DecimalPlaces,
    IReadOnlyDictionary<string, string>? CurrencyNames = null,
    IReadOnlyDictionary<string, int>? CurrencyDecimalPlaces = null);

/// <summary>
/// رسید معامله به‌صورت HTML (RTL و قابل چاپ). وب و ویندوز از همین خروجی استفاده می‌کنند.
/// همه‌ی متن‌های کاربر قبل از قرار گرفتن در HTML کدگذاری می‌شوند.
/// </summary>
public static class ReceiptHtml
{
    private const string Style = @"
body { font-family: Tahoma, Arial, sans-serif; background: #f4f5f7; margin: 0; color: #111; }
.receipt { max-width: 720px; margin: 24px auto; background: #fff; border: 1px solid #c9ced6; border-radius: 8px; padding: 24px 28px; }
h1 { font-size: 20px; margin: 0 0 4px; text-align: center; }
.sub { text-align: center; color: #555; margin: 0 0 18px; }
.row { display: flex; justify-content: space-between; gap: 16px; padding: 7px 0; border-bottom: 1px dashed #dde1e7; }
.row .label { color: #444; }
.row .value { font-weight: bold; text-align: left; direction: ltr; unicode-bidi: plaintext; }
.row.total { font-size: 17px; border-bottom: 2px solid #111; margin-top: 6px; }
.void { margin-top: 16px; padding: 10px 12px; border: 2px solid #b42318; color: #b42318; border-radius: 6px; font-weight: bold; }
.actions { text-align: center; margin-top: 20px; }
.actions button { padding: 8px 22px; font-size: 15px; cursor: pointer; }
.footer { text-align: center; color: #777; font-size: 12px; margin-top: 18px; }
@media print {
  body { background: #fff; }
  .receipt { margin: 0; border: none; border-radius: 0; max-width: none; padding: 0; }
  .actions { display: none; }
}";

    public static string Render(ReceiptData data)
    {
        var trade = data.Trade;
        var isBuy = trade.Type == TradeType.Buy;
        var decimals = data.DecimalPlaces;
        var receiptNumber = $"{trade.BranchCode}-{trade.Id:D6}";
        IReadOnlyList<TradeSettlementInfo> settlements = trade.Settlements ?? Array.Empty<TradeSettlementInfo>();
        if (trade.Settlements is null && trade.SettlementMode == TradeSettlementMode.Direct)
        {
            var legacyAmount = isBuy ? trade.IrrAmount - trade.FeeIrr : trade.IrrAmount + trade.FeeIrr;
            settlements = new[]
            {
                new TradeSettlementInfo(1,
                    isBuy ? TradeSettlementDirection.Payment : TradeSettlementDirection.Receipt,
                    CurrencyCodes.Irr, legacyAmount, 1m, legacyAmount, legacyAmount, 0m),
            };
        }

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"fa\" dir=\"rtl\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\" />\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        sb.Append("<title>").Append(Enc("رسید " + receiptNumber)).Append("</title>\n");
        sb.Append("<style>").Append(Style).Append("</style>\n</head>\n<body>\n<main class=\"receipt\">\n");
        sb.Append("<h1>رسید معامله ارزی</h1>\n");
        sb.Append("<p class=\"sub\">").Append(Enc(trade.BranchName)).Append("</p>\n");

        Row(sb, "شماره رسید", receiptNumber);
        Row(sb, "تاریخ و ساعت (شمسی)", PersianDate.FormatDateTime(trade.OccurredAt));
        Row(sb, "نوع معامله", isBuy ? "خرید ارز از مشتری" : "فروش ارز به مشتری");
        Row(sb, "ارز معامله", $"{trade.CurrencyCode} - {data.CurrencyName}");
        Row(sb, "مقدار ارز", MoneyMath.FormatAmount(trade.Amount, decimals));
        Row(sb, "نرخ پایه (ریال به ازای یک واحد)", MoneyMath.FormatRate(trade.Rate));
        Row(sb, "مبلغ ریالی معامله", MoneyMath.FormatAmount(trade.IrrAmount, 0) + " ریال");
        Row(sb, "کارمزد", MoneyMath.FormatAmount(trade.FeeIrr, 0) + " ریال");
        Row(sb, "روش تسویه", SettlementModeText(trade.SettlementMode));
        if (trade.SettlementMode == TradeSettlementMode.Direct)
        {
            var counterCode = trade.SettlementCurrencyCode ?? settlements.FirstOrDefault()?.CurrencyCode ?? CurrencyCodes.Irr;
            Row(sb, "ارز مقابل", CurrencyLabel(counterCode, data.CurrencyNames));
            Row(sb, "روش تعیین نرخ", trade.RateMode == TradeRateMode.Direct ? "نرخ مستقیم جفت‌ارز" : "محاسبه از نرخ‌های ریالی روز");
            if (trade.CrossRate > 0m)
            {
                Row(sb, "نرخ جفت‌ارز", trade.CrossRate.ToString("0.########", CultureInfo.InvariantCulture));
            }
        }
        foreach (var line in settlements)
        {
            var direction = line.Direction == TradeSettlementDirection.Payment ? "پرداخت" : "دریافت";
            var lineDecimals = line.CurrencyCode == CurrencyCodes.Irr
                ? 0
                : data.CurrencyDecimalPlaces is not null && data.CurrencyDecimalPlaces.TryGetValue(line.CurrencyCode, out var savedDecimals)
                    ? savedDecimals
                    : 4;
            var value = $"{MoneyMath.FormatAmount(line.Amount, lineDecimals)} {CurrencyLabel(line.CurrencyCode, data.CurrencyNames)} — ارزش {MoneyMath.FormatAmount(line.IrrAmount, 0)} ریال";
            Row(sb, $"{direction} ({line.LineNumber})", value);
        }
        if (trade.CustomerOffsetIrr > 0m)
        {
            Row(sb, "تهاتر مانده‌ی مشتری", MoneyMath.FormatAmount(trade.CustomerOffsetIrr, 0) + " ریال");
        }
        var dueIrr = isBuy ? trade.IrrAmount - trade.FeeIrr : trade.IrrAmount + trade.FeeIrr;
        var remainingIrr = dueIrr - trade.CustomerOffsetIrr - settlements.Sum(line => line.IrrAmount);
        if (remainingIrr != 0m)
        {
            var remainingLabel = remainingIrr > 0m
                ? isBuy ? "باقی‌مانده‌ی پرداخت به مشتری" : "باقی‌مانده‌ی دریافت از مشتری"
                : isBuy ? "دریافت اضافه از مشتری" : "پرداخت اضافه به مشتری";
            Row(sb, remainingLabel, MoneyMath.FormatAmount(Math.Abs(remainingIrr), 0) + " ریال", "total");
        }
        if (trade.SettlementMode == TradeSettlementMode.CustomerAccount && settlements.Count == 0)
        {
            Row(sb, "وضعیت وجه نقد", "وجهی جابه‌جا نشده؛ مبلغ روی حساب مشتری ثبت شده است.");
        }
        Row(sb, "نام مشتری", trade.CustomerName ?? "-");
        Row(sb, "کد ملی / شناسه", trade.NationalCode ?? "-");
        if (!string.IsNullOrEmpty(trade.Note))
        {
            Row(sb, "توضیحات", trade.Note);
        }
        Row(sb, "ثبت‌کننده", trade.CreatedBy);

        if (trade.IsVoided)
        {
            var voidedAt = trade.VoidedAt.HasValue ? PersianDate.FormatDateTime(trade.VoidedAt.Value) : "-";
            sb.Append("<div class=\"void\">این معامله باطل شده است.")
              .Append(" تاریخ ابطال: ").Append(Enc(voidedAt)).Append(".")
              .Append(" دلیل ابطال: ").Append(Enc(trade.VoidReason ?? "-")).Append(".</div>\n");
        }

        sb.Append("<div class=\"actions\"><button type=\"button\" onclick=\"window.print()\">چاپ رسید</button></div>\n");
        sb.Append("<p class=\"footer\">این رسید با سیستم حسابداری صرافی تولید شده است.</p>\n");
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string SettlementModeText(TradeSettlementMode mode) => mode switch
    {
        TradeSettlementMode.Direct => "تبادل مستقیم دو ارز",
        TradeSettlementMode.Split => "تسویه‌ی چندبخشی",
        TradeSettlementMode.CustomerAccount => "ثبت روی حساب مشتری",
        _ => "نامشخص",
    };

    private static string CurrencyLabel(string code, IReadOnlyDictionary<string, string>? names) =>
        names is not null && names.TryGetValue(code, out var name) ? $"{code} - {name}" : code;

    private static void Row(StringBuilder sb, string label, string value, string? cssClass = null)
    {
        var classes = cssClass is null ? "row" : "row " + cssClass;
        sb.Append("<div class=\"").Append(classes).Append("\"><span class=\"label\">")
          .Append(Enc(label)).Append("</span><span class=\"value\">")
          .Append(Enc(value)).Append("</span></div>\n");
    }

    private static string Enc(string text) => WebUtility.HtmlEncode(text);
}
