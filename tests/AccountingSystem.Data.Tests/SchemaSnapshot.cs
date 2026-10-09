using Microsoft.Data.SqlClient;

namespace AccountingSystem.Data.Tests;

/// <summary>
/// تصویر ساختار و داده‌ی سرفصل/مبناهای یک بانک، به‌صورت خطوط مرتب‌نشدنی.
/// دو بانک که ساختار یکسان دارند، تصویرهای یکسان می‌دهند (جدول SchemaVersion جزو تصویر نیست).
/// </summary>
internal static class SchemaSnapshot
{
    private static readonly string[] Queries =
    {
        @"SELECT N'COL|' + s.name + N'.' + t.name + N'.' + c.name + N'|' + ty.name + N'|' + CONVERT(NVARCHAR(10), c.max_length) + N'|' + CONVERT(NVARCHAR(10), c.precision) + N'|' + CONVERT(NVARCHAR(10), c.scale) + N'|null=' + CONVERT(NVARCHAR(1), c.is_nullable) + N'|identity=' + CONVERT(NVARCHAR(1), c.is_identity) + N'|computed=' + ISNULL(cc.definition, N'-') + N'|default=' + ISNULL(dc.name, N'-') + N':' + ISNULL(dc.definition, N'-')
FROM sys.columns AS c
INNER JOIN sys.tables AS t ON t.object_id = c.object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE t.name <> N'SchemaVersion';",

        @"SELECT N'CK|' + s.name + N'.' + t.name + N'|' + k.name + N'|' + k.definition
FROM sys.check_constraints AS k
INNER JOIN sys.tables AS t ON t.object_id = k.parent_object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE t.name <> N'SchemaVersion';",

        @"SELECT N'FK|' + OBJECT_NAME(f.parent_object_id) + N'|' + f.name + N'|' + OBJECT_NAME(f.referenced_object_id) + N'|' + pc.name + N'>' + rc.name + N'|' + f.delete_referential_action_desc + N'|' + f.update_referential_action_desc
FROM sys.foreign_keys AS f
INNER JOIN sys.foreign_key_columns AS fk ON fk.constraint_object_id = f.object_id
INNER JOIN sys.columns AS pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fk.parent_column_id
INNER JOIN sys.columns AS rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fk.referenced_column_id
WHERE OBJECT_NAME(f.parent_object_id) <> N'SchemaVersion';",

        @"SELECT N'IX|' + OBJECT_NAME(i.object_id) + N'|' + i.name + N'|' + i.type_desc + N'|unique=' + CONVERT(NVARCHAR(1), i.is_unique) + N'|pk=' + CONVERT(NVARCHAR(1), i.is_primary_key) + N'|filter=' + ISNULL(i.filter_definition, N'-')
 + N'|keys=' + ISNULL((SELECT STRING_AGG(c.name + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END, N',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.index_columns AS ic INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0), N'-')
 + N'|include=' + ISNULL((SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY c.name) FROM sys.index_columns AS ic INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1), N'-')
FROM sys.indexes AS i
WHERE i.index_id > 0 AND i.is_hypothetical = 0 AND OBJECTPROPERTY(i.object_id, N'IsUserTable') = 1 AND OBJECT_NAME(i.object_id) <> N'SchemaVersion';",

        @"SELECT N'SEQ|' + s.name + N'|' + TYPE_NAME(s.system_type_id) + N'|' + CONVERT(NVARCHAR(40), s.start_value) + N'|' + CONVERT(NVARCHAR(40), s.increment) FROM sys.sequences AS s;",

        @"SELECT N'ACC|' + a.Code + N'|' + a.Name + N'|' + a.AccountType + N'|' + CONVERT(NVARCHAR(3), a.Level) + N'|' + ISNULL(a.ParentCode, N'-') + N'|sys=' + CONVERT(NVARCHAR(1), a.IsSystem) + N'|active=' + CONVERT(NVARCHAR(1), a.IsActive) FROM dbo.Accounts AS a;",

        @"SELECT N'CUR|' + c.Code + N'|' + c.Name + N'|' + CONVERT(NVARCHAR(3), c.DecimalPlaces) FROM dbo.Currencies AS c;",

        @"SELECT N'BR|' + b.Code + N'|' + b.Name FROM dbo.Branches AS b;",

        @"SELECT N'RP|' + b.Code + N'|' + r.Name + N'|' + p.Permission FROM dbo.AccessRolePermissions AS p INNER JOIN dbo.AccessRoles AS r ON r.Id = p.RoleId INNER JOIN dbo.Branches AS b ON b.Id = r.BranchId;",
    };

    public static async Task<List<string>> ReadAsync(string connectionString)
    {
        var lines = new List<string>();
        await using var conn = await SqlTestDb.OpenWithRetryAsync(connectionString);
        foreach (var sql in Queries)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        return lines;
    }
}
