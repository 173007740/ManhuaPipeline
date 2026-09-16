-- =============================================
-- 本地库升级脚本：把旧版数据库补齐到当前代码所需结构
-- 用途：本地/备份库恢复后，补齐新表和新增字段
-- 特点：幂等，可重复执行；只加表加字段，不删不改旧数据
-- 执行：SSMS 连接本地库后整段执行
-- =============================================
USE [ManhuaPipeline];
GO

-- ============ 一、新建表 ============

-- 1. 剧集表 Dramas（代码要求，旧库缺失）
IF OBJECT_ID('Dramas','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Dramas] (
        [DramaId]     INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]      INT NOT NULL,
        [Title]       NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [CoverImage]  NVARCHAR(500) NULL,
        [CreatedAt]   DATETIME2 NOT NULL DEFAULT GETDATE(),
        [UpdatedAt]   DATETIME2 NOT NULL DEFAULT GETDATE()
    );
END
GO

-- 2. 参考图库表 ReferenceAssets（代码要求，旧库缺失）
IF OBJECT_ID('ReferenceAssets','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ReferenceAssets] (
        [AssetId]     INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]      INT NOT NULL,
        [FileName]    NVARCHAR(255) NOT NULL,
        [LocalPath]   NVARCHAR(500) NOT NULL,
        [FileType]    NVARCHAR(50) NOT NULL,
        [Category]    NVARCHAR(100) NULL,
        [SubCategory] NVARCHAR(100) NULL,
        [Tags]        NVARCHAR(500) NULL,
        [FileSize]    BIGINT NULL,
        [CreatedAt]   DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_RefAssets_User] ON [dbo].[ReferenceAssets]([UserId]);
END
GO

-- 3. 视频生成任务表（代码会自动建，这里兜底，防止漏触发）
IF OBJECT_ID('VideoGenerationTasks','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[VideoGenerationTasks] (
        [Id]                    INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]             INT NOT NULL,
        [PromptId]              INT NOT NULL,
        [TaskId]                NVARCHAR(200) NOT NULL,
        [Status]                NVARCHAR(50) NOT NULL DEFAULT 'pending',
        [VideoUrl]              NVARCHAR(MAX) NULL,
        [LocalVideoUrl]         NVARCHAR(MAX) NULL,
        [RequestDuration]       INT NOT NULL DEFAULT 11,
        [RequestRatio]          NVARCHAR(20) NOT NULL DEFAULT '16:9',
        [RequestWatermark]      BIT NOT NULL DEFAULT 0,
        [RequestGenerateAudio]  BIT NOT NULL DEFAULT 1,
        [ResponseResolution]    NVARCHAR(50) NULL,
        [ResponseUsageTokens]   INT NULL,
        [ResponseSeed]          INT NULL,
        [ApiStatus]             NVARCHAR(50) NULL,
        [ErrorMessage]          NVARCHAR(MAX) NULL,
        [CreatedAt]             DATETIME2 NOT NULL DEFAULT GETDATE(),
        [CompletedAt]           DATETIME2 NULL
    );
END
GO

-- 4. 视频风格表（代码会自动建，这里兜底）
IF OBJECT_ID('VideoStyles','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[VideoStyles] (
        [StyleId]     INT IDENTITY(1,1) PRIMARY KEY,
        [StyleName]   NVARCHAR(100) NOT NULL,
        [StylePrompt] NVARCHAR(MAX) NOT NULL,
        [IsDefault]   BIT NOT NULL DEFAULT 0,
        [CreatedAt]   DATETIME NOT NULL DEFAULT GETDATE(),
        [UpdatedAt]   DATETIME NOT NULL DEFAULT GETDATE()
    );
END
GO

-- 5. 作品表 Works（代码会自动建，这里兜底）
IF OBJECT_ID('Works','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Works] (
        [WorkId]      INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]      INT NOT NULL,
        [Title]       NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [CoverImage]  NVARCHAR(500) NULL,
        [WorkUrl]     NVARCHAR(500) NULL,
        [CreatedAt]   DATETIME2 NOT NULL DEFAULT GETDATE(),
        [UpdatedAt]   DATETIME2 NULL
    );
END
GO

