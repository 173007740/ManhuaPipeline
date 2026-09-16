using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/project/{projectId}/episode")]
public class EpisodeController : ControllerBase
{
    private readonly DbService _db;
    public EpisodeController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private IActionResult? CheckProjectAccess(int projectId, out int userId)
    {
        userId = GetUserId();
        if (userId == 0) return Unauthorized();
        return _db.ProjectBelongsToUser(projectId, userId)
            ? null
            : NotFound(new { message = "项目不存在" });
    }

    [HttpGet]
    public IActionResult GetAll(int projectId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        return Ok(_db.GetEpisodes(projectId));
    }

    [HttpPost("save")]
    public IActionResult SaveEpisodes(int projectId, [FromBody] List<Episode> episodes)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null) return access;
        _db.SaveEpisodes(projectId, uid, episodes);
        return Ok(new { message = "保存成功" });
    }

    [HttpGet("{episodeId}/frames")]
    public IActionResult GetFrames(int projectId, int episodeId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        if (!_db.EpisodeBelongsToProject(episodeId, projectId)) return NotFound(new { message = "分集不存在" });
        return Ok(_db.GetFrames(episodeId));
    }

    [HttpGet("frames")]
    public IActionResult GetAllFrames(int projectId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        return Ok(_db.GetAllFrames(projectId));
    }

    [HttpPost("{episodeId}/frames/save")]
    public IActionResult SaveFrames(int projectId, int episodeId, [FromBody] List<StoryboardFrame> frames)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        if (!_db.EpisodeBelongsToProject(episodeId, projectId)) return NotFound(new { message = "分集不存在" });
        _db.SaveFrames(episodeId, projectId, frames);
        return Ok(new { message = "保存成功" });
    }
}
