using System.Collections;
using HarmonyLib;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;

namespace OGMultiplayer.Plugin
{
    /// <summary>
    /// Hooks into the game's hot seat flow. Every patch is a no-op unless the active
    /// HotSeatFlowController was created by the online session.
    /// </summary>
    [HarmonyPatch(typeof(HotSeatFlowController))]
    internal static class HotSeatFlowPatches
    {
        private static OnlineSession Session => OnlineSession.Instance;

        private static bool Online(HotSeatFlowController c) => OnlineSession.IsOnlineController(c);

        // A game scene is waiting at the turn start: tell the server we are ready.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(HotSeatFlowController.TryAwaitTurnStart))]
        private static void TryAwaitTurnStartPostfix(bool __result)
        {
            if (__result && Online(HotSeatFlowController.Instance))
            {
                Session.OnAwaitingTurnStart();
            }
        }

        // Only the server's GO starts the turn, so everybody starts together.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(HotSeatFlowController.StartTurn))]
        private static bool StartTurnPrefix(HotSeatFlowController __instance)
        {
            if (!Online(__instance) || OnlineSession.Authorized)
            {
                return true;
            }
            return false;
        }

        // Record the local score and wait for the others instead of handing over to the next player.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(HotSeatFlowController.TryCompleteTurn))]
        private static bool TryCompleteTurnPrefix(DisciplineId discipline, int score, ref bool __result)
        {
            HotSeatFlowController c = HotSeatFlowController.Instance;
            if (!Online(c))
            {
                return true;
            }
            __result = true;
            if (HotSeatFlowController.IsPlaying && c.Match != null && discipline == c.Match.Discipline && c.Match.CurrentTurn == 0)
            {
                c.Match.TryRecordResult(0, score);
                Session.OnLocalTurnFinished(score);
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(HotSeatFlowController.SaveHighscores))]
        private static bool SaveHighscoresPrefix(HotSeatFlowController __instance)
        {
            if (!Online(__instance))
            {
                return true;
            }
            GameBridge.SaveLocalHighscore(__instance);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(HotSeatFlowController.BeginMatch))]
        private static bool BeginMatchPrefix(HotSeatFlowController __instance, DisciplineId discipline)
        {
            if (!Online(__instance) || OnlineSession.Authorized)
            {
                return true;
            }
            Session.RequestStart(discipline);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(HotSeatFlowController.Rematch))]
        private static bool RematchPrefix(HotSeatFlowController __instance)
        {
            if (!Online(__instance) || OnlineSession.Authorized)
            {
                return true;
            }
            if (__instance.Phase == HotSeatPhase.Results && __instance.Match != null)
            {
                Session.RequestStart(__instance.Match.Discipline);
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(HotSeatFlowController.ChooseDiscipline))]
        private static bool ChooseDisciplinePrefix(HotSeatFlowController __instance)
        {
            if (!Online(__instance) || Session.IsHost)
            {
                return true;
            }
            Session.Toast($"Nur der Host ({Session.HostInitials()}) waehlt die Disziplin.");
            return false;
        }

        // "Back" on the discipline page would open the local player setup: leave to the menu instead.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(HotSeatFlowController.EditPlayers))]
        private static bool EditPlayersPrefix(HotSeatFlowController __instance)
        {
            if (!Online(__instance) || OnlineSession.Authorized)
            {
                return true;
            }
            HotSeatFlowController.AbortToMenu();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("Abort")]
        private static void AbortPrefix()
        {
            if (Online(HotSeatFlowController.Instance))
            {
                Session.LeaveMatch();
            }
        }
    }

    /// <summary>
    /// The game's own score API (OnlineScoreSync) sends every request through one coroutine.
    /// While the online mode is in use, it is not started: the game treats it as a failed
    /// request and retries later.
    /// </summary>
    [HarmonyPatch(typeof(OnlineScoreSync), "Send")]
    internal static class ScoreApiPatch
    {
        private static bool logged;

        private static bool Prefix(ref IEnumerator __result)
        {
            if (!OnlineSession.BlocksScoreApi)
            {
                logged = false;
                return true;
            }
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo("Score-API des Spiels gesperrt, solange der Online-Modus aktiv ist.");
            }
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }
    }

    /// <summary>Keeps keyboard input out of the game menus while typing into the online window.</summary>
    [HarmonyPatch]
    internal static class MenuInputPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StartScreenMenuController), "Update")]
        private static bool StartScreenUpdatePrefix() => !OnlineUi.CapturesKeyboard;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HotSeatScreen), "Update")]
        private static bool HotSeatScreenUpdatePrefix() => !OnlineUi.CapturesKeyboard;
    }
}
