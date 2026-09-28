-- ============================================================
-- 时间基准统一：Canvas* / ImageStyles 从 UTC 改回服务器本地时间
-- ------------------------------------------------------------
-- 背景：这几张表建表时默认约束写的是 SYSUTCDATETIME()（UTC），
-- 而项目其余表（Projects / Episodes / AssetImageTasks …）都是 DateTime.Now，
-- 也就是服务器本地时间（中国 = UTC+8）。
-- 结果：读出来的时间被当成显示值直接渲染，「系统报告 → 出图清单 → 更新时间」
-- 比墙上时钟整整早 8 小时（15:44 显示、实际 23:44）。
--
-- 修法两步：
--   ① 默认约束改为 SYSDATETIME()（本地），配合代码里同步改过的 UPDATE 语句；
--   ② 已有记录整体平移 @off 小时，避免新旧数据各按一套基准、排序串在一起时错乱。
--
-- @off 用 GETUTCDATE() 与 GETDATE() 的差动态算，不写死 8：
-- 换时区部署时脚本自动不做事（@off = 0）。
-- ============================================================
SET NOCOUNT ON;

DECLARE @off int = DATEDIFF(hour, GETUTCDATE(), GETDATE());
PRINT 'UTC offset (hours) = ' + CAST(@off AS varchar(10));

-- ① 默认约束：UTC → 本地
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CanvasBoards_Created')
BEGIN
    ALTER TABLE CanvasBoards DROP CONSTRAINT DF_CanvasBoards_Created;
    ALTER TABLE CanvasBoards ADD CONSTRAINT DF_CanvasBoards_Created DEFAULT SYSDATETIME() FOR CreatedAt;
END
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CanvasBoards_Updated')
BEGIN
    ALTER TABLE CanvasBoards DROP CONSTRAINT DF_CanvasBoards_Updated;
    ALTER TABLE CanvasBoards ADD CONSTRAINT DF_CanvasBoards_Updated DEFAULT SYSDATETIME() FOR UpdatedAt;
END
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CanvasNodes_Created')
BEGIN
    ALTER TABLE CanvasNodes DROP CONSTRAINT DF_CanvasNodes_Created;
    ALTER TABLE CanvasNodes ADD CONSTRAINT DF_CanvasNodes_Created DEFAULT SYSDATETIME() FOR CreatedAt;
END
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CanvasNodes_Updated')
BEGIN
    ALTER TABLE CanvasNodes DROP CONSTRAINT DF_CanvasNodes_Updated;
    ALTER TABLE CanvasNodes ADD CONSTRAINT DF_CanvasNodes_Updated DEFAULT SYSDATETIME() FOR UpdatedAt;
END
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CanvasEdges_Created')
BEGIN
    ALTER TABLE CanvasEdges DROP CONSTRAINT DF_CanvasEdges_Created;
    ALTER TABLE CanvasEdges ADD CONSTRAINT DF_CanvasEdges_Created DEFAULT SYSDATETIME() FOR CreatedAt;
END
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_CanvasTasks_Created')
BEGIN
    ALTER TABLE CanvasTasks DROP CONSTRAINT DF_CanvasTasks_Created;
    ALTER TABLE CanvasTasks ADD CONSTRAINT DF_CanvasTasks_Created DEFAULT SYSDATETIME() FOR CreatedAt;
END
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_ImageStyles_Created')
BEGIN
    ALTER TABLE ImageStyles DROP CONSTRAINT DF_ImageStyles_Created;
    ALTER TABLE ImageStyles ADD CONSTRAINT DF_ImageStyles_Created DEFAULT SYSDATETIME() FOR CreatedAt;
END
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_ImageStyles_Updated')
BEGIN
    ALTER TABLE ImageStyles DROP CONSTRAINT DF_ImageStyles_Updated;
    ALTER TABLE ImageStyles ADD CONSTRAINT DF_ImageStyles_Updated DEFAULT SYSDATETIME() FOR UpdatedAt;
END
PRINT 'default constraints updated.';

-- ② 历史数据整体平移（NULL 字段 DATEADD 后仍是 NULL，不用额外判断）
IF @off <> 0
BEGIN
    UPDATE CanvasBoards SET CreatedAt = DATEADD(hour, @off, CreatedAt), UpdatedAt = DATEADD(hour, @off, UpdatedAt);
    UPDATE CanvasNodes  SET CreatedAt = DATEADD(hour, @off, CreatedAt), UpdatedAt = DATEADD(hour, @off, UpdatedAt);
    UPDATE CanvasEdges  SET CreatedAt = DATEADD(hour, @off, CreatedAt);
    UPDATE CanvasTasks  SET CreatedAt = DATEADD(hour, @off, CreatedAt),
                            StartedAt = DATEADD(hour, @off, StartedAt),
                            FinishedAt = DATEADD(hour, @off, FinishedAt);
    UPDATE ImageStyles  SET CreatedAt = DATEADD(hour, @off, CreatedAt), UpdatedAt = DATEADD(hour, @off, UpdatedAt);
    PRINT 'existing rows shifted.';
END
ELSE
    PRINT 'offset = 0, nothing to shift.';

PRINT 'done.';
