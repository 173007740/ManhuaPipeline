-- 立项（P0）表单的选项值，跟项目管理页那份对齐。
--
-- 两处入口写同一张表（Dramas）的同一批列，可选项的「值」不一样：
--   1. 提示词引擎：skill-studio 的 P0 表单存的是整句显示文本「H3（MiniMax·本地 ComfyUI）」，
--      项目管理页存的是短值「H3」，#promptType 下拉用的也是短值。
--      库里现在是「H3」（项目管理页存的），skill-studio 回填时 .val("H3") 选不中任何一个 option，
--      下拉落回第一项 SD —— 而且人一保存就把库里的 H3 覆盖成长串，项目管理页那边又选不中了。
--      两边来回覆盖，谁也绑不上。
--   2. 交付形态：skill-studio 只有 2 项且第三项叫「纯 Markdown」，
--      项目管理页是「只要 Markdown 文档 / 只要离线看板」。库值落到另一边就显示不出来。
--
-- 改法：P0 的 InputsJson 里这两个字段的 options 改成与项目管理页一致的取值
-- （引擎用短值 SD / H3，含义写在 tip 里；交付形态补齐三项）。

UPDATE DirectorSkillStages
SET InputsJson = REPLACE(InputsJson,
    N'"options":["SD（Seedance·火山方舟）","H3（MiniMax·本地 ComfyUI）"]',
    N'"options":["SD","H3"]')
WHERE PackId = 1 AND StageKey = N'P0';

UPDATE DirectorSkillStages
SET InputsJson = REPLACE(InputsJson,
    N'"options":["Markdown + 离线看板","纯 Markdown"]',
    N'"options":["Markdown + 离线看板","只要 Markdown 文档","只要离线看板"]')
WHERE PackId = 1 AND StageKey = N'P0';

SELECT StageKey,
       PATINDEX('%"SD","H3"%', InputsJson)      AS 引擎短值位置,
       PATINDEX('%只要离线看板%', InputsJson)    AS 交付三项位置,
       LEN(InputsJson)                          AS 长度
FROM DirectorSkillStages WHERE PackId = 1 AND StageKey = N'P0';
