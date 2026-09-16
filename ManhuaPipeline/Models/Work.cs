namespace ManhuaPipeline.Models;

public class Work
{
    public int WorkId { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string CoverImage { get; set; } = "";
    public string WorkUrl { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }
}

public class WorkDetail : Work
{
    public string AuthorName { get; set; } = "";
}
