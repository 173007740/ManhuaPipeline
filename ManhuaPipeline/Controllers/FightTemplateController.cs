using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/fight")]
public class FightTemplateController : ControllerBase
{
    private readonly DbService _db;
    public FightTemplateController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet("list")]
    public IActionResult GetList([FromQuery] int? tier, [FromQuery] string? search)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetFightTemplates(uid, tier, search));
    }

    [HttpPost]
    public IActionResult Create([FromBody] FightTemplateRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "模板名不能为空" });
        var id = _db.SaveFightTemplate(uid, req.Name.Trim(), req.Tier, req.Duration, req.Scene ?? "", req.Beat ?? "", req.ActionPrompt ?? "", req.CameraPrompt ?? "", req.ConstraintPrompt ?? "", req.Tags);
        return Ok(new { success = true, fightTemplateId = id });
    }

    [HttpPut("{fightTemplateId}")]
    public IActionResult Update(int fightTemplateId, [FromBody] FightTemplateRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "模板名不能为空" });
        var item = _db.GetFightTemplate(fightTemplateId);
        if (item == null || item.UserId != uid) return NotFound(new { message = "模板不存在" });
        _db.UpdateFightTemplate(fightTemplateId, uid, req.Name.Trim(), req.Tier, req.Duration, req.Scene ?? "", req.Beat ?? "", req.ActionPrompt ?? "", req.CameraPrompt ?? "", req.ConstraintPrompt ?? "", req.Tags);
        return Ok(new { success = true });
    }

    [HttpDelete("{fightTemplateId}")]
    public IActionResult Delete(int fightTemplateId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var item = _db.GetFightTemplate(fightTemplateId);
        if (item == null || item.UserId != uid) return NotFound(new { message = "模板不存在" });
        _db.DeleteFightTemplate(fightTemplateId, uid);
        return Ok(new { message = "删除成功" });
    }
}

public class FightTemplateRequest
{
    public string Name { get; set; } = "";
    public int Tier { get; set; } = 3;
    public int Duration { get; set; } = 11;
    public string? Scene { get; set; }
    public string? Beat { get; set; }
    public string? ActionPrompt { get; set; }
    public string? CameraPrompt { get; set; }
    public string? ConstraintPrompt { get; set; }
    public string? Tags { get; set; }
}
