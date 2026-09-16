/*
    ManhuaPipeline database verification (read-only)
    Target: SQL Server 2016+ / compatibility level 130+

    This script only reads metadata and aggregate counts. It does not create,
    alter, insert, update, or delete persistent database objects or data.
*/

SET NOCOUNT ON;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
BEGIN
    THROW 51000, N'请选择 ManhuaPipeline 业务数据库后再执行 Verify.sql。', 1;
END;

PRINT N'=== 0. 执行环境 ===';
SELECT
    DB_NAME() AS DatabaseName,
    CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)) AS ProductVersion,
    CAST(SERVERPROPERTY('Edition') AS NVARCHAR(100)) AS Edition,
    d.compatibility_level AS CompatibilityLevel,
    GETDATE() AS CheckedAt
FROM sys.databases d
WHERE d.name = DB_NAME();

PRINT N'=== 1. 必需表检查：Status 必须全部为 OK ===';
SELECT
    N'P0' AS Severity,
    v.TableName AS ObjectName,
    CASE WHEN OBJECT_ID(N'dbo.' + v.TableName, N'U') IS NULL THEN N'MISSING' ELSE N'OK' END AS Status
FROM (VALUES
    (N'Users'), (N'Dramas'), (N'Projects'), (N'StageData'), (N'Episodes'),
    (N'StoryboardFrames'), (N'CharacterAssets'), (N'PropAssets'),
    (N'EnvironmentAssets'), (N'EffectAssets'), (N'SeedancePrompts'),
    (N'CoherenceChecks'), (N'LLMConfigs'), (N'VideoGenerationTasks'),
    (N'DirectorPlans'),
    (N'EpisodeDirectorPlans'), (N'EpisodeDirectorStates'),
    (N'EpisodeUnitStateSnapshots'),
    (N'VideoStyles'), (N'ReferenceAssets'), (N'SkillLibrary'),
    (N'SkillElements'),
    (N'FightTemplate'), (N'FightArcTemplates'), (N'ShotDirective'), (N'TokenUsageConfig'), (N'Works'),
    (N'StageProgressLogs'), (N'CameraAtom'), (N'FrameAssetBindings'), (N'UnitAssetBindings'),
    (N'VoiceReferences'), (N'AssetPromptTemplates'), (N'AssetImageTasks'),
    (N'ProjectContinuityTables'), (N'ProjectKeyframes')
) v(TableName)
ORDER BY v.TableName;

PRINT N'=== 2. 代码必需字段检查：结果集应为空 ===';
SELECT
    v.Severity,
    v.TableName,
    v.ColumnName,
    v.ExpectedDefinition,
    N'MISSING' AS Status
