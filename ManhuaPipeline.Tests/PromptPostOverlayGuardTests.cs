using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

/// <summary>
/// L5 后期叠加守卫：只给「含文字类画面元素」的镜头补留白约束，幂等、不改剧情、不破坏字段行契约。
/// </summary>
public class PromptPostOverlayGuardTests
{
    private static string ShotWithConstraint() => """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:文戏/情感
        @图1 [少年岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [光影质感]光影质感参考，保持冷月光一致
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，枯井遗府+夜晚
        [0-3s]中景固定，少年岳沉天低头看着手机屏幕，短信内容一闪而过。
        [3-7s]近景缓推，少年岳沉天瞳孔收缩。
        灯光：暖烛光自左下照亮侧脸。
        约束：机位平滑缓推，不出画。
        """;

    [Fact]
    public void Apply_OverlayCue_AppendsRuleToExistingConstraintLine()
    {
        var result = PromptPostOverlayGuard.Apply(ShotWithConstraint());

        Assert.Contains(PostOverlayAnalyzer.RuleText, result);
        Assert.Contains("约束：机位平滑缓推，不出画", result);
        // 其它行原样保留
        Assert.Contains("[3-7s]近景缓推，少年岳沉天瞳孔收缩。", result);
        Assert.Contains("灯光：暖烛光自左下照亮侧脸。", result);
    }

    [Fact]
    public void Apply_AlreadyGuarded_IsIdempotent()
    {
        var once = PromptPostOverlayGuard.Apply(ShotWithConstraint());
        var twice = PromptPostOverlayGuard.Apply(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, CountOccurrences(twice, PostOverlayAnalyzer.RuleText));
    }

    [Fact]
    public void Apply_WithoutConstraintLine_InsertsBeforeVideoStyleLine()
    {
        const string prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:文戏/情感
        @图1 [少年岳沉天]人物形象参考，保持外貌、发型、服装、气质一致
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，枯井遗府+夜晚
        [0-3s]中景固定，少年岳沉天捏着纸条，指节发白。
        灯光：暖烛光。
        视频风格：日系动漫，线条干净流畅，色彩明快清新。
        """;

        var result = PromptPostOverlayGuard.Apply(prompt);
        var lines = result.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();

        Assert.Contains(PostOverlayAnalyzer.RuleText, result);
        // 视频风格必须仍是最后一行（既有解析契约）
        Assert.StartsWith("视频风格", lines[^1]);
        // 新增的约束行插在视频风格之前
        Assert.Contains(lines, l => l.StartsWith("约束：", StringComparison.Ordinal));
        Assert.True(lines.FindIndex(l => l.StartsWith("约束：", StringComparison.Ordinal)) < lines.Count - 1);
    }

    [Fact]
    public void Apply_NoOverlayCue_LeavesPromptUntouched()
    {
        const string prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:打斗/动作
        @图1 [岳沉天]人物形象参考，保持外貌、发型、服装、气质一致
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+白天
        [0-3s]中景跟拍，岳沉天侧身出拳，气劲炸开。
        灯光：惨白天光。
        约束：禁止站桩。
        """;

        var result = PromptPostOverlayGuard.Apply(prompt);

        Assert.Equal(prompt, result);
        Assert.DoesNotContain(PostOverlayAnalyzer.RuleText, result);
    }

    [Fact]
    public void Apply_AssetRefNamedLikeFileProp_IsNotTreatedAsOverlayCue()
    {
        // @图N 行里的资产名（如道具卡「文件」）不是画面里的文字元素，不能误伤
        const string prompt = """
        【第1集】【单元1.3】【镜头1.3-1】
        类型:文戏/情感
        @图1 [岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [文件]道具参考，保持材质、颜色一致
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，书房+夜晚
        [0-3s]中景固定，岳沉天把东西放在桌面上。
        灯光：暖灯光。
        约束：不出画。
        """;

        var result = PromptPostOverlayGuard.Apply(prompt);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void Apply_MultipleShots_OnlyOverlayShotIsGuarded()
    {
        const string prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:文戏/情感
        @图1 [少年岳沉天]人物形象参考，保持外貌、发型、服装、气质一致
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，枯井遗府+夜晚
        [0-3s]中景固定，少年岳沉天盯着手机屏幕。
        灯光：暖烛光。
        约束：不出画。
        【第1集】【单元1.1】【镜头1.1-2】
        类型:打斗/动作
        @图1 [少年岳沉天]人物形象参考，保持外貌、发型、服装、气质一致
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+白天
        [0-3s]中景跟拍，少年岳沉天回身格挡。
        灯光：惨白天光。
        约束：禁止站桩。
        """;

        var result = PromptPostOverlayGuard.Apply(prompt);

        Assert.Equal(1, CountOccurrences(result, PostOverlayAnalyzer.RuleText));
        Assert.Contains("约束：禁止站桩。", result);
        Assert.True(result.IndexOf("约束：禁止站桩。", StringComparison.Ordinal)
                    > result.IndexOf(PostOverlayAnalyzer.RuleText, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_BlankText_ReturnsInput(string prompt)
    {
        Assert.Equal(prompt, PromptPostOverlayGuard.Apply(prompt));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
