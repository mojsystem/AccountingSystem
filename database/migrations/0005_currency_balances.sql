-- 0005 - مانده‌ی مشتری به تفکیک ارز برای دریافت/پرداخت و حفظ بدهی‌های تاریخی معاملات
GO

ALTER TABLE dbo.CashTransactions
    ADD BalanceCurrencyCode NCHAR(3) NULL,
        BalanceAmount DECIMAL(19,4) NULL;
GO

-- اسناد قدیمی، مانده را در همان ارز صندوق ثبت می‌کردند.
UPDATE dbo.CashTransactions
SET BalanceCurrencyCode = CurrencyCode,
    BalanceAmount = Amount;
GO

ALTER TABLE dbo.CashTransactions
    ALTER COLUMN BalanceCurrencyCode NCHAR(3) NOT NULL;
ALTER TABLE dbo.CashTransactions
    ALTER COLUMN BalanceAmount DECIMAL(19,4) NOT NULL;
ALTER TABLE dbo.CashTransactions
    ADD CONSTRAINT FK_CashTransactions_BalanceCurrencies FOREIGN KEY (BalanceCurrencyCode) REFERENCES dbo.Currencies (Code),
        CONSTRAINT CK_CashTransactions_BalanceAmount CHECK (BalanceAmount > 0);
GO

-- سطرهای مشتری در دفتر، علاوه بر ارزش ریالی، تغییر مانده به ارز حساب را نگه می‌دارند.
ALTER TABLE dbo.JournalLines
    ADD CustomerBalanceCurrencyCode NCHAR(3) NULL,
        CustomerBalanceDelta DECIMAL(19,4) NULL;
GO

-- بدهی‌های قدیمی معاملات در ریال ثبت شده‌اند و به همان شکل در حساب ریالی مشتری باقی می‌مانند.
UPDATE dbo.JournalLines
SET CustomerBalanceCurrencyCode = N'IRR',
    CustomerBalanceDelta = Debit - Credit
WHERE CustomerId IS NOT NULL
  AND AccountCode IN (N'1201', N'2101');
GO

-- دریافت/پرداخت‌های نسخه‌ی ۴ به ارز انتخاب‌شده در صندوق منتقل می‌شوند؛ مقدار ریالی سابق مبنای مانده‌ی ارزی نیست.
UPDATE l
SET l.CustomerBalanceCurrencyCode = t.CurrencyCode,
    l.CustomerBalanceDelta = CASE WHEN t.Direction = N'RECEIVE' THEN -t.Amount ELSE t.Amount END
FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
INNER JOIN dbo.CashTransactions t ON t.Id = e.SourceId AND t.BranchId = e.BranchId
WHERE e.SourceType IN (N'CASH_RECEIPT', N'CASH_PAYMENT')
  AND l.CustomerId = t.CustomerId
  AND l.AccountCode IN (N'1201', N'2101');
GO

-- سند معکوس اسناد قدیمی نیز به همان ارز و با جهت برعکس بازنویسی می‌شود.
UPDATE l
SET l.CustomerBalanceCurrencyCode = t.CurrencyCode,
    l.CustomerBalanceDelta = CASE WHEN t.Direction = N'RECEIVE' THEN t.Amount ELSE -t.Amount END
FROM dbo.JournalLines l
INNER JOIN dbo.JournalEntries e ON e.Id = l.JournalEntryId
INNER JOIN dbo.CashTransactions t ON t.Id = e.SourceId AND t.BranchId = e.BranchId
WHERE e.SourceType = N'VOID'
  AND e.Description LIKE N'ابطال دریافت/پرداخت%'
  AND t.IsVoided = 1
  AND l.CustomerId = t.CustomerId
  AND l.AccountCode IN (N'1201', N'2101');
GO

ALTER TABLE dbo.JournalLines
    ADD CONSTRAINT FK_JournalLines_CustomerBalanceCurrency FOREIGN KEY (CustomerBalanceCurrencyCode) REFERENCES dbo.Currencies (Code),
        CONSTRAINT CK_JournalLines_CustomerBalance CHECK
        (
            (CustomerBalanceCurrencyCode IS NULL AND CustomerBalanceDelta IS NULL)
            OR (CustomerId IS NOT NULL AND CustomerBalanceCurrencyCode IS NOT NULL AND CustomerBalanceDelta IS NOT NULL)
        );
CREATE INDEX IX_JournalLines_CustomerBalanceCurrency
    ON dbo.JournalLines (CustomerId, CustomerBalanceCurrencyCode, JournalEntryId)
    INCLUDE (CustomerBalanceDelta);
GO
