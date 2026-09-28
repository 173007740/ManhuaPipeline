-- =============================================================
-- 版本记录表（系统配置 → 「版本记录」页的数据源）
--
-- 为什么建这张表：系统迭代全靠对话里的口头记录，过两周就分不清「哪次改了什么」，
-- 出问题想回查是哪次改动引入的更是无从下手。git 目前也只有 4 个提交，
-- 改完的东西长期堆在工作区里，等于没有版本史。
--
-- 这张表把每次改动钉成一条记录：版本号 + 分类 + 正文 + 时间，前端按倒序展示，
-- 顶部导航还能直接显示「当前是哪个版本」。
--
-- 字段说明：
--   · Version      语义化版本号 v1.x.0，小改动与修复各占一个号，大重构才动第一位
--   · Category     feature / fix / optimize / refactor，前端按分类上色
--   · Content      markdown 文本，一行一条（"- xxx"），前端按行渲染成列表
--   · Author       谁改的：AI 代改一律 'AI'，用户手动记的写 'user'
--   · IsPublished  0 = 内部记录（前端不展示），用于记一些不想摆在明面上的过程项
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sysobjects WHERE name='VersionLogs' AND xtype='U')
BEGIN
    CREATE TABLE VersionLogs (
        LogId       INT           IDENTITY PRIMARY KEY,
        Version     NVARCHAR(20)  NOT NULL,                 -- 版本号，如 v1.10.0
        Title       NVARCHAR(100) NOT NULL,                 -- 一句话标题
        Category    NVARCHAR(16)  NOT NULL,                 -- feature / fix / optimize / refactor
        Content     NVARCHAR(MAX) NOT NULL,                 -- 正文，一行一条 "- xxx"
        ReleasedAt  DATETIME2     NOT NULL CONSTRAINT DF_VersionLogs_At DEFAULT SYSDATETIME(),
        Author      NVARCHAR(32)  NOT NULL CONSTRAINT DF_VersionLogs_Author DEFAULT 'AI',
        IsPublished BIT           NOT NULL CONSTRAINT DF_VersionLogs_Pub DEFAULT 1
    );
    CREATE INDEX IX_VersionLogs_Time ON VersionLogs(ReleasedAt DESC);
END
