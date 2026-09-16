using Microsoft.Data.SqlClient;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services;

/// <summary>
/// L2 连续性层落库（六类连续性表统一存放在 ProjectContinuityTables，用 TableType 区分）。
/// 表结构见 Database/Upgrade_连续性表.sql。
/// </summary>
public partial class DbService
{
    /// <summary>查询某项目全部连续性表（按集号、类型排序）。</summary>
    public List<ProjectContinuityTable> GetContinuityTables(int projectId)
    {
        var result = new List<ProjectContinuityTable>();
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM ProjectContinuityTables WHERE ProjectId=@pid ORDER BY EpisodeNumber, TableType", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadContinuityTable(r));
        return result;
    }

    /// <summary>查询某项目指定类型的连续性表。episodeNumber=0 表示全片级。</summary>
    public ProjectContinuityTable? GetContinuityTable(int projectId, string tableType, int episodeNumber = 0)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("SELECT * FROM ProjectContinuityTables WHERE ProjectId=@pid AND EpisodeNumber=@ep AND TableType=@t", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.Parameters.AddWithValue("@ep", episodeNumber);
        cmd.Parameters.AddWithValue("@t", tableType);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadContinuityTable(r) : null;
    }

    /// <summary>
    /// 整表替换某项目的连续性表（重跑抽取时整体覆盖，与单元/帧绑定策略一致）。
    /// 返回实际写入的行数。
    /// </summary>
    public int ReplaceContinuityTables(int projectId, List<ProjectContinuityTable> rows)
    {
        using var conn = GetConn();
        conn.Open();
        using var txn = conn.BeginTransaction();
        try
        {
            using (var del = new SqlCommand("DELETE FROM ProjectContinuityTables WHERE ProjectId=@pid", conn, txn))
            {
                del.Parameters.AddWithValue("@pid", projectId);
                del.ExecuteNonQuery();
            }

            foreach (var row in rows)
            {
                using var ins = new SqlCommand(
                    "INSERT INTO ProjectContinuityTables(ProjectId,EpisodeNumber,TableType,ContentJson,ContentText,Source,CreatedAt,UpdatedAt) " +
                    "VALUES(@pid,@ep,@t,@json,@text,@src,SYSDATETIME(),SYSDATETIME())", conn, txn);
                ins.Parameters.AddWithValue("@pid", projectId);
                ins.Parameters.AddWithValue("@ep", row.EpisodeNumber);
                ins.Parameters.AddWithValue("@t", row.TableType);
                ins.Parameters.AddWithValue("@json", string.IsNullOrWhiteSpace(row.ContentJson) ? "{}" : row.ContentJson);
                ins.Parameters.AddWithValue("@text", (object?)row.ContentText ?? DBNull.Value);
                ins.Parameters.AddWithValue("@src", string.IsNullOrWhiteSpace(row.Source) ? "llm" : row.Source);
                ins.ExecuteNonQuery();
            }

            txn.Commit();
            return rows.Count;
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    /// <summary>清空某项目的连续性表（重新抽取前使用）。</summary>
    public void ClearContinuityTables(int projectId)
    {
        using var conn = GetConn();
        conn.Open();
        using var cmd = new SqlCommand("DELETE FROM ProjectContinuityTables WHERE ProjectId=@pid", conn);
        cmd.Parameters.AddWithValue("@pid", projectId);
        cmd.ExecuteNonQuery();
    }

    private static ProjectContinuityTable ReadContinuityTable(SqlDataReader r) => new ProjectContinuityTable
    {
        ContinuityId = (int)r["ContinuityId"],
        ProjectId = (int)r["ProjectId"],
        EpisodeNumber = (int)r["EpisodeNumber"],
        TableType = (string)r["TableType"],
        ContentJson = r["ContentJson"] == DBNull.Value ? "{}" : (string)r["ContentJson"],
        ContentText = r["ContentText"] as string,
        Source = r["Source"] == DBNull.Value ? "llm" : (string)r["Source"],
        CreatedAt = (DateTime)r["CreatedAt"],
        UpdatedAt = (DateTime)r["UpdatedAt"]
    };
}
