-- 0002 - نسخه‌ی ۲: مشتریان مشترک و سرفصل چهارسطحی
-- گروه (۱) ← کل (۲) ← معین (۳) ← تفصیلی (۴)؛ حساب‌های موجود نگه داشته می‌شوند و فقط ساختار آن‌ها به‌روز می‌شود.
-- معاملات قدیمی به مشتری وصل می‌شوند (یک مشتری برای هر کد ملی؛ معاملات بدون کد ملی: یک مشتری برای هر نام).
-- این فایل در یک تراکنش اجرا می‌شود؛ اگر هر بخشی شکست بخورد، همه‌ی تغییرات برمی‌گردند.
GO

-- ===== ۱) ستون‌های سرفصل =====
ALTER TABLE dbo.Accounts ADD Level TINYINT NULL;
ALTER TABLE dbo.Accounts ADD ParentCode NVARCHAR(20) NULL;
ALTER TABLE dbo.Accounts ADD IsSystem BIT NOT NULL CONSTRAINT DF_Accounts_IsSystem DEFAULT (0);
GO

-- ===== ۲) جدول مشتریان =====
CREATE TABLE dbo.Customers
(
    Id            INT           IDENTITY(1,1) NOT NULL,
    CustomerCode  AS ('C' + RIGHT('0000000000' + CONVERT(VARCHAR(11), [Id]), 10)) PERSISTED NOT NULL,
    FullName      NVARCHAR(100) NOT NULL,
    NationalCode  NVARCHAR(20)  NULL,
    Phone         NVARCHAR(20)  NULL,
    Mobile        NVARCHAR(20)  NULL,
    Address       NVARCHAR(250) NULL,
    City          NVARCHAR(60)  NULL,
    Sheba1        NVARCHAR(26)  NULL,
    Sheba2        NVARCHAR(26)  NULL,
    CardNumber1   NVARCHAR(16)  NULL,
    CardNumber2   NVARCHAR(16)  NULL,
    Note          NVARCHAR(250) NULL,
    CreatedBy     INT           NOT NULL,
    CreatedAt     DATETIME2(0)  NOT NULL,
    UpdatedBy     INT           NOT NULL,
    UpdatedAt     DATETIME2(0)  NOT NULL,
    CONSTRAINT PK_Customers PRIMARY KEY (Id),
    CONSTRAINT UX_Customers_CustomerCode UNIQUE (CustomerCode),
    CONSTRAINT FK_Customers_CreatedBy FOREIGN KEY (CreatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT FK_Customers_UpdatedBy FOREIGN KEY (UpdatedBy) REFERENCES dbo.Users (Id),
    CONSTRAINT CK_Customers_FullName CHECK (LEN(LTRIM(RTRIM(FullName))) >= 2),
    CONSTRAINT CK_Customers_Sheba1 CHECK (Sheba1 IS NULL OR (LEN(Sheba1) = 26 AND LEFT(Sheba1, 2) = 'IR' AND SUBSTRING(Sheba1, 3, 24) NOT LIKE '%[^0-9]%')),
    CONSTRAINT CK_Customers_Sheba2 CHECK (Sheba2 IS NULL OR (LEN(Sheba2) = 26 AND LEFT(Sheba2, 2) = 'IR' AND SUBSTRING(Sheba2, 3, 24) NOT LIKE '%[^0-9]%')),
    CONSTRAINT CK_Customers_CardNumber1 CHECK (CardNumber1 IS NULL OR (LEN(CardNumber1) = 16 AND CardNumber1 NOT LIKE '%[^0-9]%')),
    CONSTRAINT CK_Customers_CardNumber2 CHECK (CardNumber2 IS NULL OR (LEN(CardNumber2) = 16 AND CardNumber2 NOT LIKE '%[^0-9]%'))
);
CREATE UNIQUE INDEX UX_Customers_NationalCode ON dbo.Customers (NationalCode) WHERE NationalCode IS NOT NULL;
GO

-- ===== ۳) ستون مشتری در معاملات (ابتدا NULL، سپس پرشدن از روی داده‌ها) =====
ALTER TABLE dbo.CurrencyTransactions ADD CustomerId INT NULL;
GO

