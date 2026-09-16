using ManhuaPipeline.Services;

namespace ManhuaPipeline.Models;

public class ReferenceAsset
{
    public int AssetId { get; set; }
    public int UserId { get; set; }
    public string FileName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string FileType { get; set; } = "image";
    public string Category { get; set; } = "";     // 动漫 / 写实
    public string SubCategory { get; set; } = "";   // 角色 / 道具 / 环境
    public string Tags { get; set; } = "";             // 标签，逗号分隔
    /// <summary>自动出图的来源标记（如 asset:12:characters:5）；手动上传为空。用来在重新出图时精确替换上一张。</summary>
    public string? SourceKey { get; set; }
    public long? FileSize { get; set; }          // 文件大小（字节）
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>图片在服务器磁盘上的绝对路径（wwwroot 下的真实文件位置），由 LocalPath 推导，只读、供前端定位文件。</summary>
    public string DiskPath => AppPaths.ResolveUpload(LocalPath) ?? "";
}
