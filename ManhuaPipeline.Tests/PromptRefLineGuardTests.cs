using System.Linq;
using System.Text.RegularExpressions;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptRefLineGuardTests
{
    [Fact]
    public void UnknownAssetCard_IsRemovedAndRenumbered()
    {
        var prompt = """
        【第1集】【单元1.14】【镜头1.14-1】
        类型:文戏/情感
        @图1 [少年岳沉天]人物形象，保持外貌、发型、服装、气质一致；@图2 [旧灯]人物形象参考，保持外貌、发型、服装、气质一致；@图3 [七宗血契]道具参考，保持外观、材质、大小一致；@图4 [枯井遗府]场景参考，保持空间布局、色调一致；@图5 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致
        [0-2s]少年岳沉天缓缓起身，双手合拢七宗血契。
        """;

        var valid = new[] { "少年岳沉天", "七宗血契", "枯井遗府", "光影质感" };
        var result = PromptRefLineGuard.Apply(prompt, valid);

        Assert.DoesNotContain("旧灯", result);
        Assert.Contains("@图1 [少年岳沉天]人物形象参考，保持外貌、发型、服装、气质一致", result);
        Assert.Contains("@图2 [七宗血契]道具参考，保持外观、材质、大小一致", result);
        Assert.Contains("@图3 [枯井遗府]场景参考，保持空间布局、色调一致", result);
        Assert.Contains("@图4 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致", result);

        var numbers = Regex.Matches(result, @"@图(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        Assert.Equal(4, numbers.Count);
        for (var i = 1; i < numbers.Count; i++)
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
    }

    [Fact]
    public void ValidSkillAndGroupCards_AreKept()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 [七宗宗主]群像参考，保持整体造型一致；@图2 [圣光诛邪]特效参考，保持形态、色调、氛围一致；@图3 [葬天台]场景参考，保持空间布局、色调一致；@图4 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致
        [0-3s]七宗宗主结印催动圣光诛邪。
        """;

        var valid = new[] { "七宗宗主", "圣光诛邪", "葬天台", "光影质感" };
        var result = PromptRefLineGuard.Apply(prompt, valid);

        Assert.Contains("@图1 [七宗宗主]群像参考", result);
        Assert.Contains("@图2 [圣光诛邪]特效参考", result);
        Assert.Contains("@图3 [葬天台]场景参考", result);
        Assert.Contains("@图4 [光影质感]光影质感参考", result);
    }
}
