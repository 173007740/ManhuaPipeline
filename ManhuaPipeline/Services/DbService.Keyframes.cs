using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// L3 关键帧层落库（ProjectKeyframes）。
/// 表结构见 Database/Upgrade_关键帧层.sql。
///
/// 全部读写都做「表不存在则静默降级」处理：老库还没执行 Upgrade 脚本时，
/// 关键帧页显示为空、阶段 9 不注入锚定，主流程照常可跑（与连续性表的容错策略一致）。
/// </summary>
public partial class DbService
{
    /// <summary>表是否已存在。缓存一次，避免每个请求都查系统表（跑 Upgrade 脚本后需重启生效）。</summary>
    private bool? _keyframeTableExists;

    public bool KeyframeTableExists()
    {
        if (_keyframeTableExists.HasValue) return _keyframeTableExists.Value;
        try
        {
            using var conn = GetConn();
            conn.Open();
            using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.ProjectKeyframes', N'U') IS NULL THEN 0 ELSE 1 END", conn);
            _keyframeTableExists = Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 1;
        }
        catch
        {
            _keyframeTableExists = false;
        }
        return _keyframeTableExists.Value;
    }

    /// <summary>查询某项目全部关键帧（按集号、排序号）。表不存在时返回空列表。</summary>
    public List<ProjectKeyframe> GetKeyframes(int projectId)
    {
        if (!KeyframeTableExists()) return [];
        try
        {
            var result = new List<ProjectKeyframe>();
            using var conn = GetConn();
            conn.Open();
            using var cmd = new SqlCommand("SELECT * FROM ProjectKeyframes WHERE ProjectId=@pid ORDER BY EpisodeNumber, SortOrder, KeyframeId", conn);
            cmd.Parameters.AddWithValue("@pid", projectId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(ReadKeyframe(r));
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>查询某项目指定集的关键帧（episodeNumber=0 表示全片级）。</summary>
    public List<ProjectKeyframe> GetKeyframesByEpisode(int projectId, int episodeNumber)
    {
        if (!KeyframeTableExists()) return [];
        try
        {
            var result = new List<ProjectKeyframe>();
            using var conn = GetConn();
            conn.Open();
            using var cmd = new SqlCommand("SELECT * FROM ProjectKeyframes WHERE ProjectId=@pid AND EpisodeNumber=@ep ORDER BY SortOrder, KeyframeId", conn);
            cmd.Parameters.AddWithValue("@pid", projectId);
            cmd.Parameters.AddWithValue("@ep", episodeNumber);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(ReadKeyframe(r));
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 整表替换某项目的关键帧方案（重新生成时整体覆盖，与连续性表 / 单元绑定策略一致）。
    /// 返回实际写入行数；表不存在返回 0（不抛异常，避免拖垮阶段流程）。
    /// </summary>
    public int ReplaceKeyframes(int projectId, List<ProjectKeyframe> rows)
    {
        if (!KeyframeTableExists()) return 0;
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using (var del = new SqlCommand("DELETE FROM ProjectKeyframes WHERE ProjectId=@pid", conn, txn))
            {
                del.Parameters.AddWithValue("@pid", projectId);
                del.ExecuteNonQuery();
            }

            var order = 0;
            foreach (var row in rows)
            {
                order++;
                using var ins = new SqlCommand(
                    "INSERT INTO ProjectKeyframes(ProjectId,EpisodeNumber,SortOrder,NodeLabel,NodeReason,ShotLabel,UnitNumber," +
                    "Composition,LockedCharacters,LockedProps,LockedSceneDirection,ClueVisible,NextConnection,ImagePrompt,Status,Source,CreatedAt,UpdatedAt) " +
                    "VALUES(@pid,@ep,@ord,@label,@reason,@shot,@unit,@comp,@chars,@props,@dir,@clue,@next,@img,@st,@src,SYSDATETIME(),SYSDATETIME())", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId);
                ins.Parameters.AddWithValue("@ep", row.EpisodeNumber);
                ins.Parameters.AddWithValue("@ord", row.SortOrder > 0 ? row.SortOrder : order);
                ins.Parameters.AddWithValue("@label", (object?)Fit(row.NodeLabel, 100) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@reason", (object?)Fit(row.NodeReason, 400) ?? DBNull.Value);
                // 镜头号/单元号是 Stage 9 锚定对齐的依据，超长时优先抽取编号本体再兜底截断，避免截断后对不上镜头
                ins.Parameters.AddWithValue("@shot", (object?)FitShot(row.ShotLabel) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@unit", (object?)FitUnit(row.UnitNumber) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@comp", (object?)Fit(row.Composition, 1000) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@chars", (object?)Fit(row.LockedCharacters, 1000) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@props", (object?)Fit(row.LockedProps, 1000) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@dir", (object?)Fit(row.LockedSceneDirection, 1000) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@clue", (object?)Fit(row.ClueVisible, 500) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@next", (object?)Fit(row.NextConnection, 1000) ?? DBNull.Value);
                ins.Parameters.AddWithValue("@img", (object?)row.ImagePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@st", string.IsNullOrWhiteSpace(row.Status) ? "draft" : row.Status);
                ins.Parameters.AddWithValue("@src", string.IsNullOrWhiteSpace(row.Source) ? "llm" : row.Source);
                ins.ExecuteNonQuery();
            }

            txn.Commit();
            return rows.Count;
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 按表列长度截断。模型偶尔会返回超长文本，直接写入会触发 SQL 8152「将截断字符串或二进制数据」，
    /// 整个事务回滚 → 关键帧 0 行（表现为「提示词生成完了但关键帧没生成」）。
    /// 宁可截断保住这一行，也不能让整批关键帧丢掉。
    /// </summary>
    private static string? Fit(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = text.Trim();
        return t.Length <= max ? t : t.Substring(0, max);
    }

    private static string? FitShot(string? text) => FitId(text, 40, @"\d+(?:[.\-]\d+)+");

    private static string? FitUnit(string? text) => FitId(text, 40, @"\d+(?:\.\d+)+");

    private static string? FitId(string? text, int max, string pattern)
    {
        var fitted = Fit(text, max);
        if (fitted == null || fitted.Length <= max) return fitted;
        var m = Regex.Match(fitted, pattern);
        return m.Success ? m.Value : fitted.Substring(0, max);
    }

    /// <summary>清空某项目的关键帧方案。表不存在时静默返回。</summary>
    public void ClearKeyframes(int projectId)
    {
        if (!KeyframeTableExists()) return;
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("DELETE FROM ProjectKeyframes WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    private static ProjectKeyframe ReadKeyframe(SqlDataReader r) => new()
    {
        KeyframeId = (int)r["KeyframeId"],
        ProjectId = (int)r["ProjectId"],
        EpisodeNumber = r["EpisodeNumber"] == DBNull.Value ? 0 : (int)r["EpisodeNumber"],
        SortOrder = r["SortOrder"] == DBNull.Value ? 0 : (int)r["SortOrder"],
        NodeLabel = r["NodeLabel"] as string,
        NodeReason = r["NodeReason"] as string,
        ShotLabel = r["ShotLabel"] as string,
        UnitNumber = r["UnitNumber"] as string,
        Composition = r["Composition"] as string,
        LockedCharacters = r["LockedCharacters"] as string,
        LockedProps = r["LockedProps"] as string,
        LockedSceneDirection = r["LockedSceneDirection"] as string,
        ClueVisible = r["ClueVisible"] as string,
        NextConnection = r["NextConnection"] as string,
        ImagePrompt = r["ImagePrompt"] as string,
        Status = r["Status"] == DBNull.Value ? "draft" : (string)r["Status"],
        Source = r["Source"] == DBNull.Value ? "llm" : (string)r["Source"],
        CreatedAt = r["CreatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["CreatedAt"],
        UpdatedAt = r["UpdatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["UpdatedAt"]
    };
}
