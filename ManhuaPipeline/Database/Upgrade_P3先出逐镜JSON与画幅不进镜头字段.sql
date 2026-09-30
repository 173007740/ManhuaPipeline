-- =============================================================
-- P3 分镜：逐镜 JSON 前置 + 画幅不写进镜头字段（可重复执行）
--
-- 真跑之后发现的：
--   1. 逐镜 json 要求写在「产出末尾」。一次要出全部分集，回复撞上模型输出上限
--      被截断时，最先被切掉的就是末尾——json 块根本没写出来，
--      整批镜头入库 0 条（ImportError：没找到 ```json 块或可识别的镜号表格），
--      页面上看到的还是上一次的旧分镜，人以为是模型没改。
--      而且模型这回还换了排版（写成「- 景别：xxx」列表），表格兜底也认不出。
--   2. camera 字段被写成「9:16竖屏·正面0°·低机位贴地·定镜」。
--      画幅是立项锁定的全局参数，写进逐镜字段会一路带到 P4 投喂词里重复声明。
--
-- 修法：改的是规则数据（OutputContract），下次跑即生效。
--   · json 提到第一段，且不重抄详解——输出量减半，被截断也先保住结构化数据；
--   · camera 只写机位 / 角度 / 运动 / 焦段。
-- 已产出的分镜不回改（产出保留原文是审计原则），重跑一次即可。
-- =============================================================

DECLARE @rule NVARCHAR(MAX) = N'

【逐镜 JSON 是唯一权威：放第一段输出，且不重复写详解】
先输出完整的逐镜 json 块（```json ... ```），字段照抄：unitNumber / shotNumber / shotSize / camera / characters / duration / scene / description / dialogue / startState / singleAction / endState / nextConnection / forbiddenChanges / newInformation。
json 之后**不再**逐镜重复写一遍详解，只保留「## 本次假设」这类不超过 5 条的简短说明。
为什么这样要求：把 json 放在末尾时，回复一旦撞上模型输出上限被截断，最先丢的就是它——整批镜头一条都进不了库，页面上还停留在上一次的旧分镜。
【camera 只写机位 / 角度 / 运动 / 焦段】
画幅是立项锁定的全局参数，禁止写进逐镜字段（不要出现「9:16竖屏」「16:9横屏」这类字样）；逐镜 description 也控制在 60 字以内，别把输出撑爆。';

UPDATE DirectorSkillStages
SET OutputContract = OutputContract + @rule,
    UpdatedAt = SYSDATETIME()
WHERE PackId = 1 AND StageKey IN (N'P3', N'P4')
  AND OutputContract NOT LIKE N'%逐镜 JSON 是唯一权威%';
