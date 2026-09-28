using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

/// <summary>
/// 流水线运行记录的读写。
/// 跟 DirectorSkillDocs/Stages 的区别：那两张表是「定义」（可反复编辑），这张是「历史」（只追加、不改写）。
/// 每次跑都把当时用的规则清单和原始产出留下来，规则改了也查得到当初是怎么跑的。
/// </summary>
public partial class DbService
{
    public sealed record SkillDocFull(int DocId, string Title, string Scope, string? ScopeValue, string Content);

    /// <summary>取包内全部文档正文（引擎拼 prompt 用，文档量在几十份级别，一次取全比多次往返划算）。</summary>
    public List<SkillDocFull> GetSkillDocsWithContent(int packId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<SkillDocFull>();
        using var cmd = new SqlCommand(@"
SELECT DocId, Title, Scope, ScopeValue, ISNULL(Content,'')
FROM DirectorSkillDocs
WHERE PackId = @pk
ORDER BY SortOrder, DocId", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkillDocFull(r.GetInt32(0), r.GetString(1), r.GetString(2),
                                      r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        }
        return list;
    }

    public int CreateSkillRun(int packId, int? projectId, string? title, string? inputsJson)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO DirectorSkillRuns(PackId, ProjectId, Title, InputsJson, Status)
VALUES(@pk, @pid, @t, @i, 'running'); SELECT SCOPE_IDENTITY();", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)inputsJson ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateSkillRun(int runId, string? stage, string status)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillRuns
SET CurrentStage = ISNULL(@s, CurrentStage), Status = @st, UpdatedAt = SYSDATETIME()
WHERE RunId = @id", conn);
        cmd.Parameters.AddWithValue("@s", (object?)stage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@id", runId);
        cmd.ExecuteNonQuery();
    }

    public int CreateSkillStep(int runId, string stageKey, string? name, int sortOrder,
                               string? inputText, string? docsSummary, int promptChars, string? gates)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO DirectorSkillRunSteps(RunId, StageKey, Name, SortOrder, InputText, DocsSummary, PromptChars, Gates, Status)
VALUES(@r, @k, @n, @o, @i, @d, @c, @g, 'running'); SELECT SCOPE_IDENTITY();", conn);
        cmd.Parameters.AddWithValue("@r", runId);
        cmd.Parameters.AddWithValue("@k", stageKey);
        cmd.Parameters.AddWithValue("@n", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@o", sortOrder);
        cmd.Parameters.AddWithValue("@i", (object?)inputText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@d", (object?)docsSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", promptChars);
        cmd.Parameters.AddWithValue("@g", (object?)gates ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void FinishSkillStep(int stepId, string status, string? outputText, string? outputJson, string? error)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillRunSteps
SET Status = @st, OutputText = @o, OutputJson = @j, Error = @e, UpdatedAt = SYSDATETIME()
WHERE StepId = @id", conn);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@o", (object?)outputText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@j", (object?)outputJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", stepId);
        cmd.ExecuteNonQuery();
    }

    public void ConfirmSkillStep(int stepId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillRunSteps SET Status='done', ConfirmedAt=SYSDATETIME(), UpdatedAt=SYSDATETIME()
WHERE StepId=@id", conn);
        cmd.Parameters.AddWithValue("@id", stepId);
        cmd.ExecuteNonQuery();
    }

    public sealed record SkillStepRow(int StepId, string StageKey, string? Name, int SortOrder, string Status,
                                       int PromptChars, string? OutputText, string? Gates, string? Error,
                                       string? DocsSummary, string? ConfirmedAt);

    public List<SkillStepRow> GetSkillSteps(int runId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<SkillStepRow>();
        using var cmd = new SqlCommand(@"
SELECT StepId, StageKey, Name, SortOrder, Status, ISNULL(PromptChars,0), OutputText, Gates, Error, DocsSummary,
       CONVERT(varchar(16), ConfirmedAt, 120)
FROM DirectorSkillRunSteps WHERE RunId = @r ORDER BY SortOrder, StepId", conn);
        cmd.Parameters.AddWithValue("@r", runId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkillStepRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3),
                r.GetString(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return list;
    }

    public int GetRunPackId(int runId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT PackId FROM DirectorSkillRuns WHERE RunId=@id", conn);
        cmd.Parameters.AddWithValue("@id", runId);
        var v = cmd.ExecuteScalar();
        return v == null ? 0 : Convert.ToInt32(v);
    }

    /// <summary>单步完整产出：原文 + 结构化结果 + 本次加载的规则清单。</summary>
    public (string StageKey, string? OutputText, string? OutputJson, string? DocsSummary)? GetStepOutput(int stepId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT StageKey, OutputText, OutputJson, DocsSummary FROM DirectorSkillRunSteps WHERE StepId=@id", conn);
        cmd.Parameters.AddWithValue("@id", stepId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    public sealed record SkillRunRow(int RunId, int PackId, int? ProjectId, string? Title,
                                      string? CurrentStage, string Status, string? CreatedAt);

    public List<SkillRunRow> GetSkillRuns(int packId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<SkillRunRow>();
        using var cmd = new SqlCommand(@"
SELECT RunId, PackId, ProjectId, Title, CurrentStage, Status, CONVERT(varchar(16), CreatedAt, 120)
FROM DirectorSkillRuns WHERE PackId = @pk ORDER BY RunId DESC", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkillRunRow(r.GetInt32(0), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6)));
        }
        return list;
    }
}
