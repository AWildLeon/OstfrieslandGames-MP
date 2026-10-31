using System.Collections.Generic;
using System.Linq;
using System.Text;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;
using UnityEngine.UI;

namespace OGMultiplayer.Plugin
{
    public enum HudCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>
    /// In-match overlay (player list, waiting banner, messages), styled like the game's
    /// pause menu: pixel font with its outline, dark translucent backdrops.
    /// </summary>
    internal sealed class OnlineHud : MonoBehaviour
    {
        private const float PanelWidth = 360f;
        private const float LineHeight = 21f;

        private Canvas canvas;
        private RectTransform panel;
        private Image panelBackdrop;
        private readonly List<Text> panelLines = new List<Text>();
        private Image bannerBackdrop;
        private Text banner;
        private Image toastBackdrop;
        private Text toast;
        private Font font;

        // lobby overlay while watching another player (see MirrorView)
        private RectTransform watchOverlay;
        private Text watchTitle;
        private Text watchSubtitle;
        private readonly List<Text> watchRows = new List<Text>();
        private Text watchLeave;
        private Image watchArrow;
        private float watchShownAt = -1f;
        private bool watchArmed;

        private static OnlineSession Session => OnlineSession.Instance;

        /// <summary>Own round paused and only the pause menu open (not its settings or how-to pages).</summary>
        public static bool ShowOverPause => Session != null && Session.LocallyPaused
            && !IngameSettingsOverlay.BlocksInput && !HowToPlayPauseOverlay.BlocksInput;

