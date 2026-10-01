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
            // 自动串联：上一步的产出就是下一步的素材。
            // 例外在这一行里：P2c 三批要的是台账，图册要的是「台账 + 这一批的产出」
            if (i + 1 < stages.Count)
                input = BuildChainInput(runId, stages[i + 1].StageKey, st.StageKey, r.Output);
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
        await RunFromAsync(userId, runId, nextKey,
                           BuildChainInput(runId, nextKey, last.StageKey, last.OutputText),
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

    /// <summary>
    /// 取某阶段最近一次有产出的内容。多次重跑时以最新的那次为准。
    /// step_done 也得算：每一步都是人单独点的，跑到这一步边界就收手时状态是 step_done，
    /// 产出早就写进库了。以前只认 done / await_confirm，于是「点完图册再点分镜」
    /// 拿到的上游素材是空的——分镜拿着空输入开工，只能照着剧本自己编场景名。
    ///
    /// 本运行里没有这一段时回落到「已经落库的那一份」（见 DramaLevelOutputOf）：
    /// 逐集跑剧本是另起一次运行，链上根本没有 P0 那一步，不回落就是空手开工。
    /// </summary>
    private string LastOutputOf(int runId, string stageKey)
    {
        var own = _db.GetSkillSteps(runId)
                     .Where(s => s.StageKey == stageKey && s.Status is "done" or "await_confirm" or "step_done")
                     .OrderByDescending(s => s.StepId)
                     .Select(s => s.OutputText)
                     .FirstOrDefault();

        return string.IsNullOrWhiteSpace(own) ? DramaLevelOutputOf(runId, stageKey) : own;
    }

    /// <summary>
    /// 跨运行的上游产出从库里取，不再要求它跟本次运行绑在一起。
    ///
    /// 立项（P0）跑在漫剧那一次的运行上，逐集跑剧本是另一条运行——按 runId 去找 P0 步骤
    /// 必然找空，模型手里只剩「题材：宫崎骏自愈系、画幅 16:9」三个锁定项，
    /// 全剧结构一个字都没有，于是自己重写了一份「三、全剧梗概」：
    /// 第 1 集（立项那次运行里顺带跑的，链上有完整 P0）写的梗概和第 2 集（另起运行）
    /// 写的梗概对不上，12 集跑下来就是 12 个互不认识的故事。
    ///
    /// 只认两类可以安全共用的产出：
    ///   P0 —— 漫剧级，一部一份，立项结果就存在 Dramas.P0OutputText；
    ///   P1 —— 本集剧本，跑完已写回 Projects.ScriptContent，按项目取就是这一集的。
    /// 其余阶段（P2c 三批、P2d 图册、P3 分镜、P4 提示词）都是集级的，
    /// 跨运行去拿会拿到别的集的东西，宁可空着也不回落。
    /// </summary>
    private string DramaLevelOutputOf(int runId, string stageKey)
    {
        if (string.Equals(stageKey, "P0", StringComparison.OrdinalIgnoreCase))
        {
            var did = _db.GetRunDramaId(runId);
            if (did <= 0) return "";

            var saved = _db.GetDramaP0Result(did);
            var text = !string.IsNullOrWhiteSpace(saved?.OutputText) ? saved!.OutputText
                                                                    : _db.GetLatestP0Output(did);
            return string.IsNullOrWhiteSpace(text)
                ? ""
                : "（本运行里没有立项这一步，下面是这部漫剧已入库的立项结果。"
                  + "全剧结构、逐集梗概、人物与场景设定一律以这份为准：剧本里的「全剧梗概」一章照抄它，"
                  + "不得照着题材另写一份——另写一份，第 2 集就会跟第 1 集对不上。）\n" + text;
        }

        if (string.Equals(stageKey, "P1", StringComparison.OrdinalIgnoreCase))
        {
            var pid = _db.GetRunContext(runId).ProjectId;
            if (pid <= 0) return "";

            var script = _db.GetProjectScriptContent(pid);
            return string.IsNullOrWhiteSpace(script)
                ? ""
                : "（本运行里没有剧本这一步，下面是这一集已入库的剧本）\n" + script;
        }

        return "";
    }

    /// <summary>
    /// 某些阶段的上家不是它前面那一个，而是更早那一站的定稿。
    /// 资产台账（P2b）自己写着「本清单为 P2c 三批次出图的唯一依据」，
    /// 所以第二批（场景）、第三批（道具与特效）都得回头拿台账。
    /// 接上一批的提示词产出会怎样：模型手头没有资产清单，就照着剧本自己编场景名，
    /// 写出来的是「SCN-城市出租屋」这种台账上没有的东西，回填时对不上，入库 0 条。
    /// </summary>
    private static string? LedgerSourceOf(string stageKey)
        => stageKey.StartsWith("P2c", StringComparison.OrdinalIgnoreCase) ? "P2b" : null;

    /// <summary>
    /// 除了上一站的产出，还得另外补一段素材的阶段。
    /// 资产图册（P2d）是分镜与提示词引用资产的唯一依据，锚点必须是台账全名。
    /// 以前它只拿到 P2c3 那批道具提示词，手里没有台账，角色 / 场景名只能从道具描述里反推，
    /// 写出来的是 @SCN-老屋、@SCN-染布间 这种台账上没有的名字——分镜照抄，
    /// 十张场景母版图一张也绑不上。所以图册这一站要「台账 + 上一批产出」两份都给。
    /// </summary>
    private static string[] ExtraSourcesOf(string stageKey)
    {
        if (stageKey.Equals("P2d", StringComparison.OrdinalIgnoreCase)) return new[] { "P2b" };

        /* 分镜（P3）同样要剧本：台词、场次、调度依据只写在 P1 剧本里，
           图册里除资产之外一个字也没有。以前这一站只拿到图册，
           模型手里没有剧本，单元怎么切、每句台词怎么说全靠自己编——
           编出来的 OS 在剧本里查无此句，12 个单元也是凭空分的。 */
        if (stageKey.Equals("P3", StringComparison.OrdinalIgnoreCase)) return new[] { "P1" };

        /* 投喂提示词（P4）要引用资产图编号（@图N / @图片N），编号只在图册里定义。
           以前这一站只拿到分镜，模型手里没有编号对照表，只能照着资产名自己编——
           编出来的 @图3 指的是哪张图谁也不知道，十张母版图一张也绑不上。 */
        if (stageKey.Equals("P4", StringComparison.OrdinalIgnoreCase)) return new[] { "P2d" };

        return Array.Empty<string>();
    }

    /// <summary>
    /// 拼某一站的素材：台账类阶段（P2c 三批）只给台账；图册给「台账 + 上一站产出」；其余接上一站。
    /// prevOutput 传内存里刚跑出来的那份——这一步的状态此时还是 running，
    /// 按状态去库里取最新产出会取到空，把刚出来的东西丢了。
    /// </summary>
    private string BuildChainInput(int runId, string stageKey, string prevStageKey, string? prevOutput = null)
    {
        // 跨集复用锚点：只有出提示词这三批（P2c*）需要——这一步才决定资产长什么样
        var anchor = ReuseAnchorSection(runId, stageKey);
        // 本集时长目标：只有分镜（P3）需要——镜长之和要凑到这个数
        var dur = DurationTargetSection(runId, stageKey);

        var ledger = LedgerSourceOf(stageKey);
        if (ledger != null)
        {
            var l = LastOutputOf(runId, ledger);
            if (!string.IsNullOrWhiteSpace(l)) return ChainHead(ledger) + l + anchor + dur;
        }
        var head = "";
        foreach (var k in ExtraSourcesOf(stageKey))
        {
            var t = LastOutputOf(runId, k);
            if (!string.IsNullOrWhiteSpace(t)) head += ChainHead(k) + t + "\n\n";
        }
        /* prevOutput 是内存里刚跑出来的那份。空串也算没有——单跑某一步时它常常是空的，
           这时必须走回落，否则跨运行的立项/剧本接不上（见 DramaLevelOutputOf）。 */
        var prev = string.IsNullOrWhiteSpace(prevOutput) ? LastOutputOf(runId, prevStageKey) : prevOutput;
        return head + ChainHead(prevStageKey) + prev + anchor + dur;
    }

    /// <summary>
    /// 本集成片时长目标（立项里定的「单集时长（秒）」，一部剧每集一样）。
    ///
    /// 为什么必须注入：分镜（P3）的输入只有剧本和图册，模型根本不知道这一集该多长，
    /// 于是自己定了两档（过场 5 秒 / 长镜 11 秒）往下排 —— 第 2 集排出来 147 秒，
    /// 而立项定的是 180 秒，整整少 33 秒（18%）。成片时长是交付口径，差这么多等于没按立项做。
    ///
    /// 只给目标不给做法：怎么凑（补镜还是排长）由它按内容决定，但必须自己加一遍并写在末尾。
    /// 立项没填时长就不注入——那时宁可让它按内容排，也不塞一个编出来的数。
    /// </summary>
    private string DurationTargetSection(int runId, string stageKey)
    {
        if (!stageKey.Equals("P3", StringComparison.OrdinalIgnoreCase)) return "";

        var ctx = _db.GetRunContext(runId);
        if (ctx.ProjectId <= 0) return "";
        var brief = _db.GetDramaBrief(_db.GetDramaIdByProject(ctx.ProjectId));
        var sec = brief?.EpisodeDuration ?? 0;
        if (sec <= 0) return "";

        var lo = Math.Max(1, (int)Math.Round(sec * 0.95));
        var hi = (int)Math.Round(sec * 1.05);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("【本集成片时长目标·引擎注入】这一集的目标成片时长是 " + sec + " 秒（立项定的，每集一样）。");
        sb.AppendLine("分镜表里各镜时长之和必须落在 " + lo + "~" + hi + " 秒（±5%）之内：");
        sb.AppendLine("- 排完自己把镜长加一遍。不够就补镜、或把该长的镜排长；超了就压。差一截就交付等于没按立项做。");
        sb.AppendLine("- 单个镜头 3~8 秒。剧本里明确写了秒数的节奏标注（例如「静场约 8 秒」「间隔约 6 秒」）照抄进对应镜的时长，");
        sb.AppendLine("　不要换成自己那套档位；需要更长（长镜头、静场）按剧本标注走，单镜最多不超过 12 秒。");
        sb.AppendLine("- 产出末尾必须单独写一行校验：「时长校验：共 N 镜 · 合计 X 秒 · 目标 " + sec + " 秒 · 偏差 Y 秒」。");
        return sb.ToString();
    }

    /// <summary>
    /// 跨集复用锚点：这一集的「杨彦刚」跟上一集那个是同一个人（身份层按名字认的），
    /// 把前面几集已经出好的定妆图摆到模型面前，让它照着写——
    /// 以前每一集各自凭剧本描述一张脸，12 集下来杨彦刚是 12 张不同的脸。
    ///
    /// 只在出图提示词这三批（P2c1 角色 / P2c2 场景 / P2c3 道具与特效）注入：这一步才定形象。
    /// 名字对不上身份的（比如这一集才第一次出现的角色）自然不会出现在这里。
    /// </summary>
    private string ReuseAnchorSection(int runId, string stageKey)
    {
        if (!stageKey.StartsWith("P2c", StringComparison.OrdinalIgnoreCase)) return "";

        var ctx = _db.GetRunContext(runId);
        var dramaId = _db.GetRunDramaId(runId);
        if (ctx.ProjectId <= 0 || dramaId <= 0) return "";

        var list = _db.GetEpisodeAnchors(dramaId, ctx.ProjectId);
        if (list.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("【跨集复用锚点·引擎注入】同一部漫剧里，下面这些角色 / 场景 / 道具在前面几集已经出过图，");
        sb.AppendLine("它们在这一集里还是同一个（身份层按名字认的，别名也算）。写这一集的提示词时照着已经出好的那张写：");
        sb.AppendLine("人物的脸型、发型、配色与服装基调，场景的结构与材质，道具的造型与材质，一律与那张保持一致；");
        sb.AppendLine("不要把这一个在这一集写成另一个模样，也不要换一套配色。");

        foreach (var a in list.Take(40))
        {
            var kind = a.Category switch
            {
                "character" => "角色",
                "environment" => "场景",
                "prop" => "道具",
                "effect" => "特效",
                _ => "资产"
            };
            sb.AppendLine($"- [{kind}] {a.Name} —— 第 {a.Episode} 集已出图：{a.ImageUrl}");
            if (!string.IsNullOrWhiteSpace(a.Prompt))
            {
                var p = a.Prompt!.Replace("\r", " ").Replace("\n", " ").Trim();
                if (p.Length > 300) p = p[..300] + "…";
                sb.AppendLine("　　那一集用的提示词：" + p);
            }
        }
        return sb.ToString();
    }

    /// <summary>给第 idx 个阶段找素材：默认接上一个阶段的产出，P2c 三批则回头拿台账。</summary>
    private string ChainInputFor(int runId, List<DbService.SkillStageRow> stages, int idx)
    {
        if (idx <= 0 || idx >= stages.Count) return "";
        return BuildChainInput(runId, stages[idx].StageKey, stages[idx - 1].StageKey);
    }

    // ========== 3c. 单步执行的核心：单跑和连跑都走这里 ==========

    private async Task<StepResult> ExecuteStepAsync(int userId, int runId, int packId,
                                                    DbService.SkillStageRow stage, string inputText)
    {
        var provider = _db.GetActiveLLMProvider(userId);
        var cfg = _db.GetActiveConfig(userId, string.IsNullOrWhiteSpace(provider) ? "deepseek" : provider)
                  ?? throw new InvalidOperationException("没有可用的 LLM 配置，先去系统配置里配一个");

        /* 提示词引擎（SD / H3）的权威来源是立项里那一列——现在就是 Dramas.PromptEngine。
           立项是漫剧级的：一部漫剧一份，跑的是哪一集（项目）都读同一份。
           改那一列就等于换引擎——不用重开一次运行，也不用等跑到 P4 才定。
           立项里还没填时才回落到运行级参数：早期运行是建运行时把类型写进 InputsJson 的，
           那份数据还得认，不然老运行一升级就跑不出东西。 */
        if (!inputText.Contains("提示词类型"))
        {
            var runCtx = _db.GetRunContext(runId);
            // 手里是项目，立项在漫剧上——先上溯一级
            var brief = _db.GetDramaBrief(_db.GetDramaIdByProject(runCtx.ProjectId));
            var engine = NormalizeEngine(brief?.PromptEngine);

            if (engine == null && !string.IsNullOrWhiteSpace(stage.InputsJson)
                && stage.InputsJson!.Contains("promptType"))
            {
                var runIn = _db.GetRunInputsJson(runId);
                if (!string.IsNullOrWhiteSpace(runIn))
                {
                    var m = Regex.Match(runIn!, "\"promptType\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) engine = NormalizeEngine(m.Groups[1].Value);
                }
            }

            if (engine != null) inputText += "\n提示词类型：" + engine;
        }

        /* 立项锁定的参数必须跟着整条流水线走。
           下游每一站拿的都是上游产出：P2d 拿台账、P3 拿图册、P4 拿分镜，
           谁也不含 P0 那份锁定表——模型手头只有「目标平台：抖音」，
           于是照着平台自己推画幅，把立项定好的 16:9 横屏写成了 9:16 竖屏，
           一路错到 P4。这里把立项表单原样塞回去，并写明它的优先级高于任何上游产出。 */
        if (!string.Equals(stage.StageKey, "P0", StringComparison.OrdinalIgnoreCase)
            && !inputText.Contains("立项锁定参数"))
        {
            var p0 = _db.GetSkillSteps(runId)
                        .Where(s => s.StageKey == "P0" && !string.IsNullOrWhiteSpace(s.InputText))
                        .OrderBy(s => s.StepId)
                        .Select(s => s.InputText)
                        .LastOrDefault();

            /* 立项跑在漫剧上是另一次运行：逐集跑剧本的这次运行里根本没有 P0 步骤，
               只在本运行里找就会找空，画幅这些定死的值传不下去——
               下游又会照着「目标平台：抖音」把立项定好的 16:9 横屏推成竖屏。
               这时从漫剧那份立项记录里取，两份文本的字段与顺序是同一套。 */
            if (string.IsNullOrWhiteSpace(p0))
            {
                var did = _db.GetRunDramaId(runId);
                if (did > 0) p0 = _db.BuildP0InputText(did);
            }

            if (!string.IsNullOrWhiteSpace(p0))
                inputText += "\n\n【立项锁定参数·引擎注入·不得改写】下面这些是立项阶段定死的参数，"
                           + "优先级高于任何上游产出里的同名项——上游要是写了别的画幅，以本表为准，"
                           + "不要按「目标平台」之类的线索重新推断：\n" + p0;
        }

        /* 本集提纲：立项锁的是全剧（画幅、集数、平台），「这一集讲什么、卡在哪」只写在
           Dramas.EpisodeOutlineJson 里对应集号的那一条。逐集跑剧本是另一次运行，
           上游产出链上根本没有它——不给模型，第 2 集就会照着立项自己编一个故事，
           12 集跑下来可能全在讲同一件事。这里按项目集号取本集那条喂回去。 */
        if (!string.Equals(stage.StageKey, "P0", StringComparison.OrdinalIgnoreCase)
            && !inputText.Contains("本集提纲"))
        {
            var epCtx = _db.GetRunContext(runId);
            var ep = _db.GetEpisodeOutlineForProject(epCtx.ProjectId);
            if (ep != null)
            {
                var epSb = new StringBuilder();
                epSb.AppendLine("\n【本集提纲·第" + ep.EpisodeNumber + "集（漫剧立项时定死，本集所有内容以它为准）】");
                epSb.AppendLine("标题：" + ep.Title);
                if (!string.IsNullOrWhiteSpace(ep.Outline))
                    epSb.AppendLine("三幕骨架：" + ep.Outline!.Trim());
                if (!string.IsNullOrWhiteSpace(ep.Cliffhanger))
                    epSb.AppendLine("本集卡点（Cliffhanger）：" + ep.Cliffhanger!.Trim());
                epSb.AppendLine("本集的剧本、分镜与提示词只能演这一集的事：禁止把上一集或下一集的情节写进来，"
                              + "禁止改写上面的骨架与卡点；卡点必须落在本集结尾，作为悬念留给下一集。");
                inputText += epSb.ToString();
            }
        }

        var build = BuildPrompt(packId, stage);
        var cost = CostOf(packId, stage);
        int stepId = _db.CreateSkillStep(runId, stage.StageKey, stage.Name, stage.SortOrder,
                                         inputText, build.DocsSummary, build.Chars, stage.Gates);

        /* 类型覆盖要贴在最后：规则库里 SD 与 H3 两套文档是同时加载的，
           不点明本次走哪套，模型必然被字数多的那套带跑。放在末尾说的话权重最高。 */
        var sys = build.SystemPrompt + "\n\n# 输入（用户提供的素材）\n" + inputText
                  + PromptTypeOverride(inputText);

        // 分镜这一站要连着调好几次 LLM（列清单 + 分批写），调用参数收在一处，下面只管发指令
        async Task<string> CallLlm(string userMsg, bool jsonMode = false)
            => await _llm.CallAsync(cfg.ApiUrl ?? "", cfg.ApiKey, cfg.ModelName ?? "", sys, userMsg,
                                    jsonMode: jsonMode, temperature: 0.7, thinkingMode: cfg.ThinkingMode);

        try
        {
            string raw;
            string? json;
            bool jsonSalvaged = false;
            string? batchNote = null;

            /* 分镜一次写不完一整集：27 镜的 json 写到三分之二就撞上输出上限，
               尾巴上那一两个单元（常常正是带台词的收尾镜）整个消失。
               它原本打算切几个单元，事先我们无从得知——输出一断，连「缺了什么」都问不出来。
               所以先让它列一张短清单，把「一共几镜、台词归谁」写在纸上，再照清单分批写。 */
            if (string.Equals(stage.StageKey, "P3", StringComparison.OrdinalIgnoreCase))
            {
                var r = await RunStoryboardByOutlineAsync(msg => CallLlm(msg));
                raw = r.Raw;
                json = r.Json;
                jsonSalvaged = r.Salvaged;
                batchNote = r.Note;

                if (LooksBlocked(raw))
                {
                    _db.FinishSkillStep(stepId, "blocked", raw, null, "这一步没能产出内容");
                    return new StepResult(stepId, stage.StageKey, "blocked", raw, build.Chars,
                                          EstimateTokens(sys), stage.Gates, cost, 0, null);
                }
            }
            else if (string.Equals(stage.StageKey, "P4", StringComparison.OrdinalIgnoreCase))
            {
                /* 28 镜的七段式提示词一次写完 ≈ 2.8 万字，写到一半就撞输出上限，
                   后半集的提示词整个没了。分镜那站已经把单元切好入库，
                   这里不用再问模型「打算切几个」，直接照库里的单元分批写。 */
                var ctx = _db.GetRunContext(runId);
                var r = await RunPromptsByUnitsAsync(msg => CallLlm(msg), _db, ctx.ProjectId, ctx.EpisodeId,
                                                     PromptTypeOf(inputText) == "H3");
                raw = r.Raw;
                json = null;            // 提示词只认 md 标题结构，解析器不读 json
                batchNote = r.Note;

                if (LooksBlocked(raw) || string.IsNullOrWhiteSpace(raw))
                {
                    _db.FinishSkillStep(stepId, "blocked", raw, null, "这一步没能产出内容");
                    return new StepResult(stepId, stage.StageKey, "blocked", raw, build.Chars,
                                          EstimateTokens(sys), stage.Gates, cost, 0, null);
                }
            }
            else
            {
                /* 出图提示词这几批要走 JSON 模式。它们的产出是要回填到资产卡上的结构化数据，
                   让模型自由排版的话，标题写法每批都不一样（## SCN-01 · 老屋堂屋 / ## 01 · SCN-… /
                   **中文正式提示词**…），解析器只能一路追着补，追不上就整批入库 0 条。
                   定死格式比事后猜格式省事得多。 */
                raw = await CallLlm("请严格按「本阶段产出要求」输出，不要输出多余解释。",
                                    jsonMode: string.Equals(stage.OutputTarget, "asset_prompts",
                                                            StringComparison.OrdinalIgnoreCase));

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
                        raw = await CallLlm(push);
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

                json = TryExtractJson(raw, out jsonSalvaged);
            }

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

            /* 救回来的 json 是截断版：得明说这批可能少收了末尾几条，
               不然页面上数量对不上，会以为是漏单元或规则没生效。 */
            if (jsonSalvaged && imported > 0)
                importError = (string.IsNullOrEmpty(importError) ? "" : importError + "；")
                    + "产出的 json 未正常闭合（末尾缺 } 或 ]），已截断到最后一条完整镜头入库，末尾可能少收 1~2 条";

            // 哪一批没写出来要写明，不然页面上少了几镜，看的人只会以为是规则又没生效
            if (!string.IsNullOrWhiteSpace(batchNote))
                importError = (string.IsNullOrEmpty(importError) ? "" : importError + "；") + batchNote;

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

    // ========== 3d. 分镜（P3）：先列结构清单，再按清单分批写 ==========

    /// <summary>结构清单的一行：一个单元切几镜、演什么、台词归谁、出自剧本第几场。</summary>
    private sealed record OutlineUnit(string Unit, string Scene, string Summary, string Dialogue, int Source, int Shots);

    private sealed record StoryboardBuild(string Raw, string? Json, bool Salvaged, string? Note);

    /// <summary>第 1 步的指令：只要结构，不要镜头细节。</summary>
    private const string OutlineInstruction = """
        【本阶段第 1 步 / 共 2 步：只列结构清单，不要写镜头】

        读输入里的剧本，把本集切成单元，在产出末尾附一个 ```json 块，每行一个单元：
          unit      单元号，形如 "1.1"，从 1.1 顺序编到本集最后一场
          source    本单元出自剧本第几场（按剧本里的 [1] [2] 这类场号填数字）
          scene     场景名（用剧本里的地点）
          shots     本单元镜数（2~3）
          summary   一句话说这个单元演什么（不超过 20 字）
          dialogue  本单元台词，逐字照抄剧本原文；剧本里没有就写 ""

        现在不要写镜头细节——不要 shotSize / camera / description / 锚点，那些第 2 步才写。
        约束：
        - 必须覆盖剧本的每一场，不许合并，不许跳过结尾
        - 单元序号由剧本场次决定，与【总集数】无关（剧本写 12 集不等于切 12 个单元）
        - 只切当前这一集，不许出现别的集号
        - 剧本里的每句台词都要落到某个单元上，一句不许漏
        """;

    /// <summary>
    /// 分镜两阶段跑法：先要一张「本集有哪些单元」的短清单，再按清单分批要镜头。
    /// 一次要整集的话，模型写到三分之二就撞输出上限，末尾单元（常常正是带台词的收尾镜）整个没了；
    /// 而它原本打算切几个单元，事先我们无从得知——输出一断，连「缺了什么」都问不出来。
    /// 清单先把这件事钉死：一共几镜、台词归谁、缺哪一行都写在纸上，分批照着写就不会漏。
    /// </summary>
    private static async Task<StoryboardBuild> RunStoryboardByOutlineAsync(Func<string, Task<string>> call)
    {
        var sb = new StringBuilder();
        var outlineRaw = await call(OutlineInstruction);
        sb.AppendLine("## 第 1 步 · 结构清单").AppendLine(outlineRaw).AppendLine();

        var units = ParseOutlineUnits(TryExtractJson(outlineRaw, out _));

        // 清单没拿到（模型不听话，或第一段也写飞了）就退回一次全量，至少不比原来更差
        if (units.Count == 0)
        {
            var full = await call("请严格按「本阶段产出要求」输出完整分镜，不要输出多余解释。");
            sb.AppendLine("## 结构清单未取得，退回一次性全量产出").AppendLine(full);
            var fj = TryExtractJson(full, out var fs);
            return new StoryboardBuild(sb.ToString(), fj, fs, null);
        }

        var outlineJson = JsonSerializer.Serialize(
            units.Select(u => new { unit = u.Unit, source = u.Source, scene = u.Scene,
                                    shots = u.Shots, summary = u.Summary, dialogue = u.Dialogue }));

        const int unitsPerBatch = 4;   // 4 个单元 ≈ 8~12 镜 ≈ 5000 字，离输出上限还很远
        var totalBatches = (units.Count + unitsPerBatch - 1) / unitsPerBatch;
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var salvaged = false;

        for (var i = 0; i < units.Count; i += unitsPerBatch)
        {
            var batch = units.Skip(i).Take(unitsPerBatch).ToList();
            var n = i / unitsPerBatch + 1;
            var raw = await call(BuildBatchInstruction(outlineJson, batch, n, totalBatches));
            sb.AppendLine($"## 第 2 步 · 第 {n}/{totalBatches} 批（单元 {batch[0].Unit} ~ {batch[^1].Unit}）")
              .AppendLine(raw).AppendLine();

            var bJson = TryExtractJson(raw, out var bSalvaged);
            if (bSalvaged) salvaged = true;

            var got = 0;
            foreach (var (key, item) in ReadJsonItems(bJson))
            {
                if (key.Length == 0 || !seen.Add(key)) continue;   // 同一镜被重复写就只留第一次
                items.Add(item);
                got++;
            }
            // 这一批一条都没写出来：得记下来，页面上少几镜要让人知道是哪几个单元
            if (got == 0) missing.AddRange(batch.Select(b => b.Unit));
        }

        var json = items.Count > 0 ? "[" + string.Join(",", items) + "]" : null;
        var note = missing.Count > 0 ? "未产出的单元：" + string.Join("、", missing) : null;
        return new StoryboardBuild(sb.ToString(), json, salvaged, note);
    }

    /// <summary>第 2 步每批的指令：给完整清单做衔接，再点名这批只写哪几个单元、台词原文是什么。</summary>
    private static string BuildBatchInstruction(string outlineJson, List<OutlineUnit> batch, int n, int total)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【本阶段第 2 步：按结构清单写镜头。当前第 {n}/{total} 批】").AppendLine();
        sb.AppendLine("本集完整结构清单（只供你保持前后衔接，不要写清单以外的单元）：");
        sb.AppendLine(outlineJson).AppendLine();
        sb.AppendLine($"本批只写这 {batch.Count} 个单元，共 {batch.Sum(b => b.Shots)} 镜：");
        sb.AppendLine(JsonSerializer.Serialize(batch.Select(b => new { unit = b.Unit, scene = b.Scene,
                                                                       shots = b.Shots, summary = b.Summary,
                                                                       dialogue = b.Dialogue })));

        var withDialogue = batch.Where(b => !string.IsNullOrWhiteSpace(b.Dialogue)).ToList();
        if (withDialogue.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("本批台词原文，逐字照抄进 dialogue 字段，不许改写、不许漏：");
            foreach (var b in withDialogue) sb.AppendLine($"  · {b.Unit}：「{b.Dialogue}」");
        }

        sb.AppendLine();
        sb.AppendLine("要求：");
        sb.AppendLine("- 只输出这批单元的镜头，镜号写成「单元号-序号」，如 1.5-1 / 1.5-2");
        sb.AppendLine("- 字段与「本阶段产出要求」完全一致，产出末尾附 ```json 块");
        sb.AppendLine("- 锚点、画幅、视图后缀、镜长那些规矩照常执行");
        sb.AppendLine("- 不许输出清单里的其他单元，不许输出解释");
        return sb.ToString();
    }

    /// <summary>解析第 1 步的结构清单。拿不到就返回空，调用方会退回一次性全量。</summary>
    private static List<OutlineUnit> ParseOutlineUnits(string? json)
    {
        var list = new List<OutlineUnit>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var unit = (StrOf(e, "unit") ?? "").Trim();
                if (unit.Length == 0) continue;
                list.Add(new OutlineUnit(
                    unit,
                    StrOf(e, "scene") ?? "",
                    StrOf(e, "summary") ?? "",
                    StrOf(e, "dialogue") ?? "",
                    int.TryParse(StrOf(e, "source"), out var s) ? s : 0,
                    int.TryParse(StrOf(e, "shots"), out var n) ? n : 2));
            }
        }
        catch { }
        return list;
    }

    /// <summary>拆开一批的 json 数组，返回（镜号, 该元素原始 json）。镜号用于跨批去重。</summary>
    private static List<(string Key, string Raw)> ReadJsonItems(string? json)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
                list.Add(((StrOf(e, "shotNumber") ?? StrOf(e, "unit") ?? "").Trim(), e.GetRawText()));
        }
        catch { }
        return list;
    }

    private static string? StrOf(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    // ========== 3e. 投喂提示词（P4）：照库里已入库的单元分批写 ==========

    private sealed record PromptBatchBuild(string Raw, string? Note);

    /// <summary>
    /// 投喂提示词按单元分批跑。28 镜的七段式一次写完 ≈ 2.8 万字，写到一半就撞输出上限，
    /// 后半集的提示词整个没了。分镜（P3）已经把单元切好入库，这里不用再问模型「打算切几个」，
    /// 直接读库分组，每批发一次调用。
    /// 产出是 Markdown（解析器按【第X集】【单元X.Y】【镜头X.Y-N】三连标题切块），各批首尾相接。
    /// 拼接时不能在批与批之间插任何说明行——那行会落进前一批最后一个镜头的提示词里，
    /// 跟着一起喂给视频模型。所以批次信息只进 Note，不进正文。
    /// </summary>
    private static async Task<PromptBatchBuild> RunPromptsByUnitsAsync(Func<string, Task<string>> call,
                                                                       DbService db, int projectId, int episodeId,
                                                                       bool h3)
    {
        var frames = episodeId > 0 ? db.GetFrames(episodeId)
                                   : projectId > 0 ? db.GetAllFrames(projectId)
                                                   : new List<Models.StoryboardFrame>();

        // 库里一帧都没有（单独从这一步起跑、分镜还没入库）就退回一次全量，至少不比原来更差
        if (frames.Count == 0)
            return new PromptBatchBuild(await call("请严格按「本阶段产出要求」输出，不要输出多余解释。"), null);

        // 按单元分组：库里取出来的已是自然序（集.单元），照首次出现的次序分批
        var units = new List<string>();
        var byUnit = new Dictionary<string, List<Models.StoryboardFrame>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in frames)
        {
            var u = (f.UnitNumber ?? "").Trim();
            if (!byUnit.TryGetValue(u, out var l)) { l = new List<Models.StoryboardFrame>(); byUnit[u] = l; units.Add(u); }
            l.Add(f);
        }

        /* 提示词比镜头本身长得多，每批不能贪。
           H3 尤甚：一镜要写素材说明 + 六节 + 350~600 字正文，一镜顶 SD 两三镜，
           3 个单元（7~9 镜）能写到 1.8 万字，照样撞输出上限。 */
        var unitsPerBatch = h3 ? 2 : 3;   // SD：3 单元 ≈ 7~9 镜 ≈ 7000~9000 字
        var total = (units.Count + unitsPerBatch - 1) / unitsPerBatch;
        var ep = frames[0].EpisodeNumber ?? 1;

        var sb = new StringBuilder();
        var missing = new List<string>();

        for (var i = 0; i < units.Count; i += unitsPerBatch)
        {
            var batch = units.Skip(i).Take(unitsPerBatch).ToList();
            var n = i / unitsPerBatch + 1;

            var raw = await call(BuildPromptBatchInstruction(db, projectId, byUnit, batch, ep, n, total, h3));
            if (LooksBlocked(raw)) { missing.AddRange(batch); continue; }

            sb.AppendLine(raw.Trim()).AppendLine();
        }

        var note = missing.Count > 0 ? "未产出的单元：" + string.Join("、", missing) : null;
        return new PromptBatchBuild(sb.ToString(), note);
    }

    /// <summary>每批的指令：点名这批写哪几个单元，并把库里的镜明细原样摆出来。</summary>
    private static string BuildPromptBatchInstruction(DbService db, int projectId,
                                                      Dictionary<string, List<Models.StoryboardFrame>> byUnit,
                                                      List<string> batch, int episode, int n, int total,
                                                      bool h3)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【本阶段第 {n}/{total} 批：只写这几个单元的投喂提示词】").AppendLine();
        sb.AppendLine($"本批单元：{string.Join("、", batch)}，共 {batch.Sum(u => byUnit[u].Count)} 镜。").AppendLine();
        sb.AppendLine("镜头明细（取自已入库分镜，镜号、锚点、台词以这里为准）：").AppendLine();

        foreach (var u in batch)
            foreach (var f in byUnit[u])
            {
                sb.AppendLine(FrameBrief(f, episode));
                /* 音色参考 SD 和 H3 都要给：两边是同一张表、同一套编号（都由 VoiceRefResolver 产出），
                   区别只在落进正文的句式——H3 是六节里的独立声明，SD 是时间轴台词行里的内联标注。
                   必须逐镜给：<Audio N> 是每镜从 1 重新起的编号，
                   第 1 镜的 <Audio 1> 和第 2 镜的 <Audio 1> 很可能是不同角色。 */
                var vr = VoiceRefBlock(db, projectId, f, h3);
                if (vr.Length > 0) sb.AppendLine(vr);
            }

        sb.AppendLine("要求：");
        sb.AppendLine("- 每个镜头一段，标题严格写成上面给出的【第X集】【单元X.Y】【镜头X.Y-N】，一字不差");
        sb.AppendLine("- 七段式照「本阶段产出要求」写全；参考图引用只能用输入图册里出现过的编号");
        /* 音色在两种写法里的落点不同，这里再钉一次：模型很容易把 H3 那套独立声明
           照搬进 SD，多出来的段会打断七段式。 */
        if (h3)
            sb.AppendLine("- 音色：<Audio N> 写进该镜的说话句，编号照本镜【音色参考表】，禁止自造、改派或换序；同时在【参考素材说明】补 @音频N 声明行、并输出 audio_definitions: 节把 <Audio N> 定义出来（缺这两处，<Audio N> 就是悬空引用）；内心独白 OS / 旁白 / 画外音也算开口，同样带 <Audio N>");
        else
            sb.AppendLine("- 音色：只标在【时间轴分镜】的台词行里（…使用@音频N的音色说话…），编号照本镜【音色参考表】，禁止自造、改派或换序，禁止新增段落");
        sb.AppendLine("- 只输出这批单元的提示词：不许写别的单元，不许输出解释，不许输出「第 n 批」这类说明行");
        return sb.ToString();
    }

    /// <summary>把一镜压成给模型看的几行：镜号 + 景别机位角色场景时长 + 画面 + 台词 + 状态机。</summary>
    private static string FrameBrief(Models.StoryboardFrame f, int episode)
    {
        var sb = new StringBuilder();
        sb.Append($"【第{episode}集】【单元{(f.UnitNumber ?? "").Trim()}】【镜头{(f.ShotNumber ?? "").Trim()}】");

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.ShotSize)) meta.Add("景别 " + f.ShotSize!.Trim());
        if (!string.IsNullOrWhiteSpace(f.Camera)) meta.Add("机位 " + f.Camera!.Trim());
        if (!string.IsNullOrWhiteSpace(f.Characters)) meta.Add("角色 " + f.Characters!.Trim());
        if (!string.IsNullOrWhiteSpace(f.Scene)) meta.Add("场景 " + f.Scene!.Trim());
        if (!string.IsNullOrWhiteSpace(f.Duration)) meta.Add("时长 " + f.Duration!.Trim());
        if (meta.Count > 0) sb.Append(" | " + string.Join(" | ", meta));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(f.Description)) sb.AppendLine("  画面：" + f.Description!.Trim());
        if (!string.IsNullOrWhiteSpace(f.Dialogue) && f.Dialogue!.Trim() != "无")
            sb.AppendLine("  台词：" + f.Dialogue!.Trim());

        // L4 状态机三态：提示词的时间轴分镜照这三项写，起止对不上就接不上戏
        if (!string.IsNullOrWhiteSpace(f.StartState) || !string.IsNullOrWhiteSpace(f.SingleAction)
            || !string.IsNullOrWhiteSpace(f.EndState))
            sb.AppendLine($"  状态：{(f.StartState ?? "").Trim()} → {(f.SingleAction ?? "").Trim()} → {(f.EndState ?? "").Trim()}");

        if (!string.IsNullOrWhiteSpace(f.NextConnection)) sb.AppendLine("  接续：" + f.NextConnection!.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// 本镜的音色参考表，SD / H3 共用。没配音色、或出场角色一个都没配过音色时返回空串——
    /// 这种镜头提示词里就不该出现 <Audio N>，让模型自造编号只会套错人。
    /// 编号由 VoiceRefResolver 产出；H3 那边与提交 ComfyUI 时 ref_audios 的槽位顺序同源，
    /// SD（火山方舟）目前没有音频入参，<Audio N> 落在提示词里作为后期配音的音色指定。
    /// </summary>
    private static string VoiceRefBlock(DbService db, int projectId, Models.StoryboardFrame f, bool h3)
    {
        var refs = VoiceRefResolver.Resolve(db, projectId, f.FrameId, SplitCharacterNames(f.Characters));
        if (refs.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("  【音色参考表（系统已确定，最高优先级：<Audio N> 与角色的对应关系必须逐条照抄，禁止改派、增删或换序）】");
        sb.AppendLine($"  本镜共 {refs.Count} 段音色参考音频（编号 <Audio 1> ~ <Audio {refs.Count}>）：");
        foreach (var v in refs) sb.AppendLine($"  <Audio {v.AudioIndex}> [{v.CharacterName}]");

        /* 落点按引擎分开说：H3 是台词句里的独立声明；SD 的七段式栏位是写死的，
           多出一栏会触发机检的「七段式缺栏」，所以只能内联进时间轴的台词行。 */
        if (h3)
        {
            sb.AppendLine("  表内角色在本镜头开口时，其说话句必须写成：<Subject N> (Sx) 以参考 <Audio M> 的音色和说话方式说道，<d>[Chinese] 台词原文</d>；台词内容仍以源文本为准，音频只提供音色与说话方式；未在表中开口的角色不写 <Audio M>；<Audio M> 仅可用于音色/说话方式参考，禁止写成音乐风格、节拍或配乐参考。");
            /* <Audio M> 必须有对象：素材声明行 + audio_definitions 定义节，缺一就是悬空引用。
               以前只要求正文写 <Audio M>，素材说明里一张音频都没有——
               H3 读到 <Audio 1> 无从对应，音色等于白配。 */
            sb.AppendLine("  【素材声明·必写】在【参考素材说明】的 @图片N 行之后，按本表编号续写声明行：@音频M [角色名]音色参考，保持音色与说话方式一致；音频与图片是两套编号，不占用 @图片N 的号，也不计入首行「共 N 个输入素材」。");
            sb.AppendLine("  【定义节·必写】在 subject_definitions: 之后输出 audio_definitions: 节，逐行写：<Audio M> is the voice of [角色名]（音色参考：只提供音色与说话方式，不复制参考音频里的原话）.；本节是 <Audio M> 的唯一定义处，本镜无音色参考时整节省略。");
            sb.AppendLine("  【开口判定】只要该镜写了 <d> 标签就算开口，说话句必须带 <Audio M>——内心独白 OS、旁白、画外音都算；分镜 dialogue 为「无」但正文补出内心独白的同样按开口处理。只有全程沉默、不写 <d> 的角色才不引用。");
        }
        else
            sb.AppendLine("  表内角色在本镜头开口时，【时间轴分镜】里该镜的台词行必须带上音色标注，写法：台词：角色名“台词原文”，使用@音频M的音色说话，保留人声质感、情绪与语速，人物口型严格匹配台词；台词内容、时间码、景别机位仍以分镜为准，音频只提供音色与说话方式；未在表中开口的角色不写 @音频M；音色标注只许出现在台词行——禁止写进【音效】段，禁止写成配乐、音乐风格或节拍参考，也禁止为此新增任何段落（七段式的栏位一个都不能变）；@音频M 的编号最多到本表给出的段数，不许超出。");
        return sb.ToString();
    }

    /// <summary>
    /// 从分镜的「角色」字段里剥出纯角色名，好跟音色库对得上。
    /// 这个字段各项目写法不统一：有的带资产引用前缀（@CHR-杨彦刚），
    /// 有的是「名字——表情动作描述」，还有的干脆写「无」。
    /// 音色库存的是纯名字，不洗干净就一个都匹配不上。
    /// </summary>
    private static List<string> SplitCharacterNames(string? characters)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(characters)) return names;

        foreach (var part in characters.Split(new[] { '；', ';', '，', ',', '、' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length == 0 || name == "无") continue;

            // 资产引用前缀：@CHR-xxx / @CHAR_xxx / @角色:xxx 之类，剥掉只留名字
            var m = Regex.Match(name, @"^@?\s*(CHR|CHAR|角色)\s*[-_:：]?\s*(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) name = m.Groups[2].Value.Trim();

            // 「名字——描述」：破折号后面是表情动作，不属于名字
            var i = name.IndexOf("——", StringComparison.Ordinal);
            if (i <= 0) i = name.IndexOf('—');
            if (i > 0) name = name.Substring(0, i).Trim();

            if (name.Length > 0 && name != "无") names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// 按本次的「提示词类型」在系统提示末尾补一段覆盖指令。
    /// SD 与 H3 是两套互斥的写法，规则库却是一起加载的：P4 同时挂着 SD 专属规范
    /// （DocId 27/32，约 1.5 万字）和 H3 规范（DocId 40）。不明确点出本次走哪套，
    /// 模型必然被字数多的那套带跑——选了 H3 却照 SD 写七段式、写 model=seedance-2.0、
    /// 用 @图N 而不是 @图片N。这里把「本次走哪套、另一套不适用」说死。
    /// 没有「提示词类型」的阶段（P0~P3、P5）返回空串，不受影响。
    /// </summary>
    private static string PromptTypeOverride(string? inputText)
    {
        var t = PromptTypeOf(inputText);
        if (t == null) return "";

        if (t == "H3")
            return """
                   【本次生成目标：H3（MiniMax H3 · 本地 ComfyUI）】
                   「本阶段产出要求」里 SD 与 H3 二选一，本次是 H3。规则库里两套文档都在
                   （SD 专属规范与《MiniMax H3 参考生视频提示词规范》），冲突时以 H3 规范为准，
                   SD 那套本次一律不适用。
                   - 输出走 H3 的六节结构：【参考素材说明】→ subject_definitions → summary →
                     retention_analysis → detailed_description → overall_soundscape，
                     文末以 non_diegetic_music: N/A 与 非叙事性音乐: N/A 两行收尾。
                   - 不写 model= 行，不写 4K / 24fps / 浅景深 这类技术参数，
                     不套 SD 的时长档与 SD 参数行，不写 SD 七段式。
                   - 参考图写 @图片N（不是 @图N），台词写 <d>[Chinese] 原文</d>，音色写 <Audio N>。
                   - 每个镜头开头的【第X集】【单元X.Y】【镜头X.Y-N】三连标题照旧写：
                     系统靠它切块入库，与 H3 六节结构不冲突——标题之后接该镜的 H3 全文。
                   - 音色参考以每镜下方给出的【音色参考表】为唯一依据（最高优先级）：
                     表内角色开口时才写「以参考 <Audio M> 的音色和说话方式说道」，
                     编号与角色的对应关系逐条照抄，禁止改派、增删、换序；
                     没有给出音色参考表的镜头（或表里没有的角色）一律不写 <Audio M>，
                     禁止自行起编号——编号要对上提交时真正上传的那段音频，编错就是张冠李戴。
                     <Audio M> 只用于音色与说话方式，禁止写成音乐风格、节拍或配乐参考。
                   """;

        if (t == "SD")
            return """
                   【本次生成目标：SD（Seedance · 火山方舟）】
                   「本阶段产出要求」里 SD 与 H3 二选一，本次是 SD。规则库里同时挂着
                   《MiniMax H3 参考生视频提示词规范》，那是 H3 专用的，本次一律不适用：
                   不写 @图片N、不写 <Subject N>、不写 non_diegetic_music 行。
                   - 输出走 SD 的七段式：画幅风格 → 场景资产 → 核心人物 → 站位声明 →
                     时间轴分镜（末尾强制【接续状态】尾行）→ 音效 → 强制禁止项。
                   - 参考图写 @图N，首行句尾写 model=seedance-2.0。
                   - 音色写 @音频N（SD 的写法，不是 H3 的 <Audio N>）：与 H3 同一套编号，
                     以每镜下方【音色参考表】为唯一依据（最高优先级，禁止改派、增删或自造编号）。
                     只标在【时间轴分镜】的台词行里——台词：角色名“台词原文”，
                     使用@音频N的音色说话，保留人声质感、情绪与语速，人物口型严格匹配台词；
                     禁止写进【音效】段，禁止写成配乐、音乐风格或节拍参考，
                     也禁止为此新增段落：七段式的栏位一个都不能变。
                     未在表中开口的角色一律不标音色。
                   """;

        return "";
    }

    /// <summary>从输入里认出本次的提示词类型：H3 / SD；认不出（别的阶段）返回 null。</summary>
    private static string? PromptTypeOf(string? inputText)
    {
        if (string.IsNullOrWhiteSpace(inputText)) return null;
        var m = Regex.Match(inputText, @"提示词类型\s*[:：]\s*(\S+)");
        if (!m.Success) return null;

        return m.Success ? NormalizeEngine(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// 把立项里存的引擎值归一成 SD / H3，认不出返回 null，交给调用方回落。
    /// 立项那列可能存下拉的完整文本（"SD（Seedance·火山方舟）"），也可能就是短码，两种都认。
    /// </summary>
    private static string? NormalizeEngine(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (v.Contains("H3", StringComparison.OrdinalIgnoreCase)) return "H3";
        if (v.Contains("SD", StringComparison.OrdinalIgnoreCase)
            || v.Contains("Seedance", StringComparison.OrdinalIgnoreCase)) return "SD";
        return null;
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
    /// <param name="salvaged">true = 原本的 JSON 不合法，是靠截断残缺尾部救回来的，内容可能少几条。</param>
    private static string? TryExtractJson(string raw, out bool salvaged)
    {
        salvaged = false;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (s >= 0)
        {
            s += 7;
            var e = raw.IndexOf("```", s, StringComparison.Ordinal);
            if (e >= 0)
            {
                var body = raw[s..e].Trim();
                try { using var doc = JsonDocument.Parse(body); return doc.RootElement.GetRawText(); }
                catch
                {
                    /* 模型常常草率收尾：最后一个对象写完字段就直接闭合围栏，缺 } 和 ]，
                       整块 JSON 语法不合法。直接判失败就是整批入库 0 条——
                       前面二十多条镜头全白写，还得重跑一次。先试着救。 */
                    var fixedUp = SalvageTruncatedJson(body);
                    if (fixedUp != null) { salvaged = true; return fixedUp; }
                    return null;
                }
            }
        }

        /* 开了 JSON 模式后模型直接吐裸 JSON，没有围栏可找：
           可能是数组，也可能裹一层对象壳（{"assets":[...]}）。
           不认下来就退化成 md 兜底，而 md 兜底解析不了裸 JSON——整批入库 0 条。 */
        var t = raw.Trim();
        if (t.Length == 0 || (t[0] != '[' && t[0] != '{')) return null;
        try { using var doc = JsonDocument.Parse(t); return doc.RootElement.GetRawText(); }
        catch { return null; }
    }

    /// <summary>
    /// 截断残缺的 JSON 数组尾部，把前半段救成合法 JSON。
    /// 做法：从最后一个 } 往前逐个试，截断到那里再补 ]，能 Parse 通就收。
    /// 救回来的是完整对象，下游照常入库，代价只是丢掉末尾那 1~2 条没写完的镜头。
    /// </summary>
    private static string? SalvageTruncatedJson(string body)
    {
        if (!body.StartsWith("[")) return null;

        // 最多往前试 8 个对象：再多就是这块 JSON 本身写坏了，不是收尾草率
        for (var tried = 0; tried < 8; tried++)
        {
            var i = body.LastIndexOf('}');
            if (i <= 0) return null;

            var cut = body[..(i + 1)].TrimEnd().TrimEnd(',') + "]";
            try { using var doc = JsonDocument.Parse(cut); return doc.RootElement.GetRawText(); }
            catch { body = body[..i]; }   // 这个 } 是内层的（嵌在字符串或子对象里），砍掉再往前找
        }
        return null;
    }
}
