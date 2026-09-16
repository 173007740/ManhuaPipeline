using ManhuaPipeline.Services;
using System.Reflection;
using ManhuaPipeline.Models;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptSkillGuardTests
{
    [Fact]
    public void UnlockedSkillRefs_AreRemovedAndRenumbered()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[七宗宗主]形象参考，保持法光笼罩、袍服、威严气质一致；第三张(@图3)为[七曜诛圣阵]特效参考，保持形态、色调、氛围一致；第四张(@图4)为[赤金气血]特效参考，保持形态、色调、氛围一致；倒数第二张(@图5)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图6)为画面风格参考（光影、色调、材质质感）
        0-5秒[1]
        时长:5秒
        """;

        var result = PromptSkillGuard.FilterUnlockedSkills(
            prompt,
            new[] { "七曜诛圣阵", "赤金气血" },
            new[] { "七曜诛圣阵" });

        Assert.DoesNotContain("[赤金气血]特效参考", result);
        Assert.Contains("[七曜诛圣阵]特效参考", result);
        Assert.Contains("倒数第二张(@图4)为[葬天台]场景参考", result);
        Assert.Contains("最后一张(@图5)为画面风格参考", result);

        var numbers = System.Text.RegularExpressions.Regex.Matches(result, @"@图(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        Assert.Equal(numbers.Count, numbers.Count == 0 ? 0 : numbers[^1]);
        for (var i = 1; i < numbers.Count; i++)
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
    }

    [Fact]
    public void NonSkillEffects_AreKept()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        参考图:第一张(@图1)为[火焰风暴]特效参考，保持形态、色调、氛围一致；倒数第二张(@图2)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        0-5秒[1]
        时长:5秒
        """;

        var result = PromptSkillGuard.FilterUnlockedSkills(prompt, new[] { "七曜诛圣阵" }, Array.Empty<string>());

        Assert.Contains("[火焰风暴]特效参考", result);
        Assert.Contains("倒数第二张(@图2)", result);
        Assert.Contains("最后一张(@图3)", result);
    }

    [Fact]
    public void PromptWithoutRefLine_IsUnchanged()
    {
        var prompt = "【第1集】【单元1.1】【镜头1.1-1】\n0-5秒[1]\n时长:5秒";

        var result = PromptSkillGuard.FilterUnlockedSkills(prompt, new[] { "赤金气血" }, Array.Empty<string>());

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void SkillRefImages_DoNotRestoreUnlockedSkillsAfterFilter()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        参考图:第一张(@图1)为[前世岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[七曜诛圣阵]特效参考，保持形态、色调、氛围一致；第三张(@图3)为[赤金气血]特效参考，保持形态、色调、氛围一致；倒数第二张(@图4)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图5)为画面风格参考（光影、色调、材质质感）
        0-5秒[1]
        时长:5秒
        """;
        var filtered = PromptSkillGuard.FilterUnlockedSkills(
            prompt,
            new[] { "七曜诛圣阵", "赤金气血" },
            new[] { "七曜诛圣阵" });

        var skills = new List<SkillLibraryItem>
        {
            new SkillLibraryItem { Name = "七曜诛圣阵" },
            new SkillLibraryItem { Name = "赤金气血" }
        };
        var locked = new List<SkillLibraryItem> { new SkillLibraryItem { Name = "七曜诛圣阵" } };
        var method = typeof(AgentService).GetMethod("EnsureSkillRefImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = (string)method.Invoke(null, new object?[] { filtered, skills, locked, new List<string>(), null })!;

        Assert.DoesNotContain("[赤金气血]特效参考", result);
        Assert.Contains("[七曜诛圣阵]特效参考", result);
    }

    [Fact]
    public void AtImageLine_UnlockedSkill_IsRemovedAndRenumbered()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        @图1 前世岳沉天人物；@图2 前世岳沉天战斗态；@图3 七曜诛圣阵特效；@图4 赤金气血特效；@图5 葬天台场景；@图6 光影质感。
        [0-3s]大远景固定机位，七色法光交织封天。
        """;

        var result = PromptSkillGuard.FilterUnlockedSkills(
            prompt,
            new[] { "七曜诛圣阵", "赤金气血" },
            new[] { "七曜诛圣阵" });

        Assert.Contains("[七曜诛圣阵]特效参考", result);
        Assert.DoesNotContain("[赤金气血]特效参考", result);
        Assert.Contains("@图1 [前世岳沉天]人物形象参考", result);
        Assert.Contains("@图4 [葬天台]场景参考", result);
        Assert.Contains("@图5 [光影质感]光影质感参考", result);
    }
}
