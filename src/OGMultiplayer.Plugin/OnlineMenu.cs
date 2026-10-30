using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OGMultiplayer.Plugin
{
    /// <summary>
    /// Online pages in the style of the game's own menus. Our own widgets; the look comes from
    /// the game at runtime (start menu canvas and arrow, font, text style and selection
    /// effect, pause-menu theme helpers), driven by keyboard, gamepad and mouse.
    /// </summary>
    internal sealed class OnlineMenu : MonoBehaviour
    {
        private enum Page
        {
            Choice,
            Connect,
            Lobby
        }

        private enum Field
        {
            None,
            Server,
            Password,
            Room,
            Initials
        }

        private sealed class Row
        {
            public Image Background;
            public Text Label;
            public StartScreenMenuTextEffect Effect;
            public Func<string> Text;
            public Action Submit;
            public Action<int> Horizontal;
            public Field Edit;
            public Image Portrait;
            public Func<string> PortraitId;
            public RectTransform ArrowLeft;
            public RectTransform ArrowRight;
        }

        private static readonly DisciplineId[] Disciplines =
        {
            DisciplineId.Padstock,
            DisciplineId.Teezeremonie,
            DisciplineId.Bosseln,
            DisciplineId.Krabbenpulen,
            DisciplineId.Schlick
        };

        public static OnlineMenu Instance { get; private set; }

        public static bool Visible => Instance != null && Instance.visible;

        /// <summary>The start screen menu must ignore input until this time (after the page closed).</summary>
        public static float MenuInputBlockedUntil { get; private set; }

        private static OnlineSession Session => OnlineSession.Instance;

        private Canvas canvas;
        private RectTransform pageRoot;
        private Image pointer;
        private StartScreenMenuController startMenu;
        private Font font;
        /// <summary>Start menu units per canvas unit (its style values are in its own units).</summary>
        private float styleScale = 20f / 27f;
        private bool visible;
        private Page page;
        private int selection;
        private float openedAt;
        private bool armed;
        private int handledFrame = -1;
        private readonly List<Row> rows = new List<Row>();
        private Text subtitle;
        private Text footer;
        private readonly List<Text> playerLines = new List<Text>();
        private readonly List<Image> playerPortraits = new List<Image>();
        private string renderedSignature;

        // settings being edited
        private string host;
        private string port;
        private string password;
        private string room;
        private string initials;
        private int characterIndex;
        private int disciplineIndex;
        private List<CharacterSelect2DConfig.CharacterEntry> characters = new List<CharacterSelect2DConfig.CharacterEntry>();

        // inline text editing
        private Field editing = Field.None;
        private string editBuffer = string.Empty;
        private int initialsCursor;


        private void Awake()
        {
            Instance = this;
            host = Plugin.ServerHost.Value;
            port = Plugin.ServerPort.Value.ToString();
            password = Plugin.ServerPassword.Value;
            room = Plugin.RoomName.Value;
            initials = Plugin.Initials.Value;
            BuildCanvas();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ------------------------------------------------------------------ open / close

        /// <summary>Called instead of the game's MEHRSPIELER action.</summary>
        public void ShowChoice()
        {
            Show(Page.Choice, 0);
        }

        /// <summary>Opens the online page (lobby when connected).</summary>
        public void ShowOnline()
        {
            Show(Session != null && (Session.Connected || Session.Connecting) && !Session.IsOffline ? Page.Lobby : Page.Connect, 0);
        }

        private void Show(Page next, int select)
        {
            if (SceneManager.GetActiveScene().name != "StartScreen" || HotSeatFlowController.IsActive)
            {
                return;
            }
            LoadCharacters();
            StartScreenCameraScroller scroller = FindAnyObjectByType<StartScreenCameraScroller>();
            if (scroller != null)
            {
                scroller.SnapToRight();
            }
            AttachToStartMenu();
            page = next;
            selection = select;
            editing = Field.None;
            visible = true;
            // whatever opened the page may still be held down
            armed = false;
            openedAt = Time.unscaledTime;
            handledFrame = Time.frameCount;
            canvas.gameObject.SetActive(true);
            if (EventSystem.current == null)
            {
                new GameObject("OnlineMenuEvents", typeof(EventSystem), typeof(StandaloneInputModule)).transform.SetParent(canvas.transform, false);
            }
            Render();
        }

        public void Hide()
        {
            if (!visible)
            {
                return;
            }
            visible = false;
            editing = Field.None;
            canvas.gameObject.SetActive(false);
            MenuInputBlockedUntil = Time.unscaledTime + 0.3f;
            SaveSettings();
        }

        private void LoadCharacters()
        {
            characters = (GameBridge.Config?.selection?.characters ?? new CharacterSelect2DConfig.CharacterEntry[0])
                .Where(c => c != null && c.availability == CharacterSelect2DConfig.CharacterAvailability.Playable && !string.IsNullOrEmpty(c.id))
                .GroupBy(c => c.id).Select(g => g.First()).ToList();
            int idx = characters.FindIndex(c => c.id == Plugin.Character.Value);
            characterIndex = idx >= 0 ? idx : 0;
        }

        private void SaveSettings()
        {
            Plugin.ServerHost.Value = (host ?? string.Empty).Trim();
            if (int.TryParse(port, out int p) && p > 0 && p < 65536)
            {
                Plugin.ServerPort.Value = p;
            }
            Plugin.ServerPassword.Value = password ?? string.Empty;
            Plugin.RoomName.Value = (room ?? string.Empty).Trim().ToLowerInvariant();
            Plugin.Initials.Value = (initials ?? string.Empty).Trim().ToUpperInvariant();
            if (characters.Count > 0)
            {
                Plugin.Character.Value = characters[characterIndex].id;
            }
        }

        // ------------------------------------------------------------------ canvas

        private void BuildCanvas()
        {
            var root = new GameObject("OnlineMenuCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.transform.SetParent(transform, false);
            canvas = root.GetComponent<Canvas>();
            // the game's own overlay pages work in a 1280x800 space; so does this one
            PauseMenuTheme.NormalizeScaler(root);
            // clicks next to the rows must not reach the start menu underneath
            Image shield = NewImage(root.transform, "ClickShield", Color.clear, Vector2.zero, Vector2.zero);
            shield.raycastTarget = true;
            Fill(shield.rectTransform);
            pageRoot = new GameObject("Page", typeof(RectTransform)).GetComponent<RectTransform>();
            pageRoot.SetParent(root.transform, false);
            Fill(pageRoot);
            pointer = MenuSelectionArrow.Create(root.transform);
            pointer.raycastTarget = false;
            root.SetActive(false);
        }

        /// <summary>
        /// Renders through the same canvas setup as the start menu (same camera, so its pixel
        /// look applies), one step in front of it, with the start menu's own arrow and scale.
        /// </summary>
        private void AttachToStartMenu()
        {
            font = GameBridge.Font;
            startMenu = FindAnyObjectByType<StartScreenMenuController>();
            Canvas menuCanvas = startMenu != null ? startMenu.GetComponentInParent<Canvas>() : null;
            if (menuCanvas != null && menuCanvas.renderMode == RenderMode.ScreenSpaceCamera && menuCanvas.worldCamera != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = menuCanvas.worldCamera;
                canvas.planeDistance = menuCanvas.planeDistance;
                canvas.sortingLayerID = menuCanvas.sortingLayerID;
                canvas.sortingOrder = menuCanvas.sortingOrder + 1;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 501;
            }
            CanvasScaler menuScaler = startMenu != null ? startMenu.GetComponentInParent<CanvasScaler>() : null;
            if (menuScaler != null && menuScaler.referenceResolution.y > 0f)
            {
                // same aspect and matching as the start menu, expressed in 800 units of height
                Vector2 menuRef = menuScaler.referenceResolution;
                CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                scaler.referenceResolution = new Vector2(800f * menuRef.x / menuRef.y, 800f);
                scaler.matchWidthOrHeight = menuScaler.matchWidthOrHeight;
                styleScale = 800f / menuRef.y;
            }
            Image menuArrow = startMenu != null && startMenu.selectionArrow != null ? startMenu.selectionArrow.GetComponent<Image>() : null;
            if (menuArrow != null && menuArrow.sprite != null)
            {
                pointer.sprite = menuArrow.sprite;
                pointer.color = menuArrow.color;
                pointer.rectTransform.sizeDelta = menuArrow.rectTransform.sizeDelta * styleScale;
            }
        }

        // ------------------------------------------------------------------ pages

        private void Render()
        {
            foreach (Transform child in pageRoot)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
            rows.Clear();
            playerLines.Clear();
            playerPortraits.Clear();
            subtitle = null;
            footer = null;
            switch (page)
            {
                case Page.Choice:
                    RenderChoice();
                    break;
                case Page.Connect:
                    RenderConnect();
                    break;
                case Page.Lobby:
                    RenderLobby();
                    break;
            }
            selection = Mathf.Clamp(selection, 0, Mathf.Max(0, rows.Count - 1));
            renderedSignature = Signature();
            Refresh();
        }

        private void RenderChoice()
        {
            Header("MEHRSPIELER", "WIE WOLLT IHR SPIELEN?");
            float y = 120f;
            AddRow("Local", () => "AN EINEM GERAET", new Vector2(0f, y), () =>
            {
                Hide();
                StartScreenMenuController menu = FindAnyObjectByType<StartScreenMenuController>();
                if (menu != null)
                {
                    OnlineSession.RunAuthorized(menu.OpenMultiplayer);
                }
            });
            AddRow("Online", () => "ONLINE", new Vector2(0f, y - 76f), () => Show((Session.Connected || Session.Connecting) && !Session.IsOffline ? Page.Lobby : Page.Connect, 0));
            AddRow("Bots", () => "GEGEN BOTS", new Vector2(0f, y - 152f), StartBots);
            AddRow("Back", () => "ZURUECK", new Vector2(0f, y - 228f), Hide);
            PageFooter("ONLINE UND GEGEN BOTS: JEDER SPIELT AUF SEINEM EIGENEN GERAET, ALLE GLEICHZEITIG.");
        }

        private void RenderConnect()
        {
            Header("ONLINE", "MIT EINEM SERVER VERBINDEN");
            float y = 178f;
            const float step = 60f;
            AddRow("Server", () => "SERVER: " + Shown(Field.Server, host + ":" + port), new Vector2(0f, y), () => BeginEdit(Field.Server), edit: Field.Server);
            AddRow("Password", () => "PASSWORT: " + (editing == Field.Password ? Mask(editBuffer) + Cursor() : string.IsNullOrEmpty(password) ? "KEINS" : Mask(password)),
                new Vector2(0f, y - step), () => BeginEdit(Field.Password), edit: Field.Password);
            AddRow("Room", () => "RAUM: " + Shown(Field.Room, room).ToUpperInvariant(), new Vector2(0f, y - 2 * step), () => BeginEdit(Field.Room), edit: Field.Room);
            AddRow("Initials", () => "INITIALEN: " + (editing == Field.Initials ? InitialsEditText() : string.IsNullOrEmpty(initials) ? "---" : initials),
                new Vector2(0f, y - 3 * step), () => BeginEdit(Field.Initials), edit: Field.Initials);
            AddCharacterRow(new Vector2(0f, y - 4 * step));
            AddRow("Connect", () => Session.Connecting ? "VERBINDE ..." : "VERBINDEN", new Vector2(0f, y - 5 * step), Connect);
            AddRow("Back", () => "ZURUECK", new Vector2(0f, y - 6 * step), () => Show(Page.Choice, 1));
            PageFooter(string.Empty);
        }

        private void StartBots()
        {
            if (!(Session.IsOffline && Session.Connected))
            {
                SaveSettings();
                Session.StartOffline(initials, characters.Count > 0 ? characters[characterIndex].id : SelectedCharacter.DefaultId);
            }
            Show(Page.Lobby, 0);
        }

        private void RenderLobby()
        {
            bool inMatch = Session.RoomState == Protocol.StateMatch;
            Header(Session.IsOffline ? "GEGEN BOTS" : "ONLINE", string.Empty);
            float y = 190f;
            for (int i = 0; i < Protocol.MaxPlayers; i++)
            {
                float rowY = y - i * 42f;
                Image portrait = NewImage(pageRoot, "PlayerPortrait" + i, Color.white, new Vector2(-330f, rowY), new Vector2(34f, 40f));
                portrait.preserveAspect = true;
                portrait.enabled = false;
                playerPortraits.Add(portrait);
                Text line = NewText(pageRoot, "Player" + i, string.Empty, 20, Color.white, new Vector2(40f, rowY), new Vector2(680f, 40f));
                line.alignment = TextAnchor.MiddleLeft;
                playerLines.Add(line);
            }
            float c = 0f;
            const float step = 50f;
            int n = 0;
            if (!inMatch && Session.IsHost)
            {
                AddRow("Discipline", () => DisciplineName(Disciplines[disciplineIndex]), new Vector2(0f, c - step * n++), StartMatch,
                    horizontal: d => disciplineIndex = (disciplineIndex + Disciplines.Length + d) % Disciplines.Length);
                AddRow("Start", () => "RUNDE STARTEN", new Vector2(0f, c - step * n++), StartMatch);
                AddRow("BotCount", () => "BOTS: " + Session.BotCount, new Vector2(0f, c - step * n++), Session.AddBot,
                    horizontal: d =>
                    {
                        if (d > 0)
                        {
                            Session.AddBot();
                        }
                        else
                        {
                            Session.RemoveBot();
                        }
                    });
                AddRow("BotLevel", () => "BOT-STAERKE: " + Bots.LevelName(Session.BotLevel), new Vector2(0f, c - step * n++),
                    () => Session.SetBotLevel(Session.BotLevel % Bots.MaxLevel + 1),
                    horizontal: d => Session.SetBotLevel(Bots.ClampLevel(Session.BotLevel + d)));
            }
            if (!inMatch)
            {
                AddCharacterRow(new Vector2(0f, c - step * n++));
            }
            bool offline = Session.IsOffline;
            AddRow("Disconnect", () => offline ? "BEENDEN" : "TRENNEN", new Vector2(0f, c - step * n++), () =>
            {
                Session.Disconnect();
                Show(offline ? Page.Choice : Page.Connect, offline ? 2 : 0);
            });
            AddRow("Back", () => "ZURUECK", new Vector2(0f, c - step * n++), Hide);
            PageFooter(string.Empty);
        }

        private void AddCharacterRow(Vector2 position)
        {
            Row row = AddRow("Character", () => "FIGUR: " + CharacterName(), position, () => ChangeCharacter(1), horizontal: ChangeCharacter);
            row.Portrait = NewImage(pageRoot, "CharacterPortrait", Color.white, position + new Vector2(380f, 0f), new Vector2(42f, 50f));
            row.Portrait.preserveAspect = true;
            row.PortraitId = () => characters.Count > 0 ? characters[characterIndex].id : null;
        }

        private void ChangeCharacter(int direction)
        {
            if (characters.Count == 0)
            {
                return;
            }
            characterIndex = (characterIndex + characters.Count + direction) % characters.Count;
            Plugin.Character.Value = characters[characterIndex].id;
            if (Session.Connected)
            {
                Session.ChangeCharacter(characters[characterIndex].id);
            }
        }

        private string CharacterName()
        {
            if (characters.Count == 0)
            {
                return "?";
            }
            CharacterSelect2DConfig.CharacterEntry c = characters[characterIndex];
            return (string.IsNullOrWhiteSpace(c.displayName) ? c.id : c.displayName).ToUpperInvariant();
        }

        private void Connect()
        {
            if (Session.Connecting)
            {
                return;
            }
            SaveSettings();
            if (!int.TryParse(port, out int p) || p < 1 || p > 65535)
            {
                Session.Toast("UNGUELTIGER PORT.");
                return;
            }
            Session.Connect(host, p, room, initials, characters.Count > 0 ? characters[characterIndex].id : SelectedCharacter.DefaultId, password);
        }

        private void StartMatch()
        {
            Session.RequestStart(Disciplines[disciplineIndex]);
        }

        // ------------------------------------------------------------------ dynamic text

        /// <summary>Changes whenever the page layout must be rebuilt.</summary>
        private string Signature()
        {
            if (Session == null)
            {
                return string.Empty;
            }
            return $"{page}|{Session.Connected}|{Session.IsHost}|{Session.RoomState}|{Session.IsOffline}";
        }

        private void Refresh()
        {
            foreach (Row row in rows)
            {
                string text = row.Text();
                if (row.Effect != null)
                {
                    PauseMenuTheme.SetLabel(row.Label, text);
                }
                else
                {
                    row.Label.text = text;
                }
                if (row.Portrait != null)
                {
                    SetPortrait(row.Portrait, row.PortraitId());
                }
            }
            if (page == Page.Lobby && Session != null && !Session.Connected)
            {
                if (subtitle != null)
                {
                    subtitle.text = "VERBINDE ...";
                }
                if (footer != null)
                {
                    footer.text = string.Empty;
                }
            }
            else if (page == Page.Lobby && Session != null)
            {
                if (subtitle != null)
                {
                    subtitle.text = Session.IsOffline
                        ? $"{Session.Lobby.Count}/{Protocol.MaxPlayers} SPIELER  -  BOTS: {Bots.LevelName(Session.BotLevel)}"
                        : $"RAUM {Session.RoomName.ToUpperInvariant()}  -  {Session.Lobby.Count}/{Protocol.MaxPlayers} SPIELER";
                }
                for (int i = 0; i < playerLines.Count; i++)
                {
                    LobbyPlayer p = i < Session.Lobby.Count ? Session.Lobby[i] : null;
                    playerLines[i].text = p == null ? string.Empty : PlayerLine(p);
                    SetPortrait(playerPortraits[i], p?.Character);
                }
                if (footer != null)
                {
                    footer.text = Session.RoomState == Protocol.StateMatch ? "EINE RUNDE LAEUFT GERADE."
                        : Session.IsHost ? (Session.Lobby.Count < Protocol.MinPlayers ? "MIND. 2 SPIELER: MITSPIELER ABWARTEN ODER BOTS HINZUFUEGEN." : "LINKS/RECHTS: WERT AENDERN")
                        : $"{Session.HostInitials()} WAEHLT DIE DISZIPLIN.";
                }
            }
            if (page == Page.Connect && footer != null)
            {
                footer.text = editing != Field.None ? EditHint() : (Session.LastError ?? string.Empty).ToUpperInvariant();
            }
        }

        private string PlayerLine(LobbyPlayer p)
        {
            string tags = (p.IsBot ? " BOT" : string.Empty) + (p.Id == Session.HostId && !Session.IsOffline ? " HOST" : string.Empty) + (p.Id == Session.MyId ? " (DU)" : string.Empty);
            string status;
            switch (p.Status)
            {
                case Protocol.StatusLoading:
                    status = "LAEDT";
                    break;
                case Protocol.StatusReady:
                    status = "BEREIT";
                    break;
                case Protocol.StatusPlaying:
                    status = p.Score > 0 ? "SPIELT " + p.Score : "SPIELT";
                    break;
                case Protocol.StatusDone:
                    status = "FERTIG " + p.Score;
                    break;
                case Protocol.StatusLeft:
                    status = "RAUS";
                    break;
                default:
                    status = string.Empty;
                    break;
            }
            return $"{p.Initials}{tags}".PadRight(14) + status;
        }

        private void SetPortrait(Image image, string id)
        {
            CharacterSelect2DConfig.CharacterEntry entry = id == null ? null : characters.FirstOrDefault(c => c.id == id);
            Sprite sprite = entry == null ? null : entry.portrait != null ? entry.portrait : entry.bigPortrait;
            image.sprite = sprite;
            image.color = entry != null ? entry.tint : Color.white;
            image.enabled = sprite != null;
        }

        // ------------------------------------------------------------------ inline editing

        private string Shown(Field field, string value)
        {
            return editing == field ? editBuffer + Cursor() : string.IsNullOrEmpty(value) ? "-" : value;
        }

        private static string Cursor()
        {
            return Mathf.Repeat(Time.unscaledTime, 1f) < 0.5f ? "_" : " ";
        }

        private static string Mask(string value)
        {
            return new string('*', Math.Min(16, value?.Length ?? 0));
        }

        private string InitialsEditText()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                char ch = i < editBuffer.Length ? editBuffer[i] : 'A';
                sb.Append(i == initialsCursor && Mathf.Repeat(Time.unscaledTime, 0.8f) < 0.4f ? '_' : ch);
            }
            return sb.ToString();
        }

        private string EditHint()
        {
            return editing == Field.Initials
                ? "HOCH/RUNTER: BUCHSTABE  LINKS/RECHTS: STELLE  BESTAETIGEN: OK"
                : "TIPPEN, ENTER: OK, ESC: ABBRECHEN";
        }

        private void BeginEdit(Field field)
        {
            editing = field;
            switch (field)
            {
                case Field.Server:
                    editBuffer = host + ":" + port;
                    break;
                case Field.Password:
                    editBuffer = string.Empty;
                    break;
                case Field.Room:
                    editBuffer = room ?? string.Empty;
                    break;
                case Field.Initials:
                    string current = Protocol.NormalizeInitials(initials) ?? "AAA";
                    editBuffer = current;
                    initialsCursor = 0;
                    break;
            }
            Input.ResetInputAxes();
        }

        private void CommitEdit()
        {
            switch (editing)
            {
                case Field.Server:
                    string value = editBuffer.Trim();
                    int colon = value.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(value.Substring(colon + 1), out int p))
                    {
                        host = value.Substring(0, colon);
                        port = p.ToString();
                    }
                    else if (value.Length > 0)
                    {
                        host = value;
                    }
                    break;
                case Field.Password:
                    password = editBuffer;
                    break;
                case Field.Room:
                    room = editBuffer.Trim().ToLowerInvariant();
                    break;
                case Field.Initials:
                    initials = editBuffer;
                    break;
            }
            editing = Field.None;
            SaveSettings();
            SoundManager.Play(SoundEvent.MenuSelect);
        }

        private void UpdateEdit()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                editing = Field.None;
                SoundManager.Play(SoundEvent.MenuBack);
                return;
            }
            if (editing == Field.Initials)
            {
                UpdateInitialsEdit();
                return;
            }
            foreach (char c in Input.inputString)
            {
                if (c == '\b')
                {
                    if (editBuffer.Length > 0)
                    {
                        editBuffer = editBuffer.Substring(0, editBuffer.Length - 1);
                    }
                }
                else if (c == '\n' || c == '\r')
                {
                    CommitEdit();
                    return;
                }
                else if (Accepts(editing, c) && editBuffer.Length < MaxLength(editing))
                {
                    editBuffer += c;
                }
            }
            if (Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                CommitEdit();
            }
        }

        private void UpdateInitialsEdit()
        {
            char[] chars = editBuffer.PadRight(3, 'A').Substring(0, 3).ToCharArray();
            int vertical = ArcadeInput.MenuVerticalStep();
            int horizontal = ArcadeInput.MenuHorizontalStep();
            if (vertical != 0)
            {
                // up = next letter like the game's own initials entry
                chars[initialsCursor] = (char)('A' + ((chars[initialsCursor] - 'A' - vertical + 26) % 26));
                SoundManager.Play(SoundEvent.MenuMove);
            }
            if (horizontal != 0)
            {
                initialsCursor = Mathf.Clamp(initialsCursor + horizontal, 0, 2);
                SoundManager.Play(SoundEvent.MenuMove);
            }
            foreach (char c in Input.inputString.ToUpperInvariant())
            {
                if (c >= 'A' && c <= 'Z')
                {
                    chars[initialsCursor] = c;
                    initialsCursor = Math.Min(2, initialsCursor + 1);
                }
                else if (c == '\b')
                {
                    initialsCursor = Math.Max(0, initialsCursor - 1);
                }
            }
            editBuffer = new string(chars);
            bool confirm = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || (ArcadeInput.SubmitDown() && !Input.GetKeyDown(KeyCode.Space));
            if (confirm)
            {
                CommitEdit();
            }
        }

        private static bool Accepts(Field field, char c)
        {
            switch (field)
            {
                case Field.Server:
                    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == ':' || c == '[' || c == ']';
                case Field.Room:
                    char l = char.ToLowerInvariant(c);
                    return (l >= 'a' && l <= 'z') || (l >= '0' && l <= '9') || l == '-' || l == '_';
                case Field.Password:
                    return c >= ' ' && c != '\u007f';
                default:
                    return false;
            }
        }

        private static int MaxLength(Field field)
        {
            return field == Field.Room ? 32 : 128;
        }

        // ------------------------------------------------------------------ input loop

        private void Update()
        {
            if (!visible)
            {
                StartScreenPlattNotice notice = FindAnyObjectByType<StartScreenPlattNotice>();
                if (Input.GetKeyDown(Plugin.ToggleKey.Value) && Session != null && !HotSeatFlowController.IsActive
                    && FindAnyObjectByType<StartScreenMenuController>() != null && (notice == null || !notice.IsVisible))
                {
                    ShowOnline();
                }
                return;
            }
            if (SceneManager.GetActiveScene().name != "StartScreen" || HotSeatFlowController.IsActive)
            {
                Hide();
                return;
            }
            AttractDemoController.NotifyHotSeatActivity();
            if (page == Page.Lobby && !Session.Connected && !Session.Connecting)
            {
                // connection failed or was closed: back to the form, the reason shows in the footer
                Show(Session.IsOffline ? Page.Choice : Page.Connect, 0);
                return;
            }
            if (Signature() != renderedSignature)
            {
                Render();
            }
            Refresh();
            if (editing != Field.None)
            {
                UpdateEdit();
                return;
            }
            if (!armed)
            {
                // a page reacts only once the input that opened it is let go
                armed = !ConfirmHeld() && Time.unscaledTime - openedAt >= 0.25f;
                return;
            }
            if (handledFrame == Time.frameCount || rows.Count == 0)
            {
                return;
            }
            if (ArcadeInput.BackDown())
            {
                Run(Back, SoundEvent.MenuBack);
                return;
            }
            int step = ArcadeInput.MenuVerticalStep();
            if (step != 0)
            {
                Run(() => selection = (selection + step + rows.Count) % rows.Count, SoundEvent.MenuMove);
                return;
            }
            Action<int> change = rows[selection].Horizontal;
            int side = ArcadeInput.MenuHorizontalStep();
            if (side != 0 && change != null)
            {
                Run(() => change(side), SoundEvent.MenuMove);
                return;
            }
            if (ConfirmPressed())
            {
                Run(rows[selection].Submit, SoundEvent.MenuSelect);
            }
        }

        /// <summary>The confirm inputs of the game's menus (button, keypad enter, jump).</summary>
        private static bool ConfirmHeld()
        {
            return Input.GetButton("Submit") || Input.GetKey(KeyCode.KeypadEnter) || ArcadeInput.JumpHeld();
        }

        private static bool ConfirmPressed()
        {
            return ArcadeInput.SubmitDown() || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space);
        }

        private void Back()
        {
            switch (page)
            {
                case Page.Choice:
                    Hide();
                    break;
                case Page.Connect:
                    Show(Page.Choice, 1);
                    break;
                default:
                    Hide();
                    break;
            }
        }

        /// <summary>Runs a menu action once per frame, with the game's menu sound.</summary>
        private void Run(Action action, SoundEvent sound)
        {
            if (action == null || !armed || handledFrame == Time.frameCount)
            {
                return;
            }
            handledFrame = Time.frameCount;
            SoundManager.Play(sound);
            action();
        }

        private void LateUpdate()
        {
            if (!visible)
            {
                return;
            }
            float pulse = PauseMenuTheme.SelectedPulseScale();
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                bool selected = i == selection;
                if (row.Effect != null && row.Effect.selected != selected)
                {
                    row.Effect.SetSelected(selected);
                }
                row.Label.rectTransform.localScale = Vector3.one * (selected ? pulse : 1f);
                if (row.ArrowLeft != null)
                {
                    // "<" and ">" sit right next to the text, wherever it ends
                    float reach = TextHalfWidth(row) * row.Label.rectTransform.localScale.x + 34f;
                    Vector2 middle = row.Background.rectTransform.anchoredPosition;
                    row.ArrowLeft.anchoredPosition = new Vector2(middle.x - reach, middle.y);
                    row.ArrowRight.anchoredPosition = new Vector2(middle.x + reach, middle.y);
                }
            }
            PlaceArrow();
        }

        private static float TextHalfWidth(Row row)
        {
            float box = row.Background.rectTransform.rect.width;
            return 0.5f * (string.IsNullOrEmpty(row.Label.text) ? box : Mathf.Min(box, row.Label.preferredWidth));
        }

        /// <summary>The selection arrow points at the selected text from the left (outside its "&lt;").</summary>
        private void PlaceArrow()
        {
            bool show = selection >= 0 && selection < rows.Count && pointer.sprite != null;
            pointer.enabled = show;
            if (!show)
            {
                return;
            }
            Row row = rows[selection];
            float gap = startMenu != null ? startMenu.selectionArrowGap * styleScale : MenuSelectionArrow.Style.gapToText;
            // the selected text pulses, so its current size counts
            float offset = TextHalfWidth(row) * row.Label.rectTransform.localScale.x + (row.ArrowLeft != null ? 52f : 0f) + gap;
            RectTransform from = row.Background.rectTransform;
            pointer.rectTransform.position = from.TransformPoint(new Vector3(-offset, 0f, 0f));
        }

        // ------------------------------------------------------------------ widgets

        private Row AddRow(string name, Func<string> text, Vector2 position, Action submit, Action<int> horizontal = null, Field edit = Field.None)
        {
            int index = rows.Count;
            var size = new Vector2(1110f, 56f);
            Image background = NewImage(pageRoot, name, Color.clear, position, size);
            background.raycastTarget = true;
            Text label = NewText(background.transform, "Label", text(), 26, Color.white, Vector2.zero, new Vector2(size.x - 42f, size.y));
            var row = new Row
            {
                Background = background,
                Label = label,
                Effect = startMenu != null ? startMenu.ApplyMultiplayerSelectionEffect(label) : PauseMenuTheme.AddSelectionEffect(label),
                Text = text,
                Submit = submit,
                Horizontal = horizontal,
                Edit = edit
            };
            if (horizontal != null)
            {
                row.ArrowLeft = ClickArrow(name + "Previous", "<", () => horizontal(-1));
                row.ArrowRight = ClickArrow(name + "Next", ">", () => horizontal(1));
            }
            rows.Add(row);
            MenuClick.On(background.gameObject, () =>
            {
                if (editing == Field.None)
                {
                    selection = index;
                    Run(submit, SoundEvent.MenuSelect);
                }
            });
            return row;
        }

        /// <summary>Clickable "&lt;" / "&gt;" next to a value, like the game's character selector.</summary>
        private RectTransform ClickArrow(string name, string glyph, Action callback)
        {
            Image hit = NewImage(pageRoot, name, Color.clear, Vector2.zero, new Vector2(44f, 44f));
            hit.raycastTarget = true;
            NewText(hit.transform, "Glyph", glyph, 26, Color.white, Vector2.zero, new Vector2(44f, 44f));
            MenuClick.On(hit.gameObject, () =>
            {
                if (editing == Field.None)
                {
                    Run(callback, SoundEvent.MenuMove);
                }
            });
            return hit.rectTransform;
        }

        private const int TitleSize = 64;

        private void Header(string title, string sub)
        {
            // title at the height the start menu uses for its pages, subtitle right below
            int size = Mathf.RoundToInt(TitleSize * styleScale);
            float titleY = (startMenu != null ? startMenu.pageTitleTopY : 420f) * styleScale - size / 2f;
            NewText(pageRoot, "Title", title, size, Color.white, new Vector2(0f, titleY), new Vector2(1160f, size + 8f));
            subtitle = NewText(pageRoot, "Subtitle", sub, 20, Color.white, new Vector2(0f, titleY - 47f), new Vector2(1160f, 45f));
        }

        private void PageFooter(string text)
        {
            footer = NewText(pageRoot, "Footer", text, 14, Color.white, new Vector2(0f, -360f), new Vector2(1210f, 45f));
        }

        /// <summary>Centered text in the start menu's text style (pixel outline without it).</summary>
        private Text NewText(Transform parent, string name, string value, int size, Color color, Vector2 position, Vector2 box)
        {
            Text text = new GameObject(name, typeof(RectTransform)).AddComponent<Text>();
            Place(text.rectTransform, parent, position, box);
            text.font = font;
            text.text = value;
            text.color = color;
            text.fontSize = size;
            text.raycastTarget = false;
            text.alignment = TextAnchor.MiddleCenter;
            // long values wrap, then shrink instead of being cut off
            text.resizeTextForBestFit = true;
            text.resizeTextMaxSize = size;
            text.resizeTextMinSize = Math.Min(size, 12);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            if (startMenu != null)
            {
                startMenu.ApplyMultiplayerTextStyle(text, styleScale);
            }
            else
            {
                PauseMenuTheme.ApplyOutline(text);
            }
            return text;
        }

        private static Image NewImage(Transform parent, string name, Color color, Vector2 position, Vector2 box)
        {
            Image image = new GameObject(name, typeof(RectTransform)).AddComponent<Image>();
            Place(image.rectTransform, parent, position, box);
            image.sprite = WhitePixelSprite.Get();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>Centered in the parent at the given offset and size.</summary>
        private static void Place(RectTransform rect, Transform parent, Vector2 position, Vector2 box)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = box;
            rect.anchoredPosition = position;
        }

        /// <summary>Covers the whole parent.</summary>
        private static void Fill(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
        }

        internal static string DisciplineName(DisciplineId discipline) => GameBridge.DisciplineName(discipline).ToUpperInvariant();
    }

    /// <summary>Calls back on a click; no Button, so nothing ever takes keyboard focus.</summary>
    internal sealed class MenuClick : MonoBehaviour, IPointerClickHandler
    {
        private Action onClick;

        public static void On(GameObject target, Action action)
        {
            target.AddComponent<MenuClick>().onClick = action;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                onClick?.Invoke();
            }
        }
    }
}
