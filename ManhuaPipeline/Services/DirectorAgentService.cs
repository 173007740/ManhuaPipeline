using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// 导演 Skill 的执行引擎。
///
/// 这是整套设计里最关键的一层，它刻意「不懂业务」：
/// 不认识仙侠、不认识 4 View、不认识七段式，也不知道一共几个阶段。
/// 它只认四件事——取规则、拼 prompt、调 LLM、存结果。
/// 规则说什么（DirectorSkillDocs）、跑哪几步、每步要什么产出什么（DirectorSkillStages），全在库里。
/// 所以 flow 变了改数据，引擎一行不用动。
///
/// 两条硬约束：
///   1. 门禁靠数据（Stages.HumanConfirm），引擎不许自己判断「这一步该不该放行」；
///   2. 产出原样落库（OutputText 存 LLM 原文），加工归上层，方便事后复盘和复现。
/// </summary>
public class DirectorAgentService
{
    private readonly DbService _db;
    private readonly LLMService _llm;
    private readonly SkillOutputImporter _importer;
    private readonly ILogger<DirectorAgentService> _log;

    public DirectorAgentService(DbService db, LLMService llm, SkillOutputImporter importer,
                                ILogger<DirectorAgentService> log)
    {
        _db = db; _llm = llm; _importer = importer; _log = log;
    }

    // ========== 1. 拼 prompt ==========