-- ===== ۴) ساخت مشتری‌ها از معاملات قدیمی =====
;WITH src AS
(
    SELECT ct.Id,
           ct.CreatedBy,
           ct.OccurredAt,
           NULLIF(LTRIM(RTRIM(ct.NationalCode)), N'') AS NationalCode,
           CASE WHEN LEN(LTRIM(RTRIM(ISNULL(ct.CustomerName, N'')))) < 2 THEN N'مشتری نامشخص'
                ELSE LTRIM(RTRIM(ct.CustomerName)) END AS FullName
    FROM dbo.CurrencyTransactions AS ct
)
INSERT INTO dbo.Customers (FullName, NationalCode, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt)
SELECT x.FullName, x.NationalCode, x.CreatedBy, x.OccurredAt, x.CreatedBy, x.OccurredAt
FROM (SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.NationalCode ORDER BY s.Id DESC) AS rn
      FROM src AS s WHERE s.NationalCode IS NOT NULL) AS x
WHERE x.rn = 1
UNION ALL
SELECT y.FullName, NULL, y.CreatedBy, y.OccurredAt, y.CreatedBy, y.OccurredAt
FROM (SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.FullName ORDER BY s.Id) AS rn
      FROM src AS s WHERE s.NationalCode IS NULL) AS y
WHERE y.rn = 1;
GO

UPDATE ct SET ct.CustomerId = c.Id
FROM dbo.CurrencyTransactions AS ct
INNER JOIN dbo.Customers AS c ON c.NationalCode = NULLIF(LTRIM(RTRIM(ct.NationalCode)), N'')
WHERE ct.CustomerId IS NULL;

UPDATE ct SET ct.CustomerId = c.Id
FROM dbo.CurrencyTransactions AS ct
INNER JOIN dbo.Customers AS c ON c.NationalCode IS NULL
    AND c.FullName = CASE WHEN LEN(LTRIM(RTRIM(ISNULL(ct.CustomerName, N'')))) < 2 THEN N'مشتری نامشخص'
                          ELSE LTRIM(RTRIM(ct.CustomerName)) END
WHERE ct.CustomerId IS NULL;

IF EXISTS (SELECT 1 FROM dbo.CurrencyTransactions WHERE CustomerId IS NULL)
    THROW 50022, N'برخی معاملات قدیمی به مشتری وصل نشدند؛ مهاجرت لغو شد.', 1;
GO

ALTER TABLE dbo.CurrencyTransactions ALTER COLUMN CustomerId INT NOT NULL;
ALTER TABLE dbo.CurrencyTransactions ADD CONSTRAINT FK_CurrencyTransactions_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id);
GO

-- ===== ۵) سرفصل چهارسطحی =====
CREATE TABLE #AccountSeed
(
    Code        NVARCHAR(20)  NOT NULL PRIMARY KEY,
    Name        NVARCHAR(100) NOT NULL,
    AccountType NVARCHAR(20)  NOT NULL,
    Level       TINYINT       NOT NULL,
    ParentCode  NVARCHAR(20)  NULL,
    IsSystem    BIT           NOT NULL
);
INSERT INTO #AccountSeed (Code, Name, AccountType, Level, ParentCode, IsSystem) VALUES
(N'1', N'دارایی‌ها', N'Asset', 1, NULL, 0),
(N'10', N'دارایی‌های نقدی', N'Asset', 2, N'1', 0),
(N'1001', N'صندوق ریال', N'Asset', 3, N'10', 1),
(N'11', N'دارایی‌های ارزی', N'Asset', 2, N'1', 0),
(N'1101', N'موجودی ارز به تفکیک ارز', N'Asset', 3, N'11', 0),
(N'1101-USD', N'موجودی ارز - دلار آمریکا', N'Asset', 4, N'1101', 1),
(N'1101-EUR', N'موجودی ارز - یورو', N'Asset', 4, N'1101', 1),
(N'1101-GBP', N'موجودی ارز - پوند استرلینگ', N'Asset', 4, N'1101', 1),
(N'1101-AED', N'موجودی ارز - درهم امارات', N'Asset', 4, N'1101', 1),
(N'1101-TRY', N'موجودی ارز - لیر ترکیه', N'Asset', 4, N'1101', 1),
(N'3', N'سرمایه', N'Equity', 1, NULL, 0),
(N'30', N'سرمایه‌ی پایه', N'Equity', 2, N'3', 0),
(N'3001', N'سرمایه افتتاحیه', N'Equity', 3, N'30', 1),
(N'4', N'درآمدها', N'Revenue', 1, NULL, 0),
(N'40', N'درآمد معاملات ارزی', N'Revenue', 2, N'4', 0),
(N'4001', N'سود معاملات ارزی', N'Revenue', 3, N'40', 1),
(N'41', N'درآمد کارمزد', N'Revenue', 2, N'4', 0),
(N'4101', N'درآمد کارمزد معاملات', N'Revenue', 3, N'41', 1),
(N'5', N'زیان‌ها', N'Expense', 1, NULL, 0),
(N'50', N'زیان‌های معاملات ارزی', N'Expense', 2, N'5', 0),
(N'5001', N'زیان معاملات ارزی', N'Expense', 3, N'50', 1),
(N'6', N'هزینه‌های عملیاتی', N'Expense', 1, NULL, 0),
(N'60', N'هزینه‌های جاری', N'Expense', 2, N'6', 0),
(N'6001', N'هزینه‌های اداری و جاری', N'Expense', 3, N'60', 0),
(N'6002', N'هزینه‌ی اجاره', N'Expense', 3, N'60', 0),
(N'6003', N'سایر هزینه‌ها', N'Expense', 3, N'60', 0);

