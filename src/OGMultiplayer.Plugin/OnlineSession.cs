using System;
using System.Collections.Generic;
using System.Linq;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;

namespace OGMultiplayer.Plugin
{
    internal sealed class LobbyPlayer
    {
        public int Id;
        public string Initials;
        public string Character;
        public string Status;
        public int Score;
        public bool IsBot;
    }

    /// <summary>
    /// Online match = a hot seat match where every machine plays its own turn at the same
    /// time. The local player is always slot 1 of the local HotSeatMatch; remote players'
    /// scores are filled in when the server sends the result.
    /// </summary>
    internal sealed class OnlineSession : MonoBehaviour
    {
        public static OnlineSession Instance { get; private set; }

        /// <summary>Set while the mod itself calls patched game methods.</summary>
        public static bool Authorized { get; private set; }

        private ITransport net;
        private float lastPing;
        private float lastLive;
        private int lastLiveScore = -1;

        // lobby
        public bool Connecting { get; private set; }
        public bool Connected { get; private set; }
        public int MyId { get; private set; }
        public string RoomName { get; private set; } = string.Empty;
        public int HostId { get; private set; }
        public string RoomState { get; private set; } = Protocol.StateLobby;
        public List<LobbyPlayer> Lobby { get; } = new List<LobbyPlayer>();
        public bool IsHost => Connected && MyId != 0 && MyId == HostId;

        // match
        private HotSeatFlowController controller;
        private readonly List<int> slotIds = new List<int>();
        public int MatchId { get; private set; }
        public DisciplineId Discipline { get; private set; }
        public bool GoReceived { get; private set; }
        private bool readySent;
        private bool turnStarted;
        public bool LocalFinished { get; private set; }
        private bool leftMatch;

        // feedback
        public string Message { get; private set; }
        private float messageUntil;
        public string LastError { get; private set; }

        public static bool IsOnlineController(HotSeatFlowController c)
        {
            return Instance != null && c != null && Instance.controller == c;
        }

        /// <summary>True while connected/connecting or an online match is open.</summary>
        public static bool BlocksScoreApi => Instance != null && Plugin.BlockScoreApi.Value && (Instance.net is NetClient || (OnlineControllerActive && !Instance.IsOffline));

        /// <summary>Playing against bots without a server.</summary>
        public bool IsOffline => net is LocalBotServer;

        public int BotLevel { get; private set; } = Bots.DefaultLevel;

        public int BotCount => Lobby.Count(p => p.IsBot);

        public static bool OnlineControllerActive => Instance != null && Instance.controller != null && HotSeatFlowController.Instance == Instance.controller;

        public bool MatchRunning => OnlineControllerActive && MatchId != 0 && !leftMatch && controller.Match != null && !controller.Match.IsComplete;

        public bool WaitingForGo => MatchRunning && !GoReceived && controller.Phase == HotSeatPhase.Handoff;

        public bool WaitingForResults => MatchRunning && LocalFinished;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            net?.Dispose();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            net?.Dispose();
        }

        public static void RunAuthorized(Action action)
        {
            bool previous = Authorized;
            Authorized = true;
            try
            {
                action();
            }
            finally
            {
                Authorized = previous;
            }
        }

        public void Toast(string text, float seconds = 4f)
        {
            Message = text;
            messageUntil = Time.unscaledTime + seconds;
        }

        // ------------------------------------------------------------------ connection

