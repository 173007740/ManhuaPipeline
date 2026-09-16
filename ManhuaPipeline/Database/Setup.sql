-- =============================================
-- 漫剧流水线 - 数据库建表脚本 (SQL Server)
-- =============================================

-- 创建数据库（如果不存在）
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'ManhuaPipeline')
    CREATE DATABASE [ManhuaPipeline];
GO

USE [ManhuaPipeline];
GO

-- 1. 用户表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
BEGIN
    CREATE TABLE [dbo].[Users] (
        [UserId]       INT IDENTITY(1,1) PRIMARY KEY,
        [Username]     NVARCHAR(50)  NOT NULL UNIQUE,
        [Email]        NVARCHAR(100) NOT NULL UNIQUE,
        [PasswordHash] NVARCHAR(256) NOT NULL,
        [Nickname]     NVARCHAR(50)  NULL,
        [Avatar]       NVARCHAR(500) NULL,
        [Role]         NVARCHAR(20)  NOT NULL DEFAULT 'user',
        [IsActive]     BIT           NOT NULL DEFAULT 1,
        [CreatedAt]    DATETIME2     NOT NULL DEFAULT GETDATE(),
        [LastLoginAt]  DATETIME2     NULL,
        [ActiveLLMProvider] NVARCHAR(20) NOT NULL DEFAULT 'deepseek',
        [ActiveVideoEngine] NVARCHAR(20) NOT NULL DEFAULT 'volcano'
    );
END
GO

-- 2. 项目表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Projects')
BEGIN
    CREATE TABLE [dbo].[Projects] (
        [ProjectId]     INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]        INT           NOT NULL REFERENCES [Users]([UserId]),
        [Title]         NVARCHAR(200) NOT NULL,
        [Description]   NVARCHAR(MAX) NULL,
        [ScriptContent] NVARCHAR(MAX) NULL,
        [CurrentStage]  INT           NOT NULL DEFAULT 0,
        [Status]        NVARCHAR(20)  NOT NULL DEFAULT 'draft',
        [CreatedAt]     DATETIME2     NOT NULL DEFAULT GETDATE(),
        [UpdatedAt]     DATETIME2     NOT NULL DEFAULT GETDATE(),
        [EpisodeCount] INT           NOT NULL        
    );
    CREATE INDEX [IX_Projects_UserId] ON [dbo].[Projects]([UserId]);
END
GO

-- 3. 阶段数据表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'StageData')
BEGIN
    CREATE TABLE [dbo].[StageData] (
        [StageId]     INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]   INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [StageNumber] INT           NOT NULL,
        [Content]     NVARCHAR(MAX) NULL,
        [LlmResponse] NVARCHAR(MAX) NULL,
        -- L1 故事层：阶段 1/2/3 合并为一次调用后，结构化故事基线（节 1-5）JSON 落在这里（见 Upgrade_StageData_StructuredJson.sql）
        [StructuredJson] NVARCHAR(MAX) NULL,
        [Status]      NVARCHAR(20)  NOT NULL DEFAULT 'pending',
        [CreatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE(),
        [UpdatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_StageData_Project] ON [dbo].[StageData]([ProjectId], [StageNumber]);
END
GO

-- 4. 分集表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Episodes')
BEGIN
    CREATE TABLE [dbo].[Episodes] (
        [EpisodeId]     INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]     INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [UserId]        INT           NOT NULL REFERENCES [Users]([UserId]),
        [EpisodeNumber] INT           NOT NULL,
        [Title]         NVARCHAR(200) NOT NULL,
        [Summary]       NVARCHAR(MAX) NULL,
        [Content]       NVARCHAR(MAX) NULL,
        [SortOrder]     INT           NOT NULL DEFAULT 0,
        [CreatedAt]     DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_Episodes_Project] ON [dbo].[Episodes]([ProjectId]);
END
GO

