/*
    AccountingSystem - اسکریپت ایجاد پایگاه داده (نسخه‌ی چند شعبه، کارمزد و ابطال معامله)
    برای SQL Server 2019 (سطح سازگاری 130، همان تنظیم تست‌شده در CI)

    اجرا (فقط روی یک پایگاه داده‌ی جدید؛ پایگاه داده‌ی نسخه‌ی قبلی با این اسکریپت ارتقا نمی‌یابد):
      - در SSMS با کاربری که دسترسی sysadmin دارد باز و Execute کنید، یا
      - از خط فرمان:  sqlcmd -S localhost -E -b -f 65001 -i AccountingSystem.sql
*/
SET NOCOUNT ON;
GO

IF DB_ID(N'AccountingSystem') IS NULL
    CREATE DATABASE [AccountingSystem];
GO

ALTER DATABASE [AccountingSystem] SET COMPATIBILITY_LEVEL = 130;
GO

USE [AccountingSystem];
GO

IF OBJECT_ID(N'dbo.Currencies', N'U') IS NOT NULL
    THROW 50000, N'پایگاه داده‌ی AccountingSystem قبلاً ساخته شده است. این اسکریپت فقط برای نصب تازه است.', 1;
GO

CREATE TABLE dbo.Branches
(
    Id        INT           NOT NULL IDENTITY(1,1),
    Code      NVARCHAR(10)  NOT NULL,
    Name      NVARCHAR(100) NOT NULL,
    CreatedAt DATETIME2(0)  NOT NULL CONSTRAINT DF_Branches_CreatedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_Branches PRIMARY KEY (Id),
    CONSTRAINT UQ_Branches_Code UNIQUE (Code),
    CONSTRAINT CK_Branches_Code CHECK (LEN(Code) BETWEEN 1 AND 10)
);
GO

CREATE TABLE dbo.Currencies
(
    Code          NCHAR(3)      NOT NULL,
    Name          NVARCHAR(100) NOT NULL,
    DecimalPlaces TINYINT       NOT NULL,
    IsActive      BIT           NOT NULL CONSTRAINT DF_Currencies_IsActive DEFAULT (1),
    CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_Currencies_CreatedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_Currencies PRIMARY KEY (Code),
    CONSTRAINT CK_Currencies_DecimalPlaces CHECK (DecimalPlaces BETWEEN 0 AND 4)
);
GO

-- مدیر (Admin) به همه‌ی شعبه‌ها دسترسی دارد و BranchId آن NULL است؛ کاربر صندوق حتماً شعبه دارد.
CREATE TABLE dbo.Users
(
    Id           INT IDENTITY(1,1) NOT NULL,
    Username     NVARCHAR(50)      NOT NULL,
    FullName     NVARCHAR(100)     NOT NULL,
    PasswordHash NVARCHAR(300)     NOT NULL,
    Role         NVARCHAR(20)      NOT NULL,
    BranchId     INT               NULL,
    IsActive     BIT               NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    CreatedAt    DATETIME2(0)      NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_Users PRIMARY KEY (Id),
    CONSTRAINT UQ_Users_Username UNIQUE (Username),
    CONSTRAINT CK_Users_Role CHECK (Role IN (N'Admin', N'Cashier')),
    CONSTRAINT FK_Users_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT CK_Users_Branch CHECK (Role = N'Admin' OR BranchId IS NOT NULL)
);
GO

CREATE TABLE dbo.Accounts
(
    Code        NVARCHAR(20)  NOT NULL,
    Name        NVARCHAR(100) NOT NULL,
    AccountType NVARCHAR(20)  NOT NULL,
    IsActive    BIT           NOT NULL CONSTRAINT DF_Accounts_IsActive DEFAULT (1),
    CONSTRAINT PK_Accounts PRIMARY KEY (Code),
    CONSTRAINT CK_Accounts_Type CHECK (AccountType IN (N'Asset', N'Liability', N'Equity', N'Revenue', N'Expense'))
);
GO

-- هر شعبه برای هر ارز یک صندوق جدا دارد.
CREATE TABLE dbo.CashBoxes
(
    Id           INT IDENTITY(1,1) NOT NULL,
    BranchId     INT               NOT NULL,
    CurrencyCode NCHAR(3)          NOT NULL,
    Name         NVARCHAR(100)     NOT NULL,
    Balance      DECIMAL(19,4)     NOT NULL CONSTRAINT DF_CashBoxes_Balance DEFAULT (0),
    UpdatedAt    DATETIME2(0)      NOT NULL CONSTRAINT DF_CashBoxes_UpdatedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_CashBoxes PRIMARY KEY (Id),
    CONSTRAINT UQ_CashBoxes_Branch_Currency UNIQUE (BranchId, CurrencyCode),
    CONSTRAINT FK_CashBoxes_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_CashBoxes_Currencies FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT CK_CashBoxes_Balance CHECK (Balance >= 0)
);
GO

