using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace OGMultiplayer.Plugin
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("OG.x86_64")]
    [BepInProcess("OG.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "de.onlh.ostfriesland.multiplayer";
        public const string Name = "Ostfriesland Games Online Multiplayer";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> ServerHost;
        internal static ConfigEntry<int> ServerPort;
        internal static ConfigEntry<string> RoomName;
        internal static ConfigEntry<string> ServerPassword;
        internal static ConfigEntry<string> AcceptedPrivacy;
        internal static ConfigEntry<string> Initials;
        internal static ConfigEntry<string> Character;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<HudCorner> HudPosition;
        internal static ConfigEntry<bool> BlockScoreApi;
        internal static ConfigEntry<bool> Spectate;
        internal static ConfigEntry<bool> AllowWatchers;

        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            ServerHost = Config.Bind("Server", "Host", "127.0.0.1", "Hostname oder IP des OGMultiplayer-Servers.");
            ServerPort = Config.Bind("Server", "Port", OGMultiplayer.Protocol.DefaultPort, "TCP-Port des Servers.");
            ServerPassword = Config.Bind("Server", "Password", "", "Server-Passwort (leer = offener Server). Wird im Klartext in dieser Datei gespeichert, aber nie im Klartext uebertragen.");
            AcceptedPrivacy = Config.Bind("Server", "AcceptedPrivacy", "", "Akzeptierte Datenschutzhinweise je Server (host:port=Fassung). Leeren, um erneut gefragt zu werden.");
            RoomName = Config.Bind("Server", "Room", "ostfriesland", "Raumname; alle Mitspieler muessen denselben Raum nutzen.");
            Initials = Config.Bind("Player", "Initials", "", "Deine drei Initialen (A-Z).");
            Character = Config.Bind("Player", "Character", "tim", "Figur-ID.");
            ToggleKey = Config.Bind("UI", "ToggleKey", KeyCode.F6, "Abkuerzung im Hauptmenue: oeffnet direkt die Online-Seite.");
            BlockScoreApi = Config.Bind("Privacy", "BlockScoreApiWhileOnline", true, "Keine Verbindungen zur Online-Bestenliste des Spiels, solange der Online-Modus verbunden ist. Online-Runden landen nie in der weltweiten Bestenliste.");
            if (ToggleKey.Value == KeyCode.F8)
            {
                // F8 is the game's pin editor key in the character select; never share keys with the game
                ToggleKey.Value = KeyCode.F6;
            }
            Spectate = Config.Bind("Spectate", "WatchOthers", true, "Wenn du fertig bist, siehst du im Hintergrund dem besten Spieler zu, der noch spielt.");
            AllowWatchers = Config.Bind("Spectate", "AllowWatchers", true, "Andere duerfen dir zuschauen, wenn sie vor dir fertig sind (dein Spielbild geht dann ueber den Server an sie).");
            AllowWatchers.SettingChanged += (_, __) => OnlineSession.Instance?.SendViewPreference();
            HudPosition = Config.Bind("UI", "HudPosition", HudCorner.BottomRight, "Ecke fuer die Spielerliste waehrend einer Online-Runde.");

            foreach (string missing in System.Linq.Enumerable.Concat(GameBridge.MissingMembers(), OnlineSetup.MissingMembers()))
            {
                Log.LogError($"Spielversion passt nicht zum Mod: {missing} fehlt. Online-Modus ist evtl. defekt.");
            }

            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);

            var host = new GameObject("OGMultiplayer");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<OnlineSession>();
            host.AddComponent<OnlineMenu>();
            host.AddComponent<OnlineHud>();
            host.AddComponent<MirrorSender>();
            host.AddComponent<MirrorView>();
            Log.LogInfo("Inoffizieller Fan-Mod, keine Verbindung zu den Entwicklern von Ostfriesland Games. Fehler bitte nicht an deren Support melden.");
            Log.LogInfo($"{Name} {Version} geladen (Protokoll v{OGMultiplayer.Protocol.Version}). Hauptmenue > MEHRSPIELER oder {ToggleKey.Value} oeffnet den Online-Modus.");
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
        }
    }
}