-- 5. 分镜表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'StoryboardFrames')
BEGIN
    CREATE TABLE [dbo].[StoryboardFrames] (
        [FrameId]     INT IDENTITY(1,1) PRIMARY KEY,
        [EpisodeId]   INT           NOT NULL REFERENCES [Episodes]([EpisodeId]) ON DELETE CASCADE,
        [ProjectId]   INT           NOT NULL REFERENCES [Projects]([ProjectId]),
        [FrameNumber] INT           NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [Composition] NVARCHAR(MAX) NULL,
        [Characters]  NVARCHAR(MAX) NULL,
        [Dialogue]    NVARCHAR(MAX) NULL,
        [Camera]      NVARCHAR(MAX) NULL,
        [Duration]    NVARCHAR(MAX) NULL,
        [StartScene]  NVARCHAR(MAX) NULL,
        [EndScene]    NVARCHAR(MAX) NULL,
        [Scene]       NVARCHAR(MAX) NULL,
        -- L4 镜头状态机六字段（见 Upgrade_StoryboardFrames_L4StateFields.sql）
        [StartState]       NVARCHAR(MAX) NULL,
        [SingleAction]     NVARCHAR(MAX) NULL,
        [EndState]         NVARCHAR(MAX) NULL,
        [NextConnection]   NVARCHAR(MAX) NULL,
        [ForbiddenChanges] NVARCHAR(MAX) NULL,
        [NewInformation]   NVARCHAR(MAX) NULL,
        [UnitNumber]  NVARCHAR(50) NULL,
        [UnitOrder]   INT           NOT NULL DEFAULT 0,
        [EpisodeNumber] INT           NULL,
        [UnitType]      NVARCHAR(100) NULL,
        [ShotNumber]    NVARCHAR(50) NULL,
        [ShotSize]      NVARCHAR(200) NULL,
        [BatchNumber] INT           NOT NULL DEFAULT 1,
        [SortOrder]   INT           NOT NULL DEFAULT 0,
        [CreatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_Frames_Episode] ON [dbo].[StoryboardFrames]([EpisodeId]);
    CREATE INDEX [IX_Frames_Project] ON [dbo].[StoryboardFrames]([ProjectId]);
    CREATE INDEX [IX_Frames_Unit] ON [dbo].[StoryboardFrames]([ProjectId], [EpisodeId], [UnitNumber]);
END
GO

-- 6. 角色资产表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CharacterAssets')
BEGIN
    CREATE TABLE [dbo].[CharacterAssets] (
        [AssetId]     INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]   INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [Name]        NVARCHAR(100) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [ImageUrl]    NVARCHAR(500) NULL,
        [Attributes]  NVARCHAR(MAX) NULL,
        [CreatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_CharAssets_Project] ON [dbo].[CharacterAssets]([ProjectId]);
END
GO

-- 7. 道具资产表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PropAssets')
BEGIN
    CREATE TABLE [dbo].[PropAssets] (
        [AssetId]     INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]   INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [Name]        NVARCHAR(100) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [ImageUrl]    NVARCHAR(500) NULL,
        [CreatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_PropAssets_Project] ON [dbo].[PropAssets]([ProjectId]);
END
GO

-- 8. 环境资产表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'EnvironmentAssets')
BEGIN
    CREATE TABLE [dbo].[EnvironmentAssets] (
        [AssetId]     INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]   INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [Name]        NVARCHAR(100) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [ImageUrl]    NVARCHAR(500) NULL,
        [CreatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_EnvAssets_Project] ON [dbo].[EnvironmentAssets]([ProjectId]);
END
GO

-- 8.1 特效资产表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'EffectAssets')
BEGIN
    CREATE TABLE [dbo].[EffectAssets] (
        [AssetId]     INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]   INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [Name]        NVARCHAR(100) NOT NULL,
        [Description] NVARCHAR(MAX) NULL,
        [ImageUrl]    NVARCHAR(500) NULL,
        [CreatedAt]   DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_EffectAssets_Project] ON [dbo].[EffectAssets]([ProjectId]);
END
GO

