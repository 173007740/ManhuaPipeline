using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

/// <summary>
/// 系统报告（出图 / 视频 / Token）的统计查询。
///
/// 统计口径：
///   出图  —— CanvasNodes（画布节点），按 BoardId → CanvasBoards.UserId 归到当前用户；
///   视频  —— VideoGenerationTasks，按 ProjectId → Projects.UserId 归到当前用户；
///   Token —— 只统计视频任务回传的 ResponseUsageTokens（与 token-report.html 现有口径一致）；
///            大模型对话的 token 目前没有落库，所以不在这里体现。
/// days &lt;= 0 表示不限时间（全部）。
/// </summary>
public partial class DbService
{
    // ==================== 系统报告 ====================

    public sealed record DayStat(string Date, int Total, int Done, int Failed, long Tokens);
    public sealed record TypeStat(string NodeType, int Total, int Done, int Failed);
    public sealed record ImageRow(int Id, string Title, string NodeType, string Status, string ErrorMsg,
                                  string UpdatedAt, string BoardName);
    public sealed record VideoProjectRow(int ProjectId, string ProjectName, int Total, int Done, int Failed,
                                          long Tokens, long Seconds);
    public sealed record ImageStats(int Total, int Done, int Failed, int Running, int Idle,
                                    List<DayStat> ByDay, List<TypeStat> ByType);
    public sealed record VideoStats(int Total, int Done, int Failed, int Running, int Cancelled,
                                    long Tokens, long Seconds, List<DayStat> ByDay,
                                    int TokenReported, int DurationReported);

    private static DateTime FromDate(int days)
        => days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;

    /// <summary>时间下界参数：不限时间时传 NULL，且显式指定类型，避免 DBNull 被推断成 nvarchar。</summary>
    private static void AddFrom(SqlCommand cmd, DateTime from)
    {
        var p = cmd.Parameters.Add("@from", System.Data.SqlDbType.DateTime2);
        p.Value = from == DateTime.MinValue ? (object)DBNull.Value : from;
    }