-- 6. Token 配额配置表（代码会自动建，这里兜底）
IF OBJECT_ID('TokenUsageConfig','U') IS NULL
BEGIN
    CREATE TABLE [dbo].[TokenUsageConfig] (
        [Id]           INT PRIMARY KEY,
        [QuotaTokens]  BIGINT NOT NULL DEFAULT 14000000,
        [UpdatedAt]    DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    IF NOT EXISTS (SELECT 1 FROM TokenUsageConfig WHERE Id=1)
        INSERT INTO TokenUsageConfig(Id, QuotaTokens) VALUES(1, 14000000);
END
GO

-- ============ 二、旧表加字段（幂等） ============

-- Users：ActiveLLMProvider（大模型切换开关）
IF COL_LENGTH('Users','ActiveLLMProvider') IS NULL ALTER TABLE [dbo].[Users] ADD [ActiveLLMProvider] NVARCHAR(20) NOT NULL DEFAULT 'deepseek';
IF COL_LENGTH('Users','ActiveVideoEngine') IS NULL ALTER TABLE [dbo].[Users] ADD [ActiveVideoEngine] NVARCHAR(20) NOT NULL DEFAULT 'volcano';
IF OBJECT_ID('VideoGenerationTasks','U') IS NOT NULL AND COL_LENGTH('VideoGenerationTasks','Engine') IS NULL ALTER TABLE [dbo].[VideoGenerationTasks] ADD [Engine] NVARCHAR(20) NOT NULL DEFAULT 'volcano';

-- Projects：DramaId（新建项目必需）、CurrentBatch（分批必需）
IF COL_LENGTH('Projects','DramaId') IS NULL ALTER TABLE [dbo].[Projects] ADD [DramaId] INT NULL;
IF COL_LENGTH('Projects','CurrentBatch') IS NULL ALTER TABLE [dbo].[Projects] ADD [CurrentBatch] INT NOT NULL DEFAULT 1;
IF COL_LENGTH('Projects','VideoRatio') IS NULL ALTER TABLE [dbo].[Projects] ADD [VideoRatio] NVARCHAR(10) NOT NULL DEFAULT '16:9';
IF COL_LENGTH('Projects','VideoWatermark') IS NULL ALTER TABLE [dbo].[Projects] ADD [VideoWatermark] BIT NOT NULL DEFAULT 0;
IF COL_LENGTH('Projects','VideoAudio') IS NULL ALTER TABLE [dbo].[Projects] ADD [VideoAudio] BIT NOT NULL DEFAULT 1;
IF COL_LENGTH('Projects','VideoResolution') IS NULL ALTER TABLE [dbo].[Projects] ADD [VideoResolution] NVARCHAR(10) NOT NULL DEFAULT '720p';


-- Episodes：BatchNumber
IF COL_LENGTH('Episodes','BatchNumber') IS NULL ALTER TABLE [dbo].[Episodes] ADD [BatchNumber] INT NOT NULL DEFAULT 1;

-- StoryboardFrames：BatchNumber
IF COL_LENGTH('StoryboardFrames','BatchNumber') IS NULL ALTER TABLE [dbo].[StoryboardFrames] ADD [BatchNumber] INT NOT NULL DEFAULT 1;

-- StageData：CurrentBatch
IF COL_LENGTH('StageData','CurrentBatch') IS NULL ALTER TABLE [dbo].[StageData] ADD [CurrentBatch] INT NOT NULL DEFAULT 1;

-- SeedancePrompts：补齐 10 个新字段（LocalVideoUrl/BatchNumber/EpisodeNumber/UnitName/ShotLabel/ShotType/Duration/参考图/参考视频/参考音频）
IF COL_LENGTH('SeedancePrompts','LocalVideoUrl') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [LocalVideoUrl] NVARCHAR(500) NULL;
IF COL_LENGTH('SeedancePrompts','BatchNumber') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [BatchNumber] INT NOT NULL DEFAULT 1;
IF COL_LENGTH('SeedancePrompts','EpisodeNumber') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [EpisodeNumber] INT NULL;
IF COL_LENGTH('SeedancePrompts','UnitName') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [UnitName] NVARCHAR(200) NULL;
IF COL_LENGTH('SeedancePrompts','ShotLabel') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [ShotLabel] NVARCHAR(100) NULL;
IF COL_LENGTH('SeedancePrompts','ShotType') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [ShotType] NVARCHAR(50) NULL;
IF COL_LENGTH('SeedancePrompts','Duration') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [Duration] INT NOT NULL DEFAULT 11;
IF COL_LENGTH('SeedancePrompts','ReferenceImages') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [ReferenceImages] NVARCHAR(MAX) NULL;
IF COL_LENGTH('SeedancePrompts','ReferenceVideos') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [ReferenceVideos] NVARCHAR(MAX) NULL;
IF COL_LENGTH('SeedancePrompts','ReferenceAudio') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [ReferenceAudio] NVARCHAR(MAX) NULL;
IF COL_LENGTH('SeedancePrompts','PromptTextH3') IS NULL ALTER TABLE [dbo].[SeedancePrompts] ADD [PromptTextH3] NVARCHAR(MAX) NULL;

-- VideoGenerationTasks：LocalVideoUrl（代码 UPDATE 用到，兜底）
IF COL_LENGTH('VideoGenerationTasks','LocalVideoUrl') IS NULL ALTER TABLE [dbo].[VideoGenerationTasks] ADD [LocalVideoUrl] NVARCHAR(MAX) NULL;
GO

-- ============ 三、完成提示 ============
PRINT N'✅ 升级脚本执行完成：新表已建、新字段已补，旧数据保留。';
GO
