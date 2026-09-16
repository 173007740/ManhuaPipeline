-- =============================================
-- Upgrade_FrameAssetBindings.sql
-- 用途：资产 ↔ 分镜 的结构化引用绑定层。
--   Stage 5 落库后，把每个 StoryboardFrame 的「出镜角色 / 场景 / 道具 / 特效」
--   解析成对资产库的确定引用（Category + AssetId），供 Stage 9 直接查表生成
--   参考图清单，不再由 Stage 9 用文本 Contains + 全量兜底去猜。
-- 特点：幂等，可重复执行；只建新表，不动旧表旧数据。
-- 执行：SSMS 连接目标库后整段执行。
-- =============================================
USE [ManhuaPipeline];
GO

IF OBJECT_ID('dbo.FrameAssetBindings','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[FrameAssetBindings] (
        [BindingId] INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId] INT           NOT NULL,
        [FrameId]   INT           NOT NULL REFERENCES [dbo].[StoryboardFrames]([FrameId]) ON DELETE CASCADE,
        [Category]  NVARCHAR(20)  NOT NULL,               -- Character / Environment / Prop / Effect
        [AssetId]   INT           NOT NULL,               -- 对应类别资产表主键
        [Name]      NVARCHAR(100) NOT NULL,               -- 资产规范名（冗余，便于人工核对）
        [HasImage]  BIT           NOT NULL DEFAULT 0,     -- 该资产参考图是否已就绪（ImageUrl 非空）
        [SortOrder] INT           NOT NULL DEFAULT 0,     -- 镜头内参考图顺序（Stage9 按此生成 @图片N）
        [CreatedAt] DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_FAB_Project_Frame] ON [dbo].[FrameAssetBindings]([ProjectId], [FrameId]);
    CREATE INDEX [IX_FAB_Frame]        ON [dbo].[FrameAssetBindings]([FrameId]);
END
GO

PRINT N'✅ FrameAssetBindings 已就绪（分镜帧↔资产 绑定表）。';
GO
