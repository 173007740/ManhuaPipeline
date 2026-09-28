using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

/// <summary>
/// Skill 包读写。这里跟其它配置类接口唯一的不同：它是<b>可写</b>的。
/// 因为「规则」本来就该能被改 —— 写死在代码里才需要发版，落成数据就能在线调。
/// </summary>
[ApiController]
[Route("api/director-skill")]
public class DirectorSkillController : ControllerBase
{
    private readonly DbService _db;

    public DirectorSkillController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet("packs")]
    public IActionResult GetPacks()
    {
        if (GetUserId() == 0) return Unauthorized();
        return Ok(_db.GetSkillPacks().Select(p => new
        {
            packId = p.PackId, packKey = p.PackKey, name = p.Name, version = p.Version,
            sourcePath = p.SourcePath, docCount = p.DocCount, chars = p.Chars
        }));
    }

    [HttpGet("packs/{packId:int}/docs")]
    public IActionResult GetDocs(int packId)
    {
        if (GetUserId() == 0) return Unauthorized();
        return Ok(_db.GetSkillDocs(packId).Select(d => new
        {
            docId = d.DocId, fileName = d.FileName, title = d.Title, scope = d.Scope,
            scopeValue = d.ScopeValue, isCore = d.IsCore, chars = d.Chars, updatedAt = d.UpdatedAt
        }));
    }

    [HttpGet("docs/{docId:int}")]
    public IActionResult GetDoc(int docId)
    {
        if (GetUserId() == 0) return Unauthorized();
        var d = _db.GetSkillDoc(docId);
        if (d == null) return NotFound(new { message = "文档不存在" });
        return Ok(new
        {
            docId = d.DocId, packId = d.PackId, fileName = d.FileName, title = d.Title,
            scope = d.Scope, scopeValue = d.ScopeValue, isCore = d.IsCore, content = d.Content
        });
    }

    /// <summary>保存规则正文与加载范围。改完下一次跑流水线就生效，不需要重启。</summary>
    [HttpPut("docs/{docId:int}")]
    public IActionResult SaveDoc(int docId, [FromBody] SaveDocBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Title)) return BadRequest(new { message = "标题不能为空" });
        if (body.Content == null) return BadRequest(new { message = "内容不能为空" });

        var scope = string.IsNullOrWhiteSpace(body.Scope) ? "ref" : body.Scope.Trim();
        if (scope != "always" && scope != "stage" && scope != "when" && scope != "ref")
            return BadRequest(new { message = "加载范围只能是 always / stage / when / ref" });

        var sv = string.IsNullOrWhiteSpace(body.ScopeValue) ? null : body.ScopeValue.Trim();
        if (scope == "stage" && string.IsNullOrEmpty(sv))
            return BadRequest(new { message = "stage 必须指定阶段，如 P2" });
        if (scope == "when" && string.IsNullOrEmpty(sv))
            return BadRequest(new { message = "when 必须指定触发条件，如 战斗" });

        return _db.SaveSkillDoc(docId, body.Title.Trim(), scope, scope == "stage" || scope == "when" ? sv : null, body.Content)
            ? Ok(new { ok = true })
            : NotFound(new { message = "文档不存在" });
    }

    public sealed class SaveDocBody
    {
        public string Title { get; set; } = "";
        public string Scope { get; set; } = "";
        public string? ScopeValue { get; set; }
        public string? Content { get; set; }
    }

    [HttpGet("packs/{packId:int}/stages")]
    public IActionResult GetStages(int packId)
    {
        if (GetUserId() == 0) return Unauthorized();
        return Ok(_db.GetSkillStages(packId).Select(s => new
        {
            stageId = s.StageId, stageKey = s.StageKey, name = s.Name, sortOrder = s.SortOrder,
            docFilter = s.DocFilter, inputsJson = s.InputsJson, outputContract = s.OutputContract,
            gates = s.Gates, humanConfirm = s.HumanConfirm, isEnabled = s.IsEnabled,
            outputTarget = s.OutputTarget
        }));
    }

    /// <summary>保存阶段编排：加载哪些规则、收集哪些输入、产出什么格式、什么条件放行。</summary>
    [HttpPut("stages/{stageId:int}")]
    public IActionResult SaveStage(int stageId, [FromBody] SaveStageBody body)
    {
        if (GetUserId() == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { message = "阶段名不能为空" });
        if (!string.IsNullOrWhiteSpace(body.InputsJson))
        {
            try { System.Text.Json.JsonDocument.Parse(body.InputsJson!); }
            catch { return BadRequest(new { message = "输入表单不是合法 JSON" }); }
        }
        return _db.SaveSkillStage(stageId, body.Name.Trim(), body.DocFilter, body.InputsJson,
                                  body.OutputContract, body.Gates, body.HumanConfirm, body.IsEnabled,
                                  body.OutputTarget)
            ? Ok(new { ok = true })
            : NotFound(new { message = "阶段不存在" });
    }

    public sealed class SaveStageBody
    {
        public string Name { get; set; } = "";
        public string? DocFilter { get; set; }
        public string? InputsJson { get; set; }
        public string? OutputContract { get; set; }
        public string? Gates { get; set; }
        public bool HumanConfirm { get; set; }
        public bool IsEnabled { get; set; } = true;
        /// <summary>产出落到哪：assets / asset_prompts / frames / prompts，留空则不入库。</summary>
        public string? OutputTarget { get; set; }
    }
}
