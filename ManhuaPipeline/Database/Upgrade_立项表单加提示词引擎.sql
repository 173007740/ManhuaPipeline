-- =============================================================
-- 立项表单加「提示词引擎」字段
--
-- 它以前是左侧栏那个运行级下拉，值存在 DirectorSkillRuns.InputsJson 里，
-- P4 靠正则从那段 JSON 里抠出来。现在它是立项的一个字段：
-- 填进 ProjectBriefs.PromptEngine，P4 直接读列，改一次不用重开运行。
-- =============================================================

IF EXISTS (SELECT 1 FROM DirectorSkillStages
           WHERE PackId = 1 AND StageKey = N'P0' AND InputsJson NOT LIKE N'%promptEngine%')
BEGIN
    UPDATE DirectorSkillStages
    SET InputsJson = STUFF(InputsJson, LEN(InputsJson), 1,
        N',{"key":"promptEngine","label":"提示词引擎","type":"select",'
        + N'"options":["SD（Seedance·火山方舟）","H3（MiniMax·本地 ComfyUI）"],'
        + N'"default":"SD（Seedance·火山方舟）","required":true,'
        + N'"tip":"P4 生成投喂提示词时按这个走。它存进立项表，改这里不用重跑流水线"}')
    WHERE PackId = 1 AND StageKey = N'P0';
END
GO

SELECT StageKey, InputsJson FROM DirectorSkillStages WHERE PackId = 1 AND StageKey = N'P0';
GO
