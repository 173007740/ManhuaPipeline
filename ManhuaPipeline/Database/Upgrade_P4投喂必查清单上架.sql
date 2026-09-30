/* P4 投喂提示词：把专属的《投喂提示词生成 · 必查清单》挂进这一站。

   这份文档（DocId 39）自称「★权威（P4 投喂生成专用执行序列）」、
   「定义 /生成视频提示词 的唯一执行顺序，缺一不可，禁止只凭单一模板文件直接开写」，
   却是 Scope='ref'；而引擎只在阶段的 DocFilter 里写了 ref 才加载 ref 文档，
   全部 11 个阶段的 DocFilter 没有一个含 ref —— 它从上线到现在一次都没进过任何 prompt。

   这里改成 Scope='stage' / ScopeValue='P4'，而不是给 P4 的 DocFilter 加 ref：
   加 ref 会把 DocId 1（V6.8 全流程 14234 字）等四篇一起塞进来，
   P4 的 prompt 从 3.1 万字涨到 5.1 万字，那几篇是给人查的手册，不值这个价。 */

UPDATE DirectorSkillDocs
SET Scope = N'stage', ScopeValue = N'P4', SortOrder = 99
WHERE DocId = 39;

-- 确认一下这一站现在加载哪些规则
SELECT DocId, Title, Chars FROM DirectorSkillDocs
WHERE (Scope = N'always') OR (Scope = N'stage' AND ScopeValue = N'P4')
ORDER BY SortOrder;
