using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OGMultiplayer.Plugin
{
    /// <summary>
    /// Player setup for online and bot games: runs the game's own hot seat registration
    /// (character map, quote bubble, initials boxes) with a single player and hands the
    /// result back to the online menu. Nothing of that screen is rebuilt here; the game
    /// draws it, the mod only steers it.
    /// </summary>
    internal static class OnlineSetup
    {
        public enum Purpose
        {
            None,
            Online,
            Bots,
            ChangeInLobby,
            Choice
        }

        private static readonly PropertyInfo PlayerCountProp = AccessTools.Property(typeof(HotSeatCharacterSetup), nameof(HotSeatCharacterSetup.PlayerCount));
        private static readonly PropertyInfo StepProp = AccessTools.Property(typeof(HotSeatCharacterSetup), nameof(HotSeatCharacterSetup.Step));
        private static readonly FieldInfo PlayersField = AccessTools.Field(typeof(HotSeatCharacterSetup), "players");
        private static readonly MethodInfo EnsurePlayersMethod = AccessTools.Method(typeof(HotSeatCharacterSetup), "EnsurePlayers");
        private static readonly MethodInfo ShowPlayerMethod = AccessTools.Method(typeof(HotSeatCharacterSetup), "ShowPlayer");
        private static readonly MethodInfo RefreshMethod = AccessTools.Method(typeof(HotSeatCharacterSetup), "Refresh");

        /// <summary>The hot seat controller that only exists for this setup.</summary>
        public static HotSeatFlowController Controller { get; private set; }

        public static Purpose Current { get; private set; }

        /// <summary>The purpose of the last setup, so a cancelled one returns to the same menu entry.</summary>
        public static Purpose Last { get; private set; }

        /// <summary>What the online menu should open once the start screen is back.</summary>
        public static Purpose Finished { get; set; }

        public static bool IsSetupController(HotSeatFlowController c) => c != null && Controller == c;

        public static IEnumerable<string> MissingMembers()
        {
            var members = new Dictionary<string, object>
            {
                { "HotSeatCharacterSetup.PlayerCount", PlayerCountProp?.GetSetMethod(true) },
                { "HotSeatCharacterSetup.Step", StepProp?.GetSetMethod(true) },
                { "HotSeatCharacterSetup.players", PlayersField },
                { "HotSeatCharacterSetup.EnsurePlayers", EnsurePlayersMethod },
                { "HotSeatCharacterSetup.ShowPlayer", ShowPlayerMethod },
                { "HotSeatCharacterSetup.Refresh", RefreshMethod },
            };
            foreach (KeyValuePair<string, object> kv in members)
            {
                if (kv.Value == null)
                {
                    yield return kv.Key;
                }
            }
        }

        /// <summary>Opens the game's character registration for one player.</summary>
        public static void Begin(Purpose purpose)
        {
            if (HotSeatFlowController.IsActive || SceneManager.GetActiveScene().name != "StartScreen")
            {
                return;
            }
            Current = purpose;
            Last = purpose;
            Finished = Purpose.None;
            Controller = GameBridge.CreateController();
            OnlineSession.RunAuthorized(Controller.EditPlayers);
            Plugin.Log.LogInfo($"Figurenauswahl des Spiels für {purpose} geoeffnet");
        }

        /// <summary>Called right after the game built its registration screen.</summary>
        public static void OnRegistrationShown(HotSeatCharacterSetup setup)
        {
            PlayerCountProp.SetValue(setup, 1);
            var players = (List<HotSeatPlayer>)PlayersField.GetValue(setup);
            players.Clear();
            string initials = Protocol.NormalizeInitials(Plugin.Initials.Value) ?? "AAA";
            players.Add(new HotSeatPlayer(1, initials, Plugin.Character.Value));
            EnsurePlayersMethod.Invoke(setup, null);
            ShowPlayerMethod.Invoke(setup, new object[] { 0 });
            StepProp.SetValue(setup, HotSeatCharacterSetup.SetupStep.Character);
            RefreshMethod.Invoke(setup, null);
        }

        /// <summary>Back pressed on the first step: leave to the online menu instead of the player count.</summary>
        public static bool WantsToLeave(HotSeatCharacterSetup setup)
        {
            return setup.Step == HotSeatCharacterSetup.SetupStep.PlayerCount;
        }

        /// <summary>The player confirmed the initials: keep the choice and return to the start screen.</summary>
        public static void Complete(HotSeatPlayer player)
        {
            Plugin.Initials.Value = player.Initials;
            Plugin.Character.Value = player.CharacterId;
            Finished = Current;
            Plugin.Log.LogInfo($"Spieler gewählt: {player.Initials} ({player.CharacterId})");
            Close();
        }

        public static void Cancel()
        {
            Finished = Current == Purpose.ChangeInLobby ? Purpose.ChangeInLobby : Purpose.Choice;
            Close();
        }

        private static void Close()
        {
            Current = Purpose.None;
            HotSeatFlowController.AbortToMenu();
            Controller = null;
        }
    }
}
