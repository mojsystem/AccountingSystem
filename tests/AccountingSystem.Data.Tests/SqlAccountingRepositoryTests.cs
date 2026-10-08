using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Services;
using Xunit;

namespace AccountingSystem.Data.Tests;

/// <summary>
/// تست‌های یکپارچگی روی SQL Server. هر تست ارز اختصاصی خودش را می‌سازد تا مستقل از بقیه باشد.
/// </summary>
public class SqlAccountingRepositoryTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlAccountingRepositoryTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Buy_then_sell_updates_cash_inventory_and_journal()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var tradeService = new CurrencyTradeService(repo);
        var reports = new ReportService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز آزمایشی", 2, now);
        await admin.SetRateAsync(user, code, 1_000_000m, 1_100_000m, now);

        var irrBefore = await IrrBalanceAsync(repo);
        await admin.OpeningIrrAsync(user, 100_000_000m, now);
        await admin.OpeningForeignAsync(user, code, 50m, 1_000_000m, now);

        var buyId = await tradeService.BuyFromCustomerAsync(new TradeInput(code, 10m, 1_000_000m, "مشتری تست", "0012345678", null), user, now);
        Assert.True(buyId > 0);

        var afterBuy = await repo.GetTradeSnapshotAsync(code);
        Assert.NotNull(afterBuy);
        Assert.Equal(60m, afterBuy!.ForeignBalance);
        Assert.Equal(60_000_000m, afterBuy.ForeignCostIrr);
        Assert.Equal(irrBefore + 100_000_000m - 10_000_000m, afterBuy.IrrBalance);

        var sellId = await tradeService.SellToCustomerAsync(new TradeInput(code, 40m, 1_200_000m, null, null, null), user, now);
        Assert.True(sellId > 0);

        var afterSell = await repo.GetTradeSnapshotAsync(code);
        Assert.NotNull(afterSell);
        Assert.Equal(20m, afterSell!.ForeignBalance);
        Assert.Equal(20_000_000m, afterSell.ForeignCostIrr);
        Assert.Equal(afterBuy.IrrBalance + 48_000_000m, afterSell.IrrBalance);

        var day = now.Date;
        var todayTrades = await reports.GetTradesAsync(day, day.AddDays(1));
        var sell = todayTrades.Single(t => t.Id == sellId);
        Assert.Equal(TradeType.Sell, sell.Type);
        Assert.Equal(40_000_000m, sell.CostIrr);
        Assert.Equal(8_000_000m, sell.ProfitIrr);

        var journal = await reports.GetJournalAsync(day, day.AddDays(1));
        var sellEntry = journal.Single(e =>
            e.Description.StartsWith("فروش", StringComparison.Ordinal)
            && e.Lines.Any(l => l.AccountCode == AccountCodes.ForeignCash(code)));
        Assert.Equal(sellEntry.Lines.Sum(l => l.Debit), sellEntry.Lines.Sum(l => l.Credit));
        Assert.Contains(sellEntry.Lines, l => l.AccountCode == AccountCodes.FxProfit && l.Credit == 8_000_000m);

        var dashboard = await reports.GetDashboardAsync(now);
        var position = dashboard.Positions.Single(p => p.CurrencyCode == code);
        Assert.Equal(20m, position.Quantity);
        Assert.Equal(20_000_000m, position.CostIrr);
    }

    [Fact]
    public async Task Selling_more_than_stock_is_rejected_before_posting()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var tradeService = new CurrencyTradeService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز موجودی کم", 2, now);
        await admin.OpeningIrrAsync(user, 50_000_000m, now);
        await admin.OpeningForeignAsync(user, code, 5m, 1_000_000m, now);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            tradeService.SellToCustomerAsync(new TradeInput(code, 6m, 1_000_000m, null, null, null), user, now));
    }

    [Fact]
    public async Task Stale_snapshot_is_rejected_by_compare_and_set()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var tradeService = new CurrencyTradeService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز همزمانی", 2, now);
        await admin.OpeningIrrAsync(user, 50_000_000m, now);
        await admin.OpeningForeignAsync(user, code, 10m, 1_000_000m, now);

        // نسخه‌ای از وضعیت که یک کاربر دیگر قبل از ثبت، آن را تغییر داده است.
        var stale = await repo.GetTradeSnapshotAsync(code);
        Assert.NotNull(stale);
        await tradeService.SellToCustomerAsync(new TradeInput(code, 1m, 1_100_000m, null, null, null), user, now);

        var posting = TradePlanner.PlanSell(new TradeInput(code, 1m, 1_100_000m, null, null, null), stale!, user.Id, now);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo.PostAsync(posting));
    }

    [Fact]
    public async Task Duplicate_currency_code_is_reported_as_business_error()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var user = await EnsureAdminAsync(repo, DateTime.Now);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            admin.AddCurrencyAsync(user, "USD", "دلار تکراری", 2, DateTime.Now));
    }

    private static async Task<CurrentUser> EnsureAdminAsync(IAccountingRepository repo, DateTime now)
    {
        var users = new UserService(repo);
        if (await users.CountUsersAsync() == 0)
        {
            return await users.CreateFirstAdminAsync("admin", "مدیر آزمایشی", "Test#12345", now);
        }

        var account = await repo.GetUserByUsernameAsync("admin")
            ?? throw new InvalidOperationException("کاربر admin برای آزمون یافت نشد.");
        return new CurrentUser(account.Id, account.Username, account.FullName, account.Role);
    }

    private static async Task<string> NewCurrencyCodeAsync(IAccountingRepository repo)
    {
        var existing = (await repo.GetCurrenciesAsync())
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var candidate = "Q" + (char)('A' + Random.Shared.Next(26)) + (char)('A' + Random.Shared.Next(26));
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task<decimal> IrrBalanceAsync(IAccountingRepository repo)
    {
        var boxes = await repo.GetCashBoxesAsync();
        return boxes.Single(b => b.CurrencyCode == CurrencyCodes.Irr).Balance;
    }
}
