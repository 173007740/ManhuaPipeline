using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;

namespace ManhuaPipeline.Controllers;

/// <summary>
/// 系统报告：给 /pages/dashboard.html 的「系统报告」弹层供数。
/// 只做汇总，不做任何写操作；全部按当前登录用户隔离。
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly DbService _db;
    public DashboardController(DbService db) { _db = db; }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    /// <summary>
    /// GET /api/dashboard/report?days=7 —— days&lt;=0 表示全部。
    /// 返回出图 / 视频 / Token 三块统计；Token 里的 quota 是全局配额（跟 token-report 一致）。
    /// </summary>
    [HttpGet("report")]
    public IActionResult Report([FromQuery] int days = 7)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();

        var (quota, globalUsed, remaining) = _db.GetTokenUsageStats();
        var images = _db.GetImageStats(uid, days);
        var videos = _db.GetVideoStats(uid, days);
        var recent = _db.GetImageRecent(uid, days, 100);      // 出图清单（最近 100 条）
        var byProject = _db.GetVideoByProject(uid, days);     // 视频 / Token 共用

        // 项目维度：视频报告按任务数排，Token 报告按消耗排，各取前 10 防止响应过大
        var videoProjects = byProject.Take(10)
            .Select(p => new { p.ProjectId, p.ProjectName, p.Total, p.Done, p.Failed, p.Tokens, p.Seconds })
            .ToList();

        var tokenProjects = byProject.OrderByDescending(p => p.Tokens).Take(10)
            .Select(p => new { p.ProjectId, p.ProjectName, UsedTokens = p.Tokens, TotalSeconds = p.Seconds })
            .ToList();

        return Ok(new
        {
            days,
            images = new
            {
                images.Total, images.Done, images.Failed, images.Running, images.Idle,
                byDay = images.ByDay, byType = images.ByType, recent = recent
            },
            videos = new
            {
                videos.Total, videos.Done, videos.Failed, videos.Running, videos.Cancelled,
                tokens = videos.Tokens, seconds = videos.Seconds, byDay = videos.ByDay,
                byProject = videoProjects,
                tokenReported = videos.TokenReported, durationReported = videos.DurationReported
            },
            tokens = new
            {
                quotaTokens = quota,
                usedTokens = videos.Tokens,      // 当前用户在所选时间内的消耗
                globalUsedTokens = globalUsed,   // 全站累计（配额口径）
                remainingTokens = remaining,
                byProject = tokenProjects,      // 项目维度清单（按消耗排序）
                // 上游没回传 usage 时不记账，所以这里要带上「多少条任务真的有用量」，页面上才能说清楚为什么是 0
                totalTasks = videos.Total,
                reportedTasks = videos.TokenReported

            }
        });
    }
}
