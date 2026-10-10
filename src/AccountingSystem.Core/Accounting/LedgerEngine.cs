using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;

namespace AccountingSystem.Core.Accounting;

/// <summary>نوع سندی که یک رویداد دفتر به آن تعلق دارد.</summary>
public enum LedgerDocKind
{
    Trade,
    Opening,
    Manual,
    CashTransaction,
}

/// <summary>
/// ارجاع به یک سند. Id = 0 یعنی سندی که همین ثبت آن را می‌سازد (شناسه هنگام ذخیره تعیین می‌شود).
/// </summary>
public readonly record struct DocRef(LedgerDocKind Kind, long Id);

public enum LedgerEventKind
{
    /// <summary>ورود ارز به موجودی (خرید یا موجودی افتتاحیه‌ی ارز). بهای ورودی ValueIrr است.</summary>
    Acquire,

    /// <summary>خروج ارز از موجودی (فروش). بهای تمام‌شده با میانگین موزون همان لحظه محاسبه می‌شود.</summary>
    Dispose,

    /// <summary>تغییر صرفاً صندوق ریال: افتتاحیه، سند دستی یا دریافت/پرداخت ریالی مستقل.</summary>
    CashOnly,
}

/// <summary>مقدار یک صندوق ارز: مقدار ارز و بهای ریالی موجودی آن.</summary>
public readonly record struct PoolBalance(decimal Quantity, decimal CostIrr);

/// <summary>نتیجه‌ی یک خروج ارز: بهای تمام‌شده و سود (یا زیان منفی) آن.</summary>
public readonly record struct DisposalResult(decimal CostIrr, decimal ProfitIrr);

/// <summary>
/// یک رویداد دفتر یک شعبه. ترتیب رویدادها با (زمان وقوع، Seq) است. Seq ترتیب ثبت را برای زمان‌های یکسان نگه می‌دارد.
/// فیلدهای StoredCostIrr و StoredProfitIrr مقدار ذخیره‌شده‌ی فعلی یک فروش است تا تغییر آن تشخیص داده شود.
/// </summary>
public sealed record LedgerEvent(
    LedgerDocKind DocKind,
    long DocId,
    LedgerEventKind Kind,
    string CurrencyCode,
    DateTime OccurredAt,
    long Seq,
    decimal Quantity,
    decimal ValueIrr,
    decimal FeeIrr,
    decimal IrrDelta,
    decimal StoredCostIrr,
    decimal StoredProfitIrr,
    int? SettlementLineNumber = null,
    int? BankAccountId = null)
{
    public DocRef Doc => new(DocKind, DocId);
}

/// <summary>
/// وضعیت کامل دفتر یک شعبه: همه‌ی رویدادهای فعال، اسناد فعال و مقادیر ذخیره‌شده برای کنترل سازگاری.
/// Version شمارنده‌ی تغییرات دفتر شعبه است و هنگام ثبت برای کنترل همزمانی استفاده می‌شود.
/// </summary>
public sealed record BranchLedger(
    int BranchId,
    long Version,
    IReadOnlyList<LedgerEvent> Events,
    IReadOnlySet<DocRef> ActiveDocs,
    decimal StoredIrrBalance,
    IReadOnlyDictionary<string, PoolBalance> StoredPools,
    IReadOnlyDictionary<int, PoolBalance>? StoredBankPools = null)
{
    /// <summary>
    /// دفتر ساختگی از روی یک عکس‌فوری صندوق. این وضعیت به‌عنوان یک «موجودی ابتدایی» بدون سند در نظر گرفته می‌شود
    /// و برای معامله‌هایی که تاریخچه‌ی کامل ندارند (مثلاً تست‌ها یا محاسبه‌ی یک معامله‌ی جدید بدون تاریخ گذشته) به کار می‌رود.
    /// </summary>
    public static BranchLedger FromSnapshot(TradeSnapshot snapshot)
    {
        var start = DateTime.MinValue;
        var events = new List<LedgerEvent>
        {
            new(LedgerDocKind.Opening, -1, LedgerEventKind.CashOnly, CurrencyCodes.Irr, start, long.MinValue, 0m, 0m, 0m, snapshot.IrrBalance, 0m, 0m),
            new(LedgerDocKind.Opening, -1, LedgerEventKind.Acquire, snapshot.Currency.Code, start, long.MinValue + 1, snapshot.ForeignBalance, snapshot.ForeignCostIrr, 0m, 0m, 0m, 0m),
        };
        var pools = new Dictionary<string, PoolBalance>(StringComparer.Ordinal)
        {
            [snapshot.Currency.Code] = new(snapshot.ForeignBalance, snapshot.ForeignCostIrr),
        };
        return new BranchLedger(snapshot.BranchId, 0, events, new HashSet<DocRef>(), snapshot.IrrBalance, pools);
    }

    /// <summary>دفتر خالی شعبه با موجودی ریال داده‌شده (برای موجودی افتتاحیه‌ی ریال).</summary>
    public static BranchLedger Empty(int branchId, decimal irrBalance)
    {
        var events = new List<LedgerEvent>
        {
            new(LedgerDocKind.Opening, -1, LedgerEventKind.CashOnly, CurrencyCodes.Irr, DateTime.MinValue, long.MinValue, 0m, 0m, 0m, irrBalance, 0m, 0m),
        };
        return new BranchLedger(branchId, 0, events, new HashSet<DocRef>(), irrBalance, new Dictionary<string, PoolBalance>());
    }
}

