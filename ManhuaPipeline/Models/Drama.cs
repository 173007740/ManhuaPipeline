namespace ManhuaPipeline.Models;

public class Drama
{
    public int DramaId { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverImage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
