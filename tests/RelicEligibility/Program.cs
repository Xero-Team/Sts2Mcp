using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using STS2_MCP;

var cases = new (bool Expected, Player[] Players)[] {
    (true, [new(new Ironclad(), 0)]),
    (false, [new(new Ironclad(), 1)]),
    (false, [new(new object(), 0)]),
    (true, [new(new object(), 10), new(new Ironclad(), 0)]),
    (true, [new(new Ironclad(), 0), new(new object(), 10)]),
    (false, [new(new Ironclad(), 10), new(new object(), 0)]),
    (false, [new(null, null)]),
};
foreach (var (expected, players) in cases)
    if (McpMod.TestFirstRunIronclad(new RunState(players)) != expected)
        throw new Exception("Party eligibility must inspect each Ironclad's own run count");
Console.WriteLine($"{cases.Length} production relic eligibility checks passed.");

namespace STS2_MCP {
    public static partial class McpMod {
        public static bool TestFirstRunIronclad(RunState run) => HasFirstRunIronclad(run);
    }
}
namespace MegaCrit.Sts2.Core.Runs {
    public interface IRunState { IReadOnlyList<Player> Players { get; } }
    public sealed class RunState : IRunState {
        public RunState(IReadOnlyList<Player> players) => Players = players;
        public IReadOnlyList<Player> Players { get; }
    }
}
namespace MegaCrit.Sts2.Core.Entities.Players {
    public sealed record UnlockState(int NumberOfRuns);
    public sealed class Player(object? character, int? runs) {
        public object? Character { get; } = character;
        public UnlockState? UnlockState { get; } = runs.HasValue ? new(runs.Value) : null;
    }
}
namespace MegaCrit.Sts2.Core.Models.Characters { public sealed class Ironclad; }
