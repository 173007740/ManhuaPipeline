-- StoryboardFrames: add unit-level ordering so concurrent Stage 5 writes keep stage order.
IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitOrder') IS NULL
BEGIN
    ALTER TABLE dbo.StoryboardFrames ADD UnitOrder INT NOT NULL DEFAULT 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StoryboardFrames') AND name = N'IX_Frames_Project_UnitOrder')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Frames_Project_UnitOrder ON dbo.StoryboardFrames(ProjectId, EpisodeId, UnitOrder, SortOrder);
END
GO
