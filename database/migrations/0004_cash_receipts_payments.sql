-- 0004 - رسید دریافت و پرداخت مستقل از معامله، با ثبت دوطرفه روی صندوق و حساب مشتری
GO

CREATE TABLE dbo.CashTransactions
(
    Id           BIGINT         NOT NULL IDENTITY(1,1),
    BranchId     INT            NOT NULL,
    Direction    NVARCHAR(8)    NOT NULL,
    CustomerId   INT            NOT NULL,
    CurrencyCode NCHAR(3)       NOT NULL,
    Amount       DECIMAL(19,4)  NOT NULL,
    RateMode     NVARCHAR(12)   NOT NULL,
    RateIrr      DECIMAL(19,4)  NOT NULL,
    IrrAmount    DECIMAL(19,4)  NOT NULL,
    CostIrr      DECIMAL(19,4)  NOT NULL CONSTRAINT DF_CashTransactions_CostIrr DEFAULT (0),
    ProfitIrr    DECIMAL(19,4)  NOT NULL CONSTRAINT DF_CashTransactions_ProfitIrr DEFAULT (0),
    Note         NVARCHAR(250)  NULL,
    OccurredAt   DATETIME2(0)   NOT NULL,
    CreatedBy    INT            NOT NULL,
    CreatedAt    DATETIME2(0)   NOT NULL CONSTRAINT DF_CashTransactions_CreatedAt DEFAULT (SYSDATETIME()),
    IsVoided     BIT            NOT NULL CONSTRAINT DF_CashTransactions_IsVoided DEFAULT (0),
    VoidedAt     DATETIME2(0)   NULL,
    VoidedBy     INT            NULL,
    VoidReason   NVARCHAR(250)  NULL,
    Seq          BIGINT         NOT NULL CONSTRAINT DF_CashTransactions_Seq DEFAULT (NEXT VALUE FOR dbo.LedgerSeq),
    ReplacesId   BIGINT         NULL,
    CONSTRAINT PK_CashTransactions PRIMARY KEY (Id),
    CONSTRAINT FK_CashTransactions_Branches FOREIGN KEY (BranchId) REFERENCES dbo.Branches (Id),
    CONSTRAINT FK_CashTransactions_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id),
    CONSTRAINT FK_CashTransactions_Currencies FOREIGN KEY (CurrencyCode) REFERENCES dbo.Currencies (Code),
    CONSTRAINT FK_CashTransactions_CreatedBy FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_CashTransactions_VoidedBy FOREIGN KEY (VoidedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_CashTransactions_Replaces FOREIGN KEY (ReplacesId) REFERENCES dbo.CashTransactions (Id),
    CONSTRAINT CK_CashTransactions_Direction CHECK (Direction IN (N'RECEIVE', N'PAY')),
    CONSTRAINT CK_CashTransactions_RateMode CHECK (RateMode IN (N'DIRECT', N'DERIVED')),
    CONSTRAINT CK_CashTransactions_Amount CHECK (Amount > 0 AND RateIrr > 0 AND IrrAmount > 0 AND CostIrr >= 0),
    CONSTRAINT CK_CashTransactions_Rate CHECK (CurrencyCode <> N'IRR' OR (RateIrr = 1 AND RateMode = N'DERIVED')),
    CONSTRAINT CK_CashTransactions_Void CHECK
    (
        (IsVoided = 0 AND VoidedAt IS NULL AND VoidedBy IS NULL AND VoidReason IS NULL)
        OR (IsVoided = 1 AND VoidedAt IS NOT NULL AND VoidedBy IS NOT NULL AND VoidReason IS NOT NULL)
    )
);
CREATE INDEX IX_CashTransactions_Branch_OccurredAt ON dbo.CashTransactions (BranchId, OccurredAt, Id);
CREATE INDEX IX_CashTransactions_Customer_OccurredAt ON dbo.CashTransactions (CustomerId, OccurredAt, Id);
GO

-- سندها و حرکت‌های صندوق جدید باید در محدودیت‌های همان جداول پذیرفته شوند.
ALTER TABLE dbo.JournalEntries DROP CONSTRAINT CK_JournalEntries_Source;
ALTER TABLE dbo.JournalEntries ADD CONSTRAINT CK_JournalEntries_Source CHECK
    (SourceType IN (N'TRADE', N'OPENING', N'VOID', N'ADJUST', N'MANUAL', N'CASH_RECEIPT', N'CASH_PAYMENT', N'CASH_ADJUST'));
ALTER TABLE dbo.CashMovements DROP CONSTRAINT CK_CashMovements_RefType;
ALTER TABLE dbo.CashMovements ADD CONSTRAINT CK_CashMovements_RefType CHECK
    (RefType IN (N'TRADE', N'OPENING', N'VOID', N'MANUAL', N'CASH_TRANSACTION'));
GO

-- نقش‌های پیش‌فرض موجود، وظایف تازه را می‌گیرند؛ نقش‌های سفارشی بدون تغییر می‌مانند.
ALTER TABLE dbo.AccessRolePermissions DROP CONSTRAINT CK_AccessRolePermissions_Permission;
ALTER TABLE dbo.AccessRolePermissions ADD CONSTRAINT CK_AccessRolePermissions_Permission CHECK (Permission IN
(
    N'TRADE_RECORD', N'TRADE_EDIT', N'TRADE_VOID',
    N'OPENING_CREATE', N'OPENING_EDIT', N'OPENING_VOID',
    N'MANUAL_CREATE', N'MANUAL_EDIT', N'MANUAL_VOID',
    N'RATE_SET',
    N'CASH_TRANSACTION_CREATE', N'CASH_TRANSACTION_EDIT', N'CASH_TRANSACTION_VOID'
));
GO

INSERT INTO dbo.AccessRolePermissions (RoleId, Permission)
SELECT r.Id, p.Permission
FROM dbo.AccessRoles r
JOIN (VALUES
    (N'حسابدار', N'CASH_TRANSACTION_CREATE'),
    (N'حسابدار', N'CASH_TRANSACTION_EDIT'),
    (N'حسابدار', N'CASH_TRANSACTION_VOID'),
    (N'مدیر شعبه', N'CASH_TRANSACTION_CREATE'),
    (N'مدیر شعبه', N'CASH_TRANSACTION_EDIT'),
    (N'مدیر شعبه', N'CASH_TRANSACTION_VOID'),
    (N'کاربر صندوق', N'CASH_TRANSACTION_CREATE')
) AS p (RoleName, Permission) ON p.RoleName = r.Name
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.AccessRolePermissions existing
    WHERE existing.RoleId = r.Id AND existing.Permission = p.Permission
);
GO
