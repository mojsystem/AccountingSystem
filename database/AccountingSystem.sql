/*
    AccountingSystem - اسکریپت ایجاد پایگاه داده (نسخه‌ی چند شعبه، کارمزد، ابطال و ویرایش اسناد)
    برای SQL Server 2019 (سطح سازگاری 150)

    اجرا (فقط روی یک پایگاه داده‌ی جدید؛ پایگاه داده‌ی نسخه‌ی قبلی با این اسکریپت ارتقا نمی‌یابد):
      - در SSMS با کاربری که دسترسی sysadmin دارد باز و Execute کنید، یا
      - از خط فرمان:  sqlcmd -S localhost -E -b -f 65001 -i AccountingSystem.sql
*/
SET NOCOUNT ON;
GO

IF DB_ID(N'AccountingSystem') IS NULL
    CREATE DATABASE [AccountingSystem];
GO

ALTER DATABASE [AccountingSystem] SET COMPATIBILITY_LEVEL = 150;
GO

USE [AccountingSystem];
GO

IF OBJECT_ID(N'dbo.Currencies', N'U') IS NOT NULL
    THROW 50000, N'پایگاه داده‌ی AccountingSystem قبلاً ساخته شده است. این اسکریپت فقط برای نصب تازه است.', 1;
GO

-- ترتیب ثابت همه‌ی اسناد با زمان یکسان. با هر سند جدید مقدار بعدی گرفته می‌شود.
CREATE SEQUENCE dbo.LedgerSeq AS BIGINT START WITH 1 INCREMENT BY 1;
GO