        private void Start()
        {
            font = GameBridge.Font;
            var go = new GameObject("OnlineHudCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900;
            PauseMenuTheme.NormalizeScaler(go);

            panel = new GameObject("Players", typeof(RectTransform)).GetComponent<RectTransform>();
            panel.SetParent(go.transform, false);
            panelBackdrop = Backdrop(panel, Vector2.zero, Vector2.zero);
            panelBackdrop.rectTransform.anchorMin = Vector2.zero;
            panelBackdrop.rectTransform.anchorMax = Vector2.one;
            panelBackdrop.rectTransform.offsetMin = Vector2.zero;
            panelBackdrop.rectTransform.offsetMax = Vector2.zero;

            bannerBackdrop = Backdrop(go.transform, new Vector2(0f, 300f), new Vector2(900f, 56f));
            banner = MakeText(go.transform, "Banner", 18, new Vector2(0f, 300f), new Vector2(880f, 56f));
            toastBackdrop = Backdrop(go.transform, new Vector2(0f, 236f), new Vector2(980f, 48f));
            toast = MakeText(go.transform, "Toast", 14, new Vector2(0f, 236f), new Vector2(960f, 48f));
            BuildWatchOverlay(go.transform);
        }

        /// <summary>
        /// Lobby page built like the game's pause menu (same backdrop over the whole screen,
        /// title, item font, selection effect and arrow): who is done with how many points,
        /// who still plays, whom you watch, and the way back to the lobby.
        /// </summary>
        private void BuildWatchOverlay(Transform parent)
        {
            watchOverlay = new GameObject("WatchOverlay", typeof(RectTransform)).GetComponent<RectTransform>();
            watchOverlay.SetParent(parent, false);
            watchOverlay.anchorMin = Vector2.zero;
            watchOverlay.anchorMax = Vector2.one;
            watchOverlay.offsetMin = Vector2.zero;
            watchOverlay.offsetMax = Vector2.zero;
            var backdropGo = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            backdropGo.transform.SetParent(watchOverlay, false);
            Image backdrop = backdropGo.GetComponent<Image>();
            backdrop.sprite = WhitePixelSprite.Get();
            backdrop.color = PauseMenuTheme.BackdropColor;
            backdrop.raycastTarget = false;
            backdrop.rectTransform.anchorMin = Vector2.zero;
            backdrop.rectTransform.anchorMax = Vector2.one;
            backdrop.rectTransform.offsetMin = Vector2.zero;
            backdrop.rectTransform.offsetMax = Vector2.zero;
            watchTitle = PauseMenuTheme.CreateText(watchOverlay, "Title", font, PauseMenuTheme.TitleFontSize, PauseMenuTheme.TitlePosition, PauseMenuTheme.TitleSize);
            watchSubtitle = PauseMenuTheme.CreateText(watchOverlay, "Subtitle", font, 16, new Vector2(0f, 92f), new Vector2(760f, 30f));
            for (int i = 0; i < Protocol.MaxPlayers; i++)
            {
                watchRows.Add(PauseMenuTheme.CreateText(watchOverlay, "Player" + i, font, 18, new Vector2(0f, 44f - 34f * i), PauseMenuTheme.ItemSize));
            }
            watchLeave = PauseMenuTheme.CreateText(watchOverlay, "Leave", font, PauseMenuTheme.ItemFontSize, new Vector2(0f, PauseMenuTheme.ItemY(3)), PauseMenuTheme.ItemSize);
            PauseMenuTheme.AddSelectionEffect(watchLeave);
            PauseMenuTheme.ApplySelection(watchLeave, true);
            watchArrow = MenuSelectionArrow.Create(watchOverlay);
            watchOverlay.gameObject.SetActive(false);
        }

        private bool UpdateWatchOverlay()
        {
            MirrorView view = MirrorView.Instance;
            bool show = Session.WaitingForResults && view != null && view.Showing && view.Watching != 0
                && !PauseManager.IsAnyPaused && !OnlineMenu.Visible;
            watchOverlay.gameObject.SetActive(show);
            if (!show)
            {
                watchShownAt = -1f;
                return false;
            }
            if (watchShownAt < 0f)
            {
                watchShownAt = Time.unscaledTime;
                watchArmed = false;
            }
            LobbyPlayer watched = Session.Lobby.FirstOrDefault(p => p.Id == view.Watching);
            PauseMenuTheme.SetLabel(watchTitle, "WARTE AUF DIE ANDEREN");
            PauseMenuTheme.SetLabel(watchSubtitle, $"DU SIEHST {watched?.Initials ?? "?"} ZU");
            List<LobbyPlayer> players = Session.Lobby
                .OrderByDescending(p => p.Status == Protocol.StatusDone ? 1 : 0).ThenByDescending(p => p.Score).ToList();
            for (int i = 0; i < watchRows.Count; i++)
            {
                bool used = i < players.Count;
                watchRows[i].gameObject.SetActive(used);
                if (used)
                {
                    LobbyPlayer p = players[i];
                    string who = p.Initials + (p.Id == Session.MyId ? " (DU)" : p.IsBot ? " (BOT)" : string.Empty);
                    PauseMenuTheme.SetLabel(watchRows[i], who.PadRight(9) + " " + StatusText(p).PadRight(12));
                }
            }
            PauseMenuTheme.SetLabel(watchLeave, "ZUR LOBBY");
            MenuSelectionArrow.Place(watchArrow, watchLeave);
            // only the game's own submit input; buttons still held from the round do not count
            bool held = Input.GetButton("Submit") || ArcadeInput.JumpHeld() || Input.GetKey(KeyCode.KeypadEnter);
            if (!watchArmed)
            {
                watchArmed = !held && Time.unscaledTime - watchShownAt > 1.5f;
            }
            else if (ArcadeInput.SubmitDown() || Input.GetKeyDown(KeyCode.KeypadEnter)
                || (Input.GetMouseButtonDown(0) && RectTransformUtility.RectangleContainsScreenPoint(watchLeave.rectTransform, Input.mousePosition, null)))
            {
                watchArmed = false;
                HotSeatFlowController.AbortToMenu();
            }
            return true;
        }

        private Image Backdrop(Transform parent, Vector2 position, Vector2 size)
        {
            var go = new GameObject("Backdrop", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = PauseMenuTheme.BackdropColor;
            image.raycastTarget = false;
            image.rectTransform.anchoredPosition = position;
            image.rectTransform.sizeDelta = size;
            return image;
        }

        /// <summary>Game font with the game's teal text shadow (the pixel outline gets blocky at small sizes).</summary>
        private Text MakeText(Transform parent, string name, int size, Vector2 position, Vector2 dimensions)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Shadow));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = Color.white;
            text.raycastTarget = false;
            text.supportRichText = false;
            text.alignment = TextAnchor.MiddleCenter;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            RectTransform rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = dimensions;
            rt.anchoredPosition = position;
            Shadow shadow = go.GetComponent<Shadow>();
            shadow.effectColor = GameTextStyle.ShadowColor;
            shadow.effectDistance = new Vector2(2f, -2f);
            return text;
        }

