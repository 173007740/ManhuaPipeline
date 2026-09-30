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

    /// <summary>
    /// 找同一项目下同规则包还没跑完的运行。
    /// 存在的理由很实在：页面上一个「开始跑」点下去就是一次 LLM 调用，连点几下就多出几份 P0、几份剧本，
    /// 还都是 pending 在那没跑完的孤儿。开跑之前先问一句「上次那个完了吗」。
    /// </summary>
    public int? FindUnfinishedSkillRun(int packId, int? projectId, int? dramaId = null)
    {
        // 立项运行不绑项目（跑 P0 时还没有任何项目），只有漫剧。两个都没有就没什么可防的
        if (projectId is not > 0 && dramaId is not > 0) return null;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 RunId
FROM DirectorSkillRuns
WHERE PackId=@pk AND Status IN ('running','await_confirm','blocked')
  AND ((@pid IS NOT NULL AND ProjectId=@pid)
       OR (@pid IS NULL AND @did IS NOT NULL AND DramaId=@did))
ORDER BY RunId DESC", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@did", (object?)dramaId ?? DBNull.Value);
        var v = cmd.ExecuteScalar();
        return v == null ? null : Convert.ToInt32(v);
    }

    /// <summary>
    /// 丢弃一次运行。产出已经进交付物表了，丢的是「还没跑完的进度」，不是跑出来的东西。
    /// 用户要重新来一次时得有个明确的动作把它收掉，否则那条记录会一直挂着，把后面每次开跑都拦下来。
    /// </summary>
    public void AbandonSkillRun(int runId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillRuns SET Status='abandoned', UpdatedAt=SYSDATETIME()
WHERE RunId=@id AND Status IN ('running','await_confirm','blocked')", conn);
        cmd.Parameters.AddWithValue("@id", runId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>开一次运行。projectId/episodeId 决定产出往哪入库，不传就只跑文本不落业务表。
    /// dramaId 是立项运行的归属：P0 跑在漫剧上，那时一个项目都还没建。</summary>
    public int CreateSkillRun(int packId, int? projectId, int? episodeId, string? title, string? inputsJson,
                              int? dramaId = null)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO DirectorSkillRuns(PackId, ProjectId, EpisodeId, DramaId, Title, InputsJson, Status)
VALUES(@pk, @pid, @eid, @did, @t, @i, 'running'); SELECT SCOPE_IDENTITY();", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@eid", (object?)episodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@did", (object?)dramaId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)inputsJson ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>改落点。页面上的项目/剧集是随时可改的，不能让第一次创建时的选择把整轮锁死。</summary>
    public void UpdateSkillRunTarget(int runId, int? projectId, int? episodeId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE DirectorSkillRuns
SET ProjectId = ISNULL(@pid, ProjectId), EpisodeId = ISNULL(@eid, EpisodeId), UpdatedAt = SYSDATETIME()
WHERE RunId = @id", conn);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@eid", (object?)episodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", runId);
        cmd.ExecuteNonQuery();
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
                                       string? DocsSummary, string? ConfirmedAt,
                                       int? ImportedCount = null, string? ImportError = null,
                                       string? InputText = null);

    public List<SkillStepRow> GetSkillSteps(int runId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<SkillStepRow>();
        // InputText 必须取回来：门禁被放行的那一步要靠它重跑，不然只能拿它那句拒绝通知当素材
        using var cmd = new SqlCommand(@"
SELECT StepId, StageKey, Name, SortOrder, Status, ISNULL(PromptChars,0), OutputText, Gates, Error, DocsSummary,
       CONVERT(varchar(16), ConfirmedAt, 120), ImportedCount, ImportError, InputText
FROM DirectorSkillRunSteps WHERE RunId = @r ORDER BY SortOrder, StepId", conn);
        cmd.Parameters.AddWithValue("@r", runId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkillStepRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3),
                r.GetString(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13)));
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

    /// <summary>查一次运行当前处在什么状态。重启之前判一下，别对已经结掉的那次下手。</summary>
    public string? GetSkillRunStatus(int runId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT Status FROM DirectorSkillRuns WHERE RunId=@id", conn);
        cmd.Parameters.AddWithValue("@id", runId);
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : Convert.ToString(v);
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

    public sealed record SkillRunRow(int RunId, int PackId, int? ProjectId, int? DramaId, string? Title,
                                      string? CurrentStage, string Status, string? CreatedAt,
                                      string? InputsJson);

    /// <summary>
    /// 按项目取最近一次运行。
    /// 「接上次运行」原本只认浏览器里记的编号：清过缓存、换台机器、或者直接从
    /// ?projectId=57 这种链接进来，页面就退回「尚未开始」——跑过的产出其实都还在库里，
    /// 只是没人去问。这里给一条按项目回查的路。
    /// 作废掉的那次（abandoned）不接：接上去只会看到半截产出，不如让他重新点火。
    /// </summary>
    public SkillRunRow? GetLatestSkillRunByProject(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 RunId, PackId, ProjectId, DramaId, Title, CurrentStage, Status,
       CONVERT(varchar(16), CreatedAt, 120), InputsJson
FROM DirectorSkillRuns
WHERE ProjectId = @pid AND Status <> N'abandoned'
ORDER BY RunId DESC", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SkillRunRow(r.GetInt32(0), r.GetInt32(1),
            r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8));
    }

    /// <summary>
    /// 取「整部漫剧共用」的阶段产出——目前就是立项 P0。
    /// 产出挂在运行上，运行又是按集（项目）绑的：第 1 集跑完立项，第 2 集自己没跑过，
    /// 就没有 P0 记录，页面上那一格空着，看着像立项没做。可立项本来就是漫剧级的，
    /// 12 集共用一份。所以本集没有时，拿这部漫剧里集号最小的那集跑出来的那条。
    /// 本集自己跑过就优先用本集的（重跑过的以新那份为准）。
    /// </summary>
    public (int StepId, int EpisodeNumber)? GetSharedStageStep(int projectId, string stageKey)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 s.StepId, ISNULL(p.EpisodeNumber, 999)
FROM DirectorSkillRunSteps s
JOIN DirectorSkillRuns r ON r.RunId = s.RunId
JOIN Projects p ON p.ProjectId = r.ProjectId
WHERE p.DramaId = (SELECT DramaId FROM Projects WHERE ProjectId = @pid)
  AND s.StageKey = @sk
  AND s.Status = N'done'
  AND LEN(ISNULL(s.OutputText, '')) > 0
ORDER BY CASE WHEN p.ProjectId = @pid THEN 0 ELSE 1 END,
         ISNULL(p.EpisodeNumber, 999), s.StepId DESC", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@sk", stageKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt32(0), r.GetInt32(1));
    }

    /// <summary>
    /// 按漫剧直接取漫剧级阶段产出（立项 P0）。
    /// 立项整部漫剧一份，本来就该按漫剧查。只有 projectId 时要先上溯 DramaId、再跨到别的集
    /// 的运行里翻——那条路绕，还容易因为某一集的记录状态不对而整个查不到。
    /// 页面 URL 上带了 dramaId（从项目管理页点进来的）就走这条直路。
    /// </summary>
    public (int StepId, int EpisodeNumber)? GetSharedStageStepByDrama(int dramaId, string stageKey)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 s.StepId, ISNULL(p.EpisodeNumber, 999)
