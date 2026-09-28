using Microsoft.Data.SqlClient;

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
