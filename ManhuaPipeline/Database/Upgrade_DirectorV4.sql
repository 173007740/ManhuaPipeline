-- =============================================
-- Director V4：整集导演计划新增 Unit 情绪/交接/结束状态/高潮预算
-- EpisodeDirectorPlans / EpisodeDirectorStates 加列
-- 幂等，可重复执行；只加列，不改旧数据。
-- 执行：SSMS 连接 ManhuaPipeline 后整段执行
-- =============================================

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EpisodeDirectorPlans') AND name = N'UnitEmotionCurveJson')
    ALTER TABLE [dbo].[EpisodeDirectorPlans]
        ADD [UnitEmotionCurveJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_UnitEmotionCurveJson] DEFAULT N'[]';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EpisodeDirectorPlans') AND name = N'UnitTransitionsJson')
    ALTER TABLE [dbo].[EpisodeDirectorPlans]
        ADD [UnitTransitionsJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_UnitTransitionsJson] DEFAULT N'[]';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EpisodeDirectorPlans') AND name = N'UnitEndStatesJson')
    ALTER TABLE [dbo].[EpisodeDirectorPlans]
        ADD [UnitEndStatesJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_UnitEndStatesJson] DEFAULT N'[]';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EpisodeDirectorPlans') AND name = N'ClimaxBudgetJson')
    ALTER TABLE [dbo].[EpisodeDirectorPlans]
        ADD [ClimaxBudgetJson] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_EpisodeDirectorPlans_ClimaxBudgetJson] DEFAULT N'{}';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EpisodeDirectorStates') AND name = N'SmallClimaxCount')
    ALTER TABLE [dbo].[EpisodeDirectorStates]
        ADD [SmallClimaxCount] INT NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_SmallClimaxCount] DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EpisodeDirectorStates') AND name = N'MidClimaxCount')
    ALTER TABLE [dbo].[EpisodeDirectorStates]
        ADD [MidClimaxCount] INT NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_MidClimaxCount] DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.EpisodeDirectorStates') AND name = N'LargeClimaxCount')
    ALTER TABLE [dbo].[EpisodeDirectorStates]
        ADD [LargeClimaxCount] INT NOT NULL
            CONSTRAINT [DF_EpisodeDirectorStates_LargeClimaxCount] DEFAULT 0;
GO

PRINT N'Director V4 升级脚本执行完成：EpisodeDirectorPlans / EpisodeDirectorStates 新列已就绪。';
GO
