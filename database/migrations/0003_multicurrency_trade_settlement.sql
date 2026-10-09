-- 0003 - خرید و فروش جفت‌ارزی، تسویه‌ی چندبخشی و حساب مشتری
-- سوابق قبلی به‌عنوان تسویه‌ی کامل ریالی حفظ می‌شوند.
GO

-- روش تسویه و نرخِ ثبت‌شده در همان معامله نگه‌داری می‌شود تا سوابق پس از تغییر نرخ روز تغییر نکنند.
ALTER TABLE dbo.CurrencyTransactions ADD
    SettlementCurrencyCode NCHAR(3) NULL,
    SettlementMode NVARCHAR(12) NOT NULL CONSTRAINT DF_CurrencyTransactions_SettlementMode DEFAULT (N'DIRECT'),
    RateMode NVARCHAR(12) NOT NULL CONSTRAINT DF_CurrencyTransactions_RateMode DEFAULT (N'DERIVED'),
    CrossRate DECIMAL(19,8) NOT NULL CONSTRAINT DF_CurrencyTransactions_CrossRate DEFAULT (0),
    CustomerOffsetIrr DECIMAL(19,4) NOT NULL CONSTRAINT DF_CurrencyTransactions_CustomerOffset DEFAULT (0);
GO

UPDATE dbo.CurrencyTransactions
SET CrossRate = Rate,
    SettlementCurrencyCode = N'IRR'
WHERE CrossRate = 0;
GO

ALTER TABLE dbo.CurrencyTransactions ADD CONSTRAINT FK_CurrencyTransactions_SettlementCurrency
    FOREIGN KEY (SettlementCurrencyCode) REFERENCES dbo.Currencies (Code);
ALTER TABLE dbo.CurrencyTransactions ADD CONSTRAINT CK_CurrencyTransactions_SettlementMode
    CHECK (SettlementMode IN (N'DIRECT', N'SPLIT', N'ACCOUNT'));
ALTER TABLE dbo.CurrencyTransactions ADD CONSTRAINT CK_CurrencyTransactions_RateMode
    CHECK (RateMode IN (N'DIRECT', N'DERIVED'));
ALTER TABLE dbo.CurrencyTransactions ADD CONSTRAINT CK_CurrencyTransactions_CrossRate
    CHECK (CrossRate >= 0 AND CustomerOffsetIrr >= 0);
GO

-- ریز دریافت/پرداخت معامله؛ برای فروش ارز، دریافت و برای خرید ارز، پرداخت ثبت می‌شود.
CREATE TABLE dbo.CurrencyTransactionSettlements
(
    TradeId       BIGINT         NOT NULL,
    LineNumber    INT            NOT NULL,
    Direction     NVARCHAR(8)    NOT NULL,
    CurrencyCode  NCHAR(3)       NOT NULL,
    Amount        DECIMAL(19,4)  NOT NULL,
    RateIrr       DECIMAL(19,4)  NOT NULL,
    IrrAmount     DECIMAL(19,4)  NOT NULL,
    CostIrr       DECIMAL(19,4)  NOT NULL CONSTRAINT DF_TradeSettlements_Cost DEFAULT (0),
    ProfitIrr     DECIMAL(19,4)  NOT NULL CONSTRAINT DF_TradeSettlements_Profit DEFAULT (0),
    CONSTRAINT PK_CurrencyTransactionSettlements PRIMARY KEY (TradeId, LineNumber),
    CONSTRAINT FK_TradeSettlements_Trade FOREIGN KEY (TradeId) REFERENCES dbo.CurrencyTransactions (Id),
    CONSTRAINT FK_TradeSettlements_Currency FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT CK_TradeSettlements_Direction CHECK (Direction IN (N'PAY', N'RECEIVE')),
    CONSTRAINT CK_TradeSettlements_Amount CHECK (Amount > 0 AND RateIrr > 0 AND IrrAmount > 0 AND CostIrr >= 0)
);
CREATE INDEX IX_TradeSettlements_Currency ON dbo.CurrencyTransactionSettlements (CurrencyCode, TradeId);
GO

-- تمام معاملات پیشین با ریال تسویه شده بودند؛ داده‌ی تسویه را از ارقام ثبت‌شده‌ی معامله بازسازی می‌کنیم.
INSERT INTO dbo.CurrencyTransactionSettlements
    (TradeId, LineNumber, Direction, CurrencyCode, Amount, RateIrr, IrrAmount, CostIrr, ProfitIrr)
SELECT Id,
       1,
       CASE WHEN TradeType = N'BUY' THEN N'PAY' ELSE N'RECEIVE' END,
       N'IRR',
       CASE WHEN TradeType = N'BUY' THEN IrrAmount - FeeIrr ELSE IrrAmount + FeeIrr END,
       1,
       CASE WHEN TradeType = N'BUY' THEN IrrAmount - FeeIrr ELSE IrrAmount + FeeIrr END,
       CASE WHEN TradeType = N'BUY' THEN IrrAmount - FeeIrr ELSE IrrAmount + FeeIrr END,
       0
FROM dbo.CurrencyTransactions;
GO

-- حساب‌های کنترلی مانده‌ی مشتری؛ CustomerId در سطر سند تفصیلیِ مانده را نگه می‌دارد.
IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'2')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'2', N'بدهی‌ها', N'Liability', 1, NULL, 1, 1);
ELSE
    UPDATE dbo.Accounts SET Name = N'بدهی‌ها', AccountType = N'Liability', Level = 1, ParentCode = NULL, IsSystem = 1 WHERE Code = N'2';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'12')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'12', N'مطالبات از مشتریان', N'Asset', 2, N'1', 1, 1);
ELSE
    UPDATE dbo.Accounts SET Name = N'مطالبات از مشتریان', AccountType = N'Asset', Level = 2, ParentCode = N'1', IsSystem = 1 WHERE Code = N'12';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'1201')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'1201', N'حساب دریافتنی مشتریان', N'Asset', 3, N'12', 1, 1);
ELSE
    UPDATE dbo.Accounts SET Name = N'حساب دریافتنی مشتریان', AccountType = N'Asset', Level = 3, ParentCode = N'12', IsSystem = 1 WHERE Code = N'1201';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'21')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'21', N'بدهی به مشتریان', N'Liability', 2, N'2', 1, 1);
ELSE
    UPDATE dbo.Accounts SET Name = N'بدهی به مشتریان', AccountType = N'Liability', Level = 2, ParentCode = N'2', IsSystem = 1 WHERE Code = N'21';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'2101')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'2101', N'حساب پرداختنی مشتریان', N'Liability', 3, N'21', 1, 1);
ELSE
    UPDATE dbo.Accounts SET Name = N'حساب پرداختنی مشتریان', AccountType = N'Liability', Level = 3, ParentCode = N'21', IsSystem = 1 WHERE Code = N'2101';
GO

ALTER TABLE dbo.JournalLines ADD CustomerId INT NULL;
GO

ALTER TABLE dbo.JournalLines ADD CONSTRAINT FK_JournalLines_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id);
ALTER TABLE dbo.JournalLines ADD CONSTRAINT CK_JournalLines_CustomerAccount
    CHECK (CustomerId IS NULL OR AccountCode IN (N'1201', N'2101'));
CREATE INDEX IX_JournalLines_Customer ON dbo.JournalLines (CustomerId, AccountCode, JournalEntryId) INCLUDE (Debit, Credit) WHERE CustomerId IS NOT NULL;
GO
