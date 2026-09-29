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
        // 已经跑完或被丢掉的那次不许再点火：重跑一遍只会多一份同名产出
        var st = _db.GetSkillRunStatus(runId);
        if (st is "done" or "abandoned")
            return Ok(new { started = false, message = $"这次运行已经结掉了（{st}），要重来请「新开一次」" });
        if (!_running.TryAdd(runId, 0)) return Ok(new { started = false, message = "这次运行正在跑" });

        _ = Task.Run(async () =>
        {
            try { await _agent.RunFromAsync(uid, runId, body.StageKey, body.InputText ?? "", body.ProjectId, body.EpisodeId); }
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
    public IActionResult Continue(int runId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_running.TryAdd(runId, 0)) return Ok(new { started = false, message = "这次运行正在跑" });

        _ = Task.Run(async () =>
        {
            try { await _agent.ContinueAsync(uid, runId); }
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
        return Ok(new
        {
            runId = run.RunId, packId = run.PackId, projectId = run.ProjectId, title = run.Title,
            currentStage = run.CurrentStage, status = run.Status, createdAt = run.CreatedAt,
            steps = _db.GetSkillSteps(runId).Select(s => new
            {
                stepId = s.StepId, stageKey = s.StageKey, name = s.Name, status = s.Status,
                promptChars = s.PromptChars, gates = s.Gates, error = s.Error,
                confirmedAt = s.ConfirmedAt,
                imported = s.ImportedCount, importError = s.ImportError,
                hasOutput = !string.IsNullOrEmpty(s.OutputText)
            })
        });
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
        /// <summary>本次落点。以请求带的为准，页面上改了项目/剧集立刻生效。</summary>
        public int? ProjectId { get; set; }
        public int? EpisodeId { get; set; }
    }
}
