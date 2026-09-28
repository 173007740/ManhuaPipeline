using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

/// <summary>
/// 版本记录（系统配置 → 「版本记录」页）。
///
/// 每次改动钉一条：版本号 + 分类 + 正文 + 时间，前端按时间倒序展示，
/// 顶部导航显示最新版本号。回查「这个毛病是哪次改出来的」时就靠它。
/// </summary>
public partial class DbService
{
    public sealed record VersionLogRow(int LogId, string Version, string Title, string Category,
                                       string Content, string ReleasedAt, string Author);

    /// <summary>版本列表，新的在前。publishedOnly=true 时过滤掉 IsPublished=0 的内部记录。</summary>
    public List<VersionLogRow> GetVersionLogs(bool publishedOnly = true)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<VersionLogRow>();
        using var cmd = new SqlCommand(@"
SELECT LogId, Version, Title, Category,
       Content, CONVERT(varchar(16), ReleasedAt, 120), Author
FROM VersionLogs
WHERE (@pub = 0 OR IsPublished = 1)
ORDER BY ReleasedAt DESC, LogId DESC", conn);
        cmd.Parameters.AddWithValue("@pub", publishedOnly ? 1 : 0);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VersionLogRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6)));
        }
        return list;
    }

    /// <summary>最新版本号（导航栏显示用）；还没有记录时返回空串，前端据此不显示。</summary>
    public string GetLatestVersion()
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "SELECT TOP 1 Version FROM VersionLogs WHERE IsPublished = 1 ORDER BY ReleasedAt DESC, LogId DESC", conn);
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? "" : (string)v;
    }
}
