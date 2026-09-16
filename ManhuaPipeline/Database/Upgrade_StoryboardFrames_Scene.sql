-- 分镜表增加 Scene 列：镜头场景（优先取环境资产清单中的资产名）
IF COL_LENGTH(N'dbo.StoryboardFrames', N'Scene') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD Scene NVARCHAR(MAX) NULL;
END
GO
