using System;
using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

public partial class DbService
{
    /// <summary>剧本的一版。Version 是同项目同集的第几版，重跑不冲掉旧版。</summary>
    public sealed record ScriptDraftRow(int DraftId, int ProjectId, int? EpisodeId,
                                        int? RunId, int? StepId, int Version, string Status,
                                        string? Title, string? Premise, string? Content,
                                        string? PayloadJson, string? CreatedAt);

    /// <summary>
    /// 存一版剧本。Version 自动取同项目同集的 max+1——
    /// 重跑一次就多一版，旧版留在表里能回看，不用靠「覆盖前把旧版另存一份」这种补救。
    /// </summary>
    public int InsertScriptDraft(int projectId, int? episodeId, int? runId, int? stepId,
                                 string? title, string? content, string? payloadJson)
    {
        if (projectId <= 0) return 0;
        using var conn = GetConn(); conn.Open();

        int ver = 1;
        using (var v = new SqlCommand(@"
SELECT ISNULL(MAX(Version), 0) + 1 FROM ScriptDrafts
WHERE ProjectId = @pid
  AND ((@eid IS NOT NULL AND EpisodeId = @eid) OR (@eid IS NULL AND EpisodeId IS NULL))", conn))
        {
            v.Parameters.AddWithValue("@pid", projectId);
            v.Parameters.AddWithValue("@eid", (object?)episodeId ?? DBNull.Value);
            ver = Convert.ToInt32(v.ExecuteScalar());
        }

        using var ins = new SqlCommand(@"
INSERT INTO ScriptDrafts(ProjectId, EpisodeId, RunId, StepId, Version, Status,
                         Title, Premise, Content, PayloadJson)
VALUES(@pid, @eid, @rid, @sid, @ver, 'draft', @title, NULL, @content, @payload);
SELECT SCOPE_IDENTITY();", conn);
        ins.Parameters.AddWithValue("@pid", projectId);
        ins.Parameters.AddWithValue("@eid", (object?)episodeId ?? DBNull.Value);
        ins.Parameters.AddWithValue("@rid", (object?)runId ?? DBNull.Value);
        ins.Parameters.AddWithValue("@sid", (object?)stepId ?? DBNull.Value);
        ins.Parameters.AddWithValue("@ver", ver);
        ins.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        ins.Parameters.AddWithValue("@content", (object?)content ?? DBNull.Value);
        ins.Parameters.AddWithValue("@payload", (object?)payloadJson ?? DBNull.Value);
        return Convert.ToInt32(ins.ExecuteScalar());
    }

    /// <summary>取某项目（某集）最新一版剧本。没有返回 null。</summary>
    public ScriptDraftRow? GetLatestScriptDraft(int projectId, int? episodeId)
    {
        if (projectId <= 0) return null;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 DraftId, ProjectId, EpisodeId, RunId, StepId, Version, Status,
             Title, Premise, Content, PayloadJson, CONVERT(varchar(19), CreatedAt, 120)
FROM ScriptDrafts
WHERE ProjectId = @pid
  AND ((@eid IS NOT NULL AND EpisodeId = @eid) OR (@eid IS NULL AND EpisodeId IS NULL))
ORDER BY Version DESC, DraftId DESC", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@eid", (object?)episodeId ?? DBNull.Value);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ScriptDraftRow(
            r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4),
            r.GetInt32(5), r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
            r.GetString(11));
    }
}
