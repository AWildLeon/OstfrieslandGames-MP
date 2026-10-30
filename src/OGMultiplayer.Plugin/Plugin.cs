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
        internal static ConfigEntry<string> Initials;
        internal static ConfigEntry<string> Character;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<HudCorner> HudPosition;
        internal static ConfigEntry<bool> BlockScoreApi;

        private Harmony harmony;

        private void Awake()
        {
            Log = Logger;
            ServerHost = Config.Bind("Server", "Host", "127.0.0.1", "Hostname oder IP des OGMultiplayer-Servers.");
            ServerPort = Config.Bind("Server", "Port", OGMultiplayer.Protocol.DefaultPort, "TCP-Port des Servers.");
            ServerPassword = Config.Bind("Server", "Password", "", "Server-Passwort (leer = offener Server). Wird im Klartext in dieser Datei gespeichert, aber nie im Klartext uebertragen.");
            RoomName = Config.Bind("Server", "Room", "ostfriesland", "Raumname; alle Mitspieler muessen denselben Raum nutzen.");
            Initials = Config.Bind("Player", "Initials", "", "Deine drei Initialen (A-Z).");
            Character = Config.Bind("Player", "Character", "tim", "Figur-ID.");
            ToggleKey = Config.Bind("UI", "ToggleKey", KeyCode.F8, "Taste fuer das Online-Fenster.");
            BlockScoreApi = Config.Bind("Privacy", "BlockScoreApiWhileOnline", true, "Keine Verbindungen zur Online-Bestenliste des Spiels, solange der Online-Modus verbunden ist. Online-Runden landen nie in der weltweiten Bestenliste.");
            HudPosition = Config.Bind("UI", "HudPosition", HudCorner.BottomRight, "Ecke fuer die Spielerliste waehrend einer Online-Runde.");

            foreach (string missing in GameBridge.MissingMembers())
            {
                Log.LogError($"Spielversion passt nicht zum Mod: {missing} fehlt. Online-Modus ist evtl. defekt.");
            }

            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);

            var host = new GameObject("OGMultiplayer");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<OnlineSession>();
            host.AddComponent<OnlineUi>();
            Log.LogInfo($"{Name} {Version} geladen (Protokoll v{OGMultiplayer.Protocol.Version}). {ToggleKey.Value} oeffnet das Online-Fenster.");
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
        }
    }
}
