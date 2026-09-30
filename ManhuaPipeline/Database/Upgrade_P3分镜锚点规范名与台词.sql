-- =============================================================
-- P3 分镜：锚点必须取台账规范名 + 每镜必带 scene / dialogue
--
-- 真跑之后发现的（可重复执行）：
--   1. 角色被写成简称：资产台账里是「杨彦刚」，分镜里写成 @CHR-彦-正面。
--      简称还能靠匹配器的反向兜底救回来，但正文、提示词、图册里到处是「彦」，
--      跟资产库对不上，人也没法核对。
--   2. 场景名自造：台账只有 10 个场景，分镜却写出 @SCN-老屋院落、@SCN-染坊作坊、
--      @SCN-后山小径、@SCN-外婆卧室，甚至 @SCN-城市车站（台账上根本没有）。
--      绑定是「包含台账规范名」才算命中，一个都对不上——10 张场景母版图
--      一张也进不了镜头参考图槽，等于白出。
--   3. 场景字段整体缺失：场景写在 md 表格上方的「**场景**：@SCN-xxx」里，
--      逐镜 JSON 不带 scene 键，落库后 52/52 帧的 Scene 全是 NULL。
--   4. 台词整体缺失：52/52 帧 Dialogue 为 NULL，投喂阶段没有口型与时长线索。
--
-- 修法：在 P3 / P4 的 OutputContract 补锚点规范与必填字段。改的是规则数据，
-- 下次跑即生效；已产生的分镜不回改（产出保留原文是审计原则），重跑一次即可。
-- =============================================================

DECLARE @rule NVARCHAR(MAX) = N'

【锚点一律取台账规范名（禁止自造、禁止简称）】
@CHR- 用角色台账全名（@CHR-杨彦刚，不得简写成 @CHR-彦）；@SCN- 用场景台账名，只能是台账列出的那几个，禁止自造台账上没有的场景；@PRP- 用道具台账名。
剧本里用了简称或别名，锚点里也必须还原成规范全名——简称与自造名会让资产绑定落空，母版图进不了参考图槽，跨镜头外观就没人兜底了。
【每镜必带 scene 与 dialogue】
scene 写本镜所属场景锚点（@SCN-规范名）；dialogue 写本镜台词，无台词写「无」。
缺 scene，场景母版图无法作为参考图进入本镜；缺 dialogue，投喂词没有口型与时长线索。
【逐镜 JSON 字段】unitNumber / shotNumber / shotSize / camera / characters / duration / scene / description / dialogue / startState / singleAction / endState / nextConnection / forbiddenChanges / newInformation。';

UPDATE DirectorSkillStages
SET OutputContract = OutputContract + @rule,
    UpdatedAt = SYSDATETIME()
WHERE PackId = 1 AND StageKey IN (N'P3', N'P4')
  AND OutputContract NOT LIKE N'%锚点一律取台账规范名%';
