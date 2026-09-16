namespace ManhuaPipeline.Models;

public class PipelineStage
{
    // 阶段编号是持久化 ID。特效资产后加为 11，不能重排历史编号。
    public const int ScriptAnalysis    = 1;  // 剧本解析（原创意构思）
    public const int StoryAnalysis     = 2;  // 故事分析
    public const int GlobalBlueprint   = 3;  // 全局蓝图
    public const int SceneSplit        = 4;  // 场景拆分（原分集细化）
    public const int ShotPlanning      = 5;  // 镜头规划（原分镜脚本）
    public const int CharacterAssets   = 6;  // 角色资产
    public const int PropAssets        = 7;  // 道具资产
    public const int EnvironmentAssets = 8;  // 环境资产
    public const int EffectAssets      = 11; // 特效资产
    public const int PromptGen         = 9;  // 提示词生成
    public const int CoherenceCheck    = 10; // 衔接检查

    // 逻辑顺序：剧本(1-3) → 资产(6/7/8/11) → 分集细化(4) → 分镜(5) → 提示词(9) → 衔接(10)
    // 分集细化阶段会引入资产目录，故资产类阶段必须先于 SceneSplit 执行。
    public static readonly int[] Order = {
        ScriptAnalysis, StoryAnalysis, GlobalBlueprint,
        CharacterAssets, PropAssets, EnvironmentAssets, EffectAssets,
        SceneSplit, ShotPlanning, PromptGen, CoherenceCheck
    };

    public static readonly string[] Names = {
        "", "创意构思", "故事分析", "全局蓝图", "分集细化",
        "分镜脚本", "角色资产", "道具资产", "环境资产",
        "提示词生成", "衔接检查", "特效资产"
    };

    public static readonly string[] Icons = {
        "", "💡", "📖", "🌍", "📑",
        "📋", "👤", "🔧", "🏞️",
        "🤖", "✅", "✨"
    };

    public static string Name(int stage) => stage >= 1 && stage <= 11 ? Names[stage] : "未知";
    public static string Icon(int stage) => stage >= 1 && stage <= 11 ? Icons[stage] : "❓";

    public static int? Next(int stage)
    {
        var index = Array.IndexOf(Order, stage);
        return index >= 0 && index + 1 < Order.Length ? Order[index + 1] : null;
    }
}