CREATE TABLE dbo.CashMovements
(
    Id           BIGINT IDENTITY(1,1) NOT NULL,
    CashBoxId    INT                  NOT NULL,
    Amount       DECIMAL(19,4)        NOT NULL,
    BalanceAfter DECIMAL(19,4)        NOT NULL,
    RefType      NVARCHAR(20)         NOT NULL,
    RefId        BIGINT               NULL,
    Description  NVARCHAR(250)        NULL,
    OccurredAt   DATETIME2(0)         NOT NULL,
    CreatedBy    INT                  NOT NULL,
    CONSTRAINT PK_CashMovements PRIMARY KEY (Id),
    CONSTRAINT FK_CashMovements_CashBoxes FOREIGN KEY (CashBoxId) REFERENCES dbo.CashBoxes (Id),
    CONSTRAINT FK_CashMovements_Users FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_CashMovements_Amount CHECK (Amount <> 0),
    CONSTRAINT CK_CashMovements_RefType CHECK (RefType IN (N'TRADE', N'OPENING', N'VOID'))
);
CREATE INDEX IX_CashMovements_CashBox_OccurredAt ON dbo.CashMovements (CashBoxId, OccurredAt);
CREATE INDEX IX_CashMovements_CashBox_Id ON dbo.CashMovements (CashBoxId, Id) INCLUDE (RefType, RefId);
GO

-- بهای تمام‌شده‌ی موجودی هر ارز در هر شعبه. مقدار ارز همان موجودی صندوق ارز آن شعبه است.
CREATE TABLE dbo.CurrencyInventory
(
    BranchId     INT           NOT NULL,
    CurrencyCode NCHAR(3)      NOT NULL,
    TotalCostIrr DECIMAL(19,4) NOT NULL CONSTRAINT DF_CurrencyInventory_Cost DEFAULT (0),
    UpdatedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_CurrencyInventory_UpdatedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_CurrencyInventory PRIMARY KEY (BranchId, CurrencyCode),
    CONSTRAINT FK_CurrencyInventory_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_CurrencyInventory_Currencies FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT CK_CurrencyInventory_Cost CHECK (TotalCostIrr >= 0)
);
GO

-- نرخ‌ها برای هر شعبه جدا هستند؛ آخرین نرخ هر شعبه و ارز معتبر است.
CREATE TABLE dbo.ExchangeRates
(
    Id           BIGINT IDENTITY(1,1) NOT NULL,
    BranchId     INT                  NOT NULL,
    CurrencyCode NCHAR(3)             NOT NULL,
    BuyRateIrr   DECIMAL(19,4)        NOT NULL,
    SellRateIrr  DECIMAL(19,4)        NOT NULL,
    CreatedAt    DATETIME2(0)         NOT NULL,
    CreatedBy    INT                  NOT NULL,
    CONSTRAINT PK_ExchangeRates PRIMARY KEY (Id),
    CONSTRAINT FK_ExchangeRates_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_ExchangeRates_Currencies FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT FK_ExchangeRates_Users FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_ExchangeRates_Buy CHECK (BuyRateIrr > 0),
    CONSTRAINT CK_ExchangeRates_Spread CHECK (SellRateIrr >= BuyRateIrr)
);
CREATE INDEX IX_ExchangeRates_Branch_Currency_Id ON dbo.ExchangeRates (BranchId, CurrencyCode, Id);
GO

