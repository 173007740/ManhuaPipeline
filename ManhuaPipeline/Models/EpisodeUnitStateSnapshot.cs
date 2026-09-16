namespace ManhuaPipeline.Models;

/// <summary>
/// Director V4 第二批：每个 Unit 完成后的落镜状态快照。
/// 下一个 Unit 必须以该快照为起点，禁止重新建立上一 Unit 已建立的战斗/情绪/资产状态。
/// </summary>
public class EpisodeUnitStateSnapshot
{
    public int EpisodeUnitStateSnapshotId { get; set; }
    public int ProjectId { get; set; }
    public int EpisodeNumber { get; set; }
    public string UnitNumber { get; set; } = "";

    public string StateJson { get; set; } = "{}";
    public string Source { get; set; } = "storyboard";

    public UnitEndState State { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
