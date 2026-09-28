-- 图片模型多配置：一个用户可以配多个出图渠道（中转 / 方舟 / 302.ai …），并能指定默认那个。
--
-- 背景：原来 Provider='image' 只允许一条 —— 表上有唯一索引 (UserId, Provider)，
-- SaveLLMConfig 也按 UserId+Provider 做 upsert，取的时候是 SELECT TOP 1，
-- 所以想换渠道只能覆盖，没法并存，画布里也没得挑。
-- 现在允许多条并存：DisplayName 是配置页上显示的别名，SortOrder 决定列表顺序，
-- IsActive=1 的那条 = 默认出图模型（画布节点没单独指定时用它，资产库出图也用它）。
--
-- 执行方式：sqlcmd -S . -d ManhuaPipeline -U <user> -P <pwd> -C -i 本文件
--（-Q 单批次不支持 GO 分批，所以请用 -i）

-- ① 新列
IF COL_LENGTH('LLMConfigs', 'DisplayName') IS NULL
    ALTER TABLE LLMConfigs ADD DisplayName NVARCHAR(120) NULL;
GO

IF COL_LENGTH('LLMConfigs', 'SortOrder') IS NULL
    ALTER TABLE LLMConfigs ADD SortOrder INT NOT NULL CONSTRAINT DF_LLMConfigs_SortOrder DEFAULT 0;
GO

-- ② 去掉 (UserId, Provider) 的唯一索引：出图要能存多条，这个约束必须放开。
--
-- 这里刻意不换成「筛选索引（Provider <> 'image'）」：筛选索引会强制要求会话
-- QUOTED_IDENTIFIER=ON，sqlcmd（默认 OFF）和其它第三方工具连上来 INSERT 会直接报
-- Msg 1934。而唯一性本来就是 SaveLLMConfig 里 IF EXISTS 判断保证的，不靠索引兜底，
-- 犯不上为它冒这个险。查询性能有现成的非唯一索引 IX_LLMConfigs_User(UserId, Provider)。
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_LLMConfigs_User_Provider' AND object_id = OBJECT_ID('dbo.LLMConfigs'))
    DROP INDEX UX_LLMConfigs_User_Provider ON dbo.LLMConfigs;
GO

-- ③ 历史数据：原来唯一那条 image 补个名字，排在第一位
UPDATE LLMConfigs
SET DisplayName = N'文生图（中转）', SortOrder = 1
WHERE Provider = 'image' AND (DisplayName IS NULL OR DisplayName = N'');

-- 之前手动备份的方舟那条转正，直接成为第二个可选渠道（2K）
UPDATE LLMConfigs
SET Provider = 'image', DisplayName = N'方舟 seedream（2K）', SortOrder = 2, IsActive = 0
WHERE Provider = 'image_ark_bak';
GO

-- ④ 保险：一条默认都没有时，把排序最靠前的那条顶上
UPDATE LLMConfigs
SET IsActive = 1
WHERE ConfigId = (
    SELECT TOP 1 ConfigId FROM LLMConfigs
    WHERE Provider = 'image'
      AND NOT EXISTS (SELECT 1 FROM LLMConfigs x WHERE x.Provider = 'image' AND x.IsActive = 1)
    ORDER BY SortOrder, ConfigId
);
GO
