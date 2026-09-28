/* ============================================================================
   Upgrade_出图参数.sql
   目标：让「出图」的五个参数可以在页面上配置，并随任务落库、后台照原样执行。

   背景（2026-09-19 对 gpt-image-2.5 中转实测）：
     · size      只认「比例」不认具体像素：中转把总像素锁在 ~1.573MP，
                 再按请求比例分配宽高（w = √(P×r)）。所以这里存比例，
                 由 ImageGenOptions.SizeForRatio 转成请求用的宽x高。
     · quality   生效（影响画质与耗时，不影响 token），档位 auto/low/medium/high/xhigh/max。
     · background 生效（transparent 会真的出带 Alpha 的 PNG）。
     · output_format 当前中转恒返回 PNG（参数先存着，换中转后即可生效）。
     · n        当前中转只返回 1 张，所以「一次出几张」由后端按次数循环实现。

   表结构：
     ImageGenOptions        —— 按用户的出图参数默认值（配置页设置）
     AssetImageTasks 新列   —— 入队时把参数快照写进任务，后台执行不受后续改配置影响

   幂等：可重复执行。
   ============================================================================ */
SET NOCOUNT ON;
GO

-- 一、出图参数默认值（每个用户一条）
IF OBJECT_ID(N'dbo.ImageGenOptions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ImageGenOptions
    (
        UserId       INT           NOT NULL CONSTRAINT PK_ImageGenOptions PRIMARY KEY,
        AspectRatio  NVARCHAR(8)   NOT NULL CONSTRAINT DF_ImageGenOptions_AspectRatio  DEFAULT(N'16:9'),
        Quality      NVARCHAR(16)  NOT NULL CONSTRAINT DF_ImageGenOptions_Quality      DEFAULT(N'high'),
        ImageCount   INT           NOT NULL CONSTRAINT DF_ImageGenOptions_ImageCount   DEFAULT(1),
        Background   NVARCHAR(16)  NOT NULL CONSTRAINT DF_ImageGenOptions_Background   DEFAULT(N'opaque'),
        OutputFormat NVARCHAR(8)   NOT NULL CONSTRAINT DF_ImageGenOptions_OutputFormat DEFAULT(N'png'),
        UpdatedAt    DATETIME      NULL
    );
    PRINT N'已创建表 dbo.ImageGenOptions';
END
ELSE
    PRINT N'表 dbo.ImageGenOptions 已存在，跳过';
GO

-- 二、出图任务表补列：把当次出图的参数快照存下来
IF COL_LENGTH(N'dbo.AssetImageTasks', N'Quality') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD Quality NVARCHAR(16) NULL;
    PRINT N'已增加列 AssetImageTasks.Quality';
END
ELSE PRINT N'列 AssetImageTasks.Quality 已存在，跳过';

IF COL_LENGTH(N'dbo.AssetImageTasks', N'ImageCount') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD ImageCount INT NULL;
    PRINT N'已增加列 AssetImageTasks.ImageCount';
END
ELSE PRINT N'列 AssetImageTasks.ImageCount 已存在，跳过';

IF COL_LENGTH(N'dbo.AssetImageTasks', N'Background') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD Background NVARCHAR(16) NULL;
    PRINT N'已增加列 AssetImageTasks.Background';
END
ELSE PRINT N'列 AssetImageTasks.Background 已存在，跳过';

IF COL_LENGTH(N'dbo.AssetImageTasks', N'OutputFormat') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD OutputFormat NVARCHAR(8) NULL;
    PRINT N'已增加列 AssetImageTasks.OutputFormat';
END
ELSE PRINT N'列 AssetImageTasks.OutputFormat 已存在，跳过';
GO

PRINT N'=== 结果核对 ===';
SELECT COUNT(*) AS OptionRows FROM dbo.ImageGenOptions;
SELECT COUNT(*) AS TaskRows  FROM dbo.AssetImageTasks;
GO
