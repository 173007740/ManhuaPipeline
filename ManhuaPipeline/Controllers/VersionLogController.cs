using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

/// <summary>
/// 版本记录读取。只提供只读接口 —— 记录由开发侧（AI 每次改动后）写入，页面不提供增删改，
/// 免得随手改花账，版本号就失去追查意义了。
/// </summary>
[ApiController]
[Route("api/version-log")]
public class VersionLogController : ControllerBase
{
    private readonly DbService _db;

    public VersionLogController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    [HttpGet]
    public IActionResult GetList()
    {
        if (GetUserId() == 0) return Unauthorized();
        return Ok(_db.GetVersionLogs());
    }

    /// <summary>最新版本号。只是个版本标识、不含任何业务数据，所以未登录也放行 —— 登录页也要显示。</summary>
    [HttpGet("latest")]
    public IActionResult GetLatest()
    {
        return Ok(new { version = _db.GetLatestVersion() });
    }
}
