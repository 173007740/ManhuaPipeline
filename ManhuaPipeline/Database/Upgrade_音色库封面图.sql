-- ============================================================
-- 音色库封面图（VoiceLibraryItems.ImageUrl）
-- 用途：音色卡片可配一张封面图，显示在卡片背景上，
--       方便一眼区分音色（角色立绘 / 场景图 / 情绪参考图都行）。
-- 归属：与音色同一行，随删除一起清理；留空表示不显示封面。
-- 幂等：可重复执行。
-- ============================================================
IF COL_LENGTH(N'dbo.VoiceLibraryItems', N'ImageUrl') IS NULL
BEGIN
    ALTER TABLE dbo.VoiceLibraryItems ADD ImageUrl NVARCHAR(1000) NULL;
END
GO
