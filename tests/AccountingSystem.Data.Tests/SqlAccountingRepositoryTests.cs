using System.Globalization;
using AccountingSystem.Core.Abstractions;
using AccountingSystem.Core.Accounting;
using AccountingSystem.Core.Common;
using AccountingSystem.Core.Domain;
using AccountingSystem.Core.Export;
using AccountingSystem.Core.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AccountingSystem.Data.Tests;

/// <summary>
/// تست‌های یکپارچگی روی SQL Server. هر تست ارز اختصاصی خودش را می‌سازد تا مستقل از بقیه باشد.
/// </summary>
public class SqlAccountingRepositoryTests : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private const string SharedCustomerName = "مشتری آزمایشی";

    private readonly SqlServerFixture _fixture;
    private int _customerId;

    public SqlAccountingRepositoryTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>هر تست یک مشتری مشترک دارد تا هر معامله‌ی آزمایشی به مشتری ثبت‌شده وصل باشد.</summary>
    public async Task InitializeAsync()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }
        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = await EnsureAdminAsync(repo, DateTime.Now);
        _customerId = await new CustomerService(repo, new PermissionService(repo))
            .CreateAsync(admin, new CustomerInput(SharedCustomerName, null, null, null, null), DateTime.Now);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

        var buyId = await tradeService.BuyFromCustomerAsync(new TradeInput(branchId, code, 10m, 1_000_000m, "مشتری تست", "0012345678", null, CustomerId: _customerId), user, now);
        Assert.True(buyId > 0);

        var afterBuy = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.NotNull(afterBuy);
        Assert.Equal(60m, afterBuy!.ForeignBalance);
        Assert.Equal(60_000_000m, afterBuy.ForeignCostIrr);
        Assert.Equal(irrBefore + 100_000_000m - 10_000_000m, afterBuy.IrrBalance);

        var sellId = await tradeService.SellToCustomerAsync(new TradeInput(branchId, code, 40m, 1_200_000m, null, null, null, CustomerId: _customerId), user, now);
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
    public async Task Direct_split_customer_account_and_offset_settlements_persist_and_replay()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var trades = new CurrencyTradeService(repo);
        var receipts = new ReceiptService(repo);
        var reports = new ReportService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var branchId = await MainBranchIdAsync(repo);
        var tradedCode = await NewCurrencyCodeAsync(repo);
        string settlementCode;
        do
        {
            settlementCode = await NewCurrencyCodeAsync(repo);
        } while (settlementCode == tradedCode);

        await admin.AddCurrencyAsync(user, tradedCode, "ارز معامله‌ی مستقیم", 2, now);
        await admin.AddCurrencyAsync(user, settlementCode, "ارز تسویه‌ی مستقیم", 2, now);
        await admin.SetRateAsync(user, branchId, tradedCode, 1_000_000m, 1_100_000m, now);
        await admin.SetRateAsync(user, branchId, settlementCode, 500_000m, 550_000m, now);
        await admin.OpeningIrrAsync(user, branchId, 100_000_000m, now);
        await admin.OpeningForeignAsync(user, branchId, settlementCode, 100m, 500_000m, now);

        var directId = await trades.BuyFromCustomerAsync(new TradeInput(
            branchId, tradedCode, 10m, 0m, null, null, null,
            CustomerId: _customerId,
            SettlementMode: TradeSettlementMode.Direct,
            RateMode: TradeRateMode.Direct,
            SettlementCurrencyCode: settlementCode,
            CrossRate: 2m), user, now);
        var direct = await repo.GetTradeAsync(directId);
        Assert.NotNull(direct);
        Assert.Equal(TradeSettlementMode.Direct, direct!.SettlementMode);
        Assert.Equal(TradeRateMode.Direct, direct.RateMode);
        Assert.Equal(settlementCode, direct.SettlementCurrencyCode);
        Assert.Equal(2m, direct.CrossRate);
        var directLine = Assert.Single(direct.Settlements!);
        Assert.Equal(20m, directLine.Amount);
        Assert.Equal(550_000m, directLine.RateIrr);
        Assert.Equal(11_000_000m, directLine.IrrAmount);
        Assert.Equal(10_000_000m, directLine.CostIrr);
        Assert.Equal(1_000_000m, directLine.ProfitIrr);

        var directSnapshot = await repo.GetTradeSnapshotAsync(branchId, tradedCode);
        var settlementSnapshot = await repo.GetTradeSnapshotAsync(branchId, settlementCode);
        Assert.Equal(10m, directSnapshot!.ForeignBalance);
        Assert.Equal(80m, settlementSnapshot!.ForeignBalance);
        Assert.Equal(100_000_000m, directSnapshot.IrrBalance);

        var derivedId = await trades.BuyFromCustomerAsync(new TradeInput(
            branchId, tradedCode, 5.5m, 0m, null, null, null,
            CustomerId: _customerId,
            SettlementMode: TradeSettlementMode.Direct,
            RateMode: TradeRateMode.Derived,
            SettlementCurrencyCode: settlementCode), user, now);
        var derived = await repo.GetTradeAsync(derivedId);
        Assert.NotNull(derived);
        Assert.Equal(TradeRateMode.Derived, derived!.RateMode);
        Assert.Equal(1.81818182m, derived.CrossRate);
        var derivedLine = Assert.Single(derived.Settlements!);
        Assert.Equal(10m, derivedLine.Amount);
        Assert.Equal(5_500_000m, derivedLine.IrrAmount);

        var splitId = await trades.SellToCustomerAsync(new TradeInput(
            branchId, tradedCode, 5m, 1_200_000m, null, null, null,
            CustomerId: _customerId,
            SettlementMode: TradeSettlementMode.Split,
            SettlementLines: new[]
            {
                new TradeSettlementInput(settlementCode, 5m),
                new TradeSettlementInput(CurrencyCodes.Irr, 3_500_000m),
            }), user, now);
        var split = await repo.GetTradeAsync(splitId);
        Assert.NotNull(split);
        var splitLines = split!.Settlements!;
        Assert.Equal(2, splitLines.Count);
        Assert.Equal(TradeSettlementDirection.Receipt, splitLines[0].Direction);
        Assert.Equal(2_500_000m, splitLines[0].IrrAmount);
        Assert.Equal(TradeSettlementDirection.Receipt, splitLines[1].Direction);
        Assert.Equal(3_500_000m, splitLines[1].IrrAmount);

        var accountSaleId = await trades.SellToCustomerAsync(new TradeInput(
            branchId, tradedCode, 1m, 1_200_000m, null, null, null,
            CustomerId: _customerId,
            SettlementMode: TradeSettlementMode.CustomerAccount), user, now);
        var afterSale = await repo.GetCustomerAccountBalanceAsync(branchId, _customerId, now);
        Assert.Equal(1_200_000m, afterSale.ReceivableIrr);
        Assert.Equal(0m, afterSale.PayableIrr);

        var offsetBuyId = await trades.BuyFromCustomerAsync(new TradeInput(
            branchId, tradedCode, 1m, 1_100_000m, null, null, null,
            CustomerId: _customerId,
            SettlementMode: TradeSettlementMode.CustomerAccount,
            ApplyCustomerOffset: true), user, now);
        var offsetBuy = await repo.GetTradeAsync(offsetBuyId);
        Assert.Equal(1_100_000m, offsetBuy!.CustomerOffsetIrr);
        Assert.Empty(offsetBuy.Settlements!);
        var afterOffset = await repo.GetCustomerAccountBalanceAsync(branchId, _customerId, now);
        Assert.Equal(100_000m, afterOffset.ReceivableIrr);
        Assert.Equal(0m, afterOffset.PayableIrr);

        var customerLineCount = Convert.ToInt32(await SqlTestDb.ScalarAsync(_fixture.ConnectionString!, @"
SELECT COUNT(*) FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
WHERE e.SourceType = N'TRADE' AND e.SourceId = @tradeId AND l.CustomerId = @customerId
  AND l.AccountCode IN (N'1201', N'2101');",
            ("@tradeId", offsetBuyId), ("@customerId", _customerId)), CultureInfo.InvariantCulture);
        Assert.True(customerLineCount >= 2);

        var html = await receipts.RenderTradeReceiptAsync(user, splitId);
        Assert.Contains(settlementCode, html);
        Assert.Contains("3,500,000 IRR", html);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, tradedCode);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, settlementCode);

        // ابطال باید در مانده‌ی اشخاص با سند معکوس خنثی شود، نه اینکه مانده را دوبار تغییر دهد.
        await trades.VoidTradeAsync(user, offsetBuyId, "ابطال آزمایش گزارش", now.AddMinutes(1));
        var afterVoid = await repo.GetCustomerAccountBalanceAsync(branchId, _customerId, now.AddMinutes(2));
        Assert.Equal(1_200_000m, afterVoid.ReceivableIrr);
        Assert.Equal(0m, afterVoid.PayableIrr);

        var balanceRows = await reports.GetCustomerBalancesAsync(user, branchId, now.AddDays(1));
        var customerBalance = Assert.Single(balanceRows, row => row.CustomerId == _customerId);
        Assert.Equal(1_200_000m, customerBalance.BalanceIrr);
        Assert.Equal(1_200_000m, customerBalance.DebitBalanceIrr);
        Assert.Equal(0m, customerBalance.CreditBalanceIrr);
        var ledger = await reports.GetCustomerLedgerAsync(user, branchId, _customerId, now.Date, now.Date.AddDays(1));
        Assert.Equal(0m, ledger.OpeningBalanceIrr);
        Assert.Equal(1_200_000m, ledger.ClosingBalanceIrr);
        Assert.Contains(ledger.Lines, line => line.SourceId == offsetBuyId && line.IsVoided);
        Assert.Contains(ledger.Lines, line => line.SourceType == SourceTypes.Void && line.SourceId == offsetBuyId);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, tradedCode);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, settlementCode);
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
            tradeService.SellToCustomerAsync(new TradeInput(branchId, code, 6m, 1_000_000m, null, null, null, CustomerId: _customerId), user, now));
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
        await tradeService.SellToCustomerAsync(new TradeInput(branchId, code, 1m, 1_100_000m, null, null, null, CustomerId: _customerId), user, now);

        var posting = TradePlanner.PlanSell(new TradeInput(branchId, code, 1m, 1_100_000m, null, null, null, CustomerId: _customerId), stale!, user.Id, now);
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
            new TradeInput(branchId, code, 5m, 1_200_000m, "مشتری کارمزد", null, "توضیح", FeeIrr: 150_000m, CustomerId: _customerId), user, now);

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
            tradeService.SellToCustomerAsync(new TradeInput(otherId, code, 1m, 1_000_000m, null, null, null, CustomerId: _customerId), user, now));

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
            tradeService.BuyFromCustomerAsync(new TradeInput(otherId, "USD", 1m, 1_000_000m, null, null, null, CustomerId: _customerId), cashier, now));
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

    [Fact]
    public async Task Database_uses_compatibility_level_150()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        await using var conn = new SqlConnection(_fixture.ConnectionString!);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID();", conn);
        var level = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(150, level);
    }

    [Fact]
    public async Task Voiding_an_older_trade_is_blocked_only_while_a_later_sale_depends_on_it()
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

        await admin.AddCurrencyAsync(user, code, "ارز ابطال میانی", 2, now);
        await admin.RecordOpeningAsync(user, branchId, CurrencyCodes.Irr, 500_000_000m, null, now, now.Date.AddDays(-5));
        await admin.RecordOpeningAsync(user, branchId, code, 100m, 1_000_000m, now, now.Date.AddDays(-5));
        var buyId = await tradeService.RecordTradeAsync(new TradeInput(branchId, code, 50m, 1_000_000m, null, null, null, CustomerId: _customerId), TradeType.Buy, user, now, now.Date.AddDays(-4));
        var saleId = await tradeService.RecordTradeAsync(new TradeInput(branchId, code, 120m, 1_200_000m, null, null, null, CustomerId: _customerId), TradeType.Sell, user, now, now.Date.AddDays(-3));

        // بدون خرید، ۱۰۰ واحد باقی می‌ماند و فروش ۱۲۰ واحدی با موجودی کافی انجام نمی‌شد.
        var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => tradeService.VoidTradeAsync(user, buyId, "خطا در مقدار", now));
        Assert.Contains("کافی نیست", blocked.Message);

        // فروش جدیدتر باطل شود؛ سپس خرید قدیمی‌تر دیگر وابستگی ندارد و قابل ابطال است.
        await tradeService.VoidTradeAsync(user, saleId, "مشتری انصراف داد", now);
        await tradeService.VoidTradeAsync(user, buyId, "خطا در مقدار", now);

        var snapshot = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.Equal(100m, snapshot!.ForeignBalance);
        Assert.Equal(100_000_000m, snapshot.ForeignCostIrr);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, code);

        var voided = await repo.GetTradeAsync(saleId);
        Assert.True(voided!.IsVoided);
        Assert.Equal("مشتری انصراف داد", voided.VoidReason);
        Assert.Equal(user.Username, voided.VoidedBy);
    }

    [Fact]
    public async Task Backdated_trade_recalculates_the_later_sale_and_keeps_the_books_balanced()
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

        await admin.AddCurrencyAsync(user, code, "ارز پس‌نگر", 2, now);
        await admin.RecordOpeningAsync(user, branchId, CurrencyCodes.Irr, 500_000_000m, null, now, now.Date.AddDays(-5));
        await admin.RecordOpeningAsync(user, branchId, code, 100m, 1_000_000m, now, now.Date.AddDays(-5));
        var saleId = await tradeService.RecordTradeAsync(new TradeInput(branchId, code, 50m, 1_200_000m, null, null, null, CustomerId: _customerId), TradeType.Sell, user, now, now.Date.AddDays(-2));
        var before = await repo.GetTradeAsync(saleId);
        Assert.Equal(50_000_000m, before!.CostIrr);

        // خرید ۱۰۰ واحدی با تاریخ سه روز قبل: بهای میانگین فروش دو روز قبل باید بازمحاسبه شود.
        await tradeService.RecordTradeAsync(new TradeInput(branchId, code, 100m, 1_500_000m, null, null, null, CustomerId: _customerId), TradeType.Buy, user, now, now.Date.AddDays(-3));

        var after = await repo.GetTradeAsync(saleId);
        Assert.Equal(62_500_000m, after!.CostIrr);
        Assert.Equal(-2_500_000m, after.ProfitIrr);

        var snapshot = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.Equal(150m, snapshot!.ForeignBalance);
        Assert.Equal(187_500_000m, snapshot.ForeignCostIrr);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, code);
    }

    [Fact]
    public async Task Trade_can_be_edited_and_keeps_the_old_version_voided()
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

        await admin.AddCurrencyAsync(user, code, "ارز ویرایش", 2, now);
        await admin.OpeningIrrAsync(user, branchId, 100_000_000m, now);
        await admin.OpeningForeignAsync(user, branchId, code, 50m, 1_000_000m, now);
        var buyId = await tradeService.BuyFromCustomerAsync(new TradeInput(branchId, code, 10m, 1_000_000m, null, null, null, CustomerId: _customerId), user, now);

        // تغییر اطلاعات توصیفی: همان سند به‌روز می‌شود و نسخه‌ی جدید ساخته نمی‌شود.
        var metadataOnly = await tradeService.EditTradeAsync(user, buyId, new TradeInput(branchId, code, 10m, 1_000_000m, "علی رضایی", "0012345678", "یادداشت", CustomerId: _customerId),
            TradeType.Buy, null, now);
        Assert.Null(metadataOnly);
        var same = await repo.GetTradeAsync(buyId);
        Assert.Equal(SharedCustomerName, same!.CustomerName);
        Assert.False(same.IsVoided);

        // تغییر مبلغ: نسخه‌ی قبلی باطل می‌شود و نسخه‌ی اصلاحی جای آن را می‌گیرد.
        var newId = await tradeService.EditTradeAsync(user, buyId, new TradeInput(branchId, code, 12m, 1_000_000m, "علی رضایی", null, null, CustomerId: _customerId),
            TradeType.Buy, null, now);
        Assert.NotNull(newId);
        var old = await repo.GetTradeAsync(buyId);
        Assert.True(old!.IsVoided);
        Assert.Equal(CurrencyTradeService.ReplacementReason, old.VoidReason);

        var snapshot = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.Equal(62m, snapshot!.ForeignBalance);
        Assert.Equal(62_000_000m, snapshot.ForeignCostIrr);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, code);

        await using var conn = new SqlConnection(_fixture.ConnectionString!);
        await conn.OpenAsync();
        var audits = Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.AuditLog WHERE EntityType = N'TRADE' AND Action = N'TRADE_REPLACE';"), CultureInfo.InvariantCulture);
        Assert.True(audits >= 1);
    }

    [Fact]
    public async Task Opening_balance_can_be_edited_and_voided_when_no_trade_depends_on_it()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var branchId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(user, code, "ارز افتتاحیه", 2, now);
        await admin.OpeningIrrAsync(user, branchId, 100_000_000m, now);
        var openingId = await admin.RecordOpeningAsync(user, branchId, code, 10m, 1_000_000m, now);
        Assert.NotNull(openingId);

        var replacedId = await admin.EditOpeningAsync(user, openingId!.Value, 20m, 1_000_000m, null, now);
        Assert.NotNull(replacedId);
        Assert.True((await repo.GetOpeningAsync(openingId.Value))!.IsVoided);
        Assert.Equal(20m, (await repo.GetTradeSnapshotAsync(branchId, code))!.ForeignBalance);

        await admin.VoidOpeningAsync(user, replacedId!.Value, "ورود اشتباه", now);
        var snapshot = await repo.GetTradeSnapshotAsync(branchId, code);
        Assert.Equal(0m, snapshot!.ForeignBalance);
        Assert.Equal(0m, snapshot.ForeignCostIrr);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, code);
    }

    [Fact]
    public async Task Manual_expense_moves_cash_and_can_be_edited_and_voided()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var manual = new ManualJournalService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var branchId = await MainBranchIdAsync(repo);

        await admin.OpeningIrrAsync(user, branchId, 100_000_000m, now);
        var irrBefore = await IrrBalanceAsync(repo, branchId);

        var lines = new[] { new JournalLineDraft("6001", 10_000_000m, 0m), new JournalLineDraft(AccountCodes.IrrCash, 0m, 10_000_000m) };
        var entryId = await manual.CreateAsync(user, branchId, "هزینه‌ی برق", null, lines, now);
        Assert.NotNull(entryId);
        Assert.Equal(irrBefore - 10_000_000m, await IrrBalanceAsync(repo, branchId));

        var edited = new[] { new JournalLineDraft("6001", 15_000_000m, 0m), new JournalLineDraft(AccountCodes.IrrCash, 0m, 15_000_000m) };
        var replacedId = await manual.EditAsync(user, entryId!.Value, "هزینه‌ی برق و آب", null, edited, now);
        Assert.NotNull(replacedId);
        Assert.Equal(irrBefore - 15_000_000m, await IrrBalanceAsync(repo, branchId));

        await manual.VoidAsync(user, replacedId!.Value, "ثبت اشتباه", now);
        Assert.Equal(irrBefore, await IrrBalanceAsync(repo, branchId));
        Assert.True((await repo.GetJournalEntryAsync(entryId.Value))!.IsVoided);
        await AssertLedgerConsistentAsync(_fixture.ConnectionString!, branchId, CurrencyCodes.Irr);
    }

    [Fact]
    public async Task Backdating_beyond_thirty_days_is_rejected()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var tradeService = new CurrencyTradeService(repo);
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var branchId = await MainBranchIdAsync(repo);

        await Assert.ThrowsAsync<BusinessRuleException>(() => tradeService.RecordTradeAsync(
            new TradeInput(branchId, "USD", 1m, 1_000_000m, null, null, null, CustomerId: _customerId), TradeType.Buy, user, now, now.Date.AddDays(-31)));
    }

    /// <summary>
    /// کنترل سازگاری دفتر یک شعبه و ارز: هر سند متوازن است، مانده‌ی هر صندوق با حرکت‌هایش برابر است،
    /// آخرین موجودی تراکمی با مانده برابر است و بهای موجودی در دفتر کل با جدول موجودی برابر است.
    /// </summary>
    [Fact]
    public async Task Every_branch_gets_the_three_default_roles_with_the_agreed_tasks()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var branches = new BranchService(repo);
        var now = DateTime.Now;
        var admin = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var newId = await branches.CreateBranchAsync(admin, "R" + Random.Shared.Next(100_000, 999_999), "شعبه‌ی نقش", now);

        foreach (var branchId in new[] { mainId, newId })
        {
            var roles = (await repo.GetRolesAsync(branchId)).ToDictionary(r => r.Name);
            // سه نقش پیش‌فرض باید باشند؛ تست‌های دیگر ممکن است نقش‌های سفارشی به همین شعبه اضافه کرده باشند.
            Assert.True(roles.ContainsKey(RolePresets.Accountant));
            Assert.True(roles.ContainsKey(RolePresets.BranchManager));
            Assert.True(roles.ContainsKey(RolePresets.Cashier));
            Assert.Equal(7, roles[RolePresets.Accountant].Permissions.Count);
            Assert.DoesNotContain(Permission.TradeRecord, roles[RolePresets.Accountant].Permissions);
            Assert.Equal(10, roles[RolePresets.BranchManager].Permissions.Count);
            Assert.Equal(new[] { Permission.TradeRecord }, roles[RolePresets.Cashier].Permissions.ToArray());
        }
        Assert.All(await repo.GetRolesAsync(newId), role => Assert.Equal(0, role.AssignedUsers));
    }

    [Fact]
    public async Task Membership_gives_read_access_everywhere_and_each_branch_has_its_own_role()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var permissions = new PermissionService(repo);
        var reports = new ReportService(repo);
        var tradeService = new CurrencyTradeService(repo);
        var branches = new BranchService(repo);
        var now = DateTime.Now;
        var admin = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var otherId = await branches.CreateBranchAsync(admin, "M" + Random.Shared.Next(100_000, 999_999), "شعبه‌ی چندشعبه", now);
        var cashier = await CreateCashierAsync(repo, admin, mainId, now);
        var accountantRole = await RoleIdAsync(repo, otherId, RolePresets.Accountant);

        // در شعبه‌ی اصلی کاربر صندوق است و شعبه‌ی دوم را اصلاً نمی‌بیند.
        Assert.True(await permissions.HasAsync(cashier, Permission.TradeRecord, mainId));
        Assert.False(await permissions.HasAsync(cashier, Permission.TradeRecord, otherId));
        await Assert.ThrowsAsync<BusinessRuleException>(() => reports.GetDashboardAsync(cashier, otherId, now));

        // مدیر کاربر را با نقش حسابدار به شعبه‌ی دوم اضافه می‌کند؛ اثر آن فوری است.
        await permissions.ApplyMembershipsAsync(admin, cashier.Id, new Dictionary<int, int?> { [otherId] = accountantRole }, mainId, now);
        Assert.NotNull(await reports.GetDashboardAsync(cashier, otherId, now));
        Assert.True(await permissions.HasAsync(cashier, Permission.TradeEdit, otherId));
        Assert.False(await permissions.HasAsync(cashier, Permission.TradeRecord, otherId));
        Assert.True(await permissions.HasAsync(cashier, Permission.TradeRecord, mainId));

        // ثبت معامله در شعبه‌ی دوم رد می‌شود چون نقش حسابدار آن را ندارد.
        var recordInOther = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            tradeService.BuyFromCustomerAsync(new TradeInput(otherId, "USD", 1m, 1_000_000m, null, null, null, CustomerId: _customerId), cashier, now));
        Assert.Contains("ثبت معامله", recordInOther.Message);

        // نقش شعبه‌ی اول به مدیر شعبه تغییر می‌کند؛ نقش شعبه‌ی دوم دست‌نخورده می‌ماند.
        var managerRole = await RoleIdAsync(repo, mainId, RolePresets.BranchManager);
        await permissions.ApplyMembershipsAsync(admin, cashier.Id, new Dictionary<int, int?> { [mainId] = managerRole }, mainId, now);
        Assert.True(await permissions.HasAsync(cashier, Permission.OpeningCreate, mainId));
        Assert.False(await permissions.HasAsync(cashier, Permission.OpeningCreate, otherId));
        Assert.True(await permissions.HasAsync(cashier, Permission.TradeEdit, otherId));

        // هر تغییر عضویت در سابقه ثبت می‌شود: یک بار افزودن به شعبه‌ی دوم، یک بار تغییر نقش شعبه‌ی اول.
        await using var conn = new SqlConnection(_fixture.ConnectionString!);
        await conn.OpenAsync();
        var auditRows = Convert.ToInt32(await ScalarAsync(conn,
            "SELECT COUNT(*) FROM dbo.AuditLog WHERE Action = N'USER_BRANCH_ROLE' AND EntityType = N'USER' AND EntityId = @userId;",
            new SqlParameter("@userId", cashier.Id)), CultureInfo.InvariantCulture);
        Assert.Equal(2, auditRows);
    }

    [Fact]
    public async Task Default_branch_must_stay_a_membership_and_can_move_in_the_same_change()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var permissions = new PermissionService(repo);
        var branches = new BranchService(repo);
        var now = DateTime.Now;
        var admin = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var otherId = await branches.CreateBranchAsync(admin, "D" + Random.Shared.Next(100_000, 999_999), "شعبه‌ی اصلی", now);
        var accountantRole = await RoleIdAsync(repo, otherId, RolePresets.Accountant);
        var cashier = await CreateCashierAsync(repo, admin, mainId, now);

        // حذف عضویت شعبه‌ی اصلی بدون انتخاب شعبه‌ی اصلی جدید رد می‌شود.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            permissions.ApplyMembershipsAsync(admin, cashier.Id, new Dictionary<int, int?> { [mainId] = null }, null, now));

        // شعبه‌ی اصلی جدید و حذف عضویت قبلی در یک تغییر انجام می‌شود.
        await permissions.ApplyMembershipsAsync(admin, cashier.Id,
            new Dictionary<int, int?> { [otherId] = accountantRole, [mainId] = null }, otherId, now);
        var access = await repo.GetUserAccessAsync(cashier.Id);
        Assert.NotNull(access);
        Assert.Equal(otherId, access!.DefaultBranchId);
        Assert.False(access.Branches.ContainsKey(mainId));
        Assert.False(await permissions.HasAsync(cashier, Permission.TradeRecord, mainId));
        Assert.True(await permissions.HasAsync(cashier, Permission.TradeEdit, otherId));
    }

    [Fact]
    public async Task Roles_in_use_cannot_be_deleted_and_a_role_of_another_branch_cannot_be_assigned()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var permissions = new PermissionService(repo);
        var branches = new BranchService(repo);
        var now = DateTime.Now;
        var admin = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var newId = await branches.CreateBranchAsync(admin, "E" + Random.Shared.Next(100_000, 999_999), "شعبه‌ی نقش‌ها", now);
        var cashier = await CreateCashierAsync(repo, admin, mainId, now);

        var duplicate = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            permissions.CreateRoleAsync(admin, mainId, RolePresets.Cashier, Array.Empty<Permission>(), now));
        Assert.Contains("نقشی با این نام", duplicate.Message);

        var roleId = await permissions.CreateRoleAsync(admin, newId, "ناظر " + Random.Shared.Next(100_000, 999_999), new[] { Permission.TradeRecord }, now);

        // نقش شعبه‌ی دیگر را نمی‌توان به عضویت این شعبه داد.
        var mainCashierRole = await RoleIdAsync(repo, mainId, RolePresets.Cashier);
        var cross = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            permissions.ApplyMembershipsAsync(admin, cashier.Id, new Dictionary<int, int?> { [newId] = mainCashierRole }, mainId, now));
        Assert.Contains("متعلق به این شعبه", cross.Message);

        // نقش داده‌شده قابل حذف نیست؛ بعد از برداشتن عضویت حذف می‌شود.
        await permissions.ApplyMembershipsAsync(admin, cashier.Id, new Dictionary<int, int?> { [newId] = roleId }, mainId, now);
        var inUse = await Assert.ThrowsAsync<BusinessRuleException>(() => permissions.DeleteRoleAsync(admin, roleId, now));
        Assert.Contains("داده شده", inUse.Message);

        await permissions.ApplyMembershipsAsync(admin, cashier.Id, new Dictionary<int, int?> { [newId] = null }, mainId, now);
        await permissions.DeleteRoleAsync(admin, roleId, now);
        Assert.DoesNotContain(await repo.GetRolesAsync(newId), r => r.Id == roleId);
    }

    [Fact]
    public async Task Only_admin_manages_roles_and_admin_never_receives_branch_roles()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var permissions = new PermissionService(repo);
        var now = DateTime.Now;
        var admin = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var cashier = await CreateCashierAsync(repo, admin, mainId, now);
        var managerRole = await RoleIdAsync(repo, mainId, RolePresets.BranchManager);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            permissions.CreateRoleAsync(cashier, mainId, "نقش ناجور", new[] { Permission.RateSet }, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            permissions.ApplyMembershipsAsync(cashier, cashier.Id, new Dictionary<int, int?>(), null, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            permissions.ApplyMembershipsAsync(admin, admin.Id, new Dictionary<int, int?> { [mainId] = managerRole }, null, now));
    }

    [Fact]
    public async Task Openings_rates_and_manual_documents_need_their_own_tasks()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var manual = new ManualJournalService(repo);
        var permissions = new PermissionService(repo);
        var now = DateTime.Now;
        var adminUser = await EnsureAdminAsync(repo, now);
        var mainId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);

        await admin.AddCurrencyAsync(adminUser, code, "ارز افتتاحیه‌ی دسترسی", 2, now);
        await admin.OpeningIrrAsync(adminUser, mainId, 100_000_000m, now);
        var openingId = await admin.RecordOpeningAsync(adminUser, mainId, code, 10m, 1_000_000m, now);
        Assert.NotNull(openingId);
        var lines = new[] { new JournalLineDraft("6001", 10_000_000m, 0m), new JournalLineDraft(AccountCodes.IrrCash, 0m, 10_000_000m) };
        var manualId = await manual.CreateAsync(adminUser, mainId, "هزینه‌ی آزمایشی دسترسی", null, lines, now);
        Assert.NotNull(manualId);

        // کاربر صندوق فقط ثبت معامله دارد؛ هیچ‌کدام از این کارها را ندارد.
        var cashier = await CreateCashierAsync(repo, adminUser, mainId, now);
        await Assert.ThrowsAsync<BusinessRuleException>(() => admin.RecordOpeningAsync(cashier, mainId, code, 1m, 1_000_000m, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => admin.SetRateAsync(cashier, mainId, code, 1_000_000m, 1_100_000m, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => manual.CreateAsync(cashier, mainId, "سند کاربر", null, lines, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => admin.EditOpeningAsync(cashier, openingId!.Value, 20m, 1_000_000m, null, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => admin.VoidOpeningAsync(cashier, openingId.Value, "دلیل", now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => manual.EditAsync(cashier, manualId!.Value, "ویرایش", null, lines, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => manual.VoidAsync(cashier, manualId.Value, "دلیل", now));

        // نقش جدا با دو کار: ابطال افتتاحیه و ویرایش سند دستی؛ بقیه‌ی کارها همچنان بسته است.
        var roleId = await permissions.CreateRoleAsync(adminUser, mainId, "ابطال افتتاحیه " + Random.Shared.Next(100_000, 999_999),
            new[] { Permission.OpeningVoid, Permission.ManualEdit }, now);
        await permissions.ApplyMembershipsAsync(adminUser, cashier.Id, new Dictionary<int, int?> { [mainId] = roleId }, mainId, now);

        await admin.VoidOpeningAsync(cashier, openingId.Value, "ورود اشتباه", now);
        Assert.True((await repo.GetOpeningAsync(openingId.Value))!.IsVoided);

        var editedManualId = await manual.EditAsync(cashier, manualId.Value, "هزینه‌ی ویرایش‌شده", null, lines, now);
        Assert.NotNull(editedManualId);
        Assert.True((await repo.GetJournalEntryAsync(manualId.Value))!.IsVoided);
        Assert.False((await repo.GetJournalEntryAsync(editedManualId!.Value))!.IsVoided);
        await Assert.ThrowsAsync<BusinessRuleException>(() => manual.VoidAsync(cashier, editedManualId.Value, "دلیل", now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => admin.RecordOpeningAsync(cashier, mainId, code, 1m, 1_000_000m, now));
    }

    [Fact]
    public async Task Chart_is_a_consistent_four_level_tree_and_flags_match_the_data()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var accounts = await repo.GetAccountsAsync();
        var byCode = accounts.ToDictionary(a => a.Code);

        foreach (var account in accounts)
        {
            if (account.Level == 1)
            {
                Assert.Null(account.ParentCode);
                Assert.Equal(1, account.Code.Length);
            }
            else
            {
                Assert.NotNull(account.ParentCode);
                var parent = byCode[account.ParentCode!];
                Assert.Equal(parent.Level + 1, account.Level);
                Assert.StartsWith(parent.Code, account.Code, StringComparison.Ordinal);
                Assert.Equal(parent.AccountType, account.AccountType);
            }
            Assert.Equal(accounts.Any(c => c.ParentCode == account.Code), account.HasChildren);
        }
        Assert.True(byCode["1001"].IsSystem);
        Assert.False(byCode["6"].IsPostable);
        Assert.True(byCode["6001"].IsPostable);
    }

    [Fact]
    public async Task Admin_builds_the_chart_and_the_guards_hold()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var chart = new AccountService(repo);
        var manual = new ManualJournalService(repo);
        var now = DateTime.Now;
        var admin = await EnsureAdminAsync(repo, now);
        var branchId = await MainBranchIdAsync(repo);
        var code = "60" + Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
        var detail = code + "-1";

        await chart.CreateAsync(admin, code, "هزینه‌ی آزمایشی", "60", "Expense", now);
        await chart.CreateAsync(admin, detail, "تفصیلی آزمایشی", code, "Expense", now);
        var created = (await repo.GetAccountsAsync()).Single(a => a.Code == detail);
        Assert.Equal(4, created.Level);
        Assert.Equal("Expense", created.AccountType);

        await Assert.ThrowsAsync<BusinessRuleException>(() => chart.CreateAsync(admin, code + "-2", "زیر تفصیلی", detail, "Expense", now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => chart.CreateAsync(admin, "1001-9", "زیر صندوق", "1001", "Asset", now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => chart.UpdateAsync(admin, "1001", "1002", "صندوق ریال", "10", "Asset", true, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => chart.UpdateAsync(admin, "1001", "1001", "صندوق ریال", "10", "Asset", false, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => chart.UpdateAsync(admin, code, code, "هزینه", "60", "Expense", false, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => chart.DeleteAsync(admin, code, now));
        await Assert.ThrowsAsync<BusinessRuleException>(() => chart.DeleteAsync(admin, "1001", now));

        var postToGroup = new[] { new JournalLineDraft("6", 100m, 0m), new JournalLineDraft("1001", 0m, 100m) };
        await Assert.ThrowsAsync<BusinessRuleException>(() => manual.CreateAsync(admin, branchId, "سند روی گروه", null, postToGroup, now));

        await chart.UpdateAsync(admin, "1001", "1001", "صندوق ریال", "10", "Asset", true, now);
        await chart.DeleteAsync(admin, detail, now);
        await chart.DeleteAsync(admin, code, now);
        Assert.DoesNotContain(await repo.GetAccountsAsync(), a => a.Code == code);
    }

    [Fact]
    public async Task Every_trade_needs_a_customer_and_keeps_the_name_it_was_recorded_with()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var admin = new CurrencyAdminService(repo);
        var trades = new CurrencyTradeService(repo);
        var customers = new CustomerService(repo, new PermissionService(repo));
        var now = DateTime.Now;
        var user = await EnsureAdminAsync(repo, now);
        var branchId = await MainBranchIdAsync(repo);
        var code = await NewCurrencyCodeAsync(repo);
        await admin.AddCurrencyAsync(user, code, "ارز آزمایشی", 2, now);
        await admin.SetRateAsync(user, branchId, code, 1_000_000m, 1_100_000m, now);
        var nationalCode = "T" + Guid.NewGuid().ToString("N")[..10];

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            trades.BuyFromCustomerAsync(new TradeInput(branchId, code, 1m, 1_000_000m, null, null, null), user, now));

        var customerId = await customers.CreateAsync(user,
            new CustomerInput("  زهرا احمدی ", nationalCode, "۰۹۱۲۳۴۵۶۷۸۹", null, null), now);
        var buyId = await trades.BuyFromCustomerAsync(
            new TradeInput(branchId, code, 1m, 1_000_000m, null, null, null, CustomerId: customerId), user, now);

        var recorded = await repo.GetTradeAsync(buyId);
        Assert.Equal(customerId, recorded!.CustomerId);
        Assert.Equal("زهرا احمدی", recorded.CustomerName);
        Assert.Equal(nationalCode, recorded.NationalCode);

        await customers.UpdateAsync(user, customerId, new CustomerInput("زهرا احمدی‌نژاد", nationalCode, "۰۹۱۲۳۴۵۶۷۸۹", null, null), now);
        var afterEdit = await repo.GetTradeAsync(buyId);
        Assert.Equal("زهرا احمدی", afterEdit!.CustomerName);

        var found = await customers.SearchAsync(user, "احمدی‌نژاد");
        Assert.Contains(found, c => c.Id == customerId && c.Phone == "09123456789");

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            customers.CreateAsync(user, new CustomerInput("کس دیگر", nationalCode, null, null, null), now));
    }

    [Fact]
    public async Task Customer_code_is_generated_by_the_system_and_cannot_be_changed_in_the_database()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var user = await EnsureAdminAsync(repo, DateTime.Now);
        var customers = new CustomerService(repo, new PermissionService(repo));
        var now = DateTime.Now;
        var id = await customers.CreateAsync(user, new CustomerInput("مریم کاظمی", null, null, null, null), now);

        var created = await repo.GetCustomerAsync(id);
        Assert.NotNull(created);
        Assert.Equal("C" + id.ToString("D10", CultureInfo.InvariantCulture), created!.CustomerCode);

        await customers.UpdateAsync(user, id, new CustomerInput("مریم کاظمی‌پور", null, null, null, null), now);
        Assert.Equal(created.CustomerCode, (await repo.GetCustomerAsync(id))!.CustomerCode);

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("UPDATE dbo.Customers SET CustomerCode = N'C0000000000' WHERE Id = @id;", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Customer_extra_fields_are_stored_normalised_and_searchable()
    {
        if (!_fixture.IsEnabled)
        {
            return;
        }

        var repo = new SqlAccountingRepository(_fixture.ConnectionString!);
        var user = await EnsureAdminAsync(repo, DateTime.Now);
        var customers = new CustomerService(repo, new PermissionService(repo));
        var now = DateTime.Now;
        var id = await customers.CreateAsync(user, new CustomerInput(
            "رضا نوری", null, "۰۲۱ ۱۲۳۴۵۶۷۸", "تهران، خیابان ولیعصر", "یادداشت",
            "۰۹۱۲-۳۴۵-۶۷۸۹", "تهران",
            "ir12 3456 7890 1234 5678 9012 34", "IR 1111 2222 3333 4444 5555 6666",
            "6037-9975-1234-5678", "5892 1012 3456 7890"), now);

        var stored = await repo.GetCustomerAsync(id);
        Assert.Equal("02112345678", stored!.Phone);
        Assert.Equal("09123456789", stored.Mobile);
        Assert.Equal("تهران", stored.City);
        Assert.Equal("IR123456789012345678901234", stored.Sheba1);
        Assert.Equal("IR111122223333444455556666", stored.Sheba2);
        Assert.Equal("6037997512345678", stored.CardNumber1);
        Assert.Equal("5892101234567890", stored.CardNumber2);

        var byMobile = await customers.SearchAsync(user, "۰۹۱۲۳۴۵۶۷۸۹");
        Assert.Contains(byMobile, c => c.Id == id);
        var byCode = await customers.SearchAsync(user, stored.CustomerCode);
        Assert.Contains(byCode, c => c.Id == id);
        var byCard = await customers.SearchAsync(user, "5892101234567890");
        Assert.Contains(byCard, c => c.Id == id);
        var bySheba = await customers.SearchAsync(user, "1111222233334444");
        Assert.Contains(bySheba, c => c.Id == id);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            customers.CreateAsync(user, new CustomerInput("رضا نوری", CardNumber1: "1234"), now));
    }

    private static async Task AssertLedgerConsistentAsync(string connectionString, int branchId, string currencyCode)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var unbalanced = Convert.ToInt32(await ScalarAsync(conn, @"
SELECT COUNT(*) FROM (SELECT JournalEntryId FROM dbo.JournalLines GROUP BY JournalEntryId HAVING SUM(Debit) <> SUM(Credit)) x;"), CultureInfo.InvariantCulture);
        Assert.Equal(0, unbalanced);

        var boxMismatch = Convert.ToInt32(await ScalarAsync(conn, @"
SELECT COUNT(*) FROM dbo.CashBoxes b
WHERE b.BranchId = @branchId AND b.Balance <> (SELECT COALESCE(SUM(m.Amount), 0) FROM dbo.CashMovements m WHERE m.CashBoxId = b.Id);",
            new SqlParameter("@branchId", branchId)), CultureInfo.InvariantCulture);
        Assert.Equal(0, boxMismatch);

        var runningMismatch = Convert.ToInt32(await ScalarAsync(conn, @"
SELECT COUNT(*) FROM dbo.CashBoxes b
CROSS APPLY (SELECT TOP (1) m.BalanceAfter FROM dbo.CashMovements m WHERE m.CashBoxId = b.Id ORDER BY m.OccurredAt DESC, m.Id DESC) last
WHERE b.BranchId = @branchId AND last.BalanceAfter <> b.Balance;",
            new SqlParameter("@branchId", branchId)), CultureInfo.InvariantCulture);
        Assert.Equal(0, runningMismatch);

        var irrInJournal = Convert.ToDecimal(await ScalarAsync(conn, @"
SELECT COALESCE(SUM(l.Debit - l.Credit), 0) FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
WHERE e.BranchId = @branchId AND l.AccountCode = N'1001';",
            new SqlParameter("@branchId", branchId)), CultureInfo.InvariantCulture);
        var irrInBox = Convert.ToDecimal(await ScalarAsync(conn,
            "SELECT Balance FROM dbo.CashBoxes WHERE BranchId = @branchId AND CurrencyCode = N'IRR';",
            new SqlParameter("@branchId", branchId)), CultureInfo.InvariantCulture);
        Assert.Equal(irrInBox, irrInJournal);

        if (currencyCode != CurrencyCodes.Irr)
        {
            var costInJournal = Convert.ToDecimal(await ScalarAsync(conn, @"
SELECT COALESCE(SUM(l.Debit - l.Credit), 0) FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
WHERE e.BranchId = @branchId AND l.AccountCode = @account;",
                new SqlParameter("@branchId", branchId),
                new SqlParameter("@account", AccountCodes.ForeignCash(currencyCode))), CultureInfo.InvariantCulture);
            var costInInventory = Convert.ToDecimal(await ScalarAsync(conn,
                "SELECT TotalCostIrr FROM dbo.CurrencyInventory WHERE BranchId = @branchId AND CurrencyCode = @code;",
                new SqlParameter("@branchId", branchId),
                new SqlParameter("@code", currencyCode)), CultureInfo.InvariantCulture);
            Assert.Equal(costInInventory, costInJournal);
        }
    }

    private static async Task<CurrentUser> CreateCashierAsync(IAccountingRepository repo, CurrentUser admin, int branchId, DateTime now)
    {
        var username = "usr" + Random.Shared.Next(100_000, 999_999);
        await new UserService(repo).CreateUserAsync(admin, username, "کاربر آزمایشی", "Test#12345", UserRole.Cashier, branchId, now);
        var account = await repo.GetUserByUsernameAsync(username)
            ?? throw new InvalidOperationException("کاربر آزمایشی ساخته نشد.");
        return new CurrentUser(account.Id, account.Username, account.FullName, account.Role, account.BranchId, account.BranchName);
    }

    private static async Task<int> RoleIdAsync(IAccountingRepository repo, int branchId, string roleName)
    {
        var roles = await repo.GetRolesAsync(branchId);
        return roles.Single(r => r.Name == roleName).Id;
    }

    private static async Task<object?> ScalarAsync(SqlConnection conn, string sql, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteScalarAsync();
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
