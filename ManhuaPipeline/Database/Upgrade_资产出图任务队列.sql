/* ============================================================================
   Upgrade_资产出图任务队列.sql
   目标：资产卡出图（文生图）从「同步请求」升级为「后台队列任务」。

   背景：原来出图在 HTTP 请求里同步等中转接口返回，页面一关/一刷新连接断开，
         ASP.NET Core 的 RequestAborted 会把这次出图取消——图白出、且资产卡不回填。
         改成入队后由后台服务执行，关页面/刷新/换设备都会继续跑完；前端回来轮询状态即可。

   表结构：
     Status: queued（排队中）→ running（出图中）→ completed（完成）/ failed（失败）
     PromptOverride / NegativeOverride / ExtraPrompt：出图请求参数落库，后台照原样执行
     ImageUrl / LibraryAssetId：结果回填（资产卡图片地址 + 参考图库记录 Id）
     UsedPrompt：实际发给图像模型的完整提示词（排查用）

   幂等：可重复执行。
   ============================================================================ */
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.AssetImageTasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AssetImageTasks
    (
        TaskId           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AssetImageTasks PRIMARY KEY,
        ProjectId        INT               NOT NULL,
        UserId           INT               NOT NULL,
        Category         NVARCHAR(32)      NOT NULL,
        AssetId          INT               NOT NULL,
        AssetName        NVARCHAR(200)     NULL,
        Status           NVARCHAR(16)      NOT NULL CONSTRAINT DF_AssetImageTasks_Status DEFAULT(N'queued'),
        PromptOverride   NVARCHAR(MAX)     NULL,
        NegativeOverride NVARCHAR(MAX)     NULL,
        ExtraPrompt      NVARCHAR(MAX)     NULL,
        Size             NVARCHAR(16)      NULL,
        ImageUrl         NVARCHAR(400)     NULL,
        LibraryAssetId   INT               NULL,
        UsedPrompt       NVARCHAR(MAX)     NULL,
        ErrorMessage     NVARCHAR(MAX)     NULL,
        CreatedAt        DATETIME          NOT NULL CONSTRAINT DF_AssetImageTasks_CreatedAt DEFAULT(GETDATE()),
        StartedAt        DATETIME          NULL,
        FinishedAt       DATETIME          NULL
    );
    PRINT N'已创建表 dbo.AssetImageTasks';
END
ELSE
    PRINT N'表 dbo.AssetImageTasks 已存在，跳过';
GO

-- 队列取任务：按 Status + TaskId 排序扫 queued
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AssetImageTasks_Status'
               AND object_id = OBJECT_ID(N'dbo.AssetImageTasks'))
BEGIN
    CREATE INDEX IX_AssetImageTasks_Status ON dbo.AssetImageTasks(Status, TaskId);
    PRINT N'已创建索引 IX_AssetImageTasks_Status';
END
GO

-- 页面轮询：按项目取最近任务
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AssetImageTasks_Project'
               AND object_id = OBJECT_ID(N'dbo.AssetImageTasks'))
BEGIN
    CREATE INDEX IX_AssetImageTasks_Project ON dbo.AssetImageTasks(ProjectId, TaskId DESC);
    PRINT N'已创建索引 IX_AssetImageTasks_Project';
END
GO

PRINT N'=== 结果核对：出图任务表 ===';
SELECT COUNT(*) AS TaskCount FROM dbo.AssetImageTasks;
GO
