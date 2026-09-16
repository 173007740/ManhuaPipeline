using Microsoft.Data.SqlClient;
using ManhuaPipeline.Models;
using System.Text.Json;

namespace ManhuaPipeline.Services;

public partial class DbService
{
    private readonly string _connStr;

    public DbService(IConfiguration config)
    {
        _connStr = config.GetConnectionString("DefaultConnection")
            ?? throw new Exception("数据库连接字符串未配置");
    }

    private SqlConnection GetConn() => new SqlConnection(_connStr);

    // ========== �û� ==========
    public User? GetUserById(int id)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT UserId,Username,Email,PasswordHash,Nickname,Avatar,Role,IsActive,CreatedAt,LastLoginAt FROM Users WHERE UserId=@id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadUser(r);
        return null;
    }

    public Drama? GetDrama(int id, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Dramas WHERE DramaId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return new Drama
        {
            DramaId = (int)r["DramaId"],
            UserId = (int)r["UserId"],
            Title = (string)r["Title"],
            Description = r["Description"] == DBNull.Value ? null : (string)r["Description"],
            CoverImage = r["CoverImage"] == DBNull.Value ? null : (string)r["CoverImage"],
        CreatedAt = (DateTime)r["CreatedAt"],
            UpdatedAt = (DateTime)r["UpdatedAt"]
        };
        return null;
    }

    public List<Drama> GetDramas(int userId)
    {
        var list = new List<Drama>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Dramas WHERE UserId=@uid ORDER BY UpdatedAt DESC", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new Drama
        {
            DramaId = (int)r["DramaId"],
            UserId = (int)r["UserId"],
            Title = (string)r["Title"],
            Description = r["Description"] == DBNull.Value ? null : (string)r["Description"],
            CoverImage = r["CoverImage"] == DBNull.Value ? null : (string)r["CoverImage"],
            CreatedAt = (DateTime)r["CreatedAt"],
            UpdatedAt = (DateTime)r["UpdatedAt"]
        });
        return list;
    }

    public int CreateDrama(int userId, string title, string? description)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("INSERT INTO Dramas(UserId,Title,Description) OUTPUT INSERTED.DramaId VALUES(@uid,@t,@d)", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@t", title);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public bool UpdateDrama(int dramaId, int userId, string title, string? description, string? coverImage)
    {
        using var conn = GetConn(); conn.Open();
        if (coverImage != null)
        {
            using var cmd = new SqlCommand("UPDATE Dramas SET Title=@t, Description=@d, CoverImage=@c, UpdatedAt=GETDATE() WHERE DramaId=@id AND UserId=@uid", conn);
            cmd.Parameters.AddWithValue("@id", dramaId);
            cmd.Parameters.AddWithValue("@uid", userId);
            cmd.Parameters.AddWithValue("@t", title);
            cmd.Parameters.AddWithValue("@d", description ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@c", coverImage);
            return cmd.ExecuteNonQuery() > 0;
        }
        else
        {
            using var cmd = new SqlCommand("UPDATE Dramas SET Title=@t, Description=@d, UpdatedAt=GETDATE() WHERE DramaId=@id AND UserId=@uid", conn);
            cmd.Parameters.AddWithValue("@id", dramaId);
            cmd.Parameters.AddWithValue("@uid", userId);
            cmd.Parameters.AddWithValue("@t", title);
            cmd.Parameters.AddWithValue("@d", description ?? (object)DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteDrama(int dramaId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            var deleted = false;
            // 先获取该漫剧下的所有项目ID
            var projIds = new List<int>();
            using var getProj = new SqlCommand("SELECT ProjectId FROM Projects WHERE DramaId=@did AND UserId=@uid", conn, txn);
            getProj.Parameters.AddWithValue("@did", dramaId);
            getProj.Parameters.AddWithValue("@uid", userId);
            using var r = getProj.ExecuteReader();
            while (r.Read()) projIds.Add((int)r[0]);
            r.Close();
            foreach (var pid in projIds)
            {
                using var sd = new SqlCommand("DELETE FROM StageData WHERE ProjectId=@pid", conn, txn);
                sd.Parameters.AddWithValue("@pid", pid); sd.ExecuteNonQuery();
                using var ep = new SqlCommand("DELETE FROM Episodes WHERE ProjectId=@pid", conn, txn);
                ep.Parameters.AddWithValue("@pid", pid); ep.ExecuteNonQuery();
                using var edp = new SqlCommand("DELETE FROM EpisodeDirectorPlans WHERE ProjectId=@pid", conn, txn);
                edp.Parameters.AddWithValue("@pid", pid); edp.ExecuteNonQuery();
                using var eds = new SqlCommand("DELETE FROM EpisodeDirectorStates WHERE ProjectId=@pid", conn, txn);
                eds.Parameters.AddWithValue("@pid", pid); eds.ExecuteNonQuery();
                using var sf = new SqlCommand("DELETE FROM StoryboardFrames WHERE ProjectId=@pid", conn, txn);
                sf.Parameters.AddWithValue("@pid", pid); sf.ExecuteNonQuery();
                using var sp = new SqlCommand("DELETE FROM SeedancePrompts WHERE ProjectId=@pid", conn, txn);
                sp.Parameters.AddWithValue("@pid", pid); sp.ExecuteNonQuery();
                using var ca = new SqlCommand("DELETE FROM CharacterAssets WHERE ProjectId=@pid", conn, txn);
                ca.Parameters.AddWithValue("@pid", pid); ca.ExecuteNonQuery();
                using var pa = new SqlCommand("DELETE FROM PropAssets WHERE ProjectId=@pid", conn, txn);
                pa.Parameters.AddWithValue("@pid", pid); pa.ExecuteNonQuery();
                using var ea = new SqlCommand("DELETE FROM EnvironmentAssets WHERE ProjectId=@pid", conn, txn);
                ea.Parameters.AddWithValue("@pid", pid); ea.ExecuteNonQuery();
                using var vt = new SqlCommand("DELETE FROM VideoGenerationTasks WHERE ProjectId=@pid", conn, txn);
                vt.Parameters.AddWithValue("@pid", pid); vt.ExecuteNonQuery();
            }
            using var delProj = new SqlCommand("DELETE FROM Projects WHERE DramaId=@did AND UserId=@uid", conn, txn);
            delProj.Parameters.AddWithValue("@did", dramaId);
            delProj.Parameters.AddWithValue("@uid", userId);
            delProj.ExecuteNonQuery();
            using var del = new SqlCommand("DELETE FROM Dramas WHERE DramaId=@id AND UserId=@uid", conn, txn);
            del.Parameters.AddWithValue("@id", dramaId);
            del.Parameters.AddWithValue("@uid", userId);
            deleted = del.ExecuteNonQuery() > 0;
            txn.Commit();
            return deleted;
        }
        catch { txn.Rollback(); throw; }
    }

    public User? GetUserByUsername(string username)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT UserId,Username,Email,PasswordHash,Nickname,Avatar,Role,IsActive,CreatedAt,LastLoginAt FROM Users WHERE Username=@u", conn);
        cmd.Parameters.AddWithValue("@u", username);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadUser(r);
        return null;
    }

    public void UpdateDramaCover(int dramaId, int userId, string coverUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE Dramas SET CoverImage=@c, UpdatedAt=GETDATE() WHERE DramaId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", dramaId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@c", coverUrl);
        cmd.ExecuteNonQuery();
    }

    public User? GetUserByEmail(string email)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT UserId,Username,Email,PasswordHash,Nickname,Avatar,Role,IsActive,CreatedAt,LastLoginAt FROM Users WHERE Email=@e", conn);
        cmd.Parameters.AddWithValue("@e", email);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadUser(r);
        return null;
    }

    public int CreateUser(string username, string email, string passwordHash, string? nickname = null)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("INSERT INTO Users(Username,Email,PasswordHash,Nickname) OUTPUT INSERTED.UserId VALUES(@u,@e,@p,@n)", conn);
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@e", email);
        cmd.Parameters.AddWithValue("@p", passwordHash);
        cmd.Parameters.AddWithValue("@n", (object?)nickname ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public void UpdateUserProfile(int userId, string? nickname, string? avatar)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Users SET Nickname=@n, Avatar=@a WHERE UserId=@id", conn);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.Parameters.AddWithValue("@n", (object?)nickname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@a", (object?)avatar ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void UpdateLastLogin(int userId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Users SET LastLoginAt=GETDATE() WHERE UserId=@id", conn);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
    }

    private static User ReadUser(SqlDataReader r) => new User
    {
        UserId = (int)r["UserId"],
        Username = (string)r["Username"],
        Email = (string)r["Email"],
        PasswordHash = (string)r["PasswordHash"],
        Nickname = r["Nickname"] == DBNull.Value ? null : (string)r["Nickname"],
        Avatar = r["Avatar"] == DBNull.Value ? null : (string)r["Avatar"],
        Role = (string)r["Role"],
        IsActive = (bool)r["IsActive"],
        LastLoginAt = r["LastLoginAt"] == DBNull.Value ? null : (DateTime)r["LastLoginAt"]
    };

    // ========== ��Ŀ ==========
    
    public List<Project> GetProjectsByDrama(int dramaId, int userId)
    {
        var list = new List<Project>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Projects WHERE DramaId=@did AND UserId=@uid AND Status<>N'deleted' ORDER BY UpdatedAt DESC", conn);
        cmd.Parameters.AddWithValue("@did", dramaId);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadProject(r));
        return list;
    }

public List<Project> GetProjects(int userId)
    {
        var list = new List<Project>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Projects WHERE UserId=@uid AND Status<>N'deleted' ORDER BY UpdatedAt DESC", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadProject(r));
        return list;
    }

    public List<ProjectDurationStat> GetProjectDurationStats(int? dramaId, int userId)
    {
        var list = new List<ProjectDurationStat>();
        using var conn = GetConn(); conn.Open();
        var sql = @"
SELECT p.ProjectId, pr.Title,
  COUNT(*) AS PromptCount,
  SUM(ISNULL(p.Duration,0)) AS TotalDurationSec,
  SUM(CASE WHEN p.Status IN ('completed','succeeded') THEN 1 ELSE 0 END) AS CompletedCount,
  SUM(CASE WHEN p.Status IN ('completed','succeeded') THEN ISNULL(p.Duration,0) ELSE 0 END) AS CompletedDurationSec,
  SUM(CASE WHEN p.VideoUrl IS NOT NULL AND p.VideoUrl<>'' THEN 1 ELSE 0 END) AS HasVideoCount
FROM SeedancePrompts p
JOIN Projects pr ON pr.ProjectId = p.ProjectId
WHERE pr.UserId=@uid AND pr.Status<>N'deleted'" + (dramaId.HasValue ? " AND pr.DramaId=@did" : "") + @"
GROUP BY p.ProjectId, pr.Title
ORDER BY p.ProjectId";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        if (dramaId.HasValue) cmd.Parameters.AddWithValue("@did", dramaId.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ProjectDurationStat
            {
                ProjectId = (int)r["ProjectId"],
                Title = (string)r["Title"],
                PromptCount = (int)r["PromptCount"],
                TotalDurationSec = r["TotalDurationSec"] == DBNull.Value ? 0 : Convert.ToInt32(r["TotalDurationSec"]),
                CompletedCount = (int)r["CompletedCount"],
                CompletedDurationSec = r["CompletedDurationSec"] == DBNull.Value ? 0 : Convert.ToInt32(r["CompletedDurationSec"]),
                HasVideoCount = (int)r["HasVideoCount"]
            });
        }
        return list;
    }

    public Project? GetProject(int id, int userId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Projects WHERE ProjectId=@id AND UserId=@uid AND Status<>N'deleted'", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadProject(r);
        return null;
    }

    public bool ProjectBelongsToUser(int projectId, int userId)
    {
        if (projectId <= 0 || userId <= 0) return false;
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM Projects WHERE ProjectId=@pid AND UserId=@uid AND Status<>N'deleted') THEN 1 ELSE 0 END", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@uid", userId);
        return (int)cmd.ExecuteScalar() == 1;
    }

    public bool EpisodeBelongsToProject(int episodeId, int projectId)
    {
        if (episodeId <= 0 || projectId <= 0) return false;
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM Episodes WHERE EpisodeId=@eid AND ProjectId=@pid) THEN 1 ELSE 0 END", conn);
        cmd.Parameters.AddWithValue("@eid", episodeId);
        cmd.Parameters.AddWithValue("@pid", projectId);
        return (int)cmd.ExecuteScalar() == 1;
    }

    public bool PromptBelongsToProject(int promptId, int projectId)
    {
        if (promptId <= 0 || projectId <= 0) return false;
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM SeedancePrompts WHERE PromptId=@prid AND ProjectId=@pid) THEN 1 ELSE 0 END", conn);
        cmd.Parameters.AddWithValue("@prid", promptId);
        cmd.Parameters.AddWithValue("@pid", projectId);
        return (int)cmd.ExecuteScalar() == 1;
    }

    /// <summary>
    /// L5 逐镜状态机：人工置位（accepted 验收通过 / rejected 打回 / ready 恢复自动判定）。
    /// 注意不要与 SeedancePrompts.Status（视频生成状态 pending/processing/completed/failed）混用。
    /// </summary>
    public void UpdatePromptShotStatus(int promptId, string shotStatus)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET ShotStatus=@s WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@s", shotStatus);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// L5 逐镜状态机：按当前资产出图情况重算自动状态——
    /// 本镜在 FrameAssetBindings 里存在 HasImage=0 的资产 → blocked_by_missing_asset，否则 ready。
    /// 人工结论（accepted / rejected）不会被覆盖。返回受影响条数。
    /// </summary>
    public int SyncPromptShotStatusFromAssets(int projectId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE p SET p.ShotStatus = CASE WHEN EXISTS (" +
            "SELECT 1 FROM FrameAssetBindings b WHERE b.FrameId = p.FrameId AND ISNULL(b.HasImage,0) = 0) " +
            "THEN 'blocked_by_missing_asset' ELSE 'ready' END " +
            "FROM SeedancePrompts p " +
            "WHERE p.ProjectId = @pid AND p.FrameId IS NOT NULL " +
            "AND ISNULL(p.ShotStatus,'') NOT IN ('accepted','rejected')", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        return cmd.ExecuteNonQuery();
    }



    public Project? GetProjectById(int id)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Projects WHERE ProjectId=@id AND Status<>N'deleted'", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadProject(r);
        return null;
    }
    public int CreateProject(int userId, int dramaId, string title, string? description, string? scriptContent)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("INSERT INTO Projects(UserId,DramaId,Title,Description,ScriptContent,EpisodeCount) OUTPUT INSERTED.ProjectId VALUES(@uid,@did,@t,@d,@s,12)", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@did", dramaId);
        cmd.Parameters.AddWithValue("@t", title);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", (object?)scriptContent ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public void UpdateProjectBatch(int projectId, int batchNumber)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET CurrentBatch=@b WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.Parameters.AddWithValue("@b", batchNumber);
        cmd.ExecuteNonQuery();
    }

    public int GetMaxEpisodeNumber(int projectId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT ISNULL(MAX(EpisodeNumber),0) FROM Episodes WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        return (int)cmd.ExecuteScalar();
    }
    public void UpdateProjectScript(int id, int userId, string script)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET ScriptContent=@s, UpdatedAt=GETDATE() WHERE ProjectId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@s", script);
        cmd.ExecuteNonQuery();
    }

    public void UpdateProjectStage(int id, int userId, int stage)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET CurrentStage=@s, UpdatedAt=GETDATE() WHERE ProjectId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@s", stage);
        cmd.ExecuteNonQuery();
    }

    public void UpdateEpisodeCount(int id, int userId, int count)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET EpisodeCount=@c, UpdatedAt=GETDATE() WHERE ProjectId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@c", count);
        cmd.ExecuteNonQuery();
    }

    /// <summary>保存/清除分集细化的目标总时长；text 传 null 或空表示清除（回退到剧本头部识别）。</summary>
    public void UpdateProjectTargetDuration(int id, int userId, string? text)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET TargetDurationText=@t, UpdatedAt=GETDATE() WHERE ProjectId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@t", (object?)(string.IsNullOrWhiteSpace(text) ? null : text.Trim()) ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>保存/清除项目的「资产库类型」：资产图同步进参考图库时写进图库的「类型」大类。空值=清除（沿用资产分类）。</summary>
    public void UpdateProjectLibraryCategory(int id, int userId, string? category)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET LibraryCategory=@c, UpdatedAt=GETDATE() WHERE ProjectId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@c", (object?)(string.IsNullOrWhiteSpace(category) ? null : category.Trim()) ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteProject(int id, int userId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE Projects
