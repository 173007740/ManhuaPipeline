using Microsoft.Data.SqlClient;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 产出入库需要的读写。
///
/// 这里只提供「往哪儿写」的原语，不认识任何产出格式——解析归 SkillOutputImporter。
/// 四张资产表结构略有差异（角色表多 Attributes、各表 ImagePrompt/NegativePrompt 列未必齐全），
/// 所以涉及可选列的地方先查 INFORMATION_SCHEMA 再拼 SQL，避免升级脚本没跑全就炸。
/// </summary>
public partial class DbService
{
    /// <summary>本次运行绑定到哪个项目/剧集。产出入库必须知道落点，绑不上就只留文本不入库。</summary>
    public (int ProjectId, int EpisodeId) GetRunContext(int runId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT ProjectId, EpisodeId FROM DirectorSkillRuns WHERE RunId=@id", conn);
        cmd.Parameters.AddWithValue("@id", runId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0);
        return (r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1));
    }

    /// <summary>这次运行级别的输入（如页面顶部选的提示词类型）。跟着整次运行走，不属于任何单步。</summary>
    public string? GetRunInputsJson(int runId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT InputsJson FROM DirectorSkillRuns WHERE RunId=@id", conn);
        cmd.Parameters.AddWithValue("@id", runId);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string?)v;
    }

    /// <summary>
    /// 按镜头覆盖 H3 提示词，只动 PromptTextH3，绝不碰 PromptText。
    /// SD 和 H3 是同一条记录的两列、各存各的；拿 SD 的 upsert 写 H3 会把 SD 正文清空。
    /// </summary>
    public void UpsertH3PromptsForShot(int projectId, string? unitName, string? shotLabel, List<SeedancePrompt> rows)
    {
        if (rows == null || rows.Count == 0) return;
        using var conn = GetConn(); conn.Open(); using var txn = conn.BeginTransaction();
        try
        {
            var existingIds = new List<int>();
            using (var q = new SqlCommand("SELECT PromptId FROM SeedancePrompts WHERE ProjectId=@pid AND UnitName=@un AND ShotLabel=@sl ORDER BY PromptId", conn, txn))
            {
                q.Parameters.AddWithValue("@pid", projectId);
                q.Parameters.AddWithValue("@un", (object?)unitName ?? DBNull.Value);
                q.Parameters.AddWithValue("@sl", (object?)shotLabel ?? DBNull.Value);
                using var r = q.ExecuteReader();
                while (r.Read()) existingIds.Add((int)r["PromptId"]);
            }

            var min = Math.Min(existingIds.Count, rows.Count);
            for (int i = 0; i < min; i++)
            {
                using var upd = new SqlCommand("UPDATE SeedancePrompts SET PromptTextH3=@h3, ShotType=@stt, Duration=@dur WHERE PromptId=@id", conn, txn);
                upd.Parameters.AddWithValue("@h3", rows[i].PromptTextH3 ?? "");
                upd.Parameters.AddWithValue("@stt", (object?)rows[i].ShotType ?? DBNull.Value);
                upd.Parameters.AddWithValue("@dur", rows[i].Duration);
                upd.Parameters.AddWithValue("@id", existingIds[i]);
                upd.ExecuteNonQuery();
            }

            // 多出来的行新建：PromptText 留空，等 SD 那边生成时填，这里只写 H3 列
            for (int i = existingIds.Count; i < rows.Count; i++)
            {
                var p = rows[i];
                using var ins = new SqlCommand(@"INSERT INTO SeedancePrompts(ProjectId,FrameId,PromptText,PromptTextH3,NegativePrompt,Status,BatchNumber,EpisodeNumber,UnitName,ShotLabel,ShotType,Duration,CreatedAt)
VALUES(@pid,@fid,'',@h3,@np,@st,@bn,@en,@un,@sl,@stt,@dur,GETDATE())", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId);
                ins.Parameters.AddWithValue("@fid", (object?)p.FrameId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@h3", p.PromptTextH3 ?? "");
                ins.Parameters.AddWithValue("@np", (object?)p.NegativePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@st", p.Status);
                ins.Parameters.AddWithValue("@bn", p.BatchNumber);
                ins.Parameters.AddWithValue("@en", p.EpisodeNumber);
                ins.Parameters.AddWithValue("@un", (object?)p.UnitName ?? DBNull.Value);
                ins.Parameters.AddWithValue("@sl", (object?)p.ShotLabel ?? DBNull.Value);
                ins.Parameters.AddWithValue("@stt", (object?)p.ShotType ?? DBNull.Value);
                ins.Parameters.AddWithValue("@dur", p.Duration);
                ins.ExecuteNonQuery();
            }

            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    /// <summary>
    /// 取项目下的第一集；一集都没有就先建一集再返回。
    /// 剧集本来是阶段 3（分集蓝图）才产出的，但流水线可以直接产出分镜——
    /// 不能因为「还没分集」就让分镜没地方落，那这一步等于白跑。
    /// </summary>
    public int EnsureFirstEpisode(int projectId)
    {
        if (projectId <= 0) return 0;
        using var conn = GetConn(); conn.Open();
        using (var q = new SqlCommand("SELECT TOP 1 EpisodeId FROM Episodes WHERE ProjectId=@p ORDER BY EpisodeNumber, EpisodeId", conn))
        {
            q.Parameters.AddWithValue("@p", projectId);
            var v = q.ExecuteScalar();
            if (v is not null and not DBNull) return Convert.ToInt32(v);
        }
        using (var ins = new SqlCommand(@"
INSERT INTO Episodes(ProjectId, UserId, EpisodeNumber, Title, SortOrder, CreatedAt, BatchNumber)
SELECT @p, UserId, 1, N'第1集', 1, SYSDATETIME(), 1 FROM Projects WHERE ProjectId=@p;
SELECT SCOPE_IDENTITY();", conn))
        {
            ins.Parameters.AddWithValue("@p", projectId);
            var v = ins.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt32(v);
        }
    }

    /// <summary>资产类别前缀 → 表名。AUD（声音）不出图也没有表，返回空串由调用方跳过。</summary>
    public static string AssetTableOf(string? category) => (category ?? "").ToUpperInvariant() switch
    {
        "CHR" => "CharacterAssets",
        "SCN" => "EnvironmentAssets",
        "PRP" => "PropAssets",
        "VFX" => "EffectAssets",
        _ => ""
    };

    private bool TableHasColumn(string table, string column)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME=@t AND COLUMN_NAME=@c", conn);
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>同名资产是否已存在。入库要幂等——重跑一次不该多出一份。</summary>
    public bool AssetNameExists(int projectId, string table, string name)
    {
        if (string.IsNullOrEmpty(table)) return false;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"SELECT COUNT(1) FROM {table} WHERE ProjectId=@p AND Name=@n", conn);
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public int AddAsset(int projectId, string table, string name, string? description)
    {
        if (string.IsNullOrEmpty(table)) return 0;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            $"INSERT INTO {table}(ProjectId, Name, Description, CreatedAt) OUTPUT INSERTED.AssetId VALUES(@p,@n,@d,GETDATE())", conn);
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    /// <summary>把 P2c 的出图提示词回填到资产上。资产不存在时返回 false，由调用方决定是否先建。</summary>
    public bool UpdateAssetPrompt(int projectId, string table, string name, string? imagePrompt, string? negativePrompt)
    {
        if (string.IsNullOrEmpty(table)) return false;
        var sets = new List<string>();
        if (imagePrompt != null && TableHasColumn(table, "ImagePrompt")) sets.Add("ImagePrompt=@ip");
        if (negativePrompt != null && TableHasColumn(table, "NegativePrompt")) sets.Add("NegativePrompt=@np");
        if (sets.Count == 0) return false;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"UPDATE {table} SET {string.Join(",", sets)} WHERE ProjectId=@p AND Name=@n", conn);
        if (imagePrompt != null) cmd.Parameters.AddWithValue("@ip", imagePrompt);
        if (negativePrompt != null) cmd.Parameters.AddWithValue("@np", negativePrompt);
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>项目现有资产名。投喂提示词解析要拿它做引用名规范化（@角色引用 → [赵日天]）。</summary>
    public (List<string> Chars, List<string> Props, List<string> Envs, List<string> Effects) GetAssetNames(int projectId)
    {
        List<string> Query(string table)
        {
            var list = new List<string>();
            using var conn = GetConn(); conn.Open();
            using var cmd = new SqlCommand($"SELECT Name FROM {table} WHERE ProjectId=@p", conn);
            cmd.Parameters.AddWithValue("@p", projectId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
        return (Query("CharacterAssets"), Query("PropAssets"), Query("EnvironmentAssets"), Query("EffectAssets"));
    }

    public int GetEpisodeNumber(int episodeId)
    {
        if (episodeId <= 0) return 0;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT EpisodeNumber FROM Episodes WHERE EpisodeId=@id", conn);
        cmd.Parameters.AddWithValue("@id", episodeId);
        var v = cmd.ExecuteScalar();
        return v == null ? 0 : Convert.ToInt32(v);
    }

    /// <summary>记下入库结果：成了几条、失败原因是什么。解析不出来也要写清楚，不能默默丢。</summary>
    public void SetStepImport(int stepId, int count, string? error)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillRunSteps SET ImportedCount=@c, ImportError=@e, UpdatedAt=SYSDATETIME() WHERE StepId=@id", conn);
        cmd.Parameters.AddWithValue("@c", count);
        cmd.Parameters.AddWithValue("@e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", stepId);
        cmd.ExecuteNonQuery();
    }
}
