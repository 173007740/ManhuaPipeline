-- =============================================================
-- P2c 由「一条阶段」拆成「三条批次阶段」（可重复执行）
--
-- 原来 P2c 是一条，DocFilter 写死 always,stage:P2,when:战斗，
-- 于是出「角色 4 View」这种跟战斗毫无关系的批次，也要把 10 份战斗规则一起塞进 prompt。
-- 实测第一批 46,475 prompt token，其中大半是战斗规则——纯粹白烧。
--
-- 拆法：一条阶段 = 一批，各带自己的 DocFilter 与产出契约。
-- 角色批不带 when:战斗（省掉 10 份）；场景批要出「战斗站位版」、道具特效批要出「特效状态图」，保留。
-- 这样还顺带贴合了规则本身的要求——分批逐批确认，每批一次门禁。
-- =============================================================

-- 原 P2c 改为「第一批 角色 4 View」，并去掉战斗规则
UPDATE DirectorSkillStages
SET StageKey = 'P2c1',
    Name = N'分批出图提示词 · 第一批 角色 4 View',
    DocFilter = N'always,stage:P2',
    OutputContract = N'默认只出提示词，不直接出图。
本批只处理 A/B 级角色，逐个输出：
1. 中文正式提示词
2. English Formal Prompt
3. 负面约束

4 View 严格版式：16:9 白底；左区为全图唯一带头面部特写；右区为无头正面 / 无头 90° 侧面 / 无头背面三联全身，不呈现断颈，以衣领自然收束。
每个角色必须锁定：身高与头身比、五官 10 项、服装材质 6 项、性别锚点（年龄+男/女+身份代称）。
有亲缘关系的角色写明继承比例（如 40%~60%）与共享特征，并注明不与群众图混用。
负面约束需逐项针对该角色（禁止异性化骨相、禁止断颈、禁真人脸、禁塑料反光服等）。',
    UpdatedAt = SYSDATETIME()
WHERE PackId = 1 AND StageKey = N'P2c';

-- 第二批：场景（需要战斗站位规则）
IF NOT EXISTS (SELECT 1 FROM DirectorSkillStages WHERE PackId = 1 AND StageKey = N'P2c2')
    INSERT INTO DirectorSkillStages(PackId, StageKey, Name, SortOrder, DocFilter, InputsJson, OutputContract, Gates, HumanConfirm)
    VALUES (1, N'P2c2', N'分批出图提示词 · 第二批 场景', 51, N'always,stage:P2,when:战斗',
     N'[]',
     N'默认只出提示词，不直接出图。
本批处理场景资产，逐个输出中文正式提示词 + English Formal Prompt + 负面约束。
每个场景出：场景母版（布局/材质/光源/机位占位）+ 场景拼接图 + 机位/站位版。
战斗场景必须给「战斗站位版」：标出对决双方左右站位、动作轴、安全机位区。
状态变体场景（如封印台·裂界状态）作为附加页输出，不重复出母版。
场景资产图统一 16:9 横版，与成片画幅解耦。',
     N'【硬门禁 2】分批逐批确认，禁止把"开始 P2 出图"当默认动作；只有用户明确逐次下出图口令才真的出图。', 1);

-- 第三批：道具与特效（需要战斗特效规则）
IF NOT EXISTS (SELECT 1 FROM DirectorSkillStages WHERE PackId = 1 AND StageKey = N'P2c3')
    INSERT INTO DirectorSkillStages(PackId, StageKey, Name, SortOrder, DocFilter, InputsJson, OutputContract, Gates, HumanConfirm)
    VALUES (1, N'P2c3', N'分批出图提示词 · 第三批 道具与特效', 52, N'always,stage:P2,when:战斗',
     N'[]',
     N'默认只出提示词，不直接出图。
本批处理道具与特效资产，逐个输出中文正式提示词 + English Formal Prompt + 负面约束。
道具出道具资产图（结构/材质/关键特征/战损状态）。
按确认结果采用合并模式：特效不单独建资产，并入对应 CHR/SCN/PRP 状态图，
输出时标明「XX · YY 状态图」及其宿主资产，例如「PRP-守印符环 · 裂纹状态图」。
只出静态结构图的道具（如天道锁链）注明动态效果走 VFX，不在本批出动态图。',
     N'【硬门禁 2】分批逐批确认，禁止把"开始 P2 出图"当默认动作；只有用户明确逐次下出图口令才真的出图。', 1);
