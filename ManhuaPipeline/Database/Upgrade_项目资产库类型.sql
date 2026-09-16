-- Upgrade_项目资产库类型.sql
-- 目的：
--   1) Projects 表新增「资产库类型」列：资产图同步进参考图库时，写进图库的「类型」大类
--      （图库里的类型选项：动漫 / 写实 / 游戏 / 仙侠）。留空则沿用资产分类（角色/道具/环境/特效）。
--   2) ReferenceAssets 表新增「来源标记」列：记录这张图来自哪部剧的哪条资产（如 asset:12:characters:5）。
--      重新出图时按它精确替换上一张，这样「标签」可以纯粹用来放项目名称，也不会误删手动上传的图。
-- 用法：登录目标库执行一次即可；本脚本幂等（列已存在时跳过）。
IF COL_LENGTH(N'dbo.Projects', N'LibraryCategory') IS NULL
    ALTER TABLE dbo.Projects ADD LibraryCategory NVARCHAR(50) NULL;

IF COL_LENGTH(N'dbo.ReferenceAssets', N'SourceKey') IS NULL
    ALTER TABLE dbo.ReferenceAssets ADD SourceKey NVARCHAR(200) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ReferenceAssets') AND name = N'IX_RefAssets_User_SourceKey')
    CREATE INDEX IX_RefAssets_User_SourceKey ON dbo.ReferenceAssets(UserId, SourceKey);
