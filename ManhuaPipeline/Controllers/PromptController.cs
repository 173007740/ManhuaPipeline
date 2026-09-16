using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/project/{projectId}/prompt")]
public class PromptController : ControllerBase
{
    private readonly DbService _db;
    public PromptController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private IActionResult? CheckProjectAccess(int projectId)
    {
        var userId = GetUserId();
        if (userId == 0) return Unauthorized();
        return _db.ProjectBelongsToUser(projectId, userId)
            ? null
            : NotFound(new { message = "项目不存在" });
    }

    private IActionResult? CheckPromptAccess(int projectId, int promptId)
    {
        var projectAccess = CheckProjectAccess(projectId);
        if (projectAccess != null) return projectAccess;
        return _db.PromptBelongsToProject(promptId, projectId)
            ? null
            : NotFound(new { message = "提示词不存在" });
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetAll(int projectId)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        // L5：读取前按当前资产出图情况重算逐镜状态（ready / blocked_by_missing_asset）。
        // accepted / rejected 是人工结论，不会被自动重算覆盖。
        _db.SyncPromptShotStatusFromAssets(projectId);
        return Ok(_db.GetPrompts(projectId));
    }

    [HttpPost("import-excel")]
    public async Task<IActionResult> ImportExcel(int projectId, IFormFile file)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (file == null || file.Length == 0) return BadRequest(new { message = "请选择 Excel 文件" });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx") return BadRequest(new { message = "仅支持 .xlsx 格式（老版 .xls 请先另存为 .xlsx）" });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;

        var result = ExcelPromptParser.Parse(ms);
        if (result.Prompts.Count == 0)
            return BadRequest(new { message = "未解析到任何有效提示词", warnings = result.Warnings });

        // 导入为全量覆盖：先清空当前项目下的所有提示词，再插入新数据。
        _db.DeleteAllSeedancePrompts(projectId);

        // 显式把当前项目 ID 关联到每条提示词上。
        foreach (var p in result.Prompts) p.ProjectId = projectId;
        _db.InsertSeedancePrompts(projectId, result.Prompts);
        return Ok(new { imported = result.Prompts.Count, warnings = result.Warnings });
    }

    [HttpPost]
    public IActionResult Create(int projectId, [FromBody] CreatePromptRequest req)
    {
        var access = CheckProjectAccess(projectId);
        if (access != null) return access;
        if (string.IsNullOrWhiteSpace(req.PromptText)) return BadRequest(new { message = "提示词不能为空" });
        var id = _db.AddPrompt(projectId, req.PromptText.Trim(), req.EpisodeNumber, req.UnitName ?? "", req.ShotLabel ?? "", req.Duration, req.ShotType ?? "");
        return Ok(new { promptId = id, message = "添加成功" });
    }

    [HttpPut("{promptId}")]
    public IActionResult Update(int projectId, int promptId, [FromBody] UpdatePromptRequest req)
    {
        var access = CheckPromptAccess(projectId, promptId);
        if (access != null) return access;
        _db.UpdatePromptText(promptId, req.PromptText);
        if (req.PromptTextH3 != null) _db.UpdatePromptTextH3(promptId, req.PromptTextH3);
        return Ok(new { message = "保存成功" });
    }

    [HttpGet("{promptId}/refs")]
    public IActionResult GetRefs(int projectId, int promptId)
    {
        var access = CheckPromptAccess(projectId, promptId);
        if (access != null) return access;
        var prompt = _db.GetPrompt(promptId);
        if (prompt == null) return NotFound(new { message = "提示词不存在" });
        try
        {
            var refs = string.IsNullOrEmpty(prompt.ReferenceImages)
                ? new List<string>()
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(prompt.ReferenceImages);
            var refVids = string.IsNullOrEmpty(prompt.ReferenceVideos)
                ? new List<string>()
                : System.Text.Json.JsonSerializer.Deserialize<List<string>>(prompt.ReferenceVideos);
            return Ok(new { referenceImages = refs ?? new List<string>(), referenceVideos = refVids ?? new List<string>() });
        }
        catch
        {
            return Ok(new { referenceImages = new List<string>() });
        }
    }

    [HttpDelete("{promptId}")]
    public IActionResult Delete(int projectId, int promptId)
    {
        var access = CheckPromptAccess(projectId, promptId);
        if (access != null) return access;
        _db.DeletePrompt(promptId);
        return Ok(new { message = "删除成功" });
    }

    [HttpPut("{promptId}/shotlabel")]
    public IActionResult UpdateShotLabel(int projectId, int promptId, [FromBody] UpdateShotLabelRequest req)
    {
        var access = CheckPromptAccess(projectId, promptId);
        if (access != null) return access;
        _db.UpdatePromptShotLabel(promptId, req.ShotLabel);
        return Ok(new { message = "修改成功" });
    }

    [HttpPut("{promptId}/duration")]
    public IActionResult UpdateDuration(int projectId, int promptId, [FromBody] UpdateDurationRequest req)
    {
        var access = CheckPromptAccess(projectId, promptId);
        if (access != null) return access;
        _db.UpdatePromptDuration(promptId, req.Duration);
        return Ok(new { message = "修改成功" });
    }

    /// <summary>
    /// L5 逐镜状态机：人工置位验收状态。
    /// accepted（通过）/ rejected（打回）会被持久保留；ready 表示交回自动判定（按资产出图情况重算为 ready 或 blocked_by_missing_asset）。
    /// </summary>
    [HttpPut("{promptId}/shot-status")]
    public IActionResult UpdateShotStatus(int projectId, int promptId, [FromBody] UpdateShotStatusRequest req)
    {
        var access = CheckPromptAccess(projectId, promptId);
        if (access != null) return access;

        var status = (req.ShotStatus ?? "").Trim().ToLowerInvariant();
        if (status != "accepted" && status != "rejected" && status != "ready")
            return BadRequest(new { message = "状态只能是 accepted / rejected / ready" });

        _db.UpdatePromptShotStatus(promptId, status);
        if (status == "ready")
        {
            // 交回自动判定：立即按当前资产出图情况重算，避免前端显示 ready 但实际被缺图阻塞
            _db.SyncPromptShotStatusFromAssets(projectId);
        }
        return Ok(new { message = "已更新", shotStatus = status });
    }
}

public class CreatePromptRequest
{
    public string PromptText { get; set; } = "";
    public int EpisodeNumber { get; set; }
    public string? UnitName { get; set; }
    public string? ShotLabel { get; set; }
    public int Duration { get; set; } = 10;
    public string? ShotType { get; set; }
}

public class UpdateDurationRequest
{
    public int Duration { get; set; } = 11;
}
public class UpdatePromptRequest
{
    public string PromptText { get; set; } = "";
    public string? PromptTextH3 { get; set; }
}

public class UpdateShotLabelRequest
{
    public string ShotLabel { get; set; } = "";
}

/// <summary>L5 逐镜状态机：人工置位的目标状态。</summary>
public class UpdateShotStatusRequest
{
    public string? ShotStatus { get; set; }
}
