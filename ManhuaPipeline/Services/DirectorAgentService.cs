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
            sb.AppendLine("## 本阶段门禁（不满足就不要产出，直接指出卡在哪）");
            sb.AppendLine(stage.Gates);
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
                                   int? projectId = null, int? episodeId = null)
    {
        // 落点以本次请求为准：页面上换了项目/剧集立刻生效，不用重开一次运行
        if (projectId > 0 || episodeId > 0)
            _db.UpdateSkillRunTarget(runId, projectId > 0 ? projectId : null, episodeId > 0 ? episodeId : null);

        var packId = _db.GetRunPackId(runId);
        var stages = _db.GetSkillStages(packId).Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToList();
        var idx = stages.FindIndex(s => s.StageKey == startStageKey);
        if (idx < 0) throw new InvalidOperationException($"阶段不存在：{startStageKey}");

        var input = firstInput;
        for (int i = idx; i < stages.Count; i++)
        {
            var st = stages[i];
            _db.UpdateSkillRun(runId, st.StageKey, "running");
            StepResult r;
            try { r = await ExecuteStepAsync(userId, runId, packId, st, input); }
            catch { _db.UpdateSkillRun(runId, st.StageKey, "error"); throw; }

            if (st.HumanConfirm)
            {
                _db.UpdateSkillRun(runId, st.StageKey, "await_confirm");
                return;
            }
            input = r.Output ?? "";   // 自动串联：上一步的产出就是下一步的素材
        }
        _db.UpdateSkillRun(runId, stages[^1].StageKey, "done");
    }

    /// <summary>确认卡点，然后接着往下跑到下一个卡点（或跑完）。</summary>
    public async Task ContinueAsync(int userId, int runId)
    {
        var steps = _db.GetSkillSteps(runId);
        var pending = steps.Where(s => s.Status == "await_confirm").OrderBy(s => s.SortOrder).FirstOrDefault();
        if (pending != null) _db.ConfirmSkillStep(pending.StepId);

        var last = steps.OrderByDescending(s => s.SortOrder).FirstOrDefault()
                   ?? throw new InvalidOperationException("这次运行还没跑过任何阶段");
        var packId = _db.GetRunPackId(runId);
        var stages = _db.GetSkillStages(packId).Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToList();
        var idx = stages.FindIndex(s => s.StageKey == last.StageKey) + 1;
        if (idx >= stages.Count)
        {
            _db.UpdateSkillRun(runId, last.StageKey, "done");
            return;
        }
        await RunFromAsync(userId, runId, stages[idx].StageKey, last.OutputText ?? "");
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
