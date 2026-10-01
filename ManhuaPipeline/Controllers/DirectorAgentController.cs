using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

/// <summary>
/// 流水线执行接口。
/// 只有四个动作：预览（跑之前先看烧多少）、跑一步、确认放行、看进度。
/// 「跑什么」不在这里定义，在 DirectorSkillStages 里。
/// </summary>
[ApiController]
[Route("api/director-agent")]
public class DirectorAgentController : ControllerBase
{
    private readonly DbService _db;
    private readonly DirectorAgentService _agent;
    private readonly ILogger<DirectorAgentController> _log;

    public DirectorAgentController(DbService db, DirectorAgentService agent, ILogger<DirectorAgentController> log)
    { _db = db; _agent = agent; _log = log; }

    /// <summary>正在跑的运行，防重复启动。一次连跑十几分钟，同一次运行不该被点两下。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _running = new();

    /// <summary>
    /// 交付物列表：立项、剧本、资产清单、图册、质检这几步的产出。
    /// 它们不落到业务表，以前只能躺在步骤记录里没人看，现在有个正式的出口。
    /// </summary>
    [HttpGet("deliverables")]
    public IActionResult Deliverables([FromQuery] int projectId)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (projectId <= 0) return Ok(new { items = Array.Empty<object>() });
        return Ok(new
        {
            items = _db.GetDeliverables(projectId).Select(d => new
            {
                deliverableId = d.DeliverableId, stageKey = d.StageKey, title = d.Title,
                version = d.Version, createdAt = d.CreatedAt
            })
        });
    }

    [HttpGet("deliverables/{id:int}")]
    public IActionResult DeliverableContent(int id)
    {
        if (GetUserId() == 0) return Unauthorized();
        return Ok(new { content = _db.GetDeliverableContent(id) });
    }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    /// <summary>代价说明：这一步错了要赔多少。门禁弹窗和预览面板都用它，避免「确认」变成无信息的点头。</summary>
    private static object Cost(DirectorAgentService.StepCost c) => new
    {
        promptTokens = c.PromptTokens,
        rerunTokens = c.RerunTokens,
        downstreamCount = c.DownstreamCount,
        downstreamTokens = c.DownstreamTokens
    };

    /// <summary>
    /// 跑之前的预览：这一阶段会加载哪些规则、prompt 多长、大约多少 token、要不要人工确认。
    /// 存在的理由很实在——一次调用可能几万 token，不该在用户不知情的情况下就烧掉。
    /// </summary>
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] PreviewBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        var stage = _db.GetSkillStages(body.PackId).FirstOrDefault(s => s.StageKey == body.StageKey);
        if (stage == null) return NotFound(new { message = "阶段不存在" });
        var p = _agent.Preview(body.PackId, body.StageKey, stage);
        return Ok(new
        {
            stageKey = body.StageKey,
            promptChars = p.PromptChars,
            estTokens = p.EstTokens,
            humanConfirm = p.HumanConfirm,
            gates = p.Gates,
            inputsJson = p.InputsJson,
            docs = p.Docs.Select(d => new { title = d.Title, chars = d.Chars }),
            cost = Cost(p.Cost)
        });
    }

    [HttpPost("runs")]
    public IActionResult CreateRun([FromBody] CreateRunBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        // 同一项目上一次还没跑完就再来一次，只会多出一份重复的立项/剧本。
        // 宁可在这里挡下来让他先处理完，也不要事后让他从一堆同名交付物里挑哪个是新的
        var running = _db.FindUnfinishedSkillRun(body.PackId, body.ProjectId);
        if (running != null)
            return Conflict(new
            {
                message = $"这个项目还有一次没跑完的运行（#{running}）。先把它处理完；确实要重来，就点「新开一次」把那次丢掉。",
                runId = running
            });
        var id = _db.CreateSkillRun(body.PackId, body.ProjectId, body.EpisodeId, body.Title, body.InputsJson);
        return Ok(new { runId = id });
    }

    /// <summary>丢弃一次没跑完的运行。跑出来的产出已经进交付物表，不会跟着丢。</summary>
    [HttpPost("runs/{runId:int}/abort")]
    public IActionResult AbortRun(int runId)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (_running.ContainsKey(runId)) return Ok(new { ok = false, message = "这次运行正在跑，停不下来" });
        _db.AbandonSkillRun(runId);
        return Ok(new { ok = true });
    }

    /// <summary>跑一步。门禁阶段（HumanConfirm=1）跑完会停在 await_confirm，等人确认。</summary>
    [HttpPost("runs/{runId:int}/steps")]
    public async Task<IActionResult> RunStep(int runId, [FromBody] RunStepBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        try
        {
            var r = await _agent.RunStepAsync(GetUserId(), runId, body.StageKey, body.InputText ?? "",
                                              body.ProjectId, body.EpisodeId);
            return Ok(new
            {
                stepId = r.StepId, stageKey = r.StageKey, status = r.Status,
                output = r.Output, promptChars = r.PromptChars, estTokens = r.EstTokens,
                gates = r.Gates, cost = Cost(r.Cost), imported = r.Imported, importError = r.ImportError
            });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
    }

    /// <summary>
    /// 从某阶段起一路往下跑，遇到人工确认阶段自动停下。
    /// 后台跑、前端轮询进度：一次可能十几分钟，不能让 HTTP 请求干等，也不能让浏览器转圈转到超时。
    /// </summary>
    [HttpPost("runs/{runId:int}/run-all")]
    public IActionResult RunAll(int runId, [FromBody] RunStepBody body)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return StartRun(uid, runId, body.StageKey, body.InputText, body.ProjectId, body.EpisodeId, body.StopStageKey);
    }

    /// <summary>点火一次并立刻返回：状态先落库，后台接着跑（run-all / continue / 按底本跑剧本共用）。</summary>
    private IActionResult StartRun(int uid, int runId, string? stageKey, string? inputText,
                                   int? projectId, int? episodeId, string? stopStageKey)
    {
        /* 只拦「已经作废的那次」和「正在跑的那次」。
           跑完（done）的那次允许再点火——页面上「重跑 · 生成场景提示词」这类按钮点下去
           就是要把这一件事再做一遍，挡掉它等于点了没反应。
           而且必须记在同一次运行里：这一站的上游（立项 / 剧本 / P2b 资产台账）都挂在这次运行上，
           换成新开一次运行它们全是空的，模型手里没有台账就只能照剧本编场景名，
           回填时一条也对不上、入库 0 条（LedgerSourceOf 规定 P2c 三批一律取 P2b）。 */
        var st = _db.GetSkillRunStatus(runId);
        if (st is "abandoned")
            return Ok(new { started = false, message = "这次运行已经作废了，要再来请「新开一次」" });
        if (!_running.TryAdd(runId, 0)) return Ok(new { started = false, message = "这次运行正在跑" });

        // 先把状态落库再动手。后台任务要等排上队、跑到第一个阶段才会写 running，
        // 前端点火后立刻轮询到的会是点火前那个状态，于是判定「没在跑」把轮询停掉——
        // 按钮就一直停在「重跑」，刷新页面才变。这里同步写一次，让点火和可见状态同一时刻发生
        _db.UpdateSkillRun(runId, stageKey, "running");

        _ = Task.Run(async () =>
        {
            try { await _agent.RunFromAsync(uid, runId, stageKey ?? "", inputText ?? "",
                                            projectId, episodeId, stopStageKey); }
            catch (Exception ex)
            {
                _log.LogError(ex, "run failed run={RunId} stage={Stage}", runId, stageKey);
                _db.UpdateSkillRun(runId, stageKey, "error");
            }
            finally { _running.TryRemove(runId, out _); }
        });
        return Ok(new { started = true });
    }

    /// <summary>确认卡点，然后接着往下跑到下一个卡点。</summary>
    [HttpPost("runs/{runId:int}/continue")]
    public IActionResult Continue(int runId, [FromBody] RunStepBody? body)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_running.TryAdd(runId, 0)) return Ok(new { started = false, message = "这次运行正在跑" });

        // 同 RunAll：状态先落库，别让前端点火后的第一次轮询读到「await_confirm」就收摊
        _db.UpdateSkillRun(runId, null, "running");

        _ = Task.Run(async () =>
        {
            try { await _agent.ContinueAsync(uid, runId, body?.StopStageKey); }
            catch (Exception ex)
            {
                _log.LogError(ex, "continue failed run={RunId}", runId);
                _db.UpdateSkillRun(runId, null, "error");
            }
            finally { _running.TryRemove(runId, out _); }
        });
        return Ok(new { started = true });
    }

    [HttpPost("steps/{stepId:int}/confirm")]
    public IActionResult Confirm(int stepId)
    {
        if (GetUserId() == 0) return Unauthorized();
        _agent.ConfirmStep(stepId);
        return Ok(new { ok = true });
    }

    [HttpGet("runs/{runId:int}")]
    public IActionResult GetRun(int runId)
    {
        if (GetUserId() == 0) return Unauthorized();
        var runs = _db.GetSkillRuns(_db.GetRunPackId(runId));
        var run = runs.FirstOrDefault(r => r.RunId == runId);
        if (run == null) return NotFound(new { message = "运行不存在" });
        return Ok(RunPayload(run));
    }

    /// <summary>
    /// 取漫剧级阶段（立项 P0）的产出记录。
    /// 立项整部漫剧一份：立完项按集数生成 N 个集项目，每一集打开都得看得到它，
    /// 而不是只有跑过的那一集有——其余集没有自己的运行，那一格就空着。
    ///
    /// 立项结果就存在 Dramas 那一行上（画幅、集数那些立项字段也在那儿），
    /// 所以按 dramaId 一次读出来即可：不用上溯项目、不用跨到别的集的运行里翻。
    /// </summary>
    [HttpGet("shared-step")]
    public IActionResult GetSharedStep([FromQuery] int projectId, [FromQuery] string? stageKey,
                                       [FromQuery] int dramaId = 0)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(stageKey))
            return NotFound(new { message = "缺少 stageKey" });
        // 立项是漫剧级的：给了 dramaId 就按漫剧直接查，不用从项目上溯
        if (projectId <= 0 && dramaId <= 0)
            return NotFound(new { message = "缺少 projectId 或 dramaId" });

        var did = dramaId > 0 ? dramaId : _db.GetDramaIdByProject(projectId);

        // 立项（P0）：读漫剧那一行上的立项结果
        if (did > 0 && string.Equals(stageKey, "P0", StringComparison.OrdinalIgnoreCase))
        {
            var p0 = _db.GetDramaP0Result(did);
            if (p0 != null)
                return Ok(new { stepId = p0.StepId, stageKey, episodeNumber = 0,
                                finishedAt = p0.FinishedAt, dramaId = did });
        }

        /* 走到这儿是两件事：还没回填进漫剧的老数据，或者查的不是立项。
           这两种只能回到运行里找——产出挂在运行上，运行按集绑。 */
        var f = dramaId > 0 ? _db.GetSharedStageStepByDrama(dramaId, stageKey)
                            : _db.GetSharedStageStep(projectId, stageKey);
        if (f == null) return NotFound(new { message = "这部漫剧还没跑过这一步" });
        return Ok(new { stepId = f.Value.StepId, stageKey, episodeNumber = f.Value.EpisodeNumber });
    }

    /// <summary>
    /// 按项目取最近一次运行，返回的东西跟 runs/{runId} 一模一样。
    /// 页面「接上次运行」以前只查浏览器 localStorage，记着就接、没记着就当没跑过——
    /// 于是换个浏览器打开 ?projectId=57 看到的是空页面，其实那次运行的产出都在库里。
    /// </summary>
    [HttpGet("runs/latest")]
    public IActionResult GetLatestRun([FromQuery] int projectId)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (projectId <= 0) return NotFound(new { message = "缺少 projectId" });
        var run = _db.GetLatestSkillRunByProject(projectId);
        if (run == null) return NotFound(new { message = "这个项目还没跑过" });
        return Ok(RunPayload(run));
    }

    /// <summary>一次运行画成给页面看的样子。两个「取运行」的接口共用，免得结构跑偏。</summary>
    private object RunPayload(Services.DbService.SkillRunRow run)
    {
        return new
        {
            runId = run.RunId, packId = run.PackId, projectId = run.ProjectId, title = run.Title,
            currentStage = run.CurrentStage, status = run.Status, createdAt = run.CreatedAt,
            // 建运行时选定的「提示词引擎」存在这里。页面接上上次运行要靠它把下拉恢复成
            // 真正生效的那个值——不回读的话下拉永远显示默认的 SD，人选了 H3 也会被当成没选。
            inputsJson = run.InputsJson,
            steps = _db.GetSkillSteps(run.RunId).Select(s => new
            {
                stepId = s.StepId, stageKey = s.StageKey, name = s.Name, status = s.Status,
                promptChars = s.PromptChars, gates = s.Gates, error = s.Error,
                confirmedAt = s.ConfirmedAt,
                imported = s.ImportedCount, importError = s.ImportError,
                hasOutput = !string.IsNullOrEmpty(s.OutputText)
            })
        };
    }

    /// <summary>
    /// 取立项。立项是漫剧级的：一部漫剧一份，画幅、集数、提示词引擎都在 Dramas 那一行里，
    /// 改哪个字段都不用重跑流水线。P4 生成提示词时直接读它的 PromptEngine。
    /// 传 projectId 时会先上溯到所属漫剧——流水线一次只跑一集，手里拿的是项目不是漫剧。
    /// </summary>
    [HttpGet("brief")]
    public IActionResult GetBrief([FromQuery] int projectId = 0, [FromQuery] int dramaId = 0)
    {
        if (GetUserId() == 0) return Unauthorized();

        var did = dramaId > 0 ? dramaId : _db.GetDramaIdByProject(projectId);
        if (did <= 0) return BadRequest(new { message = "缺少 projectId 或 dramaId" });

        var b = _db.GetDramaBrief(did);
        if (b == null) return Ok(new { exists = false });
        return Ok(new
        {
            exists = true, dramaId = b.DramaId,
            status = b.Status, aspect = b.Aspect, delivery = b.Delivery, genre = b.Genre,
            artStyleId = b.ArtStyleId, hook = b.Hook, premise = b.Premise, platform = b.Platform,
            episodeCount = b.EpisodeCount, episodeDuration = b.EpisodeDuration,
            charactersJson = b.CharactersJson, promptEngine = b.PromptEngine,
            videoStyleId = b.VideoStyleId
        });
    }

    /// <summary>
    /// 取这一集的剧本正本 + 它是谁写的。
    /// 页面上「用我自己的剧本」那一格要把已存的剧本读回来，也要说清现在这份是模型写的还是人自己写的。
    /// </summary>
    [HttpGet("script")]
    public IActionResult GetScript([FromQuery] int projectId)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (projectId <= 0) return BadRequest(new { message = "缺少 projectId" });
        var s = _db.GetProjectScriptContent(projectId);
        if (!string.IsNullOrWhiteSpace(s))
            return Ok(new { script = s!, source = _db.GetProjectScriptSource(projectId) ?? "", chars = s!.Length });

        /* 这一集还没有剧本时，把整部素材（建 / 改漫剧时录的那份，存在 Dramas 上）递过去：
           人不必为了同一份剧本在每一集里粘一遍。只做预填，不自动喂给模型 ——
           12 集每一集都拿整部去跑，等于每集都在写全集。 */
        var did = _db.GetDramaIdByProject(projectId);
        if (did <= 0) return Ok(new { script = "", source = "", chars = 0 });
        var drama = _db.GetDramaScriptContent(did);
        if (string.IsNullOrWhiteSpace(drama)) return Ok(new { script = "", source = "", chars = 0 });
        return Ok(new { script = drama!, source = "drama", chars = drama!.Length, wholeDrama = true });
    }

    public sealed class ScriptImportBody
    {
        public int ProjectId { get; set; }
        public string? Script { get; set; }
    }

    /// <summary>
    /// 用我自己的剧本：把人写好的剧本直接接上流水线。
    ///
    /// 为什么要有这个接口：剧本正本（Projects.ScriptContent）在流水线上根本没人读——
    /// 下游（P2a 提取资产 / P3 分镜 / P4 提示词）一律从本次运行的 P1 步骤产出取剧本，
    /// 而 P1 跑完又会拿模型产出把人粘进去的那份整篇盖掉。
    /// 于是「我自己写剧本」这件事在页面上看起来能填、存得进去，实际一步也接不上。
    ///
    /// 这里把它直接落成本次运行的 P1 产出（status=done）：下游照旧取 P1，取到的就是人这一份；
    /// 同时写回剧本正本并标成 user（P1 跑完不再覆盖，见 SkillOutputImporter）。
    /// 之后想换成模型写的，点「重跑 · 剧本生成」即可——那次会把来源改回 ai。
    ///
    /// 改剧本是常事（改两句台词、调一场顺序，再往下跑一遍看效果），所以第二次起不新插一条：
    /// 改十次就堆十条 P1 步骤，而下游只认最新那条，前面九条全是垃圾。直接改上次那条记录。
    /// 每一次的原文仍存进交付物（Version 递增），要回看上一版还能找到。
    /// </summary>
    [HttpPost("runs/{runId:int}/script")]
    public IActionResult ImportScript(int runId, [FromBody] ScriptImportBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        var text = (body?.Script ?? "").Trim();
        if (text.Length == 0) return BadRequest(new { message = "剧本是空的" });

        var pid = body!.ProjectId > 0 ? body.ProjectId : _db.GetRunContext(runId).ProjectId;
        if (pid <= 0) return BadRequest(new { message = "这次运行没挂项目，先在左边选好落点项目" });

        // 剧本正本：老流水线阶段 3/4/6/7/8 与「补资产提示词」都读这一列
        _db.UpdateProjectScript(pid, GetUserId(), text);

        // 本次运行的 P1 产出：这才是下游真正会读的那一份
        var stepId = _db.AttachUserScriptStep(runId, pid, text);

        return Ok(new { stepId = stepId, chars = text.Length });
    }

    /// <summary>
    /// 我的剧本当底本，让模型整理成稿（另一种「我自己写剧本」的方式）。
    ///
    /// 跟上面那条的区别：上面是「这就是终稿，别动」，这条是「这是素材，你按一步 step 的产出契约整理成
    /// 能往下用的剧本」。人写的东西常常不合流水线格式（缺页头四行、场号不是 [1内]·[地点]-[日] 这种、
    /// 没有人物关系小节、缺节奏秒数），下游 P2a/P3 是按这些结构解析的，格式不合等于跑不出来。
    /// 反过来全交给模型写，内容就不是他要的。这条接口是中间那条路：内容归人、格式归模型。
    ///
    /// 防它改内容的办法写在指令里：明说哪些必须原样保留、哪些才是它该补的，
    /// 并要求产出末尾交一份「改动清单」——人一眼能看出它改了什么，改多了就退回来用上面那种方式。
    /// </summary>
    [HttpPost("runs/{runId:int}/script/draft")]
    public IActionResult RunScriptFromDraft(int runId, [FromBody] ScriptImportBody body)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var draft = (body?.Script ?? "").Trim();
        if (draft.Length == 0) return BadRequest(new { message = "剧本是空的" });

        var pid = body!.ProjectId > 0 ? body.ProjectId : _db.GetRunContext(runId).ProjectId;
        if (pid <= 0) return BadRequest(new { message = "这次运行没挂项目，先在左边选好落点项目" });

        /* 底本模式这一次是让模型照着重写一版剧本：来源先标成 draft。
           标了之后，往后哪怕只是点第 2 步「写剧本」，P1 也会自动把这份当作素材带入
           （见 DirectorAgentService.ExecuteStepAsync 里的 P1 注入）——
           不用每次都从这一格重新点一遍。
           这里不标 ai：标 ai 就等于说「这份是模型写的」，下次无从知道人交过底本。
           人要继续改这份，改的时候又会标回 user（终稿）；整理好的那一版落地时由 P1 覆盖成 ai。 */
        _db.SetProjectScriptSource(pid, "draft");

        return StartRun(uid, runId, "P1", DirectorAgentService.DraftInputText(draft), pid, null, "P1");
    }

    /// <summary>
    /// 存立项。只传要改的字段，没传的保持原样——
    /// 所以只想换提示词引擎时，请求体里放一个 promptEngine 就够了，别的字段原样不动。
    /// </summary>
    [HttpPut("brief")]
    public IActionResult SaveBrief([FromBody] BriefBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (body == null) return BadRequest(new { message = "缺少请求体" });

        var did = body.DramaId > 0 ? body.DramaId : _db.GetDramaIdByProject(body.ProjectId);
        if (did <= 0) return BadRequest(new { message = "缺少 projectId 或 dramaId" });

        var id = _db.UpsertDramaBrief(did, body.Status, body.Aspect, body.Delivery, body.Genre,
            body.ArtStyleId, body.Hook, body.Premise, body.Platform, body.EpisodeCount,
            body.EpisodeDuration, body.CharactersJson, body.PromptEngine, body.VideoStyleId);
        return Ok(new { dramaId = id });
    }

    public sealed class BriefBody
    {
        public int DramaId { get; set; }
        /// <summary>页面上拿到的是项目（一集一个），立项要按它上溯到漫剧。</summary>
        public int ProjectId { get; set; }
        public string? Status { get; set; }
        public string? Aspect { get; set; }
        public string? Delivery { get; set; }
        public string? Genre { get; set; }
        public int? ArtStyleId { get; set; }
        public string? Hook { get; set; }
        public string? Premise { get; set; }
        public string? Platform { get; set; }
        public int? EpisodeCount { get; set; }
        public int? EpisodeDuration { get; set; }
        public string? CharactersJson { get; set; }
        public string? PromptEngine { get; set; }
        /// <summary>视觉风格（VideoStyles）：整部漫剧的影像调性。跟 ArtStyleId（图片风格）各管一段。</summary>
        public int? VideoStyleId { get; set; }
    }

    /// <summary>取某一步的完整产出（原文 + 结构化结果 + 本次实际加载了哪些规则）。</summary>
    [HttpGet("steps/{stepId:int}/output")]
    public IActionResult GetStepOutput(int stepId)
    {
        if (GetUserId() == 0) return Unauthorized();
        var o = _db.GetStepOutput(stepId);
        if (o == null) return NotFound(new { message = "步骤不存在" });
        return Ok(new
        {
            stageKey = o.Value.StageKey,
            output = o.Value.OutputText,
            outputJson = o.Value.OutputJson,
            docsSummary = o.Value.DocsSummary
        });
    }

    public sealed class PreviewBody { public int PackId { get; set; } public string StageKey { get; set; } = ""; }
    public sealed class CreateRunBody
    {
        public int PackId { get; set; }
        public int? ProjectId { get; set; }
        public int? EpisodeId { get; set; }
        public string? Title { get; set; }
        public string? InputsJson { get; set; }
    }
    public sealed class RunStepBody
    {
        public string StageKey { get; set; } = "";
        public string? InputText { get; set; }
        /// <summary>跑到这个阶段为止就收手。五步里的每一步单独点，不该替人越过这一步往下做。</summary>
        public string? StopStageKey { get; set; }
        /// <summary>本次落点。以请求带的为准，页面上改了项目/剧集立刻生效。</summary>
        public int? ProjectId { get; set; }
        public int? EpisodeId { get; set; }
    }
}
