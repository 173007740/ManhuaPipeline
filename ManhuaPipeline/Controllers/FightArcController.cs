using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/fight-arc")]
public class FightArcController : ControllerBase
{
    private readonly DbService _db;
    public FightArcController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet("list")]
    public IActionResult GetList([FromQuery] string? status)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        return Ok(_db.GetFightArcTemplates(status));
    }

    [HttpGet("{arcTypeId}")]
    public IActionResult Get(string arcTypeId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var item = _db.GetFightArcTemplate(arcTypeId);
        if (item == null) return NotFound(new { message = "骨架不存在" });
        return Ok(item);
    }
}