CREATE TABLE dbo.Branches
(
    Id            INT           NOT NULL IDENTITY(1,1),
    Code          NVARCHAR(10)  NOT NULL,
    Name          NVARCHAR(100) NOT NULL,
    CreatedAt     DATETIME2(0)  NOT NULL CONSTRAINT DF_Branches_CreatedAt DEFAULT (SYSDATETIME()),
    -- شمارنده‌ی تغییرات دفتر شعبه؛ هر ثبت یا ابطال آن را یک واحد بالا می‌برد (کنترل همزمانی).
    LedgerVersion BIGINT        NOT NULL CONSTRAINT DF_Branches_LedgerVersion DEFAULT (0),
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

-- مشتریان صرافی: فهرست مشترک همه‌ی شعبه‌ها. هر معامله به یک مشتری وصل است و نام/کد ملی آن را نیز نگه می‌دارد.
CREATE TABLE dbo.Customers
(
    Id           INT           IDENTITY(1,1) NOT NULL,
    FullName     NVARCHAR(100) NOT NULL,
    NationalCode NVARCHAR(20)  NULL,
    Phone        NVARCHAR(20)  NULL,
    Address      NVARCHAR(250) NULL,
    Note         NVARCHAR(250) NULL,
    CreatedBy    INT           NOT NULL,
    CreatedAt    DATETIME2(0)  NOT NULL,
    UpdatedBy    INT           NOT NULL,
    UpdatedAt    DATETIME2(0)  NOT NULL,
    CONSTRAINT PK_Customers PRIMARY KEY (Id),
    CONSTRAINT FK_Customers_CreatedBy FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_Customers_UpdatedBy FOREIGN KEY (UpdatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_Customers_FullName CHECK (LEN(LTRIM(RTRIM(FullName))) >= 2)
);
CREATE UNIQUE INDEX UX_Customers_NationalCode ON dbo.Customers (NationalCode) WHERE NationalCode IS NOT NULL;
GO

CREATE TABLE dbo.Accounts
(
    Code        NVARCHAR(20)  NOT NULL,
    Name        NVARCHAR(100) NOT NULL,
    AccountType NVARCHAR(20)  NOT NULL,
    Level       TINYINT       NOT NULL,
    ParentCode  NVARCHAR(20)  NULL,
    IsSystem    BIT           NOT NULL CONSTRAINT DF_Accounts_IsSystem DEFAULT (0),
    IsActive    BIT           NOT NULL CONSTRAINT DF_Accounts_IsActive DEFAULT (1),
    CONSTRAINT PK_Accounts PRIMARY KEY (Code),
    CONSTRAINT FK_Accounts_Parent FOREIGN KEY (ParentCode) REFERENCES dbo.Accounts (Code),
    CONSTRAINT CK_Accounts_Type CHECK (AccountType IN (N'Asset', N'Liability', N'Equity', N'Revenue', N'Expense')),
    CONSTRAINT CK_Accounts_Level CHECK (Level BETWEEN 1 AND 4),
    CONSTRAINT CK_Accounts_Parent CHECK ((Level = 1 AND ParentCode IS NULL) OR (Level > 1 AND ParentCode IS NOT NULL))
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
    CONSTRAINT CK_CashMovements_RefType CHECK (RefType IN (N'TRADE', N'OPENING', N'VOID', N'MANUAL'))
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
    CustomerId   INT                  NOT NULL CONSTRAINT FK_CurrencyTransactions_Customers REFERENCES dbo.Customers (Id),
    CustomerName NVARCHAR(100)        NULL,
    NationalCode NVARCHAR(20)         NULL,
    Note         NVARCHAR(250)        NULL,
    OccurredAt   DATETIME2(0)         NOT NULL,
    CreatedBy    INT                  NOT NULL,
    IsVoided     BIT                  NOT NULL CONSTRAINT DF_CurrencyTransactions_IsVoided DEFAULT (0),
    VoidedAt     DATETIME2(0)         NULL,
    VoidedBy     INT                  NULL,
    VoidReason   NVARCHAR(250)        NULL,
    Seq          BIGINT               NOT NULL CONSTRAINT DF_CurrencyTransactions_Seq DEFAULT (NEXT VALUE FOR dbo.LedgerSeq),
    -- معامله‌ای که این معامله نسخه‌ی اصلاحی آن است (ویرایش مالی با ابطال نسخه‌ی قبلی انجام می‌شود).
    ReplacesId   BIGINT               NULL,
    CONSTRAINT PK_CurrencyTransactions PRIMARY KEY (Id),
    CONSTRAINT FK_CurrencyTransactions_Replaces FOREIGN KEY (ReplacesId) REFERENCES dbo.CurrencyTransactions (Id),
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

-- موجودی افتتاحیه‌ی ریال یا ارز هر شعبه. ابطال و ویرایش مثل معاملات با بازمحاسبه‌ی تاریخچه انجام می‌شود.
CREATE TABLE dbo.OpeningBalances
(
    Id           BIGINT IDENTITY(1,1) NOT NULL,
    BranchId     INT                  NOT NULL,
    CurrencyCode NCHAR(3)             NOT NULL,
    Quantity     DECIMAL(19,4)        NOT NULL,
    RateIrr      DECIMAL(19,4)        NULL,
    CostIrr      DECIMAL(19,4)        NOT NULL,
    OccurredAt   DATETIME2(0)         NOT NULL,
    Seq          BIGINT               NOT NULL CONSTRAINT DF_OpeningBalances_Seq DEFAULT (NEXT VALUE FOR dbo.LedgerSeq),
    CreatedBy    INT                  NOT NULL,
    CreatedAt    DATETIME2(0)         NOT NULL CONSTRAINT DF_OpeningBalances_CreatedAt DEFAULT (SYSDATETIME()),
    IsVoided     BIT                  NOT NULL CONSTRAINT DF_OpeningBalances_IsVoided DEFAULT (0),
    VoidedAt     DATETIME2(0)         NULL,
    VoidedBy     INT                  NULL,
    VoidReason   NVARCHAR(250)        NULL,
    ReplacesId   BIGINT               NULL,
    CONSTRAINT PK_OpeningBalances PRIMARY KEY (Id),
    CONSTRAINT FK_OpeningBalances_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_OpeningBalances_Currencies FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT FK_OpeningBalances_CreatedBy FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_OpeningBalances_VoidedBy FOREIGN KEY (VoidedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_OpeningBalances_Replaces FOREIGN KEY (ReplacesId) REFERENCES dbo.OpeningBalances (Id),
    CONSTRAINT CK_OpeningBalances_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_OpeningBalances_Cost CHECK (CostIrr > 0),
    CONSTRAINT CK_OpeningBalances_Void CHECK (
        (IsVoided = 0 AND VoidedAt IS NULL AND VoidedBy IS NULL AND VoidReason IS NULL)
        OR (IsVoided = 1 AND VoidedAt IS NOT NULL AND VoidedBy IS NOT NULL AND VoidReason IS NOT NULL))
);
CREATE INDEX IX_OpeningBalances_Branch_OccurredAt ON dbo.OpeningBalances (BranchId, OccurredAt);
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
    Seq         BIGINT               NOT NULL CONSTRAINT DF_JournalEntries_Seq DEFAULT (NEXT VALUE FOR dbo.LedgerSeq),
    -- سندی که باطل شده است (معامله، افتتاحیه یا سند دستی) یا سندی که این سند جایگزین آن است.
    IsVoided    BIT                  NOT NULL CONSTRAINT DF_JournalEntries_IsVoided DEFAULT (0),
    ReplacesId  BIGINT               NULL,
    CONSTRAINT PK_JournalEntries PRIMARY KEY (Id),
    CONSTRAINT FK_JournalEntries_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_JournalEntries_Users FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_JournalEntries_Replaces FOREIGN KEY (ReplacesId) REFERENCES dbo.JournalEntries (Id),
    CONSTRAINT CK_JournalEntries_Source CHECK (SourceType IN (N'TRADE', N'OPENING', N'VOID', N'ADJUST', N'MANUAL'))
);
CREATE INDEX IX_JournalEntries_Branch_OccurredAt ON dbo.JournalEntries (BranchId, OccurredAt);
GO

-- سابقه‌ی همه‌ی ثبت‌ها، ابطال‌ها و ویرایش‌ها (چه کسی، چه زمانی، چه تغییری).
CREATE TABLE dbo.AuditLog
(
    Id         BIGINT IDENTITY(1,1) NOT NULL,
    OccurredAt DATETIME2(0)         NOT NULL,
    UserId     INT                  NOT NULL,
    Action     NVARCHAR(50)         NOT NULL,
    EntityType NVARCHAR(20)         NOT NULL,
    EntityId   BIGINT               NULL,
    Details    NVARCHAR(MAX)        NULL,
    CONSTRAINT PK_AuditLog PRIMARY KEY (Id),
    CONSTRAINT FK_AuditLog_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
);
CREATE INDEX IX_AuditLog_Entity ON dbo.AuditLog (EntityType, EntityId);
GO

-- نقش‌های دسترسی. هر شعبه نقش‌های خودش را دارد؛ سه نقش پیش‌فرض برای هر شعبه ساخته می‌شود.
CREATE TABLE dbo.AccessRoles
(
    Id        INT IDENTITY(1,1) NOT NULL,
    BranchId  INT               NOT NULL,
    Name      NVARCHAR(60)      NOT NULL,
    CreatedBy INT               NULL,
    CreatedAt DATETIME2(0)      NOT NULL CONSTRAINT DF_AccessRoles_CreatedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_AccessRoles PRIMARY KEY (Id),
    CONSTRAINT UQ_AccessRoles_Branch_Name UNIQUE (BranchId, Name),
    CONSTRAINT UQ_AccessRoles_Id_Branch UNIQUE (Id, BranchId),
    CONSTRAINT FK_AccessRoles_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_AccessRoles_CreatedBy FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_AccessRoles_Name CHECK (LEN(LTRIM(RTRIM(Name))) >= 2)
);
GO

-- کارهایی که یک نقش مجاز است انجام دهد (مجموعه‌ی کارهای نقش؛ هیچ استثنای جدا برای کاربر وجود ندارد).
CREATE TABLE dbo.AccessRolePermissions
(
    RoleId     INT          NOT NULL,
    Permission NVARCHAR(40) NOT NULL,
    CONSTRAINT PK_AccessRolePermissions PRIMARY KEY (RoleId, Permission),
    CONSTRAINT FK_AccessRolePermissions_Roles FOREIGN KEY (RoleId) REFERENCES dbo.AccessRoles (Id),
    CONSTRAINT CK_AccessRolePermissions_Permission CHECK (Permission IN (
        N'TRADE_RECORD', N'TRADE_EDIT', N'TRADE_VOID',
        N'OPENING_CREATE', N'OPENING_EDIT', N'OPENING_VOID',
        N'MANUAL_CREATE', N'MANUAL_EDIT', N'MANUAL_VOID',
        N'RATE_SET'))
);
GO

-- عضویت کاربر در هر شعبه با یک نقش. کاربر می‌تواند در چند شعبه عضو باشد و در هر شعبه نقش متفاوتی داشته باشد.
-- شعبه‌ی اصلی کاربر (Users.BranchId) باید یکی از همین عضویت‌ها باشد؛ این را برنامه کنترل می‌کند.
CREATE TABLE dbo.UserBranchRoles
(
    UserId    INT          NOT NULL,
    BranchId  INT          NOT NULL,
    RoleId    INT          NOT NULL,
    GrantedBy INT          NOT NULL,
    GrantedAt DATETIME2(0) NOT NULL CONSTRAINT DF_UserBranchRoles_GrantedAt DEFAULT (SYSDATETIME()),
    CONSTRAINT PK_UserBranchRoles PRIMARY KEY (UserId, BranchId),
    CONSTRAINT FK_UserBranchRoles_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_UserBranchRoles_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_UserBranchRoles_GrantedBy FOREIGN KEY (GrantedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_UserBranchRoles_Role_Branch FOREIGN KEY (RoleId, BranchId) REFERENCES dbo.AccessRoles (Id, BranchId)
);
CREATE INDEX IX_UserBranchRoles_Role ON dbo.UserBranchRoles (RoleId);
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

-- نقش‌های پیش‌فرض شعبه‌ی MAIN (همان فهرست RolePresets در برنامه).
DECLARE @MainBranchId INT = (SELECT Id FROM dbo.Branches WHERE Code = N'MAIN');
INSERT INTO dbo.AccessRoles (BranchId, Name) VALUES
    (@MainBranchId, N'حسابدار'),
    (@MainBranchId, N'مدیر شعبه'),
    (@MainBranchId, N'کاربر صندوق');
INSERT INTO dbo.AccessRolePermissions (RoleId, Permission)
SELECT r.Id, p.Permission
FROM dbo.AccessRoles r
JOIN (VALUES
    (N'حسابدار', N'TRADE_EDIT'), (N'حسابدار', N'TRADE_VOID'),
    (N'حسابدار', N'OPENING_EDIT'), (N'حسابدار', N'OPENING_VOID'),
    (N'حسابدار', N'MANUAL_CREATE'), (N'حسابدار', N'MANUAL_EDIT'), (N'حسابدار', N'MANUAL_VOID'),
    (N'مدیر شعبه', N'TRADE_RECORD'), (N'مدیر شعبه', N'TRADE_EDIT'), (N'مدیر شعبه', N'TRADE_VOID'),
    (N'مدیر شعبه', N'OPENING_CREATE'), (N'مدیر شعبه', N'OPENING_EDIT'), (N'مدیر شعبه', N'OPENING_VOID'),
    (N'مدیر شعبه', N'MANUAL_CREATE'), (N'مدیر شعبه', N'MANUAL_EDIT'), (N'مدیر شعبه', N'MANUAL_VOID'),
    (N'مدیر شعبه', N'RATE_SET'),
    (N'کاربر صندوق', N'TRADE_RECORD')
) AS p (RoleName, Permission) ON p.RoleName = r.Name
WHERE r.BranchId = @MainBranchId;
GO

INSERT INTO dbo.Currencies (Code, Name, DecimalPlaces) VALUES
(N'IRR', N'ریال ایران', 0),
(N'USD', N'دلار آمریکا', 2),
(N'EUR', N'یورو', 2),
(N'GBP', N'پوند استرلینگ', 2),
(N'AED', N'درهم امارات', 2),
(N'TRY', N'لیر ترکیه', 2);
GO

-- سرفصل چهارسطحی: گروه (۱) ← کل (۲) ← معین (۳) ← تفصیلی (۴). نوع حساب از گروه به زیرمجموعه‌ها به ارث می‌رسد.
-- حساب‌های IsSystem=1 پایه‌ی موتور معاملات و سندهای خودکارند: کد، پدر، نوع و وضعیت آن‌ها قابل تغییر نیست؛ فقط نام قابل ویرایش است.
INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem) VALUES
(N'1',          N'دارایی‌ها',                       N'Asset',   1, NULL,     0),
(N'10',         N'دارایی‌های نقدی',                 N'Asset',   2, N'1',     0),
(N'1001',       N'صندوق ریال',                      N'Asset',   3, N'10',    1),
(N'11',         N'دارایی‌های ارزی',                 N'Asset',   2, N'1',     0),
(N'1101',       N'موجودی ارز به تفکیک ارز',         N'Asset',   3, N'11',    0),
(N'1101-USD',   N'موجودی ارز - دلار آمریکا',        N'Asset',   4, N'1101',  1),
(N'1101-EUR',   N'موجودی ارز - یورو',               N'Asset',   4, N'1101',  1),
(N'1101-GBP',   N'موجودی ارز - پوند استرلینگ',      N'Asset',   4, N'1101',  1),
(N'1101-AED',   N'موجودی ارز - درهم امارات',        N'Asset',   4, N'1101',  1),
(N'1101-TRY',   N'موجودی ارز - لیر ترکیه',          N'Asset',   4, N'1101',  1),
(N'3',          N'سرمایه',                          N'Equity',  1, NULL,     0),
(N'30',         N'سرمایه‌ی پایه',                    N'Equity',  2, N'3',     0),
(N'3001',       N'سرمایه افتتاحیه',                 N'Equity',  3, N'30',    1),
(N'4',          N'درآمدها',                         N'Revenue', 1, NULL,     0),
(N'40',         N'درآمد معاملات ارزی',              N'Revenue', 2, N'4',     0),
(N'4001',       N'سود معاملات ارزی',                N'Revenue', 3, N'40',    1),
(N'41',         N'درآمد کارمزد',                    N'Revenue', 2, N'4',     0),
(N'4101',       N'درآمد کارمزد معاملات',            N'Revenue', 3, N'41',    1),
(N'5',          N'زیان‌ها',                         N'Expense', 1, NULL,     0),
(N'50',         N'زیان‌های معاملات ارزی',           N'Expense', 2, N'5',     0),
(N'5001',       N'زیان معاملات ارزی',               N'Expense', 3, N'50',    1),
(N'6',          N'هزینه‌های عملیاتی',               N'Expense', 1, NULL,     0),
(N'60',         N'هزینه‌های جاری',                  N'Expense', 2, N'6',     0),
(N'6001',       N'هزینه‌های اداری و جاری',          N'Expense', 3, N'60',    0),
(N'6002',       N'هزینه‌ی اجاره',                   N'Expense', 3, N'60',    0),
(N'6003',       N'سایر هزینه‌ها',                   N'Expense', 3, N'60',    0);
GO

INSERT INTO dbo.CashBoxes (BranchId, CurrencyCode, Name)
SELECT b.Id, c.Code, N'صندوق ' + c.Name FROM dbo.Branches b CROSS JOIN dbo.Currencies c;
GO

INSERT INTO dbo.CurrencyInventory (BranchId, CurrencyCode)
SELECT b.Id, c.Code FROM dbo.Branches b CROSS JOIN dbo.Currencies c WHERE c.Code <> N'IRR';
GO
