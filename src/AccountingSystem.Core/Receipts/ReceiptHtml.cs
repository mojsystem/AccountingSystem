using System.Globalization;
using System.Net;
using System.Text;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Receipts;

/// <summary>اطلاعات لازم برای چاپ رسید یک معامله.</summary>
public sealed record ReceiptData(TradeInfo Trade, string CurrencyName, int DecimalPlaces);

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
        var cashAmount = isBuy ? trade.IrrAmount - trade.FeeIrr : trade.IrrAmount + trade.FeeIrr;
        var cashLabel = isBuy ? "مبلغ پرداختی به مشتری (ریال)" : "مبلغ دریافتی از مشتری (ریال)";

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
        Row(sb, "ارز", $"{trade.CurrencyCode} - {data.CurrencyName}");
        Row(sb, "مقدار ارز", MoneyMath.FormatAmount(trade.Amount, decimals));
        Row(sb, "نرخ (ریال به ازای یک واحد)", MoneyMath.FormatRate(trade.Rate));
        Row(sb, "مبلغ ریالی معامله", MoneyMath.FormatAmount(trade.IrrAmount, 0) + " ریال");
        Row(sb, "کارمزد", MoneyMath.FormatAmount(trade.FeeIrr, 0) + " ریال");
        Row(sb, cashLabel, MoneyMath.FormatAmount(cashAmount, 0) + " ریال", "total");
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

    private static void Row(StringBuilder sb, string label, string value, string? cssClass = null)
    {
        var classes = cssClass is null ? "row" : "row " + cssClass;
        sb.Append("<div class=\"").Append(classes).Append("\"><span class=\"label\">")
          .Append(Enc(label)).Append("</span><span class=\"value\">")
          .Append(Enc(value)).Append("</span></div>\n");
    }

    private static string Enc(string text) => WebUtility.HtmlEncode(text);
}
