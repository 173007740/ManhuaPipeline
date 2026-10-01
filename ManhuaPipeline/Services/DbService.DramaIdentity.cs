using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace ManhuaPipeline.Services;

/// <summary>
/// 漫剧复用资产身份层。
///
/// 为什么要有这一层：角色 / 场景 / 道具都是整部漫剧复用的，但资产表是按集（ProjectId）存的——
/// 第 1 集一条「杨彦刚」、第 2 集又一条「杨彦刚」，两条记录互不相干，
/// 没有任何东西说明「这俩是同一个人」。于是每一集各自抽、各自出图，
/// 12 集下来杨彦刚是 12 张不同的脸。
///
/// 身份层把「杨彦刚」这个人提到漫剧上：四张资产表各加一列 IdentityId 指过来，
/// 每集那条只是这个身份在这一集的一份实例。锚点（AnchorImageUrl 等）记着
/// 这个身份的定妆图出自哪一集哪条资产——后面几集照着它出，脸才对得上。
///
/// 四类共用一张表（character / environment / prop / effect），机制一样，不重复建四套。
/// </summary>
public partial class DbService
{
    /// <summary>资产类别前缀（CHR/SCN/PRP/VFX）→ 身份类别。</summary>
    public static string IdentityCategoryOf(string? assetCategory) => (assetCategory ?? "").ToUpperInvariant() switch
    {
        "CHR" => "character",
        "SCN" => "environment",
        "PRP" => "prop",
        "VFX" => "effect",
        _ => ""
    };

    /// <summary>
    /// 名字归一化，用来兜住写法不一致：「外婆（照片）」「外婆 · 回忆态」「杨彦刚（少年）」
    /// 都该认成同一个身份。去掉括号内容、分隔符与空格再比。
    /// </summary>
    private static string NormName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Regex.Replace(s, @"[（(][^）)]*[）)]", "");      // 括号及其内容
        t = Regex.Replace(t, @"[\s·・\-—_/\\、,，。.:：]+", "");  // 分隔符与标点
        return t.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 在本漫剧里找这个身份。三步：精确名 → 归一化名 → 别名。
    /// 一部剧的身份条目就几十条，直接取回来在内存里比，比在 SQL 里拼字符串清楚。
    /// </summary>
    public int? FindDramaIdentity(int dramaId, string category, string name)
    {
        if (dramaId <= 0 || string.IsNullOrEmpty(category) || string.IsNullOrWhiteSpace(name)) return null;

        var want = name.Trim();
        var wantNorm = NormName(want);
        if (wantNorm.Length == 0) return null;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "SELECT IdentityId, Name, AliasJson FROM DramaAssetIdentities WHERE DramaId=@d AND Category=@c", conn);
        cmd.Parameters.AddWithValue("@d", dramaId);
        cmd.Parameters.AddWithValue("@c", category);

