-- StoryboardFrames: record the fields shown on the Stage 5 storyboard page
IF COL_LENGTH(N'dbo.StoryboardFrames', N'EpisodeNumber') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD EpisodeNumber INT NULL;
END
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitType') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD UnitType NVARCHAR(100) NULL;
END
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'ShotNumber') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD ShotNumber NVARCHAR(50) NULL;
END
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'ShotSize') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD ShotSize NVARCHAR(200) NULL;
END
GO