-- رده‌ها به ترتیب سطح نوشته می‌شوند تا والد همیشه پیش از فرزند وجود داشته باشد
INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem)
SELECT s.Code, s.Name, s.AccountType, s.Level, s.ParentCode, s.IsSystem
FROM #AccountSeed AS s
WHERE s.Level = 1 AND NOT EXISTS (SELECT 1 FROM dbo.Accounts AS a WHERE a.Code = s.Code);
INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem)
SELECT s.Code, s.Name, s.AccountType, s.Level, s.ParentCode, s.IsSystem
FROM #AccountSeed AS s
WHERE s.Level = 2 AND NOT EXISTS (SELECT 1 FROM dbo.Accounts AS a WHERE a.Code = s.Code);
INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem)
SELECT s.Code, s.Name, s.AccountType, s.Level, s.ParentCode, s.IsSystem
FROM #AccountSeed AS s
WHERE s.Level = 3 AND NOT EXISTS (SELECT 1 FROM dbo.Accounts AS a WHERE a.Code = s.Code);
INSERT INTO dbo.Accounts (Code, Name, AccountType, Level, ParentCode, IsSystem)
SELECT s.Code, s.Name, s.AccountType, s.Level, s.ParentCode, s.IsSystem
FROM #AccountSeed AS s
WHERE s.Level = 4 AND NOT EXISTS (SELECT 1 FROM dbo.Accounts AS a WHERE a.Code = s.Code);

-- حساب‌های موجود: فقط ساختار به‌روز می‌شود؛ نام، نوع و وضعیت فعال بودن کاربر حفظ می‌شود
UPDATE a SET a.Level = s.Level, a.ParentCode = s.ParentCode, a.IsSystem = s.IsSystem
FROM dbo.Accounts AS a
INNER JOIN #AccountSeed AS s ON s.Code = a.Code;

-- ارزهای اضافه‌شده در نسخه‌ی قبل (1101-<ccy>) زیر معین 1101 قرار می‌گیرند
UPDATE dbo.Accounts SET Level = 4, ParentCode = N'1101', IsSystem = 1
WHERE Level IS NULL AND Code LIKE N'1101-%';

DECLARE @msg NVARCHAR(2048);
IF EXISTS (SELECT 1 FROM dbo.Accounts WHERE Level IS NULL)
BEGIN
    SET @msg = N'حساب‌های بدون سطح باقی مانده‌اند؛ مهاجرت لغو شد: '
             + LEFT((SELECT STRING_AGG(Code, N'، ') FROM dbo.Accounts WHERE Level IS NULL), 1500);
    THROW 50021, @msg, 1;
END;

DROP TABLE #AccountSeed;

ALTER TABLE dbo.Accounts ALTER COLUMN Level TINYINT NOT NULL;
ALTER TABLE dbo.Accounts ADD CONSTRAINT FK_Accounts_Parent FOREIGN KEY (ParentCode) REFERENCES dbo.Accounts (Code);
ALTER TABLE dbo.Accounts ADD CONSTRAINT CK_Accounts_Level CHECK (Level BETWEEN 1 AND 4);
ALTER TABLE dbo.Accounts ADD CONSTRAINT CK_Accounts_Parent CHECK ((Level = 1 AND ParentCode IS NULL) OR (Level > 1 AND ParentCode IS NOT NULL));
GO
