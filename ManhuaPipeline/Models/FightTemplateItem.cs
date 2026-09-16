namespace ManhuaPipeline.Models;

public class FightTemplateItem
{
    public int FightTemplateId { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";            // 模板名（如 近身对砍 · 正反打）
    public int Tier { get; set; } = 3;                // 五层战斗体系层级 1-5
    public int Duration { get; set; } = 11;           // 时长档 5/11/15
    public string Scene { get; set; } = "";           // 适用场景
    public string Beat { get; set; } = "";            // 节拍（快-快-顿-重）
    public string ActionPrompt { get; set; } = "";    // 动作行
    public string CameraPrompt { get; set; } = "";    // 运镜行
    public string ConstraintPrompt { get; set; } = "";// 约束行
    public string Tags { get; set; } = "";            // 标签，逗号分隔
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
