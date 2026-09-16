namespace ManhuaPipeline.Models;

public class StageUnit
{
    public int EpisodeNumber { get; set; }
    public string UnitNumber { get; set; } = "";
    public string Type { get; set; } = "";
    public int Duration { get; set; }
    public string Location { get; set; } = "";
    public string CoreAction { get; set; } = "";
    public string StartState { get; set; } = "";
    public string EndState { get; set; } = "";
    public string Dialogue { get; set; } = "";
    public string KeyElements { get; set; } = "";
    public string DirectorTemplate { get; set; } = "";
    public string RawText { get; set; } = "";

    public bool IsCombat =>
        Type.Contains("打斗") ||
        Type.Contains("追逐") ||
        Type.Contains("逃亡") ||
        Type.Contains("高潮") ||
        Type.Contains("对决") ||
        Type.Contains("战斗") ||
        Type.Contains("对战") ||
        Type.Contains("缠斗") ||
        Type.Contains("混战") ||
        Type.Contains("追击") ||
        Type.Contains("厮杀");
}

public class CombatParticipant
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Weapon { get; set; } = "";
}

public class CombatIntent
{
    public List<CombatParticipant> Participants { get; set; } = [];
    public string CombatForm { get; set; } = "";
    public string Objective { get; set; } = "";
    public int Intensity { get; set; } = 3;
    public int Duration { get; set; } = 11;
    public string EnvironmentType { get; set; } = "";
    public List<string> RequiredSkills { get; set; } = [];
    public List<string> RequiredActions { get; set; } = [];
    public string Result { get; set; } = "";
    public string StartState { get; set; } = "";
    public string EndState { get; set; } = "";
    public List<string> Forbidden { get; set; } = [];
}
