using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data.Tests;

/// <summary>
/// تصویر ساختار و داده‌ی مبنا (سرفصل، واحدها، شعبه‌ها، نقش‌ها) یک بانک، به‌صورت خطوط متنی.
/// دو بانک که ساختار یکسان دارند، تصویرهای یکسان می‌دهند (جدول SchemaVersion جزو تصویر نیست).
/// </summary>
/// <remarks>
/// هیچ عبارتی در SQL به هم چسبانده نمی‌شود: هر ردیف ستون‌های جدا برمی‌گرداند و خط متنی در C# ساخته می‌شود.
/// دلیل: نام‌های کاتالوگ (sysname) و متن تعریف‌ها (definition) هر کدام collation جدا دارند و ترکیب مستقیم آن‌ها در SQL خطای collation می‌دهد.
/// </remarks>
internal static class SchemaSnapshot
{
    private static readonly (string Label, string Sql)[] Queries =
    {
        ("COL", @"SELECT s.name, t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, cc.definition, dc.name, dc.definition
FROM sys.columns AS c
INNER JOIN sys.tables AS t ON t.object_id = c.object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE t.name <> N'SchemaVersion';"),

        ("CK", @"SELECT s.name, t.name, k.name, k.definition
FROM sys.check_constraints AS k
INNER JOIN sys.tables AS t ON t.object_id = k.parent_object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE t.name <> N'SchemaVersion';"),

        ("FK", @"SELECT OBJECT_NAME(f.parent_object_id), f.name, OBJECT_NAME(f.referenced_object_id), pc.name, rc.name, f.delete_referential_action_desc, f.update_referential_action_desc
FROM sys.foreign_keys AS f
INNER JOIN sys.foreign_key_columns AS fk ON fk.constraint_object_id = f.object_id
INNER JOIN sys.columns AS pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fk.parent_column_id
INNER JOIN sys.columns AS rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fk.referenced_column_id
WHERE OBJECT_NAME(f.parent_object_id) <> N'SchemaVersion';"),

        ("IX", @"SELECT OBJECT_NAME(i.object_id), i.name, i.type_desc, i.is_unique, i.is_primary_key, i.filter_definition,
    (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.index_columns AS ic INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0),
    (SELECT STRING_AGG(CONVERT(NVARCHAR(1), ic.is_descending_key), N',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.index_columns AS ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0),
    (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY c.name) FROM sys.index_columns AS ic INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1)
FROM sys.indexes AS i
WHERE i.index_id > 0 AND i.is_hypothetical = 0 AND OBJECTPROPERTY(i.object_id, N'IsUserTable') = 1 AND OBJECT_NAME(i.object_id) <> N'SchemaVersion';"),

        ("SEQ", @"SELECT s.name, TYPE_NAME(s.system_type_id), CONVERT(NVARCHAR(40), s.start_value), CONVERT(NVARCHAR(40), s.increment) FROM sys.sequences AS s;"),

        ("ACC", @"SELECT a.Code, a.Name, a.AccountType, a.Level, a.ParentCode, a.IsSystem, a.IsActive FROM dbo.Accounts AS a;"),

        ("CUR", @"SELECT c.Code, c.Name, c.DecimalPlaces FROM dbo.Currencies AS c;"),

        ("BR", @"SELECT b.Code, b.Name FROM dbo.Branches AS b;"),

        ("RP", @"SELECT b.Code, r.Name, p.Permission FROM dbo.AccessRolePermissions AS p INNER JOIN dbo.AccessRoles AS r ON r.Id = p.RoleId INNER JOIN dbo.Branches AS b ON b.Id = r.BranchId;"),
    };

    public static async Task<List<string>> ReadAsync(string connectionString)
    {
        var lines = new List<string>();
        await using var conn = await SqlTestDb.OpenWithRetryAsync(connectionString);
        foreach (var (label, sql) in Queries)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var fields = new string[reader.FieldCount];
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = reader.IsDBNull(i) ? "-" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                }

                lines.Add(label + "|" + string.Join("|", fields));
            }
        }

        return lines;
    }
}
