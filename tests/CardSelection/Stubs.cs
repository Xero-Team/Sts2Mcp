using System;

// Stand-ins for the game types the shipped card-selection annotator touches. They
// keep the shapes the production file reads, nothing else.

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatchAttribute : Attribute
    {
    }
}

namespace MegaCrit.Sts2.Core.CardSelection
{
    public sealed class CardSelectorPrefs
    {
        public int MinSelect { get; set; }

        public int MaxSelect { get; set; }

        public bool RequireManualConfirmation { get; set; }

        public bool Cancelable { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Commands
{
    public static class CardSelectCmd
    {
        public static void FromHandForDiscard()
        {
        }

        public static void FromHandForUpgrade()
        {
        }

        public static void FromDeck()
        {
        }

        public static void FromChooseACard()
        {
        }
    }
}

namespace MegaCrit.Sts2.Core.Entities.Cards
{
    public enum PileType
    {
        Hand,
        Draw,
        Discard,
        Exhaust,
    }

    public sealed class CardPile
    {
        public PileType Type { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Models
{
    public sealed class ModelId
    {
        public string Entry { get; set; } = "";
    }

    public abstract class AbstractModel
    {
        public ModelId Id { get; } = new();
    }

    public class CardModel : AbstractModel
    {
        public bool IsUpgraded { get; set; }
    }
}

namespace MegaCrit.Sts2.Core.Models.Cards
{
    public sealed class Anger : MegaCrit.Sts2.Core.Models.CardModel
    {
    }
}

namespace MegaCrit.Sts2.Core.Models.Potions
{
    public sealed class FirePotion
    {
    }
}

namespace MegaCrit.Sts2.Core.Models.Powers
{
    public sealed class StrengthPower
    {
    }
}

namespace MegaCrit.Sts2.Core.Models.Relics
{
    public sealed class Anchor
    {
    }
}

namespace MegaCrit.Sts2.Core.Models.Monsters
{
    public sealed class Cultist
    {
    }
}

namespace MegaCrit.Sts2.Core.Models.Events
{
    public sealed class PunchOff
    {
    }
}

namespace Sts2Mcp.Tests.CardSelection
{
    internal sealed class Unrelated
    {
    }
}

namespace Sts2Mcp
{
    public static partial class McpMod
    {
        // The production TryDo lives in McpMod.Helpers.cs, which needs the game; the
        // harness keeps the same "run it, report a failure instead of throwing" meaning.
        internal static bool TryDo(Action action,
            [System.Runtime.CompilerServices.CallerMemberName] string member = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
            => TryDo($"{member}:{line}", action);

        internal static bool TryDo(string context, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
