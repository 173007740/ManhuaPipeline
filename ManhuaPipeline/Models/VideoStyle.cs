namespace ManhuaPipeline.Models;

public class VideoStyle
{
    public int StyleId { get; set; }
    public string StyleName { get; set; } = "";
    public string StylePrompt { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
