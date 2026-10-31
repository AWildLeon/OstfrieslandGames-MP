using System.Collections;
using System.Collections.Generic;
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
                // Bosseln (and others) are played by hammering Space/A; the same presses must not
                // start the next round the moment the results appear
                if (!ResultsInputReady())
                {
                    return false;
                }
                if (Session.IsHost)
                {
                    Session.RequestStart(__instance.Match.Discipline);
                }
                else
                {
                    Session.Toast($"{Session.HostInitials()} startet die nächste Runde. Esc: zurück in die Lobby.", 5f);
                }
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
            // guests: Back on the results page leaves to the lobby (from there "Raum verlassen")
            HotSeatFlowController.AbortToMenu();
            return false;
        }


        private static bool ResultsInputReady()
        {
            if (Time.unscaledTime - GameBridge.ResultsShownAt < 2.5f)
            {
                return false;
            }
            return true;
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

    /// <summary>
    /// The start screen camera scrolls back to the logo on Back; it skips that during hot seat,
    /// and does the same while an online page is open (so leaving a page stays in the menu).
    /// </summary>
    [HarmonyPatch(typeof(StartScreenCameraScroller), "Update")]
    internal static class CameraScrollerPatch
    {
        private static bool Prefix()
        {
            return !StartScreenPatches.Covered && Time.unscaledTime >= OnlineMenu.MenuInputBlockedUntil;
        }
    }

    /// <summary>Lets the game's own hot seat registration pick the online player (see OnlineSetup).</summary>
    [HarmonyPatch]
    internal static class OnlineSetupPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HotSeatCharacterSetup), nameof(HotSeatCharacterSetup.Initialize))]
        private static void InitializePostfix(HotSeatCharacterSetup __instance, HotSeatFlowController flow)
        {
            if (OnlineSetup.IsSetupController(flow))
            {
                OnlineSetup.OnRegistrationShown(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HotSeatCharacterSetup), nameof(HotSeatCharacterSetup.Back))]
        private static void BackPostfix(HotSeatCharacterSetup __instance)
        {
            if (OnlineSetup.IsSetupController(HotSeatFlowController.Instance) && OnlineSetup.WantsToLeave(__instance))
            {
                OnlineSetup.Cancel();
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HotSeatFlowController), nameof(HotSeatFlowController.ConfigurePlayers))]
        private static bool ConfigurePlayersPrefix(HotSeatFlowController __instance, List<HotSeatPlayer> players)
        {
            if (!OnlineSetup.IsSetupController(__instance))
            {
                return true;
            }
            if (players != null && players.Count > 0)
            {
                OnlineSetup.Complete(players[0]);
            }
            else
            {
                OnlineSetup.Cancel();
            }
            return false;
        }
    }

    /// <summary>Hooks the online pages into the start screen menu.</summary>
    [HarmonyPatch(typeof(StartScreenMenuController))]
    internal static class StartScreenPatches
    {
        // MEHRSPIELER opens the choice page (one device / online / bots) instead of the local setup.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(StartScreenMenuController.OpenMultiplayer))]
        private static bool OpenMultiplayerPrefix()
        {
            if (OnlineSession.Authorized || OnlineMenu.Instance == null || HotSeatFlowController.IsActive)
            {
                return true;
            }
            OnlineMenu.Instance.ShowChoice();
            return false;
        }

        /// <summary>True while an online page covers the start screen (or is about to).</summary>
        internal static bool Covered => OnlineMenu.Visible || OnlineSetup.Finished != OnlineSetup.Purpose.None
            || (OnlineSession.Instance != null && OnlineSession.Instance.ReturnToLobby && OnlineSession.Instance.Connected);

        // While an online page is open the main menu is hidden and ignores input, like during hot seat.
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static bool UpdatePrefix(StartScreenMenuController __instance)
        {
            // also while returning from the game's registration, so the main menu (and its
            // one-time notices) does not flash up before the online page takes over
            bool covered = Covered;
            if (!covered && Time.unscaledTime >= OnlineMenu.MenuInputBlockedUntil)
            {
                return true;
            }
            if (covered && __instance.canvasGroup != null)
            {
                __instance.canvasGroup.alpha = 0f;
                __instance.canvasGroup.interactable = false;
                __instance.canvasGroup.blocksRaycasts = false;
            }
            return false;
        }
    }
}