SET Status=N'deleted', UpdatedAt=GETDATE()
WHERE ProjectId=@id AND UserId=@uid AND Status<>N'deleted'", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        return cmd.ExecuteNonQuery() == 1;
    }

    private static Project ReadProject(SqlDataReader r) => new Project
    {
        ProjectId = (int)r["ProjectId"],
        DramaId = r["DramaId"] == DBNull.Value ? 0 : (int)r["DramaId"],
        UserId = (int)r["UserId"],
        Title = (string)r["Title"],
        Description = r["Description"] == DBNull.Value ? null : (string)r["Description"],
        ScriptContent = r["ScriptContent"] == DBNull.Value ? null : (string)r["ScriptContent"],
        CurrentStage = (int)r["CurrentStage"],
        EpisodeCount = r["EpisodeCount"] == DBNull.Value ? 12 : (int)r["EpisodeCount"],
        Status = (string)r["Status"],
        CreatedAt = (DateTime)r["CreatedAt"],
        UpdatedAt = (DateTime)r["UpdatedAt"],
        CoverImage = r["CoverImage"] == DBNull.Value ? null : (string)r["CoverImage"],
        StyleId = r["StyleId"] == DBNull.Value ? null : (int)r["StyleId"],
        VideoRatio = r["VideoRatio"] == DBNull.Value ? "16:9" : (string)r["VideoRatio"],
        VideoWatermark = r["VideoWatermark"] != DBNull.Value && (bool)r["VideoWatermark"],
        VideoAudio = r["VideoAudio"] == DBNull.Value || (bool)r["VideoAudio"],
        VideoResolution = r["VideoResolution"] == DBNull.Value ? "720p" : (string)r["VideoResolution"],
        CurrentBatch = r["CurrentBatch"] == DBNull.Value ? 1 : (int)r["CurrentBatch"],
        Tags = r["Tags"] == DBNull.Value ? null : (string)r["Tags"],
        LibraryCategory = r["LibraryCategory"] == DBNull.Value ? null : (string)r["LibraryCategory"],
        TargetDurationText = r["TargetDurationText"] == DBNull.Value ? null : (string)r["TargetDurationText"]
    };

    // ========== �׶����� ==========
    public StageData? GetStageData(int projectId, int stageNumber)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM StageData WHERE ProjectId=@pid AND StageNumber=@sn", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@sn", stageNumber);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadStageData(r);
        return null;
    }

    public void SaveStageData(int projectId, int stageNumber, string? content, string? llmResponse, string status)
    {
        using var conn = GetConn();
        conn.Open();
        var existing = GetStageData(projectId, stageNumber);
        if (existing != null)
        {
            using var cmd = new SqlCommand("UPDATE StageData SET Content=@c, LlmResponse=@lr, Status=@s, UpdatedAt=GETDATE() WHERE ProjectId=@pid AND StageNumber=@sn", conn);
            cmd.Parameters.AddWithValue("@pid", projectId);
            cmd.Parameters.AddWithValue("@sn", stageNumber);
            cmd.Parameters.AddWithValue("@c", (object?)content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lr", (object?)llmResponse ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", status);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = new SqlCommand("INSERT INTO StageData(ProjectId,StageNumber,Content,LlmResponse,Status) VALUES(@pid,@sn,@c,@lr,@s)", conn);
            cmd.Parameters.AddWithValue("@pid", projectId);
            cmd.Parameters.AddWithValue("@sn", stageNumber);
            cmd.Parameters.AddWithValue("@c", (object?)content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lr", (object?)llmResponse ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", status);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 只写「结构化产物」列（L1 故事基线 JSON），不动 Content / LlmResponse / Status。
    /// 传 null 表示作废该阶段的结构化产物（例如阶段 1 重跑或人工改写后，阶段 2/3 必须回退到逐阶段调用）。
    /// </summary>
    public void SaveStageStructuredJson(int projectId, int stageNumber, string? structuredJson)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("IF EXISTS(SELECT 1 FROM StageData WHERE ProjectId=@pid AND StageNumber=@sn) " +
            "UPDATE StageData SET StructuredJson=@j, UpdatedAt=GETDATE() WHERE ProjectId=@pid AND StageNumber=@sn " +
            "ELSE INSERT INTO StageData(ProjectId,StageNumber,StructuredJson,Status) VALUES(@pid,@sn,@j,'pending')", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@sn", stageNumber);
        cmd.Parameters.AddWithValue("@j", (object?)structuredJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void ResetStageForContinue(int projectId, int[] stageNumbers)
    {
        using var conn = GetConn();
        conn.Open();
        foreach (var sn in stageNumbers)
        {
            using var cmd = new SqlCommand("IF EXISTS(SELECT 1 FROM StageData WHERE ProjectId=@pid AND StageNumber=@sn) " +
                "UPDATE StageData SET Status='pending', Content=NULL, LlmResponse=NULL, StructuredJson=NULL, UpdatedAt=GETDATE() WHERE ProjectId=@pid AND StageNumber=@sn " +
                "ELSE INSERT INTO StageData(ProjectId,StageNumber,Status) VALUES(@pid,@sn,'pending')", conn);
            cmd.Parameters.AddWithValue("@pid", projectId);
            cmd.Parameters.AddWithValue("@sn", sn);
            cmd.ExecuteNonQuery();
        }
    }
    public List<StageData> GetAllStageData(int projectId)
    {
        var list = new List<StageData>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM StageData WHERE ProjectId=@pid ORDER BY StageNumber", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadStageData(r));
        return list;
    }

   private static StageData ReadStageData(SqlDataReader r) => new StageData
   {
       StageId = (int)r["StageId"],
       ProjectId = (int)r["ProjectId"],
       StageNumber = (int)r["StageNumber"],
       Content = r["Content"] == DBNull.Value ? null : (string)r["Content"],
       Status = (string)r["Status"],
       LlmResponse = r["LlmResponse"] == DBNull.Value ? null : (string)r["LlmResponse"],
       CreatedAt = (DateTime)r["CreatedAt"],
       UpdatedAt = (DateTime)r["UpdatedAt"],
       CurrentBatch = r["CurrentBatch"] == DBNull.Value ? 1 : (int)r["CurrentBatch"],
       StructuredJson = r["StructuredJson"] == DBNull.Value ? null : (string)r["StructuredJson"]
   };

    public void ClearStageProgressLogs(int projectId, int stageNumber)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("DELETE FROM StageProgressLogs WHERE ProjectId=@pid AND StageNumber=@sn", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@sn", stageNumber);
        cmd.ExecuteNonQuery();
    }

    public void AppendStageProgressLog(int projectId, int stageNumber, string logText)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("INSERT INTO StageProgressLogs(ProjectId, StageNumber, LogText) VALUES(@pid,@sn,@text)", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@sn", stageNumber);
        cmd.Parameters.AddWithValue("@text", logText);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetStageProgressLogs(int projectId, int stageNumber)
    {
        var list = new List<string>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT LogText FROM StageProgressLogs WHERE ProjectId=@pid AND StageNumber=@sn ORDER BY ProgressLogId", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@sn", stageNumber);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((string)r["LogText"]);
        return list;
    }

    // ========== DirectorPlan (导演决策层) ==========
    public DirectorPlan? GetDirectorPlan(int projectId, string unitNumber)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM DirectorPlans WHERE ProjectId=@pid AND UnitNumber=@un", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@un", unitNumber);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadDirectorPlan(r);
        return null;
    }

    public List<DirectorPlan> GetDirectorPlans(int projectId)
    {
        var list = new List<DirectorPlan>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM DirectorPlans WHERE ProjectId=@pid ORDER BY EpisodeNumber, UnitNumber", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadDirectorPlan(r));
        return list;
    }

    public void SaveDirectorPlan(DirectorPlan plan)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"IF EXISTS (SELECT 1 FROM DirectorPlans WHERE ProjectId=@pid AND UnitNumber=@un)
            UPDATE DirectorPlans SET
                EpisodeNumber=@ep, UnitType=@ut, DramaticPurpose=@dp, PrimarySubject=@ps, SecondarySubject=@ss,
                ConflictType=@ct, CorePayoff=@cp, EmotionCurve=@ec, RhythmStrategy=@rs, ActionStrategy=@act,
                PerformanceStrategy=@perf, CameraStrategy=@cs, VfxStrategy=@vfx, IntensityLevel=@il,
                CombatGrammarIds=@cgi, CombatRoundCount=@crc, VfxPeakPhase=@vpp, ActionPlan=@ap,
                NeedsReview=@nr, ValidationScore=@vs, ViolationsJson=@vj, RepairCount=@rc, LastValidationAt=@lva, UpdatedAt=GETDATE(),
                FightArcType=@fat, FightSequenceJson=@fsq
            WHERE ProjectId=@pid AND UnitNumber=@un
        ELSE
            INSERT INTO DirectorPlans(ProjectId,EpisodeNumber,UnitNumber,UnitType,DramaticPurpose,PrimarySubject,SecondarySubject,ConflictType,CorePayoff,EmotionCurve,RhythmStrategy,ActionStrategy,PerformanceStrategy,CameraStrategy,VfxStrategy,IntensityLevel,CombatGrammarIds,CombatRoundCount,VfxPeakPhase,ActionPlan,FightArcType,FightSequenceJson,NeedsReview,ValidationScore,ViolationsJson,RepairCount,LastValidationAt)
            VALUES(@pid,@ep,@un,@ut,@dp,@ps,@ss,@ct,@cp,@ec,@rs,@act,@perf,@cs,@vfx,@il,@cgi,@crc,@vpp,@ap,@fat,@fsq,@nr,@vs,@vj,@rc,@lva);", conn);
        cmd.Parameters.AddWithValue("@pid", plan.ProjectId);
        cmd.Parameters.AddWithValue("@ep", plan.EpisodeNumber);
        cmd.Parameters.AddWithValue("@un", plan.UnitNumber ?? "");
        cmd.Parameters.AddWithValue("@ut", plan.UnitType ?? "");
        cmd.Parameters.AddWithValue("@dp", plan.DramaticPurpose ?? "");
        cmd.Parameters.AddWithValue("@ps", plan.PrimarySubject ?? "");
        cmd.Parameters.AddWithValue("@ss", plan.SecondarySubject ?? "");
        cmd.Parameters.AddWithValue("@ct", plan.ConflictType ?? "NonCombat");
        cmd.Parameters.AddWithValue("@cp", plan.CorePayoff ?? "");
        cmd.Parameters.AddWithValue("@ec", plan.EmotionCurve ?? "");
        cmd.Parameters.AddWithValue("@rs", plan.RhythmStrategy ?? "");
        cmd.Parameters.AddWithValue("@act", plan.ActionStrategy ?? "");
        cmd.Parameters.AddWithValue("@perf", plan.PerformanceStrategy ?? "");
        cmd.Parameters.AddWithValue("@cs", plan.CameraStrategy ?? "");
        cmd.Parameters.AddWithValue("@vfx", plan.VfxStrategy ?? "");
        cmd.Parameters.AddWithValue("@il", plan.IntensityLevel);
        cmd.Parameters.AddWithValue("@cgi", plan.CombatGrammarIds ?? "");
        cmd.Parameters.AddWithValue("@crc", plan.CombatRoundCount);
        cmd.Parameters.AddWithValue("@vpp", plan.VfxPeakPhase ?? "");
        cmd.Parameters.AddWithValue("@ap", plan.ActionPlan ?? "");
        cmd.Parameters.AddWithValue("@fat", plan.FightArcType ?? "");
        cmd.Parameters.AddWithValue("@fsq", plan.FightSequenceJson ?? "");
        cmd.Parameters.AddWithValue("@nr", plan.NeedsReview);
        cmd.Parameters.AddWithValue("@vs", plan.ValidationScore);
        cmd.Parameters.AddWithValue("@vj", plan.ViolationsJson ?? "");
        cmd.Parameters.AddWithValue("@rc", plan.RepairCount);
        cmd.Parameters.AddWithValue("@lva", (object?)plan.LastValidationAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SaveDirectorPlanValidation(int projectId, string unitNumber, int score, bool needsReview, string violationsJson, int repairCount)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"UPDATE DirectorPlans SET
            NeedsReview=@nr, ValidationScore=@vs, ViolationsJson=@vj, RepairCount=@rc,
            LastValidationAt=GETDATE(), UpdatedAt=GETDATE()
            WHERE ProjectId=@pid AND UnitNumber=@un;", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@un", unitNumber ?? "");
        cmd.Parameters.AddWithValue("@nr", needsReview);
        cmd.Parameters.AddWithValue("@vs", score);
        cmd.Parameters.AddWithValue("@vj", violationsJson ?? "");
        cmd.Parameters.AddWithValue("@rc", repairCount);
        cmd.ExecuteNonQuery();
    }

    public void ClearDirectorPlans(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM DirectorPlans WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    // ========== EpisodeDirectorPlan / EpisodeDirectorState (Director V3) ==========
    public EpisodeDirectorPlan? GetEpisodeDirectorPlan(int projectId, int episodeNumber)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM EpisodeDirectorPlans WHERE ProjectId=@pid AND EpisodeNumber=@ep", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadEpisodeDirectorPlan(r);
        return null;
    }

    public List<EpisodeDirectorPlan> GetEpisodeDirectorPlans(int projectId)
    {
        var list = new List<EpisodeDirectorPlan>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM EpisodeDirectorPlans WHERE ProjectId=@pid ORDER BY EpisodeNumber", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEpisodeDirectorPlan(r));
        return list;
    }

    public void SaveEpisodeDirectorPlan(EpisodeDirectorPlan plan)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"IF EXISTS (SELECT 1 FROM EpisodeDirectorPlans WHERE ProjectId=@pid AND EpisodeNumber=@ep)
            UPDATE EpisodeDirectorPlans SET
                EpisodeGoal=@goal, EmotionCurve=@emotion, IntensityCurveJson=@ic, PayoffScheduleJson=@ps,
                ReservedVisualsJson=@rv, ForbiddenEarlyPayoffsJson=@fp, RepetitionPolicyJson=@rp,
                UnitEmotionCurveJson=@ue, UnitTransitionsJson=@ut, UnitEndStatesJson=@ues, ClimaxBudgetJson=@cb,
                RawJson=@raw, UpdatedAt=GETDATE()
            WHERE ProjectId=@pid AND EpisodeNumber=@ep
        ELSE
            INSERT INTO EpisodeDirectorPlans(ProjectId,EpisodeNumber,EpisodeGoal,EmotionCurve,IntensityCurveJson,PayoffScheduleJson,ReservedVisualsJson,ForbiddenEarlyPayoffsJson,RepetitionPolicyJson,UnitEmotionCurveJson,UnitTransitionsJson,UnitEndStatesJson,ClimaxBudgetJson,RawJson)
            VALUES(@pid,@ep,@goal,@emotion,@ic,@ps,@rv,@fp,@rp,@ue,@ut,@ues,@cb,@raw);", conn);
        cmd.Parameters.AddWithValue("@pid", plan.ProjectId);
        cmd.Parameters.AddWithValue("@ep", plan.EpisodeNumber);
        cmd.Parameters.AddWithValue("@goal", plan.EpisodeGoal ?? "");
        cmd.Parameters.AddWithValue("@emotion", plan.EmotionCurve ?? "");
        cmd.Parameters.AddWithValue("@ic", JsonSerializer.Serialize(plan.IntensityCurve ?? new List<UnitIntensityPlan>()));
        cmd.Parameters.AddWithValue("@ps", JsonSerializer.Serialize(plan.PayoffSchedule ?? new List<PayoffPoint>()));
        cmd.Parameters.AddWithValue("@rv", JsonSerializer.Serialize(plan.ReservedVisuals ?? new List<ReservedVisual>()));
        cmd.Parameters.AddWithValue("@fp", JsonSerializer.Serialize(plan.ForbiddenEarlyPayoffs ?? new List<string>()));
        cmd.Parameters.AddWithValue("@rp", JsonSerializer.Serialize(plan.RepetitionPolicy ?? new RepetitionPolicy()));
        cmd.Parameters.AddWithValue("@ue", JsonSerializer.Serialize(plan.UnitEmotionCurve ?? new List<UnitEmotionPlan>()));
        cmd.Parameters.AddWithValue("@ut", JsonSerializer.Serialize(plan.UnitTransitions ?? new List<UnitTransition>()));
        cmd.Parameters.AddWithValue("@ues", JsonSerializer.Serialize(plan.UnitEndStates ?? new List<UnitEndState>()));
        cmd.Parameters.AddWithValue("@cb", JsonSerializer.Serialize(plan.ClimaxBudget ?? new ClimaxBudget()));
        cmd.Parameters.AddWithValue("@raw", plan.RawJson ?? "");
        cmd.ExecuteNonQuery();
    }

    public void ClearEpisodeDirectorPlans(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM EpisodeDirectorPlans WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    public EpisodeDirectorState? GetEpisodeDirectorState(int projectId, int episodeNumber)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM EpisodeDirectorStates WHERE ProjectId=@pid AND EpisodeNumber=@ep", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadEpisodeDirectorState(r);
        return null;
    }

    public void SaveEpisodeDirectorState(EpisodeDirectorState state)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"IF EXISTS (SELECT 1 FROM EpisodeDirectorStates WHERE ProjectId=@pid AND EpisodeNumber=@ep)
            UPDATE EpisodeDirectorStates SET
                CameraPatternCountsJson=@cam, CombatPatternCountsJson=@combat, VfxPatternCountsJson=@vfx,
                SlowMotionCount=@slow, MajorExplosionCount=@explosion, CurrentPeakIntensity=@peak,
                SmallClimaxCount=@small, MidClimaxCount=@mid, LargeClimaxCount=@large, UpdatedAt=GETDATE()
            WHERE ProjectId=@pid AND EpisodeNumber=@ep
        ELSE
            INSERT INTO EpisodeDirectorStates(ProjectId,EpisodeNumber,CameraPatternCountsJson,CombatPatternCountsJson,VfxPatternCountsJson,SlowMotionCount,MajorExplosionCount,CurrentPeakIntensity,SmallClimaxCount,MidClimaxCount,LargeClimaxCount)
            VALUES(@pid,@ep,@cam,@combat,@vfx,@slow,@explosion,@peak,@small,@mid,@large);", conn);
        cmd.Parameters.AddWithValue("@pid", state.ProjectId);
        cmd.Parameters.AddWithValue("@ep", state.EpisodeNumber);
        cmd.Parameters.AddWithValue("@cam", JsonSerializer.Serialize(state.CameraPatternCounts ?? new Dictionary<string, int>()));
        cmd.Parameters.AddWithValue("@combat", JsonSerializer.Serialize(state.CombatPatternCounts ?? new Dictionary<string, int>()));
        cmd.Parameters.AddWithValue("@vfx", JsonSerializer.Serialize(state.VfxPatternCounts ?? new Dictionary<string, int>()));
        cmd.Parameters.AddWithValue("@slow", state.SlowMotionCount);
        cmd.Parameters.AddWithValue("@explosion", state.MajorExplosionCount);
        cmd.Parameters.AddWithValue("@peak", state.CurrentPeakIntensity);
        cmd.Parameters.AddWithValue("@small", state.SmallClimaxCount);
        cmd.Parameters.AddWithValue("@mid", state.MidClimaxCount);
        cmd.Parameters.AddWithValue("@large", state.LargeClimaxCount);
        cmd.ExecuteNonQuery();
    }

    public void ClearEpisodeDirectorStates(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM EpisodeDirectorStates WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    public EpisodeUnitStateSnapshot? GetEpisodeUnitStateSnapshot(int projectId, int episodeNumber, string unitNumber)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM EpisodeUnitStateSnapshots WHERE ProjectId=@pid AND EpisodeNumber=@ep AND UnitNumber=@un", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        cmd.Parameters.AddWithValue("@un", unitNumber ?? "");
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadEpisodeUnitStateSnapshot(r);
        return null;
    }

    public List<EpisodeUnitStateSnapshot> GetEpisodeUnitStateSnapshots(int projectId, int episodeNumber)
    {
        var list = new List<EpisodeUnitStateSnapshot>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM EpisodeUnitStateSnapshots WHERE ProjectId=@pid AND EpisodeNumber=@ep ORDER BY UnitNumber", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEpisodeUnitStateSnapshot(r));
        return list;
    }

    public void SaveEpisodeUnitStateSnapshot(EpisodeUnitStateSnapshot snapshot)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"IF EXISTS (SELECT 1 FROM EpisodeUnitStateSnapshots WHERE ProjectId=@pid AND EpisodeNumber=@ep AND UnitNumber=@un)
            UPDATE EpisodeUnitStateSnapshots SET
                StateJson=@json, Source=@source, UpdatedAt=GETDATE()
            WHERE ProjectId=@pid AND EpisodeNumber=@ep AND UnitNumber=@un
        ELSE
            INSERT INTO EpisodeUnitStateSnapshots(ProjectId,EpisodeNumber,UnitNumber,StateJson,Source)
            VALUES(@pid,@ep,@un,@json,@source);", conn);
        cmd.Parameters.AddWithValue("@pid", snapshot.ProjectId);
        cmd.Parameters.AddWithValue("@ep", snapshot.EpisodeNumber);
        cmd.Parameters.AddWithValue("@un", snapshot.UnitNumber ?? "");
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(snapshot.State ?? new UnitEndState()));
        cmd.Parameters.AddWithValue("@source", string.IsNullOrWhiteSpace(snapshot.Source) ? "storyboard" : snapshot.Source);
        cmd.ExecuteNonQuery();
    }

    public void ClearEpisodeUnitStateSnapshots(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM EpisodeUnitStateSnapshots WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    private static EpisodeDirectorPlan ReadEpisodeDirectorPlan(SqlDataReader r) => new EpisodeDirectorPlan
    {
        EpisodeDirectorPlanId = (int)r["EpisodeDirectorPlanId"],
        ProjectId = (int)r["ProjectId"],
        EpisodeNumber = (int)r["EpisodeNumber"],
        EpisodeGoal = r["EpisodeGoal"] == DBNull.Value ? "" : (string)r["EpisodeGoal"],
        EmotionCurve = r["EmotionCurve"] == DBNull.Value ? "" : (string)r["EmotionCurve"],
        IntensityCurve = TryParseJson(r["IntensityCurveJson"] as string, () => new List<UnitIntensityPlan>()),
        PayoffSchedule = TryParseJson(r["PayoffScheduleJson"] as string, () => new List<PayoffPoint>()),
        ReservedVisuals = TryParseJson(r["ReservedVisualsJson"] as string, () => new List<ReservedVisual>()),
        ForbiddenEarlyPayoffs = TryParseJson(r["ForbiddenEarlyPayoffsJson"] as string, () => new List<string>()),
        RepetitionPolicy = TryParseJson(r["RepetitionPolicyJson"] as string, () => new RepetitionPolicy()),
        UnitEmotionCurve = TryParseJson(r["UnitEmotionCurveJson"] as string, () => new List<UnitEmotionPlan>()),
        UnitTransitions = TryParseJson(r["UnitTransitionsJson"] as string, () => new List<UnitTransition>()),
        UnitEndStates = TryParseJson(r["UnitEndStatesJson"] as string, () => new List<UnitEndState>()),
        ClimaxBudget = TryParseJson(r["ClimaxBudgetJson"] as string, () => new ClimaxBudget()),
        RawJson = r["RawJson"] == DBNull.Value ? "" : (string)r["RawJson"],
        CreatedAt = r["CreatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["CreatedAt"],
        UpdatedAt = r["UpdatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["UpdatedAt"]
    };

    private static EpisodeDirectorState ReadEpisodeDirectorState(SqlDataReader r) => new EpisodeDirectorState
    {
        EpisodeDirectorStateId = (int)r["EpisodeDirectorStateId"],
        ProjectId = (int)r["ProjectId"],
        EpisodeNumber = (int)r["EpisodeNumber"],
        CameraPatternCounts = TryParseJson(r["CameraPatternCountsJson"] as string, () => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
        CombatPatternCounts = TryParseJson(r["CombatPatternCountsJson"] as string, () => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
        VfxPatternCounts = TryParseJson(r["VfxPatternCountsJson"] as string, () => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
        SlowMotionCount = r["SlowMotionCount"] == DBNull.Value ? 0 : (int)r["SlowMotionCount"],
        MajorExplosionCount = r["MajorExplosionCount"] == DBNull.Value ? 0 : (int)r["MajorExplosionCount"],
        CurrentPeakIntensity = r["CurrentPeakIntensity"] == DBNull.Value ? 0 : (int)r["CurrentPeakIntensity"],
        SmallClimaxCount = r["SmallClimaxCount"] == DBNull.Value ? 0 : (int)r["SmallClimaxCount"],
        MidClimaxCount = r["MidClimaxCount"] == DBNull.Value ? 0 : (int)r["MidClimaxCount"],
        LargeClimaxCount = r["LargeClimaxCount"] == DBNull.Value ? 0 : (int)r["LargeClimaxCount"],
        CreatedAt = r["CreatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["CreatedAt"],
        UpdatedAt = r["UpdatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["UpdatedAt"]
    };

    private static EpisodeUnitStateSnapshot ReadEpisodeUnitStateSnapshot(SqlDataReader r) => new EpisodeUnitStateSnapshot
    {
        EpisodeUnitStateSnapshotId = (int)r["EpisodeUnitStateSnapshotId"],
        ProjectId = (int)r["ProjectId"],
        EpisodeNumber = (int)r["EpisodeNumber"],
        UnitNumber = (string)r["UnitNumber"],
        StateJson = r["StateJson"] == DBNull.Value ? "{}" : (string)r["StateJson"],
        Source = r["Source"] == DBNull.Value ? "storyboard" : (string)r["Source"],
        State = TryParseJson(r["StateJson"] as string, () => new UnitEndState()),
        CreatedAt = r["CreatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["CreatedAt"],
        UpdatedAt = r["UpdatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["UpdatedAt"]
    };

    private static T TryParseJson<T>(string? json, Func<T> fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback();
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return value ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }

    private static DirectorPlan ReadDirectorPlan(SqlDataReader r) => new DirectorPlan
    {
        DirectorPlanId = (int)r["DirectorPlanId"],
        ProjectId = (int)r["ProjectId"],
        EpisodeNumber = r["EpisodeNumber"] == DBNull.Value ? 0 : (int)r["EpisodeNumber"],
        UnitNumber = (string)r["UnitNumber"],
        UnitType = r["UnitType"] == DBNull.Value ? "" : (string)r["UnitType"],
        DramaticPurpose = r["DramaticPurpose"] == DBNull.Value ? "" : (string)r["DramaticPurpose"],
        PrimarySubject = r["PrimarySubject"] == DBNull.Value ? "" : (string)r["PrimarySubject"],
        SecondarySubject = r["SecondarySubject"] == DBNull.Value ? "" : (string)r["SecondarySubject"],
        ConflictType = r["ConflictType"] == DBNull.Value ? "NonCombat" : (string)r["ConflictType"],
        CorePayoff = r["CorePayoff"] == DBNull.Value ? "" : (string)r["CorePayoff"],
        EmotionCurve = r["EmotionCurve"] == DBNull.Value ? "" : (string)r["EmotionCurve"],
        RhythmStrategy = r["RhythmStrategy"] == DBNull.Value ? "" : (string)r["RhythmStrategy"],
        ActionStrategy = r["ActionStrategy"] == DBNull.Value ? "" : (string)r["ActionStrategy"],
        PerformanceStrategy = r["PerformanceStrategy"] == DBNull.Value ? "" : (string)r["PerformanceStrategy"],
        CameraStrategy = r["CameraStrategy"] == DBNull.Value ? "" : (string)r["CameraStrategy"],
        VfxStrategy = r["VfxStrategy"] == DBNull.Value ? "" : (string)r["VfxStrategy"],
        IntensityLevel = r["IntensityLevel"] == DBNull.Value ? 3 : (int)r["IntensityLevel"],
        CombatGrammarIds = r["CombatGrammarIds"] == DBNull.Value ? "" : (string)r["CombatGrammarIds"],
        CombatRoundCount = r["CombatRoundCount"] == DBNull.Value ? 0 : (int)r["CombatRoundCount"],
        VfxPeakPhase = r["VfxPeakPhase"] == DBNull.Value ? "" : (string)r["VfxPeakPhase"],
        ActionPlan = r["ActionPlan"] == DBNull.Value ? "" : (string)r["ActionPlan"],
        FightArcType = r["FightArcType"] == DBNull.Value ? "" : (string)r["FightArcType"],
        FightSequenceJson = r["FightSequenceJson"] == DBNull.Value ? "" : (string)r["FightSequenceJson"],
        NeedsReview = r["NeedsReview"] != DBNull.Value && (bool)r["NeedsReview"],
        ValidationScore = r["ValidationScore"] == DBNull.Value ? 0 : (int)r["ValidationScore"],
        ViolationsJson = r["ViolationsJson"] == DBNull.Value ? "" : (string)r["ViolationsJson"],
        RepairCount = r["RepairCount"] == DBNull.Value ? 0 : (int)r["RepairCount"],
        LastValidationAt = r["LastValidationAt"] == DBNull.Value ? null : (DateTime?)r["LastValidationAt"],
        CreatedAt = r["CreatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["CreatedAt"],
        UpdatedAt = r["UpdatedAt"] == DBNull.Value ? DateTime.Now : (DateTime)r["UpdatedAt"]
    };
    // ========== �ּ� ==========
    public List<Episode> GetEpisodes(int projectId)
    {
        var list = new List<Episode>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Episodes WHERE ProjectId=@pid ORDER BY EpisodeNumber", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEpisode(r));
        return list;
    }

    public void SaveEpisodes(int projectId, int userId, List<Episode> episodes)
    {
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using var del = new SqlCommand("DELETE FROM Episodes WHERE ProjectId=@pid", conn, txn);
            del.Parameters.AddWithValue("@pid", projectId); del.ExecuteNonQuery();
            foreach (var ep in episodes)
            {
                using var ins = new SqlCommand("INSERT INTO Episodes(ProjectId,UserId,EpisodeNumber,Title,Summary,Content,BatchNumber,SortOrder) VALUES(@pid,@uid,@en,@t,@s,@c,@bn,@so)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId);
                ins.Parameters.AddWithValue("@uid", userId);
                ins.Parameters.AddWithValue("@en", ep.EpisodeNumber);
                ins.Parameters.AddWithValue("@t", ep.Title);
                ins.Parameters.AddWithValue("@s", (object?)ep.Summary ?? DBNull.Value);
                ins.Parameters.AddWithValue("@c", (object?)ep.Content ?? DBNull.Value);
                ins.Parameters.AddWithValue("@so", ep.SortOrder);
                ins.Parameters.AddWithValue("@bn", ep.BatchNumber);
                ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }



    public void InsertEpisode(Episode ep)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("INSERT INTO Episodes(ProjectId,UserId,EpisodeNumber,Title,Summary,Content,BatchNumber,SortOrder) VALUES(@pid,@uid,@en,@t,@s,@c,@bn,@so)", conn);
        cmd.Parameters.AddWithValue("@pid", ep.ProjectId);
        cmd.Parameters.AddWithValue("@uid", ep.UserId);
        cmd.Parameters.AddWithValue("@en", ep.EpisodeNumber);
        cmd.Parameters.AddWithValue("@t", ep.Title);
        cmd.Parameters.AddWithValue("@s", (object?)ep.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", (object?)ep.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@so", ep.SortOrder);
        cmd.Parameters.AddWithValue("@bn", ep.BatchNumber);
        cmd.ExecuteNonQuery();
    }
    private static Episode ReadEpisode(SqlDataReader r) => new Episode
    {
        EpisodeId = (int)r["EpisodeId"],
        ProjectId = (int)r["ProjectId"],
        UserId = (int)r["UserId"],
        EpisodeNumber = (int)r["EpisodeNumber"],
        Title = (string)r["Title"],
        Summary = r["Summary"] == DBNull.Value ? null : (string)r["Summary"],
        Content = r["Content"] == DBNull.Value ? null : (string)r["Content"],
        SortOrder = (int)r["SortOrder"],
        CreatedAt = (DateTime)r["CreatedAt"]
    };

    // ========== �־� ==========
    public List<StoryboardFrame> GetFrames(int episodeId)
    {
        var list = new List<StoryboardFrame>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM StoryboardFrames WHERE EpisodeId=@eid ORDER BY UnitOrder,SortOrder", conn);
        cmd.Parameters.AddWithValue("@eid", episodeId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFrame(r));
        return list;
    }

    public List<StoryboardFrame> GetAllFrames(int projectId)
    {
        var list = new List<StoryboardFrame>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM StoryboardFrames WHERE ProjectId=@pid ORDER BY EpisodeId,UnitOrder,SortOrder", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFrame(r));
        return list;
    }

    public StoryboardFrame? GetFrameById(int frameId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM StoryboardFrames WHERE FrameId=@fid", conn);
        cmd.Parameters.AddWithValue("@fid", frameId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFrame(r) : null;
    }

    public void SaveFrames(int episodeId, int projectId, List<StoryboardFrame> frames)
    {
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using var del = new SqlCommand("DELETE FROM StoryboardFrames WHERE EpisodeId=@eid", conn, txn);
            del.Parameters.AddWithValue("@eid", episodeId); del.ExecuteNonQuery();
            foreach (var f in frames)
            {
                using var ins = new SqlCommand(InsertFrameSql, conn, txn);
                BindFrame(ins, episodeId, projectId, f);
                ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    private static StoryboardFrame ReadFrame(SqlDataReader r) => new StoryboardFrame
    {
        FrameId = (int)r["FrameId"],
        EpisodeId = (int)r["EpisodeId"],
        ProjectId = (int)r["ProjectId"],
        FrameNumber = (int)r["FrameNumber"],
        EpisodeNumber = r["EpisodeNumber"] == DBNull.Value ? null : (int)r["EpisodeNumber"],
        UnitNumber = r["UnitNumber"] == DBNull.Value ? null : (string)r["UnitNumber"],
        UnitType = r["UnitType"] == DBNull.Value ? null : (string)r["UnitType"],
        ShotNumber = r["ShotNumber"] == DBNull.Value ? null : (string)r["ShotNumber"],
        ShotSize = r["ShotSize"] == DBNull.Value ? null : (string)r["ShotSize"],
        CombatBeatIndex = r["CombatBeatIndex"] == DBNull.Value ? null : (int)r["CombatBeatIndex"],
        CombatBeatIds = r["CombatBeatIds"] == DBNull.Value ? null : (string)r["CombatBeatIds"],
        Skills = r["Skills"] == DBNull.Value ? null : (string)r["Skills"],
        Timeline = r["Timeline"] == DBNull.Value ? null : (string)r["Timeline"],
        Description = r["Description"] == DBNull.Value ? null : (string)r["Description"],
        Composition = r["Composition"] == DBNull.Value ? null : (string)r["Composition"],
        Characters = r["Characters"] == DBNull.Value ? null : (string)r["Characters"],
        Dialogue = r["Dialogue"] == DBNull.Value ? null : (string)r["Dialogue"],
        Camera = r["Camera"] == DBNull.Value ? null : (string)r["Camera"],
        Duration = r["Duration"] == DBNull.Value ? null : (string)r["Duration"],
        StartScene = r["StartScene"] == DBNull.Value ? null : (string)r["StartScene"],
        EndScene = r["EndScene"] == DBNull.Value ? null : (string)r["EndScene"],
        Scene = HasColumn(r, "Scene") && r["Scene"] != DBNull.Value ? (string)r["Scene"] : null,
        // L4 镜头状态机六字段（用 HasColumn 兼容尚未执行 Upgrade_StoryboardFrames_L4StateFields.sql 的库）
        StartState = HasColumn(r, "StartState") && r["StartState"] != DBNull.Value ? (string)r["StartState"] : null,
        SingleAction = HasColumn(r, "SingleAction") && r["SingleAction"] != DBNull.Value ? (string)r["SingleAction"] : null,
        EndState = HasColumn(r, "EndState") && r["EndState"] != DBNull.Value ? (string)r["EndState"] : null,
        NextConnection = HasColumn(r, "NextConnection") && r["NextConnection"] != DBNull.Value ? (string)r["NextConnection"] : null,
        ForbiddenChanges = HasColumn(r, "ForbiddenChanges") && r["ForbiddenChanges"] != DBNull.Value ? (string)r["ForbiddenChanges"] : null,
        NewInformation = HasColumn(r, "NewInformation") && r["NewInformation"] != DBNull.Value ? (string)r["NewInformation"] : null,
        UnitOrder = r["UnitOrder"] == DBNull.Value ? 0 : (int)r["UnitOrder"],
        SortOrder = (int)r["SortOrder"]
    };

    // Stage 5 增量写入：同一单元替换，已完成单元不删除
    public void SaveFramesIncremental(int episodeId, int projectId, List<StoryboardFrame> frames)
    {
        if (frames == null || frames.Count == 0) return;
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            foreach (var group in frames.GroupBy(f => f.UnitNumber?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(group.Key))
                {
                    using var del = new SqlCommand("DELETE FROM StoryboardFrames WHERE EpisodeId=@eid AND ProjectId=@pid AND (UnitNumber IS NULL OR LTRIM(RTRIM(UnitNumber)) = '')", conn, txn);
                    del.Parameters.AddWithValue("@eid", episodeId);
                    del.Parameters.AddWithValue("@pid", projectId);
                    del.ExecuteNonQuery();
                }
                else
                {
                    using var del = new SqlCommand("DELETE FROM StoryboardFrames WHERE EpisodeId=@eid AND ProjectId=@pid AND UnitNumber=@un", conn, txn);
                    del.Parameters.AddWithValue("@eid", episodeId);
                    del.Parameters.AddWithValue("@pid", projectId);
                    del.Parameters.AddWithValue("@un", group.Key);
                    del.ExecuteNonQuery();
                }
                foreach (var f in group)
                {
                    using var ins = new SqlCommand(InsertFrameSql, conn, txn);
                    BindFrame(ins, episodeId, projectId, f);
                    ins.ExecuteNonQuery();
                }
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    /// <summary>存量自愈：把「镜头号前缀与所属单元号不一致」的历史帧改写成「单元号-序号」。
    /// 用于修复旧版解析器采信 LLM 笔误（如单元 1.14 下写入 1.4-1）留下的错位编号。</summary>
    public int NormalizeFrameShotNumbers(int projectId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE StoryboardFrames
SET ShotNumber = LTRIM(RTRIM(UnitNumber)) + '-' + RIGHT(ShotNumber, CHARINDEX('-', REVERSE(ShotNumber)) - 1)
WHERE ProjectId = @pid
  AND UnitNumber IS NOT NULL AND LTRIM(RTRIM(UnitNumber)) <> ''
  AND ShotNumber IS NOT NULL AND ShotNumber LIKE '%-%'
  AND CHARINDEX('-', REVERSE(ShotNumber)) > 1
  AND ShotNumber NOT LIKE LTRIM(RTRIM(UnitNumber)) + '-%'", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        return cmd.ExecuteNonQuery();
    }

    public int CountFrameUnits(int projectId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(DISTINCT UnitNumber) FROM StoryboardFrames WHERE ProjectId=@pid AND UnitNumber IS NOT NULL AND LTRIM(RTRIM(UnitNumber)) <> ''", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        return (int)cmd.ExecuteScalar();
    }

    // ========== 分镜帧 ↔ 项目资产 绑定表（Stage 5 后解析写入，Stage 9 读取） ==========
    public List<FrameAssetBinding> GetFrameAssetBindings(int projectId)
    {
        var list = new List<FrameAssetBinding>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT BindingId,ProjectId,FrameId,Category,AssetId,Name,HasImage,SortOrder FROM FrameAssetBindings WHERE ProjectId=@pid ORDER BY FrameId,SortOrder", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFrameAssetBinding(r));
        return list;
    }

    public List<FrameAssetBinding> GetFrameAssetBindings(int projectId, int frameId)
    {
        var list = new List<FrameAssetBinding>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT BindingId,ProjectId,FrameId,Category,AssetId,Name,HasImage,SortOrder FROM FrameAssetBindings WHERE ProjectId=@pid AND FrameId=@fid ORDER BY SortOrder", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@fid", frameId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFrameAssetBinding(r));
        return list;
    }

    /// <summary>整表替换某项目的帧资产绑定（重算解析后调用）。</summary>
    public void ReplaceFrameAssetBindings(int projectId, List<FrameAssetBinding> rows)
    {
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using (var del = new SqlCommand("DELETE FROM FrameAssetBindings WHERE ProjectId=@pid", conn, txn))
            {
                del.Parameters.AddWithValue("@pid", projectId);
                del.ExecuteNonQuery();
            }
            foreach (var b in rows)
            {
                using var ins = new SqlCommand("INSERT INTO FrameAssetBindings(ProjectId,FrameId,Category,AssetId,Name,HasImage,SortOrder) VALUES(@pid,@fid,@cat,@aid,@name,@img,@so)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId);
                ins.Parameters.AddWithValue("@fid", b.FrameId);
                ins.Parameters.AddWithValue("@cat", b.Category);
                ins.Parameters.AddWithValue("@aid", b.AssetId);
                ins.Parameters.AddWithValue("@name", b.Name);
                ins.Parameters.AddWithValue("@img", b.HasImage);
                ins.Parameters.AddWithValue("@so", b.SortOrder);
                ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    private static FrameAssetBinding ReadFrameAssetBinding(SqlDataReader r) => new FrameAssetBinding
    {
        BindingId = (int)r["BindingId"],
        ProjectId = (int)r["ProjectId"],
        FrameId = (int)r["FrameId"],
        Category = (string)r["Category"],
        AssetId = (int)r["AssetId"],
        Name = (string)r["Name"],
        HasImage = (bool)r["HasImage"],
        SortOrder = (int)r["SortOrder"]
    };

    // ---------- 角色音色参考音频（VoiceReferences：项目 + 角色 唯一） ----------
    // 建表脚本：ManhuaPipeline/Database/Upgrade_VoiceReferences.sql（应用不在运行时自动建表）

    /// <summary>查询某项目的全部角色音色参考音频。表缺失时抛 SqlException，由调用方决定容错或提示。</summary>
    public List<VoiceReference> GetVoiceReferences(int projectId)
    {
        var list = new List<VoiceReference>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT VoiceId,ProjectId,CharacterName,AudioUrl,OriginalFileName,CreatedAt FROM VoiceReferences WHERE ProjectId=@pid ORDER BY CharacterName", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new VoiceReference
        {
            VoiceId = (int)r["VoiceId"],
            ProjectId = (int)r["ProjectId"],
            CharacterName = (string)r["CharacterName"],
            AudioUrl = (string)r["AudioUrl"],
            OriginalFileName = r["OriginalFileName"] as string,
            CreatedAt = (DateTime)r["CreatedAt"]
        });
        return list;
    }

    /// <summary>上传/替换某角色的音色参考音频（同项目同角色唯一，存在即覆盖）。返回 VoiceId。</summary>
    public int UpsertVoiceReference(int projectId, string characterName, string audioUrl, string? originalFileName)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand(@"
IF EXISTS(SELECT 1 FROM VoiceReferences WHERE ProjectId=@pid AND CharacterName=@name)
BEGIN
    UPDATE VoiceReferences SET AudioUrl=@url, OriginalFileName=@file, CreatedAt=SYSDATETIME() WHERE ProjectId=@pid AND CharacterName=@name;
    SELECT VoiceId FROM VoiceReferences WHERE ProjectId=@pid AND CharacterName=@name;
END
ELSE
BEGIN
    INSERT INTO VoiceReferences(ProjectId,CharacterName,AudioUrl,OriginalFileName) VALUES(@pid,@name,@url,@file);
    SELECT CAST(SCOPE_IDENTITY() AS INT);
END", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@name", characterName);
        cmd.Parameters.AddWithValue("@url", audioUrl);
        cmd.Parameters.AddWithValue("@file", (object?)originalFileName ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    /// <summary>删除某角色的音色参考音频。返回是否真的删掉了一条。</summary>
    public bool DeleteVoiceReference(int projectId, int voiceId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("DELETE FROM VoiceReferences WHERE ProjectId=@pid AND VoiceId=@vid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@vid", voiceId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ---------- 单元资产绑定（Stage 4 分集细化 → Stage 5 分镜继承） ----------

    /// <summary>查询某项目全部单元资产绑定。</summary>
    public List<UnitAssetBinding> GetUnitAssetBindings(int projectId)
    {
        var result = new List<UnitAssetBinding>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM UnitAssetBindings WHERE ProjectId=@pid ORDER BY EpisodeNumber, UnitNumber, SortOrder", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadUnitAssetBinding(r));
        return result;
    }

    /// <summary>查询某项目指定单元的资产绑定。</summary>
    public List<UnitAssetBinding> GetUnitAssetBindings(int projectId, int episodeNumber, string unitNumber)
    {
        var result = new List<UnitAssetBinding>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM UnitAssetBindings WHERE ProjectId=@pid AND EpisodeNumber=@ep AND UnitNumber=@un ORDER BY SortOrder", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        cmd.Parameters.AddWithValue("@un", unitNumber);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadUnitAssetBinding(r));
        return result;
    }

    /// <summary>整表替换某项目的单元资产绑定（每次跑完分集细化整体重算，与帧绑定的策略一致）。</summary>
    public void ReplaceUnitAssetBindings(int projectId, List<UnitAssetBinding> rows)
    {
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using (var del = new SqlCommand("DELETE FROM UnitAssetBindings WHERE ProjectId=@pid", conn, txn))
            {
                del.Parameters.AddWithValue("@pid", projectId);
                del.ExecuteNonQuery();
            }
            foreach (var b in rows)
            {
                using var ins = new SqlCommand("INSERT INTO UnitAssetBindings(ProjectId,EpisodeNumber,UnitNumber,Category,AssetId,Name,HasImage,SortOrder) VALUES(@pid,@ep,@un,@cat,@aid,@name,@img,@so)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId);
                ins.Parameters.AddWithValue("@ep", b.EpisodeNumber);
                ins.Parameters.AddWithValue("@un", b.UnitNumber);
                ins.Parameters.AddWithValue("@cat", b.Category);
                ins.Parameters.AddWithValue("@aid", b.AssetId);
                ins.Parameters.AddWithValue("@name", b.Name);
                ins.Parameters.AddWithValue("@img", b.HasImage);
                ins.Parameters.AddWithValue("@so", b.SortOrder);
                ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    private static UnitAssetBinding ReadUnitAssetBinding(SqlDataReader r) => new UnitAssetBinding
    {
        BindingId = (int)r["BindingId"],
        ProjectId = (int)r["ProjectId"],
        EpisodeNumber = (int)r["EpisodeNumber"],
        UnitNumber = (string)r["UnitNumber"],
        Category = (string)r["Category"],
        AssetId = (int)r["AssetId"],
        Name = (string)r["Name"],
        HasImage = (bool)r["HasImage"],
        SortOrder = (int)r["SortOrder"]
    };

    private const string InsertFrameSql = "INSERT INTO StoryboardFrames(" +
        "EpisodeId,ProjectId,FrameNumber,EpisodeNumber,UnitNumber,UnitType,ShotNumber,ShotSize,CombatBeatIndex,CombatBeatIds,Skills,Timeline," +
        "Description,Composition,Characters,Dialogue,Camera,Duration,StartScene,EndScene,Scene," +
        "StartState,SingleAction,EndState,NextConnection,ForbiddenChanges,NewInformation," +
        "BatchNumber,SortOrder,UnitOrder) VALUES(" +
        "@eid,@pid,@fn,@en,@un,@ut,@sn,@sz,@cbi,@cbiIds,@sk,@tl,@d,@co,@ch,@dg,@ca,@du,@ss,@es,@sc," +
        "@stSt,@stAct,@enSt,@nxtCon,@forb,@newInfo,@bn,@so,@uo)";

    private static void BindFrame(SqlCommand cmd, int episodeId, int projectId, StoryboardFrame f)
    {
        cmd.Parameters.AddWithValue("@eid", episodeId);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@fn", f.FrameNumber);
        cmd.Parameters.AddWithValue("@d", (object?)f.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@co", (object?)f.Composition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ch", (object?)f.Characters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dg", (object?)f.Dialogue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", (object?)f.Camera ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@du", (object?)f.Duration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ss", (object?)f.StartScene ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@es", (object?)f.EndScene ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sc", (object?)f.Scene ?? DBNull.Value);
        // L4 镜头状态机六字段
        cmd.Parameters.AddWithValue("@stSt", (object?)f.StartState ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stAct", (object?)f.SingleAction ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enSt", (object?)f.EndState ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nxtCon", (object?)f.NextConnection ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@forb", (object?)f.ForbiddenChanges ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@newInfo", (object?)f.NewInformation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@en", (object?)f.EpisodeNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ut", (object?)f.UnitType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sn", (object?)f.ShotNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sz", (object?)f.ShotSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cbi", (object?)f.CombatBeatIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cbiIds", (object?)f.CombatBeatIds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sk", (object?)f.Skills ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tl", (object?)f.Timeline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@un", (object?)f.UnitNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bn", f.BatchNumber);
        cmd.Parameters.AddWithValue("@so", f.SortOrder);
        cmd.Parameters.AddWithValue("@uo", f.UnitOrder);
    }

    // ========== �ʲ� ==========
    public List<CharacterAsset> GetCharacterAssets(int projectId)
    {
        var list = new List<CharacterAsset>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM CharacterAssets WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new CharacterAsset
        {
            AssetId = (int)r["AssetId"],
            ProjectId = (int)r["ProjectId"],
            Name = (string)r["Name"],
            Description = r["Description"] == DBNull.Value ? null : (string)r["Description"],
            ImageUrl = r["ImageUrl"] == DBNull.Value ? null : (string)r["ImageUrl"],
            Attributes = r["Attributes"] == DBNull.Value ? null : (string)r["Attributes"],
            ImagePrompt = r["ImagePrompt"] == DBNull.Value ? null : (string)r["ImagePrompt"],
            NegativePrompt = r["NegativePrompt"] == DBNull.Value ? null : (string)r["NegativePrompt"],
        });
        return list;
    }

    public List<PropAsset> GetPropAssets(int projectId)
    {
        var list = new List<PropAsset>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM PropAssets WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new PropAsset
        {
            AssetId = (int)r["AssetId"],
            ProjectId = (int)r["ProjectId"],
            Name = (string)r["Name"],
            Description = r["Description"] == DBNull.Value ? null : (string)r["Description"],
            ImageUrl = r["ImageUrl"] == DBNull.Value ? null : (string)r["ImageUrl"],
            ImagePrompt = r["ImagePrompt"] == DBNull.Value ? null : (string)r["ImagePrompt"],
            NegativePrompt = r["NegativePrompt"] == DBNull.Value ? null : (string)r["NegativePrompt"]
        });
        return list;
    }

    public List<EffectAsset> GetEffectAssets(int projectId)
    {
        var list = new List<EffectAsset>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM EffectAssets WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new EffectAsset
        {
            AssetId = r.GetInt32(r.GetOrdinal("AssetId")),
            ProjectId = r.GetInt32(r.GetOrdinal("ProjectId")),
            Name = r["Name"] as string ?? "",
            Description = r["Description"] as string,
            ImageUrl = r["ImageUrl"] as string,
            ImagePrompt = r["ImagePrompt"] as string,
            NegativePrompt = r["NegativePrompt"] as string
        });
        return list;
    }

    public List<EnvironmentAsset> GetEnvAssets(int projectId)
    {
        var list = new List<EnvironmentAsset>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM EnvironmentAssets WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new EnvironmentAsset
        {
            AssetId = (int)r["AssetId"],
            ProjectId = (int)r["ProjectId"],
            Name = (string)r["Name"],
            Description = r["Description"] == DBNull.Value ? null : (string)r["Description"],
            ImageUrl = r["ImageUrl"] == DBNull.Value ? null : (string)r["ImageUrl"],
            ImagePrompt = r["ImagePrompt"] == DBNull.Value ? null : (string)r["ImagePrompt"],
            NegativePrompt = r["NegativePrompt"] == DBNull.Value ? null : (string)r["NegativePrompt"]
        });
        return list;
    }

    // ========== ��ʾ�� ==========
    public List<SeedancePrompt> GetPrompts(int projectId)
    {
        var list = new List<SeedancePrompt>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM SeedancePrompts WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new SeedancePrompt
        {
            PromptId = (int)r["PromptId"],
            ProjectId = (int)r["ProjectId"],
            FrameId = r["FrameId"] == DBNull.Value ? null : (int)r["FrameId"],
            PromptText = (string)r["PromptText"],
            PromptTextH3 = r["PromptTextH3"] == DBNull.Value ? null : (string)r["PromptTextH3"],
            NegativePrompt = r["NegativePrompt"] == DBNull.Value ? null : (string)r["NegativePrompt"],
            VideoUrl = r["VideoUrl"] == DBNull.Value ? null : (string)r["VideoUrl"],
            LocalVideoUrl = r["LocalVideoUrl"] == DBNull.Value ? null : (string)r["LocalVideoUrl"],
            Status = (string)r["Status"],
            // L5 逐镜状态机（用 HasColumn 兼容尚未执行 Upgrade_SeedancePrompts_ShotStatus.sql 的库）
            ShotStatus = HasColumn(r, "ShotStatus") && r["ShotStatus"] != DBNull.Value ? (string)r["ShotStatus"] : "ready",
            EpisodeNumber = r["EpisodeNumber"] == DBNull.Value ? 0 : (int)r["EpisodeNumber"],
            ShotLabel = r["ShotLabel"] == DBNull.Value ? null : (string)r["ShotLabel"],
            ShotType = r["ShotType"] == DBNull.Value ? null : (string)r["ShotType"],
            Duration = r["Duration"] == DBNull.Value ? 11 : (int)r["Duration"],
            UnitName = r["UnitName"] == DBNull.Value ? null : (string)r["UnitName"],
            BatchNumber = r["BatchNumber"] == DBNull.Value ? 1 : (int)r["BatchNumber"],
            ReferenceImages = r["ReferenceImages"] == DBNull.Value ? null : (string)r["ReferenceImages"],
            ReferenceVideos = r["ReferenceVideos"] == DBNull.Value ? null : (string)r["ReferenceVideos"],
            ReferenceAudio = r["ReferenceAudio"] == DBNull.Value ? null : (string)r["ReferenceAudio"]
        });
        return list
            .OrderBy(p => string.IsNullOrWhiteSpace(p.UnitName) && p.EpisodeNumber <= 0 ? 1 : 0)
            .ThenBy(p => p.EpisodeNumber)
            .ThenBy(p => GetUnitSortKey(p.UnitName))
            .ThenBy(p => GetShotSortKey(p.ShotLabel))
            .ToList();
    }

    private static double GetUnitSortKey(string? unitName)
    {
        if (string.IsNullOrWhiteSpace(unitName)) return double.MaxValue;
        var parts = unitName.Split('.');
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var episode) &&
            int.TryParse(parts[1], out var unit))
        {
            return episode * 1000.0 + unit;
        }
        return double.MaxValue;
    }

    private static int GetShotSortKey(string? shotLabel)
    {
        if (string.IsNullOrWhiteSpace(shotLabel)) return int.MaxValue;
        var idx = shotLabel.LastIndexOf('-');
        var num = idx >= 0 ? shotLabel.Substring(idx + 1) : shotLabel;
        return int.TryParse(num, out var n) ? n : int.MaxValue;
    }

    public SeedancePrompt? GetPrompt(int promptId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM SeedancePrompts WHERE PromptId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", promptId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return new SeedancePrompt
        {
            PromptId = (int)r["PromptId"],
            ProjectId = (int)r["ProjectId"],
            FrameId = r["FrameId"] == DBNull.Value ? null : (int)r["FrameId"],
            PromptText = (string)r["PromptText"],
            PromptTextH3 = r["PromptTextH3"] == DBNull.Value ? null : (string)r["PromptTextH3"],
            NegativePrompt = r["NegativePrompt"] == DBNull.Value ? null : (string)r["NegativePrompt"],
            VideoUrl = r["VideoUrl"] == DBNull.Value ? null : (string)r["VideoUrl"],
            Status = (string)r["Status"],
            // L5 逐镜状态机（用 HasColumn 兼容尚未执行 Upgrade_SeedancePrompts_ShotStatus.sql 的库）
            ShotStatus = HasColumn(r, "ShotStatus") && r["ShotStatus"] != DBNull.Value ? (string)r["ShotStatus"] : "ready",
            EpisodeNumber = r["EpisodeNumber"] == DBNull.Value ? 0 : (int)r["EpisodeNumber"],
            ShotLabel = r["ShotLabel"] == DBNull.Value ? null : (string)r["ShotLabel"],
            ShotType = r["ShotType"] == DBNull.Value ? null : (string)r["ShotType"],
            UnitName = r["UnitName"] == DBNull.Value ? null : (string)r["UnitName"],
            BatchNumber = r["BatchNumber"] == DBNull.Value ? 1 : (int)r["BatchNumber"],
            ReferenceImages = r["ReferenceImages"] == DBNull.Value ? null : (string)r["ReferenceImages"],
            ReferenceVideos = r["ReferenceVideos"] == DBNull.Value ? null : (string)r["ReferenceVideos"],
            ReferenceAudio = r["ReferenceAudio"] == DBNull.Value ? null : (string)r["ReferenceAudio"],
        };
        return null;
    }



    // ========== Asset write methods ==========
    public void SaveCharacterAssets(int projectId, List<CharacterAsset> assets)
    {
        using var conn = GetConn(); conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using var del = new SqlCommand("DELETE FROM CharacterAssets WHERE ProjectId=@pid", conn, txn);
            del.Parameters.AddWithValue("@pid", projectId); del.ExecuteNonQuery();
            foreach (var a in assets)
            {
                using var ins = new SqlCommand("INSERT INTO CharacterAssets(ProjectId,Name,Description,ImageUrl,Attributes,ImagePrompt,NegativePrompt) VALUES(@pid,@n,@d,@i,@at,@p,@np)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId); ins.Parameters.AddWithValue("@n", a.Name);
                ins.Parameters.AddWithValue("@d", (object?)a.Description ?? DBNull.Value);
                ins.Parameters.AddWithValue("@i", (object?)a.ImageUrl ?? DBNull.Value);
                ins.Parameters.AddWithValue("@at", (object?)a.Attributes ?? DBNull.Value);
                ins.Parameters.AddWithValue("@p", (object?)a.ImagePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@np", (object?)a.NegativePrompt ?? DBNull.Value); ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }
    public void SavePropAssets(int projectId, List<PropAsset> assets)
    {
        using var conn = GetConn(); conn.Open(); using var txn = conn.BeginTransaction();
        try
        {
            using var del = new SqlCommand("DELETE FROM PropAssets WHERE ProjectId=@pid", conn, txn);
            del.Parameters.AddWithValue("@pid", projectId); del.ExecuteNonQuery();
            foreach (var a in assets)
            {
                using var ins = new SqlCommand("INSERT INTO PropAssets(ProjectId,Name,Description,ImageUrl,ImagePrompt,NegativePrompt) VALUES(@pid,@n,@d,@i,@p,@np)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId); ins.Parameters.AddWithValue("@n", a.Name);
                ins.Parameters.AddWithValue("@d", (object?)a.Description ?? DBNull.Value);
                ins.Parameters.AddWithValue("@i", (object?)a.ImageUrl ?? DBNull.Value);
                ins.Parameters.AddWithValue("@p", (object?)a.ImagePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@np", (object?)a.NegativePrompt ?? DBNull.Value); ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }
    public void SaveEffectAssets(int projectId, List<EffectAsset> assets)
    {
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        using (var del = new SqlCommand("DELETE FROM EffectAssets WHERE ProjectId=@pid", conn, txn))
        {
            del.Parameters.AddWithValue("@pid", projectId);
            del.ExecuteNonQuery();
        }
        using (var ins = new SqlCommand("INSERT INTO EffectAssets(ProjectId,Name,Description,ImageUrl,ImagePrompt,NegativePrompt) VALUES(@pid,@n,@d,@i,@p,@np)", conn, txn))
        {
            ins.Parameters.Add("@pid", System.Data.SqlDbType.Int);
            ins.Parameters.Add("@n", System.Data.SqlDbType.NVarChar);
            ins.Parameters.Add("@d", System.Data.SqlDbType.NVarChar);
            ins.Parameters.Add("@i", System.Data.SqlDbType.NVarChar);
            ins.Parameters.Add("@p", System.Data.SqlDbType.NVarChar);
            ins.Parameters.Add("@np", System.Data.SqlDbType.NVarChar);
            foreach (var a in assets)
            {
                if (string.IsNullOrWhiteSpace(a.Name)) continue;
                ins.Parameters["@pid"].Value = projectId;
                ins.Parameters["@n"].Value = a.Name.Trim();
                ins.Parameters["@d"].Value = (object?)a.Description ?? DBNull.Value;
                ins.Parameters["@i"].Value = (object?)a.ImageUrl ?? DBNull.Value;
                ins.Parameters["@p"].Value = (object?)a.ImagePrompt ?? DBNull.Value;
                ins.Parameters["@np"].Value = (object?)a.NegativePrompt ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
        }
        txn.Commit();
    }

    public void SaveEnvAssets(int projectId, List<EnvironmentAsset> assets)
    {
        using var conn = GetConn(); conn.Open(); using var txn = conn.BeginTransaction();
        try
        {
            using var del = new SqlCommand("DELETE FROM EnvironmentAssets WHERE ProjectId=@pid", conn, txn);
            del.Parameters.AddWithValue("@pid", projectId); del.ExecuteNonQuery();
            foreach (var a in assets)
            {
                using var ins = new SqlCommand("INSERT INTO EnvironmentAssets(ProjectId,Name,Description,ImageUrl,ImagePrompt,NegativePrompt) VALUES(@pid,@n,@d,@i,@p,@np)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId); ins.Parameters.AddWithValue("@n", a.Name);
                ins.Parameters.AddWithValue("@d", (object?)a.Description ?? DBNull.Value);
                ins.Parameters.AddWithValue("@i", (object?)a.ImageUrl ?? DBNull.Value);
                ins.Parameters.AddWithValue("@p", (object?)a.ImagePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@np", (object?)a.NegativePrompt ?? DBNull.Value); ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }
    public void UpdateCharacterAsset(int projectId, int assetId, string name, string? description, string? imageUrl, string? attributes)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE CharacterAssets SET Name=@n, Description=@d, ImageUrl=@i, Attributes=@at WHERE ProjectId=@pid AND AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@pid", projectId); cmd.Parameters.AddWithValue("@id", assetId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", (object?)attributes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void UpdatePropAsset(int projectId, int assetId, string name, string? description, string? imageUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE PropAssets SET Name=@n, Description=@d, ImageUrl=@i WHERE ProjectId=@pid AND AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@pid", projectId); cmd.Parameters.AddWithValue("@id", assetId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void UpdateEffectAsset(int projectId, int assetId, string name, string? description, string? imageUrl)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE EffectAssets SET Name=@n, Description=@d, ImageUrl=@i WHERE ProjectId=@pid AND AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@id", assetId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpdateEnvAsset(int projectId, int assetId, string name, string? description, string? imageUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE EnvironmentAssets SET Name=@n, Description=@d, ImageUrl=@i WHERE ProjectId=@pid AND AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@pid", projectId); cmd.Parameters.AddWithValue("@id", assetId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 只更新资产卡的图片地址（出图回填专用）。
    /// 不复用 UpdateXxxAsset 是因为那些方法要传 Name/Description 全字段，
    /// 后台自动回填时手头只有图片路径，用整行更新容易把名称/描述冲掉。
    /// </summary>
    public void UpdateAssetImage(int projectId, string category, int assetId, string? imageUrl)
    {
        var table = category switch
        {
            "characters" => "CharacterAssets",
            "props" => "PropAssets",
            "environments" => "EnvironmentAssets",
            "effects" => "EffectAssets",
            _ => null
        };
        if (table == null) throw new ArgumentException("未知资产类型: " + category, nameof(category));

        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"UPDATE {table} SET ImageUrl=@i WHERE ProjectId=@pid AND AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@id", assetId);
        cmd.ExecuteNonQuery();
    }

    // ========== 资产出图任务队列（出图后台化：关页面/刷新也跑完） ==========

    /// <summary>入队一条出图任务，返回 TaskId。</summary>
    public int EnqueueAssetImageTask(AssetImageTask task)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO AssetImageTasks(ProjectId,UserId,Category,AssetId,AssetName,Status,PromptOverride,NegativeOverride,ExtraPrompt,Size,
       SourceProjectId,SourceAssetId,SourceImageUrl,GarmentImageUrl,SourceNote,CreatedAt)
OUTPUT INSERTED.TaskId
VALUES(@pid,@uid,@cat,@aid,@an,@st,@po,@no,@ep,@sz,@spid,@said,@simg,@gimg,@snote,@ca)", conn);
        cmd.Parameters.AddWithValue("@pid", task.ProjectId);
        cmd.Parameters.AddWithValue("@uid", task.UserId);
        cmd.Parameters.AddWithValue("@cat", task.Category);
        cmd.Parameters.AddWithValue("@aid", task.AssetId);
        cmd.Parameters.AddWithValue("@an", (object?)task.AssetName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@st", string.IsNullOrWhiteSpace(task.Status) ? "queued" : task.Status);
        cmd.Parameters.AddWithValue("@po", (object?)task.PromptOverride ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@no", (object?)task.NegativeOverride ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ep", (object?)task.ExtraPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sz", (object?)task.Size ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@spid", (object?)task.SourceProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@said", (object?)task.SourceAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@simg", (object?)task.SourceImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gimg", (object?)task.GarmentImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@snote", (object?)task.SourceNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", task.CreatedAt == default ? DateTime.Now : task.CreatedAt);
        return (int)cmd.ExecuteScalar();
    }

    /// <summary>该资产是否已有排队中/出图中的任务（防重复入队）。</summary>
    public bool HasActiveAssetImageTask(int projectId, string category, int assetId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT COUNT(1) FROM AssetImageTasks
WHERE ProjectId=@pid AND Category=@cat AND AssetId=@aid AND Status IN ('queued','running')", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@aid", assetId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>取待执行任务（后台队列用，按入队先后）。</summary>
    public List<AssetImageTask> GetQueuedAssetImageTasks(int top)
    {
        var list = new List<AssetImageTask>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP (@top) * FROM AssetImageTasks WHERE Status='queued' ORDER BY TaskId", conn);
        cmd.Parameters.AddWithValue("@top", top);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadAssetImageTask(r));
        return list;
    }

    /// <summary>把任务从 queued 抢成 running（并发下只有一次能成功，防重复执行）。</summary>
    public bool TryMarkAssetImageTaskRunning(int taskId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE AssetImageTasks SET Status='running', StartedAt=GETDATE(), ErrorMessage=NULL
WHERE TaskId=@id AND Status='queued'", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void CompleteAssetImageTask(int taskId, string imageUrl, int? libraryAssetId, string? usedPrompt)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE AssetImageTasks SET Status='completed', ImageUrl=@img, LibraryAssetId=@lib, UsedPrompt=@up,
       ErrorMessage=NULL, FinishedAt=GETDATE()
WHERE TaskId=@id", conn);
        cmd.Parameters.AddWithValue("@img", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lib", (object?)libraryAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@up", (object?)usedPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", taskId);
        cmd.ExecuteNonQuery();
    }

    public void FailAssetImageTask(int taskId, string? error)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE AssetImageTasks SET Status='failed', ErrorMessage=@err, FinishedAt=GETDATE()
WHERE TaskId=@id", conn);
        cmd.Parameters.AddWithValue("@err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>失败任务重新排队（手动重试用）。同时清掉上一次的失败原因。</summary>
    public bool RetryAssetImageTask(int projectId, int taskId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE AssetImageTasks SET Status='queued', ErrorMessage=NULL, StartedAt=NULL, FinishedAt=NULL
WHERE TaskId=@id AND ProjectId=@pid AND Status IN ('failed','completed')", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        cmd.Parameters.AddWithValue("@pid", projectId);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>把正在执行的任务放回队列（服务停机时收尾，下次启动接着跑）。</summary>
    public void ReleaseAssetImageTask(int taskId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE AssetImageTasks SET Status='queued', StartedAt=NULL
WHERE TaskId=@id AND Status='running'", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>取项目下最近的任务（前端轮询状态用）。</summary>
    public List<AssetImageTask> GetAssetImageTasks(int projectId, string? category = null, int top = 300)
    {
        var list = new List<AssetImageTask>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP (@top) * FROM AssetImageTasks
WHERE ProjectId=@pid AND (@cat IS NULL OR Category=@cat)
ORDER BY TaskId DESC", conn);
        cmd.Parameters.AddWithValue("@top", top);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@cat", (object?)category ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadAssetImageTask(r));
        return list;
    }

    /// <summary>
    /// 服务重启时把「卡在 running」的任务放回队列（上次进程退出时正在执行的）。
    /// 只回收开始超过 10 分钟没动静的，避免把正在跑的任务重复入队。
    /// </summary>
    public int ResetStuckAssetImageTasks()
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE AssetImageTasks SET Status='queued', StartedAt=NULL
WHERE Status='running' AND (StartedAt IS NULL OR StartedAt < DATEADD(MINUTE,-10,GETDATE()))", conn);
        return cmd.ExecuteNonQuery();
    }

    private static AssetImageTask ReadAssetImageTask(System.Data.Common.DbDataReader r) => new()
    {
        TaskId = (int)r["TaskId"],
        ProjectId = (int)r["ProjectId"],
        UserId = (int)r["UserId"],
        Category = (string)r["Category"],
        AssetId = (int)r["AssetId"],
        AssetName = r["AssetName"] == DBNull.Value ? null : (string)r["AssetName"],
        Status = (string)r["Status"],
        PromptOverride = r["PromptOverride"] == DBNull.Value ? null : (string)r["PromptOverride"],
        NegativeOverride = r["NegativeOverride"] == DBNull.Value ? null : (string)r["NegativeOverride"],
        ExtraPrompt = r["ExtraPrompt"] == DBNull.Value ? null : (string)r["ExtraPrompt"],
        Size = r["Size"] == DBNull.Value ? null : (string)r["Size"],
        SourceProjectId = r["SourceProjectId"] == DBNull.Value ? null : (int)r["SourceProjectId"],
        SourceAssetId = r["SourceAssetId"] == DBNull.Value ? null : (int)r["SourceAssetId"],
        SourceImageUrl = r["SourceImageUrl"] == DBNull.Value ? null : (string)r["SourceImageUrl"],
        GarmentImageUrl = r["GarmentImageUrl"] == DBNull.Value ? null : (string)r["GarmentImageUrl"],
        SourceNote = r["SourceNote"] == DBNull.Value ? null : (string)r["SourceNote"],
        ImageUrl = r["ImageUrl"] == DBNull.Value ? null : (string)r["ImageUrl"],
        LibraryAssetId = r["LibraryAssetId"] == DBNull.Value ? null : (int)r["LibraryAssetId"],
        UsedPrompt = r["UsedPrompt"] == DBNull.Value ? null : (string)r["UsedPrompt"],
        ErrorMessage = r["ErrorMessage"] == DBNull.Value ? null : (string)r["ErrorMessage"],
        CreatedAt = (DateTime)r["CreatedAt"],
        StartedAt = r["StartedAt"] == DBNull.Value ? null : (DateTime)r["StartedAt"],
        FinishedAt = r["FinishedAt"] == DBNull.Value ? null : (DateTime)r["FinishedAt"],
    };

    // ---------- 角色卡派生血缘（Database\Upgrade_角色卡派生.sql） ----------
    // 各剧本的资产各管各的：这里只留痕、不建立强关系。源卡被删，已派生的卡照常可用，
    // 画布上仍能按 SourceImageUrl 的快照图画出箭头起点。

    /// <summary>登记一条派生血缘，返回 DerivationId。</summary>
    public int SaveCharacterCardDerivation(CharacterCardDerivation d)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO CharacterCardDerivations(UserId,ProjectId,AssetId,SourceProjectId,SourceAssetId,SourceImageUrl,GarmentImageUrl,Prompt,ResultImageUrl,Note,CreatedAt)
OUTPUT INSERTED.DerivationId
VALUES(@uid,@pid,@aid,@spid,@said,@simg,@gimg,@pr,@rimg,@note,@ca)", conn);
        cmd.Parameters.AddWithValue("@uid", d.UserId);
        cmd.Parameters.AddWithValue("@pid", d.ProjectId);
        cmd.Parameters.AddWithValue("@aid", d.AssetId);
        cmd.Parameters.AddWithValue("@spid", (object?)d.SourceProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@said", (object?)d.SourceAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@simg", d.SourceImageUrl);
        cmd.Parameters.AddWithValue("@gimg", (object?)d.GarmentImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pr", (object?)d.Prompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rimg", (object?)d.ResultImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@note", (object?)d.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", d.CreatedAt == default ? DateTime.Now : d.CreatedAt);
        return (int)cmd.ExecuteScalar();
    }

    /// <summary>出图完成后回填结果图。</summary>
    public void CompleteCharacterCardDerivation(int derivationId, string? resultImageUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CharacterCardDerivations SET ResultImageUrl=@img WHERE DerivationId=@id", conn);
        cmd.Parameters.AddWithValue("@img", (object?)resultImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", derivationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>某张角色卡的派生来源（画布上的入边）。没有血缘时返回 null。</summary>
    public CharacterCardDerivation? GetCharacterCardDerivation(int projectId, int assetId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 * FROM CharacterCardDerivations
WHERE ProjectId=@pid AND AssetId=@aid
ORDER BY DerivationId DESC", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@aid", assetId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCharacterCardDerivation(r) : null;
    }

    /// <summary>
    /// 派生血缘列表。projectId 为空时返回该用户全部（跨剧本谱系用），传了则只返回该项目下的。
    /// </summary>
    public List<CharacterCardDerivation> GetCharacterCardDerivations(int userId, int? projectId = null)
    {
        var list = new List<CharacterCardDerivation>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT * FROM CharacterCardDerivations
WHERE UserId=@uid AND (@pid IS NULL OR ProjectId=@pid)
ORDER BY DerivationId", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadCharacterCardDerivation(r));
        return list;
    }

    private static CharacterCardDerivation ReadCharacterCardDerivation(System.Data.Common.DbDataReader r) => new()
    {
        DerivationId = (int)r["DerivationId"],
        UserId = (int)r["UserId"],
        ProjectId = (int)r["ProjectId"],
        AssetId = (int)r["AssetId"],
        SourceProjectId = r["SourceProjectId"] == DBNull.Value ? null : (int)r["SourceProjectId"],
        SourceAssetId = r["SourceAssetId"] == DBNull.Value ? null : (int)r["SourceAssetId"],
        SourceImageUrl = (string)r["SourceImageUrl"],
        GarmentImageUrl = r["GarmentImageUrl"] == DBNull.Value ? null : (string)r["GarmentImageUrl"],
        Prompt = r["Prompt"] == DBNull.Value ? null : (string)r["Prompt"],
        ResultImageUrl = r["ResultImageUrl"] == DBNull.Value ? null : (string)r["ResultImageUrl"],
        Note = r["Note"] == DBNull.Value ? null : (string)r["Note"],
        CreatedAt = (DateTime)r["CreatedAt"],
    };

    // ---------- 资产出图提示词 / 提示词模版 ----------

    /// <summary>分类键 -> 资产表名（characters/props/environments/effects）。</summary>
    public static string? AssetTableName(string? category) => (category ?? "").Trim().ToLowerInvariant() switch
    {
        "characters" => "CharacterAssets",
        "props" => "PropAssets",
        "environments" => "EnvironmentAssets",
        "effects" => "EffectAssets",
        _ => null
    };

    /// <summary>
    /// 写入资产卡的出图提示词。提取时自动生成、资产卡里手动编辑、单个/批量重生成都走这里。
    /// 空串一律写成 NULL，避免库里出现空字符串影响「有没有提示词」的判断。
    /// </summary>
    public void SaveAssetPrompt(int projectId, string category, int assetId, string? imagePrompt, string? negativePrompt)
    {
        var table = AssetTableName(category) ?? throw new ArgumentException("未知资产类型: " + category, nameof(category));
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"UPDATE {table} SET ImagePrompt=@p, NegativePrompt=@np WHERE ProjectId=@pid AND AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@p", string.IsNullOrWhiteSpace(imagePrompt) ? DBNull.Value : imagePrompt.Trim());
        cmd.Parameters.AddWithValue("@np", string.IsNullOrWhiteSpace(negativePrompt) ? DBNull.Value : negativePrompt.Trim());
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@id", assetId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>只更新「出图提示词」正文（单个/批量重生成用，不动该资产已填的负面词）。</summary>
    public void SaveAssetImagePrompt(int projectId, string category, int assetId, string? imagePrompt)
    {
        var table = AssetTableName(category) ?? throw new ArgumentException("未知资产类型: " + category, nameof(category));
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand($"UPDATE {table} SET ImagePrompt=@p WHERE ProjectId=@pid AND AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@p", string.IsNullOrWhiteSpace(imagePrompt) ? DBNull.Value : imagePrompt.Trim());
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@id", assetId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 读取某作用域下该用户已保存的资产提示词模版。
    /// projectId = 0 读账号级默认模版；>0 读该剧专属模版（没覆盖过的分类不会返回）。
    /// </summary>
    public List<AssetPromptTemplate> GetAssetPromptTemplates(int userId, int projectId = 0)
    {
        var list = new List<AssetPromptTemplate>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT TemplateId,UserId,ProjectId,Category,StyleLock,NegativePrompt,RuleText,Enabled,UpdatedAt FROM AssetPromptTemplates WHERE UserId=@u AND ProjectId=@p", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@p", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadAssetPromptTemplateRow(r));
        return list;
    }

    /// <summary>读某作用域某分类的单条模版；没存过返回 null。</summary>
    private AssetPromptTemplate? ReadAssetPromptTemplate(int userId, int projectId, string category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT TemplateId,UserId,ProjectId,Category,StyleLock,NegativePrompt,RuleText,Enabled,UpdatedAt FROM AssetPromptTemplates WHERE UserId=@u AND ProjectId=@p AND Category=@c", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@c", c);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAssetPromptTemplateRow(r) : null;
    }

    private static AssetPromptTemplate ReadAssetPromptTemplateRow(SqlDataReader r) => new AssetPromptTemplate
    {
        TemplateId = (int)r["TemplateId"],
        UserId = (int)r["UserId"],
        ProjectId = r["ProjectId"] == DBNull.Value ? 0 : (int)r["ProjectId"],
        Category = (string)r["Category"],
        StyleLock = r["StyleLock"] as string,
        NegativePrompt = r["NegativePrompt"] as string,
        RuleText = r["RuleText"] as string,
        Enabled = r["Enabled"] != DBNull.Value && (bool)r["Enabled"],
        UpdatedAt = r["UpdatedAt"] == DBNull.Value ? DateTime.MinValue : (DateTime)r["UpdatedAt"],
    };

    /// <summary>
    /// 取某项目某分类「生效」的模版：本剧覆盖 → 账号级默认 → 出厂默认。
    /// 返回对象的来源可以这样判断：ProjectId&gt;0 为剧级覆盖；ProjectId=0 且 TemplateId&gt;0 为账号级默认；TemplateId=0 为出厂默认。
    /// 提取（Stage 6/7/8/11）与出图都靠它拿模版，所以每部剧的风格可以各不相同。
    /// </summary>
    public AssetPromptTemplate GetAssetPromptTemplate(int userId, int projectId, string category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        try
        {
            if (projectId > 0)
            {
                var own = ReadAssetPromptTemplate(userId, projectId, c);
                if (own != null) return own;
            }
            var account = ReadAssetPromptTemplate(userId, 0, c);
            if (account != null) return account;
        }
        catch
        {
            // 表缺失（未跑升级脚本）等异常不阻断流程：直接退回出厂默认
        }
        return AssetPromptTemplateDefaults.Create(c);
    }

    /// <summary>保存（新增或更新）某用户某作用域某分类的资产提示词模版。ProjectId=0 为账号级默认。</summary>
    public void SaveAssetPromptTemplate(AssetPromptTemplate template)
    {
        var c = (template.Category ?? "").Trim().ToLowerInvariant();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
IF EXISTS (SELECT 1 FROM AssetPromptTemplates WHERE UserId=@u AND ProjectId=@p AND Category=@c)
    UPDATE AssetPromptTemplates SET StyleLock=@s, NegativePrompt=@n, RuleText=@r, Enabled=@e, UpdatedAt=SYSDATETIME() WHERE UserId=@u AND ProjectId=@p AND Category=@c
ELSE
    INSERT INTO AssetPromptTemplates(UserId,ProjectId,Category,StyleLock,NegativePrompt,RuleText,Enabled) VALUES(@u,@p,@c,@s,@n,@r,@e)", conn);
        cmd.Parameters.AddWithValue("@u", template.UserId);
        cmd.Parameters.AddWithValue("@p", template.ProjectId);
        cmd.Parameters.AddWithValue("@c", c);
        cmd.Parameters.AddWithValue("@s", (object?)template.StyleLock ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@n", (object?)template.NegativePrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@r", (object?)template.RuleText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@e", template.Enabled);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除某剧某分类的剧级覆盖，恢复到账号级默认（账号级也没存过则回出厂默认）。
    /// 「本剧恢复默认」按钮走这里；不做删除以免写一份和默认值一样的冗余数据。
    /// </summary>
    public void DeleteProjectAssetPromptTemplate(int userId, int projectId, string category)
    {
        if (projectId <= 0) return;
        var c = (category ?? "").Trim().ToLowerInvariant();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM AssetPromptTemplates WHERE UserId=@u AND ProjectId=@p AND Category=@c", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@c", c);
        cmd.ExecuteNonQuery();
    }

    // ---------- 删除项目资产（连同单元/分镜资产绑定一起清理） ----------

    public AssetDeleteResult DeleteCharacterAsset(int projectId, int assetId) => DeleteAsset(projectId, "Character", assetId);
    public AssetDeleteResult DeletePropAsset(int projectId, int assetId) => DeleteAsset(projectId, "Prop", assetId);
    public AssetDeleteResult DeleteEnvAsset(int projectId, int assetId) => DeleteAsset(projectId, "Environment", assetId);
    public AssetDeleteResult DeleteEffectAsset(int projectId, int assetId) => DeleteAsset(projectId, "Effect", assetId);

    /// <summary>
    /// 删除项目资产，并清理引用它的资产绑定。UnitAssetBindings / FrameAssetBindings 只存 Category+AssetId，
    /// 而各资产表的 AssetId 是各自独立的自增序列，所以必须同时匹配 Category 才能删对。
    /// 已生成的分镜/提示词正文里写死的资产名无法回收，需要重跑对应阶段才会消失。
    /// </summary>
    private AssetDeleteResult DeleteAsset(int projectId, string category, int assetId)
    {
        var table = category switch
        {
            "Character" => "CharacterAssets",
            "Prop" => "PropAssets",
            "Environment" => "EnvironmentAssets",
            "Effect" => "EffectAssets",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未知资产分类")
        };

        var result = new AssetDeleteResult();
        using var conn = GetConn(); conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using (var delUnit = new SqlCommand("DELETE FROM UnitAssetBindings WHERE ProjectId=@pid AND Category=@cat AND AssetId=@id", conn, txn))
            {
                delUnit.Parameters.AddWithValue("@pid", projectId);
                delUnit.Parameters.AddWithValue("@cat", category);
                delUnit.Parameters.AddWithValue("@id", assetId);
                result.UnitBindingsRemoved = delUnit.ExecuteNonQuery();
            }
            using (var delFrame = new SqlCommand("DELETE FROM FrameAssetBindings WHERE ProjectId=@pid AND Category=@cat AND AssetId=@id", conn, txn))
            {
                delFrame.Parameters.AddWithValue("@pid", projectId);
                delFrame.Parameters.AddWithValue("@cat", category);
                delFrame.Parameters.AddWithValue("@id", assetId);
                result.FrameBindingsRemoved = delFrame.ExecuteNonQuery();
            }
            using (var delAsset = new SqlCommand("DELETE FROM " + table + " WHERE ProjectId=@pid AND AssetId=@id", conn, txn))
            {
                delAsset.Parameters.AddWithValue("@pid", projectId);
                delAsset.Parameters.AddWithValue("@id", assetId);
                result.Deleted = delAsset.ExecuteNonQuery() > 0;
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
        return result;
    }

    public int AddCharacterAsset(int projectId, string name, string? description, string? imageUrl, string? attributes)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("INSERT INTO CharacterAssets(ProjectId,Name,Description,ImageUrl,Attributes,CreatedAt) OUTPUT INSERTED.AssetId VALUES(@pid,@n,@d,@i,@at,GETDATE())", conn);
        cmd.Parameters.AddWithValue("@pid", projectId); cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", (object?)attributes ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }
    public int AddPropAsset(int projectId, string name, string? description, string? imageUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("INSERT INTO PropAssets(ProjectId,Name,Description,ImageUrl,CreatedAt) OUTPUT INSERTED.AssetId VALUES(@pid,@n,@d,@i,GETDATE())", conn);
        cmd.Parameters.AddWithValue("@pid", projectId); cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }
    public int AddEffectAsset(int projectId, string name, string? description, string? imageUrl)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("INSERT INTO EffectAssets(ProjectId,Name,Description,ImageUrl,CreatedAt) OUTPUT INSERTED.AssetId VALUES(@pid,@n,@d,@i,GETDATE())", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public int AddEnvAsset(int projectId, string name, string? description, string? imageUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("INSERT INTO EnvironmentAssets(ProjectId,Name,Description,ImageUrl,CreatedAt) OUTPUT INSERTED.AssetId VALUES(@pid,@n,@d,@i,GETDATE())", conn);
        cmd.Parameters.AddWithValue("@pid", projectId); cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@d", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@i", (object?)imageUrl ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }
    public void SaveSeedancePrompts(int projectId, List<SeedancePrompt> prompts)
    {
        using var conn = GetConn(); conn.Open(); using var txn = conn.BeginTransaction();
        try
        {
            using var del = new SqlCommand("DELETE FROM SeedancePrompts WHERE ProjectId=@pid", conn, txn);
            del.Parameters.AddWithValue("@pid", projectId); del.ExecuteNonQuery();
            foreach (var p in prompts)
            {
                using var ins = new SqlCommand("INSERT INTO SeedancePrompts(ProjectId,FrameId,PromptText,PromptTextH3,NegativePrompt,VideoUrl,Status,BatchNumber,EpisodeNumber,UnitName,ShotLabel,ShotType,Duration,ReferenceImages,ReferenceVideos,ReferenceAudio,CreatedAt) VALUES(@pid,@fid,@pt,@ph3,@np,@vu,@st,@bn,@en,@un,@sl,@stt,@dur,@rim,@rv,@ra,@cat)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId); ins.Parameters.AddWithValue("@fid", (object?)p.FrameId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@pt", p.PromptText); ins.Parameters.AddWithValue("@ph3", (object?)p.PromptTextH3 ?? DBNull.Value); ins.Parameters.AddWithValue("@np", (object?)p.NegativePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@vu", (object?)p.VideoUrl ?? DBNull.Value); ins.Parameters.AddWithValue("@st", p.Status); ins.Parameters.AddWithValue("@bn", p.BatchNumber); ins.Parameters.AddWithValue("@en", p.EpisodeNumber); ins.Parameters.AddWithValue("@un", (object?)p.UnitName ?? DBNull.Value); ins.Parameters.AddWithValue("@sl", (object?)p.ShotLabel ?? DBNull.Value);
                ins.Parameters.AddWithValue("@stt", (object?)p.ShotType ?? DBNull.Value); ins.Parameters.AddWithValue("@rim", (object?)p.ReferenceImages ?? DBNull.Value); ins.Parameters.AddWithValue("@rv", (object?)p.ReferenceVideos ?? DBNull.Value); ins.Parameters.AddWithValue("@ra", (object?)p.ReferenceAudio ?? DBNull.Value);
            ins.Parameters.AddWithValue("@dur", p.Duration); ins.Parameters.AddWithValue("@cat", DateTime.Now); ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    public void ClearSeedancePrompts(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SeedancePrompts WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteAllSeedancePrompts(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SeedancePrompts WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSeedancePromptsByUnit(int projectId, string unitName)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SeedancePrompts WHERE ProjectId=@pid AND UnitName=@un", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@un", unitName);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSeedancePromptsByFrame(int projectId, string? unitName, string? shotLabel)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SeedancePrompts WHERE ProjectId=@pid AND UnitName=@un AND ShotLabel=@sl", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@un", (object?)unitName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sl", (object?)shotLabel ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>单镜头"原地覆盖"写提示词：优先复用该镜头已有行（保留 PromptId、FrameId、H3、参考图/视频/音频绑定与视频记录），
    /// 仅覆盖 PromptText / NegativePrompt / ShotType / Duration 四个内容列，避免重新生成提示词后 PromptId 漂移、绑定与成片丢失。
    /// 新解析行比现有行多时补插新行；现有行多于新行时，仅删除末尾既无绑定又无 H3 的冗余空行，绝不误删已绑定内容的行。</summary>
    public void UpsertSeedancePromptsForShot(int projectId, string? unitName, string? shotLabel, List<SeedancePrompt> rows)
    {
        if (rows == null || rows.Count == 0) return;
        using var conn = GetConn(); conn.Open(); using var txn = conn.BeginTransaction();
        try
        {
            // 1) 现有该镜头行（按插入先后排序，与重生成文本的块顺序对齐）
            var existingIds = new List<int>();
            using (var q = new SqlCommand("SELECT PromptId FROM SeedancePrompts WHERE ProjectId=@pid AND UnitName=@un AND ShotLabel=@sl ORDER BY PromptId", conn, txn))
            {
                q.Parameters.AddWithValue("@pid", projectId);
                q.Parameters.AddWithValue("@un", (object?)unitName ?? DBNull.Value);
                q.Parameters.AddWithValue("@sl", (object?)shotLabel ?? DBNull.Value);
                using var r = q.ExecuteReader();
                while (r.Read()) existingIds.Add((int)r["PromptId"]);
            }

            // 2) 行数重合部分：原地覆盖内容列，其余列一律保留
            var min = Math.Min(existingIds.Count, rows.Count);
            for (int i = 0; i < min; i++)
            {
                var p = rows[i];
                using var upd = new SqlCommand("UPDATE SeedancePrompts SET PromptText=@pt, NegativePrompt=@np, ShotType=@stt, Duration=@dur WHERE PromptId=@id", conn, txn);
                upd.Parameters.AddWithValue("@pt", p.PromptText);
                upd.Parameters.AddWithValue("@np", (object?)p.NegativePrompt ?? DBNull.Value);
                upd.Parameters.AddWithValue("@stt", (object?)p.ShotType ?? DBNull.Value);
                upd.Parameters.AddWithValue("@dur", p.Duration);
                upd.Parameters.AddWithValue("@id", existingIds[i]);
                upd.ExecuteNonQuery();
            }

            // 3) 旧行多于新行：只删末尾"空行"（无 H3、无视频、无参考绑定），有内容的行保留防误删
            for (int i = rows.Count; i < existingIds.Count; i++)
            {
                using var chk = new SqlCommand("SELECT COUNT(1) FROM SeedancePrompts WHERE PromptId=@id AND (ISNULL(PromptTextH3,'')<>'' OR ISNULL(VideoUrl,'')<>'' OR ISNULL(LocalVideoUrl,'')<>'' OR ISNULL(ReferenceImages,'')<>'' OR ISNULL(ReferenceVideos,'')<>'' OR ISNULL(ReferenceAudio,'')<>'')", conn, txn);
                chk.Parameters.AddWithValue("@id", existingIds[i]);
                var bound = Convert.ToInt32(chk.ExecuteScalar());
                if (bound > 0) continue;
                using var del = new SqlCommand("DELETE FROM SeedancePrompts WHERE PromptId=@id", conn, txn);
                del.Parameters.AddWithValue("@id", existingIds[i]);
                del.ExecuteNonQuery();
            }

            // 4) 新行多于旧行：多出的行以新行方式插入
            for (int i = existingIds.Count; i < rows.Count; i++)
            {
                var p = rows[i];
                using var ins = new SqlCommand("INSERT INTO SeedancePrompts(ProjectId,FrameId,PromptText,PromptTextH3,NegativePrompt,VideoUrl,Status,BatchNumber,EpisodeNumber,UnitName,ShotLabel,ShotType,Duration,ReferenceImages,ReferenceVideos,ReferenceAudio,CreatedAt) VALUES(@pid,@fid,@pt,@ph3,@np,@vu,@st,@bn,@en,@un,@sl,@stt,@dur,@rim,@rv,@ra,@cat)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId); ins.Parameters.AddWithValue("@fid", (object?)p.FrameId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@pt", p.PromptText); ins.Parameters.AddWithValue("@ph3", (object?)p.PromptTextH3 ?? DBNull.Value); ins.Parameters.AddWithValue("@np", (object?)p.NegativePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@vu", (object?)p.VideoUrl ?? DBNull.Value); ins.Parameters.AddWithValue("@st", p.Status); ins.Parameters.AddWithValue("@bn", p.BatchNumber); ins.Parameters.AddWithValue("@en", p.EpisodeNumber); ins.Parameters.AddWithValue("@un", (object?)p.UnitName ?? DBNull.Value); ins.Parameters.AddWithValue("@sl", (object?)p.ShotLabel ?? DBNull.Value);
                ins.Parameters.AddWithValue("@stt", (object?)p.ShotType ?? DBNull.Value); ins.Parameters.AddWithValue("@rim", (object?)p.ReferenceImages ?? DBNull.Value); ins.Parameters.AddWithValue("@rv", (object?)p.ReferenceVideos ?? DBNull.Value); ins.Parameters.AddWithValue("@ra", (object?)p.ReferenceAudio ?? DBNull.Value);
                ins.Parameters.AddWithValue("@dur", p.Duration); ins.Parameters.AddWithValue("@cat", DateTime.Now);
                ins.ExecuteNonQuery();
            }

            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    public void InsertSeedancePrompts(int projectId, List<SeedancePrompt> prompts)
    {
        if (prompts == null || prompts.Count == 0) return;
        using var conn = GetConn(); conn.Open(); using var txn = conn.BeginTransaction();
        try
        {
            foreach (var p in prompts)
            {
                using var ins = new SqlCommand("INSERT INTO SeedancePrompts(ProjectId,FrameId,PromptText,PromptTextH3,NegativePrompt,VideoUrl,Status,BatchNumber,EpisodeNumber,UnitName,ShotLabel,ShotType,Duration,ReferenceImages,ReferenceVideos,ReferenceAudio,CreatedAt) VALUES(@pid,@fid,@pt,@ph3,@np,@vu,@st,@bn,@en,@un,@sl,@stt,@dur,@rim,@rv,@ra,@cat)", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId); ins.Parameters.AddWithValue("@fid", (object?)p.FrameId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@pt", p.PromptText); ins.Parameters.AddWithValue("@ph3", (object?)p.PromptTextH3 ?? DBNull.Value); ins.Parameters.AddWithValue("@np", (object?)p.NegativePrompt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@vu", (object?)p.VideoUrl ?? DBNull.Value); ins.Parameters.AddWithValue("@st", p.Status); ins.Parameters.AddWithValue("@bn", p.BatchNumber); ins.Parameters.AddWithValue("@en", p.EpisodeNumber); ins.Parameters.AddWithValue("@un", (object?)p.UnitName ?? DBNull.Value); ins.Parameters.AddWithValue("@sl", (object?)p.ShotLabel ?? DBNull.Value);
                ins.Parameters.AddWithValue("@stt", (object?)p.ShotType ?? DBNull.Value); ins.Parameters.AddWithValue("@rim", (object?)p.ReferenceImages ?? DBNull.Value); ins.Parameters.AddWithValue("@rv", (object?)p.ReferenceVideos ?? DBNull.Value); ins.Parameters.AddWithValue("@ra", (object?)p.ReferenceAudio ?? DBNull.Value);
                ins.Parameters.AddWithValue("@dur", p.Duration); ins.Parameters.AddWithValue("@cat", DateTime.Now); ins.ExecuteNonQuery();
            }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    public int CountDistinctPromptUnits(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(DISTINCT UnitName) FROM SeedancePrompts WHERE ProjectId=@pid AND UnitName IS NOT NULL AND LTRIM(RTRIM(UnitName))<>''", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
    }
    public int AddPrompt(int projectId, string promptText, int episodeNumber, string unitName, string shotLabel, int duration, string shotType)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("INSERT INTO SeedancePrompts(ProjectId,PromptText,Status,BatchNumber,EpisodeNumber,UnitName,ShotLabel,ShotType,Duration,CreatedAt) OUTPUT INSERTED.PromptId VALUES(@pid,@pt,'',1,@ep,@un,@sl,@stt,@dur,@cat)", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@pt", promptText);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        cmd.Parameters.AddWithValue("@un", string.IsNullOrWhiteSpace(unitName) ? (object)DBNull.Value : unitName);
        cmd.Parameters.AddWithValue("@sl", string.IsNullOrWhiteSpace(shotLabel) ? (object)DBNull.Value : shotLabel);
        cmd.Parameters.AddWithValue("@stt", string.IsNullOrWhiteSpace(shotType) ? (object)DBNull.Value : shotType);
        cmd.Parameters.AddWithValue("@dur", duration);
        cmd.Parameters.AddWithValue("@cat", DateTime.Now);
        return (int)cmd.ExecuteScalar();
    }

    public void UpdatePromptTextH3(int promptId, string h3Text)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET PromptTextH3=@h3 WHERE PromptId=@pid", conn);
        cmd.Parameters.AddWithValue("@h3", h3Text);
        cmd.Parameters.AddWithValue("@pid", promptId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>覆盖模式：删除项目已生成的全部提示词记录（整行删除，重新生成时全部走插入重建）。</summary>
    public void DeleteProjectPrompts(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SeedancePrompts WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>镜头记录尚不存在时，直接插入一条仅有 H3 提示词的镜头记录（SD 提示词留空，两条生成线完全独立）。</summary>
    public int InsertSeedancePromptWithH3(int projectId, int episodeNumber, string? unitName, string? shotLabel, string? shotType, int duration, string h3Text)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO SeedancePrompts(ProjectId, EpisodeNumber, UnitName, ShotLabel, ShotType, Duration, PromptText, PromptTextH3, Status, CreatedAt)
VALUES(@pid,@ep,@un,@sl,@st,@du,'',@h3,'h3',GETDATE());
SELECT SCOPE_IDENTITY();", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        cmd.Parameters.AddWithValue("@un", (object?)unitName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sl", (object?)shotLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@st", (object?)shotType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@du", duration);
        cmd.Parameters.AddWithValue("@h3", h3Text);
        var id = cmd.ExecuteScalar();
        return id == DBNull.Value || id == null ? 0 : Convert.ToInt32(id);
    }

    public CoherenceCheck? GetCoherenceCheck(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT TOP 1 * FROM CoherenceChecks WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return new CoherenceCheck
        {
            CheckId = (int)r["CheckId"],
            ProjectId = (int)r["ProjectId"],
            Issues = r["Issues"] == DBNull.Value ? null : (string)r["Issues"],
            Status = (string)r["Status"],
            CreatedAt = (DateTime)r["CreatedAt"]
        };
        return null;
    }
    public void SaveCoherenceCheck(int projectId, string? issues, string status)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("IF EXISTS(SELECT 1 FROM CoherenceChecks WHERE ProjectId=@pid) " +
            "UPDATE CoherenceChecks SET Issues=@i, Status=@st WHERE ProjectId=@pid " +
            "ELSE INSERT INTO CoherenceChecks(ProjectId,Issues,Status) VALUES(@pid,@i,@st)", conn);
        cmd.Parameters.AddWithValue("@pid", projectId); cmd.Parameters.AddWithValue("@i", (object?)issues ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@st", status); cmd.ExecuteNonQuery();
    }
    // ========== LLM ���� ==========
    public LLMConfig? GetActiveConfig(int userId, string provider)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT TOP 1 * FROM LLMConfigs WHERE UserId=@uid AND Provider=@p AND IsActive=1", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@p", provider);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return new LLMConfig
        {
            ConfigId = (int)r["ConfigId"],
            UserId = (int)r["UserId"],
            Provider = (string)r["Provider"],
            ApiKey = (string)r["ApiKey"],
            ApiUrl = r["ApiUrl"] == DBNull.Value ? null : (string)r["ApiUrl"],
            ModelName = r["ModelName"] == DBNull.Value ? null : (string)r["ModelName"],
            ThinkingMode = r["ThinkingMode"] == DBNull.Value ? null : (string)r["ThinkingMode"],
            IsActive = (bool)r["IsActive"],
            AutoEnhance = r["AutoEnhance"] == DBNull.Value ? false : (bool)r["AutoEnhance"]
        };
        return null;
    }

    public void SaveLLMConfig(LLMConfig config)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("IF EXISTS(SELECT 1 FROM LLMConfigs WHERE UserId=@uid AND Provider=@p) " +
            "UPDATE LLMConfigs SET ApiKey=CASE WHEN NULLIF(@k,'') IS NULL THEN ApiKey ELSE @k END, ApiUrl=@u, ModelName=@m, ThinkingMode=@tm, IsActive=@a, AutoEnhance=@ae, UpdatedAt=GETDATE() WHERE UserId=@uid AND Provider=@p " +
            "ELSE INSERT INTO LLMConfigs(UserId,Provider,ApiKey,ApiUrl,ModelName,ThinkingMode,IsActive,AutoEnhance) VALUES(@uid,@p,ISNULL(@k,''),@u,@m,@tm,@a,@ae)", conn);
        cmd.Parameters.AddWithValue("@uid", config.UserId);
        cmd.Parameters.AddWithValue("@p", config.Provider);
        cmd.Parameters.AddWithValue("@k", config.ApiKey);
        cmd.Parameters.AddWithValue("@u", (object?)config.ApiUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@m", (object?)config.ModelName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tm", (object?)config.ThinkingMode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@a", config.IsActive);
        cmd.Parameters.AddWithValue("@ae", config.AutoEnhance);
        cmd.ExecuteNonQuery();
    }
    public string GetActiveLLMProvider(int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT TOP 1 ActiveLLMProvider FROM Users WHERE UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        var val = cmd.ExecuteScalar();
        var s = val == null || val == DBNull.Value ? "" : (string)val;
        return string.IsNullOrWhiteSpace(s) ? "deepseek" : s.Trim();
    }

    public void SetActiveLLMProvider(int userId, string provider)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE Users SET ActiveLLMProvider=@p WHERE UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@p", provider);
        cmd.ExecuteNonQuery();
    }

    public string GetActiveVideoEngine(int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT TOP 1 ActiveVideoEngine FROM Users WHERE UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        var val = cmd.ExecuteScalar();
        var s = val == null || val == DBNull.Value ? "" : (string)val;
        return s.Trim() == "comfyui" ? "comfyui" : "volcano";
    }

    public void SetActiveVideoEngine(int userId, string engine)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE Users SET ActiveVideoEngine=@e WHERE UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@e", engine == "comfyui" ? "comfyui" : "volcano");
        cmd.ExecuteNonQuery();
    }
    // ========== Video generation ==========
    public void UpdatePromptStatus(int promptId, string status)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET Status=@s WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }

    public void ResetPromptVideo(int promptId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET Status='processing', VideoUrl=NULL, LocalVideoUrl=NULL WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }
    public void UpdatePromptVideo(int promptId, string? videoUrl, string? localVideoUrl, string status)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET VideoUrl=@v, LocalVideoUrl=@lv, Status=@s WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@v", (object?)videoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lv", (object?)localVideoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }
    /// <summary>
    /// 增强完成后，把增强视频的地址同步到该提示词最新的视频生成任务记录上（Engine<>mediakit），
    /// 让视频任务记录成为最终视频入口；增强任务记录（mediakit）保留自己的数据。
    /// </summary>
    public void SyncEnhanceResultToGenerationTask(int promptId, string? videoUrl, string? localVideoUrl, string? resolution)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE VideoGenerationTasks
SET VideoUrl=@vu, LocalVideoUrl=@lv, EnhanceResolution=@res
WHERE PromptId=@prid AND Engine <> 'mediakit'
  AND Id = (SELECT TOP 1 Id FROM VideoGenerationTasks t2 WHERE t2.PromptId=@prid AND t2.Engine <> 'mediakit' ORDER BY t2.CreatedAt DESC, t2.Id DESC)", conn);
        cmd.Parameters.AddWithValue("@prid", promptId);
        cmd.Parameters.AddWithValue("@vu", (object?)videoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lv", (object?)localVideoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@res", (object?)resolution ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePromptText(int promptId, string promptText)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET PromptText=@pt WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@pt", promptText);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }
    public void DeletePrompt(int promptId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SeedancePrompts WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }


    public void UpdatePromptReferences(int promptId, string? refImages, string? refVideos, string? refAudio)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET ReferenceImages=@ri, ReferenceVideos=@rv, ReferenceAudio=@ra WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@ri", (object?)refImages ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rv", (object?)refVideos ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ra", (object?)refAudio ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }
    public void UpdatePromptShotLabel(int promptId, string shotLabel)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET ShotLabel=@sl WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@sl", shotLabel ?? "");
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }

    // ========== 视频任务记录 ==========
    public void SaveVideoTask(VideoGenerationTask task)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
INSERT INTO VideoGenerationTasks(ProjectId,PromptId,TaskId,Engine,Status,VideoUrl,RequestDuration,RequestRatio,RequestWatermark,RequestGenerateAudio,ResponseResolution,ResponseUsageTokens,ResponseSeed,ApiStatus,ErrorMessage,CreatedAt,CompletedAt)
VALUES(@pid,@prid,@tid,@eng,@st,@vu,@rd,@rr,@rw,@rga,@res,@tok,@seed,@apist,@err,@cat,@com)
", conn);
        cmd.Parameters.AddWithValue("@pid", task.ProjectId);
        cmd.Parameters.AddWithValue("@prid", task.PromptId);
        cmd.Parameters.AddWithValue("@tid", task.TaskId);
        cmd.Parameters.AddWithValue("@eng", string.IsNullOrEmpty(task.Engine) ? "volcano" : task.Engine);
        cmd.Parameters.AddWithValue("@st", task.Status);
        cmd.Parameters.AddWithValue("@vu", (object?)task.VideoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rd", task.RequestDuration);
        cmd.Parameters.AddWithValue("@rr", task.RequestRatio);
        cmd.Parameters.AddWithValue("@rw", task.RequestWatermark);
        cmd.Parameters.AddWithValue("@rga", task.RequestGenerateAudio);
        cmd.Parameters.AddWithValue("@res", (object?)task.ResponseResolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tok", (object?)task.ResponseUsageTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@seed", (object?)task.ResponseSeed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@apist", (object?)task.ApiStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err", (object?)task.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cat", task.CreatedAt);
        cmd.Parameters.AddWithValue("@com", (object?)task.CompletedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpdateVideoTaskStatus(int promptId, string taskId, string status, string? videoUrl, string? localVideoUrl, string? resolution, int? usageTokens, int? seed, DateTime? completedAt, string? apiStatus = null, double? duration = null)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE VideoGenerationTasks SET Status=@st, VideoUrl=@vu, LocalVideoUrl=@lv, ResponseResolution=@res, ResponseUsageTokens=@tok, ResponseSeed=@seed, ApiStatus=@apist, CompletedAt=@com,
EnhanceDuration = CASE WHEN Engine='mediakit' AND @st IN ('completed','succeeded') THEN DATEDIFF(SECOND, CreatedAt, @com) ELSE EnhanceDuration END
WHERE PromptId=@prid AND TaskId=@tid
", conn);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@vu", (object?)videoUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lv", (object?)localVideoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@res", (object?)resolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tok", (object?)usageTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@seed", (object?)seed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@apist", (object?)apiStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@com", (object?)completedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prid", promptId);
        cmd.Parameters.AddWithValue("@tid", taskId);
        cmd.ExecuteNonQuery();

        if (duration.HasValue)
        {
            using var cmd2 = new SqlCommand("UPDATE VideoGenerationTasks SET ResponseDuration=@dur WHERE PromptId=@prid AND TaskId=@tid", conn);
            cmd2.Parameters.AddWithValue("@dur", duration.Value);
            cmd2.Parameters.AddWithValue("@prid", promptId);
            cmd2.Parameters.AddWithValue("@tid", taskId);
            cmd2.ExecuteNonQuery();
        }
    }

    /// <summary>保存视频解析出的真实分辨率与时长（秒）</summary>
    public void UpdateVideoTaskMedia(int promptId, string taskId, string? resolution, double? duration)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
UPDATE VideoGenerationTasks SET ResponseResolution=@res, ResponseDuration=@dur
WHERE PromptId=@prid AND TaskId=@tid
", conn);
        cmd.Parameters.AddWithValue("@res", (object?)resolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", (object?)duration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prid", promptId);
        cmd.Parameters.AddWithValue("@tid", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>按远程视频地址查已落地过的本地文件，用于避免同一份视频被重复下载。</summary>
    public string? GetLocalVideoUrlByRemoteUrl(string videoUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT TOP 1 LocalVideoUrl FROM VideoGenerationTasks
WHERE VideoUrl=@vu AND LocalVideoUrl IS NOT NULL AND LocalVideoUrl<>''
ORDER BY Id DESC", conn);
        cmd.Parameters.AddWithValue("@vu", videoUrl);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// 所有被数据库引用过的本地视频地址（生成任务记录 + 提示词记录）。
    /// 整理重复文件时用来保护在用文件：被引用的一律优先保留，绝不搬走。
    /// </summary>
    public HashSet<string> GetAllReferencedLocalVideoUrls()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = GetConn(); conn.Open();
        foreach (var sql in new[]
                 {
                     "SELECT LocalVideoUrl FROM VideoGenerationTasks WHERE LocalVideoUrl IS NOT NULL AND LocalVideoUrl<>''",
                     "SELECT LocalVideoUrl FROM SeedancePrompts WHERE LocalVideoUrl IS NOT NULL AND LocalVideoUrl<>''"
                 })
        {
            using var cmd = new SqlCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.GetValue(0) is string v && !string.IsNullOrWhiteSpace(v)) set.Add(v.Trim());
            }
        }
        return set;
    }

    public List<VideoGenerationTask> GetVideoTasks(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM VideoGenerationTasks WHERE ProjectId=@pid ORDER BY CreatedAt DESC", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        var list = new List<VideoGenerationTask>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VideoGenerationTask
            {
                Id = (int)r["Id"],
                ProjectId = (int)r["ProjectId"],
                PromptId = (int)r["PromptId"],
                TaskId = (string)r["TaskId"],
                Engine = r["Engine"] == DBNull.Value ? "volcano" : (string)r["Engine"],
                Status = (string)r["Status"],
                VideoUrl = r["VideoUrl"] == DBNull.Value ? null : (string)r["VideoUrl"],
                RequestDuration = (int)r["RequestDuration"],
                RequestRatio = (string)r["RequestRatio"],
                RequestWatermark = (bool)r["RequestWatermark"],
                RequestGenerateAudio = (bool)r["RequestGenerateAudio"],
                ResponseResolution = r["ResponseResolution"] == DBNull.Value ? null : (string)r["ResponseResolution"],
                ResponseDuration = r["ResponseDuration"] == DBNull.Value ? null : (double?)r["ResponseDuration"],
                EnhanceDuration = r["EnhanceDuration"] == DBNull.Value ? null : (int?)r["EnhanceDuration"],
                EnhanceResolution = r["EnhanceResolution"] == DBNull.Value ? null : (string)r["EnhanceResolution"],
                ResponseUsageTokens = r["ResponseUsageTokens"] == DBNull.Value ? null : (int?)r["ResponseUsageTokens"],
                ResponseSeed = r["ResponseSeed"] == DBNull.Value ? null : (int?)r["ResponseSeed"],
                ApiStatus = r["ApiStatus"] == DBNull.Value ? null : (string)r["ApiStatus"],
                ErrorMessage = r["ErrorMessage"] == DBNull.Value ? null : (string)r["ErrorMessage"],
                CreatedAt = (DateTime)r["CreatedAt"],
                CompletedAt = r["CompletedAt"] == DBNull.Value ? null : (DateTime?)r["CompletedAt"]
            });
        }
        return list;
    }

    /// <summary>
    /// 只取某个提示词最新的一条视频任务记录。
    /// <para>状态轮询是高频接口（每个生成中的镜头每 5 秒一次），原来走 GetVideoTasks(projectId) 会把
    /// 整个项目的历史任务全捞出来再在内存里筛，项目跑久了就是几千行的无谓读取。这里下推到 SQL。</para>
    /// </summary>
    public VideoGenerationTask? GetLatestVideoTask(int projectId, int promptId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"SELECT TOP 1 * FROM VideoGenerationTasks
WHERE ProjectId=@pid AND PromptId=@prid
ORDER BY CreatedAt DESC, Id DESC", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@prid", promptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new VideoGenerationTask
        {
            Id = (int)r["Id"],
            ProjectId = (int)r["ProjectId"],
            PromptId = (int)r["PromptId"],
            TaskId = (string)r["TaskId"],
            Engine = r["Engine"] == DBNull.Value ? "volcano" : (string)r["Engine"],
            Status = (string)r["Status"],
            VideoUrl = r["VideoUrl"] == DBNull.Value ? null : (string)r["VideoUrl"],
            RequestDuration = (int)r["RequestDuration"],
            RequestRatio = (string)r["RequestRatio"],
            RequestWatermark = (bool)r["RequestWatermark"],
            RequestGenerateAudio = (bool)r["RequestGenerateAudio"],
            ResponseResolution = r["ResponseResolution"] == DBNull.Value ? null : (string)r["ResponseResolution"],
            ResponseDuration = r["ResponseDuration"] == DBNull.Value ? null : (double?)r["ResponseDuration"],
            EnhanceDuration = r["EnhanceDuration"] == DBNull.Value ? null : (int?)r["EnhanceDuration"],
            EnhanceResolution = r["EnhanceResolution"] == DBNull.Value ? null : (string)r["EnhanceResolution"],
            ResponseUsageTokens = r["ResponseUsageTokens"] == DBNull.Value ? null : (int?)r["ResponseUsageTokens"],
            ResponseSeed = r["ResponseSeed"] == DBNull.Value ? null : (int?)r["ResponseSeed"],
            ApiStatus = r["ApiStatus"] == DBNull.Value ? null : (string)r["ApiStatus"],
            ErrorMessage = r["ErrorMessage"] == DBNull.Value ? null : (string)r["ErrorMessage"],
            CreatedAt = (DateTime)r["CreatedAt"],
            CompletedAt = r["CompletedAt"] == DBNull.Value ? null : (DateTime?)r["CompletedAt"]
        };
    }

    /// <summary>
    /// 只取提示词的「状态 / 远程视频地址 / 本地视频地址」三个字段。
    /// <para>轮询里只需要这三项，但 GetPrompts(projectId) 会把整个项目的提示词正文（含 H3 长文本）
    /// 全部读出来再筛一条 —— 39 条就是每次轮询白读几十 KB。</para>
    /// </summary>
    public (string Status, string? VideoUrl, string? LocalVideoUrl)? GetPromptLight(int promptId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT Status, VideoUrl, LocalVideoUrl FROM SeedancePrompts WHERE PromptId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", promptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (
            r["Status"] == DBNull.Value ? "pending" : (string)r["Status"],
            r["VideoUrl"] == DBNull.Value ? null : (string)r["VideoUrl"],
            r["LocalVideoUrl"] == DBNull.Value ? null : (string)r["LocalVideoUrl"]);
    }

    public void UpdateVideoTaskError(int promptId, string taskId, string error)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE VideoGenerationTasks SET Status='failed', ErrorMessage=@err WHERE PromptId=@prid AND TaskId=@tid", conn);
        cmd.Parameters.AddWithValue("@err", error);
        cmd.Parameters.AddWithValue("@prid", promptId);
        cmd.Parameters.AddWithValue("@tid", taskId);
        cmd.ExecuteNonQuery();
    }

    // ========== Background polling support ==========
    public List<(int PromptId, int ProjectId, int UserId, string VideoUrl, int Duration)> GetPendingAutoEnhancePrompts()
    {
        var list = new List<(int PromptId, int ProjectId, int UserId, string VideoUrl, int Duration)>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT p.PromptId, p.ProjectId, pr.UserId, p.VideoUrl, p.Duration
FROM SeedancePrompts p
JOIN Projects pr ON p.ProjectId = pr.ProjectId
WHERE p.Status = 'completed'
  AND pr.Status<>N'deleted'
  AND p.VideoUrl IS NOT NULL AND p.VideoUrl LIKE 'https://%'
  AND NOT EXISTS (
      SELECT 1 FROM VideoGenerationTasks t
      WHERE t.PromptId = p.PromptId AND t.Engine = 'mediakit'
        AND t.Status IN ('enhancing','completed','succeeded')
  )
  -- 只增强开关开启（UpdatedAt）之后完成的视频，避免历史已完成视频被误增强
  AND EXISTS (
      SELECT 1 FROM VideoGenerationTasks g
      WHERE g.PromptId = p.PromptId AND g.Engine <> 'mediakit'
        AND g.Status IN ('completed','succeeded')
        AND g.CompletedAt IS NOT NULL
        AND g.CompletedAt >= (SELECT UpdatedAt FROM LLMConfigs WHERE UserId=pr.UserId AND Provider='mediakit' AND IsActive=1)
  )", conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((
                (int)r["PromptId"],
                (int)r["ProjectId"],
                (int)r["UserId"],
                (string)r["VideoUrl"],
                (int)r["Duration"]
            ));
        }
        return list;
    }

    public List<(int PromptId, int ProjectId, int UserId, string? TaskId, string Engine)> GetProcessingPrompts()
    {
        var list = new List<(int PromptId, int ProjectId, int UserId, string? TaskId, string Engine)>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"SELECT p.PromptId, p.ProjectId, pr.UserId, t.TaskId, t.Engine
            FROM SeedancePrompts p
            JOIN Projects pr ON p.ProjectId = pr.ProjectId
            OUTER APPLY (
                SELECT TOP 1 t.TaskId, t.Engine FROM VideoGenerationTasks t
                WHERE t.PromptId = p.PromptId
                ORDER BY t.CreatedAt DESC, t.Id DESC
            ) t
            WHERE p.Status IN ('processing','running','pending','enhancing')
              AND pr.Status<>N'deleted'", conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((
                (int)r["PromptId"],
                (int)r["ProjectId"],
                (int)r["UserId"],
                r["TaskId"] == DBNull.Value ? null : (string)r["TaskId"],
                r["Engine"] == DBNull.Value ? "volcano" : (string)r["Engine"]
            ));
        }
        return list;
    }
    public void UpdateProjectCover(int id, int userId, string? coverImage, string? title, string? description = null)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET CoverImage=@c, Title=@t, Description=@description, UpdatedAt=GETDATE() WHERE ProjectId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@c", (object?)coverImage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        cmd.ExecuteNonQuery();

    }

    public void UpdateProjectCoverImage(int id, int userId, string coverUrl)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET CoverImage=@c, UpdatedAt=GETDATE() WHERE ProjectId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@c", (object?)coverUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ========== 视频风格 ==========
    public List<VideoStyle> GetVideoStyles()
    {
        var list = new List<VideoStyle>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM VideoStyles ORDER BY IsDefault DESC, StyleId ASC", conn);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadVideoStyle(r));
        return list;
    }

    public VideoStyle? GetVideoStyle(int styleId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM VideoStyles WHERE StyleId=@id", conn);
        cmd.Parameters.AddWithValue("@id", styleId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadVideoStyle(r);
        return null;
    }

    public int SaveVideoStyle(int? styleId, string styleName, string stylePrompt)
    {
        using var conn = GetConn(); conn.Open();
        if (styleId.HasValue && styleId.Value > 0)
        {
            using var cmd = new SqlCommand("UPDATE VideoStyles SET StyleName=@n, StylePrompt=@p, UpdatedAt=GETDATE() WHERE StyleId=@id", conn);
            cmd.Parameters.AddWithValue("@n", styleName);
            cmd.Parameters.AddWithValue("@p", stylePrompt);
            cmd.Parameters.AddWithValue("@id", styleId.Value);
            cmd.ExecuteNonQuery();
            return styleId.Value;
        }
        using var ins = new SqlCommand("INSERT INTO VideoStyles(StyleName,StylePrompt,IsDefault) OUTPUT INSERTED.StyleId VALUES(@n,@p,0)", conn);
        ins.Parameters.AddWithValue("@n", styleName);
        ins.Parameters.AddWithValue("@p", stylePrompt);
        return (int)ins.ExecuteScalar();
    }

    public void DeleteVideoStyle(int styleId)
    {
        using var conn = GetConn(); conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using (var c1 = new SqlCommand("UPDATE Projects SET StyleId=NULL WHERE StyleId=@id", conn, txn))
            { c1.Parameters.AddWithValue("@id", styleId); c1.ExecuteNonQuery(); }
            using (var c2 = new SqlCommand("DELETE FROM VideoStyles WHERE StyleId=@id", conn, txn))
            { c2.Parameters.AddWithValue("@id", styleId); c2.ExecuteNonQuery(); }
            txn.Commit();
        }
        catch { txn.Rollback(); throw; }
    }

    public void UpdateProjectStyle(int projectId, int? styleId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET StyleId=@s, UpdatedAt=GETDATE() WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@s", (object?)styleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateProjectTags(int projectId, string? tags)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET Tags=@t, UpdatedAt=GETDATE() WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@t", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateProjectVideoSettings(int projectId, string ratio, bool watermark, bool audio, string resolution)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE Projects SET VideoRatio=@ratio, VideoWatermark=@wm, VideoAudio=@aud, VideoResolution=@res, UpdatedAt=GETDATE() WHERE ProjectId=@id", conn);
        cmd.Parameters.AddWithValue("@ratio", ratio);
        cmd.Parameters.AddWithValue("@wm", watermark);
        cmd.Parameters.AddWithValue("@aud", audio);
        cmd.Parameters.AddWithValue("@res", resolution);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }

    private static VideoStyle ReadVideoStyle(SqlDataReader r) => new VideoStyle
    {
        StyleId = (int)r["StyleId"],
        StyleName = (string)r["StyleName"],
        StylePrompt = (string)r["StylePrompt"],
        IsDefault = r["IsDefault"] == DBNull.Value ? false : (bool)r["IsDefault"],
        CreatedAt = (DateTime)r["CreatedAt"],
        UpdatedAt = (DateTime)r["UpdatedAt"]
    };

    // ========== Token 用量统计 ==========
    public (long QuotaTokens, long UsedTokens, long RemainingTokens) GetTokenUsageStats()
    {
        long quota = 0, used = 0;
        using var conn = GetConn(); conn.Open();
        using (var cmd = new SqlCommand("SELECT TOP 1 QuotaTokens FROM TokenUsageConfig WHERE Id=1", conn))
        {
            var v = cmd.ExecuteScalar();
            if (v != null && v != DBNull.Value) quota = Convert.ToInt64(v);
        }
        using (var cmd = new SqlCommand("SELECT ISNULL(SUM(CAST(ResponseUsageTokens AS BIGINT)),0) FROM VideoGenerationTasks", conn))
        {
            var v = cmd.ExecuteScalar();
            if (v != null && v != DBNull.Value) used = Convert.ToInt64(v);
        }
        return (quota, used, Math.Max(0, quota - used));
    }

    public long GetProjectTokenUsage(int projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT ISNULL(SUM(CAST(ResponseUsageTokens AS BIGINT)),0) FROM VideoGenerationTasks WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        var v = cmd.ExecuteScalar();
        return v != null && v != DBNull.Value ? Convert.ToInt64(v) : 0;
    }

    public List<ProjectTokenUsage> GetProjectTokenUsages(int userId)
    {
        var list = new List<ProjectTokenUsage>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT p.ProjectId, p.Title AS ProjectName,
       ISNULL(SUM(CAST(t.ResponseUsageTokens AS BIGINT)),0) AS UsedTokens,
       ISNULL(SUM(CAST(t.ResponseDuration AS BIGINT)),0) AS TotalSeconds
FROM Projects p
LEFT JOIN VideoGenerationTasks t ON t.ProjectId = p.ProjectId
WHERE p.UserId=@uid AND p.Status<>N'deleted'
GROUP BY p.ProjectId, p.Title
ORDER BY UsedTokens DESC", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProjectTokenUsage
            {
                ProjectId = (int)r["ProjectId"],
                ProjectName = (string)r["ProjectName"],
                UsedTokens = Convert.ToInt64(r["UsedTokens"]),
                TotalSeconds = Convert.ToInt64(r["TotalSeconds"])
            });
        return list;
    }

    public void SetTokenQuota(long quota)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE TokenUsageConfig SET QuotaTokens=@q, UpdatedAt=GETDATE() WHERE Id=1", conn);
        cmd.Parameters.AddWithValue("@q", quota);
        cmd.ExecuteNonQuery();
    }

    // ========== Reference Assets (用户级资产库) ==========
    public List<ReferenceAsset> GetReferenceAssets(int userId, string? category = null, string? subCategory = null, string? search = null, string? tag = null, List<string>? tags = null, string? sourceKey = null)
    {
        var list = new List<ReferenceAsset>();
        using var conn = GetConn(); conn.Open();
        var sql = "SELECT * FROM ReferenceAssets WHERE UserId=@uid";
        if (!string.IsNullOrEmpty(category)) sql += " AND Category=@cat";
        if (!string.IsNullOrEmpty(subCategory)) sql += " AND SubCategory=@sub";
        if (!string.IsNullOrEmpty(search)) sql += " AND FileName LIKE @search";
        if (!string.IsNullOrEmpty(tag)) sql += " AND Tags LIKE @tag";
        if (!string.IsNullOrEmpty(sourceKey)) sql += " AND SourceKey=@sk";
        if (tags != null && tags.Count > 0)
        {
            var ors = new List<string>();
            for (var i = 0; i < tags.Count; i++) ors.Add($"Tags LIKE @tag{i}");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        sql += " ORDER BY CreatedAt DESC";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        if (!string.IsNullOrEmpty(category)) cmd.Parameters.AddWithValue("@cat", category);
        if (!string.IsNullOrEmpty(subCategory)) cmd.Parameters.AddWithValue("@sub", subCategory);
                if (!string.IsNullOrEmpty(search)) cmd.Parameters.AddWithValue("@search", $"%{search}%");
        if (!string.IsNullOrEmpty(tag)) cmd.Parameters.AddWithValue("@tag", $"%{tag}%");
        if (!string.IsNullOrEmpty(sourceKey)) cmd.Parameters.AddWithValue("@sk", sourceKey);
        if (tags != null) for (var i = 0; i < tags.Count; i++) cmd.Parameters.AddWithValue($"@tag{i}", $"%{tags[i]}%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadReferenceAsset(r));
        return list;
    }

    public void UpdatePromptDuration(int promptId, int duration)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SeedancePrompts SET Duration=@dur WHERE PromptId=@id", conn);
        cmd.Parameters.AddWithValue("@dur", duration);
        cmd.Parameters.AddWithValue("@id", promptId);
        cmd.ExecuteNonQuery();
    }    

    public ReferenceAsset? GetReferenceAsset(int assetId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM ReferenceAssets WHERE AssetId=@id", conn);
        cmd.Parameters.AddWithValue("@id", assetId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadReferenceAsset(r);
        return null;
    }

    public int SaveReferenceAsset(int userId, string fileName, string localPath, string category, string subCategory, string? tags = null, long? fileSize = null, string? sourceKey = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "INSERT INTO ReferenceAssets(UserId,FileName,LocalPath,FileType,Category,SubCategory,Tags,FileSize,SourceKey) OUTPUT INSERTED.AssetId VALUES(@uid,@fn,@lp,'image',@cat,@sub,@tags,@fs,@sk)";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@fn", fileName);
        cmd.Parameters.AddWithValue("@lp", localPath);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@sub", subCategory);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fs", (object?)fileSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sk", (object?)sourceKey ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public void DeleteReferenceAsset(int assetId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM ReferenceAssets WHERE AssetId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", assetId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public void RenameReferenceAsset(int assetId, int userId, string newName, string? category = null, string? subCategory = null, string? tags = null)
    {
        UpdateReferenceAsset(assetId, userId, newName, category, subCategory, tags);
    }

    public void UpdateReferenceAsset(int assetId, int userId, string newName, string? category = null, string? subCategory = null, string? tags = null, string? localPath = null, long? fileSize = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "UPDATE ReferenceAssets SET FileName=@fn, Category=@cat, SubCategory=@sub, Tags=@tags";
        if (localPath != null) sql += ", LocalPath=@lp";
        if (fileSize.HasValue) sql += ", FileSize=@fs";
        sql += " WHERE AssetId=@id AND UserId=@uid";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@fn", newName);
        cmd.Parameters.AddWithValue("@cat", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sub", (object?)subCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        if (localPath != null) cmd.Parameters.AddWithValue("@lp", localPath);
        if (fileSize.HasValue) cmd.Parameters.AddWithValue("@fs", fileSize.Value);
        cmd.Parameters.AddWithValue("@id", assetId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }


    // ========== Skill Library (技能库) ==========
    public List<SkillLibraryItem> GetSkills(int userId, string? element = null, int? tier = null, string? search = null, int? projectId = null, string? tags = null, string? owner = null)
    {
        var list = new List<SkillLibraryItem>();
        using var conn = GetConn(); conn.Open();
        var sql = "SELECT * FROM SkillLibrary WHERE UserId=@uid";
        if (!string.IsNullOrEmpty(element)) sql += " AND Element=@el";
        if (tier.HasValue && tier.Value >= 1 && tier.Value <= 5) sql += " AND Tier=@tier";
        if (!string.IsNullOrEmpty(search)) sql += " AND (Name LIKE @search OR Tags LIKE @search OR PromptImage LIKE @search OR PromptVideo LIKE @search)";
        if (projectId.HasValue && projectId.Value > 0) sql += " AND (ProjectId=@pid OR ProjectId IS NULL)";
        var tagList = ParseSkillTagList(tags);
        if (tagList.Count > 0)
        {
            var tagConditions = tagList.Select((t, i) => $"Tags LIKE @tag{i}").ToList();
            tagConditions.Add("(Tags IS NULL OR Tags='')");
            sql += " AND (" + string.Join(" OR ", tagConditions) + ")";
        }
        if (!string.IsNullOrEmpty(owner)) sql += " AND OwnerCharacter LIKE @owner";
        sql += " ORDER BY Element, Tier DESC, CreatedAt DESC";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        if (!string.IsNullOrEmpty(element)) cmd.Parameters.AddWithValue("@el", element);
        if (tier.HasValue && tier.Value >= 1 && tier.Value <= 5) cmd.Parameters.AddWithValue("@tier", tier.Value);
        if (!string.IsNullOrEmpty(search)) cmd.Parameters.AddWithValue("@search", "%" + search + "%");
        if (projectId.HasValue && projectId.Value > 0) cmd.Parameters.AddWithValue("@pid", projectId.Value);
        for (var i = 0; i < tagList.Count; i++) cmd.Parameters.AddWithValue($"@tag{i}", "%" + tagList[i] + "%");
        if (!string.IsNullOrEmpty(owner)) cmd.Parameters.AddWithValue("@owner", "%" + owner + "%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSkillItem(r));
        return list;
    }

    public SkillLibraryItem? GetSkill(int skillId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM SkillLibrary WHERE SkillId=@id", conn);
        cmd.Parameters.AddWithValue("@id", skillId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadSkillItem(r);
        return null;
    }

    public int SaveSkill(int userId, string name, string element, int tier, string promptImage, string promptVideo, string? tags = null, int? projectId = null, string? ownerCharacter = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "INSERT INTO SkillLibrary(UserId,Name,Element,Tier,OwnerCharacter,PromptImage,PromptVideo,Tags,ProjectId) OUTPUT INSERTED.SkillId VALUES(@uid,@name,@el,@tier,@owner,@pi,@pv,@tags,@pid)";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@el", (object?)element ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tier", tier);
        cmd.Parameters.AddWithValue("@owner", (object?)ownerCharacter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pi", (object?)promptImage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pv", (object?)promptVideo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public void UpdateSkill(int skillId, int userId, string name, string element, int tier, string promptImage, string promptVideo, string? tags = null, int? projectId = null, string? ownerCharacter = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "UPDATE SkillLibrary SET Name=@name, Element=@el, Tier=@tier, OwnerCharacter=@owner, PromptImage=@pi, PromptVideo=@pv, Tags=@tags, ProjectId=@pid WHERE SkillId=@id AND UserId=@uid";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@el", (object?)element ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tier", tier);
        cmd.Parameters.AddWithValue("@owner", (object?)ownerCharacter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pi", (object?)promptImage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pv", (object?)promptVideo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", skillId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateSkillImage(int skillId, int userId, string imageUrl)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("UPDATE SkillLibrary SET ImageUrl=@img WHERE SkillId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@img", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", skillId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSkill(int skillId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SkillLibrary WHERE SkillId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", skillId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public List<SkillElement> GetSkillElements(int userId)
    {
        var list = new List<SkillElement>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"SELECT e.ElementId, e.UserId, e.Name, e.SortOrder, e.CreatedAt,
       (SELECT COUNT(*) FROM SkillLibrary s WHERE s.UserId=e.UserId AND s.Element=e.Name) AS UsageCount
FROM SkillElements e WHERE e.UserId=@uid ORDER BY e.SortOrder, e.ElementId", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSkillElement(r));
        return list;
    }

    public SkillElement? GetSkillElement(int elementId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"SELECT e.ElementId, e.UserId, e.Name, e.SortOrder, e.CreatedAt,
       (SELECT COUNT(*) FROM SkillLibrary s WHERE s.UserId=e.UserId AND s.Element=e.Name) AS UsageCount
FROM SkillElements e WHERE e.ElementId=@id", conn);
        cmd.Parameters.AddWithValue("@id", elementId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadSkillElement(r);
        return null;
    }

    public int SaveSkillElement(int userId, string name, int sortOrder)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("INSERT INTO SkillElements(UserId,Name,SortOrder) OUTPUT INSERTED.ElementId VALUES(@uid,@name,@sort)", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@sort", sortOrder);
        return (int)cmd.ExecuteScalar();
    }

    public bool UpdateSkillElement(int elementId, int userId, string newName, int sortOrder, string oldName)
    {
        using var conn = GetConn(); conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using var cmd = new SqlCommand("UPDATE SkillElements SET Name=@name, SortOrder=@sort WHERE ElementId=@id AND UserId=@uid", conn, txn);
            cmd.Parameters.AddWithValue("@name", newName);
            cmd.Parameters.AddWithValue("@sort", sortOrder);
            cmd.Parameters.AddWithValue("@id", elementId);
            cmd.Parameters.AddWithValue("@uid", userId);
            if (cmd.ExecuteNonQuery() == 0) { txn.Rollback(); return false; }
            using var sync = new SqlCommand("UPDATE SkillLibrary SET Element=@name WHERE UserId=@uid AND Element=@old", conn, txn);
            sync.Parameters.AddWithValue("@name", newName);
            sync.Parameters.AddWithValue("@uid", userId);
            sync.Parameters.AddWithValue("@old", oldName);
            sync.ExecuteNonQuery();
            txn.Commit();
            return true;
        }
        catch { txn.Rollback(); throw; }
    }

    public int GetSkillElementUsageCount(int elementId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"SELECT COUNT(*) FROM SkillLibrary s
WHERE s.UserId=@uid AND s.Element=(SELECT Name FROM SkillElements WHERE ElementId=@id AND UserId=@uid)", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@id", elementId);
        return (int)cmd.ExecuteScalar();
    }

    public bool DeleteSkillElement(int elementId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM SkillElements WHERE ElementId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", elementId);
        cmd.Parameters.AddWithValue("@uid", userId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void InitializeDefaultSkillElements(int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"INSERT INTO SkillElements(UserId,Name,SortOrder)
SELECT @uid, x.Name, x.SortOrder
FROM (VALUES (N'火',1),(N'冰',2),(N'雷',3),(N'剑阵',4),(N'风',5),(N'暗',6),(N'圣',7)) x(Name,SortOrder)
WHERE NOT EXISTS (SELECT 1 FROM SkillElements WHERE UserId=@uid AND Name=x.Name)", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    private static SkillElement ReadSkillElement(SqlDataReader r) => new SkillElement
    {
        ElementId = r.GetInt32(r.GetOrdinal("ElementId")),
        UserId = r.GetInt32(r.GetOrdinal("UserId")),
        Name = r.IsDBNull(r.GetOrdinal("Name")) ? "" : r.GetString(r.GetOrdinal("Name")),
        SortOrder = r.IsDBNull(r.GetOrdinal("SortOrder")) ? 0 : r.GetInt32(r.GetOrdinal("SortOrder")),
        UsageCount = r.IsDBNull(r.GetOrdinal("UsageCount")) ? 0 : r.GetInt32(r.GetOrdinal("UsageCount")),
        CreatedAt = r.IsDBNull(r.GetOrdinal("CreatedAt")) ? DateTime.Now : r.GetDateTime(r.GetOrdinal("CreatedAt"))
    };

    private static SkillLibraryItem ReadSkillItem(SqlDataReader r)
    {
        return new SkillLibraryItem
        {
            SkillId = r.GetInt32(r.GetOrdinal("SkillId")),
            UserId = r.GetInt32(r.GetOrdinal("UserId")),
            ProjectId = r.IsDBNull(r.GetOrdinal("ProjectId")) ? null : r.GetInt32(r.GetOrdinal("ProjectId")),
            Name = r.IsDBNull(r.GetOrdinal("Name")) ? "" : r.GetString(r.GetOrdinal("Name")),
            Element = r.IsDBNull(r.GetOrdinal("Element")) ? "" : r.GetString(r.GetOrdinal("Element")),
            Tier = r.IsDBNull(r.GetOrdinal("Tier")) ? 4 : r.GetInt32(r.GetOrdinal("Tier")),
            OwnerCharacter = HasColumn(r, "OwnerCharacter") && !r.IsDBNull(r.GetOrdinal("OwnerCharacter")) ? r.GetString(r.GetOrdinal("OwnerCharacter")) : "",
            PromptImage = r.IsDBNull(r.GetOrdinal("PromptImage")) ? "" : r.GetString(r.GetOrdinal("PromptImage")),
            PromptVideo = r.IsDBNull(r.GetOrdinal("PromptVideo")) ? "" : r.GetString(r.GetOrdinal("PromptVideo")),
            ImageUrl = r.IsDBNull(r.GetOrdinal("ImageUrl")) ? "" : r.GetString(r.GetOrdinal("ImageUrl")),
            Tags = r.IsDBNull(r.GetOrdinal("Tags")) ? "" : r.GetString(r.GetOrdinal("Tags")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("CreatedAt")) ? DateTime.Now : r.GetDateTime(r.GetOrdinal("CreatedAt"))
        };
    }

    private static List<string> ParseSkillTagList(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return new List<string>();
        return tags.Split(new[] { ',', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasColumn(SqlDataReader r, string columnName)
    {
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (string.Equals(r.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ========== FightTemplate (打斗模板库) ==========
    public List<FightTemplateItem> GetFightTemplates(int userId, int? tier = null, string? search = null)
    {
        var list = new List<FightTemplateItem>();
        using var conn = GetConn(); conn.Open();
        var sql = "SELECT * FROM FightTemplate WHERE UserId=@uid";
        if (tier.HasValue && tier.Value >= 1 && tier.Value <= 5) sql += " AND Tier=@tier";
        if (!string.IsNullOrEmpty(search)) sql += " AND (Name LIKE @search OR Scene LIKE @search OR Beat LIKE @search OR Tags LIKE @search OR ActionPrompt LIKE @search)";
        sql += " ORDER BY Tier, Duration DESC, FightTemplateId";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        if (tier.HasValue && tier.Value >= 1 && tier.Value <= 5) cmd.Parameters.AddWithValue("@tier", tier.Value);
        if (!string.IsNullOrEmpty(search)) cmd.Parameters.AddWithValue("@search", "%" + search + "%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFightTemplateItem(r));
        return list;
    }

    public FightTemplateItem? GetFightTemplate(int fightTemplateId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM FightTemplate WHERE FightTemplateId=@id", conn);
        cmd.Parameters.AddWithValue("@id", fightTemplateId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadFightTemplateItem(r);
        return null;
    }

    public int SaveFightTemplate(int userId, string name, int tier, int duration, string scene, string beat, string actionPrompt, string cameraPrompt, string constraintPrompt, string? tags = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "INSERT INTO FightTemplate(UserId,Name,Tier,Duration,Scene,Beat,ActionPrompt,CameraPrompt,ConstraintPrompt,Tags) OUTPUT INSERTED.FightTemplateId VALUES(@uid,@name,@tier,@dur,@scene,@beat,@ap,@cp,@kp,@tags)";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@tier", tier);
        cmd.Parameters.AddWithValue("@dur", duration);
        cmd.Parameters.AddWithValue("@scene", (object?)scene ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@beat", (object?)beat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ap", (object?)actionPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cp", (object?)cameraPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kp", (object?)constraintPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public void UpdateFightTemplate(int fightTemplateId, int userId, string name, int tier, int duration, string scene, string beat, string actionPrompt, string cameraPrompt, string constraintPrompt, string? tags = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "UPDATE FightTemplate SET Name=@name, Tier=@tier, Duration=@dur, Scene=@scene, Beat=@beat, ActionPrompt=@ap, CameraPrompt=@cp, ConstraintPrompt=@kp, Tags=@tags WHERE FightTemplateId=@id AND UserId=@uid";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@tier", tier);
        cmd.Parameters.AddWithValue("@dur", duration);
        cmd.Parameters.AddWithValue("@scene", (object?)scene ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@beat", (object?)beat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ap", (object?)actionPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cp", (object?)cameraPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kp", (object?)constraintPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", fightTemplateId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteFightTemplate(int fightTemplateId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM FightTemplate WHERE FightTemplateId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", fightTemplateId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    private static FightTemplateItem ReadFightTemplateItem(SqlDataReader r)
    {
        return new FightTemplateItem
        {
            FightTemplateId = r.GetInt32(r.GetOrdinal("FightTemplateId")),
            UserId = r.GetInt32(r.GetOrdinal("UserId")),
            Name = r.IsDBNull(r.GetOrdinal("Name")) ? "" : r.GetString(r.GetOrdinal("Name")),
            Tier = r.IsDBNull(r.GetOrdinal("Tier")) ? 3 : r.GetInt32(r.GetOrdinal("Tier")),
            Duration = r.IsDBNull(r.GetOrdinal("Duration")) ? 11 : r.GetInt32(r.GetOrdinal("Duration")),
            Scene = r.IsDBNull(r.GetOrdinal("Scene")) ? "" : r.GetString(r.GetOrdinal("Scene")),
            Beat = r.IsDBNull(r.GetOrdinal("Beat")) ? "" : r.GetString(r.GetOrdinal("Beat")),
            ActionPrompt = r.IsDBNull(r.GetOrdinal("ActionPrompt")) ? "" : r.GetString(r.GetOrdinal("ActionPrompt")),
            CameraPrompt = r.IsDBNull(r.GetOrdinal("CameraPrompt")) ? "" : r.GetString(r.GetOrdinal("CameraPrompt")),
            ConstraintPrompt = r.IsDBNull(r.GetOrdinal("ConstraintPrompt")) ? "" : r.GetString(r.GetOrdinal("ConstraintPrompt")),
            Tags = r.IsDBNull(r.GetOrdinal("Tags")) ? "" : r.GetString(r.GetOrdinal("Tags")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("CreatedAt")) ? DateTime.Now : r.GetDateTime(r.GetOrdinal("CreatedAt"))
        };
    }

    // ========== CameraAtom (运镜原子库) ==========

    // ========== FightArcTemplates (战斗段落骨架库) ==========
    public List<FightArcTemplate> GetFightArcTemplates(string? status = null)
    {
        var list = new List<FightArcTemplate>();
        using var conn = GetConn(); conn.Open();
        var sql = "SELECT * FROM FightArcTemplates";
        if (!string.IsNullOrWhiteSpace(status)) sql += " WHERE Status=@status";
        sql += " ORDER BY FightArcTemplateId";
        using var cmd = new SqlCommand(sql, conn);
        if (!string.IsNullOrWhiteSpace(status)) cmd.Parameters.AddWithValue("@status", status);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFightArcTemplate(r));
        return list;
    }

    public FightArcTemplate? GetFightArcTemplate(string arcTypeId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM FightArcTemplates WHERE ArcTypeId=@id", conn);
        cmd.Parameters.AddWithValue("@id", arcTypeId ?? "");
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadFightArcTemplate(r);
        return null;
    }

    private static FightArcTemplate ReadFightArcTemplate(SqlDataReader r)
    {
        return new FightArcTemplate
        {
            FightArcTemplateId = r.GetInt32(r.GetOrdinal("FightArcTemplateId")),
            ArcTypeId = r.IsDBNull(r.GetOrdinal("ArcTypeId")) ? "" : r.GetString(r.GetOrdinal("ArcTypeId")),
            Name = r.IsDBNull(r.GetOrdinal("Name")) ? "" : r.GetString(r.GetOrdinal("Name")),
            Description = r.IsDBNull(r.GetOrdinal("Description")) ? "" : r.GetString(r.GetOrdinal("Description")),
            Version = r.IsDBNull(r.GetOrdinal("Version")) ? "1.0" : r.GetString(r.GetOrdinal("Version")),
            Phases = TryParseJson(r["PhasesJson"] as string, () => new List<FightArcPhase>()),
            DurationBudget = TryParseJson(r["DurationBudgetJson"] as string, () => new FightDurationBudget()),
            Rules = TryParseJson(r["RulesJson"] as string, () => new List<string>()),
            Status = r.IsDBNull(r.GetOrdinal("Status")) ? "Active" : r.GetString(r.GetOrdinal("Status")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("CreatedAt")) ? DateTime.Now : r.GetDateTime(r.GetOrdinal("CreatedAt")),
            UpdatedAt = r.IsDBNull(r.GetOrdinal("UpdatedAt")) ? DateTime.Now : r.GetDateTime(r.GetOrdinal("UpdatedAt"))
        };
    }
    public List<CameraAtomItem> GetCameraAtoms(int userId, string? category = null, string? search = null, string? unitType = null)
    {
        var list = new List<CameraAtomItem>();
        using var conn = GetConn(); conn.Open();
        var sql = "SELECT * FROM CameraAtom WHERE UserId=@uid";
        if (!string.IsNullOrEmpty(category)) sql += " AND Category=@cat";
        if (!string.IsNullOrEmpty(search)) sql += " AND (Name LIKE @search OR Description LIKE @search OR Tags LIKE @search)";
        if (!string.IsNullOrEmpty(unitType)) sql += " AND (Tags IS NULL OR Tags='' OR Tags LIKE @ut OR Tags LIKE '%通用%')";
        sql += " ORDER BY Category, AtomId";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        if (!string.IsNullOrEmpty(category)) cmd.Parameters.AddWithValue("@cat", category);
        if (!string.IsNullOrEmpty(search)) cmd.Parameters.AddWithValue("@search", "%" + search + "%");
        if (!string.IsNullOrEmpty(unitType)) cmd.Parameters.AddWithValue("@ut", "%" + unitType + "%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadCameraAtomItem(r));
        return list;
    }

    public CameraAtomItem? GetCameraAtom(int atomId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM CameraAtom WHERE AtomId=@id", conn);
        cmd.Parameters.AddWithValue("@id", atomId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadCameraAtomItem(r);
        return null;
    }

    public int SaveCameraAtom(int userId, string name, string category, string description, string? tags = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "INSERT INTO CameraAtom(UserId,Name,Category,Description,Tags) OUTPUT INSERTED.AtomId VALUES(@uid,@name,@cat,@desc,@tags)";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public void UpdateCameraAtom(int atomId, int userId, string name, string category, string description, string? tags = null)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "UPDATE CameraAtom SET Name=@name, Category=@cat, Description=@desc, Tags=@tags WHERE AtomId=@id AND UserId=@uid";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", (object?)tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", atomId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteCameraAtom(int atomId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM CameraAtom WHERE AtomId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", atomId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    private static CameraAtomItem ReadCameraAtomItem(SqlDataReader r)
    {
        return new CameraAtomItem
        {
            AtomId = r.GetInt32(r.GetOrdinal("AtomId")),
            UserId = r.GetInt32(r.GetOrdinal("UserId")),
            Name = r.IsDBNull(r.GetOrdinal("Name")) ? "" : r.GetString(r.GetOrdinal("Name")),
            Category = r.IsDBNull(r.GetOrdinal("Category")) ? "" : r.GetString(r.GetOrdinal("Category")),
            Description = r.IsDBNull(r.GetOrdinal("Description")) ? "" : r.GetString(r.GetOrdinal("Description")),
            Tags = r.IsDBNull(r.GetOrdinal("Tags")) ? "" : r.GetString(r.GetOrdinal("Tags")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("CreatedAt")) ? DateTime.Now : r.GetDateTime(r.GetOrdinal("CreatedAt"))
        };
    }

    // ========== Works (作品展示) ==========
    public List<Work> GetWorks(int userId)
    {
        var list = new List<Work>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Works WHERE UserId=@uid ORDER BY CreatedAt DESC", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadWork(r));
        return list;
    }

    public Work? GetWorkById(int workId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM Works WHERE WorkId=@id", conn);
        cmd.Parameters.AddWithValue("@id", workId);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return ReadWork(r);
        return null;
    }

    public int SaveWork(int userId, string title, string description, string? coverImage, string? workUrl)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "INSERT INTO Works(UserId,Title,Description,CoverImage,WorkUrl) OUTPUT INSERTED.WorkId VALUES(@uid,@title,@desc,@cover,@url)";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cover", (object?)coverImage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", (object?)workUrl ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    public void UpdateWork(int workId, int userId, string title, string description, string? coverImage, string? workUrl)
    {
        using var conn = GetConn(); conn.Open();
        var sql = "UPDATE Works SET Title=@title, Description=@desc, UpdatedAt=GETDATE()";
        if (!string.IsNullOrEmpty(coverImage)) sql += ", CoverImage=@cover";
        if (!string.IsNullOrEmpty(workUrl)) sql += ", WorkUrl=@url";
        sql += " WHERE WorkId=@id AND UserId=@uid";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
        if (!string.IsNullOrEmpty(workUrl)) cmd.Parameters.AddWithValue("@url", workUrl);
        if (!string.IsNullOrEmpty(coverImage)) cmd.Parameters.AddWithValue("@cover", coverImage);
        cmd.Parameters.AddWithValue("@id", workId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteWork(int workId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM Works WHERE WorkId=@id AND UserId=@uid", conn);
        cmd.Parameters.AddWithValue("@id", workId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    public WorkDetail? GetWorkDetail(int workId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(@"
SELECT w.WorkId, w.UserId, w.Title, w.Description, w.CoverImage, w.WorkUrl, w.CreatedAt, w.UpdatedAt,
       ISNULL(u.Nickname, u.Username) AS AuthorName
FROM Works w LEFT JOIN Users u ON w.UserId = u.UserId
WHERE w.WorkId=@id", conn);
        cmd.Parameters.AddWithValue("@id", workId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new WorkDetail
        {
            WorkId = (int)r["WorkId"],
            UserId = (int)r["UserId"],
            Title = (string)r["Title"],
            Description = r["Description"] == DBNull.Value ? "" : (string)r["Description"],
            CoverImage = r["CoverImage"] == DBNull.Value ? "" : (string)r["CoverImage"],
            WorkUrl = r["WorkUrl"] == DBNull.Value ? "" : (string)r["WorkUrl"],
            CreatedAt = (DateTime)r["CreatedAt"],
            UpdatedAt = r["UpdatedAt"] == DBNull.Value ? null : (DateTime)r["UpdatedAt"],
            AuthorName = r["AuthorName"] == DBNull.Value ? "" : (string)r["AuthorName"]
        };
    }

    private static Work ReadWork(SqlDataReader r) => new Work
    {
        WorkId = (int)r["WorkId"],
        UserId = (int)r["UserId"],
        Title = (string)r["Title"],
        Description = r["Description"] == DBNull.Value ? "" : (string)r["Description"],
        CoverImage = r["CoverImage"] == DBNull.Value ? "" : (string)r["CoverImage"],
        WorkUrl = r["WorkUrl"] == DBNull.Value ? "" : (string)r["WorkUrl"],
        CreatedAt = (DateTime)r["CreatedAt"],
        UpdatedAt = r["UpdatedAt"] == DBNull.Value ? null : (DateTime)r["UpdatedAt"]
    };

    private static ReferenceAsset ReadReferenceAsset(SqlDataReader r) => new ReferenceAsset
    {
        AssetId = (int)r["AssetId"],
        UserId = (int)r["UserId"],
        FileName = (string)r["FileName"],
        LocalPath = r["LocalPath"] == DBNull.Value ? "" : (string)r["LocalPath"],
        FileType = (string)r["FileType"],
        Category = r["Category"] == DBNull.Value ? "" : (string)r["Category"],
        SubCategory = r["SubCategory"] == DBNull.Value ? "" : (string)r["SubCategory"],
        Tags = r["Tags"] == DBNull.Value ? "" : (string)r["Tags"],
        SourceKey = r["SourceKey"] == DBNull.Value ? null : (string)r["SourceKey"],
        FileSize = r["FileSize"] == DBNull.Value ? null : (long)r["FileSize"],
        CreatedAt = (DateTime)r["CreatedAt"]
    };
}
