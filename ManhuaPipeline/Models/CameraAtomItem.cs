namespace ManhuaPipeline.Models;

public class CameraAtomItem
{
    public int AtomId { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";            // 原子名（如 缓推）
    public string Category { get; set; } = "";         // 分类：景别/运镜/转场/光影/节奏
    public string Description { get; set; } = "";      // 抽象技法描述，不含时代/场景物件
    public string Tags { get; set; } = "";             // 适用单元类型标签，逗号分隔（通用/打斗/文戏/悬疑/追逐/高潮）
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
