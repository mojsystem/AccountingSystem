using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
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
        var branchId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز آزمایشی", 2, now);
        await admin.SetRateAsync(user, branchId, code, 1_000_000m, 1_100_000m, now);

        var irrBefore = await IrrBalanceAsync(repo, branchId);
        await admin.OpeningIrrAsync(user, branchId, 100_000_000m, now);
        await admin.OpeningForeignAsync(user, branchId, code, 50m, 1_000_000m, now);

        var buyId = await tradeService.BuyFromCustomerAsync(new TradeInput(branchId, code, 10m, 1_000_000m, "مشتری تست", "0012345678", null), user, now);
        Assert.True(buyId > 0);

        var afterBuy = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.NotNull(afterBuy);
        Assert.Equal(60m, afterBuy!.ForeignBalance);
        Assert.Equal(60_000_000m, afterBuy.ForeignCostIrr);
        Assert.Equal(irrBefore + 100_000_000m - 10_000_000m, afterBuy.IrrBalance);

        var sellId = await tradeService.SellToCustomerAsync(new TradeInput(branchId, code, 40m, 1_200_000m, null, null, null), user, now);
        Assert.True(sellId > 0);

        var afterSell = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.NotNull(afterSell);
        Assert.Equal(20m, afterSell!.ForeignBalance);
        Assert.Equal(20_000_000m, afterSell.ForeignCostIrr);
        Assert.Equal(afterBuy.IrrBalance + 48_000_000m, afterSell.IrrBalance);

        var day = now.Date;
        var todayTrades = await reports.GetTradesAsync(user, branchId, day, day.AddDays(1));
        var sell = todayTrades.Single(t => t.Id == sellId);
        Assert.Equal(TradeType.Sell, sell.Type);
        Assert.Equal(40_000_000m, sell.CostIrr);
        Assert.Equal(8_000_000m, sell.ProfitIrr);

        var journal = await reports.GetJournalAsync(user, branchId, day, day.AddDays(1));
        var sellEntry = journal.Single(e =>
            e.Description.StartsWith("فروش", StringComparison.Ordinal)
            && e.Lines.Any(l => l.AccountCode == AccountCodes.ForeignCash(code)));
        Assert.Equal(sellEntry.Lines.Sum(l => l.Debit), sellEntry.Lines.Sum(l => l.Credit));
        Assert.Contains(sellEntry.Lines, l => l.AccountCode == AccountCodes.FxProfit && l.Credit == 8_000_000m);

        var dashboard = await reports.GetDashboardAsync(user, branchId, now);
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
        var branchId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز موجودی کم", 2, now);
        await admin.OpeningIrrAsync(user, branchId, 50_000_000m, now);
        await admin.OpeningForeignAsync(user, branchId, code, 5m, 1_000_000m, now);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            tradeService.SellToCustomerAsync(new TradeInput(branchId, code, 6m, 1_000_000m, null, null, null), user, now));
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
        var branchId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز همزمانی", 2, now);
        await admin.OpeningIrrAsync(user, branchId, 50_000_000m, now);
        await admin.OpeningForeignAsync(user, branchId, code, 10m, 1_000_000m, now);

        // نسخه‌ای از وضعیت که یک کاربر دیگر قبل از ثبت، آن را تغییر داده است.
        var stale = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.NotNull(stale);
        await tradeService.SellToCustomerAsync(new TradeInput(branchId, code, 1m, 1_100_000m, null, null, null), user, now);

        var posting = TradePlanner.PlanSell(new TradeInput(branchId, code, 1m, 1_100_000m, null, null, null), stale!, user.Id, now);
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

    [Fact]
    public async Task Fee_is_posted_to_fee_income_and_appears_on_the_receipt()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var tradeService = new CurrencyTradeService(repo);
        var reports = new ReportService(repo);
        var receipts = new ReceiptService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var branchId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز کارمزد", 2, now);
        await admin.OpeningIrrAsync(user, branchId, 50_000_000m, now);
        await admin.OpeningForeignAsync(user, branchId, code, 20m, 1_000_000m, now);

        var sellId = await tradeService.SellToCustomerAsync(
            new TradeInput(branchId, code, 5m, 1_200_000m, "مشتری کارمزد", null, "توضیح", FeeIrr: 150_000m), user, now);

        var day = now.Date;
        var trade = (await reports.GetTradesAsync(user, branchId, day, day.AddDays(1))).Single(t => t.Id == sellId);
        Assert.Equal(150_000m, trade.FeeIrr);
        Assert.Equal(6_000_000m, trade.IrrAmount);
        Assert.False(trade.IsVoided);

        var journal = await reports.GetJournalAsync(user, branchId, day, day.AddDays(1));
        var entry = journal.Single(e => e.Description.Contains(code, StringComparison.Ordinal) && e.Description.StartsWith("فروش", StringComparison.Ordinal));
        Assert.Contains(entry.Lines, l => l.AccountCode == AccountCodes.FeeIncome && l.Credit == 150_000m);
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));

        var html = await receipts.RenderTradeReceiptAsync(user, sellId);
        Assert.Contains("MAIN-", html);
        Assert.Contains("150,000 ریال", html);
    }

    [Fact]
    public async Task Voiding_only_the_latest_movement_restores_stock_cash_and_cost()
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
        var branchId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز ابطال", 2, now);
        await admin.OpeningIrrAsync(user, branchId, 100_000_000m, now);
        await admin.OpeningForeignAsync(user, branchId, code, 50m, 1_000_000m, now);
        var irrStart = (await repo.GetTradeSnapshotAsync(branchId, code))!.IrrBalance;

        var sellId = await tradeService.SellToCustomerAsync(new TradeInput(branchId, code, 10m, 1_200_000m, null, null, null), user, now);
        var buyId = await tradeService.BuyFromCustomerAsync(new TradeInput(branchId, code, 5m, 1_000_000m, null, null, null), user, now);

        // معامله‌ی قدیمی‌تر تا وقتی معامله‌ی جدیدتر همین ارز باطل نشده، قابل ابطال نیست.
        var notLatest = await Assert.ThrowsAsync<BusinessRuleException>(() => tradeService.VoidTradeAsync(user, sellId, "دلیل", now));
        Assert.Contains("آخرین", notLatest.Message);

        await tradeService.VoidTradeAsync(user, buyId, "خطا در مقدار", now);
        var afterBuyVoid = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.Equal(40m, afterBuyVoid!.ForeignBalance);
        Assert.Equal(40_000_000m, afterBuyVoid.ForeignCostIrr);

        await tradeService.VoidTradeAsync(user, sellId, "مشتری انصراف داد", now);
        var afterSellVoid = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.Equal(50m, afterSellVoid!.ForeignBalance);
        Assert.Equal(50_000_000m, afterSellVoid.ForeignCostIrr);
        Assert.Equal(irrStart, afterSellVoid.IrrBalance);

        await Assert.ThrowsAsync<BusinessRuleException>(() => tradeService.VoidTradeAsync(user, sellId, "دوباره", now));

        var day = now.Date;
        var trades = await reports.GetTradesAsync(user, branchId, day, day.AddDays(1));
        var voided = trades.Single(t => t.Id == sellId);
        Assert.True(voided.IsVoided);
        Assert.Equal("مشتری انصراف داد", voided.VoidReason);
        Assert.Equal(user.Username, voided.VoidedBy);

        var journal = await reports.GetJournalAsync(user, branchId, day, day.AddDays(1));
        Assert.Contains(journal, e => e.SourceType == SourceTypes.Void && e.Description.Contains("مشتری انصراف داد", StringComparison.Ordinal));

        var dashboard = await reports.GetDashboardAsync(user, branchId, now);
        Assert.Equal(50m, dashboard.Positions.Single(p => p.CurrencyCode == code).Quantity);
    }

    [Fact]
    public async Task Branches_keep_separate_stock_cash_and_rates()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var tradeService = new CurrencyTradeService(repo);
        var branches = new BranchService(repo);
        var reports = new ReportService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        var otherId = await branches.CreateBranchAsync(user, "B" + Random.Shared.Next(100_000, 999_999), "شعبه‌ی آزمایشی", now);
        await admin.AddCurrencyAsync(user, code, "ارز شعبه", 2, now);
        await admin.OpeningIrrAsync(user, mainId, 10_000_000m, now);
        await admin.OpeningForeignAsync(user, mainId, code, 10m, 1_000_000m, now);

        var mainSnapshot = await repo.GetTradeSnapshotAsync(mainId, code);
        var otherSnapshot = await repo.GetTradeSnapshotAsync(otherId, code);
        Assert.Equal(10m, mainSnapshot!.ForeignBalance);
        Assert.Equal(0m, otherSnapshot!.ForeignBalance);
        Assert.Equal(0m, otherSnapshot.IrrBalance);

        await admin.SetRateAsync(user, otherId, code, 900_000m, 950_000m, now);
        var otherRates = await admin.GetLatestRatesAsync(user, otherId);
        Assert.Contains(otherRates, r => r.CurrencyCode == code && r.BuyRateIrr == 900_000m);
        var mainRates = await repo.GetLatestRatesAsync(mainId);
        Assert.DoesNotContain(mainRates, r => r.CurrencyCode == code);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            tradeService.SellToCustomerAsync(new TradeInput(otherId, code, 1m, 1_000_000m, null, null, null), user, now));

        var dashboard = await reports.GetDashboardAsync(user, otherId, now);
        Assert.Equal(0m, dashboard.Positions.Single(p => p.CurrencyCode == code).Quantity);
    }

    [Fact]
    public async Task Cashier_is_limited_to_their_own_branch()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var tradeService = new CurrencyTradeService(repo);
        var reports = new ReportService(repo);
        var branches = new BranchService(repo);
        var users = new UserService(repo);
        var now = DateTime.Now;
        var admin = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var otherId = await branches.CreateBranchAsync(admin, "C" + Random.Shared.Next(100_000, 999_999), "شعبه‌ی دوم", now);

        var username = "cash" + Random.Shared.Next(100_000, 999_999);
        await users.CreateUserAsync(admin, username, "کاربر صندوق", "Test#12345", UserRole.Cashier, mainId, now);
        var account = await repo.GetUserByUsernameAsync(username)
            ?? throw new InvalidOperationException("کاربر آزمایشی ساخته نشد.");
        var cashier = new CurrentUser(account.Id, account.Username, account.FullName, account.Role, account.BranchId, account.BranchName);
        Assert.Equal(mainId, cashier.BranchId);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            tradeService.BuyFromCustomerAsync(new TradeInput(otherId, "USD", 1m, 1_000_000m, null, null, null), cashier, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => reports.GetDashboardAsync(cashier, otherId, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => tradeService.VoidTradeAsync(cashier, 1, "دلیل", now));

        var ownDashboard = await reports.GetDashboardAsync(cashier, null, now);
        Assert.NotNull(ownDashboard);
    }

    [Fact]
    public async Task Duplicate_branch_code_is_reported_as_business_error()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var branches = new BranchService(repo);
        var admin = await EnsureAdminAsync(repo, DateTime.Now);

        await Assert.ThrowsAsync<BusinessRuleException>(() => branches.CreateBranchAsync(admin, "MAIN", "تکراری", DateTime.Now));
    }

    [Fact]
    public async Task Trades_workbook_exports_the_posted_trades()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var reports = new ReportService(repo);
        var user = await EnsureAdminAsync(repo, DateTime.Now);
        var day = DateTime.Now.Date;

        var trades = await reports.GetTradesAsync(user, null, day.AddYears(-1), day.AddDays(1));
        var bytes = WorkbookBuilder.Trades(trades);

        Assert.True(bytes.Length > 100);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
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
        return new CurrentUser(account.Id, account.Username, account.FullName, account.Role, account.BranchId, account.BranchName);
    }

    private static async Task<int> MainBranchIdAsync(IAccountingRepository repo)
    {
        var branches = await repo.GetBranchesAsync();
        return branches.Single(b => b.Code == "MAIN").Id;
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

    private static async Task<decimal> IrrBalanceAsync(IAccountingRepository repo, int branchId)
    {
        var boxes = await repo.GetCashBoxesAsync(branchId);
        return boxes.Single(b => b.CurrencyCode == CurrencyCodes.Irr).Balance;
    }
}
