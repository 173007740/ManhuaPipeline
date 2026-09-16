-- StoryboardFrames: expand LLM storyboard fields
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.StoryboardFrames')
      AND name = N'Composition'
      AND max_length <> -1
)
    ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Composition NVARCHAR(MAX) NULL;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.StoryboardFrames')
      AND name = N'Characters'
      AND max_length <> -1
)
    ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Characters NVARCHAR(MAX) NULL;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.StoryboardFrames')
      AND name = N'Camera'
      AND max_length <> -1
)
    ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Camera NVARCHAR(MAX) NULL;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.StoryboardFrames')
      AND name = N'Duration'
      AND max_length <> -1
)
    ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Duration NVARCHAR(MAX) NULL;
