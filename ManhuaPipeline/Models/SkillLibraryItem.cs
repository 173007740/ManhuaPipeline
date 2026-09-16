namespace ManhuaPipeline.Models;

public class SkillLibraryItem
{
    public int SkillId { get; set; }
    public int UserId { get; set; }
    public int? ProjectId { get; set; }
    public string Name { get; set; } = "";          // 技能名（如 赤炎天坠）
    public string Element { get; set; } = "";       // 系别：火/冰/雷/剑阵/风/暗/圣
    public int Tier { get; set; } = 4;              // 五层战斗体系层级 1-5
    public string OwnerCharacter { get; set; } = ""; // 归属角色（空=通用/共享）
    public string PromptImage { get; set; } = "";   // 出图版提示词
    public string PromptVideo { get; set; } = "";   // 视频版提示词
    public string ImageUrl { get; set; } = "";      // 参考图

    /// <summary>供 LLM 阅读的技能描述：技能设定了参考图（ImageUrl 非空）时不再输出视频版提示词全文，
    /// 以简短说明替代，避免文字与参考图冲突误导模型；无参考图时原样返回视频版提示词。</summary>
    public string PromptVideoForLLM
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImageUrl))
                return "形态、色调、氛围以参考图为准";
            return PromptVideo;
        }
    }
    public string Tags { get; set; } = "";          // 标签，逗号分隔
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
