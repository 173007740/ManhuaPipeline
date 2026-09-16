using ManhuaPipeline.Models.Combat;
using ManhuaPipeline.Services.Combat;
using Microsoft.AspNetCore.Mvc;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/combat/grammar")]
public class CombatGrammarController : ControllerBase
{
    private readonly CombatGrammarEngine _engine;

    public CombatGrammarController(CombatGrammarEngine engine)
    {
        _engine = engine;
    }

    [HttpPost("generate")]
    public IActionResult Generate([FromBody] CombatRequest request)
    {
        if (request == null)
            return BadRequest(new { message = "请求体不能为空" });

        var sequence = _engine.Generate(request);
        return Ok(new
        {
            sequence.Id,
            sequence.Tier,
            sequence.Intent,
            sequence.Style,
            BeatCount = sequence.Beats.Count,
            InitialState = new
            {
                Distance = sequence.InitialState?.Distance,
                SelfState = sequence.InitialState?.Self.State,
                EnemyState = sequence.InitialState?.Enemy.State,
                Advantage = sequence.InitialState?.Advantage
            },
            FinalState = new
            {
                Distance = sequence.FinalState?.Distance,
                SelfState = sequence.FinalState?.Self.State,
                EnemyState = sequence.FinalState?.Enemy.State,
                Advantage = sequence.FinalState?.Advantage
            },
            Beats = sequence.Beats.Select(b => new
            {
                b.Index,
                ActionId = b.Action.Id,
                ActionName = b.Action.Name,
                Category = b.Action.Category,
                SubCategory = b.Action.SubCategory,
                b.Description,
                BeforeDistance = b.BeforeDistance,
                AfterDistance = b.AfterDistance,
                SelfBefore = b.SelfBefore,
                SelfAfter = b.SelfAfter,
                EnemyBefore = b.EnemyBefore,
                EnemyAfter = b.EnemyAfter
            })
        });
    }

    [HttpGet("actions")]
    public IActionResult Actions()
    {
        var actions = CombatActionCatalog.Actions
            .Where(a => !a.IsReaction)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Category,
                a.SubCategory,
                a.Tiers,
                a.AllowedDistances,
                a.RequireEnemyStates,
                a.RequireSelfStates,
                a.EnemyResultState,
                a.SelfResultState,
                a.ResultDistance,
                a.Weight,
                a.NextActions,
                a.DestructionLevel,
                a.Description
            });
        return Ok(actions);
    }
}
