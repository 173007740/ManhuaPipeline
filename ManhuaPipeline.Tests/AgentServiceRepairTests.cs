using System.Reflection;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class AgentServiceRepairTests
{
    private static Dictionary<string, int> CountShotsByUnit(string text)
    {
        var method = typeof(AgentService).GetMethod("CountShotsByUnit", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Dictionary<string, int>)method.Invoke(null, new object[] { text })!;
    }

    private static string EnsureShotDescriptions(string plan, List<StoryboardFrame> frames)
    {
        var method = typeof(AgentService).GetMethod("EnsureShotDescriptions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { plan, frames })!;
    }

    [Fact]
    public void CountShotsByUnit_DetectsMissingUnit()
    {
        var plan = """
        - **镜头编号**: 1.12-1
        - **镜头编号**: 1.12-2
        - **镜头编号**: 1.13-1
        """;
        var generated = """
        【第1集】【单元1.11】【镜头1.11-1】
        【第1集】【单元1.13】【镜头1.13-1】
        """;

        var expected = CountShotsByUnit(plan);
        var actual = CountShotsByUnit(generated);

        Assert.Equal(2, expected["1.12"]);
        Assert.Equal(0, actual.GetValueOrDefault("1.12"));
    }

    [Fact]
    public void CountShotsByUnit_CountsPromptHeaders()
    {
        var text = """
        【第1集】【单元1.5】【镜头1.5-1】
        【第1集】【单元1.5】【镜头1.5-2】
        """;

        var actual = CountShotsByUnit(text);

        Assert.Equal(2, actual["1.5"]);
    }

    [Fact]
    public void EnsureShotDescriptions_FillsMissingDesc()
    {
        var plan = """
        - **镜头编号**: 1.2-1
        - **节拍**: Beat01
        - **镜头时间轴**: 0-2s: 葬天台悬浮云海之上。
        - **构图方式**: ...
        """;
        var frames = new List<StoryboardFrame>
        {
            new StoryboardFrame { ShotNumber = "1.2-1", Description = "葬天台悬浮云海之上，七色法光冲天。" }
        };

        var result = EnsureShotDescriptions(plan, frames);

        Assert.Contains("- **镜头描述**: 葬天台悬浮云海之上，七色法光冲天。", result);
        Assert.Equal(1, result.Split("- **镜头描述**", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void EnsureShotDescriptions_KeepsExistingDesc()
    {
        var plan = """
        - **镜头编号**: 1.1-1
        - **镜头描述**: 原有描述
        - **镜头时间轴**: ...
        """;
        var frames = new List<StoryboardFrame>
        {
            new StoryboardFrame { ShotNumber = "1.1-1", Description = "不应重复" }
        };

        var result = EnsureShotDescriptions(plan, frames);

        Assert.Equal(1, result.Split("- **镜头描述**", StringSplitOptions.None).Length - 1);
        Assert.Contains("原有描述", result);
    }
}
