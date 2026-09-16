using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class FrameAssetBindingResolverTests
{
    private static readonly List<CharacterAsset> Characters = new()
    {
        new CharacterAsset { AssetId = 101, Name = "遐蝶", ImageUrl = "http://x/hd.png" },
        new CharacterAsset { AssetId = 102, Name = "玻吕茜亚", ImageUrl = "http://x/bl.png" },
    };

    private static readonly List<EnvironmentAsset> Envs = new()
    {
        new EnvironmentAsset { AssetId = 201, Name = "晨光小屋的厨房", ImageUrl = "http://x/cf.png" },
    };

    private static readonly List<PropAsset> Props = new();
    private static readonly List<EffectAsset> Effects = new();

    private static StoryboardFrame Frame(string characters) => new()
    {
        FrameId = 1,
        ProjectId = 42,
        UnitNumber = "1.3",
        ShotNumber = "1.3-1",
        Characters = characters,
        Scene = "晨光小屋的厨房",
    };

    private static List<string> BoundNames(StoryboardFrame f) =>
        FrameAssetBindingResolver.Resolve(f, Characters, Envs, Props, Effects)
            .Bindings
            .Where(b => b.Category == "Character")
            .Select(b => b.Name)
            .ToList();

    [Fact]
    public void EmDashEmotionSuffix_StillBindsCharacter()
    {
        // 回归：出镜角色写「遐蝶——表情描述」时破折号后不能把整串当 token，遐蝶必须绑上
        var names = BoundNames(Frame("遐蝶——语气平稳但尾音微颤，眼神故作镇定却藏不住紧张，轻咳时侧脸，眼睑微垂，闪过慌乱"));
        Assert.Contains("遐蝶", names);
        Assert.DoesNotContain("玻吕茜亚", names);
    }

    [Fact]
    public void ParentheticalMultiCharacter_BindsBoth()
    {
        var names = BoundNames(Frame("遐蝶（端锅、转身，眼神专注）；玻吕茜亚（肩边微笑）"));
        Assert.Contains("遐蝶", names);
        Assert.Contains("玻吕茜亚", names);
    }

    [Fact]
    public void CommaSeparatedEmotionFree_Binds()
    {
        var names = BoundNames(Frame("遐蝶，出场时眉心微蹙，目光专注"));
        Assert.Contains("遐蝶", names);
    }

    [Fact]
    public void ArrowTransitionInsideSuffix_StillBinds()
    {
        var names = BoundNames(Frame("遐蝶——呆滞（目光空放）→ 无奈（抬手欲擦又收）→ 自嘲轻笑（偏头），鼻尖沾一点白粉；玻吕茜亚——远处停驻"));
        Assert.Contains("遐蝶", names);
        Assert.Contains("玻吕茜亚", names);
    }

    [Fact]
    public void NoCharacterPlaceholder_BindsNothing()
    {
        var names = BoundNames(Frame("无"));
        Assert.Empty(names);
    }

    [Fact]
    public void CharactersNotMentioning_DoesNotBind()
    {
        var names = BoundNames(Frame("路人甲——远景走动，虚化"));
        Assert.Empty(names);
    }

    // ===== 回归 44 项目：出镜角色写简称/别名，资产规范名没被丢 =====

    [Fact]
    public void ShortName_ReverseMatchesCanonicalAsset()
    {
        // 出镜角色写「洛伊娅」，资产规范名是「洛伊娅维娜」：名单片段反向兜底必须绑上
        var chars = new List<CharacterAsset> { new() { AssetId = 103, Name = "洛伊娅维娜", ImageUrl = "http://x/ly.png" } };
        var names = FrameAssetBindingResolver
            .Resolve(Frame("洛伊娅（先专注学习）｜小生物（生物点缀，非对弈主体）"), chars, Envs, Props, Effects)
            .Bindings.Where(b => b.Category == "Character").Select(b => b.Name).ToList();
        Assert.Contains("洛伊娅维娜", names);
    }

    [Fact]
    public void AssetCardAlias_BindsCanonicalAsset()
    {
        // 「薇娜」不是规范名「洛伊娅维娜」的子串，只能靠资产卡的「角色别名」命中
        var chars = new List<CharacterAsset>
        {
            new() { AssetId = 104, Name = "洛伊娅维娜", Description = "角色别名：洛伊娅、薇娜\n角色描述：主角", ImageUrl = "http://x/ly.png" }
        };
        var names = FrameAssetBindingResolver
            .Resolve(Frame("薇娜——立在窗前，目光沉静"), chars, Envs, Props, Effects)
            .Bindings.Where(b => b.Category == "Character").Select(b => b.Name).ToList();
        Assert.Contains("洛伊娅维娜", names);
    }

    [Fact]
    public void AmbiguousShortName_IsNotBound_AndReported()
    {
        // 「岳沉天」同时是「少年岳沉天」「前世岳沉天」的前缀：宁可不绑，也不能瞎绑
        var chars = new List<CharacterAsset>
        {
            new() { AssetId = 105, Name = "少年岳沉天", ImageUrl = "http://x/a.png" },
            new() { AssetId = 106, Name = "前世岳沉天", ImageUrl = "http://x/b.png" },
        };
        var r = FrameAssetBindingResolver.Resolve(Frame("岳沉天——独立阵心"), chars, Envs, Props, Effects);
        Assert.DoesNotContain(r.Bindings, b => b.Category == "Character");
        Assert.Contains(r.Issues, i => i.Contains("可对应多个角色资产"));
    }

    // ===== 单元绑定继承：单元引用的资产，该单元拆出的每个镜头都要引用 =====

    [Fact]
    public void UnitBindings_InheritedByEveryShot()
    {
        // 分集细化单元 1.3 绑了 3 个资产（遐蝶、玻吕茜亚、晨光小屋的厨房）；
        // 本镜「出镜角色」只写了遐蝶，但另外两个也必须继承进本镜绑定。
        var unitBindings = new List<UnitAssetBinding>
        {
            new() { UnitNumber = "1.3", Category = "Character", AssetId = 101, Name = "遐蝶", HasImage = true, SortOrder = 0 },
            new() { UnitNumber = "1.3", Category = "Character", AssetId = 102, Name = "玻吕茜亚", HasImage = true, SortOrder = 1 },
            new() { UnitNumber = "1.3", Category = "Environment", AssetId = 201, Name = "晨光小屋的厨房", HasImage = true, SortOrder = 2 },
        };

        var names = FrameAssetBindingResolver
            .Resolve(Frame("遐蝶——侧身端锅"), Characters, Envs, Props, Effects, unitBindings)
            .Bindings.OrderBy(b => b.SortOrder).Select(b => b.Name).ToList();

        Assert.Equal(new[] { "遐蝶", "玻吕茜亚", "晨光小屋的厨房" }, names);
    }

    [Fact]
    public void UnitBindings_Null_KeepsFrameOnlyBehavior()
    {
        var names = BoundNames(Frame("遐蝶——侧身端锅"));
        Assert.Equal(new[] { "遐蝶" }, names);
    }

    [Fact]
    public void UnitBindings_DoNotDuplicateFrameMatchedAsset()
    {
        var unitBindings = new List<UnitAssetBinding>
        {
            new() { UnitNumber = "1.3", Category = "Character", AssetId = 101, Name = "遐蝶", HasImage = true, SortOrder = 0 },
        };

        var names = FrameAssetBindingResolver
            .Resolve(Frame("遐蝶——侧身端锅"), Characters, Envs, Props, Effects, unitBindings)
            .Bindings.Where(b => b.Category == "Character").Select(b => b.Name).ToList();

        Assert.Equal(new[] { "遐蝶" }, names);
    }
}
