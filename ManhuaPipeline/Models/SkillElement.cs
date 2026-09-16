namespace ManhuaPipeline.Models;

public class SkillElement
{
    public int ElementId { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public int UsageCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
