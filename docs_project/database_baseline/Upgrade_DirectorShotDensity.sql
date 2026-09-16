-- Director V2 Shot Density upgrade: StoryboardFrames carries multi-beat coverage and in-shot timeline.
-- Safe to run repeatedly; only adds missing columns.

IF COL_LENGTH(N'dbo.StoryboardFrames', N'EpisodeNumber') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD EpisodeNumber INT NULL;
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitType') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD UnitType NVARCHAR(100) NULL;
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'ShotNumber') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD ShotNumber NVARCHAR(50) NULL;
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'ShotSize') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD ShotSize NVARCHAR(200) NULL;
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitOrder') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD UnitOrder INT NOT NULL CONSTRAINT DF_StoryboardFrames_UnitOrder DEFAULT 0 WITH VALUES;
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'CombatBeatIndex') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD CombatBeatIndex INT NULL;
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'CombatBeatIds') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD CombatBeatIds NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH(N'dbo.StoryboardFrames', N'Timeline') IS NULL
    ALTER TABLE dbo.StoryboardFrames ADD Timeline NVARCHAR(MAX) NULL;
GO
