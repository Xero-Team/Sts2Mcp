using MegaCrit.Sts2.Core.Rooms;

namespace Sts2Mcp;

public static partial class McpMod
{
    /// <summary>
    /// The <c>state_type</c> vocabulary: what a state read says it is looking at.
    ///
    /// The field is the first thing a client branches on, and it is written by the state builders
    /// and read by settle detection, the markdown renderer, and the "is the player being asked"
    /// rules. Those sites used to repeat the literals, so a rename or a typo on the reading side
    /// silently disabled a branch instead of failing the build. The wire strings are part of the
    /// documented contract, which is why each constant's value is exactly the string published.
    ///
    /// Combat state types are named after the room they came from (<c>monster</c>,
    /// <c>elite</c>, <c>boss</c>); see <see cref="CombatRoomStateType"/> for those, since they
    /// are derived, not chosen.
    /// </summary>
    internal static class StateTypes
    {
        /// <summary>
        /// No screen recognised; also the default for a builder that never set one.
        /// </summary>
        internal const string Unknown = "unknown";

        /// <summary>A menu screen is up. <c>menu_screen</c> names which one.</summary>
        internal const string Menu = "menu";

        /// <summary>The map screen, or a room that defers to it.</summary>
        internal const string Map = "map";

        internal const string Event = "event";

        internal const string FakeMerchant = "fake_merchant";

        internal const string Shop = "shop";

        internal const string RestSite = "rest_site";

        internal const string Treasure = "treasure";

        internal const string CardSelect = "card_select";

        internal const string BundleSelect = "bundle_select";

        internal const string RelicSelect = "relic_select";

        internal const string CardReward = "card_reward";

        internal const string Rewards = "rewards";

        internal const string CrystalSphere = "crystal_sphere";

        internal const string HandSelect = "hand_select";

        internal const string GameOver = "game_over";

        /// <summary>An overlay nothing more specific recognised.</summary>
        internal const string Overlay = "overlay";

        /// <summary>
        /// The read itself failed, e.g. the wrong endpoint for the run mode. Named so it does not
        /// shadow the <c>Error(...)</c> response helper.
        /// </summary>
        internal const string ReadError = "error";

        internal const string Monster = "monster";

        internal const string Elite = "elite";

        internal const string Boss = "boss";
    }

    /// <summary>
    /// The state type of a combat room, which is its room type in lower case: monster, elite, or
    /// boss. Derived rather than chosen because the room is the authority on which of the three
    /// it is, and the state reports a room the client has not been told about yet during the
    /// loading window, when only the room node exists.
    /// </summary>
    private static string CombatRoomStateType(CombatRoom room) =>
        room.RoomType.ToString().ToLowerInvariant();
}
