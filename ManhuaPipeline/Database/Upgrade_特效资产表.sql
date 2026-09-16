-- ============================================================
-- 新增「特效资产」表：与角色/道具/环境资产平级，供 Stage11 提取、Stage9 生成提示词引用
-- ============================================================
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'EffectAssets')
BEGIN
    CREATE TABLE [dbo].[EffectAssets] (
        [AssetId]     INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]   INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [Name]        NVARCHAR(100) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [ImageUrl]    NVARCHAR(500) NULL,
        [CreatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_EffectAssets_Project] ON [dbo].[EffectAssets]([ProjectId]);
END
GO

-- 迁移：把《字镇九霄》项目28中误录为「环境资产」的特效资产移到特效资产表
INSERT INTO [dbo].[EffectAssets] ([ProjectId], [Name], [Description], [ImageUrl], [CreatedAt])
SELECT [ProjectId], [Name], [Description], [ImageUrl], [CreatedAt]
FROM [dbo].[EnvironmentAssets]
WHERE [ProjectId] = 28 AND [Name] IN (N'白刃阵云·燕歌行诗境', N'长歌领域·将进酒诗境', N'砚脉觉醒·文光冲天');

DELETE FROM [dbo].[EnvironmentAssets]
WHERE [ProjectId] = 28 AND [Name] IN (N'白刃阵云·燕歌行诗境', N'长歌领域·将进酒诗境', N'砚脉觉醒·文光冲天');
GO
