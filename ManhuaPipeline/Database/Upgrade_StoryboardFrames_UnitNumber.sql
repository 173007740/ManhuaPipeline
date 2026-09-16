-- StoryboardFrames: add UnitNumber for per-unit Stage 5 incremental saves
IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitNumber') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD UnitNumber NVARCHAR(50) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StoryboardFrames') AND name = N'IX_Frames_Unit')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Frames_Unit ON dbo.StoryboardFrames(ProjectId, EpisodeId, UnitNumber);
END
GO
