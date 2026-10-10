namespace AccountingSystem.Core.Domain;

/// <summary>یک سطر گردش حساب دفتر کل؛ مانده به‌صورت بدهکار مثبت و بستانکار منفی است.</summary>
public sealed record AccountLedgerLineInfo(
    long JournalEntryId,
    long? SourceId,
    DateTime OccurredAt,
    int BranchId,
    string BranchName,
    string SourceType,
    string Description,
    int LineNo,
    string AccountCode,
    string AccountName,
    decimal Debit,
    decimal Credit,
    bool IsVoided,
    decimal BalanceIrr);

/// <summary>داده‌ی خام معین حساب شامل مانده‌ی ابتدای دوره و گردش‌های واقعی دفتر روزنامه.</summary>
public sealed record AccountLedgerData(
    decimal OpeningBalanceIrr,
    IReadOnlyList<AccountLedgerLineInfo> Lines);

/// <summary>مانده‌ی یک سرفصل تا پایان تاریخ انتخابی؛ شامل زیرحساب‌ها فقط در صورت درخواست کاربر.</summary>
public sealed record AccountBalanceReport(
    AccountInfo Account,
    bool IncludesChildren,
    DateTime AsOf,
    decimal BalanceIrr);

/// <summary>گزارش معین سرفصل، با مانده‌ی اول و آخر دوره.</summary>
public sealed record AccountLedgerReport(
    AccountInfo Account,
    bool IncludesChildren,
    DateTime FromInclusive,
    DateTime ToExclusive,
    decimal OpeningBalanceIrr,
    IReadOnlyList<AccountLedgerLineInfo> Lines,
    decimal ClosingBalanceIrr);

/// <summary>حرکت یک صندوق یا حساب بانکی؛ مبلغ مثبت افزایش موجودی و مبلغ منفی کاهش آن است.</summary>
public sealed record OperationalLedgerLineInfo(
    DateTime OccurredAt,
    string SourceType,
    long? ReferenceId,
    string Description,
    decimal Amount,
    decimal Balance);

/// <summary>داده‌ی خام معین صندوق؛ در گزارش مانده‌ای Lines خالی و OpeningBalance مانده تا تاریخ است.</summary>
public sealed record CashBoxLedgerData(
    CashBoxInfo CashBox,
    int DecimalPlaces,
    decimal OpeningBalance,
    IReadOnlyList<OperationalLedgerLineInfo> Lines);

/// <summary>معین صندوق با مانده‌ی آغاز و پایان دوره یا مانده‌ی یک تاریخ.</summary>
public sealed record CashBoxLedgerReport(
    CashBoxInfo CashBox,
    int DecimalPlaces,
    DateTime? FromInclusive,
    DateTime ToExclusive,
    decimal OpeningBalance,
    IReadOnlyList<OperationalLedgerLineInfo> Lines,
    decimal ClosingBalance);

/// <summary>داده‌ی خام معین حساب بانکی نام‌دار؛ مانده و گردش به واحد ارز همان حساب است.</summary>
public sealed record BankAccountLedgerData(
    BankAccountInfo BankAccount,
    decimal OpeningBalance,
    IReadOnlyList<OperationalLedgerLineInfo> Lines);

/// <summary>معین حساب بانکی نام‌دار با مانده‌ی آغاز و پایان دوره یا مانده‌ی یک تاریخ.</summary>
public sealed record BankAccountLedgerReport(
    BankAccountInfo BankAccount,
    DateTime? FromInclusive,
    DateTime ToExclusive,
    decimal OpeningBalance,
    IReadOnlyList<OperationalLedgerLineInfo> Lines,
    decimal ClosingBalance);
