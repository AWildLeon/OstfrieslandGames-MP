using System;
using System.Collections.Generic;
using System.Linq;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OGMultiplayer.Plugin
{
    public enum HudCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>IMGUI lobby window (toggle key, default F8) and the in-match status HUD.</summary>
    internal sealed class OnlineUi : MonoBehaviour
    {
        private const int WindowId = 0x4F474D50;

        public static bool WindowOpen { get; private set; }

        public static bool CapturesKeyboard => WindowOpen && GUIUtility.keyboardControl != 0;

        private Rect windowRect = new Rect(40, 40, 460, 0);
        private string host;
        private string portText;
        private string room;
        private string password;
        private string initials;
        private int characterIndex;
        private List<GameBridge.CharacterInfo> characters = new List<GameBridge.CharacterInfo>();
        private bool previousCursorVisible;
        private CursorLockMode previousLockState;
        private GUIStyle hudStyle;
        private GUIStyle centerStyle;
        private GUIStyle boxStyle;
        private GUIStyle windowStyle;
        private float scale = 1f;

        private static OnlineSession Session => OnlineSession.Instance;

        private void Start()
        {
            host = Plugin.ServerHost.Value;
            portText = Plugin.ServerPort.Value.ToString();
            room = Plugin.RoomName.Value;
            password = Plugin.ServerPassword.Value;
            initials = Plugin.Initials.Value;
        }

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.ToggleKey.Value))
            {
                SetWindow(!WindowOpen);
            }
            if (WindowOpen)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            if (characters.Count == 0)
            {
                RefreshCharacters();
            }
        }

        private void SetWindow(bool open)
        {
            if (open == WindowOpen)
            {
                return;
            }
            WindowOpen = open;
            if (open)
            {
                previousCursorVisible = Cursor.visible;
                previousLockState = Cursor.lockState;
                RefreshCharacters();
            }
            else
            {
                GUIUtility.keyboardControl = 0;
                Cursor.visible = previousCursorVisible;
                Cursor.lockState = previousLockState;
                SaveSettings();
            }
        }

        private void RefreshCharacters()
        {
            int before = characters.Count;
            characters = GameBridge.PlayableCharacters();
            if (characters.Count > 0 && before == 0)
            {
                Plugin.Log.LogInfo("Spielbare Figuren: " + string.Join(", ", characters.Select(c => $"{c.Id} ({c.Name})")));
            }
            int idx = characters.FindIndex(c => c.Id == Plugin.Character.Value);
            characterIndex = idx >= 0 ? idx : 0;
        }

        private void SaveSettings()
        {
            Plugin.ServerHost.Value = (host ?? string.Empty).Trim();
            if (int.TryParse(portText, out int port) && port > 0 && port < 65536)
            {
                Plugin.ServerPort.Value = port;
            }
            Plugin.RoomName.Value = (room ?? string.Empty).Trim().ToLowerInvariant();
            Plugin.ServerPassword.Value = password ?? string.Empty;
            Plugin.Initials.Value = (initials ?? string.Empty).Trim().ToUpperInvariant();
            if (characters.Count > 0)
            {
                Plugin.Character.Value = characters[characterIndex].Id;
            }
        }

        private void EnsureStyles()
        {
            scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
            if (hudStyle == null)
            {
                hudStyle = new GUIStyle(GUI.skin.label) { richText = true };
                centerStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true, fontStyle = FontStyle.Bold };
                boxStyle = new GUIStyle(GUI.skin.box);
                boxStyle.normal.background = MakeTexture(new Color(0f, 0f, 0f, 0.65f));
                Texture2D windowBackground = MakeTexture(new Color(0.08f, 0.1f, 0.13f, 0.96f));
                windowStyle = new GUIStyle(GUI.skin.window)
                {
                    padding = new RectOffset(12, 12, 28, 12),
                    fontStyle = FontStyle.Bold
                };
                windowStyle.normal.background = windowBackground;
                windowStyle.onNormal.background = windowBackground;
                windowStyle.focused.background = windowBackground;
                windowStyle.onFocused.background = windowBackground;
                windowStyle.normal.textColor = Color.white;
                windowStyle.onNormal.textColor = Color.white;
                windowStyle.focused.textColor = Color.white;
                windowStyle.onFocused.textColor = Color.white;
            }
            hudStyle.fontSize = Mathf.RoundToInt(18 * scale);
            centerStyle.fontSize = Mathf.RoundToInt(26 * scale);
        }

        private static Texture2D MakeTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private void OnGUI()
        {
            if (Session == null)
            {
                return;
            }
            EnsureStyles();
            GUI.depth = -1000;
            DrawHud();
            if (WindowOpen)
            {
                Matrix4x4 old = GUI.matrix;
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                windowRect = GUILayout.Window(WindowId, windowRect, DrawWindow, $"Ostfriesland Games - Online ({Plugin.ToggleKey.Value})", windowStyle);
                GUI.matrix = old;
            }
        }

        private void DrawHud()
        {
            if (!Session.Connected)
            {
                if (!WindowOpen && SceneManager.GetActiveScene().name == "StartScreen" && !HotSeatFlowController.IsActive)
                {
                    GUI.Label(new Rect(12 * scale, Screen.height - 34 * scale, 600 * scale, 30 * scale), $"<color=#ffffffaa>{Plugin.ToggleKey.Value}: Online-Mehrspieler</color>", hudStyle);
                }
                DrawToast();
                return;
            }

            // player list, top right
            var lines = new List<string> { $"<b>Online</b>  Raum <b>{Session.RoomName}</b>" };
            foreach (LobbyPlayer p in Session.Lobby)
            {
                string me = p.Id == Session.MyId ? " <color=#ffd84a>(du)</color>" : string.Empty;
                string host = p.Id == Session.HostId ? " <color=#9fd3ff>[Host]</color>" : string.Empty;
                lines.Add($"{p.Initials}{me}{host}  {StatusText(p)}");
            }
            string text = string.Join("\n", lines);
            float width = 380 * scale;
            float height = (lines.Count * 24 + 14) * scale;
            float margin = 12 * scale;
            HudCorner corner = Plugin.HudPosition.Value;
            float x = corner == HudCorner.TopLeft || corner == HudCorner.BottomLeft ? margin : Screen.width - width - margin;
            float y = corner == HudCorner.TopLeft || corner == HudCorner.TopRight ? margin : Screen.height - height - margin;
            var rect = new Rect(x, y, width, height);
            GUI.Box(rect, GUIContent.none, boxStyle);
            GUI.Label(new Rect(rect.x + 10 * scale, rect.y + 6 * scale, rect.width - 20 * scale, rect.height), text, hudStyle);

            string center = null;
            if (Session.WaitingForGo)
            {
                center = "Warte auf die anderen Spieler ...";
            }
            else if (Session.WaitingForResults)
            {
                center = "Fertig! Warte auf die Ergebnisse der anderen ...";
            }
            if (center != null)
            {
                var c = new Rect(0, Screen.height * 0.12f, Screen.width, 60 * scale);
                GUI.Box(new Rect(Screen.width * 0.2f, c.y, Screen.width * 0.6f, c.height), GUIContent.none, boxStyle);
                GUI.Label(c, center, centerStyle);
            }
            DrawToast();
        }

        private void DrawToast()
        {
            if (string.IsNullOrEmpty(Session.Message))
            {
                return;
            }
            var r = new Rect(Screen.width * 0.15f, Screen.height * 0.12f + 70 * scale, Screen.width * 0.7f, 50 * scale);
            GUI.Box(r, GUIContent.none, boxStyle);
            GUI.Label(r, Session.Message, centerStyle);
        }

        private static string StatusText(LobbyPlayer p)
        {
            switch (p.Status)
            {
                case Protocol.StatusLoading:
                    return "<color=#cccccc>laedt ...</color>";
                case Protocol.StatusReady:
                    return "<color=#cccccc>bereit</color>";
                case Protocol.StatusPlaying:
                    return p.Score > 0 ? $"spielt  <b>{p.Score}</b>" : "spielt";
                case Protocol.StatusDone:
                    return $"<color=#8dff8d>fertig  <b>{p.Score}</b></color>";
                case Protocol.StatusLeft:
                    return "<color=#ff8d8d>ausgestiegen</color>";
                default:
                    return "<color=#cccccc>Lobby</color>";
            }
        }

        private void DrawWindow(int id)
        {
            if (!Session.Connected)
            {
                DrawConnectForm();
            }
            else
            {
                DrawLobby();
            }
            if (!string.IsNullOrEmpty(Session.LastError))
            {
                GUILayout.Space(6);
                GUILayout.Label($"<color=#ff8080>{Session.LastError}</color>", hudStyleSmall());
            }
            GUILayout.Space(6);
            if (GUILayout.Button("Schliessen"))
            {
                SetWindow(false);
            }
            GUI.DragWindow();
        }

        private GUIStyle smallStyle;

        private GUIStyle hudStyleSmall()
        {
            if (smallStyle == null)
            {
                smallStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            }
            return smallStyle;
        }

        private void DrawConnectForm()
        {
            GUILayout.Label("Server");
            host = GUILayout.TextField(host ?? string.Empty, 128);
            GUILayout.Label("Port");
            portText = GUILayout.TextField(portText ?? string.Empty, 5);
            GUILayout.Label("Server-Passwort (leer = offener Server)");
            password = GUILayout.PasswordField(password ?? string.Empty, '*', 128);
            GUILayout.Label("Raum (alle Mitspieler gleicher Raum)");
            room = GUILayout.TextField(room ?? string.Empty, 32);
            GUILayout.Label("Initialen (3 Buchstaben)");
            initials = GUILayout.TextField(initials ?? string.Empty, 3).ToUpperInvariant();
            GUILayout.Label("Figur");
            DrawCharacterPicker();
            GUILayout.Space(10);
            GUI.enabled = !Session.Connecting;
            if (GUILayout.Button(Session.Connecting ? "Verbinde ..." : "Verbinden", GUILayout.Height(34)))
            {
                SaveSettings();
                if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
                {
                    Session.Toast("Ungueltiger Port.");
                }
                else
                {
                    Session.Connect(host, port, room, initials, characters.Count > 0 ? characters[characterIndex].Id : SelectedCharacter.DefaultId, password);
                }
            }
            GUI.enabled = true;
            if (Session.Connecting && GUILayout.Button("Abbrechen"))
            {
                Session.Disconnect();
            }
        }

        private void DrawCharacterPicker()
        {
            if (characters.Count == 0)
            {
                GUILayout.Label("<i>Figuren werden im Hauptmenue geladen ...</i>", hudStyleSmall());
                return;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(40)))
            {
                characterIndex = (characterIndex + characters.Count - 1) % characters.Count;
                OnCharacterChanged();
            }
            GUILayout.Label(characters[characterIndex].Name, centerSmall());
            if (GUILayout.Button(">", GUILayout.Width(40)))
            {
                characterIndex = (characterIndex + 1) % characters.Count;
                OnCharacterChanged();
            }
            GUILayout.EndHorizontal();
        }

        private GUIStyle centerSmallStyle;

        private GUIStyle centerSmall()
        {
            if (centerSmallStyle == null)
            {
                centerSmallStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            }
            return centerSmallStyle;
        }

        private void OnCharacterChanged()
        {
            Plugin.Character.Value = characters[characterIndex].Id;
            Session.ChangeCharacter(characters[characterIndex].Id);
        }

        private void DrawLobby()
        {
            GUILayout.Label($"Raum: <b>{Session.RoomName}</b>   ({Session.Lobby.Count}/{Protocol.MaxPlayers} Spieler)", hudStyleSmall());
            foreach (LobbyPlayer p in Session.Lobby)
            {
                string name = characters.FirstOrDefault(c => c.Id == p.Character)?.Name ?? p.Character;
                string tags = (p.Id == Session.HostId ? " [Host]" : string.Empty) + (p.Id == Session.MyId ? " (du)" : string.Empty);
                GUILayout.Label($"  {p.Initials}{tags} - {name}  {StatusText(p)}", hudStyleSmall());
            }
            GUILayout.Space(8);
            bool inMatch = Session.RoomState == Protocol.StateMatch;
            if (!inMatch)
            {
                GUILayout.Label("Figur");
                DrawCharacterPicker();
                GUILayout.Space(8);
            }
            if (inMatch)
            {
                GUILayout.Label("Eine Runde laeuft gerade.", hudStyleSmall());
            }
            else if (Session.IsHost)
            {
                if (Session.Lobby.Count < Protocol.MinPlayers)
                {
                    GUILayout.Label($"Warte auf Mitspieler (mind. {Protocol.MinPlayers}) ...", hudStyleSmall());
                }
                else
                {
                    GUILayout.Label("Disziplin starten:");
                    foreach (DisciplineId d in Enum.GetValues(typeof(DisciplineId)))
                    {
                        if (GUILayout.Button(GameBridge.DisciplineName(d), GUILayout.Height(30)))
                        {
                            Session.RequestStart(d);
                            SetWindow(false);
                        }
                    }
                }
            }
            else
            {
                GUILayout.Label($"Der Host ({Session.HostInitials()}) waehlt die Disziplin.", hudStyleSmall());
            }
            GUILayout.Space(8);
            if (GUILayout.Button("Trennen"))
            {
                Session.Disconnect();
            }
        }
    }
}
