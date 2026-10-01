using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

/// <summary>
/// 系统报告（出图 / 视频 / Token）的统计查询。
///
/// 统计口径：
///   出图  —— CanvasNodes（画布节点，按 BoardId → CanvasBoards.UserId 归到当前用户）
///            + AssetImageTasks（资产卡上点的出图 / 批量出图，UserId 就在这张表上）；
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
    /// <summary>出图清单一行。BoardName 这一列是「来源」：画布 · 画板名 / 资产出图 · 项目名。</summary>
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

    /* 出图统计：两处来源合在一起算。
         CanvasNodes     —— 无限画布上的节点出图（BoardId → CanvasBoards.UserId 归到当前用户）；
         AssetImageTasks —— 资产卡上点的「出图 / 批量出图」（UserId 就在这张表上）。
       以前只算画布那一半：资产卡上出了几百张，报告里一条也没有，看着像没出过图。
       两边状态口径不一样（画布 done/failed，资产 completed/failed），下面统一成
       done / failed / running / idle 四档再统计。 */
    private const string ImageSourceSql = @"
FROM (
    SELECT CASE WHEN n.Status = 'done'   THEN 'done'
                WHEN n.Status = 'failed' THEN 'failed'
                WHEN n.Status IN ('running','queued','pending','processing') THEN 'running'
                ELSE 'idle' END                     AS St,
           n.CreatedAt                              AS CreatedAt,
           ISNULL(n.NodeType,'')                    AS Kind
    FROM CanvasNodes n JOIN CanvasBoards b ON b.Id = n.BoardId
    WHERE b.UserId = @uid AND (@from IS NULL OR n.CreatedAt >= @from)
    UNION ALL
    SELECT CASE WHEN t.Status IN ('completed','succeeded') THEN 'done'
                WHEN t.Status = 'failed' THEN 'failed'
                WHEN t.Status IN ('running','queued','pending','processing') THEN 'running'
                ELSE 'idle' END,
           t.CreatedAt,
           CASE t.Category WHEN 'characters'   THEN '资产·角色'
                           WHEN 'props'        THEN '资产·道具'
                           WHEN 'environments' THEN '资产·环境'
                           WHEN 'effects'      THEN '资产·特效'
                           ELSE ISNULL(t.Category, '资产') END
    FROM AssetImageTasks t
    WHERE t.UserId = @uid AND (@from IS NULL OR t.CreatedAt >= @from)
) x";

    public ImageStats GetImageStats(int userId, int days)
    {
        using var conn = GetConn(); conn.Open();
        var from = FromDate(days);

        int total = 0, done = 0, failed = 0, running = 0, idle = 0;
        using (var cmd = new SqlCommand(@"
SELECT COUNT(*),
       ISNULL(SUM(CASE WHEN x.St = 'done'   THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN x.St = 'failed' THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN x.St = 'running' THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN x.St = 'idle'   THEN 1 ELSE 0 END),0)
" + ImageSourceSql, conn))
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
SELECT CONVERT(varchar(10), x.CreatedAt, 23) AS D, COUNT(*),
       ISNULL(SUM(CASE WHEN x.St = 'done'   THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN x.St = 'failed' THEN 1 ELSE 0 END),0)
" + ImageSourceSql + @"
GROUP BY CONVERT(varchar(10), x.CreatedAt, 23) ORDER BY D", conn))
        {
            cmd.Parameters.AddWithValue("@uid", userId);
            AddFrom(cmd, from);
            using var r = cmd.ExecuteReader();
            while (r.Read()) byDay.Add(new DayStat(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), 0));
        }

        var byType = new List<TypeStat>();
        using (var cmd = new SqlCommand(@"
SELECT x.Kind, COUNT(*),
       ISNULL(SUM(CASE WHEN x.St = 'done'   THEN 1 ELSE 0 END),0),
       ISNULL(SUM(CASE WHEN x.St = 'failed' THEN 1 ELSE 0 END),0)
" + ImageSourceSql + @"
GROUP BY x.Kind ORDER BY COUNT(*) DESC", conn))
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
        /* 画布节点与资产出图任务混在一张清单里，按时间倒序取最近这些条。
           末列是「来源」：画布那条写「画布 · 画板名」，资产那条写「资产出图 · 项目名」，
           一眼看出这张图是画布上出的还是资产卡上出的。 */
        using (var cmd = new SqlCommand(@"
SELECT TOP (@lim) Id, Title, Kind, St, ErrorMsg, At, Src
FROM (
    SELECT n.Id                                     AS Id,
           ISNULL(n.Title,'')                       AS Title,
           ISNULL(n.NodeType,'')                    AS Kind,
           CASE WHEN n.Status = 'done'   THEN 'done'
                WHEN n.Status = 'failed' THEN 'failed'
                WHEN n.Status IN ('running','queued','pending','processing') THEN 'running'
                ELSE 'idle' END                     AS St,
           ISNULL(n.ErrorMsg,'')                    AS ErrorMsg,
           ISNULL(n.UpdatedAt, n.CreatedAt)         AS At,
           N'画布 · ' + ISNULL(b.Title,'')          AS Src
    FROM CanvasNodes n JOIN CanvasBoards b ON b.Id = n.BoardId
    WHERE b.UserId = @uid AND (@from IS NULL OR n.CreatedAt >= @from)
    UNION ALL
    SELECT t.TaskId,
           ISNULL(t.AssetName,''),
           CASE t.Category WHEN 'characters'   THEN N'资产·角色'
                           WHEN 'props'        THEN N'资产·道具'
                           WHEN 'environments' THEN N'资产·环境'
                           WHEN 'effects'      THEN N'资产·特效'
                           ELSE ISNULL(t.Category, N'资产') END,
           CASE WHEN t.Status IN ('completed','succeeded') THEN 'done'
                WHEN t.Status = 'failed' THEN 'failed'
                WHEN t.Status IN ('running','queued','pending','processing') THEN 'running'
                ELSE 'idle' END,
           ISNULL(t.ErrorMessage,''),
           ISNULL(t.FinishedAt, t.CreatedAt),
           N'资产出图 · ' + ISNULL(pr.Title, N'项目 ' + CAST(t.ProjectId AS nvarchar(20)))
    FROM AssetImageTasks t LEFT JOIN Projects pr ON pr.ProjectId = t.ProjectId
    WHERE t.UserId = @uid AND (@from IS NULL OR t.CreatedAt >= @from)
) u
ORDER BY At DESC", conn))
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