        var rows = new List<(int Id, string Name, string? Alias)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                rows.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        }

        foreach (var it in rows)
            if (string.Equals(it.Name, want, StringComparison.OrdinalIgnoreCase)) return it.Id;

        foreach (var it in rows)
            if (NormName(it.Name) == wantNorm) return it.Id;

        // 别名（AliasJson 是字符串数组）
        foreach (var it in rows)
        {
            if (string.IsNullOrWhiteSpace(it.Alias)) continue;
            try
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(it.Alias!);
                if (arr == null) continue;
                foreach (var a in arr)
                {
                    if (string.Equals(a?.Trim(), want, StringComparison.OrdinalIgnoreCase)) return it.Id;
                    if (NormName(a) == wantNorm) return it.Id;
                }
            }
            catch { /* 别名不是合法 JSON 就跳过，不影响别的身份 */ }
        }
        return null;
    }

    /// <summary>
    /// 取这个身份；还没有就建一条。P2a 抽到资产时调它：先认人，再落这一集的那一份。
    /// </summary>
    public int GetOrCreateDramaIdentity(int dramaId, string category, string name, string? description)
    {
        var found = FindDramaIdentity(dramaId, category, name);
        if (found.HasValue)
        {
            // 描述还空着时补上：身份描述只在第一次抽到它时才有内容
            if (!string.IsNullOrWhiteSpace(description))
            {
                using var cu = GetConn(); cu.Open();
                using var cmdDesc = new SqlCommand(
                    @"UPDATE DramaAssetIdentities SET Description=@x, UpdatedAt=SYSDATETIME()
                      WHERE IdentityId=@id AND (Description IS NULL OR Description='')", cu);
                cmdDesc.Parameters.AddWithValue("@x", description!.Trim());
                cmdDesc.Parameters.AddWithValue("@id", found.Value);
                cmdDesc.ExecuteNonQuery();
            }
            return found.Value;
        }

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"INSERT INTO DramaAssetIdentities(DramaId, Category, Name, Description)
              OUTPUT INSERTED.IdentityId VALUES(@d,@c,@n,@x)", conn);
        cmd.Parameters.AddWithValue("@d", dramaId);
        cmd.Parameters.AddWithValue("@c", category);
        cmd.Parameters.AddWithValue("@n", name.Trim());
        cmd.Parameters.AddWithValue("@x", (object?)description ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    /// <summary>把这一集这条资产绑到身份上。</summary>
    public void BindAssetIdentity(string table, int assetId, int identityId)
    {
        if (string.IsNullOrEmpty(table) || assetId <= 0 || identityId <= 0) return;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"UPDATE {table} SET IdentityId=@i WHERE AssetId=@a", conn);
        cmd.Parameters.AddWithValue("@i", identityId);
        cmd.Parameters.AddWithValue("@a", assetId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>这条资产绑的是哪个身份（没有就 0）。</summary>
    public int GetAssetIdentityId(string table, int assetId)
    {
        if (string.IsNullOrEmpty(table) || assetId <= 0) return 0;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"SELECT ISNULL(IdentityId,0) FROM {table} WHERE AssetId=@a", conn);
        cmd.Parameters.AddWithValue("@a", assetId);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt32(v);
    }

    /// <summary>
    /// 这个身份的定妆锚点：图、图那次用的提示词、出自哪一集。
    /// 后面几集出图照着它出，脸 / 结构 / 造型才跟前面几集对得上。
    /// </summary>
    public (string? ImageUrl, string? Prompt, int ProjectId, int AssetId) GetIdentityAnchor(int identityId)
    {
        if (identityId <= 0) return (null, null, 0, 0);
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"SELECT AnchorImageUrl, AnchorPrompt, AnchorProjectId, AnchorAssetId FROM DramaAssetIdentities
              WHERE IdentityId=@id", conn);
        cmd.Parameters.AddWithValue("@id", identityId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null, 0, 0);
        var url = r.IsDBNull(0) ? null : r.GetString(0);
        var prompt = r.IsDBNull(1) ? null : r.GetString(1);
        var pid = r.IsDBNull(2) ? 0 : r.GetInt32(2);
        var aid = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        string? outUrl = string.IsNullOrWhiteSpace(url) ? null : url;
        string? outPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        return (outUrl, outPrompt, pid, aid);
    }

    /// <summary>
    /// 出图成功后登记锚点。**只在还没有锚图时写**——第一个出好图的那一集就是定妆，
    /// 后面几集不该把别人的锚点顶掉（要换锚点由人在页面上手动换）。
    /// </summary>
    public void SetIdentityAnchor(int identityId, string imageUrl, string? prompt, int projectId, int assetId)
    {
        if (identityId <= 0 || string.IsNullOrWhiteSpace(imageUrl)) return;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"UPDATE DramaAssetIdentities
              SET AnchorImageUrl=@u, AnchorPrompt=@p, AnchorProjectId=@pid, AnchorAssetId=@aid, UpdatedAt=SYSDATETIME()
              WHERE IdentityId=@id AND (AnchorImageUrl IS NULL OR AnchorImageUrl='')", conn);
        cmd.Parameters.AddWithValue("@u", imageUrl.Trim());
        cmd.Parameters.AddWithValue("@p", (object?)prompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@aid", assetId);
        cmd.Parameters.AddWithValue("@id", identityId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>这条资产当前那张图的站内地址（/uploads/...），没有图返回 null。</summary>
    public string? GetAssetImageUrl(string table, int assetId)
    {
        if (string.IsNullOrEmpty(table) || assetId <= 0) return null;
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"SELECT ImageUrl FROM {table} WHERE AssetId=@a", conn);
        cmd.Parameters.AddWithValue("@a", assetId);
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return null;
        var s = (string)v;
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// 同一个身份在**别的集**已经出好的图：给人手工挑「这一集直接用第几集那张」用。
    /// 跟自动锚定的区别在这儿 —— 自动锚定是拿那张当参考图重新出一张，
    /// 这里是不出图，直接把那一集那张拿过来用（同一张脸，不用再花一次调用）。
    /// </summary>
    public List<(int AssetId, int ProjectId, int Episode, string Name, string ImageUrl)> GetIdentityImageCandidates(
        int identityId, int excludeProjectId, string table)
    {
        var list = new List<(int, int, int, string, string)>();
        if (identityId <= 0 || string.IsNullOrEmpty(table)) return list;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($@"
SELECT a.AssetId, a.ProjectId, ISNULL(p.EpisodeNumber,0), a.Name, a.ImageUrl
FROM {table} a JOIN Projects p ON p.ProjectId = a.ProjectId
WHERE a.IdentityId=@id AND a.ProjectId<>@pid AND ISNULL(a.ImageUrl,'')<>''
ORDER BY ISNULL(p.EpisodeNumber,0), a.AssetId", conn);
        cmd.Parameters.AddWithValue("@id", identityId);
        cmd.Parameters.AddWithValue("@pid", excludeProjectId);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>
    /// 这一集这个身份的条目：给 P2c 注入用——「上一集已经出过图的同名角色 / 场景 / 道具」。
    /// 取最近一集有锚图的那些，按集号升序。
    /// </summary>
    public List<(string Category, string Name, string? ImageUrl, string? Prompt, int Episode)> GetEpisodeAnchors(
        int dramaId, int projectId)
    {
        var list = new List<(string, string, string?, string?, int)>();
        if (dramaId <= 0) return list;

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT i.Category, i.Name, i.AnchorImageUrl, i.AnchorPrompt, ISNULL(p.EpisodeNumber,0)
FROM DramaAssetIdentities i
LEFT JOIN Projects p ON p.ProjectId = i.AnchorProjectId
WHERE i.DramaId=@d AND i.AnchorImageUrl IS NOT NULL AND i.AnchorImageUrl<>''
  AND (i.AnchorProjectId IS NULL OR i.AnchorProjectId <> @pid)
ORDER BY i.Category, ISNULL(p.EpisodeNumber,0)", conn);
        cmd.Parameters.AddWithValue("@d", dramaId);
        cmd.Parameters.AddWithValue("@pid", projectId);

        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1),
                      r.IsDBNull(2) ? null : r.GetString(2),
                      r.IsDBNull(3) ? null : r.GetString(3),
                      r.IsDBNull(4) ? 0 : r.GetInt32(4)));
        return list;
    }
}