-- 8.2 技能库表（技能大招提示词库）
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SkillLibrary')
BEGIN
    CREATE TABLE [dbo].[SkillLibrary] (
        [SkillId]       INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]        INT           NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [ProjectId]     INT           NULL REFERENCES [Projects]([ProjectId]),
        [Name]          NVARCHAR(100) NOT NULL,
        [Element]       NVARCHAR(20)  NULL,
        [Tier]          INT           NULL DEFAULT 4,
        [OwnerCharacter] NVARCHAR(100) NULL,
        [PromptImage]   NVARCHAR(MAX) NULL,
        [PromptVideo]   NVARCHAR(MAX) NULL,
        [ImageUrl]      NVARCHAR(500) NULL,
        [Tags]          NVARCHAR(500) NULL,
        [CreatedAt]     DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_SkillLibrary_User] ON [dbo].[SkillLibrary]([UserId]);
    CREATE INDEX [IX_SkillLibrary_Project] ON [dbo].[SkillLibrary]([ProjectId]);
END
GO

-- 8.3 打斗模板库表（打斗动作提示词库）
-- 8.25 技能系别表（可维护，增删改查）
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SkillElements')
BEGIN
    CREATE TABLE [dbo].[SkillElements] (
        [ElementId] INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]    INT NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [Name]      NVARCHAR(50) NOT NULL,
        [SortOrder] INT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME2 NOT NULL DEFAULT GETDATE()
    );
    CREATE UNIQUE INDEX [UX_SkillElements_User_Name] ON [dbo].[SkillElements]([UserId], [Name]);
END
GO

IF EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
    INSERT INTO [dbo].[SkillElements]([UserId], [Name], [SortOrder])
    SELECT u.[UserId], x.[Name], x.[SortOrder]
    FROM [dbo].[Users] u
    CROSS APPLY (VALUES (N'火',1),(N'冰',2),(N'雷',3),(N'剑阵',4),(N'风',5),(N'暗',6),(N'圣',7)) x([Name], [SortOrder])
    WHERE NOT EXISTS (SELECT 1 FROM [dbo].[SkillElements] se WHERE se.[UserId]=u.[UserId] AND se.[Name]=x.[Name]);
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FightTemplate')
BEGIN
    CREATE TABLE [dbo].[FightTemplate] (
        [FightTemplateId]   INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]            INT           NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [Name]              NVARCHAR(100) NOT NULL,
        [Tier]              INT           NULL DEFAULT 3,
        [Duration]          INT           NULL DEFAULT 11,
        [Scene]             NVARCHAR(500) NULL,
        [Beat]              NVARCHAR(MAX) NULL,
        [ActionPrompt]      NVARCHAR(MAX) NULL,
        [CameraPrompt]      NVARCHAR(MAX) NULL,
        [ConstraintPrompt]  NVARCHAR(MAX) NULL,
        [Tags]              NVARCHAR(500) NULL,
        [CreatedAt]         DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_FightTemplate_User] ON [dbo].[FightTemplate]([UserId]);
END
GO

-- 8.4 分镜指令库表（景别/表情/运镜/转场/光影/组合 提示词库）
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ShotDirective')
BEGIN
    CREATE TABLE [dbo].[ShotDirective] (
        [ShotDirectiveId]   INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]            INT           NOT NULL REFERENCES [Users]([UserId]) ON DELETE CASCADE,
        [Name]              NVARCHAR(100) NOT NULL,
        [Category]          NVARCHAR(20)  NOT NULL,
        [Description]       NVARCHAR(MAX) NULL,
        [Tags]              NVARCHAR(500) NULL,
        [CreatedAt]         DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_ShotDirective_User] ON [dbo].[ShotDirective]([UserId]);
END
GO

