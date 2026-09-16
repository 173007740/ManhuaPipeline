-- =============================================
-- Director V4 第二批：Unit 落镜状态快照表
-- EpisodeUnitStateSnapshots
-- 幂等，可重复执行；只建表加索引，不改旧数据。
-- 执行：SSMS 连接 ManhuaPipeline 后整段执行
-- =============================================
IF OBJECT_ID(N'dbo.EpisodeUnitStateSnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[EpisodeUnitStateSnapshots] (
        [EpisodeUnitStateSnapshotId] INT IDENTITY(1,1) NOT NULL
            CONSTRAINT [PK_EpisodeUnitStateSnapshots] PRIMARY KEY,
        [ProjectId]     INT NOT NULL,
        [EpisodeNumber] INT NOT NULL,
        [UnitNumber]    NVARCHAR(50) NOT NULL,
        [StateJson]     NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeUnitStateSnapshots_StateJson] DEFAULT N'{}',
        [Source]        NVARCHAR(20) NOT NULL
            CONSTRAINT [DF_EpisodeUnitStateSnapshots_Source] DEFAULT N'storyboard',
        [CreatedAt]     DATETIME2 NOT NULL
            CONSTRAINT [DF_EpisodeUnitStateSnapshots_CreatedAt] DEFAULT GETDATE(),
        [UpdatedAt]     DATETIME2 NOT NULL
            CONSTRAINT [DF_EpisodeUnitStateSnapshots_UpdatedAt] DEFAULT GETDATE(),
        CONSTRAINT [FK_EpisodeUnitStateSnapshots_Projects]
            FOREIGN KEY ([ProjectId]) REFERENCES [dbo].[Projects]([ProjectId]) ON DELETE CASCADE,
        CONSTRAINT [CK_EpisodeUnitStateSnapshots_Episode] CHECK ([EpisodeNumber] >= 1)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EpisodeUnitStateSnapshots') AND name=N'UX_EpisodeUnitStateSnapshots_Project_Episode_Unit')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.EpisodeUnitStateSnapshots GROUP BY ProjectId, EpisodeNumber, UnitNumber HAVING COUNT(*) > 1)
        PRINT N'EpisodeUnitStateSnapshots 存在重复 ProjectId+EpisodeNumber+UnitNumber，已跳过唯一索引。';
    ELSE
        CREATE UNIQUE INDEX [UX_EpisodeUnitStateSnapshots_Project_Episode_Unit]
            ON [dbo].[EpisodeUnitStateSnapshots]([ProjectId], [EpisodeNumber], [UnitNumber]);
END
GO

PRINT N'Director V4 第二批升级脚本执行完成：EpisodeUnitStateSnapshots 已就绪。';
GO