FROM DirectorSkillRunSteps s
JOIN DirectorSkillRuns r ON r.RunId = s.RunId
LEFT JOIN Projects p ON p.ProjectId = r.ProjectId
WHERE s.StageKey = @sk
  AND s.Status = N'done'
  AND LEN(ISNULL(s.OutputText, '')) > 0
  AND (r.DramaId = @did OR p.DramaId = @did)
ORDER BY s.StepId DESC", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);
        cmd.Parameters.AddWithValue("@sk", stageKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetInt32(0), r.GetInt32(1));
    }

    public List<SkillRunRow> GetSkillRuns(int packId)
    {
        using var conn = GetConn(); conn.Open();
        var list = new List<SkillRunRow>();
        // InputsJson 里存着建运行时选定的提示词引擎（promptType）。页面接上上次运行要靠它
        // 把下拉恢复成真正生效的值，不然每次刷新都显示默认的 SD，跟实际跑的那套对不上。
        using var cmd = new SqlCommand(@"
SELECT RunId, PackId, ProjectId, DramaId, Title, CurrentStage, Status, CONVERT(varchar(16), CreatedAt, 120), InputsJson
FROM DirectorSkillRuns WHERE PackId = @pk ORDER BY RunId DESC", conn);
        cmd.Parameters.AddWithValue("@pk", packId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SkillRunRow(r.GetInt32(0), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        }
        return list;
    }
}
