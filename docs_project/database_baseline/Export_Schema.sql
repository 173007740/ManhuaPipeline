/*
    ManhuaPipeline 数据库结构导出（只读）

    用途：
      从「当前库」反向拉取最新表结构，供 Baseline.sql / Upgrade_Current.sql / Verify.sql
      对照刷新使用。库结构变了（新增表 / 新增字段 / 新增索引）之后跑一遍本脚本即可拿到
      权威现状，不需要手工逐个回忆。

    目标：SQL Server 2016+ / 兼容级别 130+（不使用 STRING_AGG 等 2017+ 语法）

    用法：
      sqlcmd -S . -d ManhuaPipeline -U <账号> -P <密码> -f 65001 -W -s"|" -i Export_Schema.sql -o _schema_dump.txt

    本脚本只读取 sys.* 目录视图，不含任何 CREATE / ALTER / DROP / INSERT / UPDATE / DELETE。
*/

-- sqlcmd 默认 QUOTED_IDENTIFIER OFF，而结果集 3 的 FOR XML PATH().value() 需要 ON。
-- SET QUOTED_IDENTIFIER 是批处理解析期设置，必须独立成批才能生效。
SET QUOTED_IDENTIFIER ON;
GO

SET NOCOUNT ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
BEGIN
    THROW 51000, N'请选择 ManhuaPipeline 业务数据库后再执行 Export_Schema.sql。', 1;
END;

PRINT N'=== 1. 表清单（按表名）===';

SELECT
    t.name AS TableName,
    COUNT(c.column_id) AS ColumnCount
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.columns c ON c.object_id = t.object_id
WHERE s.name = N'dbo' AND t.is_ms_shipped = 0
GROUP BY t.name
ORDER BY t.name;

PRINT N'=== 2. 列定义（每表每列一行，按 column_id 排序，可直接转成 CREATE TABLE 片段）===';

SELECT
    t.name AS TableName,
    c.column_id AS ColumnId,
    c.name AS ColumnName,
    CASE
        WHEN ty.name IN (N'nvarchar', N'nchar')
            THEN UPPER(ty.name) + N'(' + CASE WHEN c.max_length = -1 THEN N'MAX' ELSE CONVERT(NVARCHAR(10), c.max_length / 2) END + N')'
        WHEN ty.name IN (N'varchar', N'char', N'varbinary', N'binary')
            THEN UPPER(ty.name) + N'(' + CASE WHEN c.max_length = -1 THEN N'MAX' ELSE CONVERT(NVARCHAR(10), c.max_length) END + N')'
        WHEN ty.name IN (N'decimal', N'numeric')
            THEN UPPER(ty.name) + N'(' + CONVERT(NVARCHAR(10), c.precision) + N',' + CONVERT(NVARCHAR(10), c.scale) + N')'
        WHEN ty.name IN (N'datetime2', N'datetimeoffset', N'time')
            THEN UPPER(ty.name) + N'(' + CONVERT(NVARCHAR(10), c.scale) + N')'
        ELSE UPPER(ty.name)
    END AS DataType,
    CASE WHEN c.is_nullable = 1 THEN N'NULL' ELSE N'NOT NULL' END AS Nullability,
    CASE WHEN c.is_identity = 1 THEN N'IDENTITY' ELSE N'' END AS IdentityFlag,
    ISNULL(dc.name, N'') AS DefaultConstraintName,
    ISNULL(REPLACE(REPLACE(dc.definition, NCHAR(13), N''), NCHAR(10), N' '), N'') AS DefaultDefinition
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc
       ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE s.name = N'dbo' AND t.is_ms_shipped = 0
ORDER BY t.name, c.column_id;

PRINT N'=== 3. 索引（KeyColumns 含 DESC；IsUnique / IsPrimaryKey 供对照）===';

SELECT
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.is_primary_key AS IsPrimaryKey,
    ISNULL(i.filter_definition, N'') AS FilterDefinition,
    STUFF((
        SELECT N', ' + c2.name + CASE WHEN ic2.is_descending_key = 1 THEN N' DESC' ELSE N'' END
        FROM sys.index_columns ic2
        JOIN sys.columns c2 ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id
        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
        ORDER BY ic2.key_ordinal
        FOR XML PATH(N''), TYPE
    ).value(N'.', N'NVARCHAR(MAX)'), 1, 2, N'') AS KeyColumns
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = N'dbo' AND i.type > 0 AND i.name IS NOT NULL
ORDER BY t.name, i.name;

PRINT N'=== 4. 外键（含删除规则）===';

SELECT
    f.name AS ForeignKeyName,
    OBJECT_NAME(f.parent_object_id) AS ParentTable,
    pc.name AS ParentColumn,
    OBJECT_NAME(f.referenced_object_id) AS ReferencedTable,
    rc.name AS ReferencedColumn,
    f.delete_referential_action_desc AS OnDelete,
    f.update_referential_action_desc AS OnUpdate
FROM sys.foreign_keys f
JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id
JOIN sys.columns pc ON pc.object_id = f.parent_object_id AND pc.column_id = fc.parent_column_id
JOIN sys.columns rc ON rc.object_id = f.referenced_object_id AND rc.column_id = fc.referenced_column_id
WHERE SCHEMA_NAME(f.schema_id) = N'dbo'
ORDER BY ParentTable, ParentColumn;

PRINT N'=== 5. 表行数快照 ===';

SELECT
    t.name AS TableName,
    SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS [Rows]
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.partitions p ON p.object_id = t.object_id
WHERE s.name = N'dbo' AND t.is_ms_shipped = 0
GROUP BY t.name
ORDER BY t.name;

PRINT N'=== Export_Schema.sql 执行完成：本脚本未修改数据库 ===';
