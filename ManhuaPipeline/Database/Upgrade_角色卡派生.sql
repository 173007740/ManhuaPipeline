/* ============================================================================
   Upgrade_角色卡派生.sql
   目标：为「角色卡派生」提供数据基础 —— 同一个角色跨剧本复用，
        换装/换形态时以「上一张角色图 + 一件衣服参考图 + 描述性提示词」生成一张新的角色卡。

   设计前提（与业务约定一致）：
     · 各剧本的资产各管各的、图片不共享：本表只记录「这次派生用了哪两张图」，
       不建身份表、不做资产级联；源卡被删除也不影响已经派生出来的新卡。
     · 来源是弱引用（SourceProjectId / SourceAssetId 可空），源卡已删时靠快照图 SourceImageUrl 溯源。
     · 目前只用于角色（characters），道具/环境/特效暂不派生。

   表结构：
     ProjectId / AssetId              —— 派生出的新角色卡（强引用，新卡删了这条记录仍留档）
     SourceProjectId / SourceAssetId  —— 来源角色卡（弱引用，可空；两个都为空 = 来源是手动上传的图）
     SourceImageUrl                   —— 来源角色图（落盘路径快照，画布上作为箭头起点）
     GarmentImageUrl                  —— 服装参考图（可空：只改风格/姿态时可以不给）
     Prompt                           —— 用户填写的描述性提示词（存档以便复现）
     ResultImageUrl                   —— 生成结果（= 新卡的 ImageUrl）
     Note                             —— 备注，如「冬季版」

   幂等：可重复执行。
   ============================================================================ */
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.CharacterCardDerivations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CharacterCardDerivations
    (
        DerivationId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CharacterCardDerivations PRIMARY KEY,
        UserId          INT               NOT NULL,
        ProjectId       INT               NOT NULL,
        AssetId         INT               NOT NULL,
        SourceProjectId INT               NULL,
        SourceAssetId   INT               NULL,
        SourceImageUrl  NVARCHAR(400)     NOT NULL,
        GarmentImageUrl NVARCHAR(400)     NULL,
        Prompt          NVARCHAR(MAX)     NULL,
        ResultImageUrl  NVARCHAR(400)     NULL,
        Note            NVARCHAR(200)     NULL,
        CreatedAt       DATETIME          NOT NULL CONSTRAINT DF_CCD_CreatedAt DEFAULT(GETDATE())
    );
    PRINT N'已创建表 dbo.CharacterCardDerivations';
END
ELSE
    PRINT N'表 dbo.CharacterCardDerivations 已存在，跳过';
GO

-- 查某张卡的派生来源（画布上按卡取入边）
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CCD_Child'
               AND object_id = OBJECT_ID(N'dbo.CharacterCardDerivations'))
BEGIN
    CREATE INDEX IX_CCD_Child ON dbo.CharacterCardDerivations(ProjectId, AssetId, DerivationId DESC);
    PRINT N'已创建索引 IX_CCD_Child';
END
GO

-- 查某张卡派生出了哪些卡（画布上按卡取出边）
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CCD_Source'
               AND object_id = OBJECT_ID(N'dbo.CharacterCardDerivations'))
BEGIN
    CREATE INDEX IX_CCD_Source ON dbo.CharacterCardDerivations(SourceProjectId, SourceAssetId);
    PRINT N'已创建索引 IX_CCD_Source';
END
GO

/* ---------------------------------------------------------------------------
   AssetImageTasks：出图任务支持「带参考图派生」。
   五个字段全为空 = 原来的纯文生图路径（行为完全不变）。
   --------------------------------------------------------------------------- */
IF COL_LENGTH(N'dbo.AssetImageTasks', N'SourceProjectId') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD SourceProjectId INT NULL;
    PRINT N'AssetImageTasks 已加列 SourceProjectId';
END
GO

IF COL_LENGTH(N'dbo.AssetImageTasks', N'SourceAssetId') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD SourceAssetId INT NULL;
    PRINT N'AssetImageTasks 已加列 SourceAssetId';
END
GO

IF COL_LENGTH(N'dbo.AssetImageTasks', N'SourceImageUrl') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD SourceImageUrl NVARCHAR(400) NULL;
    PRINT N'AssetImageTasks 已加列 SourceImageUrl';
END
GO

IF COL_LENGTH(N'dbo.AssetImageTasks', N'GarmentImageUrl') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD GarmentImageUrl NVARCHAR(400) NULL;
    PRINT N'AssetImageTasks 已加列 GarmentImageUrl';
END
GO

IF COL_LENGTH(N'dbo.AssetImageTasks', N'SourceNote') IS NULL
BEGIN
    ALTER TABLE dbo.AssetImageTasks ADD SourceNote NVARCHAR(200) NULL;
    PRINT N'AssetImageTasks 已加列 SourceNote';
END
GO

PRINT N'=== 结果核对：角色卡派生 ===';
SELECT COUNT(*) AS DerivationCount FROM dbo.CharacterCardDerivations;
GO
