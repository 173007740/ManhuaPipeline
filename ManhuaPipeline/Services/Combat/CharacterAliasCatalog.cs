using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Combat;

/// <summary>
/// 技能归属的群像/别名解析：全部从角色资产 Description 自动推导，
/// 换剧本时不需要改代码，也不需要再往数据库手工配置别名。
/// </summary>
public static class CharacterAliasCatalog
{
    private static readonly string[] GroupRoleSuffixes =
        { "宗主", "掌门", "盟主", "教主", "殿主", "阁主", "门主", "族长", "城主", "长老" };

    private static readonly string[] MembershipMarkers =
        { "之一", "亦在其中", "成员", "所属", "属于", "门下", "宗主", "掌门", "盟主", "教主", "殿主", "阁主", "门主", "长老", "护法", "弟子" };

    public static IReadOnlyList<string> GetOwnerAliases(string? owner, IReadOnlyList<CharacterAsset>? characters = null)
    {
        if (string.IsNullOrWhiteSpace(owner)) return Array.Empty<string>();

        var name = owner.Trim();
        var aliases = new List<string> { name };
        var list = characters?.Where(c => c != null).ToList() ?? new List<CharacterAsset>();

        var ownerAsset = list.FirstOrDefault(c =>
            string.Equals(c.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (ownerAsset != null)
        {
            var description = ownerAsset.Description ?? "";
            aliases.AddRange(ParseAliases(ExtractAliasLine(description)));

            // 群像描述里的成员句式（例如“太虚圣主亦在其中”）。
            AddMentionedGroupMembers(aliases, description);

            // 群像资产的描述里直接点名成员（例如“太虚圣主亦在其中”）。
            foreach (var other in list)
            {
                var otherName = other.Name?.Trim();
                if (string.IsNullOrEmpty(otherName) ||
                    string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (description.IndexOf(otherName, StringComparison.OrdinalIgnoreCase) >= 0)
                    AddMemberAliases(aliases, other);
            }
        }

        // 从其他角色描述反推群像归属（例如“七宗之一太虚宗的宗主”属于“七宗宗主”）。
        var groupName = InferGroupName(name);
        if (groupName != null)
        {
            foreach (var other in list)
            {
                var otherName = other.Name?.Trim();
                if (string.IsNullOrEmpty(otherName) ||
                    string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var description = other.Description ?? "";
                if (description.IndexOf(groupName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!MembershipMarkers.Any(marker =>
                        description.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                AddMemberAliases(aliases, other);
            }
        }

        return aliases
            .Where(a => a.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>取资产卡 Description 里「角色别名」的原始值（没有该行或写「无」时返回 null）。
    /// 供资产绑定层复用同一口径，避免别名只在战斗技能归属里生效。</summary>
    public static string? GetAliasLine(string? description) => ExtractAliasLine(description);

    public static IReadOnlyList<string> ParseAliases(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(
                new[] { '、', '，', ',', ';', '；', '/', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length >= 2 && !x.Equals("无", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddMemberAliases(List<string> aliases, CharacterAsset member)
    {
        var memberName = member.Name?.Trim();
        if (!string.IsNullOrEmpty(memberName)) aliases.Add(memberName);
        aliases.AddRange(ParseAliases(ExtractAliasLine(member.Description)));
    }

    private static string? ExtractAliasLine(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        foreach (var line in description.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^角色别名\s*[:：]\s*(.+)$");

            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                return value.Equals("无", StringComparison.OrdinalIgnoreCase) ? null : value;
            }
        }
        return null;
    }

    private static void AddMentionedGroupMembers(List<string> aliases, string description)
    {
        var patterns = new[]
        {
            @"([^，。；、\n]{2,24}?)(?:亦在其中|就在其中|都在其中|正是其中之一)",
        };
        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(description, pattern))
            {
                foreach (var part in match.Groups[1].Value.Split(
                        new[] { '、', '，', ',', '和', '与', '及' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = part.Trim(' ', '。', '；', ';');
                    if (name.Length >= 2) aliases.Add(name);
                }
            }
        }
    }

    private static string? InferGroupName(string name)
    {
        foreach (var suffix in GroupRoleSuffixes)
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = name.Substring(0, name.Length - suffix.Length).Trim();
            if (prefix.Length >= 2 && prefix.Contains("宗"))
                return prefix;
        }
        return null;
    }
}