        private void LateUpdate()
        {
            if (canvas == null || Session == null)
            {
                return;
            }
            bool menuOpen = OnlineMenu.Visible;
            // the game's own results/setup pages already list everyone
            HotSeatFlowController hotSeat = HotSeatFlowController.Instance;
            bool hotSeatPage = hotSeat != null && (hotSeat.Phase == HotSeatPhase.Results || hotSeat.Phase == HotSeatPhase.Setup);
            bool watching = UpdateWatchOverlay();
            bool showPanel = Session.Connected && !menuOpen && !hotSeatPage && !watching;
            panel.gameObject.SetActive(showPanel);
            if (showPanel)
            {
                LayoutPanel();
            }

            string center = null;
            int myGrace = Session.MyGraceLeft;
            LobbyPlayer pausing = Session.Lobby.FirstOrDefault(p => p.Id != Session.MyId && p.Status == Protocol.StatusPaused);
            // the game's pause menu sits on top (order 5000) and hides other texts; the
            // countdown must show over it (texts hidden while a settings page was open come back)
            bool overPause = ShowOverPause;
            canvas.sortingOrder = overPause ? 5001 : 900;
            if (overPause)
            {
                foreach (Graphic g in canvas.GetComponentsInChildren<Graphic>(true))
                {
                    g.canvasRenderer.SetAlpha(1f);
                }
            }
            if (myGrace >= 0)
            {
                center = $"NOCH {myGrace} S PAUSE, DANN ZÄHLT DEIN STAND";
            }
            else if (Session.WaitingForResults && pausing != null)
            {
                center = pausing.GraceLeft >= 0
                    ? $"{pausing.Initials} PAUSIERT - NOCH {pausing.GraceLeft} S, DANN ZÄHLT DER STAND"
                    : $"{pausing.Initials} PAUSIERT ...";
            }
            else if (Session.WaitingForGo)
            {
                center = "WARTE AUF DIE ANDEREN SPIELER ...";
            }
            else if (watching)
            {
                // the overlay says it all
            }
            else if (Session.WaitingForResults)
            {
                // the game's own pause menu leads out ("ZUM START" returns to the lobby here)
                center = "FERTIG! WARTE AUF DIE ANDEREN ...  (PAUSE > "
                    + GameLocalization.GetOrDefault("ui.pause.backToStart", "ZUM START").ToUpperInvariant() + ": ZUR LOBBY)";
            }
            // over the pause menu the title is at the top, so the countdown goes below its items
            float bannerY = myGrace >= 0 ? -200f : 300f;
            bannerBackdrop.rectTransform.anchoredPosition = new Vector2(0f, bannerY);
            banner.rectTransform.anchoredPosition = new Vector2(0f, bannerY);
            SetBox(bannerBackdrop, banner, center);
            // over the menu the title area is taken, so messages go to the bottom there
            float toastY = menuOpen ? -338f : 236f;
            toastBackdrop.rectTransform.anchoredPosition = new Vector2(0f, toastY);
            toast.rectTransform.anchoredPosition = new Vector2(0f, toastY);
            SetBox(toastBackdrop, toast, string.IsNullOrEmpty(Session.Message) ? null : Session.Message.ToUpperInvariant());
        }

        private static void SetBox(Image backdrop, Text text, string value)
        {
            bool show = !string.IsNullOrEmpty(value);
            backdrop.gameObject.SetActive(show);
            text.gameObject.SetActive(show);
            if (show && text.text != value)
            {
                text.text = value;
            }
        }

        private void LayoutPanel()
        {
            var lines = new List<string> { (Session.IsOffline ? "GEGEN BOTS" : "ONLINE  " + Session.RoomName.ToUpperInvariant()) };
            foreach (LobbyPlayer p in Session.Lobby)
            {
                var sb = new StringBuilder();
                sb.Append(p.Initials);
                sb.Append(p.Id == Session.MyId ? " DU " : p.IsBot ? " BOT" : "    ");
                sb.Append("  ");
                sb.Append(StatusText(p));
                lines.Add(sb.ToString());
            }
            if (Session.RoomState != Protocol.StateMatch)
            {
                lines.Add(Session.LobbyStatus(brief: true));
            }
            while (panelLines.Count < lines.Count)
            {
                Text t = MakeText(panel, "Line" + panelLines.Count, 13, Vector2.zero, new Vector2(PanelWidth - 24f, LineHeight));
                t.alignment = TextAnchor.MiddleLeft;
                panelLines.Add(t);
            }
            float height = lines.Count * LineHeight + 18f;
            panel.sizeDelta = new Vector2(PanelWidth, height);
            HudCorner corner = Plugin.HudPosition.Value;
            bool left = corner == HudCorner.TopLeft || corner == HudCorner.BottomLeft;
            bool top = corner == HudCorner.TopLeft || corner == HudCorner.TopRight;
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(left ? 0f : 1f, top ? 1f : 0f);
            panel.anchoredPosition = new Vector2(left ? 14f : -14f, top ? -14f : 14f);
            for (int i = 0; i < panelLines.Count; i++)
            {
                Text t = panelLines[i];
                bool used = i < lines.Count;
                t.gameObject.SetActive(used);
                if (!used)
                {
                    continue;
                }
                RectTransform rt = t.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -9f - LineHeight * (i + 0.5f));
                if (t.text != lines[i])
                {
                    t.text = lines[i];
                }
            }
        }

        private static string StatusText(LobbyPlayer p)
        {
            switch (p.Status)
            {
                case Protocol.StatusLoading:
                    return "LÄDT";
                case Protocol.StatusReady:
                    return "BEREIT";
                case Protocol.StatusPlaying:
                    return p.Score > 0 ? "SPIELT " + p.Score : "SPIELT";
                case Protocol.StatusPaused:
                    return p.GraceLeft >= 0 ? $"PAUSE {p.GraceLeft} S" : "PAUSE";
                case Protocol.StatusDone:
                    return "FERTIG " + p.Score;
                case Protocol.StatusLeft:
                    return "RAUS";
                default:
                    return "LOBBY";
            }
        }
    }
}
