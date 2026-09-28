using Microsoft.Data.SqlClient;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// 音色库（VoiceLibraryItems）的读写。
/// 与项目级音色参考（VoiceReferences）是两张表：这里存的是可跨项目复用的「音色素材」，
/// 项目里绑定时会复制一份音频文件到项目音色目录，所以删库里的音色不会影响已绑定的项目。
/// </summary>
public partial class DbService
{
    // ==================== 音色库 ====================

    /// <summary>音色库列表：search 匹配名称/标签/备注；category / tag 为空表示不过滤。</summary>
    public List<VoiceLibraryItem> GetVoiceLibrary(int userId, string? search, string? tag, string? category = null)
    {
        var list = new List<VoiceLibraryItem>();
        using var conn = GetConn(); conn.Open();
        var sql = @"
SELECT Id, UserId, Name, ISNULL(Category,''), ISNULL(Tag,''), ISNULL(Note,''), AudioUrl,
       ISNULL(ImageUrl,''), ISNULL(OriginalFileName,''), DurationSec, CreatedAt, UpdatedAt
FROM VoiceLibraryItems
WHERE UserId = @uid
  AND (@q  IS NULL OR Name LIKE '%' + @q + '%' OR ISNULL(Tag,'') LIKE '%' + @q + '%' OR ISNULL(Note,'') LIKE '%' + @q + '%')
  AND (@tag IS NULL OR Tag = @tag)
  AND (@cat IS NULL OR Category = @cat)
ORDER BY UpdatedAt DESC, Id DESC";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@q", string.IsNullOrWhiteSpace(search) ? (object)DBNull.Value : search.Trim());
        cmd.Parameters.AddWithValue("@tag", string.IsNullOrWhiteSpace(tag) ? (object)DBNull.Value : tag.Trim());
        cmd.Parameters.AddWithValue("@cat", string.IsNullOrWhiteSpace(category) ? (object)DBNull.Value : category.Trim());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VoiceLibraryItem
            {
                Id = r.GetInt32(0),
                UserId = r.GetInt32(1),
                Name = r.GetString(2),
                Category = r.GetString(3),
                Tag = r.GetString(4),
                Note = r.GetString(5),
                AudioUrl = r.GetString(6),
                ImageUrl = r.IsDBNull(7) ? null : r.GetString(7),
                OriginalFileName = r.GetString(8),
                DurationSec = r.IsDBNull(9) ? null : r.GetInt32(9),
                CreatedAt = r.GetDateTime(10),
                UpdatedAt = r.GetDateTime(11)
            });
        }
        return list;
    }

    /// <summary>按 Id 取单条（删除与「从音色库绑定」时用，不校验归属，调用方负责）。</summary>
    public VoiceLibraryItem? GetVoiceLibraryItemById(int id)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT Id, UserId, Name, ISNULL(Category,''), ISNULL(Tag,''), ISNULL(Note,''), AudioUrl,
       ISNULL(ImageUrl,''), ISNULL(OriginalFileName,''), DurationSec, CreatedAt, UpdatedAt
FROM VoiceLibraryItems WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new VoiceLibraryItem
        {
            Id = r.GetInt32(0),
            UserId = r.GetInt32(1),
            Name = r.GetString(2),
            Category = r.GetString(3),
            Tag = r.GetString(4),
            Note = r.GetString(5),
            AudioUrl = r.GetString(6),
            ImageUrl = r.IsDBNull(7) ? null : r.GetString(7),
            OriginalFileName = r.GetString(8),
            DurationSec = r.IsDBNull(9) ? null : r.GetInt32(9),
            CreatedAt = r.GetDateTime(10),
            UpdatedAt = r.GetDateTime(11)
        };
    }

    /// <summary>已有同名音色时视为重复（同一用户下音色名唯一）。</summary>
    public bool VoiceLibraryNameExists(int userId, string name, int? exceptId = null)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT COUNT(*) FROM VoiceLibraryItems
WHERE UserId = @uid AND Name = @name AND (@except IS NULL OR Id <> @except)", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@except", (object?)exceptId ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public int InsertVoiceLibraryItem(int userId, string name, string? category, string? tag, string? note,
                                      string audioUrl, string? originalFileName, int? durationSec,
                                      string? imageUrl = null)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO VoiceLibraryItems(UserId, Name, Category, Tag, Note, AudioUrl, ImageUrl, OriginalFileName, DurationSec)
VALUES(@uid, @name, @cat, @tag, @note, @url, @img, @file, @dur);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@cat", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tag", (object?)tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", audioUrl);
        cmd.Parameters.AddWithValue("@img", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@file", (object?)originalFileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", (object?)durationSec ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>单独换封面图（编辑弹窗里选了新图时调用）。</summary>
    public bool UpdateVoiceLibraryImage(int userId, int id, string? imageUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE VoiceLibraryItems SET ImageUrl=@img, UpdatedAt=SYSDATETIME()
WHERE Id=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@img", (object?)imageUrl ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool UpdateVoiceLibraryItem(int userId, int id, string name, string? category, string? tag, string? note)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE VoiceLibraryItems SET Name=@name, Category=@cat, Tag=@tag, Note=@note, UpdatedAt=SYSDATETIME()
WHERE Id=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@cat", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tag", (object?)tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteVoiceLibraryItem(int userId, int id)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM VoiceLibraryItems WHERE Id=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>已用过的标签（给页面做筛选下拉）。</summary>
    public List<string> GetVoiceLibraryTags(int userId)
    {
        var list = new List<string>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT DISTINCT Tag FROM VoiceLibraryItems
WHERE UserId=@uid AND Tag IS NOT NULL AND LTRIM(RTRIM(Tag)) <> ''
ORDER BY Tag", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
