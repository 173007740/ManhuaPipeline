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

    /// <summary>
    /// 取指定集号那一集；没有就按「第N集」建一集再返回。
    /// 分镜是跨集产出的（单元号「12.1」＝ 第 12 集第 1 单元），而流水线这一步没让选集——
    /// 以前不管单元号写的是第几集，统统落到第一集，分镜页按集筛选、P4 按集投喂就全乱了套。
    /// </summary>
    public int EnsureEpisodeNumber(int projectId, int episodeNumber)
    {
        if (projectId <= 0 || episodeNumber <= 0) return 0;
        using var conn = GetConn(); conn.Open();
        using (var q = new SqlCommand(
                   "SELECT TOP 1 EpisodeId FROM Episodes WHERE ProjectId=@p AND EpisodeNumber=@n ORDER BY EpisodeId", conn))
        {
            q.Parameters.AddWithValue("@p", projectId);
            q.Parameters.AddWithValue("@n", episodeNumber);
            var v = q.ExecuteScalar();
            if (v is not null and not DBNull) return Convert.ToInt32(v);
        }
        using (var ins = new SqlCommand(@"
INSERT INTO Episodes(ProjectId, UserId, EpisodeNumber, Title, SortOrder, CreatedAt, BatchNumber)
SELECT @p, UserId, @n, N'第' + CONVERT(nvarchar(10), @n) + N'集', @n, SYSDATETIME(), 1 FROM Projects WHERE ProjectId=@p;
SELECT SCOPE_IDENTITY();", conn))
        {
            ins.Parameters.AddWithValue("@p", projectId);
            ins.Parameters.AddWithValue("@n", episodeNumber);
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

    /// <summary>
    /// 建一条资产。identityId 是它在漫剧里的身份（见 DbService.DramaIdentity）：
    /// 同一部剧里「杨彦刚」这个人只有一个身份，每集这条只是这个身份在这一集的一份实例。
    /// 手工加的资产可以不传，那就是没有身份，不参与跨集复用。
    /// </summary>
    public int AddAsset(int projectId, string table, string name, string? description, int identityId = 0)
    {
        if (string.IsNullOrEmpty(table)) return 0;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            $"INSERT INTO {table}(ProjectId, Name, Description, IdentityId, CreatedAt) " +
            "OUTPUT INSERTED.AssetId VALUES(@p,@n,@d,@i,GETDATE())", conn);
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", identityId > 0 ? identityId : (object)DBNull.Value);
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

    /// <summary>
    /// 名字精确相等一条都没匹配上时的兜底：在同类资产里找「互相包含」的那一条。
    /// 模型给的名字常常只是台账名的一部分——台账上叫「刘如烟植物染工坊」，
    /// 提示词那批写的是「染工坊」，只按相等比就一条也对不上，整批入库 0，这一步等于白跑。
    /// 只在唯一命中时才认：命中多条说明这个名字指代不清，宁可跳过也不乱写。
    /// </summary>
    public string? FindAssetNameLike(int projectId, string table, string key)
    {
        if (projectId <= 0 || string.IsNullOrEmpty(table) || string.IsNullOrWhiteSpace(key)) return null;
        key = key!.Trim();
        if (key.Length < 2) return null;          // 一两个字的键谁都像，不做推断

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            $"SELECT DISTINCT Name FROM {table} WHERE ProjectId=@p AND Name IS NOT NULL " +
            "AND (Name LIKE '%' + @k + '%' OR @k LIKE '%' + Name + '%')", conn);
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@k", key);

        var hits = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) hits.Add(r.GetString(0));
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>四类资产表：CHR / SCN / PRP / VFX。</summary>
    public static readonly string[] AssetTables =
        { "CharacterAssets", "EnvironmentAssets", "PropAssets", "EffectAssets" };

    /// <summary>
    /// 清掉项目下全部资产。重跑 P2a 时调：台账重出一版，旧的连同挂在它身上的
    /// 出图结果（ImageUrl）一起作废——留着只会让「外婆」和「外婆（照片/回忆态）」变成两个角色。
    /// </summary>
    public int DeleteAllAssets(int projectId)
    {
        if (projectId <= 0) return 0;
        var n = 0;
        using var conn = GetConn(); conn.Open();
        foreach (var t in AssetTables)
        {
            using var cmd = new SqlCommand($"DELETE FROM {t} WHERE ProjectId=@p", conn);
            cmd.Parameters.AddWithValue("@p", projectId);
            n += cmd.ExecuteNonQuery();
        }
        return n;
    }

    /// <summary>
    /// 清空某类资产上的出图提示词。重跑 P2c 时调：这一批整体重写，
    /// 不让上一版残留下的词跟新的混在一张卡上。
    /// </summary>
    public int ClearAssetPrompts(int projectId, string table)
    {
        if (projectId <= 0 || string.IsNullOrEmpty(table)) return 0;
        var sets = new List<string>();
        if (TableHasColumn(table, "ImagePrompt")) sets.Add("ImagePrompt=NULL");
        if (TableHasColumn(table, "NegativePrompt")) sets.Add("NegativePrompt=NULL");
        if (sets.Count == 0) return 0;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"UPDATE {table} SET {string.Join(",", sets)} WHERE ProjectId=@p", conn);
        cmd.Parameters.AddWithValue("@p", projectId);
        return cmd.ExecuteNonQuery();
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

    /// <summary>
    /// 存一份交付物（立项 / 剧本 / 资产清单 / 图册 / 质检这类整篇文本）。
    /// 同一项目同一阶段每跑一次 Version +1，旧版留在表里——重跑不冲掉上一版，
    /// 这也是剧本敢覆盖 Projects.ScriptContent 的底气：要回看随时能翻出来。
    /// </summary>
    public int InsertDeliverable(int runId, int stepId, string stageKey, int projectId, int episodeId,
                                 string? title, string? content, string? payloadJson)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
DECLARE @v INT = ISNULL((SELECT MAX(Version) FROM DirectorSkillDeliverables WHERE ProjectId=@p AND StageKey=@k),0)+1;
INSERT INTO DirectorSkillDeliverables(RunId,StepId,StageKey,ProjectId,EpisodeId,Title,Content,PayloadJson,Version)
VALUES(@r,@s,@k,@p,@e,@t,@c,@j,@v);
SELECT @v;", conn);
        cmd.Parameters.AddWithValue("@r", runId);
        cmd.Parameters.AddWithValue("@s", stepId);
        cmd.Parameters.AddWithValue("@k", stageKey);
        cmd.Parameters.AddWithValue("@p", projectId > 0 ? projectId : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@e", episodeId > 0 ? episodeId : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", (object?)content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@j", (object?)payloadJson ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<(int DeliverableId, string? StageKey, string? Title, int Version, string? CreatedAt)> GetDeliverables(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<(int, string?, string?, int, string?)>();
        using var cmd = new SqlCommand(@"
SELECT DeliverableId, StageKey, Title, Version, CONVERT(varchar(16), CreatedAt, 120)
FROM DirectorSkillDeliverables WHERE ProjectId=@p ORDER BY DeliverableId DESC", conn);
        cmd.Parameters.AddWithValue("@p", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                      r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    public string? GetDeliverableContent(int deliverableId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT Content FROM DirectorSkillDeliverables WHERE DeliverableId=@id", conn);
        cmd.Parameters.AddWithValue("@id", deliverableId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>剧本正本。老流水线的阶段 3/4/6/7/8 全读这一列，是它们的输入源。</summary>
    public string? GetProjectScriptContent(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT ScriptContent FROM Projects WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@id", projectId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// 剧本来源：user = 人自己写的定稿，ai = 模型跑出来的。
    /// 列还没建（没执行 Upgrade_剧本来源.sql）时返回 null，调用方按「模型写的」处理。
    /// </summary>
    public string? GetProjectScriptSource(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT ScriptSource FROM Projects WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@id", projectId);
        try { return cmd.ExecuteScalar() as string; }
        catch (Microsoft.Data.SqlClient.SqlException) { return null; }   // 列不存在
    }

    public void SetProjectScriptSource(int projectId, string? source)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET ScriptSource=@s, UpdatedAt=SYSDATETIME() WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@s", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", projectId);
        try { cmd.ExecuteNonQuery(); }
        catch (Microsoft.Data.SqlClient.SqlException) { /* 列没建就算了，不影响剧本本身 */ }
    }

    /// <summary>
    /// 把剧本写回项目。source 传 null 表示「沿用现在的来源」，覆盖 AI 产出时传 N'ai'。
    /// </summary>
    public void SetProjectScriptContent(int projectId, string? text, string? source = null)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE Projects SET ScriptContent=@s,
       ScriptSource = CASE WHEN @src IS NULL THEN ScriptSource ELSE @src END,
       UpdatedAt=SYSDATETIME()
WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@s", (object?)text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@src", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }
}
