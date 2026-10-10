-- 0006 - روش دریافت/پرداخت معامله، حساب‌های بانکی نام‌دار و حساب‌های واسط چک/کارتخوان
GO

ALTER TABLE dbo.CurrencyTransactions
    ADD PaymentMethod NVARCHAR(12) NOT NULL
        CONSTRAINT DF_CurrencyTransactions_PaymentMethod DEFAULT (N'CASH');
GO

-- معاملات قدیمیِ ثبت‌شده روی حساب مشتری از این پس با روش نسیه نمایش داده می‌شوند.
UPDATE dbo.CurrencyTransactions
SET PaymentMethod = N'CREDIT'
WHERE SettlementMode = N'ACCOUNT';
GO

ALTER TABLE dbo.CurrencyTransactions
    ADD CONSTRAINT CK_CurrencyTransactions_PaymentMethod
        CHECK (PaymentMethod IN (N'CASH', N'CREDIT', N'CHEQUE', N'POS', N'TRANSFER'));
GO

ALTER TABLE dbo.CurrencyTransactions
    ADD CONSTRAINT CK_CurrencyTransactions_PaymentMethod_SettlementMode
        CHECK ((PaymentMethod = N'CREDIT' AND SettlementMode = N'ACCOUNT')
            OR (PaymentMethod <> N'CREDIT' AND SettlementMode IN (N'DIRECT', N'SPLIT')));
GO

CREATE TABLE dbo.BankAccounts
(
    Id              INT IDENTITY(1,1) NOT NULL,
    BranchId        INT               NOT NULL,
    Name            NVARCHAR(100)     NOT NULL,
    CurrencyCode    NCHAR(3)          NOT NULL,
    OpeningBalance  DECIMAL(19,4)     NOT NULL,
    OpeningCostIrr  DECIMAL(19,4)     NOT NULL,
    Balance         DECIMAL(19,4)     NOT NULL,
    CostIrr         DECIMAL(19,4)     NOT NULL,
    CreatedAt       DATETIME2(0)      NOT NULL,
    CreatedBy       INT               NOT NULL,
    UpdatedAt       DATETIME2(0)      NOT NULL,
    CONSTRAINT PK_BankAccounts PRIMARY KEY (Id),
    CONSTRAINT FK_BankAccounts_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_BankAccounts_Currencies FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT FK_BankAccounts_CreatedBy FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_BankAccounts_Name CHECK (LEN(LTRIM(RTRIM(Name))) BETWEEN 2 AND 100),
    CONSTRAINT CK_BankAccounts_Opening CHECK (OpeningBalance >= 0 AND OpeningCostIrr >= 0),
    CONSTRAINT CK_BankAccounts_Balance CHECK (Balance >= 0 AND CostIrr >= 0)
);
CREATE UNIQUE INDEX UX_BankAccounts_Branch_Currency_Name
    ON dbo.BankAccounts (BranchId, CurrencyCode, Name);
GO

-- افتتاحیه‌ی حساب بانکی هم سند دفتر کل است و باید در محدودیت نوع سند پذیرفته شود.
ALTER TABLE dbo.JournalEntries DROP CONSTRAINT CK_JournalEntries_Source;
ALTER TABLE dbo.JournalEntries ADD CONSTRAINT CK_JournalEntries_Source CHECK
    (SourceType IN (N'TRADE', N'OPENING', N'VOID', N'ADJUST', N'MANUAL', N'CASH_RECEIPT', N'CASH_PAYMENT', N'CASH_ADJUST', N'BANK_OPENING'));
GO

ALTER TABLE dbo.CurrencyTransactionSettlements
    ADD BankAccountId INT NULL;
GO

ALTER TABLE dbo.CurrencyTransactionSettlements
    ADD CONSTRAINT FK_TradeSettlements_BankAccount
        FOREIGN KEY (BankAccountId) REFERENCES dbo.BankAccounts (Id);
GO

-- حساب‌های دفتر کل متناظر با صندوق بانکی و حساب‌های واسط؛ حساب بانکی به تفکیک ارز در BankAccounts نگهداری می‌شود.
IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'1002')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'1002', N'حساب بانکی ریالی', N'Asset', 3, N'10', 1, 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'1102')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'1102', N'موجودی حساب‌های بانکی ارزی', N'Asset', 3, N'11', 0, 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'12')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'12', N'مطالبات از مشتریان', N'Asset', 2, N'1', 1, 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'1202')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'1202', N'اسناد و چک‌های دریافتنی', N'Asset', 3, N'12', 1, 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'1203')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'1203', N'مطالبات کارتخوان', N'Asset', 3, N'12', 1, 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'21')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'21', N'بدهی به مشتریان', N'Liability', 2, N'2', 1, 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'2102')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'2102', N'چک‌های پرداختنی', N'Liability', 3, N'21', 1, 1);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Code = N'2103')
    INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
    VALUES (N'2103', N'حساب واسط کارتخوان پرداختی', N'Liability', 3, N'21', 1, 1);
GO

INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem, IsActive)
SELECT N'1102-' + RTRIM(c.Code), N'موجودی بانکی - ' + c.Name, N'Asset', 4, N'1102', 1, 1
FROM dbo.Currencies c
WHERE c.Code <> N'IRR'
  AND NOT EXISTS (SELECT 1 FROM dbo.Accounts a WHERE a.Code = N'1102-' + RTRIM(c.Code));
GO
