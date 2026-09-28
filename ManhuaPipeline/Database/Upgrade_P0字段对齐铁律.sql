-- =============================================================
-- P0 输入字段与前置铁律 A~E 对齐（真跑之后发现的，可重复执行）
--
-- 第一次真跑 P0，模型直接阻断：「前置铁律 A~E 未全部锁定」。
-- 但输入里模型、画幅、交付、题材、平台、集数、时长其实都给了，它却认字母标签不认内容。
-- 根因不是模型笨，是两处没对齐：
--   1. 我们在 Stages.InputsJson 里定义的字段（模型/画幅/…）跟规则文档里 A~E 的分类没有映射关系；
--   2. 输入字段漏了「画风方向」「核心爽点」，而铁律 D 要的正是这两项。
--
-- 修法两条，都在数据层，没碰一行 C#：
--   1. 补字段：artStyle、hook；
--   2. OutputContract 里写明映射：A=模型 B=画幅 C=交付 D=题材+画风+爽点 E=平台+集数+时长，
--      并规定「以中文标签填写即视为锁定」，避免模型照标签字母做判定。
--
-- 改完重跑，P0 正常产出 P0A 创作基准 + 前置锁定清单并放行 P1。
-- 这就是整套设计的兑现：行为不对，改的是规则数据，不是代码。
-- =============================================================

UPDATE DirectorSkillStages
SET InputsJson = N'[
 {"key":"model","label":"模型","type":"select","options":["Seedance 2.0","Seedance 2.5"],"required":true,"tip":"不支持默认值，必须二选一"},
 {"key":"aspect","label":"画幅","type":"select","options":["16:9 横屏","9:16 竖屏","21:9 超宽"],"required":true},
 {"key":"delivery","label":"交付形态","type":"select","options":["Markdown + 离线看板","纯 Markdown"],"required":true},
 {"key":"genre","label":"题材","type":"text","required":true},
 {"key":"artStyle","label":"画风方向","type":"text","required":true,"tip":"如 3D 国漫史诗风格；参考图须为动漫/CG/绘画，不得用真人脸"},
 {"key":"hook","label":"核心爽点","type":"textarea","required":true},
 {"key":"episodeCount","label":"总集数","type":"number","required":true},
 {"key":"episodeDuration","label":"单集时长（秒）","type":"number","required":true},
 {"key":"platform","label":"目标平台","type":"text","required":true},
 {"key":"premise","label":"核心设定","type":"textarea","required":true},
 {"key":"characters","label":"角色","type":"list","required":true,"tip":"逗号分隔"}
]'
WHERE PackId = 1 AND StageKey = N'P0';

-- 映射说明只追加一次：靠特征串判断，重复执行不会越加越长
UPDATE DirectorSkillStages
SET OutputContract = OutputContract + N'

【输入映射（必读，避免误判阻断）】
用户填写的字段与前置铁律 A~E 的对应关系：A=模型，B=画幅，C=交付形态，D=题材+画风方向+核心爽点，E=平台+总集数+单集时长。
用户以中文标签填写即视为对应项已锁定，不得因为没按 A/B/C/D/E 字母标签书写就判定未锁定。
只有真正缺失的项才阻断；能产出 P0A 创作基准时就直接产出，把存疑项单独列出。',
    UpdatedAt = SYSDATETIME()
WHERE PackId = 1 AND StageKey = N'P0'
  AND OutputContract NOT LIKE N'%输入映射（必读%';