        public void Connect(string host, int port, string room, string initials, string character, string password)
        {
            Disconnect();
            string ini = Protocol.NormalizeInitials(initials);
            string roomId = Protocol.NormalizeId(room);
            string charId = Protocol.NormalizeId(character);
            if (ini == null)
            {
                LastError = "Initialen: genau drei Buchstaben A-Z.";
                return;
            }
            if (InitialsEntryOverlay.IsBlocked(ini))
            {
                LastError = "Bitte andere Initialen waehlen.";
                return;
            }
            if (roomId == null)
            {
                LastError = "Raumname: 1-32 Zeichen a-z, 0-9, - oder _.";
                return;
            }
            if (charId == null)
            {
                LastError = "Bitte eine Figur waehlen.";
                return;
            }
            LastError = null;
            Connecting = true;
            pendingHello = new[] { ini, charId, roomId };
            pendingPassword = password ?? string.Empty;
            var client = new NetClient();
            net = client;
            client.Connect(host.Trim(), port);
            lastPing = Time.unscaledTime;
            Plugin.Log.LogInfo($"Connecting to {host}:{port}, room '{roomId}' as {ini}");
        }

        /// <summary>Starts the offline mode against bots (local stand-in server).</summary>
        public void StartOffline(string initials, string character)
        {
            Disconnect();
            string ini = Protocol.NormalizeInitials(initials);
            if (ini == null || InitialsEntryOverlay.IsBlocked(ini))
            {
                ini = "ICH";
            }
            string charId = Protocol.NormalizeId(character) ?? SelectedCharacter.DefaultId;
            LastError = null;
            Connecting = true;
            pendingHello = new[] { ini, charId, "offline" };
            pendingPassword = string.Empty;
            net = new LocalBotServer(GameBridge.LocalScores);
            Plugin.Log.LogInfo("Offline-Modus gegen Bots gestartet");
        }

        public void AddBot() => SendBot(Protocol.BotAdd);

        public void RemoveBot() => SendBot(Protocol.BotRemove);

        public void SetBotLevel(int level) => SendBot(Protocol.BotLevel, Protocol.Int(Bots.ClampLevel(level)));

        private void SendBot(params string[] args)
        {
            if (!Connected)
            {
                return;
            }
            if (!IsHost)
            {
                Toast($"Nur der Host ({HostInitials()}) verwaltet Bots.");
                return;
            }
            Send(new[] { Protocol.Bot }.Concat(args).ToArray());
        }

        private string[] pendingHello;
        private string pendingPassword;

        /// <summary>Answers the server's CHALLENGE with HELLO and the password proof.</summary>
        private void HandleChallenge(string[] f)
        {
            if (pendingHello == null)
            {
                return;
            }
            if (!Protocol.TryInt(f.ElementAtOrDefault(1), out int version) || version != Protocol.Version)
            {
                FailConnect($"Server nutzt Protokoll v{f.ElementAtOrDefault(1)}, Mod v{Protocol.Version}. Bitte aktualisieren.");
                return;
            }
            string nonce = f.ElementAtOrDefault(2) ?? string.Empty;
            bool required = f.ElementAtOrDefault(3) == "1";
            if (required && string.IsNullOrEmpty(pendingPassword))
            {
                FailConnect("Der Server verlangt ein Passwort.");
                return;
            }
            string proof = required ? Protocol.AuthProof(pendingPassword, nonce) : string.Empty;
            Send(Protocol.Hello, Protocol.Int(Protocol.Version), pendingHello[0], pendingHello[1], pendingHello[2], proof);
            pendingHello = null;
            pendingPassword = null;
        }

        private void FailConnect(string message)
        {
            LastError = message;
            net?.Dispose();
            net = null;
            ResetConnectionState();
        }

        public void Disconnect()
        {
            if (net == null)
            {
                return;
            }
            LeaveMatch();
            net.Dispose();
            net = null;
            ResetConnectionState();
        }

        private void ResetConnectionState()
        {
            pendingHello = null;
            pendingPassword = null;
            Connected = false;
            Connecting = false;
            MyId = 0;
            HostId = 0;
            RoomName = string.Empty;
            RoomState = Protocol.StateLobby;
            Lobby.Clear();
            if (OnlineControllerActive)
            {
                HotSeatFlowController.AbortToMenu();
            }
            MatchId = 0;
        }

        private void Send(params string[] fields)
        {
            net?.Send(Protocol.Encode(fields));
        }

