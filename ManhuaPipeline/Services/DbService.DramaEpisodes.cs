using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.Json;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 立项（Dramas）与单集项目（Projects）之间的那一段：分集提纲。
///
/// 链路是三步，各自可重来：
///   ① 跑 P0 —— 产出里带一张「N 集分集卡点 + Cliffhanger」表；
///   ② EpisodeOutlineParser 把它解析成 List&lt;EpisodeOutline&gt;，存进 Dramas.EpisodeOutlineJson；
///   ③ BuildEpisodeProjects 按提纲长出 N 个项目，一集一个。
///
/// 提纲存库而不是只放内存，是因为它有两个用途：建项目是一时的，
/// 逐集跑剧本时把「本集的三幕骨架与卡点」喂回去是长期的。
/// </summary>
public partial class DbService
{
    /// <summary>提纲是给自己读的，中文不转义；字段名走 camelCase，前端拿到能直接用。</summary>
    private static readonly JsonSerializerOptions OutlineJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>取分集提纲。没生成过、或者存的内容被手工改坏了，都当没有，不抛异常。</summary>
    public List<EpisodeOutline> GetEpisodeOutline(int dramaId)
    {
        if (dramaId <= 0) return new List<EpisodeOutline>();

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT EpisodeOutlineJson FROM Dramas WHERE DramaId=@did", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);
        var v = cmd.ExecuteScalar();
        if (v == null || v == DBNull.Value) return new List<EpisodeOutline>();

        try
        {
            return JsonSerializer.Deserialize<List<EpisodeOutline>>((string)v, OutlineJson)
                   ?? new List<EpisodeOutline>();
        }
        catch
        {
            return new List<EpisodeOutline>();
        }
    }

