using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/project/{projectId}/coherence")]
public class CoherenceController : ControllerBase
{
    private readonly DbService _db;
    public CoherenceController(DbService db) { _db = db; }
    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;
    [HttpGet]
    public IActionResult Get(int projectId)
    {
        var userId = GetUserId();
        if (userId == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, userId)) return NotFound(new { message = "项目不存在" });
        var check = _db.GetCoherenceCheck(projectId);
        if (check == null) return Ok(new { issues = "", status = "pending" });
        return Ok(new { issues = check.Issues ?? "", status = check.Status });
    }
}
