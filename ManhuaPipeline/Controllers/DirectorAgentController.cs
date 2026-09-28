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

    public DirectorAgentController(DbService db, DirectorAgentService agent) { _db = db; _agent = agent; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

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
            docs = p.Docs.Select(d => new { title = d.Title, chars = d.Chars })
        });
    }

    [HttpPost("runs")]
    public IActionResult CreateRun([FromBody] CreateRunBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        var id = _db.CreateSkillRun(body.PackId, body.ProjectId, body.Title, body.InputsJson);
        return Ok(new { runId = id });
    }

    /// <summary>跑一步。门禁阶段（HumanConfirm=1）跑完会停在 await_confirm，等人确认。</summary>
    [HttpPost("runs/{runId:int}/steps")]
    public async Task<IActionResult> RunStep(int runId, [FromBody] RunStepBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        try
        {
            var r = await _agent.RunStepAsync(GetUserId(), runId, body.StageKey, body.InputText ?? "");
            return Ok(new
            {
                stepId = r.StepId, stageKey = r.StageKey, status = r.Status,
                output = r.Output, promptChars = r.PromptChars, estTokens = r.EstTokens, gates = r.Gates
            });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) { return StatusCode(500, new { message = ex.Message }); }
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
        public string? Title { get; set; }
        public string? InputsJson { get; set; }
    }
    public sealed class RunStepBody { public string StageKey { get; set; } = ""; public string? InputText { get; set; } }
}
