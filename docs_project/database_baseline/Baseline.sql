/*
    ManhuaPipeline clean database baseline
    Version: 2026-08-17
    Target: SQL Server 2016+ / compatibility level 130+

    IMPORTANT:
    - Connect to a newly-created, empty business database before running.
    - This script refuses to run when any dbo user table already exists.
    - This script does not create logins, users, API keys, or business accounts.
    - Stage IDs stay fixed. Execution order is 1-8 -> 11 -> 9 -> 10.
    - Stage 11 is optional; no StageData row is required for it.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
BEGIN
    THROW 51000, N'请选择新建的空业务数据库后再执行 Baseline.sql。', 1;
END;

IF EXISTS (
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'dbo' AND t.is_ms_shipped = 0
)
BEGIN
    THROW 51001, N'目标数据库不是空库；Baseline.sql 已拒绝执行。历史数据库请使用 Upgrade_Current.sql。', 1;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.Users (
        UserId             INT IDENTITY(1,1) NOT NULL,
        Username           NVARCHAR(50) NOT NULL,
        Email              NVARCHAR(100) NOT NULL,
        PasswordHash       NVARCHAR(256) NOT NULL,
        Nickname           NVARCHAR(500) NULL,
        Avatar             NVARCHAR(500) NULL,
        Role               NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_Role DEFAULT N'user',
        IsActive           BIT NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1,
        ActiveLLMProvider  NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_ActiveLLMProvider DEFAULT N'deepseek',
        ActiveVideoEngine  NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_ActiveVideoEngine DEFAULT N'volcano',
        CreatedAt          DATETIME2 NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT GETDATE(),
        LastLoginAt        DATETIME2 NULL,
        CONSTRAINT PK_Users PRIMARY KEY (UserId),
        CONSTRAINT UQ_Users_Username UNIQUE (Username),
        CONSTRAINT UQ_Users_Email UNIQUE (Email)
    );

    CREATE TABLE dbo.VideoStyles (
        StyleId      INT IDENTITY(1,1) NOT NULL,
        StyleName    NVARCHAR(100) NOT NULL,
        StylePrompt  NVARCHAR(MAX) NOT NULL,
        IsDefault    BIT NOT NULL CONSTRAINT DF_VideoStyles_IsDefault DEFAULT 0,
        CreatedAt    DATETIME NOT NULL CONSTRAINT DF_VideoStyles_CreatedAt DEFAULT GETDATE(),
        UpdatedAt    DATETIME NOT NULL CONSTRAINT DF_VideoStyles_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_VideoStyles PRIMARY KEY (StyleId),
        CONSTRAINT UQ_VideoStyles_StyleName UNIQUE (StyleName)
    );

    CREATE TABLE dbo.Dramas (
        DramaId      INT IDENTITY(1,1) NOT NULL,
        UserId       INT NOT NULL,
        Title        NVARCHAR(200) NOT NULL,
        Description  NVARCHAR(MAX) NULL,
        CoverImage   NVARCHAR(500) NULL,
        CreatedAt    DATETIME2 NOT NULL CONSTRAINT DF_Dramas_CreatedAt DEFAULT GETDATE(),
        UpdatedAt    DATETIME2 NOT NULL CONSTRAINT DF_Dramas_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_Dramas PRIMARY KEY (DramaId),
        CONSTRAINT FK_Dramas_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId)
    );
    CREATE INDEX IX_Dramas_UserId ON dbo.Dramas(UserId);

    CREATE TABLE dbo.Projects (
        ProjectId        INT IDENTITY(1,1) NOT NULL,
        DramaId          INT NOT NULL,
        UserId           INT NOT NULL,
        Title            NVARCHAR(200) NOT NULL,
        Description      NVARCHAR(MAX) NULL,
        ScriptContent    NVARCHAR(MAX) NULL,
        CurrentStage     INT NOT NULL CONSTRAINT DF_Projects_CurrentStage DEFAULT 0,
        EpisodeCount     INT NOT NULL CONSTRAINT DF_Projects_EpisodeCount DEFAULT 12,
        CurrentBatch     INT NOT NULL CONSTRAINT DF_Projects_CurrentBatch DEFAULT 1,
        CoverImage       NVARCHAR(MAX) NULL,
        StyleId          INT NULL,
        Tags              NVARCHAR(1000) NULL,
        TargetDurationText NVARCHAR(64) NULL,
        LibraryCategory   NVARCHAR(50) NULL,
        VideoRatio       NVARCHAR(10) NOT NULL CONSTRAINT DF_Projects_VideoRatio DEFAULT N'16:9',
        VideoWatermark   BIT NOT NULL CONSTRAINT DF_Projects_VideoWatermark DEFAULT 0,
        VideoAudio       BIT NOT NULL CONSTRAINT DF_Projects_VideoAudio DEFAULT 1,
        VideoResolution  NVARCHAR(10) NOT NULL CONSTRAINT DF_Projects_VideoResolution DEFAULT N'720p',
        Status           NVARCHAR(20) NOT NULL CONSTRAINT DF_Projects_Status DEFAULT N'draft',
        CreatedAt        DATETIME2 NOT NULL CONSTRAINT DF_Projects_CreatedAt DEFAULT GETDATE(),
        UpdatedAt        DATETIME2 NOT NULL CONSTRAINT DF_Projects_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_Projects PRIMARY KEY (ProjectId),
        CONSTRAINT FK_Projects_Dramas FOREIGN KEY (DramaId) REFERENCES dbo.Dramas(DramaId),
        CONSTRAINT FK_Projects_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId),
        CONSTRAINT FK_Projects_VideoStyles FOREIGN KEY (StyleId) REFERENCES dbo.VideoStyles(StyleId),
        CONSTRAINT CK_Projects_EpisodeCount CHECK (EpisodeCount BETWEEN 1 AND 100),
        CONSTRAINT CK_Projects_CurrentBatch CHECK (CurrentBatch >= 1)
    );
    CREATE INDEX IX_Projects_UserId ON dbo.Projects(UserId);
    CREATE INDEX IX_Projects_DramaId ON dbo.Projects(DramaId);

    CREATE TABLE dbo.StageData (
        StageId       INT IDENTITY(1,1) NOT NULL,
        ProjectId     INT NOT NULL,
        StageNumber   INT NOT NULL,
        Content       NVARCHAR(MAX) NULL,
        LlmResponse   NVARCHAR(MAX) NULL,
        Status        NVARCHAR(20) NOT NULL CONSTRAINT DF_StageData_Status DEFAULT N'pending',
        CurrentBatch  INT NOT NULL CONSTRAINT DF_StageData_CurrentBatch DEFAULT 1,
        CreatedAt     DATETIME2 NOT NULL CONSTRAINT DF_StageData_CreatedAt DEFAULT GETDATE(),
        UpdatedAt     DATETIME2 NOT NULL CONSTRAINT DF_StageData_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_StageData PRIMARY KEY (StageId),
        CONSTRAINT FK_StageData_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT CK_StageData_StageNumber CHECK (StageNumber BETWEEN 1 AND 11),
        CONSTRAINT CK_StageData_CurrentBatch CHECK (CurrentBatch >= 1)
    );
    CREATE UNIQUE INDEX UX_StageData_Project_Stage ON dbo.StageData(ProjectId, StageNumber);

    CREATE TABLE dbo.DirectorPlans (
        DirectorPlanId      INT IDENTITY(1,1) NOT NULL,
        ProjectId           INT NOT NULL,
        EpisodeNumber       INT NOT NULL CONSTRAINT DF_DirectorPlans_EpisodeNumber DEFAULT 0,
        UnitNumber          NVARCHAR(50) NOT NULL,
        UnitType            NVARCHAR(50) NOT NULL CONSTRAINT DF_DirectorPlans_UnitType DEFAULT N'',
        DramaticPurpose     NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_DramaticPurpose DEFAULT N'',
        PrimarySubject      NVARCHAR(200) NOT NULL CONSTRAINT DF_DirectorPlans_PrimarySubject DEFAULT N'',
        SecondarySubject    NVARCHAR(200) NOT NULL CONSTRAINT DF_DirectorPlans_SecondarySubject DEFAULT N'',
        ConflictType        NVARCHAR(20) NOT NULL CONSTRAINT DF_DirectorPlans_ConflictType DEFAULT N'NonCombat',
        CorePayoff          NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_CorePayoff DEFAULT N'',
        EmotionCurve        NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_EmotionCurve DEFAULT N'',
        RhythmStrategy      NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_RhythmStrategy DEFAULT N'',
        ActionStrategy      NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_ActionStrategy DEFAULT N'',
        PerformanceStrategy NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_PerformanceStrategy DEFAULT N'',
        CameraStrategy      NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_CameraStrategy DEFAULT N'',
        VfxStrategy         NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_VfxStrategy DEFAULT N'',
        IntensityLevel      INT NOT NULL CONSTRAINT DF_DirectorPlans_IntensityLevel DEFAULT 3,
        CombatGrammarIds    NVARCHAR(500) NOT NULL CONSTRAINT DF_DirectorPlans_CombatGrammarIds DEFAULT N'',
        CombatRoundCount    INT NOT NULL CONSTRAINT DF_DirectorPlans_CombatRoundCount DEFAULT 0,
        VfxPeakPhase        NVARCHAR(20) NOT NULL CONSTRAINT DF_DirectorPlans_VfxPeakPhase DEFAULT N'',
        ActionPlan          NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_ActionPlan DEFAULT N'',
        FightArcType        NVARCHAR(50) NOT NULL CONSTRAINT DF_DirectorPlans_FightArcType DEFAULT N'',
        FightSequenceJson   NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_FightSequenceJson DEFAULT N'',
        NeedsReview         BIT NOT NULL CONSTRAINT DF_DirectorPlans_NeedsReview DEFAULT 0,
        ValidationScore     INT NOT NULL CONSTRAINT DF_DirectorPlans_ValidationScore DEFAULT 0,
        ViolationsJson      NVARCHAR(MAX) NOT NULL CONSTRAINT DF_DirectorPlans_ViolationsJson DEFAULT N'',
        RepairCount         INT NOT NULL CONSTRAINT DF_DirectorPlans_RepairCount DEFAULT 0,
        LastValidationAt    DATETIME NULL,
        CreatedAt           DATETIME2 NOT NULL CONSTRAINT DF_DirectorPlans_CreatedAt DEFAULT GETDATE(),
        UpdatedAt           DATETIME2 NOT NULL CONSTRAINT DF_DirectorPlans_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_DirectorPlans PRIMARY KEY (DirectorPlanId),
        CONSTRAINT FK_DirectorPlans_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT CK_DirectorPlans_IntensityLevel CHECK (IntensityLevel BETWEEN 1 AND 10)
    );
    CREATE UNIQUE INDEX UX_DirectorPlans_Project_Unit ON dbo.DirectorPlans(ProjectId, UnitNumber);
    CREATE INDEX IX_DirectorPlans_Project ON dbo.DirectorPlans(ProjectId);

    CREATE TABLE dbo.EpisodeDirectorPlans (
        EpisodeDirectorPlanId      INT IDENTITY(1,1) NOT NULL,
        ProjectId                  INT NOT NULL,
        EpisodeNumber              INT NOT NULL,
        EpisodeGoal                NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_EpisodeGoal DEFAULT N'',
        EmotionCurve               NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_EmotionCurve DEFAULT N'',
        IntensityCurveJson         NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_IntensityCurveJson DEFAULT N'[]',
        PayoffScheduleJson         NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_PayoffScheduleJson DEFAULT N'[]',
        ReservedVisualsJson        NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_ReservedVisualsJson DEFAULT N'[]',
        ForbiddenEarlyPayoffsJson  NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_ForbiddenEarlyPayoffsJson DEFAULT N'[]',
        RepetitionPolicyJson       NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_RepetitionPolicyJson DEFAULT N'{}',
        UnitEmotionCurveJson       NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitEmotionCurveJson DEFAULT N'[]',
        UnitTransitionsJson        NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitTransitionsJson DEFAULT N'[]',
        UnitEndStatesJson          NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UnitEndStatesJson DEFAULT N'[]',
        ClimaxBudgetJson           NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_ClimaxBudgetJson DEFAULT N'{}',
        RawJson                    NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_RawJson DEFAULT N'',
        CreatedAt                  DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_CreatedAt DEFAULT GETDATE(),
        UpdatedAt                  DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorPlans_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_EpisodeDirectorPlans PRIMARY KEY (EpisodeDirectorPlanId),
        CONSTRAINT FK_EpisodeDirectorPlans_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT CK_EpisodeDirectorPlans_Episode CHECK (EpisodeNumber >= 1)
    );
    CREATE UNIQUE INDEX UX_EpisodeDirectorPlans_Project_Episode ON dbo.EpisodeDirectorPlans(ProjectId, EpisodeNumber);

    CREATE TABLE dbo.EpisodeDirectorStates (
        EpisodeDirectorStateId     INT IDENTITY(1,1) NOT NULL,
        ProjectId                  INT NOT NULL,
        EpisodeNumber              INT NOT NULL,
        CameraPatternCountsJson    NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CameraPatternCountsJson DEFAULT N'{}',
        CombatPatternCountsJson    NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CombatPatternCountsJson DEFAULT N'{}',
        VfxPatternCountsJson       NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeDirectorStates_VfxPatternCountsJson DEFAULT N'{}',
        SlowMotionCount            INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_SlowMotionCount DEFAULT 0,
        MajorExplosionCount        INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_MajorExplosionCount DEFAULT 0,
        CurrentPeakIntensity       INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CurrentPeakIntensity DEFAULT 0,
        SmallClimaxCount           INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_SmallClimaxCount DEFAULT 0,
        MidClimaxCount             INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_MidClimaxCount DEFAULT 0,
        LargeClimaxCount           INT NOT NULL CONSTRAINT DF_EpisodeDirectorStates_LargeClimaxCount DEFAULT 0,
        CreatedAt                  DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorStates_CreatedAt DEFAULT GETDATE(),
        UpdatedAt                  DATETIME2 NOT NULL CONSTRAINT DF_EpisodeDirectorStates_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_EpisodeDirectorStates PRIMARY KEY (EpisodeDirectorStateId),
        CONSTRAINT FK_EpisodeDirectorStates_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT CK_EpisodeDirectorStates_Episode CHECK (EpisodeNumber >= 1)
    );
    CREATE UNIQUE INDEX UX_EpisodeDirectorStates_Project_Episode ON dbo.EpisodeDirectorStates(ProjectId, EpisodeNumber);

    CREATE TABLE dbo.EpisodeUnitStateSnapshots (
        EpisodeUnitStateSnapshotId INT IDENTITY(1,1) NOT NULL,
        ProjectId                  INT NOT NULL,
        EpisodeNumber              INT NOT NULL,
        UnitNumber                 NVARCHAR(50) NOT NULL,
        StateJson                  NVARCHAR(MAX) NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_StateJson DEFAULT N'{}',
        Source                     NVARCHAR(20) NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_Source DEFAULT N'storyboard',
        CreatedAt                  DATETIME2 NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_CreatedAt DEFAULT GETDATE(),
        UpdatedAt                  DATETIME2 NOT NULL CONSTRAINT DF_EpisodeUnitStateSnapshots_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_EpisodeUnitStateSnapshots PRIMARY KEY (EpisodeUnitStateSnapshotId),
        CONSTRAINT FK_EpisodeUnitStateSnapshots_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT CK_EpisodeUnitStateSnapshots_Episode CHECK (EpisodeNumber >= 1)
    );
    CREATE UNIQUE INDEX UX_EpisodeUnitStateSnapshots_Project_Episode_Unit ON dbo.EpisodeUnitStateSnapshots(ProjectId, EpisodeNumber, UnitNumber);

    CREATE TABLE dbo.Episodes (
        EpisodeId      INT IDENTITY(1,1) NOT NULL,
        ProjectId      INT NOT NULL,
        UserId         INT NOT NULL,
        EpisodeNumber  INT NOT NULL,
        Title          NVARCHAR(200) NOT NULL,
        Summary        NVARCHAR(MAX) NULL,
        Content        NVARCHAR(MAX) NULL,
        BatchNumber    INT NOT NULL CONSTRAINT DF_Episodes_BatchNumber DEFAULT 1,
        SortOrder      INT NOT NULL CONSTRAINT DF_Episodes_SortOrder DEFAULT 0,
        CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_Episodes_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_Episodes PRIMARY KEY (EpisodeId),
        CONSTRAINT FK_Episodes_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT FK_Episodes_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId),
        CONSTRAINT CK_Episodes_EpisodeNumber CHECK (EpisodeNumber >= 1),
        CONSTRAINT CK_Episodes_BatchNumber CHECK (BatchNumber >= 1)
    );
    CREATE INDEX IX_Episodes_Project ON dbo.Episodes(ProjectId);

    CREATE TABLE dbo.StoryboardFrames (
        FrameId      INT IDENTITY(1,1) NOT NULL,
        EpisodeId    INT NOT NULL,
        ProjectId    INT NOT NULL,
        FrameNumber  INT NOT NULL,
        Description  NVARCHAR(MAX) NULL,
        Composition  NVARCHAR(MAX) NULL,
        Characters   NVARCHAR(MAX) NULL,
        Dialogue     NVARCHAR(MAX) NULL,
        Camera       NVARCHAR(MAX) NULL,
        Duration     NVARCHAR(MAX) NULL,
        StartScene   NVARCHAR(MAX) NULL,
        EndScene     NVARCHAR(MAX) NULL,
        UnitNumber   NVARCHAR(50) NULL,
        EpisodeNumber  INT NULL,
        UnitType       NVARCHAR(100) NULL,
        ShotNumber     NVARCHAR(50) NULL,
        ShotSize       NVARCHAR(200) NULL,
        UnitOrder      INT NOT NULL CONSTRAINT DF_StoryboardFrames_UnitOrder DEFAULT 0,
        CombatBeatIndex INT NULL,
        CombatBeatIds   NVARCHAR(MAX) NULL,
        Timeline        NVARCHAR(MAX) NULL,
        Skills          NVARCHAR(MAX) NULL,
        Scene           NVARCHAR(MAX) NULL,
        BatchNumber  INT NOT NULL CONSTRAINT DF_StoryboardFrames_BatchNumber DEFAULT 1,
        SortOrder    INT NOT NULL CONSTRAINT DF_StoryboardFrames_SortOrder DEFAULT 0,
        CreatedAt    DATETIME2 NOT NULL CONSTRAINT DF_StoryboardFrames_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_StoryboardFrames PRIMARY KEY (FrameId),
        CONSTRAINT FK_StoryboardFrames_Episodes FOREIGN KEY (EpisodeId) REFERENCES dbo.Episodes(EpisodeId) ON DELETE CASCADE,
        CONSTRAINT FK_StoryboardFrames_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId),
        CONSTRAINT CK_StoryboardFrames_FrameNumber CHECK (FrameNumber >= 1),
        CONSTRAINT CK_StoryboardFrames_BatchNumber CHECK (BatchNumber >= 1)
    );
    CREATE INDEX IX_Frames_Episode ON dbo.StoryboardFrames(EpisodeId);
    CREATE INDEX IX_Frames_Project ON dbo.StoryboardFrames(ProjectId);
    CREATE INDEX IX_Frames_Unit ON dbo.StoryboardFrames(ProjectId, EpisodeId, UnitNumber);

    CREATE TABLE dbo.CharacterAssets (
        AssetId      INT IDENTITY(1,1) NOT NULL,
        ProjectId    INT NOT NULL,
        Name         NVARCHAR(100) NOT NULL,
        Description  NVARCHAR(MAX) NULL,
        ImageUrl     NVARCHAR(500) NULL,
        Attributes     NVARCHAR(MAX) NULL,
        ImagePrompt    NVARCHAR(MAX) NULL,
        NegativePrompt NVARCHAR(MAX) NULL,
        CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_CharacterAssets_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_CharacterAssets PRIMARY KEY (AssetId),
        CONSTRAINT FK_CharacterAssets_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE
    );
    CREATE INDEX IX_CharAssets_Project ON dbo.CharacterAssets(ProjectId);

    CREATE TABLE dbo.PropAssets (
        AssetId      INT IDENTITY(1,1) NOT NULL,
        ProjectId    INT NOT NULL,
        Name         NVARCHAR(100) NOT NULL,
        Description  NVARCHAR(MAX) NULL,
        ImageUrl       NVARCHAR(500) NULL,
        ImagePrompt    NVARCHAR(MAX) NULL,
        NegativePrompt NVARCHAR(MAX) NULL,
        CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_PropAssets_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_PropAssets PRIMARY KEY (AssetId),
        CONSTRAINT FK_PropAssets_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE
    );
    CREATE INDEX IX_PropAssets_Project ON dbo.PropAssets(ProjectId);

    CREATE TABLE dbo.EnvironmentAssets (
        AssetId      INT IDENTITY(1,1) NOT NULL,
        ProjectId    INT NOT NULL,
        Name         NVARCHAR(100) NOT NULL,
        Description  NVARCHAR(MAX) NULL,
        ImageUrl       NVARCHAR(500) NULL,
        ImagePrompt    NVARCHAR(MAX) NULL,
        NegativePrompt NVARCHAR(MAX) NULL,
        CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_EnvironmentAssets_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_EnvironmentAssets PRIMARY KEY (AssetId),
        CONSTRAINT FK_EnvironmentAssets_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE
    );
    CREATE INDEX IX_EnvAssets_Project ON dbo.EnvironmentAssets(ProjectId);

    CREATE TABLE dbo.EffectAssets (
        AssetId      INT IDENTITY(1,1) NOT NULL,
        ProjectId    INT NOT NULL,
        Name         NVARCHAR(100) NOT NULL,
        Description  NVARCHAR(MAX) NULL,
        ImageUrl       NVARCHAR(500) NULL,
        ImagePrompt    NVARCHAR(MAX) NULL,
        NegativePrompt NVARCHAR(MAX) NULL,
        CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_EffectAssets_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_EffectAssets PRIMARY KEY (AssetId),
        CONSTRAINT FK_EffectAssets_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE
    );
    CREATE INDEX IX_EffectAssets_Project ON dbo.EffectAssets(ProjectId);

    CREATE TABLE dbo.SeedancePrompts (
        PromptId         INT IDENTITY(1,1) NOT NULL,
        ProjectId        INT NOT NULL,
        FrameId          INT NULL,
        PromptText       NVARCHAR(MAX) NOT NULL,
        NegativePrompt   NVARCHAR(MAX) NULL,
        VideoUrl         NVARCHAR(500) NULL,
        LocalVideoUrl    NVARCHAR(500) NULL,
        Status           NVARCHAR(20) NOT NULL CONSTRAINT DF_SeedancePrompts_Status DEFAULT N'pending',
        BatchNumber      INT NOT NULL CONSTRAINT DF_SeedancePrompts_BatchNumber DEFAULT 1,
        EpisodeNumber    INT NOT NULL CONSTRAINT DF_SeedancePrompts_EpisodeNumber DEFAULT 0,
        UnitName         NVARCHAR(200) NULL,
        ShotLabel        NVARCHAR(100) NULL,
        ShotType         NVARCHAR(50) NULL,
        Duration         INT NOT NULL CONSTRAINT DF_SeedancePrompts_Duration DEFAULT 11,
        ReferenceImages  NVARCHAR(MAX) NULL,
        ReferenceVideos  NVARCHAR(MAX) NULL,
        ReferenceAudio   NVARCHAR(MAX) NULL,
        ShotNumber       INT NOT NULL CONSTRAINT DF_SeedancePrompts_ShotNumber DEFAULT 0,
        PromptTextH3     NVARCHAR(MAX) NULL,
        CreatedAt        DATETIME2 NOT NULL CONSTRAINT DF_SeedancePrompts_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_SeedancePrompts PRIMARY KEY (PromptId),
        CONSTRAINT FK_SeedancePrompts_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE,
        CONSTRAINT CK_SeedancePrompts_BatchNumber CHECK (BatchNumber >= 1),
        CONSTRAINT CK_SeedancePrompts_Duration CHECK (Duration > 0)
    );
    CREATE INDEX IX_Prompts_Project ON dbo.SeedancePrompts(ProjectId);

    CREATE TABLE dbo.CoherenceChecks (
        CheckId    INT IDENTITY(1,1) NOT NULL,
        ProjectId  INT NOT NULL,
        Issues     NVARCHAR(MAX) NULL,
        Status     NVARCHAR(20) NOT NULL CONSTRAINT DF_CoherenceChecks_Status DEFAULT N'pending',
        CreatedAt  DATETIME2 NOT NULL CONSTRAINT DF_CoherenceChecks_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_CoherenceChecks PRIMARY KEY (CheckId),
        CONSTRAINT FK_CoherenceChecks_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_CoherenceChecks_Project ON dbo.CoherenceChecks(ProjectId);

    CREATE TABLE dbo.LLMConfigs (
        ConfigId     INT IDENTITY(1,1) NOT NULL,
        UserId       INT NOT NULL,
        Provider     NVARCHAR(50) NOT NULL,
        ApiKey       NVARCHAR(500) NOT NULL CONSTRAINT DF_LLMConfigs_ApiKey DEFAULT N'',
        ApiUrl       NVARCHAR(500) NULL,
        ModelName    NVARCHAR(100) NULL,
        ThinkingMode NVARCHAR(20) NULL,
        IsActive     BIT NOT NULL CONSTRAINT DF_LLMConfigs_IsActive DEFAULT 1,
        AutoEnhance  BIT NOT NULL CONSTRAINT DF_LLMConfigs_AutoEnhance DEFAULT 0,
        CreatedAt    DATETIME2 NOT NULL CONSTRAINT DF_LLMConfigs_CreatedAt DEFAULT GETDATE(),
        UpdatedAt    DATETIME2 NULL,
        CONSTRAINT PK_LLMConfigs PRIMARY KEY (ConfigId),
        CONSTRAINT FK_LLMConfigs_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId)
    );
    CREATE UNIQUE INDEX UX_LLMConfigs_User_Provider ON dbo.LLMConfigs(UserId, Provider);

    CREATE TABLE dbo.VideoGenerationTasks (
        Id                    INT IDENTITY(1,1) NOT NULL,
        ProjectId             INT NOT NULL,
        PromptId              INT NOT NULL,
        TaskId                NVARCHAR(200) NOT NULL,
        Engine                NVARCHAR(20) NOT NULL CONSTRAINT DF_VideoTasks_Engine DEFAULT N'volcano',
        Status                NVARCHAR(50) NOT NULL CONSTRAINT DF_VideoTasks_Status DEFAULT N'pending',
        VideoUrl              NVARCHAR(MAX) NULL,
        LocalVideoUrl         NVARCHAR(MAX) NULL,
        RequestDuration       INT NOT NULL CONSTRAINT DF_VideoTasks_RequestDuration DEFAULT 11,
        RequestRatio          NVARCHAR(20) NOT NULL CONSTRAINT DF_VideoTasks_RequestRatio DEFAULT N'16:9',
        RequestWatermark      BIT NOT NULL CONSTRAINT DF_VideoTasks_RequestWatermark DEFAULT 0,
        RequestGenerateAudio  BIT NOT NULL CONSTRAINT DF_VideoTasks_RequestAudio DEFAULT 1,
        ResponseResolution    NVARCHAR(50) NULL,
        ResponseDuration      FLOAT NULL,
        EnhanceDuration       INT NULL,
        EnhanceResolution     NVARCHAR(50) NULL,
        ResponseUsageTokens   INT NULL,
        ResponseSeed          INT NULL,
        ApiStatus             NVARCHAR(50) NULL,
        ErrorMessage          NVARCHAR(MAX) NULL,
        CreatedAt             DATETIME2 NOT NULL CONSTRAINT DF_VideoTasks_CreatedAt DEFAULT GETDATE(),
        CompletedAt           DATETIME2 NULL,
        CONSTRAINT PK_VideoGenerationTasks PRIMARY KEY (Id),
        CONSTRAINT FK_VideoTasks_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId),
        CONSTRAINT FK_VideoTasks_Prompts FOREIGN KEY (PromptId) REFERENCES dbo.SeedancePrompts(PromptId) ON DELETE CASCADE,
        CONSTRAINT CK_VideoTasks_RequestDuration CHECK (RequestDuration > 0)
    );
    CREATE INDEX IX_VideoTasks_Project ON dbo.VideoGenerationTasks(ProjectId);
    CREATE INDEX IX_VideoTasks_Prompt_CreatedAt ON dbo.VideoGenerationTasks(PromptId, CreatedAt DESC);
    CREATE INDEX IX_VideoTasks_TaskId ON dbo.VideoGenerationTasks(TaskId);

    CREATE TABLE dbo.ReferenceAssets (
        AssetId      INT IDENTITY(1,1) NOT NULL,
        UserId       INT NOT NULL,
        FileName     NVARCHAR(255) NOT NULL,
        LocalPath    NVARCHAR(500) NOT NULL,
        FileType     NVARCHAR(50) NULL,
        Category     NVARCHAR(100) NULL,
        SubCategory  NVARCHAR(100) NULL,
        Tags         NVARCHAR(500) NULL,
        FileSize     BIGINT NULL,
        SourceKey    NVARCHAR(200) NULL,
        CreatedAt    DATETIME2 NOT NULL CONSTRAINT DF_ReferenceAssets_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_ReferenceAssets PRIMARY KEY (AssetId),
        CONSTRAINT FK_ReferenceAssets_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId)
    );
    CREATE INDEX IX_RefAssets_User ON dbo.ReferenceAssets(UserId);
    CREATE INDEX IX_RefAssets_User_SourceKey ON dbo.ReferenceAssets(UserId, SourceKey);

    CREATE TABLE dbo.SkillLibrary (
        SkillId      INT IDENTITY(1,1) NOT NULL,
        UserId       INT NOT NULL,
        ProjectId    INT NULL,
        Name         NVARCHAR(100) NOT NULL,
        Element      NVARCHAR(20) NULL,
        Tier         INT NULL CONSTRAINT DF_SkillLibrary_Tier DEFAULT 4,
        PromptImage  NVARCHAR(MAX) NULL,
        PromptVideo  NVARCHAR(MAX) NULL,
        ImageUrl       NVARCHAR(500) NULL,
        Tags           NVARCHAR(500) NULL,
        OwnerCharacter NVARCHAR(100) NULL,
        CreatedAt      DATETIME2 NOT NULL CONSTRAINT DF_SkillLibrary_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_SkillLibrary PRIMARY KEY (SkillId),
        CONSTRAINT FK_SkillLibrary_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId) ON DELETE CASCADE,
        CONSTRAINT FK_SkillLibrary_Projects FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ProjectId),
        CONSTRAINT CK_SkillLibrary_Tier CHECK (Tier IS NULL OR Tier BETWEEN 1 AND 5)
    );
    CREATE INDEX IX_SkillLibrary_User ON dbo.SkillLibrary(UserId);
    CREATE INDEX IX_SkillLibrary_Project ON dbo.SkillLibrary(ProjectId);

    CREATE TABLE dbo.SkillElements (
        ElementId   INT IDENTITY(1,1) NOT NULL,
        UserId      INT NOT NULL,
        Name        NVARCHAR(50) NOT NULL,
        SortOrder   INT NOT NULL CONSTRAINT DF_SkillElements_SortOrder DEFAULT 0,
        CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_SkillElements_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_SkillElements PRIMARY KEY (ElementId),
        CONSTRAINT FK_SkillElements_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_SkillElements_User_Name ON dbo.SkillElements(UserId, Name);

    CREATE TABLE dbo.FightTemplate (
        FightTemplateId   INT IDENTITY(1,1) NOT NULL,
        UserId            INT NOT NULL,
        Name              NVARCHAR(100) NOT NULL,
        Tier              INT NULL CONSTRAINT DF_FightTemplate_Tier DEFAULT 3,
        Duration          INT NULL CONSTRAINT DF_FightTemplate_Duration DEFAULT 11,
        Scene             NVARCHAR(500) NULL,
        Beat              NVARCHAR(MAX) NULL,
        ActionPrompt      NVARCHAR(MAX) NULL,
        CameraPrompt      NVARCHAR(MAX) NULL,
        ConstraintPrompt  NVARCHAR(MAX) NULL,
        Tags              NVARCHAR(500) NULL,
        CreatedAt         DATETIME2 NOT NULL CONSTRAINT DF_FightTemplate_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_FightTemplate PRIMARY KEY (FightTemplateId),
        CONSTRAINT FK_FightTemplate_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId) ON DELETE CASCADE,
        CONSTRAINT CK_FightTemplate_Tier CHECK (Tier IS NULL OR Tier BETWEEN 1 AND 5),
        CONSTRAINT CK_FightTemplate_Duration CHECK (Duration IS NULL OR Duration > 0)
    );
    CREATE INDEX IX_FightTemplate_User ON dbo.FightTemplate(UserId);

    CREATE TABLE dbo.FightArcTemplates (
        FightArcTemplateId   INT IDENTITY(1,1) NOT NULL,
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
        CONSTRAINT PK_FightArcTemplates PRIMARY KEY (FightArcTemplateId),
        CONSTRAINT UQ_FightArcTemplates_ArcType UNIQUE (ArcTypeId)
    );

    CREATE TABLE dbo.ShotDirective (
        ShotDirectiveId  INT IDENTITY(1,1) NOT NULL,
        UserId           INT NOT NULL,
        Name             NVARCHAR(100) NOT NULL,
        Category         NVARCHAR(20) NOT NULL,
        Description      NVARCHAR(MAX) NULL,
        Tags             NVARCHAR(500) NULL,
        CreatedAt        DATETIME2 NOT NULL CONSTRAINT DF_ShotDirective_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_ShotDirective PRIMARY KEY (ShotDirectiveId),
        CONSTRAINT FK_ShotDirective_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId) ON DELETE CASCADE
    );
    CREATE INDEX IX_ShotDirective_User ON dbo.ShotDirective(UserId);

    CREATE TABLE dbo.TokenUsageConfig (
        Id           INT NOT NULL,
        QuotaTokens  BIGINT NOT NULL CONSTRAINT DF_TokenUsageConfig_Quota DEFAULT 14000000,
        UpdatedAt    DATETIME2 NOT NULL CONSTRAINT DF_TokenUsageConfig_UpdatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_TokenUsageConfig PRIMARY KEY (Id),
        CONSTRAINT CK_TokenUsageConfig_Quota CHECK (QuotaTokens > 0)
    );

    CREATE TABLE dbo.Works (
        WorkId      INT IDENTITY(1,1) NOT NULL,
        UserId      INT NOT NULL,
        Title       NVARCHAR(200) NOT NULL,
        Description NVARCHAR(MAX) NULL,
        CoverImage  NVARCHAR(500) NULL,
        WorkUrl     NVARCHAR(500) NULL,
        CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_Works_CreatedAt DEFAULT GETDATE(),
        UpdatedAt   DATETIME2 NULL,
        CONSTRAINT PK_Works PRIMARY KEY (WorkId),
        CONSTRAINT FK_Works_Users FOREIGN KEY (UserId) REFERENCES dbo.Users(UserId)
    );
    CREATE INDEX IX_Works_User ON dbo.Works(UserId);

    -- ===== 以下 9 张表最初由 Database\Upgrade_*.sql 增量创建，
    --       其中 7 张于 2026-09-12、ProjectContinuityTables 与 ProjectKeyframes 于 2026-09-16
    --       按正式库实际结构同步进基线（结构以 Export_Schema.sql 导出结果为准）。=====

    CREATE TABLE dbo.StageProgressLogs (
        ProgressLogId  INT IDENTITY(1,1) NOT NULL,
        ProjectId      INT NOT NULL,
        StageNumber    INT NOT NULL,
        LogText        NVARCHAR(1000) NOT NULL,
        CreatedAt      DATETIME NOT NULL CONSTRAINT DF_StageProgressLogs_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_StageProgressLogs PRIMARY KEY (ProgressLogId)
    );

    -- CameraAtom 的出厂原子数据（UserId=1，约 65 条）由 Upgrade_CameraAtom.sql 负责种入；
    -- 基线只建结构，与 FightArcTemplates 的处理方式一致。
    CREATE TABLE dbo.CameraAtom (
        AtomId       INT IDENTITY(1,1) NOT NULL,
        UserId       INT NOT NULL,
        Category     NVARCHAR(20) NOT NULL,
        Name         NVARCHAR(50) NOT NULL,
        Description  NVARCHAR(600) NOT NULL,
        Tags         NVARCHAR(200) NULL,
        CreatedAt    DATETIME2 NULL CONSTRAINT DF_CameraAtom_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_CameraAtom PRIMARY KEY (AtomId)
    );
    CREATE INDEX IX_CameraAtom_User ON dbo.CameraAtom(UserId);

    CREATE TABLE dbo.FrameAssetBindings (
        BindingId   INT IDENTITY(1,1) NOT NULL,
        ProjectId   INT NOT NULL,
        FrameId     INT NOT NULL,
        Category    NVARCHAR(20) NOT NULL,
        AssetId     INT NOT NULL,
        Name        NVARCHAR(100) NOT NULL,
        HasImage    BIT NOT NULL CONSTRAINT DF_FrameAssetBindings_HasImage DEFAULT 0,
        SortOrder   INT NOT NULL CONSTRAINT DF_FrameAssetBindings_SortOrder DEFAULT 0,
        CreatedAt   DATETIME2 NOT NULL CONSTRAINT DF_FrameAssetBindings_CreatedAt DEFAULT GETDATE(),
        CONSTRAINT PK_FrameAssetBindings PRIMARY KEY (BindingId),
        CONSTRAINT FK_FrameAssetBindings_Frames FOREIGN KEY (FrameId) REFERENCES dbo.StoryboardFrames(FrameId) ON DELETE CASCADE
    );
    CREATE INDEX IX_FAB_Project_Frame ON dbo.FrameAssetBindings(ProjectId, FrameId);
    CREATE INDEX IX_FAB_Frame ON dbo.FrameAssetBindings(FrameId);

    CREATE TABLE dbo.UnitAssetBindings (
        BindingId      INT IDENTITY(1,1) NOT NULL,
        ProjectId      INT NOT NULL,
        EpisodeNumber  INT NOT NULL,
        UnitNumber     NVARCHAR(64) NOT NULL,
        Category       NVARCHAR(16) NOT NULL,
        AssetId        INT NOT NULL,
        Name           NVARCHAR(200) NOT NULL,
        HasImage       BIT NOT NULL CONSTRAINT DF_UnitAssetBindings_HasImage DEFAULT 0,
        SortOrder      INT NOT NULL CONSTRAINT DF_UnitAssetBindings_SortOrder DEFAULT 0,
        CONSTRAINT PK_UnitAssetBindings PRIMARY KEY (BindingId)
    );
    CREATE INDEX IX_UnitAssetBindings_Project ON dbo.UnitAssetBindings(ProjectId);
    CREATE INDEX IX_UnitAssetBindings_Unit ON dbo.UnitAssetBindings(ProjectId, EpisodeNumber, UnitNumber);

    CREATE TABLE dbo.VoiceReferences (
        VoiceId           INT IDENTITY(1,1) NOT NULL,
        ProjectId         INT NOT NULL,
        CharacterName     NVARCHAR(200) NOT NULL,
        AudioUrl          NVARCHAR(1000) NOT NULL,
        OriginalFileName  NVARCHAR(400) NULL,
        CreatedAt         DATETIME2(0) NOT NULL CONSTRAINT DF_VoiceReferences_CreatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT PK_VoiceReferences PRIMARY KEY (VoiceId)
    );
    CREATE UNIQUE INDEX UX_VoiceReferences_Project_Character ON dbo.VoiceReferences(ProjectId, CharacterName);
    CREATE INDEX IX_VoiceReferences_Project ON dbo.VoiceReferences(ProjectId);

    -- ProjectId = 0 表示账号级默认模版，>0 表示该剧专属覆盖。
    CREATE TABLE dbo.AssetPromptTemplates (
        TemplateId       INT IDENTITY(1,1) NOT NULL,
        UserId           INT NOT NULL,
        ProjectId        INT NOT NULL CONSTRAINT DF_AssetPromptTemplates_ProjectId DEFAULT 0,
        Category         NVARCHAR(20) NOT NULL,
        StyleLock        NVARCHAR(MAX) NULL,
        NegativePrompt   NVARCHAR(MAX) NULL,
        RuleText         NVARCHAR(MAX) NULL,
        Enabled          BIT NOT NULL CONSTRAINT DF_AssetPromptTemplates_Enabled DEFAULT 1,
        UpdatedAt        DATETIME2 NOT NULL CONSTRAINT DF_AssetPromptTemplates_UpdatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT PK_AssetPromptTemplates PRIMARY KEY (TemplateId)
    );
    CREATE UNIQUE INDEX UX_AssetPromptTemplates_User_Project_Category
        ON dbo.AssetPromptTemplates(UserId, ProjectId, Category);

    CREATE TABLE dbo.AssetImageTasks (
        TaskId            INT IDENTITY(1,1) NOT NULL,
        ProjectId         INT NOT NULL,
        UserId            INT NOT NULL,
        Category          NVARCHAR(32) NOT NULL,
        AssetId           INT NOT NULL,
        AssetName         NVARCHAR(200) NULL,
        Status            NVARCHAR(16) NOT NULL CONSTRAINT DF_AssetImageTasks_Status DEFAULT N'queued',
        PromptOverride    NVARCHAR(MAX) NULL,
        NegativeOverride  NVARCHAR(MAX) NULL,
        ExtraPrompt       NVARCHAR(MAX) NULL,
        Size              NVARCHAR(16) NULL,
        ImageUrl          NVARCHAR(400) NULL,
        LibraryAssetId    INT NULL,
        UsedPrompt        NVARCHAR(MAX) NULL,
        ErrorMessage      NVARCHAR(MAX) NULL,
        CreatedAt         DATETIME NOT NULL CONSTRAINT DF_AssetImageTasks_CreatedAt DEFAULT GETDATE(),
        StartedAt         DATETIME NULL,
        FinishedAt        DATETIME NULL,
        CONSTRAINT PK_AssetImageTasks PRIMARY KEY (TaskId)
    );
    CREATE INDEX IX_AssetImageTasks_Status ON dbo.AssetImageTasks(Status, TaskId);
    CREATE INDEX IX_AssetImageTasks_Project ON dbo.AssetImageTasks(ProjectId, TaskId DESC);

    -- L2 连续性层六类表（权威来源 Database\Upgrade_连续性表.sql）：
    -- 阶段 3 之后抽取落库、阶段 4 作为硬约束注入；
    -- TableType 取值 SceneSpace / CharacterContinuity / PropState / ClueReveal / ActionCausality / TransitionMotive，
    -- EpisodeNumber = 0 表示全片（整季）级，> 0 表示该集级。
    CREATE TABLE dbo.ProjectContinuityTables (
        ContinuityId   INT IDENTITY(1,1) NOT NULL,
        ProjectId      INT NOT NULL,
        EpisodeNumber  INT NOT NULL CONSTRAINT DF_ProjectContinuityTables_EpisodeNumber DEFAULT 0,
        TableType      NVARCHAR(40) NOT NULL,
        ContentJson    NVARCHAR(MAX) NOT NULL CONSTRAINT DF_ProjectContinuityTables_ContentJson DEFAULT N'{}',
        ContentText    NVARCHAR(MAX) NULL,
        Source         NVARCHAR(20) NOT NULL CONSTRAINT DF_ProjectContinuityTables_Source DEFAULT N'llm',
        CreatedAt      DATETIME2(0) NOT NULL CONSTRAINT DF_ProjectContinuityTables_CreatedAt DEFAULT SYSDATETIME(),
        UpdatedAt      DATETIME2(0) NOT NULL CONSTRAINT DF_ProjectContinuityTables_UpdatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT PK_ProjectContinuityTables PRIMARY KEY (ContinuityId)
    );
    CREATE INDEX IX_ProjectContinuityTables_Project ON dbo.ProjectContinuityTables(ProjectId);
    CREATE UNIQUE INDEX UX_ProjectContinuityTables_Project_Type
        ON dbo.ProjectContinuityTables(ProjectId, EpisodeNumber, TableType);

    -- L3 关键帧层（权威来源 Database\Upgrade_关键帧层.sql）：
    -- 每剧情节点一张关键帧（8-16 张/集），锁定角色站位/道具状态/场景朝向/线索可见性/镜头衔接；
    -- 人工触发生成，阶段 9 按集注入【关键帧锚定】。EpisodeNumber = 0 表示全片级。
    CREATE TABLE dbo.ProjectKeyframes (
        KeyframeId            INT IDENTITY(1,1) NOT NULL,
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
        UpdatedAt             DATETIME2(0) NOT NULL CONSTRAINT DF_ProjectKeyframes_UpdatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT PK_ProjectKeyframes PRIMARY KEY (KeyframeId)
    );
    CREATE INDEX IX_ProjectKeyframes_Project ON dbo.ProjectKeyframes(ProjectId, EpisodeNumber, SortOrder);

    INSERT INTO dbo.TokenUsageConfig(Id, QuotaTokens) VALUES(1, 14000000);

    INSERT INTO dbo.VideoStyles(StyleName, StylePrompt, IsDefault) VALUES
        (N'米哈游（二次元）', N'米哈游二次元风格，画面色彩高饱和，柔光遍布，角色面部细节及表情刻画细腻', 0),
        (N'写实风格', N'写实主义风格，低饱和冷色调，细节克制，强调人物微表情与真实光影', 0),
        (N'日系动漫', N'日系动漫风格，线条干净流畅，色彩明快清新，角色大眼睛萌系表情', 0),
        (N'赛博朋克', N'赛博朋克风格，霓虹光效，高对比冷暖撞色，未来都市科技感', 0),
        (N'水墨古风', N'水墨古风风格，留白构图，淡雅墨色晕染，东方意境', 0),
        (N'星穹铁道PV风', N'崩坏星穹铁道PV风格：电影级日系动画电影质感，厚涂插画与精致渲染结合，画面完成度极高；角色肤色通透，发丝、衣物、瞳孔高光刻画细腻；背景大气透视+景深虚化，场景有厚重材质感；电影级布光，强调侧逆光、轮廓光、体积光，明暗对比强烈但整体色调统一；高饱和、低对比的统一色板，带轻微胶片颗粒与辉光；镜头语言富有张力，广角大透视、快速推拉、镜头光晕，强调氛围与情绪。', 1);

    COMMIT TRANSACTION;
    PRINT N'Baseline.sql 执行成功：已创建 36 张业务表及基础种子数据。';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
