using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;
using UnityEngine.UI;

namespace OGMultiplayer.Plugin
{
    /// <summary>
    /// Access to non-public parts of the game. Everything reflective lives here so
    /// that a game update only breaks one file.
    /// </summary>
    internal static class GameBridge
    {
        private static readonly FieldInfo ScreenField = AccessTools.Field(typeof(HotSeatFlowController), "screen");
        private static readonly FieldInfo WaitingOwnerField = AccessTools.Field(typeof(HotSeatFlowController), "waitingOwner");
        private static readonly FieldInfo SubmittingField = AccessTools.Field(typeof(HotSeatFlowController), "submittingHighscores");
        private static readonly PropertyInfo ConfigurationProp = AccessTools.Property(typeof(HotSeatFlowController), "Configuration");
        private static readonly PropertyInfo FontProp = AccessTools.Property(typeof(HotSeatFlowController), "Font");
        private static readonly PropertyInfo PhaseProp = AccessTools.Property(typeof(HotSeatFlowController), "Phase");
        private static readonly MethodInfo RefreshEligibilityMethod = AccessTools.Method(typeof(HotSeatFlowController), "RefreshHighscoreEligibility");
        private static readonly MethodInfo PresentMenuMethod = AccessTools.Method(typeof(HotSeatFlowController), "PresentMenu");
        private static readonly PropertyInfo HighscoresSavedProp = AccessTools.Property(typeof(HotSeatMatch), "HighscoresSaved");
        private static readonly FieldInfo ScoreDocField = AccessTools.Field(typeof(ScoreStore), "doc");
        private static readonly MethodInfo ScoreSaveMethod = AccessTools.Method(typeof(ScoreStore), "Save", Type.EmptyTypes);

        private static CharacterSelect2DConfig cachedConfig;
        private static Font cachedFont;

        public static IEnumerable<string> MissingMembers()
        {
            var members = new Dictionary<string, object>
            {
                { "HotSeatFlowController.screen", ScreenField },
                { "HotSeatFlowController.waitingOwner", WaitingOwnerField },
                { "HotSeatFlowController.submittingHighscores", SubmittingField },
                { "HotSeatFlowController.Configuration", ConfigurationProp?.GetSetMethod(true) },
                { "HotSeatFlowController.Font", FontProp?.GetSetMethod(true) },
                { "HotSeatFlowController.Phase", PhaseProp?.GetSetMethod(true) },
                { "HotSeatFlowController.RefreshHighscoreEligibility", RefreshEligibilityMethod },
                { "HotSeatFlowController.PresentMenu", PresentMenuMethod },
                { "HotSeatMatch.HighscoresSaved", HighscoresSavedProp?.GetSetMethod(true) },
                { "ScoreStore.doc", ScoreDocField },
                { "ScoreStore.Save", ScoreSaveMethod },
                { "OnlineScoreSync.Send", AccessTools.Method(typeof(OnlineScoreSync), "Send") },
            };
            return members.Where(kv => kv.Value == null).Select(kv => kv.Key);
        }

        /// <summary>Remembers the menu's character config and font while the start screen is loaded.</summary>
        public static void CacheMenuAssets()
        {
            if (cachedConfig != null && cachedFont != null)
            {
                return;
            }
            StartScreenMenuController menu = UnityEngine.Object.FindAnyObjectByType<StartScreenMenuController>();
            if (menu == null)
            {
                return;
            }
            if (cachedConfig == null)
            {
                cachedConfig = menu.disciplineCharacterConfig;
            }
            if (cachedFont == null && menu.mainItems != null)
            {
                foreach (StartScreenMenuController.MenuItem item in menu.mainItems)
                {
                    if (item?.text != null && item.text.font != null)
                    {
                        cachedFont = item.text.font;
                        break;
                    }
                }
            }
        }

        public static CharacterSelect2DConfig Config
        {
            get
            {
                CacheMenuAssets();
                if (cachedConfig == null)
                {
                    cachedConfig = Resources.FindObjectsOfTypeAll<CharacterSelect2DConfig>()
                        .FirstOrDefault(c => c != null && c.selection?.characters != null && c.selection.characters.Length > 0);
                }
                return cachedConfig;
            }
        }

        public static Font Font
        {
            get
            {
                CacheMenuAssets();
                if (cachedFont == null)
                {
                    Text any = UnityEngine.Object.FindAnyObjectByType<Text>();
                    cachedFont = any != null && any.font != null ? any.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }
                return cachedFont;
            }
        }

        public sealed class CharacterInfo
        {
            public string Id;
            public string Name;
        }

        public static List<CharacterInfo> PlayableCharacters()
        {
            var list = new List<CharacterInfo>();
            CharacterSelect2DConfig config = Config;
            if (config?.selection?.characters == null)
            {
                return list;
            }
            foreach (CharacterSelect2DConfig.CharacterEntry c in config.selection.characters)
            {
                if (c != null && c.availability == CharacterSelect2DConfig.CharacterAvailability.Playable && !string.IsNullOrEmpty(c.id))
                {
                    list.Add(new CharacterInfo { Id = c.id, Name = string.IsNullOrEmpty(c.displayName) ? c.id : c.displayName });
                }
            }
            return list;
        }

