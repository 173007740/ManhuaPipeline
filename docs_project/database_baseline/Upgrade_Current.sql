/*
    ManhuaPipeline historical database upgrade
    Version: 2026-09-16
    Target: SQL Server 2016+ / compatibility level 130+

    SAFETY CONTRACT
    - Run only after taking a verified backup, preferably on a restored copy first.
    - Never run this script on master/model/msdb/tempdb.
    - The script never drops tables, columns, indexes, constraints, or business rows.
    - It does not include the old project-specific EffectAssets data move.
    - NULL values are normalized only where current code already uses the same fallback.
    - Unique indexes and foreign keys are skipped and reported when historical data conflicts.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
BEGIN
    THROW 51100, N'请选择 ManhuaPipeline 历史数据库或其还原副本后再执行。', 1;
END;

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL OR OBJECT_ID(N'dbo.Projects', N'U') IS NULL
BEGIN
    THROW 51101, N'缺少 Users 或 Projects 核心表；该数据库不适用历史升级脚本，请对空库使用 Baseline.sql。', 1;
END;

IF OBJECT_ID(N'dbo.StageData', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Episodes', N'U') IS NULL
   OR OBJECT_ID(N'dbo.CharacterAssets', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PropAssets', N'U') IS NULL
   OR OBJECT_ID(N'dbo.EnvironmentAssets', N'U') IS NULL
   OR OBJECT_ID(N'dbo.SeedancePrompts', N'U') IS NULL
   OR OBJECT_ID(N'dbo.CoherenceChecks', N'U') IS NULL
   OR OBJECT_ID(N'dbo.LLMConfigs', N'U') IS NULL
BEGIN
    THROW 51102, N'历史数据库缺少一个或多个核心业务表，拒绝猜测性升级；请先人工核对数据库来源。', 1;
END;

DECLARE @Warnings TABLE (
    WarningId  INT IDENTITY(1,1) PRIMARY KEY,
    Severity   NVARCHAR(10) NOT NULL,
    Code       NVARCHAR(80) NOT NULL,
    Details    NVARCHAR(1000) NOT NULL
);

BEGIN TRY
    BEGIN TRANSACTION;

    /* ---------- Missing tables from known historical layouts ---------- */

    IF OBJECT_ID(N'dbo.VideoStyles', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VideoStyles (
            StyleId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VideoStyles PRIMARY KEY,
            StyleName NVARCHAR(100) NOT NULL,
            StylePrompt NVARCHAR(MAX) NOT NULL,
            IsDefault BIT NOT NULL CONSTRAINT DF_VideoStyles_IsDefault DEFAULT 0,
            CreatedAt DATETIME NOT NULL CONSTRAINT DF_VideoStyles_CreatedAt DEFAULT GETDATE(),
            UpdatedAt DATETIME NOT NULL CONSTRAINT DF_VideoStyles_UpdatedAt DEFAULT GETDATE(),
            CONSTRAINT UQ_VideoStyles_StyleName UNIQUE (StyleName)
        );
    END;

    IF OBJECT_ID(N'dbo.Dramas', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Dramas (
            DramaId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Dramas PRIMARY KEY,
            UserId INT NOT NULL,
            Title NVARCHAR(200) NOT NULL,
            Description NVARCHAR(MAX) NULL,
            CoverImage NVARCHAR(500) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Dramas_CreatedAt DEFAULT GETDATE(),
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_Dramas_UpdatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.StoryboardFrames', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StoryboardFrames (
            FrameId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StoryboardFrames PRIMARY KEY,
            EpisodeId INT NOT NULL,
            ProjectId INT NOT NULL,
            FrameNumber INT NOT NULL,
            Description NVARCHAR(MAX) NULL,
            Composition NVARCHAR(MAX) NULL,
            Characters NVARCHAR(MAX) NULL,
            Dialogue NVARCHAR(MAX) NULL,
            Camera NVARCHAR(MAX) NULL,
            Duration NVARCHAR(MAX) NULL,
            StartScene NVARCHAR(MAX) NULL,
            EndScene NVARCHAR(MAX) NULL,
            UnitNumber NVARCHAR(50) NULL,
            EpisodeNumber INT NULL,
            UnitType NVARCHAR(100) NULL,
            ShotNumber NVARCHAR(50) NULL,
            ShotSize NVARCHAR(200) NULL,
            UnitOrder INT NOT NULL CONSTRAINT DF_StoryboardFrames_UnitOrder DEFAULT 0,
            CombatBeatIndex INT NULL,
            CombatBeatIds NVARCHAR(MAX) NULL,
            Timeline NVARCHAR(MAX) NULL,
            BatchNumber INT NOT NULL CONSTRAINT DF_StoryboardFrames_BatchNumber DEFAULT 1,
            SortOrder INT NOT NULL CONSTRAINT DF_StoryboardFrames_SortOrder DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_StoryboardFrames_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.EffectAssets', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EffectAssets (
            AssetId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EffectAssets PRIMARY KEY,
            ProjectId INT NOT NULL,
            Name NVARCHAR(100) NOT NULL,
            Description NVARCHAR(MAX) NULL,
            ImageUrl NVARCHAR(500) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_EffectAssets_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.ReferenceAssets', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ReferenceAssets (
            AssetId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceAssets PRIMARY KEY,
            UserId INT NOT NULL,
            FileName NVARCHAR(255) NOT NULL,
            LocalPath NVARCHAR(500) NOT NULL,
            FileType NVARCHAR(50) NULL,
            Category NVARCHAR(100) NULL,
            SubCategory NVARCHAR(100) NULL,
            Tags NVARCHAR(500) NULL,
            FileSize BIGINT NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ReferenceAssets_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.SkillLibrary', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SkillLibrary (
            SkillId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SkillLibrary PRIMARY KEY,
            UserId INT NOT NULL,
            ProjectId INT NULL,
            Name NVARCHAR(100) NOT NULL,
            Element NVARCHAR(20) NULL,
            Tier INT NULL CONSTRAINT DF_SkillLibrary_Tier DEFAULT 4,
            PromptImage NVARCHAR(MAX) NULL,
            PromptVideo NVARCHAR(MAX) NULL,
            ImageUrl NVARCHAR(500) NULL,
            Tags NVARCHAR(500) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SkillLibrary_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.SkillElements', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SkillElements (
            ElementId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SkillElements PRIMARY KEY,
            UserId INT NOT NULL,
            Name NVARCHAR(50) NOT NULL,
            SortOrder INT NOT NULL CONSTRAINT DF_SkillElements_SortOrder DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SkillElements_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.FightTemplate', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FightTemplate (
            FightTemplateId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FightTemplate PRIMARY KEY,
            UserId INT NOT NULL,
            Name NVARCHAR(100) NOT NULL,
            Tier INT NULL CONSTRAINT DF_FightTemplate_Tier DEFAULT 3,
            Duration INT NULL CONSTRAINT DF_FightTemplate_Duration DEFAULT 11,
            Scene NVARCHAR(500) NULL,
            Beat NVARCHAR(MAX) NULL,
            ActionPrompt NVARCHAR(MAX) NULL,
            CameraPrompt NVARCHAR(MAX) NULL,
            ConstraintPrompt NVARCHAR(MAX) NULL,
            Tags NVARCHAR(500) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_FightTemplate_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.ShotDirective', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ShotDirective (
            ShotDirectiveId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShotDirective PRIMARY KEY,
            UserId INT NOT NULL,
            Name NVARCHAR(100) NOT NULL,
            Category NVARCHAR(20) NOT NULL,
            Description NVARCHAR(MAX) NULL,
            Tags NVARCHAR(500) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ShotDirective_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.DirectorPlans', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.DirectorPlans (
            DirectorPlanId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DirectorPlans PRIMARY KEY,
            ProjectId INT NOT NULL,
            EpisodeNumber INT NOT NULL CONSTRAINT DF_DirectorPlans_EpisodeNumber DEFAULT 0,
            UnitNumber NVARCHAR(50) NOT NULL,
            UnitType NVARCHAR(50) NOT NULL CONSTRAINT DF_DirectorPlans_UnitType DEFAULT N'',
            DramaticPurpose NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_DramaticPurpose DEFAULT N'',
            PrimarySubject NVARCHAR(200) NOT NULL CONSTRAINT DF_DirectorPlans_PrimarySubject DEFAULT N'',
            SecondarySubject NVARCHAR(200) NOT NULL CONSTRAINT DF_DirectorPlans_SecondarySubject DEFAULT N'',
            ConflictType NVARCHAR(20) NOT NULL CONSTRAINT DF_DirectorPlans_ConflictType DEFAULT N'NonCombat',
            CorePayoff NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_CorePayoff DEFAULT N'',
            EmotionCurve NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_EmotionCurve DEFAULT N'',
            RhythmStrategy NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_RhythmStrategy DEFAULT N'',
            ActionStrategy NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_ActionStrategy DEFAULT N'',
            PerformanceStrategy NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_PerformanceStrategy DEFAULT N'',
            CameraStrategy NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_CameraStrategy DEFAULT N'',
            VfxStrategy NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_VfxStrategy DEFAULT N'',
            IntensityLevel INT NOT NULL CONSTRAINT DF_DirectorPlans_IntensityLevel DEFAULT 3,
            CombatGrammarIds NVARCHAR(500) NOT NULL CONSTRAINT DF_DirectorPlans_CombatGrammarIds DEFAULT N'',
            FightArcType NVARCHAR(50) NOT NULL CONSTRAINT DF_DirectorPlans_FightArcType DEFAULT N'',
            FightSequenceJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_FightSequenceJson DEFAULT N'',
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_DirectorPlans_CreatedAt DEFAULT GETDATE(),
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_DirectorPlans_UpdatedAt DEFAULT GETDATE(),
            CONSTRAINT CK_DirectorPlans_IntensityLevel CHECK (IntensityLevel BETWEEN 1 AND 10)
        );
    END;

    IF OBJECT_ID(N'dbo.EpisodeDirectorPlans', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EpisodeDirectorPlans (
            EpisodeDirectorPlanId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EpisodeDirectorPlans PRIMARY KEY,
            ProjectId INT NOT NULL,
            EpisodeNumber INT NOT NULL,
            EpisodeGoal NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_EpisodeGoal DEFAULT N'',
            EmotionCurve NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_EmotionCurve DEFAULT N'',
            IntensityCurveJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_IntensityCurveJson DEFAULT N'[]',
            PayoffScheduleJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_PayoffScheduleJson DEFAULT N'[]',
            ReservedVisualsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_ReservedVisualsJson DEFAULT N'[]',
            ForbiddenEarlyPayoffsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_ForbiddenEarlyPayoffsJson DEFAULT N'[]',
            RepetitionPolicyJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_RepetitionPolicyJson DEFAULT N'{}',
            UnitEmotionCurveJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitEmotionCurveJson DEFAULT N'[]',
            UnitTransitionsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitTransitionsJson DEFAULT N'[]',
            UnitEndStatesJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitEndStatesJson DEFAULT N'[]',
            ClimaxBudgetJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_ClimaxBudgetJson DEFAULT N'{}',
            RawJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_RawJson DEFAULT N'',
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_CreatedAt DEFAULT GETDATE(),
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UpdatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.EpisodeDirectorStates', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EpisodeDirectorStates (
            EpisodeDirectorStateId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EpisodeDirectorStates PRIMARY KEY,
            ProjectId INT NOT NULL,
            EpisodeNumber INT NOT NULL,
            CameraPatternCountsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CameraPatternCountsJson DEFAULT N'{}',
            CombatPatternCountsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CombatPatternCountsJson DEFAULT N'{}',
            VfxPatternCountsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorStates_VfxPatternCountsJson DEFAULT N'{}',
            SlowMotionCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_SlowMotionCount DEFAULT 0,
            MajorExplosionCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_MajorExplosionCount DEFAULT 0,
            CurrentPeakIntensity INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CurrentPeakIntensity DEFAULT 0,
            SmallClimaxCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_SmallClimaxCount DEFAULT 0,
            MidClimaxCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_MidClimaxCount DEFAULT 0,
            LargeClimaxCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_LargeClimaxCount DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CreatedAt DEFAULT GETDATE(),
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorStates_UpdatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.EpisodeUnitStateSnapshots', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.EpisodeUnitStateSnapshots (
            EpisodeUnitStateSnapshotId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EpisodeUnitStateSnapshots PRIMARY KEY,
            ProjectId INT NOT NULL,
            EpisodeNumber INT NOT NULL,
            UnitNumber NVARCHAR(50) NOT NULL,
            StateJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_StateJson DEFAULT N'{}',
            Source NVARCHAR(20) NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_Source DEFAULT N'storyboard',
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_CreatedAt DEFAULT GETDATE(),
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_UpdatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.VideoGenerationTasks', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VideoGenerationTasks (
            Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VideoGenerationTasks PRIMARY KEY,
            ProjectId INT NOT NULL,
            PromptId INT NOT NULL,
            TaskId NVARCHAR(200) NOT NULL,
            Engine NVARCHAR(20) NOT NULL CONSTRAINT DF_VideoTasks_Engine DEFAULT N'volcano',
            Status NVARCHAR(50) NOT NULL CONSTRAINT DF_VideoTasks_Status DEFAULT N'pending',
            VideoUrl NVARCHAR(MAX) NULL,
            LocalVideoUrl NVARCHAR(MAX) NULL,
            RequestDuration INT NOT NULL CONSTRAINT DF_VideoTasks_RequestDuration DEFAULT 11,
            RequestRatio NVARCHAR(20) NOT NULL CONSTRAINT DF_VideoTasks_RequestRatio DEFAULT N'16:9',
            RequestWatermark BIT NOT NULL CONSTRAINT DF_VideoTasks_RequestWatermark DEFAULT 0,
            RequestGenerateAudio BIT NOT NULL CONSTRAINT DF_VideoTasks_RequestAudio DEFAULT 1,
            ResponseResolution NVARCHAR(50) NULL,
            ResponseDuration FLOAT NULL,
            EnhanceDuration INT NULL,
            EnhanceResolution NVARCHAR(50) NULL,
            ResponseUsageTokens INT NULL,
            ResponseSeed INT NULL,
            ApiStatus NVARCHAR(50) NULL,
            ErrorMessage NVARCHAR(MAX) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_VideoTasks_CreatedAt DEFAULT GETDATE(),
            CompletedAt DATETIME2 NULL
        );
    END;

    IF OBJECT_ID(N'dbo.TokenUsageConfig', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.TokenUsageConfig (
            Id INT NOT NULL CONSTRAINT PK_TokenUsageConfig PRIMARY KEY,
            QuotaTokens BIGINT NOT NULL CONSTRAINT DF_TokenUsageConfig_Quota DEFAULT 14000000,
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_TokenUsageConfig_UpdatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.Works', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Works (
            WorkId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Works PRIMARY KEY,
            UserId INT NOT NULL,
            Title NVARCHAR(200) NOT NULL,
            Description NVARCHAR(MAX) NULL,
            CoverImage NVARCHAR(500) NULL,
            WorkUrl NVARCHAR(500) NULL,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Works_CreatedAt DEFAULT GETDATE(),
            UpdatedAt DATETIME2 NULL
        );
    END;

    /* ---------- Missing columns from known historical layouts ---------- */

    IF COL_LENGTH(N'dbo.Users', N'ActiveLLMProvider') IS NULL
        ALTER TABLE dbo.Users ADD ActiveLLMProvider NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_ActiveLLMProvider DEFAULT N'deepseek' WITH VALUES;
    IF COL_LENGTH(N'dbo.Users', N'ActiveVideoEngine') IS NULL
        ALTER TABLE dbo.Users ADD ActiveVideoEngine NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_ActiveVideoEngine DEFAULT N'volcano' WITH VALUES;

    IF COL_LENGTH(N'dbo.Projects', N'DramaId') IS NULL
        ALTER TABLE dbo.Projects ADD DramaId INT NULL;
    IF COL_LENGTH(N'dbo.Projects', N'EpisodeCount') IS NULL
        ALTER TABLE dbo.Projects ADD EpisodeCount INT NOT NULL CONSTRAINT DF_Projects_EpisodeCount DEFAULT 12 WITH VALUES;
    IF COL_LENGTH(N'dbo.Projects', N'CurrentBatch') IS NULL
        ALTER TABLE dbo.Projects ADD CurrentBatch INT NOT NULL CONSTRAINT DF_Projects_CurrentBatch DEFAULT 1 WITH VALUES;
    IF COL_LENGTH(N'dbo.Projects', N'CoverImage') IS NULL
        ALTER TABLE dbo.Projects ADD CoverImage NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.Projects', N'StyleId') IS NULL
        ALTER TABLE dbo.Projects ADD StyleId INT NULL;
    IF COL_LENGTH(N'dbo.Projects', N'Tags') IS NULL
        ALTER TABLE dbo.Projects ADD Tags NVARCHAR(1000) NULL;
    IF COL_LENGTH(N'dbo.Projects', N'VideoRatio') IS NULL
        ALTER TABLE dbo.Projects ADD VideoRatio NVARCHAR(10) NOT NULL CONSTRAINT DF_Projects_VideoRatio DEFAULT N'16:9' WITH VALUES;
    IF COL_LENGTH(N'dbo.Projects', N'VideoWatermark') IS NULL
        ALTER TABLE dbo.Projects ADD VideoWatermark BIT NOT NULL CONSTRAINT DF_Projects_VideoWatermark DEFAULT 0 WITH VALUES;
    IF COL_LENGTH(N'dbo.Projects', N'VideoAudio') IS NULL
        ALTER TABLE dbo.Projects ADD VideoAudio BIT NOT NULL CONSTRAINT DF_Projects_VideoAudio DEFAULT 1 WITH VALUES;
    IF COL_LENGTH(N'dbo.Projects', N'VideoResolution') IS NULL
        ALTER TABLE dbo.Projects ADD VideoResolution NVARCHAR(10) NOT NULL CONSTRAINT DF_Projects_VideoResolution DEFAULT N'720p' WITH VALUES;
    IF COL_LENGTH(N'dbo.Projects', N'TargetDurationText') IS NULL
        ALTER TABLE dbo.Projects ADD TargetDurationText NVARCHAR(64) NULL;

    IF COL_LENGTH(N'dbo.StageData', N'CurrentBatch') IS NULL
        ALTER TABLE dbo.StageData ADD CurrentBatch INT NOT NULL CONSTRAINT DF_StageData_CurrentBatch DEFAULT 1 WITH VALUES;
    IF COL_LENGTH(N'dbo.Episodes', N'BatchNumber') IS NULL
        ALTER TABLE dbo.Episodes ADD BatchNumber INT NOT NULL CONSTRAINT DF_Episodes_BatchNumber DEFAULT 1 WITH VALUES;

    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Duration') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD Duration NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'StartScene') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD StartScene NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'EndScene') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD EndScene NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'BatchNumber') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD BatchNumber INT NOT NULL CONSTRAINT DF_StoryboardFrames_BatchNumber DEFAULT 1 WITH VALUES;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitNumber') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD UnitNumber NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'EpisodeNumber') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD EpisodeNumber INT NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitType') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD UnitType NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'ShotNumber') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD ShotNumber NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'ShotSize') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD ShotSize NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'UnitOrder') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD UnitOrder INT NOT NULL CONSTRAINT DF_StoryboardFrames_UnitOrder DEFAULT 0 WITH VALUES;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'CombatBeatIndex') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD CombatBeatIndex INT NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'CombatBeatIds') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD CombatBeatIds NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Timeline') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD Timeline NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Composition') <> -1
        ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Composition NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Characters') <> -1
        ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Characters NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Camera') <> -1
        ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Camera NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Duration') <> -1
        ALTER TABLE dbo.StoryboardFrames ALTER COLUMN Duration NVARCHAR(MAX) NULL;

    IF COL_LENGTH(N'dbo.SeedancePrompts', N'LocalVideoUrl') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD LocalVideoUrl NVARCHAR(500) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'BatchNumber') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD BatchNumber INT NOT NULL CONSTRAINT DF_SeedancePrompts_BatchNumber DEFAULT 1 WITH VALUES;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'EpisodeNumber') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD EpisodeNumber INT NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'UnitName') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD UnitName NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'ShotLabel') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD ShotLabel NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'ShotType') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD ShotType NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'Duration') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD Duration INT NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'ReferenceImages') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD ReferenceImages NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'ReferenceVideos') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD ReferenceVideos NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'ReferenceAudio') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD ReferenceAudio NVARCHAR(MAX) NULL;

    IF COL_LENGTH(N'dbo.LLMConfigs', N'AutoEnhance') IS NULL
        ALTER TABLE dbo.LLMConfigs ADD AutoEnhance BIT NOT NULL CONSTRAINT DF_LLMConfigs_AutoEnhance DEFAULT 0 WITH VALUES;
    IF COL_LENGTH(N'dbo.LLMConfigs', N'UpdatedAt') IS NULL
        ALTER TABLE dbo.LLMConfigs ADD UpdatedAt DATETIME2 NULL;

    IF COL_LENGTH(N'dbo.ReferenceAssets', N'Tags') IS NULL
        ALTER TABLE dbo.ReferenceAssets ADD Tags NVARCHAR(500) NULL;
    IF COL_LENGTH(N'dbo.ReferenceAssets', N'FileSize') IS NULL
        ALTER TABLE dbo.ReferenceAssets ADD FileSize BIGINT NULL;

    IF COL_LENGTH(N'dbo.SkillLibrary', N'ProjectId') IS NULL
        ALTER TABLE dbo.SkillLibrary ADD ProjectId INT NULL;

    IF COL_LENGTH(N'dbo.VideoGenerationTasks', N'Engine') IS NULL
        ALTER TABLE dbo.VideoGenerationTasks ADD Engine NVARCHAR(20) NOT NULL CONSTRAINT DF_VideoTasks_Engine DEFAULT N'volcano' WITH VALUES;
    IF COL_LENGTH(N'dbo.VideoGenerationTasks', N'LocalVideoUrl') IS NULL
        ALTER TABLE dbo.VideoGenerationTasks ADD LocalVideoUrl NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.VideoGenerationTasks', N'ApiStatus') IS NULL
        ALTER TABLE dbo.VideoGenerationTasks ADD ApiStatus NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.VideoGenerationTasks', N'ResponseDuration') IS NULL
        ALTER TABLE dbo.VideoGenerationTasks ADD ResponseDuration FLOAT NULL;
    IF COL_LENGTH(N'dbo.VideoGenerationTasks', N'EnhanceDuration') IS NULL
        ALTER TABLE dbo.VideoGenerationTasks ADD EnhanceDuration INT NULL;
    IF COL_LENGTH(N'dbo.VideoGenerationTasks', N'EnhanceResolution') IS NULL
        ALTER TABLE dbo.VideoGenerationTasks ADD EnhanceResolution NVARCHAR(50) NULL;

    IF COL_LENGTH(N'dbo.EpisodeDirectorPlans', N'UnitEmotionCurveJson') IS NULL
        ALTER TABLE dbo.EpisodeDirectorPlans ADD UnitEmotionCurveJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitEmotionCurveJson DEFAULT N'[]' WITH VALUES;
    IF COL_LENGTH(N'dbo.EpisodeDirectorPlans', N'UnitTransitionsJson') IS NULL
        ALTER TABLE dbo.EpisodeDirectorPlans ADD UnitTransitionsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitTransitionsJson DEFAULT N'[]' WITH VALUES;
    IF COL_LENGTH(N'dbo.EpisodeDirectorPlans', N'UnitEndStatesJson') IS NULL
        ALTER TABLE dbo.EpisodeDirectorPlans ADD UnitEndStatesJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitEndStatesJson DEFAULT N'[]' WITH VALUES;
    IF COL_LENGTH(N'dbo.EpisodeDirectorPlans', N'ClimaxBudgetJson') IS NULL
        ALTER TABLE dbo.EpisodeDirectorPlans ADD ClimaxBudgetJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_ClimaxBudgetJson DEFAULT N'{}' WITH VALUES;
    IF COL_LENGTH(N'dbo.EpisodeDirectorStates', N'SmallClimaxCount') IS NULL
        ALTER TABLE dbo.EpisodeDirectorStates ADD SmallClimaxCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_SmallClimaxCount DEFAULT 0 WITH VALUES;
    IF COL_LENGTH(N'dbo.EpisodeDirectorStates', N'MidClimaxCount') IS NULL
        ALTER TABLE dbo.EpisodeDirectorStates ADD MidClimaxCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_MidClimaxCount DEFAULT 0 WITH VALUES;
    IF COL_LENGTH(N'dbo.EpisodeDirectorStates', N'LargeClimaxCount') IS NULL
        ALTER TABLE dbo.EpisodeDirectorStates ADD LargeClimaxCount INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_LargeClimaxCount DEFAULT 0 WITH VALUES;

    /* ---------- Safe widening only; never shrink historical columns ---------- */

    IF COL_LENGTH(N'dbo.Users', N'Nickname') > 0 AND COL_LENGTH(N'dbo.Users', N'Nickname') < 1000
        ALTER TABLE dbo.Users ALTER COLUMN Nickname NVARCHAR(500) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'UnitName') > 0 AND COL_LENGTH(N'dbo.SeedancePrompts', N'UnitName') < 400
        ALTER TABLE dbo.SeedancePrompts ALTER COLUMN UnitName NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'ShotLabel') > 0 AND COL_LENGTH(N'dbo.SeedancePrompts', N'ShotLabel') < 200
        ALTER TABLE dbo.SeedancePrompts ALTER COLUMN ShotLabel NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.ReferenceAssets', N'FileName') > 0 AND COL_LENGTH(N'dbo.ReferenceAssets', N'FileName') < 510
        ALTER TABLE dbo.ReferenceAssets ALTER COLUMN FileName NVARCHAR(255) NOT NULL;
    IF COL_LENGTH(N'dbo.ReferenceAssets', N'FileType') > 0 AND COL_LENGTH(N'dbo.ReferenceAssets', N'FileType') < 100
        ALTER TABLE dbo.ReferenceAssets ALTER COLUMN FileType NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.ReferenceAssets', N'Category') > 0 AND COL_LENGTH(N'dbo.ReferenceAssets', N'Category') < 200
        ALTER TABLE dbo.ReferenceAssets ALTER COLUMN Category NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.ReferenceAssets', N'SubCategory') > 0 AND COL_LENGTH(N'dbo.ReferenceAssets', N'SubCategory') < 200
        ALTER TABLE dbo.ReferenceAssets ALTER COLUMN SubCategory NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.VideoGenerationTasks', N'LocalVideoUrl') <> -1
        ALTER TABLE dbo.VideoGenerationTasks ALTER COLUMN LocalVideoUrl NVARCHAR(MAX) NULL;

    /* ---------- Normalize only values for which code already has the same fallback ---------- */

    UPDATE dbo.Projects SET CurrentBatch = 1 WHERE CurrentBatch IS NULL;
    UPDATE dbo.StageData SET CurrentBatch = 1 WHERE CurrentBatch IS NULL;
    UPDATE dbo.Episodes SET BatchNumber = 1 WHERE BatchNumber IS NULL;
    UPDATE dbo.StoryboardFrames SET BatchNumber = 1 WHERE BatchNumber IS NULL;
    UPDATE dbo.SeedancePrompts SET BatchNumber = 1 WHERE BatchNumber IS NULL;
    UPDATE dbo.SeedancePrompts SET EpisodeNumber = 0 WHERE EpisodeNumber IS NULL;
    UPDATE dbo.SeedancePrompts SET Duration = 11 WHERE Duration IS NULL;

    IF NOT EXISTS (SELECT 1 FROM dbo.Projects WHERE CurrentBatch IS NULL)
        ALTER TABLE dbo.Projects ALTER COLUMN CurrentBatch INT NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM dbo.StageData WHERE CurrentBatch IS NULL)
        ALTER TABLE dbo.StageData ALTER COLUMN CurrentBatch INT NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM dbo.Episodes WHERE BatchNumber IS NULL)
        ALTER TABLE dbo.Episodes ALTER COLUMN BatchNumber INT NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM dbo.StoryboardFrames WHERE BatchNumber IS NULL)
        ALTER TABLE dbo.StoryboardFrames ALTER COLUMN BatchNumber INT NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM dbo.SeedancePrompts WHERE BatchNumber IS NULL)
        ALTER TABLE dbo.SeedancePrompts ALTER COLUMN BatchNumber INT NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM dbo.SeedancePrompts WHERE EpisodeNumber IS NULL)
        ALTER TABLE dbo.SeedancePrompts ALTER COLUMN EpisodeNumber INT NOT NULL;
    IF NOT EXISTS (SELECT 1 FROM dbo.SeedancePrompts WHERE Duration IS NULL)
        ALTER TABLE dbo.SeedancePrompts ALTER COLUMN Duration INT NOT NULL;

    /* ---------- Infrastructure seed data only ---------- */

    IF NOT EXISTS (SELECT 1 FROM dbo.TokenUsageConfig WHERE Id = 1)
        INSERT INTO dbo.TokenUsageConfig(Id, QuotaTokens) VALUES(1, 14000000);

    IF NOT EXISTS (SELECT 1 FROM dbo.VideoStyles)
    BEGIN
        INSERT INTO dbo.VideoStyles(StyleName, StylePrompt, IsDefault) VALUES
            (N'米哈游（二次元）', N'米哈游二次元风格，画面色彩高饱和，柔光遍布，角色面部细节及表情刻画细腻', 0),
            (N'写实风格', N'写实主义风格，低饱和冷色调，细节克制，强调人物微表情与真实光影', 0),
            (N'日系动漫', N'日系动漫风格，线条干净流畅，色彩明快清新，角色大眼睛萌系表情', 0),
            (N'赛博朋克', N'赛博朋克风格，霓虹光效，高对比冷暖撞色，未来都市科技感', 0),
            (N'水墨古风', N'水墨古风风格，留白构图，淡雅墨色晕染，东方意境', 0),
            (N'星穹铁道PV风', N'崩坏星穹铁道PV风格：电影级日系动画电影质感，厚涂插画与精致渲染结合，画面完成度极高；角色肤色通透，发丝、衣物、瞳孔高光刻画细腻；背景大气透视+景深虚化，场景有厚重材质感；电影级布光，强调侧逆光、轮廓光、体积光，明暗对比强烈但整体色调统一；高饱和、低对比的统一色板，带轻微胶片颗粒与辉光；镜头语言富有张力，广角大透视、快速推拉、镜头光晕，强调氛围与情绪。', 1);
    END;

    EXEC(N'INSERT INTO dbo.SkillElements(UserId, Name, SortOrder) SELECT u.UserId, x.Name, x.SortOrder FROM dbo.Users u CROSS APPLY (VALUES (N''火'',1),(N''冰'',2),(N''雷'',3),(N''剑阵'',4),(N''风'',5),(N''暗'',6),(N''圣'',7)) x(Name, SortOrder) WHERE NOT EXISTS (SELECT 1 FROM dbo.SkillElements se WHERE se.UserId=u.UserId AND se.Name=x.Name);');

    /* ---------- Indexes ---------- */

    /* ---------- FightArcTemplates (战斗段落骨架库) ---------- */
    -- 建表与种子完整版见 ManhuaPipeline/Database/Upgrade_FightArcTemplates.sql；此处保证结构就绪。
    IF OBJECT_ID(N'dbo.FightArcTemplates', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FightArcTemplates (
            FightArcTemplateId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FightArcTemplates PRIMARY KEY,
            ArcTypeId            NVARCHAR(50) NOT NULL,
            Name                 NVARCHAR(100) NOT NULL,
            Description          NVARCHAR(500) NOT NULL CONSTRAINT DF_FightArcTemplates_Description DEFAULT N'',
            Version              NVARCHAR(20) NOT NULL CONSTRAINT DF_FightArcTemplates_Version DEFAULT N'1.0',
            PhasesJson           NVARCHAR(MAX) NOT NULL CONSTRAINT DF_FightArcTemplates_PhasesJson DEFAULT N'[]',
            DurationBudgetJson   NVARCHAR(MAX) NOT NULL CONSTRAINT DF_FightArcTemplates_DurationBudgetJson DEFAULT N'{}',
            RulesJson            NVARCHAR(MAX) NOT NULL CONSTRAINT DF_FightArcTemplates_RulesJson DEFAULT N'[]',
            Status               NVARCHAR(20) NOT NULL CONSTRAINT DF_FightArcTemplates_Status DEFAULT N'Active',
            CreatedAt            DATETIME2 NOT NULL CONSTRAINT DF_FightArcTemplates_CreatedAt DEFAULT GETDATE(),
            UpdatedAt            DATETIME2 NOT NULL CONSTRAINT DF_FightArcTemplates_UpdatedAt DEFAULT GETDATE(),
            CONSTRAINT UQ_FightArcTemplates_ArcType UNIQUE (ArcTypeId)
        );
    END;
    IF COL_LENGTH(N'dbo.DirectorPlans', N'FightArcType') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD FightArcType NVARCHAR(50) NOT NULL CONSTRAINT DF_DirectorPlans_FightArcType DEFAULT N'' WITH VALUES;
    IF COL_LENGTH(N'dbo.DirectorPlans', N'FightSequenceJson') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD FightSequenceJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_FightSequenceJson DEFAULT N'' WITH VALUES;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Projects') AND name=N'IX_Projects_UserId')
        CREATE INDEX IX_Projects_UserId ON dbo.Projects(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Projects') AND name=N'IX_Projects_DramaId')
        CREATE INDEX IX_Projects_DramaId ON dbo.Projects(DramaId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Dramas') AND name=N'IX_Dramas_UserId')
        CREATE INDEX IX_Dramas_UserId ON dbo.Dramas(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Episodes') AND name=N'IX_Episodes_Project')
        CREATE INDEX IX_Episodes_Project ON dbo.Episodes(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.StoryboardFrames') AND name=N'IX_Frames_Episode')
        CREATE INDEX IX_Frames_Episode ON dbo.StoryboardFrames(EpisodeId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.StoryboardFrames') AND name=N'IX_Frames_Project')
        CREATE INDEX IX_Frames_Project ON dbo.StoryboardFrames(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.StoryboardFrames') AND name=N'IX_Frames_Unit')
        CREATE INDEX IX_Frames_Unit ON dbo.StoryboardFrames(ProjectId, EpisodeId, UnitNumber);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.CharacterAssets') AND name=N'IX_CharAssets_Project')
        CREATE INDEX IX_CharAssets_Project ON dbo.CharacterAssets(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PropAssets') AND name=N'IX_PropAssets_Project')
        CREATE INDEX IX_PropAssets_Project ON dbo.PropAssets(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EnvironmentAssets') AND name=N'IX_EnvAssets_Project')
        CREATE INDEX IX_EnvAssets_Project ON dbo.EnvironmentAssets(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EffectAssets') AND name=N'IX_EffectAssets_Project')
        CREATE INDEX IX_EffectAssets_Project ON dbo.EffectAssets(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SeedancePrompts') AND name=N'IX_Prompts_Project')
        CREATE INDEX IX_Prompts_Project ON dbo.SeedancePrompts(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ReferenceAssets') AND name=N'IX_RefAssets_User')
        CREATE INDEX IX_RefAssets_User ON dbo.ReferenceAssets(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SkillLibrary') AND name=N'IX_SkillLibrary_User')
        CREATE INDEX IX_SkillLibrary_User ON dbo.SkillLibrary(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SkillLibrary') AND name=N'IX_SkillLibrary_Project')
        EXEC(N'CREATE INDEX IX_SkillLibrary_Project ON dbo.SkillLibrary(ProjectId);');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.FightTemplate') AND name=N'IX_FightTemplate_User')
        CREATE INDEX IX_FightTemplate_User ON dbo.FightTemplate(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SkillElements') AND name=N'UX_SkillElements_User_Name')
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.SkillElements GROUP BY UserId,Name HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_SKILL_ELEMENT',N'存在重复的 UserId + Name，已跳过唯一索引 UX_SkillElements_User_Name。');
        ELSE
            EXEC(N'CREATE UNIQUE INDEX UX_SkillElements_User_Name ON dbo.SkillElements(UserId,Name);');
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ShotDirective') AND name=N'IX_ShotDirective_User')
        CREATE INDEX IX_ShotDirective_User ON dbo.ShotDirective(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.DirectorPlans') AND name=N'IX_DirectorPlans_Project')
        CREATE INDEX IX_DirectorPlans_Project ON dbo.DirectorPlans(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.DirectorPlans') AND name=N'UX_DirectorPlans_Project_Unit')
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.DirectorPlans GROUP BY ProjectId,UnitNumber HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_DIRECTOR_PLAN',N'存在重复的 ProjectId + UnitNumber，已跳过唯一索引 UX_DirectorPlans_Project_Unit。');
        ELSE
            CREATE UNIQUE INDEX UX_DirectorPlans_Project_Unit ON dbo.DirectorPlans(ProjectId,UnitNumber);
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EpisodeDirectorPlans') AND name=N'UX_EpisodeDirectorPlans_Project_Episode')
        CREATE UNIQUE INDEX UX_EpisodeDirectorPlans_Project_Episode ON dbo.EpisodeDirectorPlans(ProjectId,EpisodeNumber);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EpisodeDirectorStates') AND name=N'UX_EpisodeDirectorStates_Project_Episode')
        CREATE UNIQUE INDEX UX_EpisodeDirectorStates_Project_Episode ON dbo.EpisodeDirectorStates(ProjectId,EpisodeNumber);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.EpisodeUnitStateSnapshots') AND name=N'UX_EpisodeUnitStateSnapshots_Project_Episode_Unit')
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.EpisodeUnitStateSnapshots GROUP BY ProjectId,EpisodeNumber,UnitNumber HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_UNIT_SNAPSHOT',N'存在重复的 ProjectId + EpisodeNumber + UnitNumber，已跳过唯一索引 UX_EpisodeUnitStateSnapshots_Project_Episode_Unit。');
        ELSE
            CREATE UNIQUE INDEX UX_EpisodeUnitStateSnapshots_Project_Episode_Unit ON dbo.EpisodeUnitStateSnapshots(ProjectId,EpisodeNumber,UnitNumber);
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Works') AND name=N'IX_Works_User')
        CREATE INDEX IX_Works_User ON dbo.Works(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.VideoGenerationTasks') AND name=N'IX_VideoTasks_Project')
        CREATE INDEX IX_VideoTasks_Project ON dbo.VideoGenerationTasks(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.VideoGenerationTasks') AND name=N'IX_VideoTasks_Prompt_CreatedAt')
        CREATE INDEX IX_VideoTasks_Prompt_CreatedAt ON dbo.VideoGenerationTasks(PromptId, CreatedAt DESC);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.VideoGenerationTasks') AND name=N'IX_VideoTasks_TaskId')
        CREATE INDEX IX_VideoTasks_TaskId ON dbo.VideoGenerationTasks(TaskId);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.StageData') AND name=N'UX_StageData_Project_Stage')
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.StageData GROUP BY ProjectId,StageNumber HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_STAGE_DATA',N'存在重复的 ProjectId + StageNumber，已跳过唯一索引 UX_StageData_Project_Stage。');
        ELSE
            CREATE UNIQUE INDEX UX_StageData_Project_Stage ON dbo.StageData(ProjectId,StageNumber);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.CoherenceChecks') AND name=N'UX_CoherenceChecks_Project')
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.CoherenceChecks GROUP BY ProjectId HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_COHERENCE',N'存在同一项目多条衔接检查，已跳过唯一索引 UX_CoherenceChecks_Project。');
        ELSE
            CREATE UNIQUE INDEX UX_CoherenceChecks_Project ON dbo.CoherenceChecks(ProjectId);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.LLMConfigs') AND name=N'UX_LLMConfigs_User_Provider')
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.LLMConfigs GROUP BY UserId,Provider HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_LLM_CONFIG',N'存在重复的 UserId + Provider，已跳过唯一索引 UX_LLMConfigs_User_Provider。');
        ELSE
            CREATE UNIQUE INDEX UX_LLMConfigs_User_Provider ON dbo.LLMConfigs(UserId,Provider);
    END;

    /* ---------- Foreign keys: add only when historical rows are clean ---------- */

    DECLARE @ForeignKeys TABLE (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ConstraintName SYSNAME NOT NULL,
        ParentTable SYSNAME NOT NULL,
        ParentColumn SYSNAME NOT NULL,
        ReferencedTable SYSNAME NOT NULL,
        ReferencedColumn SYSNAME NOT NULL,
        DeleteAction NVARCHAR(10) NOT NULL
    );

    INSERT INTO @ForeignKeys(ConstraintName,ParentTable,ParentColumn,ReferencedTable,ReferencedColumn,DeleteAction) VALUES
        (N'FK_Dramas_Users',N'Dramas',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
        (N'FK_Projects_Users',N'Projects',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
        (N'FK_Projects_Dramas',N'Projects',N'DramaId',N'Dramas',N'DramaId',N'NO_ACTION'),
        (N'FK_Projects_VideoStyles',N'Projects',N'StyleId',N'VideoStyles',N'StyleId',N'NO_ACTION'),
        (N'FK_StageData_Projects',N'StageData',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_Episodes_Projects',N'Episodes',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_Episodes_Users',N'Episodes',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
        (N'FK_StoryboardFrames_Episodes',N'StoryboardFrames',N'EpisodeId',N'Episodes',N'EpisodeId',N'CASCADE'),
        (N'FK_StoryboardFrames_Projects',N'StoryboardFrames',N'ProjectId',N'Projects',N'ProjectId',N'NO_ACTION'),
        (N'FK_CharacterAssets_Projects',N'CharacterAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_PropAssets_Projects',N'PropAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_EnvironmentAssets_Projects',N'EnvironmentAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_EffectAssets_Projects',N'EffectAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_SeedancePrompts_Projects',N'SeedancePrompts',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_CoherenceChecks_Projects',N'CoherenceChecks',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_LLMConfigs_Users',N'LLMConfigs',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
        (N'FK_ReferenceAssets_Users',N'ReferenceAssets',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
        (N'FK_SkillLibrary_Users',N'SkillLibrary',N'UserId',N'Users',N'UserId',N'CASCADE'),
        (N'FK_SkillLibrary_Projects',N'SkillLibrary',N'ProjectId',N'Projects',N'ProjectId',N'NO_ACTION'),
        (N'FK_SkillElements_Users',N'SkillElements',N'UserId',N'Users',N'UserId',N'CASCADE'),
        (N'FK_FightTemplate_Users',N'FightTemplate',N'UserId',N'Users',N'UserId',N'CASCADE'),
        (N'FK_ShotDirective_Users',N'ShotDirective',N'UserId',N'Users',N'UserId',N'CASCADE'),
        (N'FK_DirectorPlans_Projects',N'DirectorPlans',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_EpisodeDirectorPlans_Projects',N'EpisodeDirectorPlans',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_EpisodeDirectorStates_Projects',N'EpisodeDirectorStates',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_EpisodeUnitStateSnapshots_Projects',N'EpisodeUnitStateSnapshots',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
        (N'FK_Works_Users',N'Works',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
        (N'FK_VideoTasks_Projects',N'VideoGenerationTasks',N'ProjectId',N'Projects',N'ProjectId',N'NO_ACTION'),
        (N'FK_VideoTasks_Prompts',N'VideoGenerationTasks',N'PromptId',N'SeedancePrompts',N'PromptId',N'CASCADE'),
        (N'FK_FrameAssetBindings_Frames',N'FrameAssetBindings',N'FrameId',N'StoryboardFrames',N'FrameId',N'CASCADE');

    DECLARE @FkId INT=1, @FkMax INT=(SELECT MAX(Id) FROM @ForeignKeys);
    DECLARE @ConstraintName SYSNAME, @ParentTable SYSNAME, @ParentColumn SYSNAME;
    DECLARE @ReferencedTable SYSNAME, @ReferencedColumn SYSNAME, @DeleteAction NVARCHAR(10);
    DECLARE @Sql NVARCHAR(MAX), @OrphanCount BIGINT, @ExistingDeleteAction NVARCHAR(60);

    WHILE @FkId <= @FkMax
    BEGIN
        SELECT @ConstraintName=ConstraintName,@ParentTable=ParentTable,@ParentColumn=ParentColumn,
               @ReferencedTable=ReferencedTable,@ReferencedColumn=ReferencedColumn,@DeleteAction=DeleteAction
        FROM @ForeignKeys WHERE Id=@FkId;

        SET @ExistingDeleteAction = NULL;
        SELECT TOP (1) @ExistingDeleteAction=fk.delete_referential_action_desc
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
        JOIN sys.columns pc ON pc.object_id=fk.parent_object_id AND pc.column_id=fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id=fk.referenced_object_id AND rc.column_id=fkc.referenced_column_id
        WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id)=N'dbo'
          AND OBJECT_NAME(fk.parent_object_id)=@ParentTable
          AND pc.name=@ParentColumn
          AND OBJECT_NAME(fk.referenced_object_id)=@ReferencedTable
          AND rc.name=@ReferencedColumn;

        IF @ExistingDeleteAction IS NULL
        BEGIN
            SET @OrphanCount=0;
            SET @Sql=N'SELECT @Count=COUNT_BIG(*) FROM dbo.'+QUOTENAME(@ParentTable)+N' p LEFT JOIN dbo.'+QUOTENAME(@ReferencedTable)+N' r ON r.'+QUOTENAME(@ReferencedColumn)+N'=p.'+QUOTENAME(@ParentColumn)+N' WHERE p.'+QUOTENAME(@ParentColumn)+N' IS NOT NULL AND r.'+QUOTENAME(@ReferencedColumn)+N' IS NULL;';
            EXEC sys.sp_executesql @Sql,N'@Count BIGINT OUTPUT',@Count=@OrphanCount OUTPUT;

            IF @OrphanCount=0
            BEGIN
                SET @Sql=N'ALTER TABLE dbo.'+QUOTENAME(@ParentTable)+N' WITH CHECK ADD CONSTRAINT '+QUOTENAME(@ConstraintName)+N' FOREIGN KEY ('+QUOTENAME(@ParentColumn)+N') REFERENCES dbo.'+QUOTENAME(@ReferencedTable)+N'('+QUOTENAME(@ReferencedColumn)+N')'+CASE WHEN @DeleteAction=N'CASCADE' THEN N' ON DELETE CASCADE' ELSE N'' END+N'; ALTER TABLE dbo.'+QUOTENAME(@ParentTable)+N' CHECK CONSTRAINT '+QUOTENAME(@ConstraintName)+N';';
                EXEC sys.sp_executesql @Sql;
            END
            ELSE
                INSERT INTO @Warnings(Severity,Code,Details)
                VALUES(N'P1',N'ORPHAN_'+UPPER(@ParentTable)+N'_'+UPPER(@ParentColumn),N'发现 '+CONVERT(NVARCHAR(30),@OrphanCount)+N' 条孤儿记录，已跳过外键 '+@ConstraintName+N'。');
        END
        ELSE IF @ExistingDeleteAction<>@DeleteAction
            INSERT INTO @Warnings(Severity,Code,Details)
            VALUES(N'P2',N'FK_DELETE_RULE_'+UPPER(@ParentTable)+N'_'+UPPER(@ParentColumn),N'现有外键删除规则为 '+@ExistingDeleteAction+N'，目标为 '+@DeleteAction+N'；本脚本不会删除并重建历史约束。');

        SET @FkId+=1;
    END;

    IF EXISTS (SELECT 1 FROM dbo.Projects WHERE DramaId IS NULL)
        INSERT INTO @Warnings(Severity,Code,Details)
        SELECT N'P1',N'PROJECT_WITHOUT_DRAMA',N'仍有 '+CONVERT(NVARCHAR(30),COUNT_BIG(*))+N' 个历史项目没有 DramaId；脚本未自动创建或归类漫剧。' FROM dbo.Projects WHERE DramaId IS NULL;

    /* ==========================================================================
       2026-09-12 同步：按正式库（ManhuaPipeline）实际结构补齐本节内容，
       与 Baseline.sql 逐项一致。此前这些结构只存在于 ManhuaPipeline\Database\Upgrade_*.sql，
       历史库跑完本脚本后仍未补齐，会被 DatabaseSchemaValidator 在启动时拦截。
       ========================================================================== */

    IF COL_LENGTH(N'dbo.Projects', N'LibraryCategory') IS NULL
        ALTER TABLE dbo.Projects ADD LibraryCategory NVARCHAR(50) NULL;
    IF COL_LENGTH(N'dbo.ReferenceAssets', N'SourceKey') IS NULL
        ALTER TABLE dbo.ReferenceAssets ADD SourceKey NVARCHAR(200) NULL;
    IF COL_LENGTH(N'dbo.CharacterAssets', N'ImagePrompt') IS NULL
        ALTER TABLE dbo.CharacterAssets ADD ImagePrompt NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.CharacterAssets', N'NegativePrompt') IS NULL
        ALTER TABLE dbo.CharacterAssets ADD NegativePrompt NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.PropAssets', N'ImagePrompt') IS NULL
        ALTER TABLE dbo.PropAssets ADD ImagePrompt NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.PropAssets', N'NegativePrompt') IS NULL
        ALTER TABLE dbo.PropAssets ADD NegativePrompt NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.EnvironmentAssets', N'ImagePrompt') IS NULL
        ALTER TABLE dbo.EnvironmentAssets ADD ImagePrompt NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.EnvironmentAssets', N'NegativePrompt') IS NULL
        ALTER TABLE dbo.EnvironmentAssets ADD NegativePrompt NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.EffectAssets', N'ImagePrompt') IS NULL
        ALTER TABLE dbo.EffectAssets ADD ImagePrompt NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.EffectAssets', N'NegativePrompt') IS NULL
        ALTER TABLE dbo.EffectAssets ADD NegativePrompt NVARCHAR(MAX) NULL;

    IF COL_LENGTH(N'dbo.DirectorPlans', N'CombatRoundCount') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD CombatRoundCount INT NOT NULL CONSTRAINT DF_DirectorPlans_CombatRoundCount DEFAULT 0;
    IF COL_LENGTH(N'dbo.DirectorPlans', N'VfxPeakPhase') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD VfxPeakPhase NVARCHAR(20) NOT NULL CONSTRAINT DF_DirectorPlans_VfxPeakPhase DEFAULT N'';
    IF COL_LENGTH(N'dbo.DirectorPlans', N'ActionPlan') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD ActionPlan NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_ActionPlan DEFAULT N'';
    IF COL_LENGTH(N'dbo.DirectorPlans', N'NeedsReview') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD NeedsReview BIT NOT NULL CONSTRAINT DF_DirectorPlans_NeedsReview DEFAULT 0;
    IF COL_LENGTH(N'dbo.DirectorPlans', N'ValidationScore') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD ValidationScore INT NOT NULL CONSTRAINT DF_DirectorPlans_ValidationScore DEFAULT 0;
    IF COL_LENGTH(N'dbo.DirectorPlans', N'ViolationsJson') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD ViolationsJson NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_ViolationsJson DEFAULT N'';
    IF COL_LENGTH(N'dbo.DirectorPlans', N'RepairCount') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD RepairCount INT NOT NULL CONSTRAINT DF_DirectorPlans_RepairCount DEFAULT 0;
    IF COL_LENGTH(N'dbo.DirectorPlans', N'LastValidationAt') IS NULL
        ALTER TABLE dbo.DirectorPlans ADD LastValidationAt DATETIME NULL;

    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Skills') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD Skills NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.StoryboardFrames', N'Scene') IS NULL
        ALTER TABLE dbo.StoryboardFrames ADD Scene NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'ShotNumber') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD ShotNumber INT NOT NULL CONSTRAINT DF_SeedancePrompts_ShotNumber DEFAULT 0;
    IF COL_LENGTH(N'dbo.SeedancePrompts', N'PromptTextH3') IS NULL
        ALTER TABLE dbo.SeedancePrompts ADD PromptTextH3 NVARCHAR(MAX) NULL;
    IF COL_LENGTH(N'dbo.LLMConfigs', N'ThinkingMode') IS NULL
        ALTER TABLE dbo.LLMConfigs ADD ThinkingMode NVARCHAR(20) NULL;
    IF COL_LENGTH(N'dbo.SkillLibrary', N'OwnerCharacter') IS NULL
        ALTER TABLE dbo.SkillLibrary ADD OwnerCharacter NVARCHAR(100) NULL;

    IF OBJECT_ID(N'dbo.StageProgressLogs', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StageProgressLogs (
            ProgressLogId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StageProgressLogs PRIMARY KEY,
            ProjectId INT NOT NULL,
            StageNumber INT NOT NULL,
            LogText NVARCHAR(1000) NOT NULL,
            CreatedAt DATETIME NOT NULL CONSTRAINT DF_StageProgressLogs_CreatedAt DEFAULT GETDATE()
        );
    END;

    /* CameraAtom 的出厂原子数据由 ManhuaPipeline\Database\Upgrade_CameraAtom.sql 负责种入。 */
    IF OBJECT_ID(N'dbo.CameraAtom', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CameraAtom (
            AtomId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CameraAtom PRIMARY KEY,
            UserId INT NOT NULL,
            Category NVARCHAR(20) NOT NULL,
            Name NVARCHAR(50) NOT NULL,
            Description NVARCHAR(600) NOT NULL,
            Tags NVARCHAR(200) NULL,
            CreatedAt DATETIME2 NULL CONSTRAINT DF_CameraAtom_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.FrameAssetBindings', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FrameAssetBindings (
            BindingId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FrameAssetBindings PRIMARY KEY,
            ProjectId INT NOT NULL,
            FrameId INT NOT NULL,
            Category NVARCHAR(20) NOT NULL,
            AssetId INT NOT NULL,
            Name NVARCHAR(100) NOT NULL,
            HasImage BIT NOT NULL CONSTRAINT DF_FrameAssetBindings_HasImage DEFAULT 0,
            SortOrder INT NOT NULL CONSTRAINT DF_FrameAssetBindings_SortOrder DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_FrameAssetBindings_CreatedAt DEFAULT GETDATE()
        );
    END;

    IF OBJECT_ID(N'dbo.UnitAssetBindings', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.UnitAssetBindings (
            BindingId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UnitAssetBindings PRIMARY KEY,
            ProjectId INT NOT NULL,
            EpisodeNumber INT NOT NULL,
            UnitNumber NVARCHAR(64) NOT NULL,
            Category NVARCHAR(16) NOT NULL,
            AssetId INT NOT NULL,
            Name NVARCHAR(200) NOT NULL,
            HasImage BIT NOT NULL CONSTRAINT DF_UnitAssetBindings_HasImage DEFAULT 0,
            SortOrder INT NOT NULL CONSTRAINT DF_UnitAssetBindings_SortOrder DEFAULT 0
        );
    END;

    IF OBJECT_ID(N'dbo.VoiceReferences', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.VoiceReferences (
            VoiceId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VoiceReferences PRIMARY KEY,
            ProjectId INT NOT NULL,
            CharacterName NVARCHAR(200) NOT NULL,
            AudioUrl NVARCHAR(1000) NOT NULL,
            OriginalFileName NVARCHAR(400) NULL,
            CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_VoiceReferences_CreatedAt DEFAULT SYSDATETIME()
        );
    END;

    /* ProjectId = 0 表示账号级默认模版，>0 表示该剧专属覆盖。 */
    IF OBJECT_ID(N'dbo.AssetPromptTemplates', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.AssetPromptTemplates (
            TemplateId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AssetPromptTemplates PRIMARY KEY,
            UserId INT NOT NULL,
            ProjectId INT NOT NULL CONSTRAINT DF_AssetPromptTemplates_ProjectId DEFAULT 0,
            Category NVARCHAR(20) NOT NULL,
            StyleLock NVARCHAR(MAX) NULL,
            NegativePrompt NVARCHAR(MAX) NULL,
            RuleText NVARCHAR(MAX) NULL,
            Enabled BIT NOT NULL CONSTRAINT DF_AssetPromptTemplates_Enabled DEFAULT 1,
            UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_AssetPromptTemplates_UpdatedAt DEFAULT SYSDATETIME()
        );
    END;

    IF OBJECT_ID(N'dbo.AssetImageTasks', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.AssetImageTasks (
            TaskId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AssetImageTasks PRIMARY KEY,
            ProjectId INT NOT NULL,
            UserId INT NOT NULL,
            Category NVARCHAR(32) NOT NULL,
            AssetId INT NOT NULL,
            AssetName NVARCHAR(200) NULL,
            Status NVARCHAR(16) NOT NULL CONSTRAINT DF_AssetImageTasks_Status DEFAULT N'queued',
            PromptOverride NVARCHAR(MAX) NULL,
            NegativeOverride NVARCHAR(MAX) NULL,
            ExtraPrompt NVARCHAR(MAX) NULL,
            Size NVARCHAR(16) NULL,
            ImageUrl NVARCHAR(400) NULL,
            LibraryAssetId INT NULL,
            UsedPrompt NVARCHAR(MAX) NULL,
            ErrorMessage NVARCHAR(MAX) NULL,
            CreatedAt DATETIME NOT NULL CONSTRAINT DF_AssetImageTasks_CreatedAt DEFAULT GETDATE(),
            StartedAt DATETIME NULL,
            FinishedAt DATETIME NULL
        );
    END;

    /* L2 连续性层六类表（权威来源 ManhuaPipeline/Database/Upgrade_连续性表.sql）。
       TableType 取值：SceneSpace / CharacterContinuity / PropState / ClueReveal / ActionCausality / TransitionMotive。
       EpisodeNumber = 0 表示全片（整季）级，> 0 表示该集级。 */
    IF OBJECT_ID(N'dbo.ProjectContinuityTables', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ProjectContinuityTables (
            ContinuityId   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProjectContinuityTables PRIMARY KEY,
            ProjectId      INT NOT NULL,
            EpisodeNumber  INT NOT NULL CONSTRAINT DF_ProjectContinuityTables_EpisodeNumber DEFAULT 0,
            TableType      NVARCHAR(40) NOT NULL,
            ContentJson    NVARCHAR(MAX) NOT NULL CONSTRAINT DF_ProjectContinuityTables_ContentJson DEFAULT N'{}',
            ContentText    NVARCHAR(MAX) NULL,
            Source         NVARCHAR(20) NOT NULL CONSTRAINT DF_ProjectContinuityTables_Source DEFAULT N'llm',
            CreatedAt      DATETIME2(0) NOT NULL CONSTRAINT DF_ProjectContinuityTables_CreatedAt DEFAULT SYSDATETIME(),
            UpdatedAt      DATETIME2(0) NOT NULL CONSTRAINT DF_ProjectContinuityTables_UpdatedAt DEFAULT SYSDATETIME()
        );
    END;

    /* L3 关键帧层（权威来源 ManhuaPipeline/Database/Upgrade_关键帧层.sql）。
       每剧情节点一张关键帧（8-16 张/集），人工触发生成，阶段 9 按集注入【关键帧锚定】；
       EpisodeNumber = 0 表示全片（整季）级。该表初始为空，基线不种数据。 */
    IF OBJECT_ID(N'dbo.ProjectKeyframes', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ProjectKeyframes (
            KeyframeId            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProjectKeyframes PRIMARY KEY,
            ProjectId             INT NOT NULL,
            EpisodeNumber         INT NOT NULL CONSTRAINT DF_ProjectKeyframes_EpisodeNumber DEFAULT 0,
            SortOrder             INT NOT NULL CONSTRAINT DF_ProjectKeyframes_SortOrder DEFAULT 0,
            NodeLabel             NVARCHAR(100) NULL,
            NodeReason            NVARCHAR(400) NULL,
            ShotLabel             NVARCHAR(40) NULL,
            UnitNumber            NVARCHAR(40) NULL,
            Composition           NVARCHAR(1000) NULL,
            LockedCharacters      NVARCHAR(1000) NULL,
            LockedProps           NVARCHAR(1000) NULL,
            LockedSceneDirection  NVARCHAR(1000) NULL,
            ClueVisible           NVARCHAR(500) NULL,
            NextConnection        NVARCHAR(1000) NULL,
            ImagePrompt           NVARCHAR(MAX) NULL,
            Status                NVARCHAR(20) NOT NULL CONSTRAINT DF_ProjectKeyframes_Status DEFAULT N'draft',
            Source                NVARCHAR(20) NOT NULL CONSTRAINT DF_ProjectKeyframes_Source DEFAULT N'llm',
            CreatedAt             DATETIME2(0) NOT NULL CONSTRAINT DF_ProjectKeyframes_CreatedAt DEFAULT SYSDATETIME(),
            UpdatedAt             DATETIME2(0) NOT NULL CONSTRAINT DF_ProjectKeyframes_UpdatedAt DEFAULT SYSDATETIME()
        );
    END;

    /* ---------- 新增索引：唯一索引沿用上面的"先查重复再建"策略 ---------- */

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_RefAssets_User_SourceKey' AND object_id=OBJECT_ID(N'dbo.ReferenceAssets'))
        CREATE INDEX IX_RefAssets_User_SourceKey ON dbo.ReferenceAssets(UserId,SourceKey);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_CameraAtom_User' AND object_id=OBJECT_ID(N'dbo.CameraAtom'))
        CREATE INDEX IX_CameraAtom_User ON dbo.CameraAtom(UserId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_FAB_Project_Frame' AND object_id=OBJECT_ID(N'dbo.FrameAssetBindings'))
        CREATE INDEX IX_FAB_Project_Frame ON dbo.FrameAssetBindings(ProjectId,FrameId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_FAB_Frame' AND object_id=OBJECT_ID(N'dbo.FrameAssetBindings'))
        CREATE INDEX IX_FAB_Frame ON dbo.FrameAssetBindings(FrameId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_UnitAssetBindings_Project' AND object_id=OBJECT_ID(N'dbo.UnitAssetBindings'))
        CREATE INDEX IX_UnitAssetBindings_Project ON dbo.UnitAssetBindings(ProjectId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_UnitAssetBindings_Unit' AND object_id=OBJECT_ID(N'dbo.UnitAssetBindings'))
        CREATE INDEX IX_UnitAssetBindings_Unit ON dbo.UnitAssetBindings(ProjectId,EpisodeNumber,UnitNumber);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_VoiceReferences_Project' AND object_id=OBJECT_ID(N'dbo.VoiceReferences'))
        CREATE INDEX IX_VoiceReferences_Project ON dbo.VoiceReferences(ProjectId);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'UX_VoiceReferences_Project_Character' AND object_id=OBJECT_ID(N'dbo.VoiceReferences'))
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.VoiceReferences GROUP BY ProjectId,CharacterName HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_VOICE_REFERENCE',N'存在重复的 ProjectId + CharacterName，已跳过唯一索引 UX_VoiceReferences_Project_Character。');
        ELSE
            CREATE UNIQUE INDEX UX_VoiceReferences_Project_Character ON dbo.VoiceReferences(ProjectId,CharacterName);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'UX_AssetPromptTemplates_User_Project_Category' AND object_id=OBJECT_ID(N'dbo.AssetPromptTemplates'))
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.AssetPromptTemplates GROUP BY UserId,ProjectId,Category HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_ASSET_PROMPT_TEMPLATE',N'存在重复的 UserId + ProjectId + Category，已跳过唯一索引 UX_AssetPromptTemplates_User_Project_Category。');
        ELSE
            CREATE UNIQUE INDEX UX_AssetPromptTemplates_User_Project_Category ON dbo.AssetPromptTemplates(UserId,ProjectId,Category);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_AssetImageTasks_Status' AND object_id=OBJECT_ID(N'dbo.AssetImageTasks'))
        CREATE INDEX IX_AssetImageTasks_Status ON dbo.AssetImageTasks(Status,TaskId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_AssetImageTasks_Project' AND object_id=OBJECT_ID(N'dbo.AssetImageTasks'))
        CREATE INDEX IX_AssetImageTasks_Project ON dbo.AssetImageTasks(ProjectId,TaskId DESC);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_ProjectContinuityTables_Project' AND object_id=OBJECT_ID(N'dbo.ProjectContinuityTables'))
        CREATE INDEX IX_ProjectContinuityTables_Project ON dbo.ProjectContinuityTables(ProjectId);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'UX_ProjectContinuityTables_Project_Type' AND object_id=OBJECT_ID(N'dbo.ProjectContinuityTables'))
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.ProjectContinuityTables GROUP BY ProjectId,EpisodeNumber,TableType HAVING COUNT(*)>1)
            INSERT INTO @Warnings(Severity,Code,Details) VALUES(N'P1',N'DUPLICATE_PROJECT_CONTINUITY',N'存在重复的 ProjectId + EpisodeNumber + TableType，已跳过唯一索引 UX_ProjectContinuityTables_Project_Type。');
        ELSE
            CREATE UNIQUE INDEX UX_ProjectContinuityTables_Project_Type ON dbo.ProjectContinuityTables(ProjectId,EpisodeNumber,TableType);
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_ProjectKeyframes_Project' AND object_id=OBJECT_ID(N'dbo.ProjectKeyframes'))
        CREATE INDEX IX_ProjectKeyframes_Project ON dbo.ProjectKeyframes(ProjectId,EpisodeNumber,SortOrder);

    COMMIT TRANSACTION;

    SELECT WarningId,Severity,Code,Details FROM @Warnings ORDER BY WarningId;
    SELECT
        DB_NAME() AS DatabaseName,
        (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=N'dbo' AND t.is_ms_shipped=0) AS UserTableCount,
        (SELECT COUNT(*) FROM @Warnings) AS WarningCount,
        GETDATE() AS CompletedAt;

    PRINT N'Upgrade_Current.sql 执行完成。请检查警告结果，并继续运行 Verify.sql。';
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