FROM (VALUES
    (N'P0',N'Users',N'UserId',N'INT IDENTITY'),
    (N'P0',N'Users',N'Username',N'NVARCHAR(50)'),
    (N'P0',N'Users',N'Email',N'NVARCHAR(100)'),
    (N'P0',N'Users',N'PasswordHash',N'NVARCHAR(256)'),
    (N'P1',N'Users',N'Nickname',N'NVARCHAR'),
    (N'P1',N'Users',N'Avatar',N'NVARCHAR(500)'),
    (N'P0',N'Users',N'Role',N'NVARCHAR(20)'),
    (N'P0',N'Users',N'IsActive',N'BIT'),
    (N'P0',N'Users',N'CreatedAt',N'DATETIME2'),
    (N'P1',N'Users',N'LastLoginAt',N'DATETIME2 NULL'),
    (N'P1',N'Users',N'ActiveLLMProvider',N'NVARCHAR(20)'),
    (N'P1',N'Users',N'ActiveVideoEngine',N'NVARCHAR(20)'),

    (N'P0',N'Dramas',N'DramaId',N'INT IDENTITY'),
    (N'P0',N'Dramas',N'UserId',N'INT'),
    (N'P0',N'Dramas',N'Title',N'NVARCHAR(200)'),
    (N'P1',N'Dramas',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'Dramas',N'CoverImage',N'NVARCHAR(500) NULL'),
    (N'P0',N'Dramas',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'Dramas',N'UpdatedAt',N'DATETIME2'),

    (N'P0',N'Projects',N'ProjectId',N'INT IDENTITY'),
    (N'P0',N'Projects',N'DramaId',N'INT'),
    (N'P0',N'Projects',N'UserId',N'INT'),
    (N'P0',N'Projects',N'Title',N'NVARCHAR(200)'),
    (N'P1',N'Projects',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'Projects',N'ScriptContent',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'Projects',N'CurrentStage',N'INT'),
    (N'P0',N'Projects',N'EpisodeCount',N'INT'),
    (N'P1',N'Projects',N'CurrentBatch',N'INT'),
    (N'P1',N'Projects',N'CoverImage',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'Projects',N'StyleId',N'INT NULL'),
    (N'P1',N'Projects',N'Tags',N'NVARCHAR(1000) NULL'),
    (N'P1',N'Projects',N'VideoRatio',N'NVARCHAR(10)'),
    (N'P1',N'Projects',N'VideoWatermark',N'BIT'),
    (N'P1',N'Projects',N'VideoAudio',N'BIT'),
    (N'P1',N'Projects',N'VideoResolution',N'NVARCHAR(10)'),
    (N'P0',N'Projects',N'Status',N'NVARCHAR(20)'),
    (N'P0',N'Projects',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'Projects',N'UpdatedAt',N'DATETIME2'),
    (N'P1',N'Projects',N'TargetDurationText',N'NVARCHAR(64) NULL'),
    (N'P1',N'Projects',N'LibraryCategory',N'NVARCHAR(50) NULL'),

    (N'P0',N'StageData',N'StageId',N'INT IDENTITY'),
    (N'P0',N'StageData',N'ProjectId',N'INT'),
    (N'P0',N'StageData',N'StageNumber',N'INT'),
    (N'P1',N'StageData',N'Content',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StageData',N'LlmResponse',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'StageData',N'Status',N'NVARCHAR(20)'),
    (N'P1',N'StageData',N'CurrentBatch',N'INT'),
    (N'P0',N'StageData',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'StageData',N'UpdatedAt',N'DATETIME2'),

    (N'P0',N'Episodes',N'EpisodeId',N'INT IDENTITY'),
    (N'P0',N'Episodes',N'ProjectId',N'INT'),
    (N'P0',N'Episodes',N'UserId',N'INT'),
    (N'P0',N'Episodes',N'EpisodeNumber',N'INT'),
    (N'P0',N'Episodes',N'Title',N'NVARCHAR(200)'),
    (N'P1',N'Episodes',N'Summary',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'Episodes',N'Content',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'Episodes',N'BatchNumber',N'INT'),
    (N'P0',N'Episodes',N'SortOrder',N'INT'),
    (N'P0',N'Episodes',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'StoryboardFrames',N'FrameId',N'INT IDENTITY'),
    (N'P0',N'StoryboardFrames',N'EpisodeId',N'INT'),
    (N'P0',N'StoryboardFrames',N'ProjectId',N'INT'),
    (N'P0',N'StoryboardFrames',N'FrameNumber',N'INT'),
    (N'P1',N'StoryboardFrames',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Composition',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Characters',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Dialogue',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Camera',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Duration',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'StartScene',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'EndScene',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'UnitNumber',N'NVARCHAR(50) NULL'),
    (N'P1',N'StoryboardFrames',N'EpisodeNumber',N'INT NULL'),
    (N'P1',N'StoryboardFrames',N'UnitType',N'NVARCHAR(100) NULL'),
    (N'P1',N'StoryboardFrames',N'ShotNumber',N'NVARCHAR(50) NULL'),
    (N'P1',N'StoryboardFrames',N'ShotSize',N'NVARCHAR(200) NULL'),
    (N'P1',N'StoryboardFrames',N'UnitOrder',N'INT'),
    (N'P1',N'StoryboardFrames',N'CombatBeatIndex',N'INT NULL'),
    (N'P1',N'StoryboardFrames',N'CombatBeatIds',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Timeline',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Skills',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'Scene',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'StoryboardFrames',N'BatchNumber',N'INT'),
    (N'P0',N'StoryboardFrames',N'SortOrder',N'INT'),
    (N'P0',N'StoryboardFrames',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'CharacterAssets',N'AssetId',N'INT IDENTITY'),
    (N'P0',N'CharacterAssets',N'ProjectId',N'INT'),
    (N'P0',N'CharacterAssets',N'Name',N'NVARCHAR(100)'),
    (N'P1',N'CharacterAssets',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'CharacterAssets',N'ImageUrl',N'NVARCHAR(500) NULL'),
    (N'P1',N'CharacterAssets',N'Attributes',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'CharacterAssets',N'ImagePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'CharacterAssets',N'NegativePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'CharacterAssets',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'PropAssets',N'AssetId',N'INT IDENTITY'),
    (N'P0',N'PropAssets',N'ProjectId',N'INT'),
    (N'P0',N'PropAssets',N'Name',N'NVARCHAR(100)'),
    (N'P1',N'PropAssets',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'PropAssets',N'ImageUrl',N'NVARCHAR(500) NULL'),
    (N'P1',N'PropAssets',N'ImagePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'PropAssets',N'NegativePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'PropAssets',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'EnvironmentAssets',N'AssetId',N'INT IDENTITY'),
    (N'P0',N'EnvironmentAssets',N'ProjectId',N'INT'),
    (N'P0',N'EnvironmentAssets',N'Name',N'NVARCHAR(100)'),
    (N'P1',N'EnvironmentAssets',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'EnvironmentAssets',N'ImageUrl',N'NVARCHAR(500) NULL'),
    (N'P1',N'EnvironmentAssets',N'ImagePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'EnvironmentAssets',N'NegativePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'EnvironmentAssets',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'EffectAssets',N'AssetId',N'INT IDENTITY'),
    (N'P0',N'EffectAssets',N'ProjectId',N'INT'),
    (N'P0',N'EffectAssets',N'Name',N'NVARCHAR(100)'),
    (N'P1',N'EffectAssets',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'EffectAssets',N'ImageUrl',N'NVARCHAR(500) NULL'),
    (N'P1',N'EffectAssets',N'ImagePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'EffectAssets',N'NegativePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'EffectAssets',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'SeedancePrompts',N'PromptId',N'INT IDENTITY'),
    (N'P0',N'SeedancePrompts',N'ProjectId',N'INT'),
    (N'P1',N'SeedancePrompts',N'FrameId',N'INT NULL'),
    (N'P0',N'SeedancePrompts',N'PromptText',N'NVARCHAR(MAX)'),
    (N'P1',N'SeedancePrompts',N'NegativePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'SeedancePrompts',N'VideoUrl',N'NVARCHAR(500) NULL'),
    (N'P1',N'SeedancePrompts',N'LocalVideoUrl',N'NVARCHAR(500) NULL'),
    (N'P0',N'SeedancePrompts',N'Status',N'NVARCHAR(20)'),
    (N'P1',N'SeedancePrompts',N'BatchNumber',N'INT'),
    (N'P1',N'SeedancePrompts',N'EpisodeNumber',N'INT'),
    (N'P1',N'SeedancePrompts',N'UnitName',N'NVARCHAR(200) NULL'),
    (N'P1',N'SeedancePrompts',N'ShotLabel',N'NVARCHAR(100) NULL'),
    (N'P1',N'SeedancePrompts',N'ShotType',N'NVARCHAR(50) NULL'),
    (N'P1',N'SeedancePrompts',N'Duration',N'INT'),
    (N'P1',N'SeedancePrompts',N'ReferenceImages',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'SeedancePrompts',N'ReferenceVideos',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'SeedancePrompts',N'ReferenceAudio',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'SeedancePrompts',N'ShotNumber',N'INT'),
    (N'P1',N'SeedancePrompts',N'PromptTextH3',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'SeedancePrompts',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'CoherenceChecks',N'CheckId',N'INT IDENTITY'),
    (N'P0',N'CoherenceChecks',N'ProjectId',N'INT'),
    (N'P1',N'CoherenceChecks',N'Issues',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'CoherenceChecks',N'Status',N'NVARCHAR(20)'),
    (N'P0',N'CoherenceChecks',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'LLMConfigs',N'ConfigId',N'INT IDENTITY'),
    (N'P0',N'LLMConfigs',N'UserId',N'INT'),
    (N'P0',N'LLMConfigs',N'Provider',N'NVARCHAR(50)'),
    (N'P0',N'LLMConfigs',N'ApiKey',N'NVARCHAR(500)'),
    (N'P1',N'LLMConfigs',N'ApiUrl',N'NVARCHAR(500) NULL'),
    (N'P1',N'LLMConfigs',N'ModelName',N'NVARCHAR(100) NULL'),
    (N'P1',N'LLMConfigs',N'ThinkingMode',N'NVARCHAR(20) NULL'),
    (N'P0',N'LLMConfigs',N'IsActive',N'BIT'),
    (N'P1',N'LLMConfigs',N'AutoEnhance',N'BIT'),
    (N'P0',N'LLMConfigs',N'CreatedAt',N'DATETIME2'),
    (N'P1',N'LLMConfigs',N'UpdatedAt',N'DATETIME2 NULL'),

    (N'P0',N'VideoGenerationTasks',N'Id',N'INT IDENTITY'),
    (N'P0',N'VideoGenerationTasks',N'ProjectId',N'INT'),
    (N'P0',N'VideoGenerationTasks',N'PromptId',N'INT'),
    (N'P0',N'VideoGenerationTasks',N'TaskId',N'NVARCHAR(200)'),
    (N'P0',N'VideoGenerationTasks',N'Engine',N'NVARCHAR(20)'),
    (N'P0',N'VideoGenerationTasks',N'Status',N'NVARCHAR(50)'),
    (N'P1',N'VideoGenerationTasks',N'VideoUrl',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'VideoGenerationTasks',N'LocalVideoUrl',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'VideoGenerationTasks',N'RequestDuration',N'INT'),
    (N'P1',N'VideoGenerationTasks',N'RequestRatio',N'NVARCHAR(20)'),
    (N'P1',N'VideoGenerationTasks',N'RequestWatermark',N'BIT'),
    (N'P1',N'VideoGenerationTasks',N'RequestGenerateAudio',N'BIT'),
    (N'P1',N'VideoGenerationTasks',N'ResponseResolution',N'NVARCHAR(50) NULL'),
    (N'P1',N'VideoGenerationTasks',N'ResponseDuration',N'FLOAT NULL'),
    (N'P1',N'VideoGenerationTasks',N'EnhanceDuration',N'INT NULL'),
    (N'P1',N'VideoGenerationTasks',N'EnhanceResolution',N'NVARCHAR(50) NULL'),
    (N'P1',N'VideoGenerationTasks',N'ResponseUsageTokens',N'INT NULL'),
    (N'P1',N'VideoGenerationTasks',N'ResponseSeed',N'INT NULL'),
    (N'P1',N'VideoGenerationTasks',N'ApiStatus',N'NVARCHAR(50) NULL'),
    (N'P1',N'VideoGenerationTasks',N'ErrorMessage',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'VideoGenerationTasks',N'CreatedAt',N'DATETIME2'),
    (N'P1',N'VideoGenerationTasks',N'CompletedAt',N'DATETIME2 NULL'),

    (N'P0',N'VideoStyles',N'StyleId',N'INT IDENTITY'),
    (N'P0',N'VideoStyles',N'StyleName',N'NVARCHAR(100)'),
    (N'P0',N'VideoStyles',N'StylePrompt',N'NVARCHAR(MAX)'),
    (N'P1',N'VideoStyles',N'IsDefault',N'BIT'),
    (N'P0',N'VideoStyles',N'CreatedAt',N'DATETIME'),
    (N'P0',N'VideoStyles',N'UpdatedAt',N'DATETIME'),

    (N'P0',N'ReferenceAssets',N'AssetId',N'INT IDENTITY'),
    (N'P0',N'ReferenceAssets',N'UserId',N'INT'),
    (N'P0',N'ReferenceAssets',N'FileName',N'NVARCHAR'),
    (N'P0',N'ReferenceAssets',N'LocalPath',N'NVARCHAR(500)'),
    (N'P1',N'ReferenceAssets',N'FileType',N'NVARCHAR'),
    (N'P1',N'ReferenceAssets',N'Category',N'NVARCHAR NULL'),
    (N'P1',N'ReferenceAssets',N'SubCategory',N'NVARCHAR NULL'),
    (N'P1',N'ReferenceAssets',N'Tags',N'NVARCHAR(500) NULL'),
    (N'P1',N'ReferenceAssets',N'FileSize',N'BIGINT NULL'),
    (N'P1',N'ReferenceAssets',N'SourceKey',N'NVARCHAR(200) NULL'),
    (N'P0',N'ReferenceAssets',N'CreatedAt',N'DATETIME/DATETIME2'),

    (N'P0',N'SkillLibrary',N'SkillId',N'INT IDENTITY'),
    (N'P0',N'SkillLibrary',N'UserId',N'INT'),
    (N'P1',N'SkillLibrary',N'ProjectId',N'INT NULL'),
    (N'P0',N'SkillLibrary',N'Name',N'NVARCHAR(100)'),
    (N'P1',N'SkillLibrary',N'Element',N'NVARCHAR(20) NULL'),
    (N'P1',N'SkillLibrary',N'Tier',N'INT NULL'),
    (N'P1',N'SkillLibrary',N'PromptImage',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'SkillLibrary',N'PromptVideo',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'SkillLibrary',N'ImageUrl',N'NVARCHAR(500) NULL'),
    (N'P1',N'SkillLibrary',N'Tags',N'NVARCHAR(500) NULL'),
    (N'P1',N'SkillLibrary',N'OwnerCharacter',N'NVARCHAR(100) NULL'),
    (N'P0',N'SkillLibrary',N'CreatedAt',N'DATETIME/DATETIME2'),

    (N'P0',N'SkillElements',N'ElementId',N'INT IDENTITY'),
    (N'P0',N'SkillElements',N'UserId',N'INT'),
    (N'P0',N'SkillElements',N'Name',N'NVARCHAR(50)'),
    (N'P1',N'SkillElements',N'SortOrder',N'INT NOT NULL'),
    (N'P0',N'SkillElements',N'CreatedAt',N'DATETIME/DATETIME2'),

    (N'P0',N'FightTemplate',N'FightTemplateId',N'INT IDENTITY'),
    (N'P0',N'FightTemplate',N'UserId',N'INT'),
    (N'P0',N'FightTemplate',N'Name',N'NVARCHAR(100)'),
    (N'P1',N'FightTemplate',N'Tier',N'INT NULL'),
    (N'P1',N'FightTemplate',N'Duration',N'INT NULL'),
    (N'P1',N'FightTemplate',N'Scene',N'NVARCHAR(500) NULL'),
    (N'P1',N'FightTemplate',N'Beat',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'FightTemplate',N'ActionPrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'FightTemplate',N'CameraPrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'FightTemplate',N'ConstraintPrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'FightTemplate',N'Tags',N'NVARCHAR(500) NULL'),
    (N'P0',N'FightTemplate',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'FightArcTemplates',N'FightArcTemplateId',N'INT IDENTITY'),
    (N'P0',N'FightArcTemplates',N'ArcTypeId',N'NVARCHAR(50)'),
    (N'P0',N'FightArcTemplates',N'Name',N'NVARCHAR(100)'),
    (N'P0',N'FightArcTemplates',N'Version',N'NVARCHAR(20)'),
    (N'P0',N'FightArcTemplates',N'PhasesJson',N'NVARCHAR(MAX)'),
    (N'P0',N'FightArcTemplates',N'DurationBudgetJson',N'NVARCHAR(MAX)'),
    (N'P0',N'FightArcTemplates',N'RulesJson',N'NVARCHAR(MAX)'),
    (N'P0',N'FightArcTemplates',N'Status',N'NVARCHAR(20)'),

    (N'P0',N'ShotDirective',N'ShotDirectiveId',N'INT IDENTITY'),
    (N'P0',N'ShotDirective',N'UserId',N'INT'),
    (N'P0',N'ShotDirective',N'Name',N'NVARCHAR(100)'),
    (N'P0',N'ShotDirective',N'Category',N'NVARCHAR(20)'),
    (N'P1',N'ShotDirective',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'ShotDirective',N'Tags',N'NVARCHAR(500) NULL'),
    (N'P0',N'ShotDirective',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'TokenUsageConfig',N'Id',N'INT'),
    (N'P0',N'TokenUsageConfig',N'QuotaTokens',N'BIGINT'),
    (N'P0',N'TokenUsageConfig',N'UpdatedAt',N'DATETIME2'),

    (N'P0',N'DirectorPlans',N'DirectorPlanId',N'INT IDENTITY'),
    (N'P0',N'DirectorPlans',N'ProjectId',N'INT'),
    (N'P0',N'DirectorPlans',N'EpisodeNumber',N'INT'),
    (N'P0',N'DirectorPlans',N'UnitNumber',N'NVARCHAR(50)'),
    (N'P0',N'DirectorPlans',N'UnitType',N'NVARCHAR(50)'),
    (N'P0',N'DirectorPlans',N'DramaticPurpose',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'PrimarySubject',N'NVARCHAR(200)'),
    (N'P0',N'DirectorPlans',N'SecondarySubject',N'NVARCHAR(200)'),
    (N'P0',N'DirectorPlans',N'ConflictType',N'NVARCHAR(20)'),
    (N'P0',N'DirectorPlans',N'CorePayoff',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'EmotionCurve',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'RhythmStrategy',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'ActionStrategy',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'PerformanceStrategy',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'CameraStrategy',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'VfxStrategy',N'NVARCHAR(MAX)'),
    (N'P0',N'DirectorPlans',N'IntensityLevel',N'INT'),
    (N'P0',N'DirectorPlans',N'CombatGrammarIds',N'NVARCHAR(500)'),
    (N'P1',N'DirectorPlans',N'CombatRoundCount',N'INT'),
    (N'P1',N'DirectorPlans',N'VfxPeakPhase',N'NVARCHAR(20)'),
    (N'P1',N'DirectorPlans',N'ActionPlan',N'NVARCHAR(MAX)'),
    (N'P1',N'DirectorPlans',N'FightArcType',N'NVARCHAR(50)'),
    (N'P1',N'DirectorPlans',N'FightSequenceJson',N'NVARCHAR(MAX)'),
    (N'P1',N'DirectorPlans',N'NeedsReview',N'BIT'),
    (N'P1',N'DirectorPlans',N'ValidationScore',N'INT'),
    (N'P1',N'DirectorPlans',N'ViolationsJson',N'NVARCHAR(MAX)'),
    (N'P1',N'DirectorPlans',N'RepairCount',N'INT'),
    (N'P1',N'DirectorPlans',N'LastValidationAt',N'DATETIME NULL'),
    (N'P0',N'DirectorPlans',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'DirectorPlans',N'UpdatedAt',N'DATETIME2'),

    (N'P0',N'EpisodeDirectorPlans',N'EpisodeDirectorPlanId',N'INT IDENTITY'),
    (N'P0',N'EpisodeDirectorPlans',N'ProjectId',N'INT'),
    (N'P0',N'EpisodeDirectorPlans',N'EpisodeNumber',N'INT'),
    (N'P0',N'EpisodeDirectorPlans',N'EpisodeGoal',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'IntensityCurveJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'PayoffScheduleJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'ReservedVisualsJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'RepetitionPolicyJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'UnitEmotionCurveJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'UnitTransitionsJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'UnitEndStatesJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorPlans',N'ClimaxBudgetJson',N'NVARCHAR(MAX)'),

    (N'P0',N'EpisodeDirectorStates',N'EpisodeDirectorStateId',N'INT IDENTITY'),
    (N'P0',N'EpisodeDirectorStates',N'ProjectId',N'INT'),
    (N'P0',N'EpisodeDirectorStates',N'EpisodeNumber',N'INT'),
    (N'P0',N'EpisodeDirectorStates',N'CameraPatternCountsJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorStates',N'CombatPatternCountsJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorStates',N'VfxPatternCountsJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeDirectorStates',N'SlowMotionCount',N'INT'),
    (N'P0',N'EpisodeDirectorStates',N'MajorExplosionCount',N'INT'),
    (N'P0',N'EpisodeDirectorStates',N'CurrentPeakIntensity',N'INT'),
    (N'P0',N'EpisodeDirectorStates',N'SmallClimaxCount',N'INT'),
    (N'P0',N'EpisodeDirectorStates',N'MidClimaxCount',N'INT'),
    (N'P0',N'EpisodeDirectorStates',N'LargeClimaxCount',N'INT'),

    (N'P0',N'EpisodeUnitStateSnapshots',N'EpisodeUnitStateSnapshotId',N'INT IDENTITY'),
    (N'P0',N'EpisodeUnitStateSnapshots',N'ProjectId',N'INT'),
    (N'P0',N'EpisodeUnitStateSnapshots',N'EpisodeNumber',N'INT'),
    (N'P0',N'EpisodeUnitStateSnapshots',N'UnitNumber',N'NVARCHAR(50)'),
    (N'P0',N'EpisodeUnitStateSnapshots',N'StateJson',N'NVARCHAR(MAX)'),
    (N'P0',N'EpisodeUnitStateSnapshots',N'Source',N'NVARCHAR(20)'),
    (N'P0',N'EpisodeUnitStateSnapshots',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'EpisodeUnitStateSnapshots',N'UpdatedAt',N'DATETIME2'),

    (N'P0',N'Works',N'WorkId',N'INT IDENTITY'),
    (N'P0',N'Works',N'UserId',N'INT'),
    (N'P0',N'Works',N'Title',N'NVARCHAR(200)'),
    (N'P1',N'Works',N'Description',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'Works',N'CoverImage',N'NVARCHAR(500) NULL'),
    (N'P1',N'Works',N'WorkUrl',N'NVARCHAR(500) NULL'),
    (N'P0',N'Works',N'CreatedAt',N'DATETIME2'),
    (N'P1',N'Works',N'UpdatedAt',N'DATETIME2 NULL'),

    (N'P0',N'StageProgressLogs',N'ProgressLogId',N'INT IDENTITY'),
    (N'P0',N'StageProgressLogs',N'ProjectId',N'INT'),
    (N'P0',N'StageProgressLogs',N'StageNumber',N'INT'),
    (N'P0',N'StageProgressLogs',N'LogText',N'NVARCHAR(1000)'),
    (N'P0',N'StageProgressLogs',N'CreatedAt',N'DATETIME'),

    (N'P0',N'CameraAtom',N'AtomId',N'INT IDENTITY'),
    (N'P0',N'CameraAtom',N'UserId',N'INT'),
    (N'P0',N'CameraAtom',N'Category',N'NVARCHAR(20)'),
    (N'P0',N'CameraAtom',N'Name',N'NVARCHAR(50)'),
    (N'P0',N'CameraAtom',N'Description',N'NVARCHAR(600)'),
    (N'P1',N'CameraAtom',N'Tags',N'NVARCHAR(200) NULL'),
    (N'P1',N'CameraAtom',N'CreatedAt',N'DATETIME2 NULL'),

    (N'P0',N'FrameAssetBindings',N'BindingId',N'INT IDENTITY'),
    (N'P0',N'FrameAssetBindings',N'ProjectId',N'INT'),
    (N'P0',N'FrameAssetBindings',N'FrameId',N'INT'),
    (N'P0',N'FrameAssetBindings',N'Category',N'NVARCHAR(20)'),
    (N'P0',N'FrameAssetBindings',N'AssetId',N'INT'),
    (N'P0',N'FrameAssetBindings',N'Name',N'NVARCHAR(100)'),
    (N'P1',N'FrameAssetBindings',N'HasImage',N'BIT'),
    (N'P1',N'FrameAssetBindings',N'SortOrder',N'INT'),
    (N'P0',N'FrameAssetBindings',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'UnitAssetBindings',N'BindingId',N'INT IDENTITY'),
    (N'P0',N'UnitAssetBindings',N'ProjectId',N'INT'),
    (N'P0',N'UnitAssetBindings',N'EpisodeNumber',N'INT'),
    (N'P0',N'UnitAssetBindings',N'UnitNumber',N'NVARCHAR(64)'),
    (N'P0',N'UnitAssetBindings',N'Category',N'NVARCHAR(16)'),
    (N'P0',N'UnitAssetBindings',N'AssetId',N'INT'),
    (N'P0',N'UnitAssetBindings',N'Name',N'NVARCHAR(200)'),
    (N'P1',N'UnitAssetBindings',N'HasImage',N'BIT'),
    (N'P1',N'UnitAssetBindings',N'SortOrder',N'INT'),

    (N'P0',N'VoiceReferences',N'VoiceId',N'INT IDENTITY'),
    (N'P0',N'VoiceReferences',N'ProjectId',N'INT'),
    (N'P0',N'VoiceReferences',N'CharacterName',N'NVARCHAR(200)'),
    (N'P0',N'VoiceReferences',N'AudioUrl',N'NVARCHAR(1000)'),
    (N'P1',N'VoiceReferences',N'OriginalFileName',N'NVARCHAR(400) NULL'),
    (N'P0',N'VoiceReferences',N'CreatedAt',N'DATETIME2'),

    (N'P0',N'AssetPromptTemplates',N'TemplateId',N'INT IDENTITY'),
    (N'P0',N'AssetPromptTemplates',N'UserId',N'INT'),
    (N'P0',N'AssetPromptTemplates',N'ProjectId',N'INT'),
    (N'P0',N'AssetPromptTemplates',N'Category',N'NVARCHAR(20)'),
    (N'P1',N'AssetPromptTemplates',N'StyleLock',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'AssetPromptTemplates',N'NegativePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'AssetPromptTemplates',N'RuleText',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'AssetPromptTemplates',N'Enabled',N'BIT'),
    (N'P0',N'AssetPromptTemplates',N'UpdatedAt',N'DATETIME2'),

    (N'P0',N'AssetImageTasks',N'TaskId',N'INT IDENTITY'),
    (N'P0',N'AssetImageTasks',N'ProjectId',N'INT'),
    (N'P0',N'AssetImageTasks',N'UserId',N'INT'),
    (N'P0',N'AssetImageTasks',N'Category',N'NVARCHAR(32)'),
    (N'P0',N'AssetImageTasks',N'AssetId',N'INT'),
    (N'P1',N'AssetImageTasks',N'AssetName',N'NVARCHAR(200) NULL'),
    (N'P0',N'AssetImageTasks',N'Status',N'NVARCHAR(16)'),
    (N'P1',N'AssetImageTasks',N'PromptOverride',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'AssetImageTasks',N'NegativeOverride',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'AssetImageTasks',N'ExtraPrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'AssetImageTasks',N'Size',N'NVARCHAR(16) NULL'),
    (N'P1',N'AssetImageTasks',N'ImageUrl',N'NVARCHAR(400) NULL'),
    (N'P1',N'AssetImageTasks',N'LibraryAssetId',N'INT NULL'),
    (N'P1',N'AssetImageTasks',N'UsedPrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'AssetImageTasks',N'ErrorMessage',N'NVARCHAR(MAX) NULL'),
    (N'P0',N'AssetImageTasks',N'CreatedAt',N'DATETIME'),
    (N'P1',N'AssetImageTasks',N'StartedAt',N'DATETIME NULL'),
    (N'P1',N'AssetImageTasks',N'FinishedAt',N'DATETIME NULL'),

    (N'P0',N'ProjectContinuityTables',N'ContinuityId',N'INT IDENTITY'),
    (N'P0',N'ProjectContinuityTables',N'ProjectId',N'INT'),
    (N'P0',N'ProjectContinuityTables',N'EpisodeNumber',N'INT'),
    (N'P0',N'ProjectContinuityTables',N'TableType',N'NVARCHAR(40)'),
    (N'P0',N'ProjectContinuityTables',N'ContentJson',N'NVARCHAR(MAX)'),
    (N'P1',N'ProjectContinuityTables',N'ContentText',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'ProjectContinuityTables',N'Source',N'NVARCHAR(20)'),
    (N'P0',N'ProjectContinuityTables',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'ProjectContinuityTables',N'UpdatedAt',N'DATETIME2'),

    (N'P0',N'ProjectKeyframes',N'KeyframeId',N'INT IDENTITY'),
    (N'P0',N'ProjectKeyframes',N'ProjectId',N'INT'),
    (N'P0',N'ProjectKeyframes',N'EpisodeNumber',N'INT'),
    (N'P0',N'ProjectKeyframes',N'SortOrder',N'INT'),
    (N'P1',N'ProjectKeyframes',N'NodeLabel',N'NVARCHAR(100) NULL'),
    (N'P1',N'ProjectKeyframes',N'NodeReason',N'NVARCHAR(400) NULL'),
    (N'P1',N'ProjectKeyframes',N'ShotLabel',N'NVARCHAR(40) NULL'),
    (N'P1',N'ProjectKeyframes',N'UnitNumber',N'NVARCHAR(40) NULL'),
    (N'P1',N'ProjectKeyframes',N'Composition',N'NVARCHAR(1000) NULL'),
    (N'P1',N'ProjectKeyframes',N'LockedCharacters',N'NVARCHAR(1000) NULL'),
    (N'P1',N'ProjectKeyframes',N'LockedProps',N'NVARCHAR(1000) NULL'),
    (N'P1',N'ProjectKeyframes',N'LockedSceneDirection',N'NVARCHAR(1000) NULL'),
    (N'P1',N'ProjectKeyframes',N'ClueVisible',N'NVARCHAR(500) NULL'),
    (N'P1',N'ProjectKeyframes',N'NextConnection',N'NVARCHAR(1000) NULL'),
    (N'P1',N'ProjectKeyframes',N'ImagePrompt',N'NVARCHAR(MAX) NULL'),
    (N'P1',N'ProjectKeyframes',N'Status',N'NVARCHAR(20)'),
    (N'P1',N'ProjectKeyframes',N'Source',N'NVARCHAR(20)'),
    (N'P0',N'ProjectKeyframes',N'CreatedAt',N'DATETIME2'),
    (N'P0',N'ProjectKeyframes',N'UpdatedAt',N'DATETIME2')
) v(Severity, TableName, ColumnName, ExpectedDefinition)
WHERE OBJECT_ID(N'dbo.' + v.TableName, N'U') IS NULL
   OR COL_LENGTH(N'dbo.' + v.TableName, v.ColumnName) IS NULL
ORDER BY v.Severity, v.TableName, v.ColumnName;

PRINT N'=== 3. 目标索引检查：MISSING 表示基线或升级脚本仍需补齐 ===';
SELECT
    v.Severity,
    v.TableName,
    v.IndexName,
    v.Purpose,
    CASE WHEN i.object_id IS NULL THEN N'MISSING' ELSE N'OK' END AS Status,
    CASE WHEN i.object_id IS NULL THEN NULL ELSE CONVERT(INT, i.is_unique) END AS IsUnique
FROM (VALUES
    (N'P1',N'Projects',N'IX_Projects_UserId',N'按用户查询项目'),
    (N'P1',N'Dramas',N'IX_Dramas_UserId',N'按用户查询漫剧'),
    (N'P1',N'StageData',N'UX_StageData_Project_Stage',N'每项目每阶段一条'),
    (N'P1',N'Episodes',N'IX_Episodes_Project',N'按项目查询分集'),
    (N'P1',N'StoryboardFrames',N'IX_Frames_Episode',N'按分集查询分镜'),
    (N'P1',N'StoryboardFrames',N'IX_Frames_Unit',N'按单元增量替换分镜'),
    (N'P1',N'CharacterAssets',N'IX_CharAssets_Project',N'按项目查询角色'),
    (N'P1',N'PropAssets',N'IX_PropAssets_Project',N'按项目查询道具'),
    (N'P1',N'EnvironmentAssets',N'IX_EnvAssets_Project',N'按项目查询环境'),
    (N'P1',N'EffectAssets',N'IX_EffectAssets_Project',N'按项目查询特效'),
    (N'P1',N'SeedancePrompts',N'IX_Prompts_Project',N'按项目查询提示词'),
    (N'P1',N'CoherenceChecks',N'UX_CoherenceChecks_Project',N'每项目一条衔接检查'),
    (N'P1',N'LLMConfigs',N'UX_LLMConfigs_User_Provider',N'每用户每 Provider 一条配置'),
    (N'P1',N'ReferenceAssets',N'IX_RefAssets_User',N'按用户查询参考素材'),
    (N'P1',N'SkillLibrary',N'IX_SkillLibrary_User',N'按用户查询技能'),
    (N'P1',N'SkillLibrary',N'IX_SkillLibrary_Project',N'按项目查询技能'),
    (N'P1',N'SkillElements',N'UX_SkillElements_User_Name',N'每用户系别名唯一'),
    (N'P1',N'FightTemplate',N'IX_FightTemplate_User',N'按用户查询打斗模板'),
    (N'P1',N'ShotDirective',N'IX_ShotDirective_User',N'按用户查询分镜指令'),
    (N'P1',N'DirectorPlans',N'UX_DirectorPlans_Project_Unit',N'每项目每单元一条导演决策'),
    (N'P1',N'DirectorPlans',N'IX_DirectorPlans_Project',N'按项目查询导演决策'),
    (N'P1',N'Works',N'IX_Works_User',N'按用户查询作品'),
    (N'P1',N'VideoGenerationTasks',N'IX_VideoTasks_Project',N'按项目查询视频任务'),
    (N'P1',N'VideoGenerationTasks',N'IX_VideoTasks_Prompt_CreatedAt',N'按提示词查询最新任务'),
    (N'P1',N'VideoGenerationTasks',N'IX_VideoTasks_TaskId',N'按引擎任务号查询'),
    (N'P1',N'LLMConfigs',N'IX_LLMConfigs_User',N'按用户查询模型配置'),
    (N'P1',N'StoryboardFrames',N'IX_Frames_Project_UnitOrder',N'按单元顺序读取分镜'),
    (N'P1',N'ReferenceAssets',N'IX_RefAssets_User_SourceKey',N'自动出图按来源标记精确替换'),
    (N'P1',N'CameraAtom',N'IX_CameraAtom_User',N'按用户查询运镜原子'),
    (N'P1',N'FrameAssetBindings',N'IX_FAB_Project_Frame',N'按项目与帧查询资产绑定'),
    (N'P1',N'FrameAssetBindings',N'IX_FAB_Frame',N'按帧查询资产绑定'),
    (N'P1',N'UnitAssetBindings',N'IX_UnitAssetBindings_Project',N'按项目查询单元资产绑定'),
    (N'P1',N'UnitAssetBindings',N'IX_UnitAssetBindings_Unit',N'按单元查询资产绑定'),
    (N'P0',N'VoiceReferences',N'UX_VoiceReferences_Project_Character',N'每项目每角色一条音色参考'),
    (N'P1',N'VoiceReferences',N'IX_VoiceReferences_Project',N'按项目查询音色参考'),
    (N'P1',N'AssetPromptTemplates',N'UX_AssetPromptTemplates_User_Project_Category',N'每用户每剧每分类一条模版'),
    (N'P1',N'AssetImageTasks',N'IX_AssetImageTasks_Status',N'队列按状态扫单'),
    (N'P1',N'AssetImageTasks',N'IX_AssetImageTasks_Project',N'页面按项目轮询任务'),
    (N'P0',N'ProjectContinuityTables',N'UX_ProjectContinuityTables_Project_Type',N'每项目每集每类型一条连续性记录'),
    (N'P1',N'ProjectContinuityTables',N'IX_ProjectContinuityTables_Project',N'按项目查询连续性表'),
    (N'P1',N'ProjectKeyframes',N'IX_ProjectKeyframes_Project',N'按项目+集查询关键帧')
) v(Severity, TableName, IndexName, Purpose)
LEFT JOIN sys.tables t ON t.name = v.TableName AND SCHEMA_NAME(t.schema_id) = N'dbo'
LEFT JOIN sys.indexes i ON i.object_id = t.object_id AND i.name = v.IndexName
ORDER BY v.TableName, v.IndexName;

PRINT N'=== 4. 目标外键检查：本结果只报告，不自动补约束 ===';
SELECT
    v.Severity,
    v.ParentTable,
    v.ParentColumn,
    v.ReferencedTable,
    v.ReferencedColumn,
    v.ExpectedOnDelete,
    CASE WHEN fk.ForeignKeyName IS NULL THEN N'MISSING'
         WHEN fk.ActualOnDelete <> v.ExpectedOnDelete THEN N'DELETE_RULE_MISMATCH'
         ELSE N'OK' END AS Status,
    fk.ForeignKeyName,
    fk.ActualOnDelete
FROM (VALUES
    (N'P1',N'Dramas',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
    (N'P1',N'Projects',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
    (N'P1',N'Projects',N'DramaId',N'Dramas',N'DramaId',N'NO_ACTION'),
    (N'P2',N'Projects',N'StyleId',N'VideoStyles',N'StyleId',N'NO_ACTION'),
    (N'P1',N'StageData',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'Episodes',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'Episodes',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
    (N'P1',N'StoryboardFrames',N'EpisodeId',N'Episodes',N'EpisodeId',N'CASCADE'),
    (N'P1',N'StoryboardFrames',N'ProjectId',N'Projects',N'ProjectId',N'NO_ACTION'),
    (N'P1',N'CharacterAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'PropAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'EnvironmentAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'EffectAssets',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'SeedancePrompts',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'CoherenceChecks',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'LLMConfigs',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
    (N'P1',N'ReferenceAssets',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
    (N'P1',N'SkillLibrary',N'UserId',N'Users',N'UserId',N'CASCADE'),
    (N'P1',N'SkillLibrary',N'ProjectId',N'Projects',N'ProjectId',N'NO_ACTION'),
    (N'P1',N'SkillElements',N'UserId',N'Users',N'UserId',N'CASCADE'),
    (N'P1',N'FightTemplate',N'UserId',N'Users',N'UserId',N'CASCADE'),
    (N'P1',N'ShotDirective',N'UserId',N'Users',N'UserId',N'CASCADE'),
    (N'P1',N'DirectorPlans',N'ProjectId',N'Projects',N'ProjectId',N'CASCADE'),
    (N'P1',N'Works',N'UserId',N'Users',N'UserId',N'NO_ACTION'),
    (N'P1',N'VideoGenerationTasks',N'ProjectId',N'Projects',N'ProjectId',N'NO_ACTION'),
    (N'P1',N'VideoGenerationTasks',N'PromptId',N'SeedancePrompts',N'PromptId',N'CASCADE'),
    (N'P1',N'FrameAssetBindings',N'FrameId',N'StoryboardFrames',N'FrameId',N'CASCADE')
) v(Severity, ParentTable, ParentColumn, ReferencedTable, ReferencedColumn, ExpectedOnDelete)
OUTER APPLY (
    SELECT TOP (1)
        f.name AS ForeignKeyName,
        f.delete_referential_action_desc AS ActualOnDelete
    FROM sys.foreign_keys f
    JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id
    JOIN sys.columns pc ON pc.object_id = f.parent_object_id AND pc.column_id = fc.parent_column_id
    JOIN sys.columns rc ON rc.object_id = f.referenced_object_id AND rc.column_id = fc.referenced_column_id
    WHERE OBJECT_SCHEMA_NAME(f.parent_object_id) = N'dbo'
      AND OBJECT_NAME(f.parent_object_id) = v.ParentTable
      AND pc.name = v.ParentColumn
      AND OBJECT_NAME(f.referenced_object_id) = v.ReferencedTable
      AND rc.name = v.ReferencedColumn
) fk
ORDER BY v.ParentTable, v.ParentColumn;

PRINT N'=== 5. 重复键检查：IssueCount 必须为 0 才能增加唯一索引 ===';
IF OBJECT_ID(N'dbo.StageData', N'U') IS NOT NULL
    SELECT N'StageData(ProjectId,StageNumber)' AS CheckName, COUNT_BIG(*) AS IssueCount
    FROM (SELECT ProjectId, StageNumber FROM dbo.StageData GROUP BY ProjectId, StageNumber HAVING COUNT(*) > 1) d;
IF OBJECT_ID(N'dbo.CoherenceChecks', N'U') IS NOT NULL
    SELECT N'CoherenceChecks(ProjectId)' AS CheckName, COUNT_BIG(*) AS IssueCount
    FROM (SELECT ProjectId FROM dbo.CoherenceChecks GROUP BY ProjectId HAVING COUNT(*) > 1) d;
IF OBJECT_ID(N'dbo.LLMConfigs', N'U') IS NOT NULL
    SELECT N'LLMConfigs(UserId,Provider)' AS CheckName, COUNT_BIG(*) AS IssueCount
    FROM (SELECT UserId, Provider FROM dbo.LLMConfigs GROUP BY UserId, Provider HAVING COUNT(*) > 1) d;
IF OBJECT_ID(N'dbo.VoiceReferences', N'U') IS NOT NULL
    SELECT N'VoiceReferences(ProjectId,CharacterName)' AS CheckName, COUNT_BIG(*) AS IssueCount
    FROM (SELECT ProjectId, CharacterName FROM dbo.VoiceReferences GROUP BY ProjectId, CharacterName HAVING COUNT(*) > 1) d;
IF OBJECT_ID(N'dbo.AssetPromptTemplates', N'U') IS NOT NULL
    SELECT N'AssetPromptTemplates(UserId,ProjectId,Category)' AS CheckName, COUNT_BIG(*) AS IssueCount
    FROM (SELECT UserId, ProjectId, Category FROM dbo.AssetPromptTemplates GROUP BY UserId, ProjectId, Category HAVING COUNT(*) > 1) d;

IF OBJECT_ID(N'dbo.DirectorPlans', N'U') IS NOT NULL
    SELECT N'DirectorPlans(ProjectId,UnitNumber)' AS CheckName, COUNT_BIG(*) AS IssueCount
    FROM (SELECT ProjectId, UnitNumber FROM dbo.DirectorPlans GROUP BY ProjectId, UnitNumber HAVING COUNT(*) > 1) d;

PRINT N'=== 6. 孤儿数据检查：IssueCount 必须为 0 才能增加对应外键 ===';
IF OBJECT_ID(N'dbo.Projects', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Dramas', N'U') IS NOT NULL
    SELECT N'Projects without Drama' AS CheckName, COUNT_BIG(*) AS IssueCount FROM dbo.Projects p LEFT JOIN dbo.Dramas d ON d.DramaId=p.DramaId WHERE d.DramaId IS NULL;
IF OBJECT_ID(N'dbo.EffectAssets', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Projects', N'U') IS NOT NULL
    SELECT N'EffectAssets without Project' AS CheckName, COUNT_BIG(*) AS IssueCount FROM dbo.EffectAssets a LEFT JOIN dbo.Projects p ON p.ProjectId=a.ProjectId WHERE p.ProjectId IS NULL;
IF OBJECT_ID(N'dbo.DirectorPlans', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Projects', N'U') IS NOT NULL
    SELECT N'DirectorPlans without Project' AS CheckName, COUNT_BIG(*) AS IssueCount FROM dbo.DirectorPlans d LEFT JOIN dbo.Projects p ON p.ProjectId=d.ProjectId WHERE p.ProjectId IS NULL;
IF OBJECT_ID(N'dbo.VideoGenerationTasks', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Projects', N'U') IS NOT NULL
    SELECT N'VideoTasks without Project' AS CheckName, COUNT_BIG(*) AS IssueCount FROM dbo.VideoGenerationTasks t LEFT JOIN dbo.Projects p ON p.ProjectId=t.ProjectId WHERE p.ProjectId IS NULL;
IF OBJECT_ID(N'dbo.VideoGenerationTasks', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SeedancePrompts', N'U') IS NOT NULL
    SELECT N'VideoTasks without Prompt' AS CheckName, COUNT_BIG(*) AS IssueCount FROM dbo.VideoGenerationTasks t LEFT JOIN dbo.SeedancePrompts p ON p.PromptId=t.PromptId WHERE p.PromptId IS NULL;
IF OBJECT_ID(N'dbo.FrameAssetBindings', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.StoryboardFrames', N'U') IS NOT NULL
    SELECT N'FrameAssetBindings without Frame' AS CheckName, COUNT_BIG(*) AS IssueCount FROM dbo.FrameAssetBindings b LEFT JOIN dbo.StoryboardFrames f ON f.FrameId=b.FrameId WHERE f.FrameId IS NULL;

PRINT N'=== 7. Stage 编号与固定执行顺序 ===';
SELECT v.ExecutionOrder, v.StageNumber, v.StageName, v.IsOptional
FROM (VALUES
    (1,1,N'创意构思',0),(2,2,N'故事分析',0),(3,3,N'全局蓝图',0),
    (4,4,N'分集细化',0),(5,5,N'分镜脚本',0),(6,6,N'角色资产',0),
    (7,7,N'道具资产',0),(8,8,N'环境资产',0),(9,11,N'特效资产',1),
    (10,9,N'提示词生成',0),(11,10,N'衔接检查',0)
) v(ExecutionOrder, StageNumber, StageName, IsOptional)
ORDER BY v.ExecutionOrder;

IF OBJECT_ID(N'dbo.StageData', N'U') IS NOT NULL
BEGIN
    SELECT COUNT_BIG(*) AS InvalidStageNumberCount
    FROM dbo.StageData
    WHERE StageNumber NOT IN (1,2,3,4,5,6,7,8,9,10,11);

    SELECT
        COUNT_BIG(*) AS Stage11RecordCount,
        SUM(CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(Content,N''))),N'') IS NULL
                      AND NULLIF(LTRIM(RTRIM(ISNULL(LlmResponse,N''))),N'') IS NULL
                 THEN 1 ELSE 0 END) AS Stage11EmptyContentCount
    FROM dbo.StageData
    WHERE StageNumber = 11;
END;

PRINT N'=== 8. 表行数快照 ===';
SELECT
    s.name AS SchemaName,
    t.name AS TableName,
    SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) AS [Rows]
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.partitions p ON p.object_id = t.object_id
WHERE s.name = N'dbo'
GROUP BY s.name, t.name
ORDER BY t.name;

PRINT N'=== Verify.sql 执行完成：本脚本未修改数据库 ===';