    /// <summary>按 DocFilter 挑出本阶段要加载的规则。表达式形如 "always,stage:P2,when:战斗"。</summary>
    private static bool MatchFilter(string? filter, DbService.SkillDocFull d)
    {
        if (string.IsNullOrWhiteSpace(filter)) return d.Scope == "always";
        foreach (var raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("always", StringComparison.OrdinalIgnoreCase) && d.Scope == "always") return true;
            if (raw.Equals("ref", StringComparison.OrdinalIgnoreCase) && d.Scope == "ref") return true;

            var i = raw.IndexOf(':');
            if (i <= 0) continue;
            var key = raw[..i];
            var val = raw[(i + 1)..];
            if (key.Equals("stage", StringComparison.OrdinalIgnoreCase) && d.Scope == "stage" && d.ScopeValue == val) return true;
            if (key.Equals("when", StringComparison.OrdinalIgnoreCase) && d.Scope == "when" && d.ScopeValue == val) return true;
        }
        return false;
    }

    public sealed record PromptBuild(string SystemPrompt, string DocsSummary, int Chars, List<(string Title, int Chars)> Docs);

    /// <summary>拼出本阶段的 System prompt。返回时也带上文档清单，供预览和审计用。</summary>
    public PromptBuild BuildPrompt(int packId, DbService.SkillStageRow stage,
                                   List<DbService.SkillDocFull>? docsCache = null)
    {
        var docs = docsCache ?? _db.GetSkillDocsWithContent(packId);
        var picked = docs.Where(d => MatchFilter(stage.DocFilter, d)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 角色");
        sb.AppendLine("你是「AI 分镜导演」。严格按下面的规则工作，规则里没写的动作一律不许自创。");
        sb.AppendLine();
        sb.AppendLine($"# 当前阶段：{stage.StageKey} · {stage.Name}");
        if (!string.IsNullOrWhiteSpace(stage.OutputContract))
        {
            sb.AppendLine();
            sb.AppendLine("## 本阶段产出要求");
            sb.AppendLine(stage.OutputContract);
        }
        if (!string.IsNullOrWhiteSpace(stage.Gates))
        {
            sb.AppendLine();
            sb.AppendLine("## 本阶段质检清单（动笔前逐条自查，并逐条落到产出里）");
            sb.AppendLine(stage.Gates);
            // 以前这里写的是「门禁不过就第一行写 BLOCKED: 然后什么也别产出」。
            // 那条规矩本身就是这次卡死的源头：流水线是自动串下来的，中途没有一个人在听，
            // 模型却被告知可以停下来提问 —— 于是每一步都可能礼貌地拒绝一次，整部剧停在那儿等人。
            // 清单改回它本来的用途：约束产出质量，而不是给模型一个拒绝任务的合法理由
            sb.AppendLine();
            sb.AppendLine("【这条流水线上没有人在中途回答你】它由系统自动串联，你的产出会原样喂给下一个阶段。");
            sb.AppendLine("所以上面这份清单是约束你产出质量的，不是让你向用户提问的。发现某一项没给足依据时：");
            sb.AppendLine("不许写 BLOCKED，不许写「请补充 / 请确认 / 不满足 / 无法产出」——");
            sb.AppendLine("按行业惯例取最合理的那个值继续往下做，并在产出末尾用「## 本次假设」");
            sb.AppendLine("列出你替用户做出的判断（不超过 5 条），让他事后一眼看得到、也改得动。");
            // 怎么算「已锁定」说死。之前模型把明明填了值的项判成未锁定，于是整条线被一个误判锁住
            sb.AppendLine("【怎么判定有没有锁定】输入区中出现过的每一项取值，都是用户在表单里逐项选定的，");
            sb.AppendLine("一律视为已锁定。判定某项「未锁定」前，必须先在输入区里找它的值——找不到才是真缺失；");
            sb.AppendLine("不得以「表述不够明确」「需要用户再确认一次」「请用 A/B/C 标签回答」为由判缺失。");
        }
        // 产出要入库的阶段，顺便要求 LLM 附一个 json 块：解析就不用猜格式，这是可靠与能跑的分界线
        var schema = SkillOutputImporter.OutputSchemaFor(stage.OutputTarget);
        if (schema != null)
        {
            sb.AppendLine();
            sb.AppendLine("## 结构化输出（除正文外必须附）");
            sb.AppendLine(schema);
        }
        sb.AppendLine();
        sb.AppendLine("# 行业规则（skills 库）");
        foreach (var d in picked)
        {
            sb.AppendLine();
            sb.AppendLine($"## {d.Title}");
            sb.AppendLine(d.Content);
        }

        var summary = string.Join("\n", picked.Select(d => $"[{d.DocId}] {d.Title} · {d.Content.Length}字"));
        var sys = sb.ToString();
        return new PromptBuild(sys, summary, sys.Length, picked.Select(d => (d.Title, d.Content.Length)).ToList());
    }

    /// <summary>
    /// token 粗估。中文按 1 字≈1 token、西文按 1 词≈1.3 token 折算，只用于「要不要跑」的预判，
    /// 不作为计费依据。
    /// </summary>
    public static int EstimateTokens(string text)
    {
        int cjk = 0, asciiWords = 0;
        bool inWord = false;
        foreach (var ch in text)
        {
            if (ch > 0x2E80) { cjk++; inWord = false; }
            else if (char.IsWhiteSpace(ch)) { if (inWord) { asciiWords++; inWord = false; } }
            else inWord = true;
        }
        if (inWord) asciiWords++;
        return cjk + (int)(asciiWords * 1.3);
    }

    // ========== 2. 预览（跑之前的确认，防止一点就烧掉一堆额度） ==========

    /// <summary>
    /// 一步的「连带代价」：本步重跑要多少、下游有几个阶段得跟着重跑。
    /// 存在的理由是门禁不能只问「过不过」，得说清「错了要赔多少」——没有代价的确认等于形式主义。
    /// </summary>
    public sealed record StepCost(int PromptTokens, int RerunTokens, int DownstreamCount, int DownstreamTokens);

    /// <summary>算本步 + 下游的 token 代价。下游按各阶段自己的 prompt 长度累加，只拼一次文档避免反复查库。</summary>
    public StepCost CostOf(int packId, DbService.SkillStageRow stage, List<DbService.SkillDocFull>? docsCache = null)
    {
        var docs = docsCache ?? _db.GetSkillDocsWithContent(packId);
        var self = EstimateTokens(BuildPrompt(packId, stage, docs).SystemPrompt);
        var downstream = _db.GetSkillStages(packId)
                            .Where(s => s.IsEnabled && s.SortOrder > stage.SortOrder)
                            .ToList();
        int ds = 0;
        foreach (var d in downstream) ds += EstimateTokens(BuildPrompt(packId, d, docs).SystemPrompt);
        // 重跑 = 本步 prompt 再来一遍 + 输出量按 prompt 的一半粗算
        return new StepCost(self, self + self / 2, downstream.Count, ds);
    }

    public sealed record PreviewResult(int PromptChars, int EstTokens, List<(string Title, int Chars)> Docs,
                                        bool HumanConfirm, string? Gates, string? InputsJson, StepCost Cost);

    public PreviewResult Preview(int packId, string stageKey, DbService.SkillStageRow? stage = null)
    {
        stage ??= _db.GetSkillStages(packId).FirstOrDefault(s => s.StageKey == stageKey)
                  ?? throw new InvalidOperationException($"阶段不存在：{stageKey}");
        var docs = _db.GetSkillDocsWithContent(packId);
        var build = BuildPrompt(packId, stage, docs);
        return new PreviewResult(build.Chars, EstimateTokens(build.SystemPrompt), build.Docs,
                                 stage.HumanConfirm, stage.Gates, stage.InputsJson,
                                 CostOf(packId, stage, docs));
    }

    // ========== 3. 真正跑一步 ==========

    public sealed record StepResult(int StepId, string StageKey, string Status, string? Output, int PromptChars,
                                    int EstTokens, string? Gates, StepCost Cost, int Imported, string? ImportError);

    public async Task<StepResult> RunStepAsync(int userId, int runId, string stageKey, string inputText,
                                               int? projectId = null, int? episodeId = null)
    {
        // 落点以本次请求为准：页面上换了项目/剧集立刻生效，不用重开一次运行
        if (projectId > 0 || episodeId > 0)
            _db.UpdateSkillRunTarget(runId, projectId > 0 ? projectId : null, episodeId > 0 ? episodeId : null);

        var packId = _db.GetRunPackId(runId);
        var stage = _db.GetSkillStages(packId).FirstOrDefault(s => s.StageKey == stageKey)
                    ?? throw new InvalidOperationException($"阶段不存在：{stageKey}");
        if (!stage.IsEnabled) throw new InvalidOperationException($"阶段已停用：{stageKey}");

        var r = await ExecuteStepAsync(userId, runId, packId, stage, inputText);
        _db.UpdateSkillRun(runId, stageKey, stage.HumanConfirm ? "await_confirm" : "running");
        return r;
    }

    // ========== 3b. 一路往下跑：上一步产出自动喂给下一步 ==========

    /// <summary>
    /// 从某阶段起连着跑，遇到人工确认阶段就停在那儿等人。
    /// 用户只该在「错了代价大」的地方被打断，其余阶段不该来回问——
    /// 手动把上一步产出复制粘贴到下一步，是最没价值的那种操作。
    /// </summary>
    public async Task RunFromAsync(int userId, int runId, string startStageKey, string firstInput,
                                   int? projectId = null, int? episodeId = null, string? stopStageKey = null)
    {
        // 落点以本次请求为准：页面上换了项目/剧集立刻生效，不用重开一次运行
        if (projectId > 0 || episodeId > 0)
            _db.UpdateSkillRunTarget(runId, projectId > 0 ? projectId : null, episodeId > 0 ? episodeId : null);

        var packId = _db.GetRunPackId(runId);
        var stages = _db.GetSkillStages(packId).Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToList();
        var idx = stages.FindIndex(s => s.StageKey == startStageKey);
        if (idx < 0) throw new InvalidOperationException($"阶段不存在：{startStageKey}");

        var input = firstInput;

        // 点了某一步的「生成」却没带素材进来 —— 这一步页面上没有表单（资产/分镜/提示词都靠上游），
        // 人自然没什么可填。这里自动把该给的素材接上（多数时候是上一个阶段的产出，
        // P2c 三批则是资产台账），否则 LLM 会拿着一份空输入开工，对着空气提取实体
        if (string.IsNullOrWhiteSpace(input))
            input = ChainInputFor(runId, stages, idx);

        for (int i = idx; i < stages.Count; i++)
        {
            var st = stages[i];
            _db.UpdateSkillRun(runId, st.StageKey, "running");
            StepResult r;
            try { r = await ExecuteStepAsync(userId, runId, packId, st, input); }
            catch { _db.UpdateSkillRun(runId, st.StageKey, "error"); throw; }

            // 门禁没过就到此为止。继续往下喂只会被下游当成「上一步的素材」，
            // 然后每个阶段都礼貌地拒绝一遍——跑完一片空白，还查不出是哪一步开始歪的
            if (r.Status == "blocked")
            {
                _db.UpdateSkillRun(runId, st.StageKey, "blocked");
                return;
            }
            if (st.HumanConfirm)
            {
                _db.UpdateSkillRun(runId, st.StageKey, "await_confirm");
                return;
            }
            // 跑到这一步就收手。五步流程里每一步都是人单独点的，
            // 越界替他把下一步也跑掉，等于把「我在这一站要不要下车」的决定权抢了
            if (!string.IsNullOrWhiteSpace(stopStageKey)
                && string.Equals(st.StageKey, stopStageKey, StringComparison.OrdinalIgnoreCase))
            {
                _db.UpdateSkillRun(runId, st.StageKey, "step_done");
                return;
            }
            input = ChainHead(st.StageKey) + (r.Output ?? "");   // 自动串联：上一步的产出就是下一步的素材
            // 下一站要是 P2c 的某一批，它要的是台账，不是这一批的提示词
            if (i + 1 < stages.Count)
            {
                var src = ChainSourceOf(stages[i + 1].StageKey);
                var ledger = src != null ? LastOutputOf(runId, src) : "";
                if (!string.IsNullOrWhiteSpace(ledger)) input = ChainHead(src!) + ledger;
            }
        }
        _db.UpdateSkillRun(runId, stages[^1].StageKey, "done");
    }

    /// <summary>
    /// 确认卡点，然后接着往下跑到下一个卡点（或跑到 stopStageKey 为止）。
    /// 五步界面里每一步都是人单独点的，「接着跑」只该把这一步跑完，
    /// 不该顺手替他把后面几步也做了——所以允许调用方给个终点。
    /// </summary>
    public async Task ContinueAsync(int userId, int runId, string? stopStageKey = null)
    {
        var steps = _db.GetSkillSteps(runId);
        var last = steps.OrderByDescending(s => s.SortOrder).FirstOrDefault()
                   ?? throw new InvalidOperationException("这次运行还没跑过任何阶段");

        // 停在门禁阻断：这一步根本没产出，OutputText 里躺的是一句拒绝通知。
        // 「放行」是人说这一关可以过，那就用这一步原本的素材重跑它本身。
        // 跳过去会让这一阶段的产出永久缺失；拿那句拒绝当素材喂给下游，
        // 则是让每个下游礼貌地再拒绝一遍 —— 跑完一片空白，还查不出源头
        if (last.Status == "blocked")
        {
            _db.ConfirmSkillStep(last.StepId);
            await RunFromAsync(userId, runId, last.StageKey, last.InputText ?? "", stopStageKey: stopStageKey);
            return;
        }

        if (last.Status == "await_confirm") _db.ConfirmSkillStep(last.StepId);

        var packId = _db.GetRunPackId(runId);
        var stages = _db.GetSkillStages(packId).Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToList();
        var idx = stages.FindIndex(s => s.StageKey == last.StageKey) + 1;
        if (idx >= stages.Count)
        {
            _db.UpdateSkillRun(runId, last.StageKey, "done");
            return;
        }
        /* 下一站是 P2c 的某一批时，素材得换成台账：这批的出图依据是 P2b 的资产清单，
           不是上一批的提示词产出。拿错了模型只能照剧本编名字，回填时一条也对不上 */
        var nextKey = stages[idx].StageKey;
        var src = ChainSourceOf(nextKey);
        var ledger = src != null ? LastOutputOf(runId, src) : "";
        await RunFromAsync(userId, runId, nextKey,
                           !string.IsNullOrWhiteSpace(ledger) ? ChainHead(src!) + ledger
                                                              : ChainHead(last.StageKey) + (last.OutputText ?? ""),
                           stopStageKey: stopStageKey);
    }

    /// <summary>
    /// 串联时必须交代的一句话：下面这段是上游产出的**成品**，不是待办的表格。
    /// 人工卡点阶段写出来的就是一张写着「请逐项回复」的清单，取消卡点之前靠人点确认把状态记进 ConfirmedAt；
    /// 现在半路不再有任何人点头了，这句话得无条件带上——
    /// 否则下游读到那张空着的表就会判「前置条件未满足」，整个流水线停在第二次这样的误判上。
    /// </summary>
    private static string ChainHead(string prevStageKey)
        => $"【流水线状态·引擎注入】下面引用的是上游 {prevStageKey} 的最终产出。"
         + "其中「请逐项回复」「待确认」「是否修改」这类措辞，只是阶段产出模板的残留说法，"
         + "不代表还有任何事等着用户回答——这条流水线上的前置步骤均已由系统放行，请你照常接着做。\n\n";

    /// <summary>取某阶段最近一次有产出的内容。多次重跑时以最新的那次为准。</summary>
    private string LastOutputOf(int runId, string stageKey)
        => _db.GetSkillSteps(runId)
              .Where(s => s.StageKey == stageKey && s.Status is "done" or "await_confirm")
              .OrderByDescending(s => s.StepId)
              .Select(s => s.OutputText)
              .FirstOrDefault() ?? "";

    /// <summary>
    /// 某些阶段的上家不是它前面那一个，而是更早那一站的定稿。
    /// 资产台账（P2b）自己写着「本清单为 P2c 三批次出图的唯一依据」，
    /// 所以第二批（场景）、第三批（道具与特效）都得回头拿台账。
    /// 接上一批的提示词产出会怎样：模型手头没有资产清单，就照着剧本自己编场景名，
    /// 写出来的是「SCN-城市出租屋」这种台账上没有的东西，回填时对不上，入库 0 条。
    /// </summary>
    private static string? ChainSourceOf(string stageKey)
        => stageKey.StartsWith("P2c", StringComparison.OrdinalIgnoreCase) ? "P2b" : null;

    /// <summary>给第 idx 个阶段找素材：默认接上一个阶段的产出，P2c 三批则回头拿台账。</summary>
    private string ChainInputFor(int runId, List<DbService.SkillStageRow> stages, int idx)
    {
        if (idx <= 0 || idx >= stages.Count) return "";
        var src = ChainSourceOf(stages[idx].StageKey);
        if (src != null)
        {
            var ledger = LastOutputOf(runId, src);
            if (!string.IsNullOrWhiteSpace(ledger)) return ChainHead(src) + ledger;
        }
        return ChainHead(stages[idx - 1].StageKey) + LastOutputOf(runId, stages[idx - 1].StageKey);
    }

    // ========== 3c. 单步执行的核心：单跑和连跑都走这里 ==========

    private async Task<StepResult> ExecuteStepAsync(int userId, int runId, int packId,
                                                    DbService.SkillStageRow stage, string inputText)
    {
        var provider = _db.GetActiveLLMProvider(userId);
        var cfg = _db.GetActiveConfig(userId, string.IsNullOrWhiteSpace(provider) ? "deepseek" : provider)
                  ?? throw new InvalidOperationException("没有可用的 LLM 配置，先去系统配置里配一个");

        // 运行级参数注入：页面顶部选的提示词类型要跟着这次运行走到 P4——
        // P4 是自动串下来的，不会停下来让人再选一次，所以类型得由这次运行带着走。
        if (!string.IsNullOrWhiteSpace(stage.InputsJson) && stage.InputsJson!.Contains("promptType")
            && !inputText.Contains("提示词类型"))
        {
            var runIn = _db.GetRunInputsJson(runId);
            if (!string.IsNullOrWhiteSpace(runIn))
            {
                var m = Regex.Match(runIn!, "\"promptType\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) inputText += "\n提示词类型：" + m.Groups[1].Value;
            }
        }

        var build = BuildPrompt(packId, stage);
        var cost = CostOf(packId, stage);
        int stepId = _db.CreateSkillStep(runId, stage.StageKey, stage.Name, stage.SortOrder,
                                         inputText, build.DocsSummary, build.Chars, stage.Gates);

        var sys = build.SystemPrompt + "\n\n# 输入（用户提供的素材）\n" + inputText;
        try
        {
            var raw = await _llm.CallAsync(cfg.ApiUrl ?? "", cfg.ApiKey, cfg.ModelName ?? "", sys,
                                           "请严格按「本阶段产出要求」输出，不要输出多余解释。",
                                           jsonMode: false, temperature: 0.7, thinkingMode: cfg.ThinkingMode);

            if (LooksBlocked(raw))
            {
                // 契约已经改成「自查 + 自行取合理值」，模型还是吐出一句拒绝，
                // 多半是没反应过来这条线上没有人在中途答话。先给它一次机会：
                // 把它的拒绝原话塞回去，明说这条不成立，要求立刻重做。
                // 直接把这个问题甩给正在等结果的人，是这套系统最招人烦的动作
                var push = "# 补充指令\n你上一次的回复是一句拒绝（原文附后），在本任务中不成立。\n"
                         + "这条流水线由系统自动串联，中途没有任何人会回答你的提问，停下来提问等于让整部剧停在这里。\n"
                         + "现在按「本阶段产出要求」直接输出完整内容：缺依据的项按行业惯例自行取值，\n"
                         + "末尾用「## 本次假设」列出你替用户做出的判断（不超过 5 条）。\n"
                         + "不得再出现 BLOCKED、请补充、请确认、无法产出这类拒绝性表述。\n\n"
                         + "---- 你上一次的回复 ----\n" + raw;
                try
                {
                    raw = await _llm.CallAsync(cfg.ApiUrl ?? "", cfg.ApiKey, cfg.ModelName ?? "", sys, push,
                                               jsonMode: false, temperature: 0.7, thinkingMode: cfg.ThinkingMode);
                }
                catch
                {
                    // 重试这一发自己炸了就照原样往下判，下面的 blocked 分支会接住
                }
            }

            // 走到这一步还没产出：第二次仍然是一句拒绝。这是 LLM 在要东西，不是产出，
            // 不能标 done、不能往下串、更不能入库——以前一句「请回答」被当成剧本传了三步
            if (LooksBlocked(raw))
            {
                _db.FinishSkillStep(stepId, "blocked", raw, null, "这一步没能产出内容，已自动重试过一次");
                return new StepResult(stepId, stage.StageKey, "blocked", raw, build.Chars,
                                      EstimateTokens(sys), stage.Gates, cost, 0, null);
            }

            var json = TryExtractJson(raw);
            var status = stage.HumanConfirm ? "await_confirm" : "done";

            // 入库放在落原始产出之后：原文先存住，解析失败也不至于丢东西，事后还能回灌
            int imported = 0;
            string? importError = null;
            if (!string.IsNullOrWhiteSpace(stage.OutputTarget))
            {
                var ctx = _db.GetRunContext(runId);
                var res = _importer.Import(stage.OutputTarget!, json, raw, ctx.ProjectId, ctx.EpisodeId,
                                           inputText, runId, stepId, stage.StageKey);
                imported = res.Count;
                importError = res.Error;
            }

            _db.FinishSkillStep(stepId, status, raw, json, null);
            _db.SetStepImport(stepId, imported, importError);
            return new StepResult(stepId, stage.StageKey, status, raw, build.Chars,
                                  EstimateTokens(sys), stage.Gates, cost, imported, importError);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "skill step failed run={RunId} stage={Stage}", runId, stage.StageKey);
            _db.FinishSkillStep(stepId, "error", null, null, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 门禁没过时 LLM 会写 BLOCKED: 开头（prompt 里硬要求的）。
    /// 兜底再认几个它写惯了的说法——万一模型不听话，也不能把拒绝通知当成品往下传。
    /// </summary>
    private static bool LooksBlocked(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.TrimStart(' ', '\t', '\n', '\r', '#', '*', '`', '「', '"');
        if (t.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("⛔")) return true;
        // 老格式的自由发挥：短文本 + 明确的拒绝措辞。真正的产出不会只有几百字
        return t.Length < 1500
            && Regex.IsMatch(t, @"(任务不可启动|不可启动|禁止进入|无法产出|❌\s*未锁定)");
    }

    /// <summary>人工确认放行。引擎自己不判断「能不能过」，只有人点了才算过。</summary>
    public void ConfirmStep(int stepId)
    {
        _db.ConfirmSkillStep(stepId);
    }

    /// <summary>LLM 返回里如果带 JSON 块就抽出来，抽不到返回 null（产出仍是有效的 md，不强行结构化）。</summary>
    private static string? TryExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (s < 0) return null;
        s += 7;
        var e = raw.IndexOf("```", s, StringComparison.Ordinal);
        if (e < 0) return null;
        var body = raw[s..e].Trim();
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.GetRawText(); }
        catch { return null; }
    }
}