/// <summary>وضعیت دفتر بعد از اجرای همه‌ی رویدادها به ترتیب زمان.</summary>
public sealed class LedgerState
{
    internal LedgerState(
        decimal irrBalance,
        IReadOnlyDictionary<string, PoolBalance> pools,
        IReadOnlyDictionary<int, PoolBalance> bankPools,
        IReadOnlyDictionary<DocRef, DisposalResult> disposals,
        IReadOnlyDictionary<(DocRef Doc, int LineNumber), DisposalResult> settlementDisposals)
    {
        IrrBalance = irrBalance;
        Pools = pools;
        BankPools = bankPools;
        Disposals = disposals;
        SettlementDisposals = settlementDisposals;
    }

    public decimal IrrBalance { get; }

    public IReadOnlyDictionary<string, PoolBalance> Pools { get; }

    /// <summary>موجودی و بهای تمام‌شده‌ی هر حساب بانکی.</summary>
    public IReadOnlyDictionary<int, PoolBalance> BankPools { get; }

    /// <summary>بهای تمام‌شده و سود هر فروش، با کلید سند فروش.</summary>
    public IReadOnlyDictionary<DocRef, DisposalResult> Disposals { get; }

    /// <summary>بهای خروج ارزهای پرداختی در سطرهای تسویه‌ی معامله.</summary>
    public IReadOnlyDictionary<(DocRef Doc, int LineNumber), DisposalResult> SettlementDisposals { get; }

    public PoolBalance Pool(string currencyCode) =>
        Pools.TryGetValue(currencyCode, out var pool) ? pool : default;

    public PoolBalance BankPool(int bankAccountId) =>
        BankPools.TryGetValue(bankAccountId, out var pool) ? pool : default;
}