        /// <summary>
        /// Builds a hot seat controller like HotSeatFlowController.Open does, but skips
        /// the local player setup; players come from the online lobby instead.
        /// </summary>
        public static HotSeatFlowController CreateController()
        {
            CharacterSelect2DConfig config = Config;
            Font font = Font;
            if (config == null || font == null)
            {
                throw new InvalidOperationException("Spielmenue noch nicht geladen (Figurenliste fehlt).");
            }
            HotSeatFlowController existing = HotSeatFlowController.Instance;
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing.gameObject);
            }
            var go = new GameObject("HotSeatFlowController");
            HotSeatFlowController controller = go.AddComponent<HotSeatFlowController>();
            ConfigurationProp.SetValue(controller, config);
            FontProp.SetValue(controller, font);
            HotSeatScreen screen = go.AddComponent<HotSeatScreen>();
            ScreenField.SetValue(controller, screen);
            screen.Initialize(controller, config, font);
            AttractDemoController.NotifyHotSeatActivity();
            return controller;
        }

        public static void SetPhase(HotSeatFlowController controller, HotSeatPhase phase)
        {
            PhaseProp.SetValue(controller, phase);
        }

        public static bool IsWaitingForTurnStart(HotSeatFlowController controller)
        {
            return controller.Phase == HotSeatPhase.Handoff && (UnityEngine.Object)WaitingOwnerField.GetValue(controller) != null;
        }

        public static void ShowResults(HotSeatFlowController controller)
        {
            controller.StopAllCoroutines();
            RefreshEligibilityMethod.Invoke(controller, null);
            PresentMenuMethod.Invoke(controller, new object[] { HotSeatPhase.Results });
        }

        /// <summary>
        /// Online variant of SaveHighscores: only the local player (slot 1) is stored, and only
        /// in the local list. Online-match scores never go to the game's worldwide leaderboard.
        /// </summary>
        public static void SaveLocalHighscore(HotSeatFlowController controller)
        {
            HotSeatMatch match = controller.Match;
            if (controller.Phase != HotSeatPhase.Results || match == null || match.HighscoresSaved)
            {
                return;
            }
            HighscoresSavedProp.SetValue(match, true);
            SubmittingField.SetValue(controller, true);
            try
            {
                HotSeatPlayer local = match.Players[0];
                bool qualifiesLocal = local.BestScore > 0 && ScoreStore.Instance.QualifiesForTop10(match.Discipline, local.BestScore);
                local.HighscoreEligible = qualifiesLocal;
                local.HighscoreWorldwide = false;
                if (qualifiesLocal)
                {
                    SubmitLocalOnly(match.Discipline, local.BestScore, local.Initials);
                    local.HighscoreSaved = true;
                    local.HighscoreLocal = true;
                }
                for (int i = 1; i < match.Players.Count; i++)
                {
                    match.Players[i].HighscoreEligible = false;
                }
            }
            finally
            {
                SubmittingField.SetValue(controller, false);
            }
        }

        /// <summary>Stores a score in the local list and takes it out of the upload outbox again.</summary>
        private static void SubmitLocalOnly(DisciplineId discipline, int score, string nickname)
        {
            ScoreStore store = ScoreStore.Instance;
            ScoreDocument doc = ScoreDocField?.GetValue(store) as ScoreDocument;
            int before = doc?.outbox?.Count ?? -1;
            store.Submit(discipline, score, nickname);
            if (doc?.outbox == null || doc.outbox.Count <= before || ScoreSaveMethod == null)
            {
                Plugin.Log.LogWarning("Online-Score konnte nicht aus der Upload-Warteschlange genommen werden.");
                return;
            }
            ScoreEntry entry = doc.outbox[doc.outbox.Count - 1];
            entry.onlineReady = false;
            doc.outbox.RemoveAt(doc.outbox.Count - 1);
            ScoreSaveMethod.Invoke(store, null);
        }

        /// <summary>Best effort live score of the running discipline, -1 when unknown.</summary>
        public static int LiveScore(DisciplineId discipline)
        {
            switch (discipline)
            {
                case DisciplineId.Padstock:
                {
                    ScoreManager sm = UnityEngine.Object.FindAnyObjectByType<ScoreManager>();
                    return sm != null ? sm.CurrentScore : -1;
                }
                case DisciplineId.Bosseln:
                {
                    BosselnWurfPrototype b = UnityEngine.Object.FindAnyObjectByType<BosselnWurfPrototype>();
                    return b != null ? b.Score : -1;
                }
                case DisciplineId.Krabbenpulen:
                {
                    KrabbenpulenPrototype k = UnityEngine.Object.FindAnyObjectByType<KrabbenpulenPrototype>();
                    return k != null ? k.Score : -1;
                }
                default:
                    return -1;
            }
        }

        private static readonly MethodInfo DisciplineNameMethod = AccessTools.Method(typeof(HotSeatScreen), "DisciplineName", new[] { typeof(DisciplineId) });
        private static readonly MethodInfo GameSceneMethod = AccessTools.Method(typeof(HotSeatFlowController), "GameScene", new[] { typeof(DisciplineId) });

        /// <summary>The game's own (localized) name of a discipline.</summary>
        public static string DisciplineName(DisciplineId discipline)
        {
            return CallGame(DisciplineNameMethod, discipline) ?? discipline.ToString();
        }

        /// <summary>The scene the game loads for a discipline.</summary>
        public static string GameScene(DisciplineId discipline)
        {
            return CallGame(GameSceneMethod, discipline);
        }

        private static string CallGame(MethodInfo method, DisciplineId discipline)
        {
            try
            {
                return method?.Invoke(null, new object[] { discipline }) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
