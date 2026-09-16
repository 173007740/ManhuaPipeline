-- =============================================
-- Director V3：整集导演计划与运行时状态
-- EpisodeDirectorPlans / EpisodeDirectorStates
-- 幂等，可重复执行；只加表加索引，不改旧数据。
-- 执行：SSMS 连接 ManhuaPipeline 后整段执行
-- =============================================
IF OBJECT_ID(N'dbo.EpisodeDirectorPlans', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[EpisodeDirectorPlans] (
        [EpisodeDirectorPlanId] INT IDENTITY(1,1) NOT NULL
            CONSTRAINT [PK_EpisodeDirectorPlans] PRIMARY KEY,
        [ProjectId]       INT NOT NULL,
        [EpisodeNumber]   INT NOT NULL,
        [EpisodeGoal]     NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_EpisodeGoal] DEFAULT N'',
        [EmotionCurve]    NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_EmotionCurve] DEFAULT N'',
        [IntensityCurveJson]      NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_IntensityCurveJson] DEFAULT N'[]',
        [PayoffScheduleJson]      NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_PayoffScheduleJson] DEFAULT N'[]',
        [ReservedVisualsJson]     NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_ReservedVisualsJson] DEFAULT N'[]',
        [ForbiddenEarlyPayoffsJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_ForbiddenEarlyPayoffsJson] DEFAULT N'[]',
        [RepetitionPolicyJson]    NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_RepetitionPolicyJson] DEFAULT N'{}',
        [RawJson]         NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_RawJson] DEFAULT N'',
        [CreatedAt]       DATETIME2 NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_CreatedAt] DEFAULT GETDATE(),
        [UpdatedAt]       DATETIME2 NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_UpdatedAt] DEFAULT GETDATE()
    );
END
GO

IF OBJECT_ID(N'dbo.EpisodeDirectorStates', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[EpisodeDirectorStates] (
        [EpisodeDirectorStateId] INT IDENTITY(1,1) NOT NULL
            CONSTRAINT [PK_EpisodeDirectorStates] PRIMARY KEY,
        [ProjectId]           INT NOT NULL,
        [EpisodeNumber]       INT NOT NULL,
        [CameraPatternCountsJson]  NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_CameraPatternCountsJson] DEFAULT N'{}',
        [CombatPatternCountsJson]  NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_CombatPatternCountsJson] DEFAULT N'{}',
        [VfxPatternCountsJson]     NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_VfxPatternCountsJson] DEFAULT N'{}',
        [SlowMotionCount]     INT NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_SlowMotionCount] DEFAULT 0,
        [MajorExplosionCount] INT NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_MajorExplosionCount] DEFAULT 0,
        [CurrentPeakIntensity] INT NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_CurrentPeakIntensity] DEFAULT 0,
        [CreatedAt]       DATETIME2 NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_CreatedAt] DEFAULT GETDATE(),
        [UpdatedAt]       DATETIME2 NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_UpdatedAt] DEFAULT GETDATE()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EpisodeDirectorPlans') AND name=N'UX_EpisodeDirectorPlans_Project_Episode')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.EpisodeDirectorPlans GROUP BY ProjectId, EpisodeNumber HAVING COUNT(*) > 1)
        PRINT N'EpisodeDirectorPlans 存在重复 ProjectId+EpisodeNumber，已跳过唯一索引。';
    ELSE
        CREATE UNIQUE INDEX [UX_EpisodeDirectorPlans_Project_Episode]
            ON [dbo].[EpisodeDirectorPlans]([ProjectId], [EpisodeNumber]);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EpisodeDirectorStates') AND name=N'UX_EpisodeDirectorStates_Project_Episode')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.EpisodeDirectorStates GROUP BY ProjectId, EpisodeNumber HAVING COUNT(*) > 1)
        PRINT N'EpisodeDirectorStates 存在重复 ProjectId+EpisodeNumber，已跳过唯一索引。';
    ELSE
        CREATE UNIQUE INDEX [UX_EpisodeDirectorStates_Project_Episode]
            ON [dbo].[EpisodeDirectorStates]([ProjectId], [EpisodeNumber]);
END
GO

PRINT N'Director V3 升级脚本执行完成：EpisodeDirectorPlans / EpisodeDirectorStates 已就绪。';
GO
