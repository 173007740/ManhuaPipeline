using ManhuaPipeline.Models;
using Microsoft.Data.SqlClient;

namespace ManhuaPipeline.Services;

/// <summary>
/// 无限画布的数据访问（DbService 的分部实现，与 DbService.Continuity / DbService.Keyframes 同一套路）。
///
/// 只做「表和对象」之间的搬运，业务判断（能不能连这条线、要不要真出图）不在这里，
/// 分别放在 CanvasController 的校验和 CanvasNodeCatalog 里。
/// </summary>
public partial class DbService
{
    // ==================== 画布 ====================

    public List<CanvasBoard> GetCanvasBoards(int userId, int? projectId = null)
    {
        var list = new List<CanvasBoard>();
        using var conn = GetConn(); conn.Open();
        var sql = projectId.HasValue
            ? "SELECT * FROM CanvasBoards WHERE UserId=@uid AND ProjectId=@pid ORDER BY UpdatedAt DESC"
            : "SELECT * FROM CanvasBoards WHERE UserId=@uid ORDER BY UpdatedAt DESC";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        if (projectId.HasValue) cmd.Parameters.AddWithValue("@pid", projectId.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(CanvasReadBoard(r));
        return list;
    }

    public CanvasBoard? GetCanvasBoard(int boardId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM CanvasBoards WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", boardId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? CanvasReadBoard(r) : null;
    }

    public int CreateCanvasBoard(int userId, int? projectId, string title)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "INSERT INTO CanvasBoards(UserId,ProjectId,Title) OUTPUT INSERTED.Id VALUES(@uid,@pid,@t)", conn);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@t", title);
        return (int)cmd.ExecuteScalar();
    }

    public bool UpdateCanvasBoardViewport(int boardId, float x, float y, float scale)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasBoards SET ViewportX=@x, ViewportY=@y, ViewportScale=@s, UpdatedAt=SYSDATETIME() WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", boardId);
        cmd.Parameters.AddWithValue("@x", x);
        cmd.Parameters.AddWithValue("@y", y);
        cmd.Parameters.AddWithValue("@s", scale);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool UpdateCanvasBoardTitle(int boardId, string title)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasBoards SET Title=@t, UpdatedAt=SYSDATETIME() WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", boardId);
        cmd.Parameters.AddWithValue("@t", title);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 改画布挂的项目（可以给已有画布补挂 / 解除）。
    /// 画风是从项目上取的，所以这个开关直接决定「跟随项目画风」能不能拿到东西。
    /// </summary>
    public bool UpdateCanvasBoardProject(int boardId, int? projectId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasBoards SET ProjectId=@pid, UpdatedAt=SYSDATETIME() WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", boardId);
        cmd.Parameters.AddWithValue("@pid", (object?)projectId ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>删画布：连带删掉它的节点、连线与出图任务，不然会留下孤儿数据。</summary>
    public bool DeleteCanvasBoard(int boardId)
    {
        using var conn = GetConn(); conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            void Exec(string sql)
            {
                using var c = new SqlCommand(sql, conn, txn);
                c.Parameters.AddWithValue("@bid", boardId);
                c.ExecuteNonQuery();
            }
            Exec("DELETE FROM CanvasTasks WHERE BoardId=@bid");
            Exec("DELETE FROM CanvasEdges WHERE BoardId=@bid");
            Exec("DELETE FROM CanvasNodes WHERE BoardId=@bid");

            using var del = new SqlCommand("DELETE FROM CanvasBoards WHERE Id=@bid", conn, txn);
            del.Parameters.AddWithValue("@bid", boardId);
            var ok = del.ExecuteNonQuery() > 0;

            txn.Commit();
            return ok;
        }
        catch { txn.Rollback(); throw; }
    }

    // ==================== 节点 ====================

    public List<CanvasNode> GetCanvasNodes(int boardId)
    {
        var list = new List<CanvasNode>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM CanvasNodes WHERE BoardId=@bid ORDER BY Id", conn);
        cmd.Parameters.AddWithValue("@bid", boardId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(CanvasReadNode(r));
        return list;
    }

    public CanvasNode? GetCanvasNode(int nodeId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM CanvasNodes WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", nodeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? CanvasReadNode(r) : null;
    }

    public int CreateCanvasNode(CanvasNode node)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"INSERT INTO CanvasNodes(BoardId,NodeType,X,Y,Title,Prompt,Size,Status,ImageUrl,AssetId,ExtraJson)
              OUTPUT INSERTED.Id
              VALUES(@bid,@type,@x,@y,@title,@prompt,@size,@status,@img,@asset,@extra)", conn);
        cmd.Parameters.AddWithValue("@bid", node.BoardId);
        cmd.Parameters.AddWithValue("@type", node.NodeType);
        cmd.Parameters.AddWithValue("@x", node.X);
        cmd.Parameters.AddWithValue("@y", node.Y);
        cmd.Parameters.AddWithValue("@title", (object?)node.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prompt", (object?)node.Prompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size", (object?)node.Size ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", node.Status);
        cmd.Parameters.AddWithValue("@img", (object?)node.ImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@asset", (object?)node.AssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@extra", (object?)node.ExtraJson ?? DBNull.Value);
        return (int)cmd.ExecuteScalar();
    }

    /// <summary>拖拽结束才调：只改坐标，不动内容，避免提示词被旧值覆盖。</summary>
    public bool UpdateCanvasNodePosition(int nodeId, float x, float y)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasNodes SET X=@x, Y=@y, UpdatedAt=SYSDATETIME() WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.Parameters.AddWithValue("@x", x);
        cmd.Parameters.AddWithValue("@y", y);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>改提示词 / 画幅 / 标题 / 扩展参数。传 null 的字段保持原值不动。</summary>
    public bool UpdateCanvasNodeContent(
        int nodeId, string? title, string? prompt, string? size, string? extraJson)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"UPDATE CanvasNodes SET
                Title    = CASE WHEN @title IS NULL THEN Title ELSE @title END,
                Prompt   = CASE WHEN @prompt IS NULL THEN Prompt ELSE @prompt END,
                Size     = CASE WHEN @size IS NULL THEN Size ELSE @size END,
                ExtraJson= CASE WHEN @extra IS NULL THEN ExtraJson ELSE @extra END,
                UpdatedAt= SYSDATETIME()
              WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prompt", (object?)prompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size", (object?)size ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@extra", (object?)extraJson ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>回填出图结果。imageUrl / error 传 null 表示不清空（只改状态时用）。</summary>
    public bool SetCanvasNodeStatus(int nodeId, string status, string? imageUrl, string? error)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"UPDATE CanvasNodes SET
                Status   = @status,
                ImageUrl = CASE WHEN @img IS NULL THEN ImageUrl ELSE @img END,
                ErrorMsg = CASE WHEN @err IS NULL THEN ErrorMsg ELSE @err END,
                UpdatedAt= SYSDATETIME()
              WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@img", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@err", (object?)error ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>删节点：先断掉和它相连的线，再删它自己的出图任务，最后删节点。</summary>
    public bool DeleteCanvasNode(int nodeId)
    {
        using var conn = GetConn(); conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using (var e1 = new SqlCommand(
                "DELETE FROM CanvasEdges WHERE FromNodeId=@id OR ToNodeId=@id", conn, txn))
            {
                e1.Parameters.AddWithValue("@id", nodeId);
                e1.ExecuteNonQuery();
            }
            using (var e2 = new SqlCommand("DELETE FROM CanvasTasks WHERE NodeId=@id", conn, txn))
            {
                e2.Parameters.AddWithValue("@id", nodeId);
                e2.ExecuteNonQuery();
            }
            using var del = new SqlCommand("DELETE FROM CanvasNodes WHERE Id=@id", conn, txn);
            del.Parameters.AddWithValue("@id", nodeId);
            var ok = del.ExecuteNonQuery() > 0;

            txn.Commit();
            return ok;
        }
        catch { txn.Rollback(); throw; }
    }

    // ==================== 连线 ====================

    public List<CanvasEdge> GetCanvasEdges(int boardId)
    {
        var list = new List<CanvasEdge>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM CanvasEdges WHERE BoardId=@bid ORDER BY Id", conn);
        cmd.Parameters.AddWithValue("@bid", boardId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(CanvasReadEdge(r));
        return list;
    }

    public int CreateCanvasEdge(int boardId, int fromNodeId, string fromPort, int toNodeId, string toPort)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"INSERT INTO CanvasEdges(BoardId,FromNodeId,FromPort,ToNodeId,ToPort)
              OUTPUT INSERTED.Id VALUES(@bid,@fn,@fp,@tn,@tp)", conn);
        cmd.Parameters.AddWithValue("@bid", boardId);
        cmd.Parameters.AddWithValue("@fn", fromNodeId);
        cmd.Parameters.AddWithValue("@fp", fromPort);
        cmd.Parameters.AddWithValue("@tn", toNodeId);
        cmd.Parameters.AddWithValue("@tp", toPort);
        return (int)cmd.ExecuteScalar();
    }

    public CanvasEdge? GetCanvasEdge(int edgeId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM CanvasEdges WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", edgeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? CanvasReadEdge(r) : null;
    }

    public bool DeleteCanvasEdge(int edgeId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand("DELETE FROM CanvasEdges WHERE Id=@id", conn);
        cmd.Parameters.AddWithValue("@id", edgeId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>同两个端口之间已经有线就不重复建（前端拖重复时会看到提示，这里是最后一道闸）。</summary>
    public bool CanvasEdgeExists(int fromNodeId, string fromPort, int toNodeId, string toPort)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "SELECT COUNT(1) FROM CanvasEdges WHERE FromNodeId=@fn AND FromPort=@fp AND ToNodeId=@tn AND ToPort=@tp", conn);
        cmd.Parameters.AddWithValue("@fn", fromNodeId);
        cmd.Parameters.AddWithValue("@fp", fromPort);
        cmd.Parameters.AddWithValue("@tn", toNodeId);
        cmd.Parameters.AddWithValue("@tp", toPort);
        return (int)cmd.ExecuteScalar() > 0;
    }

    // ==================== 出图任务队列 ====================

    public int EnqueueCanvasTask(int boardId, int nodeId, int userId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"INSERT INTO CanvasTasks(BoardId,NodeId,UserId,Status) OUTPUT INSERTED.TaskId
              VALUES(@bid,@nid,@uid,'queued')", conn);
        cmd.Parameters.AddWithValue("@bid", boardId);
        cmd.Parameters.AddWithValue("@nid", nodeId);
        cmd.Parameters.AddWithValue("@uid", userId);
        return (int)cmd.ExecuteScalar();
    }

    public List<CanvasTask> GetQueuedCanvasTasks(int max)
    {
        var list = new List<CanvasTask>();
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "SELECT TOP (@n) * FROM CanvasTasks WHERE Status='queued' ORDER BY TaskId", conn);
        cmd.Parameters.AddWithValue("@n", max);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(CanvasReadTask(r));
        return list;
    }

    /// <summary>
    /// 抢占任务：乐观更新，只有 queued 状态能改成 running。
    /// 多个 worker 同时取任务时靠这条保证同一任务只会被处理一次。
    /// </summary>
    public bool TryMarkCanvasTaskRunning(int taskId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            @"UPDATE CanvasTasks SET Status='running', StartedAt=SYSDATETIME()
              WHERE TaskId=@id AND Status='queued'", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool CompleteCanvasTask(int taskId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasTasks SET Status='completed', FinishedAt=SYSDATETIME() WHERE TaskId=@id", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool FailCanvasTask(int taskId, string? error)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasTasks SET Status='failed', ErrorMsg=@e, FinishedAt=SYSDATETIME() WHERE TaskId=@id", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        cmd.Parameters.AddWithValue("@e", (object?)error ?? DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>停机时把 running 放回 queued，下次启动接着跑（不能用 Fail，那样任务就死了）。</summary>
    public bool ReleaseCanvasTask(int taskId)
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasTasks SET Status='queued', StartedAt=NULL WHERE TaskId=@id AND Status='running'", conn);
        cmd.Parameters.AddWithValue("@id", taskId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>进程上次退出时卡在 running 的任务，启动时回收。</summary>
    public int ResetStuckCanvasTasks()
    {
        using var conn = GetConn(); conn.Open();
        using var cmd = new SqlCommand(
            "UPDATE CanvasTasks SET Status='queued', StartedAt=NULL WHERE Status='running'", conn);
        return cmd.ExecuteNonQuery();
    }

    // ==================== 读取映射 ====================

    private static CanvasBoard CanvasReadBoard(SqlDataReader r) => new()
    {
        Id = (int)r["Id"],
        UserId = (int)r["UserId"],
        ProjectId = r["ProjectId"] == DBNull.Value ? null : (int)r["ProjectId"],
        Title = (string)r["Title"],
        ViewportX = CanvasFlt(r["ViewportX"]),
        ViewportY = CanvasFlt(r["ViewportY"]),
        ViewportScale = CanvasFlt(r["ViewportScale"]),
        CreatedAt = (DateTime)r["CreatedAt"],
        UpdatedAt = (DateTime)r["UpdatedAt"]
    };

    private static CanvasNode CanvasReadNode(SqlDataReader r) => new()
    {
        Id = (int)r["Id"],
        BoardId = (int)r["BoardId"],
        NodeType = (string)r["NodeType"],
        X = CanvasFlt(r["X"]),
        Y = CanvasFlt(r["Y"]),
        Title = CanvasStr(r["Title"]),
        Prompt = CanvasStr(r["Prompt"]),
        Size = CanvasStr(r["Size"]),
        Status = (string)r["Status"],
        ImageUrl = CanvasStr(r["ImageUrl"]),
        AssetId = r["AssetId"] == DBNull.Value ? null : (int)r["AssetId"],
        ErrorMsg = CanvasStr(r["ErrorMsg"]),
        ExtraJson = CanvasStr(r["ExtraJson"]),
        CreatedAt = (DateTime)r["CreatedAt"],
        UpdatedAt = (DateTime)r["UpdatedAt"]
    };

    private static CanvasEdge CanvasReadEdge(SqlDataReader r) => new()
    {
        Id = (int)r["Id"],
        BoardId = (int)r["BoardId"],
        FromNodeId = (int)r["FromNodeId"],
        FromPort = (string)r["FromPort"],
        ToNodeId = (int)r["ToNodeId"],
        ToPort = (string)r["ToPort"],
        CreatedAt = (DateTime)r["CreatedAt"]
    };

    private static CanvasTask CanvasReadTask(SqlDataReader r) => new()
    {
        TaskId = (int)r["TaskId"],
        BoardId = (int)r["BoardId"],
        NodeId = (int)r["NodeId"],
        UserId = (int)r["UserId"],
        Status = (string)r["Status"],
        ErrorMsg = CanvasStr(r["ErrorMsg"]),
        CreatedAt = (DateTime)r["CreatedAt"],
        StartedAt = r["StartedAt"] == DBNull.Value ? null : (DateTime)r["StartedAt"],
        FinishedAt = r["FinishedAt"] == DBNull.Value ? null : (DateTime)r["FinishedAt"]
    };

    // 这几个 helper 加了 Canvas 前缀，避免和 DbService 主文件里的同名私有方法撞车
    private static string? CanvasStr(object v) => v == DBNull.Value ? null : (string)v;
    private static float CanvasFlt(object v) => v == DBNull.Value ? 0f : Convert.ToSingle(v);
}
