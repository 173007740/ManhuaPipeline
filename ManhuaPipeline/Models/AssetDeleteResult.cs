namespace ManhuaPipeline.Models;

/// <summary>
/// 删除项目资产的结果：是否删掉资产本体，以及顺带清理了多少条绑定引用
/// （UnitAssetBindings / FrameAssetBindings 都以 Category+AssetId 指向资产）。
/// </summary>
public class AssetDeleteResult
{
    public bool Deleted { get; set; }
    public int UnitBindingsRemoved { get; set; }
    public int FrameBindingsRemoved { get; set; }
}
