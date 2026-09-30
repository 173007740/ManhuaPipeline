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
        _db.UpdateSkillRun(runId, body.StageKey, "running");

        _ = Task.Run(async () =>
        {
            try { await _agent.RunFromAsync(uid, runId, body.StageKey, body.InputText ?? "",
                                            body.ProjectId, body.EpisodeId, body.StopStageKey); }
            catch (Exception ex)
            {
                _log.LogError(ex, "run-all failed run={RunId} stage={Stage}", runId, body.StageKey);
                _db.UpdateSkillRun(runId, body.StageKey, "error");
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
            charactersJson = b.CharactersJson, promptEngine = b.PromptEngine
        });
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
            body.EpisodeDuration, body.CharactersJson, body.PromptEngine);
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
