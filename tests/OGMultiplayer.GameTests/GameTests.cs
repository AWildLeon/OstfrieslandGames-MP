using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using OGMultiplayer.Plugin;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OGMultiplayer.GameTests
{
    /// <summary>
    /// In-game test scenarios. Two game instances run the same scenario as host and guest
    /// (scripts/game-test.sh starts them headless with a test server). Every check writes
    /// "PASS name" or "FAIL name: reason" to OGT_RESULT; the scenario ends with "DONE" and
    /// quits the game.
    ///
    /// Environment: OGT_ROLE host|guest, OGT_SCENARIO online|pause, OGT_PORT, OGT_PASS,
    /// OGT_DISC (discipline number), OGT_RESULT (file), OGT_SHOTS (screenshot folder, optional).
    ///
    /// online: both join a room, the host starts, the guest finishes first and must watch the
    ///         host (scene mirror) until the host finishes; both see the results with both
    ///         scores and get back to the lobby.
    /// pause:  the guest pauses instead of finishing; once the host is done, the server's
    ///         pause grace runs out and the guest still gets the results.
    /// </summary>
    [BepInPlugin("de.onlh.ostfriesland.multiplayer.gametests", "OGMultiplayer game tests", "1.0.0")]
    [BepInDependency(OGMultiplayer.Plugin.Plugin.Guid)]
    public sealed class GameTests : BaseUnityPlugin
    {
        private const int HostScore = 1000;
        private const int GuestScore = 800;

        private string role;
        private string scenario;
        private string resultFile;
        private string shots;
        private int failures;

        private bool IsHost => role == "host";

        private static OnlineSession Session => OnlineSession.Instance;

        private static HotSeatFlowController Controller => HotSeatFlowController.Instance;

        private void Awake()
        {
            role = Env("OGT_ROLE", "host");
            scenario = Env("OGT_SCENARIO", "online");
            resultFile = Env("OGT_RESULT", Path.Combine(Paths.GameRootPath, "gametest-" + role + ".txt"));
            shots = Environment.GetEnvironmentVariable("OGT_SHOTS");
            File.WriteAllText(resultFile, string.Empty);
            // headless runs (Xvfb) stall with vsync on
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
            Application.runInBackground = true;
            GameLocalization.CurrentLanguage = GameLanguage.HighGerman;
            StartCoroutine(Run());
        }

        private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

        private void Report(string line)
        {
            Logger.LogInfo("TEST " + line);
            File.AppendAllText(resultFile, line + "\n");
        }

        private void Pass(string name) => Report("PASS " + name);

        private void Fail(string name, string reason)
        {
            failures++;
            Report($"FAIL {name}: {reason}");
        }

        private void Check(string name, bool ok, string reason)
        {
            if (ok)
            {
                Pass(name);
            }
            else
            {
                Fail(name, reason);
            }
        }

        private void Shot(string name)
        {
            if (!string.IsNullOrEmpty(shots))
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(shots, role + "_" + name + ".png"));
            }
        }

        /// <summary>Waits for a condition; false (and a FAIL line) after the timeout.</summary>
        private IEnumerator Wait(string name, Func<bool> condition, float seconds, bool[] ok, bool report = true)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (true)
            {
                bool done;
                try
                {
                    done = condition();
                }
                catch (Exception ex)
                {
                    done = false;
                    Logger.LogWarning(name + ": " + ex.Message);
                }
                if (done)
                {
                    ok[0] = true;
                    if (report)
                    {
                        Pass(name);
                    }
                    yield break;
                }
                if (Time.realtimeSinceStartup > until)
                {
                    ok[0] = false;
                    Fail(name, $"nach {seconds:0} s nicht erreicht");
                    yield break;
                }
                yield return null;
            }
        }

        private IEnumerator Run()
        {
            var ok = new bool[1];
            yield return Wait("start screen", () => SceneManager.GetActiveScene().name == "StartScreen"
                && FindAnyObjectByType<StartScreenMenuController>() != null && Session != null, 60f, ok);
            if (!ok[0])
            {
                yield return Finish();
                yield break;
            }
            // the guest comes a little later, so the host owns the room
            yield return new WaitForSecondsRealtime(IsHost ? 4f : 8f);

            string initials = IsHost ? "HST" : "GST";
            string character = IsHost ? "svea" : "karin";
            OGMultiplayer.Plugin.Plugin.Initials.Value = initials;
            OGMultiplayer.Plugin.Plugin.Character.Value = character;
            Session.Connect("127.0.0.1", int.Parse(Env("OGT_PORT", "27420")), "testraum", initials, character, Env("OGT_PASS", string.Empty));
            yield return Wait("connect", () => Session.Connected && Session.InRoom, 20f, ok);
            if (!ok[0])
            {
                yield return Finish();
                yield break;
            }
            yield return Wait("lobby has both players", () => Session.Lobby.Count(p => !p.IsBot) >= 2, 30f, ok);
            Shot("lobby");

            var discipline = (DisciplineId)int.Parse(Env("OGT_DISC", "2"));
            if (IsHost && ok[0])
            {
                Session.RequestStart(discipline);
            }
            yield return Wait("round begins", () => Session.MatchId != 0, 20f, ok);
            yield return Wait("go", () =>
            {
                HotSeatFlowController c = Controller;
                if (c != null && c.Phase == HotSeatPhase.Instructions)
                {
                    c.ContinueFromInstructions();
                }
                return Session.GoReceived && c != null && c.Phase == HotSeatPhase.Playing;
            }, 60f, ok);
            if (!ok[0])
            {
                yield return Finish();
                yield break;
            }
            yield return new WaitForSecondsRealtime(3f);
            Shot("playing");

            if (IsHost)
            {
                yield return HostPlays(discipline);
            }
            else if (scenario == "pause")
            {
                yield return GuestPauses();
            }
            else
            {
                yield return GuestFinishesAndWatches(discipline);
            }

            yield return Wait("results", () => Controller != null && Controller.Phase == HotSeatPhase.Results, 90f, ok);
            if (ok[0])
            {
                Shot("results");
                List<int> scores = Controller.Match.Players.Select(p => p.BestScore).ToList();
                // after a pause that ran out the guest's last live score counts, whatever it was
                bool expected = scores.Count == 2 && scores.Contains(HostScore) && (scenario == "pause" || scores.Contains(GuestScore));
                Check("result scores", expected, "Punkte " + string.Join(", ", scores));
                yield return new WaitForSecondsRealtime(2f);
                HotSeatFlowController.AbortToMenu();
                yield return Wait("back in the lobby", () => Controller == null && Session.InRoom && Session.MatchId == 0
                    && Session.RoomState == Protocol.StateLobby, 30f, ok);
            }
            yield return Finish();
        }

        private IEnumerator HostPlays(DisciplineId discipline)
        {
            var ok = new bool[1];
            if (scenario == "pause")
            {
                yield return Wait("guest paused", () => Session.Lobby.Any(p => p.Status == Protocol.StatusPaused), 30f, ok);
            }
            else
            {
                // the guest finishes first and watches; give it time to see something
                yield return Wait("guest done", () => Session.Lobby.Any(p => !p.IsBot && p.Id != Session.MyId && p.Status == Protocol.StatusDone), 30f, ok);
                yield return new WaitForSecondsRealtime(15f);
            }
            HotSeatFlowController.TryCompleteTurn(discipline, HostScore);
            Check("host finished", Session.LocalFinished, "eigene Runde nicht beendet");
            if (scenario == "pause")
            {
                yield return Wait("pause countdown visible", () => Session.Lobby.Any(p => p.GraceLeft > 0), 10f, ok);
                Shot("waiting_for_paused");
            }
        }

        private IEnumerator GuestFinishesAndWatches(DisciplineId discipline)
        {
            var ok = new bool[1];
            HotSeatFlowController.TryCompleteTurn(discipline, GuestScore);
            Check("guest finished", Session.LocalFinished, "eigene Runde nicht beendet");
            int host = Session.HostId;
            yield return Wait("watch target is the host", () => Session.WatchTarget == host, 10f, ok);
            yield return Wait("watching the host's scene", () => MirrorView.Instance.Showing && MirrorView.Instance.Watching == host, 20f, ok);
            yield return Wait("mirror objects bound", () => MirrorView.Instance.BoundCount > 0, 10f, ok);
            if (ok[0])
            {
                yield return new WaitForSecondsRealtime(3f);
                Shot("watching");
            }
        }

        private IEnumerator GuestPauses()
        {
            var ok = new bool[1];
            PauseManager pause = FindAnyObjectByType<PauseManager>();
            Check("pause menu exists", pause != null, "kein PauseManager in der Szene");
            if (pause == null)
            {
                yield break;
            }
            pause.SetPaused(true);
            yield return Wait("pause countdown for me", () => Session.MyGraceLeft > 0, 40f, ok);
            Shot("paused_countdown");
            yield return Wait("pause ends with the result", () => Session.LocalFinished && !PauseManager.IsAnyPaused, 40f, ok);
        }

        private IEnumerator Finish()
        {
            Report(failures == 0 ? "DONE ok" : $"DONE {failures} failed");
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }
    }
}