        public void ChangeCharacter(string character)
        {
            string id = Protocol.NormalizeId(character);
            if (Connected && id != null)
            {
                Send(Protocol.Char, id);
            }
        }

        public void RequestStart(DisciplineId discipline)
        {
            if (!Connected)
            {
                return;
            }
            if (!IsHost)
            {
                Toast($"Nur der Host ({HostInitials()}) kann eine Disziplin starten.");
                return;
            }
            if (Lobby.Count < Protocol.MinPlayers)
            {
                Toast($"Mindestens {Protocol.MinPlayers} Spieler noetig.");
                return;
            }
            Send(Protocol.Start, Protocol.Int((int)discipline));
        }

        /// <summary>One line describing what the lobby is waiting for (used by menu and HUD).</summary>
        public string LobbyStatus(bool brief = false)
        {
            string dots = new string('.', 1 + (int)(Time.unscaledTime * 2f) % 3).PadRight(3);
            if (!Connected)
            {
                return "VERBINDE " + dots;
            }
            if (RoomState == Protocol.StateMatch)
            {
                return "RUNDE LAEUFT " + dots;
            }
            if (Lobby.Count < Protocol.MinPlayers)
            {
                return (IsOffline ? "FUEGE BOTS HINZU " : brief ? "WARTE AUF SPIELER " : "WARTE AUF ANDERE SPIELER ") + dots;
            }
            if (!IsHost)
            {
                return (brief ? $"WARTE AUF {HostInitials()} " : $"WARTE AUF DEN HOST ({HostInitials()}) ") + dots;
            }
            return brief ? "BEREIT ZUM START" : "BEREIT - DU STARTEST DIE RUNDE";
        }

        public string HostInitials()
        {
            return Lobby.FirstOrDefault(p => p.Id == HostId)?.Initials ?? "?";
        }

        // ------------------------------------------------------------------ main loop