-- معاملات. کارمزد (FeeIrr) جدا از مبلغ ریالی ثبت می‌شود. ابطال با ستون‌های IsVoided و ... ثبت می‌شود و سطر حذف نمی‌شود.
CREATE TABLE dbo.CurrencyTransactions
(
    Id           BIGINT IDENTITY(1,1) NOT NULL,
    BranchId     INT                  NOT NULL,
    TradeType    NVARCHAR(10)         NOT NULL,
    CurrencyCode NCHAR(3)             NOT NULL,
    Amount       DECIMAL(19,4)        NOT NULL,
    Rate         DECIMAL(19,4)        NOT NULL,
    IrrAmount    DECIMAL(19,4)        NOT NULL,
    CostIrr      DECIMAL(19,4)        NOT NULL,
    ProfitIrr    DECIMAL(19,4)        NOT NULL,
    FeeIrr       DECIMAL(19,4)        NOT NULL CONSTRAINT DF_CurrencyTransactions_Fee DEFAULT (0),
    CustomerName NVARCHAR(100)        NULL,
    NationalCode NVARCHAR(20)         NULL,
    Note         NVARCHAR(250)        NULL,
    OccurredAt   DATETIME2(0)         NOT NULL,
    CreatedBy    INT                  NOT NULL,
    IsVoided     BIT                  NOT NULL CONSTRAINT DF_CurrencyTransactions_IsVoided DEFAULT (0),
    VoidedAt     DATETIME2(0)         NULL,
    VoidedBy     INT                  NULL,
    VoidReason   NVARCHAR(250)        NULL,
    CONSTRAINT PK_CurrencyTransactions PRIMARY KEY (Id),
    CONSTRAINT FK_CurrencyTransactions_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_CurrencyTransactions_Currencies FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT FK_CurrencyTransactions_Users FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_CurrencyTransactions_VoidedBy FOREIGN KEY (VoidedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_CurrencyTransactions_Type CHECK (TradeType IN (N'BUY', N'SELL')),
    CONSTRAINT CK_CurrencyTransactions_Amount CHECK (Amount > 0),
    CONSTRAINT CK_CurrencyTransactions_Rate CHECK (Rate > 0),
    CONSTRAINT CK_CurrencyTransactions_Irr CHECK (IrrAmount > 0 AND CostIrr >= 0),
    CONSTRAINT CK_CurrencyTransactions_Fee CHECK (FeeIrr >= 0 AND (TradeType = N'SELL' OR FeeIrr < IrrAmount)),
    CONSTRAINT CK_CurrencyTransactions_Void CHECK (
        (IsVoided = 0 AND VoidedAt IS NULL AND VoidedBy IS NULL AND VoidReason IS NULL)
        OR (IsVoided = 1 AND VoidedAt IS NOT NULL AND VoidedBy IS NOT NULL AND VoidReason IS NOT NULL))
);
CREATE INDEX IX_CurrencyTransactions_Branch_OccurredAt ON dbo.CurrencyTransactions (BranchId, OccurredAt);
GO

CREATE TABLE dbo.JournalEntries
(
    Id          BIGINT IDENTITY(1,1) NOT NULL,
    BranchId    INT                  NOT NULL,
    OccurredAt  DATETIME2(0)         NOT NULL,
    Description NVARCHAR(250)        NOT NULL,
    SourceType  NVARCHAR(20)         NOT NULL,
    SourceId    BIGINT               NULL,
    CreatedBy   INT                  NOT NULL,
    CreatedAt   DATETIME2(0)         NOT NULL CONSTRAINT DF_JournalEntries_CreatedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_JournalEntries PRIMARY KEY (Id),
    CONSTRAINT FK_JournalEntries_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_JournalEntries_Users FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_JournalEntries_Source CHECK (SourceType IN (N'TRADE', N'OPENING', N'VOID'))
);
CREATE INDEX IX_JournalEntries_Branch_OccurredAt ON dbo.JournalEntries (BranchId, OccurredAt);
GO

CREATE TABLE dbo.JournalLines
(
    Id             BIGINT IDENTITY(1,1) NOT NULL,
    JournalEntryId BIGINT               NOT NULL,
    LineNumber     INT                  NOT NULL,
    AccountCode    NVARCHAR(20)         NOT NULL,
    Debit          DECIMAL(19,4)        NOT NULL,
    Credit         DECIMAL(19,4)        NOT NULL,
    CONSTRAINT PK_JournalLines PRIMARY KEY (Id),
    CONSTRAINT UQ_JournalLines_EntryLine UNIQUE (JournalEntryId, LineNumber),
    CONSTRAINT FK_JournalLines_JournalEntries FOREIGN KEY (JournalEntryId) REFERENCES dbo.JournalEntries (Id),
    CONSTRAINT FK_JournalLines_Accounts FOREIGN KEY (AccountCode) REFERENCES dbo.Accounts (Code),
    CONSTRAINT CK_JournalLines_OneSide CHECK ((Debit > 0 AND Credit = 0) OR (Debit = 0 AND Credit > 0))
);
GO

-- داده‌های اولیه
INSERT INTO dbo.Branches (Code, Name) VALUES (N'MAIN', N'شعبه‌ی مرکزی');
GO

INSERT INTO dbo.Currencies (Code, Name, DecimalPlaces) VALUES
(N'IRR', N'ریال ایران', 0),
(N'USD', N'دلار آمریکا', 2),
(N'EUR', N'یورو', 2),
(N'GBP', N'پوند استرلینگ', 2),
(N'AED', N'درهم امارات', 2),
(N'TRY', N'لیر ترکیه', 2);
GO

INSERT INTO dbo.Accounts (Code, Name, AccountType) VALUES
(N'1001', N'صندوق ریال', N'Asset'),
(N'1101-USD', N'موجودی ارز - دلار آمریکا', N'Asset'),
(N'1101-EUR', N'موجودی ارز - یورو', N'Asset'),
(N'1101-GBP', N'موجودی ارز - پوند استرلینگ', N'Asset'),
(N'1101-AED', N'موجودی ارز - درهم امارات', N'Asset'),
(N'1101-TRY', N'موجودی ارز - لیر ترکیه', N'Asset'),
(N'3001', N'سرمایه افتتاحیه', N'Equity'),
(N'4001', N'سود معاملات ارزی', N'Revenue'),
(N'4101', N'درآمد کارمزد معاملات', N'Revenue'),
(N'5001', N'زیان معاملات ارزی', N'Expense');
GO

INSERT INTO dbo.CashBoxes (BranchId, CurrencyCode, Name)
SELECT b.Id, c.Code, N'صندوق ' + c.Name FROM dbo.Branches b CROSS JOIN dbo.Currencies c;
GO

INSERT INTO dbo.CurrencyInventory (BranchId, CurrencyCode)
SELECT b.Id, c.Code FROM dbo.Branches b CROSS JOIN dbo.Currencies c WHERE c.Code <> N'IRR';
GO
