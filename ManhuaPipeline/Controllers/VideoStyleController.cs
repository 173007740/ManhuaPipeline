using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/video-style")]
public class VideoStyleController : ControllerBase
{
    private readonly DbService _db;
    public VideoStyleController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet]
    public IActionResult GetList()
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetVideoStyles());
    }

    [HttpPost]
    public IActionResult Save([FromBody] SaveVideoStyleRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.StyleName))
            return BadRequest(new { message = "风格名称不能为空" });
        if (string.IsNullOrWhiteSpace(req.StylePrompt))
            return BadRequest(new { message = "风格提示词不能为空" });
        var id = _db.SaveVideoStyle(req.StyleId, req.StyleName.Trim(), req.StylePrompt.Trim());
        return Ok(new { styleId = id, message = "保存成功" });
    }

    [HttpDelete("{styleId}")]
    public IActionResult Delete(int styleId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        _db.DeleteVideoStyle(styleId);
        return Ok(new { message = "删除成功" });
    }
}

public class SaveVideoStyleRequest
{
    public int? StyleId { get; set; }
    public string StyleName { get; set; } = "";
    public string StylePrompt { get; set; } = "";
}