        private void Update()
        {
            GameBridge.CacheMenuAssets();
            net?.Tick();
            if (net != null)
            {
                while (net != null && net.TryReceive(out string line))
                {
                    try
                    {
                        HandleLine(line);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"Fehler bei Nachricht '{line}': {ex}");
                    }
                }
                if (net != null && Connected && Time.unscaledTime - lastPing > 5f)
                {
                    lastPing = Time.unscaledTime;
                    Send(Protocol.Ping);
                }
            }
            if (Connected)
            {
                AttractDemoController.NotifyHotSeatActivity();
            }
            if ((object)controller != null && (controller == null || HotSeatFlowController.Instance != controller))
            {
                // controller got closed (abort to menu, kiosk reset, ...)
                LeaveMatch();
                controller = null;
            }
            DriveMatch();
            if (Message != null && Time.unscaledTime > messageUntil)
            {
                Message = null;
            }
        }

        private void DriveMatch()
        {
            if (!MatchRunning)
            {
                return;
            }
            if (GoReceived && !turnStarted && GameBridge.IsWaitingForTurnStart(controller))
            {
                turnStarted = true;
                RunAuthorized(controller.StartTurn);
            }
            if (controller.Phase == HotSeatPhase.Playing && !LocalFinished && Time.unscaledTime - lastLive > 1f)
            {
                lastLive = Time.unscaledTime;
                int live = GameBridge.LiveScore(Discipline);
                if (live >= 0 && live != lastLiveScore)
                {
                    lastLiveScore = live;
                    Send(Protocol.Live, Protocol.Int(MatchId), Protocol.Int(live));
                }
            }
        }

        private void HandleLine(string line)
        {
            if (line == NetClient.DisconnectedMarker)
            {
                string error = net?.LastError;
                Plugin.Log.LogWarning("Verbindung getrennt" + (error != null ? ": " + error : string.Empty));
                if (LastError == null)
                {
                    LastError = Connected ? "Verbindung zum Server verloren." : "Verbindung fehlgeschlagen" + (error != null ? ": " + error : ".");
                }
                LeaveMatch();
                net?.Dispose();
                net = null;
                ResetConnectionState();
                return;
            }
            string[] f = Protocol.Decode(line);
            if (f.Length == 0)
            {
                return;
            }
            switch (f[0])
            {
                case Protocol.Challenge:
                    HandleChallenge(f);
                    break;
                case Protocol.Welcome:
                    Protocol.TryInt(f.ElementAtOrDefault(1), out int id);
                    MyId = id;
                    Connected = true;
                    Connecting = false;
                    LastError = null;
                    Plugin.Log.LogInfo($"Verbunden, Spieler-ID {id}");
                    break;
                case Protocol.Error:
                    LastError = f.ElementAtOrDefault(1) ?? "Fehler";
                    Toast(LastError);
                    break;
                case Protocol.Msg:
                    Plugin.Log.LogInfo("Server: " + f.ElementAtOrDefault(1));
                    Toast("Server: " + (f.ElementAtOrDefault(1) ?? string.Empty), 8f);
                    break;
                case Protocol.Bye:
                    LastError = f.ElementAtOrDefault(1) ?? "Vom Server getrennt.";
                    Toast(LastError, 8f);
                    break;
                case Protocol.Lobby:
                    HandleLobby(f);
                    break;
                case Protocol.Begin:
                    HandleBegin(f);
                    break;
                case Protocol.Go:
                    if (Protocol.TryInt(f.ElementAtOrDefault(1), out int goId) && goId == MatchId)
                    {
                        GoReceived = true;
                    }
                    break;
                case Protocol.Result:
                    HandleResult(f);
                    break;
            }
        }

        private void HandleLobby(string[] f)
        {
            if (f.Length < 4)
            {
                return;
            }
            RoomName = f[1];
            Protocol.TryInt(f[2], out int host);
            HostId = host;
            RoomState = f[3];
            if (f.Length > 4 && Protocol.TryInt(f[4], out int level))
            {
                BotLevel = level;
            }
            Lobby.Clear();
            for (int i = 5; i < f.Length; i++)
            {
                string[] p = Protocol.SplitPart(f[i]);
                if (p.Length < 5 || !Protocol.TryInt(p[0], out int pid))
                {
                    continue;
                }
                Protocol.TryInt(p[4], out int score);
                Lobby.Add(new LobbyPlayer { Id = pid, Initials = p[1], Character = p[2], Status = p[3], Score = score, IsBot = p.Length > 5 && p[5] == "1" });
            }
        }

        private void HandleBegin(string[] f)
        {
            if (f.Length < 3 || !Protocol.TryInt(f[1], out int matchId) || !Protocol.TryInt(f[2], out int disc)
                || !Enum.IsDefined(typeof(DisciplineId), disc))
            {
                return;
            }
            var remote = new List<KeyValuePair<int, HotSeatPlayer>>();
            for (int i = 3; i < f.Length; i++)
            {
                string[] p = Protocol.SplitPart(f[i]);
                if (p.Length >= 3 && Protocol.TryInt(p[0], out int pid))
                {
                    remote.Add(new KeyValuePair<int, HotSeatPlayer>(pid, new HotSeatPlayer(0, p[1], p[2])));
                }
            }
            if (!remote.Any(r => r.Key == MyId))
            {
                return;
            }
            MatchId = matchId;
            Discipline = (DisciplineId)disc;
            GoReceived = false;
            readySent = false;
            turnStarted = false;
            LocalFinished = false;
            leftMatch = false;
            lastLiveScore = -1;

            if (RunFlowController.IsActive)
            {
                Toast("Story-Lauf aktiv - Runde wird ausgelassen. Bitte ins Hauptmenue wechseln.", 6f);
                LeaveMatch();
                return;
            }

            // local player first, everyone else in server order
            var ordered = remote.Where(r => r.Key == MyId).Concat(remote.Where(r => r.Key != MyId)).ToList();
            HashSet<string> allowed = new HashSet<string>(GameBridge.PlayableCharacters().Select(c => c.Id));
            string fallback = allowed.Contains(SelectedCharacter.DefaultId) ? SelectedCharacter.DefaultId : allowed.FirstOrDefault();
            slotIds.Clear();
            var players = new List<HotSeatPlayer>();
            foreach (KeyValuePair<int, HotSeatPlayer> r in ordered)
            {
                string ini = Protocol.NormalizeInitials(r.Value.Initials);
                if (ini == null || InitialsEntryOverlay.IsBlocked(ini))
                {
                    ini = "AAA";
                }
                string character = allowed.Contains(r.Value.CharacterId) ? r.Value.CharacterId : fallback;
                players.Add(new HotSeatPlayer(0, ini, character));
                slotIds.Add(r.Key);
            }

            try
            {
                controller = GameBridge.CreateController();
                controller.ConfigurePlayers(players);
                DisciplineId d = Discipline;
                RunAuthorized(() => controller.BeginMatch(d, 1));
                Plugin.Log.LogInfo($"Online-Runde {matchId}: {GameBridge.DisciplineName(d)} mit {players.Count} Spielern");
                Toast($"Runde startet: {GameBridge.DisciplineName(d)}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Online-Runde konnte nicht gestartet werden: " + ex);
                Toast("Runde konnte nicht gestartet werden: " + ex.Message, 6f);
                LeaveMatch();
                if (controller != null)
                {
                    UnityEngine.Object.Destroy(controller.gameObject);
                    controller = null;
                }
            }
        }

        private void HandleResult(string[] f)
        {
            if (f.Length < 2 || !Protocol.TryInt(f[1], out int matchId) || matchId != MatchId)
            {
                return;
            }
            var scores = new Dictionary<int, int>();
            for (int i = 2; i < f.Length; i++)
            {
                string[] p = Protocol.SplitPart(f[i]);
                if (p.Length >= 2 && Protocol.TryInt(p[0], out int pid) && Protocol.TryInt(p[1], out int pts))
                {
                    scores[pid] = Math.Max(0, pts);
                }
            }
            bool showInGame = OnlineControllerActive && !leftMatch && controller.Match != null && !controller.Match.IsComplete;
            MatchId = 0;
            if (!showInGame)
            {
                string summary = string.Join(", ", Lobby.Select(p => $"{p.Initials} {(scores.TryGetValue(p.Id, out int s) ? s : 0)}"));
                Toast("Ergebnis: " + summary, 8f);
                return;
            }
            HotSeatMatch match = controller.Match;
            int guard = 0;
            while (!match.IsComplete && guard++ < 16)
            {
                int slot = match.CurrentPlayerIndex;
                int pid = slot < slotIds.Count ? slotIds[slot] : 0;
                int score = scores.TryGetValue(pid, out int s) ? s : 0;
                match.TryRecordResult(match.CurrentTurn, score);
            }
            GameBridge.ShowResults(controller);
        }

        // ------------------------------------------------------------------ hooks from patches

        public void OnAwaitingTurnStart()
        {
            if (MatchRunning && !readySent)
            {
                readySent = true;
                Send(Protocol.Ready, Protocol.Int(MatchId));
            }
        }

        public void OnLocalTurnFinished(int score)
        {
            if (!MatchRunning || LocalFinished)
            {
                return;
            }
            LocalFinished = true;
            Send(Protocol.Score, Protocol.Int(MatchId), Protocol.Int(Math.Max(0, score)));
            Plugin.Log.LogInfo($"Eigene Runde beendet: {score} Punkte");
        }

        public void LeaveMatch()
        {
            if (MatchId != 0 && !leftMatch && !LocalFinished)
            {
                Send(Protocol.Leave, Protocol.Int(MatchId));
            }
            leftMatch = true;
        }

        public string PlayerName(int slot)
        {
            if (slot < 0 || slot >= slotIds.Count)
            {
                return null;
            }
            return Lobby.FirstOrDefault(p => p.Id == slotIds[slot])?.Initials;
        }
    }
}
