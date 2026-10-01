using System;
using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

public partial class DbService
{
    /// <summary>
    /// 立项的一条记录——就是 Dramas 那一行本身。字段一律可空：没填过的就是 NULL，不是空串。
    ///
    /// 立项是漫剧级的：一部漫剧立一次项，立项完成后再往下长出多个项目（≈ 一集一个）。
    /// 所以这里没有 ProjectId / EpisodeId——那两个是项目层、集层的东西，比立项低一级。
    /// </summary>
    /// <summary>
    /// VideoStyleId = 这部漫剧的「视觉风格」（VideoStyles 里挑的那条：整部剧的影像调性）。
    /// 跟 ArtStyleId（图片风格，资产出图用）是两回事，两个字段各管一段：
    /// 一个定画面长什么样（图），一个定影像什么调子（视频）。
    /// </summary>
    public sealed record DramaBriefRow(
        int DramaId, string Status,
        string? Aspect, string? Delivery, string? Genre, int? ArtStyleId,
        string? Hook, string? Premise, string? Platform,
        int? EpisodeCount, int? EpisodeDuration, string? CharactersJson, string? PromptEngine,
        int? VideoStyleId = null);

    /// <summary>
    /// 项目 → 所属漫剧。立项挂在漫剧层，可流水线和页面手里拿的都是 projectId
    /// （一次运行只跑一集 = 一个项目），得先上溯一级才够得着立项。
    /// </summary>
    public int GetDramaIdByProject(int projectId)
    {
        if (projectId <= 0) return 0;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT DramaId FROM Projects WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        var v = cmd.ExecuteScalar();
        return (v == null || v == DBNull.Value) ? 0 : Convert.ToInt32(v);
    }

    /// <summary>取立项。漫剧没立过项时返回 null（不是返回一条空记录）。</summary>
    public DramaBriefRow? GetDramaBrief(int dramaId)
    {
        if (dramaId <= 0) return null;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT DramaId, Status, Aspect, Delivery, Genre, ArtStyleId, Hook, Premise, Platform,
       EpisodeCount, EpisodeDuration, CharactersJson, PromptEngine, VideoStyleId
FROM Dramas WHERE DramaId = @did", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new DramaBriefRow(
            r.GetInt32(0), r.IsDBNull(1) ? "draft" : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt32(9),
            r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetInt32(13));
    }

    /// <summary>
    /// 存立项。传 null 的字段表示「这次不改动」，保留原值——
    /// 所以只想换提示词引擎时，只传 PromptEngine 一个值就够了，别的字段原样不动。
    /// 一部漫剧永远是同一行，重复保存是更新不是新增。
    /// </summary>
    public int UpsertDramaBrief(int dramaId, string? status,
                                string? aspect, string? delivery, string? genre, int? artStyleId,
                                string? hook, string? premise, string? platform,
                                int? episodeCount, int? episodeDuration,
                                string? charactersJson, string? promptEngine,
                                int? videoStyleId = null)
    {
        if (dramaId <= 0) return 0;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE Dramas SET
    Status          = COALESCE(@st,     Status),
    Aspect          = COALESCE(@aspect, Aspect),
    Delivery        = COALESCE(@deliv,  Delivery),
    Genre           = COALESCE(@genre,  Genre),
    ArtStyleId      = COALESCE(@sid,    ArtStyleId),
    Hook            = COALESCE(@hook,   Hook),
    Premise         = COALESCE(@prem,   Premise),
    Platform        = COALESCE(@plat,   Platform),
    EpisodeCount    = COALESCE(@ec,     EpisodeCount),
    EpisodeDuration = COALESCE(@ed,     EpisodeDuration),
    CharactersJson  = COALESCE(@chars,  CharactersJson),
    PromptEngine    = COALESCE(@engine, PromptEngine),
    VideoStyleId    = COALESCE(@vsid,   VideoStyleId),
    UpdatedAt       = SYSDATETIME()
WHERE DramaId = @did", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);
        cmd.Parameters.AddWithValue("@st", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@aspect", (object?)aspect ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@deliv", (object?)delivery ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@genre", (object?)genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sid", (object?)artStyleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hook", (object?)hook ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prem", (object?)premise ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@plat", (object?)platform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ec", (object?)episodeCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ed", (object?)episodeDuration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@chars", (object?)charactersJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@engine", (object?)promptEngine ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vsid", (object?)videoStyleId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return dramaId;
    }

    // ============================================================
    // 立项结果：P0 跑出来的那份产出
    //
    // 立项整部漫剧一份，跟画幅、集数一样是漫剧的属性，所以存在 Dramas 那一行上。
    // 每一集（一集一个项目）按 dramaId 一次读出来——不用再翻「哪一集跑了 P0」那条运行史。
    //
    // 之所以要单独存一份：P0 跑在漫剧上时项目还没建，入库只认 (ProjectId, EpisodeId)，
    // 产出原文进不了任何业务表，只剩运行步骤里那一段。每一集想看它就得回头翻运行。
    // ============================================================

    public sealed record DramaP0Result(int StepId, string? OutputText, string? FinishedAt);

    /// <summary>取立项结果。没跑过立项就是 null（这一集那一格本来就该是空的）。</summary>
    public DramaP0Result? GetDramaP0Result(int dramaId)
    {
        if (dramaId <= 0) return null;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT P0StepId, P0OutputText, CONVERT(varchar(16), P0FinishedAt, 120)
FROM Dramas
WHERE DramaId = @did AND P0StepId IS NOT NULL AND P0OutputText IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new DramaP0Result(r.GetInt32(0),
                                 r.IsDBNull(1) ? null : r.GetString(1),
                                 r.IsDBNull(2) ? null : r.GetString(2));
    }

    /// <summary>
    /// 存立项结果（覆盖式）。重跑一次立项就换一份，旧的那次仍留在运行步骤里可追溯。
    /// stepId 记下来是为了顺着它能找到那次运行：产出的细则、当时加载了哪些规则都在那儿。
    /// </summary>
    public void SaveDramaP0Result(int dramaId, int stepId, string? outputText)
    {
        if (dramaId <= 0 || stepId <= 0) return;
        if (string.IsNullOrWhiteSpace(outputText)) return;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE Dramas
SET P0StepId = @sid, P0OutputText = @txt, P0FinishedAt = SYSDATETIME(), UpdatedAt = SYSDATETIME()
WHERE DramaId = @did", conn);
        cmd.Parameters.AddWithValue("@sid", stepId);
        cmd.Parameters.AddWithValue("@txt", outputText.Trim());
        cmd.Parameters.AddWithValue("@did", dramaId);
        cmd.ExecuteNonQuery();
    }
}
