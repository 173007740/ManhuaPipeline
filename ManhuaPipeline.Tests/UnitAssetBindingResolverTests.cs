using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class UnitAssetBindingResolverTests
{
    private static readonly List<EnvironmentAsset> Envs = new()
    {
        new EnvironmentAsset { AssetId = 201, Name = "教室里", ImageUrl = "http://x/room.png" }
    };

    private static StageUnit Unit(string keyElements, string dialogue = "无", string coreAction = "") => new()
    {
        EpisodeNumber = 1,
        UnitNumber = "1.4",
        Type = "文戏/情感",
        Duration = 5,
        Location = "教室里",
        CoreAction = coreAction,
        KeyElements = keyElements,
        Dialogue = dialogue,
    };

    private static List<string> BoundNames(StageUnit unit, List<CharacterAsset> chars) =>
        UnitAssetBindingResolver.Resolve(1, unit, chars, Envs, new List<PropAsset>(), new List<EffectAsset>())
            .Where(b => b.Category == "Character")
            .Select(b => b.Name)
            .ToList();

    [Fact]
    public void KeyElementShortName_ReverseMatchesCanonicalAsset()
    {
        // 回归 44 项目：单元 1.4/1.6/1.9 关键元素写「洛伊娅」，资产规范名是「洛伊娅维娜」
        var chars = new List<CharacterAsset> { new() { AssetId = 103, Name = "洛伊娅维娜", ImageUrl = "http://x/ly.png" } };
        var names = BoundNames(Unit("洛伊娅（先专注学习）、司机"), chars);
        Assert.Contains("洛伊娅维娜", names);
    }

    [Fact]
    public void AssetCardAlias_BindsCanonicalAsset()
    {
        var chars = new List<CharacterAsset>
        {
            new() { AssetId = 104, Name = "洛伊娅维娜", Description = "角色别名：洛伊娅、薇娜\n角色描述：主角", ImageUrl = "http://x/ly.png" }
        };
        var names = BoundNames(Unit("日常学习", coreAction: "薇娜坐在窗边翻书"), chars);
        Assert.Contains("洛伊娅维娜", names);
    }

    [Fact]
    public void AmbiguousShortName_IsNotBound_AndReported()
    {
        var chars = new List<CharacterAsset>
        {
            new() { AssetId = 105, Name = "少年岳沉天", ImageUrl = "http://x/a.png" },
            new() { AssetId = 106, Name = "前世岳沉天", ImageUrl = "http://x/b.png" },
        };
        var issues = new List<string>();
        var names = UnitAssetBindingResolver
            .Resolve(1, Unit("岳沉天、阵纹"), chars, Envs, new List<PropAsset>(), new List<EffectAsset>(), out issues)
            .Where(b => b.Category == "Character").Select(b => b.Name).ToList();

        Assert.Empty(names);
        Assert.Contains(issues, i => i.Contains("可对应多个角色资产"));
    }

    [Fact]
    public void TemplatePlaceholderKeyElements_ReportsMissingCharacter()
    {
        // 关键元素照抄了输出格式占位词「人物/环境」，且台词里没有可匹配的角色名 → 报问题
        var chars = new List<CharacterAsset> { new() { AssetId = 103, Name = "洛伊娅维娜", ImageUrl = "http://x/ly.png" } };
        var issues = new List<string>();
        UnitAssetBindingResolver.Resolve(
            1,
            Unit("人物/环境", dialogue: "小生物叫了一声"),
            chars,
            Envs,
            new List<PropAsset>(),
            new List<EffectAsset>(),
            out issues);

        Assert.Contains(issues, i => i.Contains("没有命中任何角色资产"));
    }

    [Fact]
    public void PronounOnlyUnit_DoesNotBindCharacter()
    {
        // 单元 1.12 那种通篇只有「她」的写法，字符串匹配不可能恢复角色，必须留给人看到
        var chars = new List<CharacterAsset> { new() { AssetId = 103, Name = "洛伊娅维娜", ImageUrl = "http://x/ly.png" } };
        Assert.Empty(BoundNames(Unit("时间到", dialogue: "司机：时间到了。", coreAction: "她上台领奖"), chars));
    }
}
