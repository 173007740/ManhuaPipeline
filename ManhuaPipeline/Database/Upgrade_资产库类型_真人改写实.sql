-- Upgrade_资产库类型_真人改写实.sql
-- 目的：参考图库「类型」里的 “真人” 重命名为 “写实”，并同步改掉已有的历史数据。
--       只改前端下拉而不改库，旧图会仍挂着 “真人”，从类型筛选和标签配色里消失。
-- 涉及列：
--   ReferenceAssets.Category  —— 图库图片的类型（动漫 / 写实 / 游戏 / 仙侠）
--   Projects.LibraryCategory  —— 项目级「资产库类型」（资产图自动同步进图库时写入的值）
-- 用法：登录目标库执行一次即可；本脚本幂等，重复执行无副作用（第二次匹配 0 行）。
SET NOCOUNT ON;

-- 1) 图库图片的类型
IF OBJECT_ID(N'ReferenceAssets', N'U') IS NOT NULL
BEGIN
    UPDATE ReferenceAssets SET Category = N'写实' WHERE Category = N'真人';
    PRINT N'ReferenceAssets.Category：' + CAST(@@ROWCOUNT AS nvarchar(20)) + N' 行已由「真人」改为「写实」';
END
ELSE
    PRINT N'ReferenceAssets 表不存在，已跳过';

-- 2) 项目的资产库类型（列可能尚未创建，先探测）
IF COL_LENGTH(N'Projects', N'LibraryCategory') IS NOT NULL
BEGIN
    UPDATE Projects SET LibraryCategory = N'写实' WHERE LibraryCategory = N'真人';
    PRINT N'Projects.LibraryCategory：' + CAST(@@ROWCOUNT AS nvarchar(20)) + N' 行已由「真人」改为「写实」';
END
ELSE
    PRINT N'Projects.LibraryCategory 列不存在，已跳过（请先执行 Upgrade_项目资产库类型.sql）';