-- 9. Seedance 提示词表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SeedancePrompts')
BEGIN
    CREATE TABLE [dbo].[SeedancePrompts] (
        [PromptId]      INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]     INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [FrameId]       INT           NULL,
        [PromptText]    NVARCHAR(MAX) NOT NULL,
        [PromptTextH3]  NVARCHAR(MAX) NULL,
        [NegativePrompt] NVARCHAR(MAX) NULL,
        [VideoUrl]      NVARCHAR(500) NULL,
        [Status]        NVARCHAR(20)  NOT NULL DEFAULT 'generated',
        -- L5 逐镜状态机（见 Upgrade_SeedancePrompts_ShotStatus.sql）：ready/blocked_by_missing_asset/accepted/rejected
        [ShotStatus]    NVARCHAR(30)  NOT NULL DEFAULT 'ready',
        [CreatedAt]     DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_Prompts_Project] ON [dbo].[SeedancePrompts]([ProjectId]);
END
GO

-- 10. 衔接检查表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CoherenceChecks')
BEGIN
    CREATE TABLE [dbo].[CoherenceChecks] (
        [CheckId]    INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]  INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [Issues]     NVARCHAR(MAX) NULL,
        [Status]     NVARCHAR(20)  NOT NULL DEFAULT 'pending',
        [CreatedAt]  DATETIME2     NOT NULL DEFAULT GETDATE()
    );
END
GO

-- 11. LLM 配置表
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'LLMConfigs')
BEGIN
    CREATE TABLE [dbo].[LLMConfigs] (
        [ConfigId]  INT IDENTITY(1,1) PRIMARY KEY,
        [UserId]    INT           NOT NULL REFERENCES [Users]([UserId]),
        [Provider]  NVARCHAR(50)  NOT NULL,
        [ApiKey]    NVARCHAR(500) NOT NULL,
        [ApiUrl]    NVARCHAR(500) NULL,
        [ModelName] NVARCHAR(100) NULL,
        [IsActive]  BIT           NOT NULL DEFAULT 1,
        [AutoEnhance] BIT           NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_LLMConfigs_User] ON [dbo].[LLMConfigs]([UserId], [Provider]);
END
GO

-- 12. L3 关键帧层：每剧情节点一张关键帧（8-16 张/集）
--     见 Upgrade_关键帧层.sql；人工触发生成，阶段 9 按集注入【关键帧锚定】
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProjectKeyframes')
BEGIN
    CREATE TABLE [dbo].[ProjectKeyframes] (
        [KeyframeId]           INT IDENTITY(1,1) PRIMARY KEY,
        [ProjectId]            INT           NOT NULL REFERENCES [Projects]([ProjectId]) ON DELETE CASCADE,
        [EpisodeNumber]        INT           NOT NULL DEFAULT 0,
        [SortOrder]            INT           NOT NULL DEFAULT 0,
        [NodeLabel]            NVARCHAR(100) NULL,
        [NodeReason]           NVARCHAR(400) NULL,
        [ShotLabel]            NVARCHAR(40)  NULL,
        [UnitNumber]           NVARCHAR(40)  NULL,
        [Composition]          NVARCHAR(1000) NULL,
        [LockedCharacters]     NVARCHAR(1000) NULL,
        [LockedProps]          NVARCHAR(1000) NULL,
        [LockedSceneDirection] NVARCHAR(1000) NULL,
        [ClueVisible]          NVARCHAR(500) NULL,
        [NextConnection]       NVARCHAR(1000) NULL,
        [ImagePrompt]          NVARCHAR(MAX) NULL,
        [Status]               NVARCHAR(20)  NOT NULL DEFAULT 'draft',
        [Source]               NVARCHAR(20)  NOT NULL DEFAULT 'llm',
        [CreatedAt]            DATETIME2     NOT NULL DEFAULT GETDATE(),
        [UpdatedAt]            DATETIME2     NOT NULL DEFAULT GETDATE()
    );
    CREATE INDEX [IX_ProjectKeyframes_Project] ON [dbo].[ProjectKeyframes]([ProjectId], [EpisodeNumber], [SortOrder]);
END
GO

PRINT N'✅ 所有表创建完成！';
