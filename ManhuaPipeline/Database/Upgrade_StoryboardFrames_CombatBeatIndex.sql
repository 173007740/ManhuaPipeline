-- StoryboardFrames V1.5：每个镜头记录所属 CombatBeatIndex，供漏拍校验使用
IF COL_LENGTH(N'dbo.StoryboardFrames', N'CombatBeatIndex') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD CombatBeatIndex INT NULL;
END
GO