/// <summary>
/// موتور بازپخش دفتر. با اجرای همه‌ی رویدادها به ترتیب زمان، بهای میانگین موزون هر فروش و موجودی هر لحظه را
/// دوباره حساب می‌کند. اگر در هیچ لحظه‌ای ریال یا ارز منفی شود، خطای قواعد کسب‌وکار برمی‌گرداند.
/// این تابع خالص است (بدون دیتابیس) تا با تست واحد بررسی شود.
/// </summary>
public static class LedgerEngine
{
    public static LedgerState Replay(IEnumerable<LedgerEvent> events)
    {
        var ordered = events.OrderBy(e => e.OccurredAt).ThenBy(e => e.Seq).ToList();
        var irr = 0m;
        var pools = new Dictionary<string, PoolBalance>(StringComparer.Ordinal);
        var bankPools = new Dictionary<int, PoolBalance>();
        var disposals = new Dictionary<DocRef, DisposalResult>();
        var settlementDisposals = new Dictionary<(DocRef Doc, int LineNumber), DisposalResult>();

        foreach (var e in ordered)
        {
            if (e.BankAccountId is { } bankAccountId)
            {
                var pool = GetBankPool(bankPools, bankAccountId);
                switch (e.Kind)
                {
                    case LedgerEventKind.Acquire:
                        bankPools[bankAccountId] = new PoolBalance(pool.Quantity + e.Quantity, pool.CostIrr + e.ValueIrr);
                        break;
                    case LedgerEventKind.Dispose:
                    {
                        if (pool.Quantity < e.Quantity)
                        {
                            throw Infeasible(e, $"موجودی حساب بانکی شماره {bankAccountId} برای این حواله کافی نیست");
                        }
                        var cost = pool.Quantity == e.Quantity
                            ? pool.CostIrr
                            : MoneyMath.RoundIrr(pool.CostIrr * e.Quantity / pool.Quantity);
                        bankPools[bankAccountId] = new PoolBalance(pool.Quantity - e.Quantity, pool.CostIrr - cost);
                        var disposal = new DisposalResult(cost, e.ValueIrr - cost);
                        if (e.SettlementLineNumber is { } bankLineNumber)
                        {
                            settlementDisposals[(e.Doc, bankLineNumber)] = disposal;
                        }
                        else
                        {
                            disposals[e.Doc] = disposal;
                        }
                        break;
                    }
                }
            }
            else
            {
                switch (e.Kind)
                {
                    case LedgerEventKind.Acquire:
                    {
                        var pool = GetPool(pools, e.CurrencyCode);
                        pools[e.CurrencyCode] = new PoolBalance(pool.Quantity + e.Quantity, pool.CostIrr + e.ValueIrr);
                        break;
                    }

                    case LedgerEventKind.Dispose:
                    {
                        var pool = GetPool(pools, e.CurrencyCode);
                        if (pool.Quantity < e.Quantity)
                        {
                            throw Infeasible(e, $"موجودی {e.CurrencyCode} برای این فروش کافی نیست");
                        }

                        // فروش کل موجودی، دقیقاً کل بهای ثبت‌شده را خارج می‌کند تا باقی‌مانده‌ی بهای صفر شود.
                        var cost = pool.Quantity == e.Quantity
                            ? pool.CostIrr
                            : MoneyMath.RoundIrr(pool.CostIrr * e.Quantity / pool.Quantity);
                        pools[e.CurrencyCode] = new PoolBalance(pool.Quantity - e.Quantity, pool.CostIrr - cost);
                        var disposal = new DisposalResult(cost, e.ValueIrr - cost);
                        if (e.SettlementLineNumber is { } lineNumber)
                        {
                            settlementDisposals[(e.Doc, lineNumber)] = disposal;
                        }
                        else
                        {
                            disposals[e.Doc] = disposal;
                        }
                        break;
                    }
                }
            }

            irr += e.IrrDelta;
            if (irr < 0)
            {
                throw Infeasible(e, "موجودی صندوق ریال کافی نیست");
            }
        }

        return new LedgerState(irr, pools, bankPools, disposals, settlementDisposals);
    }

    private static PoolBalance GetPool(Dictionary<string, PoolBalance> pools, string code) =>
        pools.TryGetValue(code, out var pool) ? pool : default;

    private static PoolBalance GetBankPool(Dictionary<int, PoolBalance> pools, int bankAccountId) =>
        pools.TryGetValue(bankAccountId, out var pool) ? pool : default;

    private static BusinessRuleException Infeasible(LedgerEvent e, string detail) =>
        new(
            $"{detail}. این مشکل در تاریخ {PersianDate.FormatDateTime(e.OccurredAt)} برای {DescribeDoc(e.Doc)} رخ می‌دهد. " +
            "ابتدا معاملات یا اسناد بعدی همین شعبه را اصلاح یا باطل کنید.");

    /// <summary>توضیح کوتاه یک سند برای پیام خطا.</summary>
    public static string DescribeDoc(DocRef doc) => doc.Kind switch
    {
        LedgerDocKind.Trade => doc.Id > 0 ? $"معامله‌ی شماره {doc.Id}" : "معامله‌ی جدید",
        LedgerDocKind.Opening => doc.Id > 0 ? $"موجودی افتتاحیه‌ی شماره {doc.Id}" : "موجودی افتتاحیه‌ی جدید",
        LedgerDocKind.CashTransaction => doc.Id > 0 ? $"رسید/پرداخت شماره {doc.Id}" : "رسید/پرداخت جدید",
        _ => doc.Id > 0 ? $"سند دستی شماره {doc.Id}" : "سند دستی جدید",
    };
}