    /// <summary>出图统计：节点成败与分布。Status：done / failed / running 类 / idle（还没跑）。</summary>
    public ImageStats GetImageStats(int userId, int days)
    {
        using var conn = GetConn(); conn.Open();
        var from = FromDate(days);
        var where = " WHERE b.UserId = @uid AND (@from IS NULL OR n.CreatedAt >= @from)";

        int total = 0, done = 0, failed = 0, running = 0, idle = 0;
        using (var cmd = new SqlCommand(@"
SELECT COUNT(*),
       ISNULL(SUM(CASE WHEN n.Status = 'done'   THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN n.Status = 'failed' THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN n.Status IN ('running','queued','pending','processing') THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN n.Status IS NULL OR n.Status = '' OR n.Status = 'idle' THEN 1 ELSE 0 END),0)
FROM CanvasNodes n JOIN CanvasBoards b ON b.Id = n.BoardId" + where, conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                total = r.GetInt32(0); done = r.GetInt32(1);
                failed = r.GetInt32(2); running = r.GetInt32(3); idle = r.GetInt32(4);
            }
        }

        var byDay = new List<DayStat>();
        using (var cmd = new SqlCommand(@"
SELECT CONVERT(varchar(10), n.CreatedAt, 23) AS D, COUNT(*),
       ISNULL(SUM(CASE WHEN n.Status = 'done'   THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN n.Status = 'failed' THEN 1 ELSE 0 END),0)
FROM CanvasNodes n JOIN CanvasBoards b ON b.Id = n.BoardId" + where + @"
GROUP BY CONVERT(varchar(10), n.CreatedAt, 23) ORDER BY D", conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            while (r.Read()) byDay.Add(new DayStat(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), 0));
        }

        var byType = new List<TypeStat>();
        using (var cmd = new SqlCommand(@"
SELECT n.NodeType, COUNT(*),
       ISNULL(SUM(CASE WHEN n.Status = 'done'   THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN n.Status = 'failed' THEN 1 ELSE 0 END),0)
FROM CanvasNodes n JOIN CanvasBoards b ON b.Id = n.BoardId" + where + @"
GROUP BY n.NodeType ORDER BY COUNT(*) DESC", conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            while (r.Read()) byType.Add(new TypeStat(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        }

        return new ImageStats(total, done, failed, running, idle, byDay, byType);
    }

    /// <summary>
    /// 视频统计：任务成败 + 消耗 token + 成片时长（秒）。
    /// 成功 = completed / succeeded；进行中 = processing / running / enhancing / queued / pending。
    /// </summary>
    public VideoStats GetVideoStats(int userId, int days)
    {
        using var conn = GetConn(); conn.Open();
        var from = FromDate(days);
        var where = " WHERE pr.UserId = @uid AND (@from IS NULL OR t.CreatedAt >= @from)";

        int total = 0, done = 0, failed = 0, running = 0, cancelled = 0;
        int tokenReported = 0, durationReported = 0;
        long tokens = 0, seconds = 0;
        using (var cmd = new SqlCommand(@"
SELECT COUNT(*),
       ISNULL(SUM(CASE WHEN t.Status IN ('completed','succeeded') THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN t.Status = 'failed' THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN t.Status IN ('processing','running','enhancing','queued','pending') THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN t.Status = 'cancelled' THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CAST(t.ResponseUsageTokens AS BIGINT)),0),
       ISNULL(SUM(CASE WHEN t.Status IN ('completed','succeeded')
                       THEN CAST(ISNULL(t.ResponseDuration, t.RequestDuration) AS BIGINT) ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN t.ResponseUsageTokens IS NOT NULL THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN t.ResponseDuration  IS NOT NULL THEN 1 ELSE 0 END),0)
FROM VideoGenerationTasks t JOIN Projects pr ON pr.ProjectId = t.ProjectId" + where, conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                total = r.GetInt32(0); done = r.GetInt32(1); failed = r.GetInt32(2);
                running = r.GetInt32(3); cancelled = r.GetInt32(4);
                tokens = r.GetInt64(5); seconds = r.GetInt64(6);
                tokenReported = r.GetInt32(7); durationReported = r.GetInt32(8);
            }
        }

        var byDay = new List<DayStat>();
        using (var cmd = new SqlCommand(@"
SELECT CONVERT(varchar(10), t.CreatedAt, 23) AS D, COUNT(*),
       ISNULL(SUM(CASE WHEN t.Status IN ('completed','succeeded') THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN t.Status = 'failed' THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CAST(t.ResponseUsageTokens AS BIGINT)),0)
FROM VideoGenerationTasks t JOIN Projects pr ON pr.ProjectId = t.ProjectId" + where + @"
GROUP BY CONVERT(varchar(10), t.CreatedAt, 23) ORDER BY D", conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            while (r.Read()) byDay.Add(new DayStat(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4)));
        }

        return new VideoStats(total, done, failed, running, cancelled, tokens, seconds, byDay,
                              tokenReported, durationReported);
    }

    /// <summary>出图清单：最近更新的节点（含失败原因），用来定位「哪张图出错了」。</summary>
    public List<ImageRow> GetImageRecent(int userId, int days, int limit)
    {
        using var conn = GetConn(); conn.Open();
        var from = FromDate(days);
        var list = new List<ImageRow>();
        using (var cmd = new SqlCommand(@"
SELECT TOP (@lim) n.Id, ISNULL(n.Title,''), ISNULL(n.NodeType,''), ISNULL(n.Status,''),
       ISNULL(n.ErrorMsg,''), CONVERT(varchar(19), ISNULL(n.UpdatedAt, n.CreatedAt), 120), ISNULL(b.Title,'')
FROM CanvasNodes n JOIN CanvasBoards b ON b.Id = n.BoardId
WHERE b.UserId = @uid AND (@from IS NULL OR n.CreatedAt >= @from)
ORDER BY ISNULL(n.UpdatedAt, n.CreatedAt) DESC", conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            cmd.Parameters.AddWithValue("@lim", limit);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var err = r.GetString(4);
                if (err.Length > 200) err = err.Substring(0, 200) + "…";
                list.Add(new ImageRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                                      err, r.GetString(5), r.GetString(6)));
            }
        }
        return list;
    }

    /// <summary>视频按项目汇总：视频报告和 Token 报告共用（Token 消耗也来自视频任务）。</summary>
    public List<VideoProjectRow> GetVideoByProject(int userId, int days)
    {
        using var conn = GetConn(); conn.Open();
        var from = FromDate(days);
        var list = new List<VideoProjectRow>();
        using (var cmd = new SqlCommand(@"
SELECT pr.ProjectId, ISNULL(pr.Title,''), COUNT(*),
       ISNULL(SUM(CASE WHEN t.Status IN ('completed','succeeded') THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN t.Status = 'failed' THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CAST(t.ResponseUsageTokens AS BIGINT)),0),
       ISNULL(SUM(CASE WHEN t.Status IN ('completed','succeeded')
                       THEN CAST(ISNULL(t.ResponseDuration, t.RequestDuration) AS BIGINT) ELSE 0 END),0)
FROM VideoGenerationTasks t JOIN Projects pr ON pr.ProjectId = t.ProjectId
WHERE pr.UserId = @uid AND (@from IS NULL OR t.CreatedAt >= @from)
GROUP BY pr.ProjectId, pr.Title
ORDER BY COUNT(*) DESC", conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new VideoProjectRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2),
                                             r.GetInt32(3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6)));
        }
        return list;
    }
}
