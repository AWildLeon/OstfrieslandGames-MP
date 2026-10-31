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
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            text.raycastTarget = false;
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
            bool showPanel = Session.Connected && !menuOpen && !hotSeatPage;
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