    /// <summary>
    /// 取某个单集项目在漫剧提纲里对应的那一条。
    ///
    /// 逐集跑剧本时必须把它喂回去：立项（P0）只锁了全剧的画幅、集数、平台，
    /// 「这一集讲什么、卡在哪」只写在提纲里。逐集跑是另一次运行，上游产出里没有它——
    /// 不给模型，第 2 集就会照着立项自己编一个故事，12 集跑下来可能全在讲同一件事。
    ///
    /// 项目没填集号、漫剧没存提纲、或提纲里没有这一集，都返回 null（不拦着跑）。
    /// </summary>
    public EpisodeOutline? GetEpisodeOutlineForProject(int projectId)
    {
        if (projectId <= 0) return null;

        try
        {
            using var conn = GetConn(); conn.Open();
            using var cmd = new SqlCommand(@"
SELECT e.episodeNumber, e.title, e.outline, e.cliffhanger
FROM Projects p
JOIN Dramas d ON d.DramaId = p.DramaId
CROSS APPLY OPENJSON(d.EpisodeOutlineJson)
  WITH (episodeNumber int '$.episodeNumber',
        title nvarchar(200) '$.title',
        outline nvarchar(max) '$.outline',
        cliffhanger nvarchar(max) '$.cliffhanger') e
WHERE p.ProjectId = @pid
  AND p.EpisodeNumber IS NOT NULL
  AND e.episodeNumber = p.EpisodeNumber", conn);
            cmd.Parameters.AddWithValue("@pid", projectId);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new EpisodeOutline
            {
                EpisodeNumber = r.GetInt32(0),
                Title = r.IsDBNull(1) ? "" : r.GetString(1),
                Outline = r.IsDBNull(2) ? null : r.GetString(2),
                Cliffhanger = r.IsDBNull(3) ? null : r.GetString(3)
            };
        }
        catch
        {
            /* 提纲列缺失、json 被手工改坏、OPENJSON 不可用：都当这一集没有提纲，
               退化成「只有立项」的老行为，绝不让跑剧本这件事本身失败。 */
            return null;
        }
    }

    /// <summary>存分集提纲（覆盖式）。传空列表即清空。</summary>
    /// <summary>立项里填的总集数（Dramas.EpisodeCount）。没填或列不存在返回 0 —— 0 表示不限。</summary>
    public int GetDramaEpisodeCount(int dramaId)
    {
        if (dramaId <= 0) return 0;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT EpisodeCount FROM Dramas WHERE DramaId=@did", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);
        try
        {
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { return 0; }
    }

    /// <summary>
    /// 按立项填的总集数截断提纲：人在立项里写「1 集」，解析出来五条就只留第一条。
    ///
    /// 为什么必须有这道闸：解析器再聪明也只是读模型写的东西，模型完全可能心里按十二集写。
    /// 「总集数」是人在立项表单里亲手填的硬值，它该压过模型的发挥 ——
    /// 否则人看到的就是「我明明写的 1 集，怎么长出 5 集」（dramaId=27 那次）。
    ///
    /// 存提纲之前就要截断：那份 JSON 是「解析分集表」再建项目时的输入，
    /// 留着多余的条目，下一次点那颗按钮又会长回来。
    /// </summary>
    public List<EpisodeOutline> ClampEpisodeOutline(int dramaId, List<EpisodeOutline> outlines)
    {
        if (outlines == null || outlines.Count == 0) return new List<EpisodeOutline>();
        var limit = GetDramaEpisodeCount(dramaId);
        return limit > 0 && outlines.Count > limit ? outlines.Take(limit).ToList() : outlines;
    }

    /// <summary>
    /// 产出里没写分集表时的兜底：只给「总集数 = 1」的单集剧补一条占位提纲。
    ///
    /// 为什么不按总集数补 N 条：填了 12 集就造出 12 个空壳项目，
    /// 名字全叫「第N集」、简介全是空的 —— 比没有更糟，人还得一个个删。
    /// 多集的情况本来就该在 P0 产出里写那张分集表，模型没写说明这一版不行，值得人去看一眼。
    ///
    /// 单集不一样：人就写了「1 集」，产出再怎么组织都是在讲这一集，
    /// 补一条「第1集」的占位去接后面的流水线是合理的。标题与简介在项目里随时能改。
    /// </summary>
    public List<EpisodeOutline> SingleEpisodeFallback(int dramaId)
    {
        if (GetDramaEpisodeCount(dramaId) != 1) return new List<EpisodeOutline>();

        return new List<EpisodeOutline>
        {
            new EpisodeOutline
            {
                EpisodeNumber = 1,
                Title = "第1集",
                Outline = "立项产出里没有分集表，这一条是按立项填写的「总集数 = 1」补的占位。"
                        + "标题与简介可以直接改；跑剧本时会按这一集往下走。",
                Cliffhanger = null
            }
        };
    }

    public void SaveEpisodeOutline(int dramaId, IReadOnlyList<EpisodeOutline> outlines)
    {
        if (dramaId <= 0) return;

        var json = outlines.Count == 0 ? null : JsonSerializer.Serialize(outlines, OutlineJson);

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE Dramas SET EpisodeOutlineJson=@j, UpdatedAt=SYSDATETIME() WHERE DramaId=@did", conn);
        cmd.Parameters.AddWithValue("@j", (object?)json ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@did", dramaId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 按分集提纲建单集项目。幂等：同一漫剧下已有该集号的项目就只更标题与简介，不重复建。
    ///
    /// 所以「重新跑一次 P0 → 再生成一次」不会长出一堆重复项目，
    /// 用户手工改过的项目简介会被新提纲覆盖——重新立项本来就是这个语义。
    /// </summary>
    public (int Created, int Updated) BuildEpisodeProjects(int userId, int dramaId,
                                                           IReadOnlyList<EpisodeOutline> outlines,
                                                           string? projectType = "drama")
    {
        if (userId <= 0 || dramaId <= 0 || outlines.Count == 0) return (0, 0);

        using var conn = GetConn(); conn.Open();
        using var tx = conn.BeginTransaction();

        // 先把这个漫剧下已有的集号捞出来，决定每条提纲是「新建」还是「更新」
        var existing = new Dictionary<int, int>();
        using (var cmd = new SqlCommand(@"
SELECT ProjectId, EpisodeNumber FROM Projects
WHERE DramaId=@did AND UserId=@uid AND Status<>N'deleted' AND EpisodeNumber IS NOT NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("@did", dramaId);
            cmd.Parameters.AddWithValue("@uid", userId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1)) existing[r.GetInt32(1)] = r.GetInt32(0);
        }

        var created = 0;
        var updated = 0;

        try
        {
            foreach (var e in outlines)
            {
                var title = string.IsNullOrWhiteSpace(e.Title) ? $"第{e.EpisodeNumber}集" : e.Title.Trim();
                var desc = ComposeProjectDescription(e);

                if (existing.TryGetValue(e.EpisodeNumber, out var pid))
                {
                    using var upd = new SqlCommand(@"
UPDATE Projects SET Title=@t, Description=@d, EpisodeNumber=@no, UpdatedAt=SYSDATETIME()
WHERE ProjectId=@pid", conn, tx);
                    upd.Parameters.AddWithValue("@t", title);
                    upd.Parameters.AddWithValue("@d", (object?)desc ?? DBNull.Value);
                    upd.Parameters.AddWithValue("@no", e.EpisodeNumber);
                    upd.Parameters.AddWithValue("@pid", pid);
                    upd.ExecuteNonQuery();
                    updated++;
                }
                else
                {
                    using var ins = new SqlCommand(@"
INSERT INTO Projects(UserId,DramaId,Title,Description,EpisodeCount,ProjectType,EpisodeNumber)
OUTPUT INSERTED.ProjectId
VALUES(@uid,@did,@t,@d,1,@pt,@no)", conn, tx);
                    ins.Parameters.AddWithValue("@uid", userId);
                    ins.Parameters.AddWithValue("@did", dramaId);
                    ins.Parameters.AddWithValue("@t", title);
                    ins.Parameters.AddWithValue("@d", (object?)desc ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@pt", NormalizeProjectType(projectType));
                    ins.Parameters.AddWithValue("@no", e.EpisodeNumber);
                    existing[e.EpisodeNumber] = Convert.ToInt32(ins.ExecuteScalar());
                    created++;
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return (created, updated);
    }

    /// <summary>项目简介 = 三幕骨架 + 本集卡点。卡片上只显示两行，骨架放前面。</summary>
    private static string? ComposeProjectDescription(EpisodeOutline e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.Outline)) parts.Add(e.Outline.Trim());
        if (!string.IsNullOrWhiteSpace(e.Cliffhanger)) parts.Add("【本集卡点】" + e.Cliffhanger.Trim());
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>
    /// 立项字段 → P0 的输入文本。顺序和字段名跟立项表单一致——模型认的是这份文本，
    /// 少一项它就少锁一项。
    ///
    /// 两个地方用它：跑立项时当输入；逐集跑剧本时原样塞回去当「立项锁定参数」，
    /// 让下游每一站都看得见画幅、集数这些定死的值，不至于照着平台自己重新推断。
    /// </summary>
    public string BuildP0InputText(int dramaId)
    {
        var b = GetDramaBrief(dramaId);
        if (b == null) return "";

        var sb = new StringBuilder();
        void Line(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine(key + "：" + value!.Trim());
        }

        var styleName = "";
        if (b.ArtStyleId is > 0)
            styleName = GetImageStyles().FirstOrDefault(s => s.StyleId == b.ArtStyleId)?.StyleName ?? "";

        var chars = new List<string>();
        if (!string.IsNullOrWhiteSpace(b.CharactersJson))
        {
            try { chars = JsonSerializer.Deserialize<List<string>>(b.CharactersJson) ?? new List<string>(); }
            catch { /* 手工改坏了就当没角色，不拦着立项 */ }
        }

        Line("画幅", b.Aspect);
        Line("交付形态", b.Delivery);
        Line("题材", b.Genre);
        Line("画风方向", styleName.Length > 0 ? styleName : b.Genre);
        Line("核心爽点", b.Hook);
        Line("总集数", b.EpisodeCount?.ToString());
        Line("单集时长（秒）", b.EpisodeDuration?.ToString());
        Line("目标平台", b.Platform);
        Line("核心设定", b.Premise);
        Line("角色", chars.Count > 0 ? string.Join("，", chars) : null);

        return sb.ToString();
    }

    /// <summary>
    /// 一次运行挂在哪个漫剧上。立项运行直接存 DramaId；逐集跑的那些只存了项目，
    /// 得从项目上溯一级。
    /// </summary>
    public int GetRunDramaId(int runId)
    {
        if (runId <= 0) return 0;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT ISNULL(r.DramaId, (SELECT p.DramaId FROM Projects p WHERE p.ProjectId = r.ProjectId))
FROM DirectorSkillRuns r WHERE r.RunId = @id", conn);
        cmd.Parameters.AddWithValue("@id", runId);
        var v = cmd.ExecuteScalar();
        return (v == null || v == DBNull.Value) ? 0 : Convert.ToInt32(v);
    }

    public sealed record P0StepRow(int StepId, string OutputText);

    /// <summary>
    /// 取这漫剧最近一次 P0 那一步——分集提纲就是从它的产出里解析出来的。
    ///
    /// 立项结果本身存在 Dramas 那一行（GetDramaP0Result），这里是「重解析」时才走的路：
    /// 上次产出没解析出提纲、或者提纲变了要重新解析一遍，就得回到那一步拿原文。
    ///
    /// 两种来源都认：立项运行（挂漫剧，DramaId 有值）和以前跑在某一集项目上的运行
    /// （ProjectId 有值、DramaId 为空）。后者是老数据，不认就得重跑一趟 P0。
    /// </summary>
    public P0StepRow? GetLatestP0Step(int dramaId)
    {
        if (dramaId <= 0) return null;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 s.StepId, s.OutputText
FROM DirectorSkillRunSteps s
JOIN DirectorSkillRuns r ON r.RunId = s.RunId
WHERE s.StageKey = N'P0'
  AND s.OutputText IS NOT NULL AND LEN(s.OutputText) > 0
  AND (r.DramaId = @did
       OR r.ProjectId IN (SELECT ProjectId FROM Projects WHERE DramaId = @did))
ORDER BY s.StepId DESC", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new P0StepRow(r.GetInt32(0), r.GetString(1));
    }

    /// <summary>只要原文时用这个。</summary>
    public string? GetLatestP0Output(int dramaId) => GetLatestP0Step(dramaId)?.OutputText;
}
