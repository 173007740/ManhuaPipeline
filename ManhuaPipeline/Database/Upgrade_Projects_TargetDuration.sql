-- Upgrade_Projects_TargetDuration.sql
-- 目的：Projects 表新增"目标总时长"列（Stage4 分集细化的全片时长预算，用户显式设置）
-- 用法：登录目标库执行一次即可；本脚本幂等（列已存在时跳过）。
IF COL_LENGTH(N'dbo.Projects', N'TargetDurationText') IS NULL
    ALTER TABLE dbo.Projects ADD TargetDurationText NVARCHAR(64) NULL;
