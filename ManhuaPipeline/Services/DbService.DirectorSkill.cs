using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

/// <summary>
/// 导演 Skill 包读写（系统配置 → 「Skill 包」页）。
///
/// 这一层的存在意义就一条：<b>让"规则"是数据而不是代码</b>。
/// 外部 skill 的本质是给 LLM 看的 md 规则文件，以前散在磁盘上跟系统没关系；
/// 落到这两张表以后，调仓库 = 改一行 Scope，改规则 = 编辑正文，都不需要碰 C#。
/// 引擎方（后续的分阶段执行器）只管按 Scope 把文档拼进 prompt 即可。
/// </summary>
public partial class DbService
{
    public sealed record SkillDocRow(int DocId, string FileName, string Title, string Scope,
                                     string? ScopeValue, bool IsCore, int Chars, int SortOrder,
                                     string UpdatedAt);

    public sealed record SkillDocDetail(int DocId, int PackId, string FileName, string Title,
                                        string Scope, string? ScopeValue, bool IsCore, string Content);

    /// <summary>全部 Skill 包。</summary>
    public List<(int PackId, string PackKey, string Name, string Version, string? SourcePath, int DocCount, long Chars)> GetSkillPacks()
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<(int, string, string, string, string?, int, long)>();
        using var cmd = new SqlCommand(@"
SELECT p.PackId, p.PackKey, p.Name, p.Version, p.SourcePath,
       ISNULL(d.cnt, 0), ISNULL(d.chars, 0)
FROM DirectorSkillPacks p
LEFT JOIN (SELECT PackId, COUNT(*) cnt, SUM(CAST(Chars AS bigint)) chars
           FROM DirectorSkillDocs GROUP BY PackId) d ON d.PackId = p.PackId
ORDER BY p.PackId", conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                      r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt64(6)));
        }
        return list;
    }

    /// <summary>包内文档清单（不含正文，列表页用）。</summary>
    public List<SkillDocRow> GetSkillDocs(int packId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<SkillDocRow>();
        using var cmd = new SqlCommand(@"
SELECT DocId, FileName, Title, Scope, ScopeValue, IsCore, Chars, SortOrder,
       CONVERT(varchar(16), UpdatedAt, 120)
FROM DirectorSkillDocs
WHERE PackId = @pk
ORDER BY SortOrder, DocId", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkillDocRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5),
                r.GetInt32(6), r.GetInt32(7), r.GetString(8)));
        }
        return list;
    }

    /// <summary>单份文档全文（编辑用）。</summary>
    public SkillDocDetail? GetSkillDoc(int docId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT DocId, PackId, FileName, Title, Scope, ScopeValue, IsCore, Content
FROM DirectorSkillDocs WHERE DocId = @id", conn);
        cmd.Parameters.AddWithValue("@id", docId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SkillDocDetail(
            r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6), r.GetString(7));
    }

    /// <summary>保存文档改动（含 Scope 调整）。Chars 跟着正文重算，方便估算 prompt 开销。</summary>
    public bool SaveSkillDoc(int docId, string title, string scope, string? scopeValue, string content)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillDocs
SET Title=@t, Scope=@s, ScopeValue=@sv, Content=@c, Chars=LEN(@c), UpdatedAt=SYSDATETIME()
WHERE DocId=@id", conn);
        cmd.Parameters.AddWithValue("@t", title);
        cmd.Parameters.AddWithValue("@s", scope);
        cmd.Parameters.AddWithValue("@sv", (object?)scopeValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", content);
        cmd.Parameters.AddWithValue("@id", docId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public sealed record SkillStageRow(int StageId, string StageKey, string Name, int SortOrder,
                                        string? DocFilter, string? InputsJson, string? OutputContract,
                                        string? Gates, bool HumanConfirm, bool IsEnabled,
                                        string? OutputTarget = null);

    /// <summary>阶段编排清单。引擎按 SortOrder 依次跑，HumanConfirm=1 的阶段必须停下来等人确认。</summary>
    public List<SkillStageRow> GetSkillStages(int packId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<SkillStageRow>();
        using var cmd = new SqlCommand(@"
SELECT StageId, StageKey, Name, SortOrder, DocFilter, InputsJson, OutputContract, Gates, HumanConfirm, IsEnabled,
       OutputTarget
FROM DirectorSkillStages
WHERE PackId = @pk
ORDER BY SortOrder, StageId", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkillStageRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.GetBoolean(8), r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return list;
    }

    /// <summary>保存阶段编排。改完下一次跑流水线就按新编排走。</summary>
    public bool SaveSkillStage(int stageId, string name, string? docFilter, string? inputsJson,
                               string? outputContract, string? gates, bool humanConfirm, bool isEnabled,
                               string? outputTarget)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillStages
SET Name=@n, DocFilter=@f, InputsJson=@i, OutputContract=@o, Gates=@g,
    HumanConfirm=@h, IsEnabled=@e, OutputTarget=@ot, UpdatedAt=SYSDATETIME()
WHERE StageId=@id", conn);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@f", (object?)docFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)inputsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@o", (object?)outputContract ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@g", (object?)gates ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@h", humanConfirm);
        cmd.Parameters.AddWithValue("@e", isEnabled);
        cmd.Parameters.AddWithValue("@ot", (object?)outputTarget ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", stageId);
        return cmd.ExecuteNonQuery() > 0;
    }
}
