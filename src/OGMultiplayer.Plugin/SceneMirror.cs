using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OstfrieslandGames.Padstockspringen;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// GetInstanceID: stable key that also works for destroyed objects
#pragma warning disable CS0618

namespace OGMultiplayer.Plugin
{
    /// <summary>
    /// Scene mirror: players who are done watch the best player still playing, rendered by
    /// their own copy of the discipline scene.
    ///
    /// The player streams what its scene looks like, not how it plays: every transform of the
    /// discipline scene with its local position/rotation/scale and active flag, plus sprite,
    /// colour, flip and enabled state of SpriteRenderer / Image / Text, the size of rect
    /// transforms and the orthographic size of cameras. Both sides load the same scene, so
    /// objects are matched by their hierarchy path; only what differs from the freshly loaded
    /// scene is sent. Objects that appear during play are created as plain proxies.
    ///
    /// The watcher loads the scene, lets it start for two frames (so everything the scripts
    /// build at start exists on both sides), then freezes the game's own scripts, animators
    /// and physics and applies the stream, with smoothed positions.
    ///
    /// Record format (one TAB field each): id;key=value;... with
    ///   P path (first time / full state) K kind (s sprite, g image, x text, r rect, e empty)
    ///   L sortingLayer,order  z fontSize  A anchorMin,anchorMax,pivot (1/1000)  a active  p x,y,z (1/100)  r rotZ (1/10 deg)
    ///   s x,y (1/1000)  e enabled  i sprite (texture/sprite)  f flip bits  c rrggbbaa
    ///   d w,h (1/100)  o ortho size (1/100)  F fill (1/1000)  t text  x removed
    /// Names and texts are URI-escaped.
    /// </summary>
    internal static class Mirror
    {
        public static readonly System.Reflection.Assembly GameAssembly = typeof(HotSeatFlowController).Assembly;

        /// <summary>The discipline's scene, as the game itself names it.</summary>
        public static string SceneFor(DisciplineId discipline) => GameBridge.GameScene(discipline);

        /// <summary>Menus on top of the game are local (pause menu and its pages), and so are input prompts.</summary>
        public static bool Excluded(Transform t)
        {
            return t.GetComponent<PauseMenuView>() != null || t.GetComponent<IngameSettingsOverlay>() != null
                || t.GetComponent<HowToPlayPauseOverlay>() != null || IsPrompt(t);
        }

        private static readonly HashSet<int> promptRoots = new HashSet<int>();
        private static int promptScene = -1;

        /// <summary>
        /// Input prompts make no sense for someone who only watches ("press as fast as you
        /// can", key caps, the throw power bar): what the game itself marks as a HUD hint and
        /// the Bosseln throw display with its prompt. Other texts may be part of the play and stay.
        /// </summary>
        public static bool IsPrompt(Transform t)
        {
            int handle = t.gameObject.scene.handle;
            if (handle != promptScene)
            {
                promptScene = handle;
                promptRoots.Clear();
                foreach (BosselnWurfStakkato stakkato in UnityEngine.Object.FindObjectsByType<BosselnWurfStakkato>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (stakkato.anzeigeRoot != null)
                    {
                        promptRoots.Add(stakkato.anzeigeRoot.GetInstanceID());
                    }
                    if (stakkato.promptText != null)
                    {
                        promptRoots.Add(stakkato.promptText.transform.GetInstanceID());
                    }
                }
            }
            return promptRoots.Contains(t.GetInstanceID()) || t.GetComponent<IngameHudHint>() != null;
        }

        /// <summary>Hides the watcher's own prompts (they are never streamed).</summary>
        public static void HidePrompts(Scene scene)
        {
            promptScene = -1;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (IsPrompt(t))
                    {
                        t.gameObject.SetActive(false);
                    }
                }
            }
        }

        /// <summary>Visits every transform of the scene with its path (name#n per level, n counts equal names).</summary>
        public static void Walk(Scene scene, Action<Transform, string> visit)
        {
            var counts = new Dictionary<string, int>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                WalkOne(root.transform, string.Empty, counts, visit);
            }
        }

        private static void WalkOne(Transform t, string parentPath, Dictionary<string, int> counts, Action<Transform, string> visit)
        {
            if (Excluded(t))
            {
                return;
            }
            string name = t.name;
            counts.TryGetValue(name, out int n);
            counts[name] = n + 1;
            string path = parentPath + "/" + name + "#" + n.ToString(CultureInfo.InvariantCulture);
            visit(t, path);
            int children = t.childCount;
            if (children == 0)
            {
                return;
            }
            var childCounts = new Dictionary<string, int>();
            for (int i = 0; i < children; i++)
            {
                WalkOne(t.GetChild(i), path, childCounts, visit);
            }
        }

        private static readonly Dictionary<Sprite, string> spriteKeys = new Dictionary<Sprite, string>();

        public static string SpriteKey(Sprite sprite)
        {
            if (sprite == null)
            {
                return string.Empty;
            }
            if (!spriteKeys.TryGetValue(sprite, out string key))
            {
                key = (sprite.texture != null ? sprite.texture.name : string.Empty) + "/" + sprite.name;
                if (spriteKeys.Count > 4096)
                {
                    spriteKeys.Clear();
                }
                spriteKeys[sprite] = key;
            }
            return key;
        }

        public static int Q(float value, float scale)
        {
            double v = Math.Round(value * scale);
            return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
        }

        public static string Esc(string text) => Uri.EscapeDataString(text ?? string.Empty);

        public static string Unesc(string text)
        {
            try
            {
                return Uri.UnescapeDataString(text ?? string.Empty);
            }
            catch (UriFormatException)
            {
                return string.Empty;
            }
        }

        public static uint Pack(Color32 c) => ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;

        public static Color32 Unpack(uint v) => new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

        public static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        public static bool TryInts(string text, int[] into)
        {
            string[] parts = text.Split(',');
            if (parts.Length != into.Length)
            {
                return false;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out into[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>What one object looks like, quantized so that unchanged means equal.</summary>
    internal struct MirrorSnap
    {
        public bool Active;
        public int Px, Py, Pz, Rz, Sx, Sy;
        public bool Enabled;
        public Sprite Sprite;
        public int Flip;
        public uint Color;
        public int Dx, Dy, Ortho, Fill;
        public string Text;
    }

    /// <summary>Streams the own discipline scene while somebody watches (see Mirror).</summary>
    internal sealed class MirrorSender : MonoBehaviour
    {
        private sealed class Ent
        {
            public int Id;
            public Transform T;
            public string Path;
            public SpriteRenderer Sr;
            public Image Img;
            public Text Txt;
            public RectTransform Rt;
            public Camera Cam;
            public bool Spawned;
            public MirrorSnap Baseline;
            public MirrorSnap Sent;
            public bool HasSent;
            public bool PathSent;
            public bool Removed;
            public bool TransformRead;
        }

        private static MirrorSender instance;
        private readonly List<Ent> ents = new List<Ent>();
        private readonly Dictionary<int, Ent> byTransform = new Dictionary<int, Ent>();
        private readonly StringBuilder line = new StringBuilder(Protocol.MaxViewChars + 512);
        private readonly List<string> records = new List<string>();
        private Scene scene;
        private bool ready;
        private bool fullState;
        private int nextId = 1;
        private int frame;
        private int cursor;
        private float nextSend;
        private float nextScan;

        private static OnlineSession Session => OnlineSession.Instance;

        /// <summary>A watcher (re)joined: send everything that differs from the loaded scene.</summary>
        public static void RequestFullState()
        {
            if (instance != null)
            {
                instance.fullState = true;
            }
        }

        private void Awake()
        {
            instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene loaded, LoadSceneMode mode)
        {
            ready = false;
            ents.Clear();
            byTransform.Clear();
            OnlineSession s = Session;
            if (s == null || !s.MatchRunning || s.LocalFinished || loaded.name != Mirror.SceneFor(s.Discipline))
            {
                return;
            }
            StartCoroutine(RegisterSoon(loaded));
        }

        /// <summary>Same moment as the watcher's freeze: two frames after loading.</summary>
        private IEnumerator RegisterSoon(Scene loaded)
        {
            yield return null;
            yield return null;
            if (!loaded.isLoaded || SceneManager.GetActiveScene() != loaded)
            {
                yield break;
            }
            scene = loaded;
            nextId = 1;
            frame = 0;
            cursor = 0;
            Mirror.Walk(scene, (t, path) => Add(t, path, spawned: false));
            ready = true;
            Plugin.Log.LogInfo($"Zuschauer-Spiegel bereit: {ents.Count} Objekte in {scene.name}");
        }

        private void Add(Transform t, string path, bool spawned)
        {
            int key = t.GetInstanceID();
            if (byTransform.ContainsKey(key))
            {
                return;
            }
            var e = new Ent
            {
                Id = nextId++,
                T = t,
                Path = path,
                Sr = t.GetComponent<SpriteRenderer>(),
                Img = t.GetComponent<Image>(),
                Txt = t.GetComponent<Text>(),
                Rt = t as RectTransform,
                Cam = t.GetComponent<Camera>(),
                Spawned = spawned,
            };
            if (!spawned)
            {
                e.Baseline = Read(e, true);
                e.Sent = e.Baseline;
                e.HasSent = true;
            }
            ents.Add(e);
            byTransform[key] = e;
        }

        private static MirrorSnap Read(Ent e, bool transformToo)
        {
            MirrorSnap s = e.Sent;
            Transform t = e.T;
            s.Active = t.gameObject.activeSelf;
            if (transformToo)
            {
                Vector3 p = t.localPosition;
                s.Px = Mirror.Q(p.x, 100f);
                s.Py = Mirror.Q(p.y, 100f);
                s.Pz = Mirror.Q(p.z, 100f);
                s.Rz = Mirror.Q(t.localEulerAngles.z, 10f);
                Vector3 sc = t.localScale;
                s.Sx = Mirror.Q(sc.x, 1000f);
                s.Sy = Mirror.Q(sc.y, 1000f);
                if (e.Rt != null)
                {
                    Vector2 d = e.Rt.sizeDelta;
                    s.Dx = Mirror.Q(d.x, 100f);
                    s.Dy = Mirror.Q(d.y, 100f);
                }
            }
            if (e.Sr != null)
            {
                s.Enabled = e.Sr.enabled;
                s.Sprite = e.Sr.sprite;
                s.Flip = (e.Sr.flipX ? 1 : 0) | (e.Sr.flipY ? 2 : 0);
                s.Color = Mirror.Pack(e.Sr.color);
            }
            else if (e.Img != null)
            {
                s.Enabled = e.Img.enabled;
                s.Sprite = e.Img.sprite;
                s.Color = Mirror.Pack(e.Img.color);
                s.Fill = Mirror.Q(e.Img.fillAmount, 1000f);
            }
            else if (e.Txt != null)
            {
                s.Enabled = e.Txt.enabled;
                s.Text = e.Txt.text;
                s.Color = Mirror.Pack(e.Txt.color);
            }
            if (e.Cam != null)
            {
                s.Ortho = Mirror.Q(e.Cam.orthographicSize, 100f);
            }
            return s;
        }

        private void Update()
        {
            OnlineSession s = Session;
            if (!ready || s == null || !s.StreamOn || !s.MatchRunning || !s.GoReceived || s.LocalFinished
                || !scene.isLoaded || SceneManager.GetActiveScene() != scene)
            {
                return;
            }
            float now = Time.unscaledTime;
            if (now >= nextScan)
            {
                nextScan = now + 1f;
                Mirror.Walk(scene, (t, path) => Add(t, path, spawned: true));
            }
            if (now < nextSend)
            {
                return;
            }
            nextSend = now + 0.1f;
            if (fullState)
            {
                fullState = false;
                cursor = 0;
                foreach (Ent e in ents)
                {
                    e.PathSent = false;
                    e.HasSent = !e.Spawned;
                    e.Sent = e.Baseline;
                    if (e.T != null)
                    {
                        e.T.hasChanged = true;
                    }
                }
            }
            SendChanges(s);
        }

        /// <summary>One VIEW line per tick; what does not fit goes out with the next one.</summary>
        private void SendChanges(OnlineSession s)
        {
            records.Clear();
            int budget = Protocol.MaxViewChars;
            int count = ents.Count;
            int i = 0;
            for (; i < count && budget > 0; i++)
            {
                int index = (cursor + i) % count;
                Ent e = ents[index];
                string record = Record(e);
                if (record == null)
                {
                    continue;
                }
                if (record.Length > Protocol.MaxViewChars)
                {
                    // never send a line the server would refuse (and drop the player for)
                    Plugin.Log.LogWarning($"Zuschauer-Spiegel: Eintrag zu lang ({record.Length} Zeichen), übersprungen: {e.Path}");
                    Commit(e);
                    continue;
                }
                if (record.Length > budget && records.Count > 0)
                {
                    break;
                }
                budget -= record.Length + 1;
                records.Add(record);
                Commit(e);
            }
            cursor = count == 0 ? 0 : (cursor + i) % count;
            for (int r = ents.Count - 1; r >= 0; r--)
            {
                if (ents[r].Removed)
                {
                    byTransform.Remove(ents[r].T.GetInstanceID());
                    ents.RemoveAt(r);
                }
            }
            if (records.Count == 0)
            {
                return;
            }
            var fields = new string[records.Count + 3];
            fields[0] = Protocol.View;
            fields[1] = Protocol.Int(s.MatchId);
            fields[2] = Protocol.Int(++frame);
            records.CopyTo(fields, 3);
            s.SendMirror(fields);
        }

        private MirrorSnap pending;

        /// <summary>Longer texts are cut; nothing on screen needs more.</summary>
        private const int MaxText = 400;

        private string Record(Ent e)
        {
            line.Length = 0;
            if (e.T == null)
            {
                if (e.Removed || (e.Spawned && !e.PathSent))
                {
                    e.Removed = true;
                    return null;
                }
                line.Append(Mirror.I(e.Id));
                if (!e.PathSent)
                {
                    line.Append(";P=").Append(Mirror.Esc(e.Path));
                }
                line.Append(";x");
                pendingRemoved = true;
                return line.ToString();
            }
            bool transformToo = e.T.hasChanged || !e.HasSent;
            MirrorSnap now = Read(e, transformToo);
            MirrorSnap old = e.Sent;
            bool all = !e.HasSent;
            bool any = false;
            line.Append(Mirror.I(e.Id));
            if (!e.PathSent)
            {
                line.Append(";P=").Append(Mirror.Esc(e.Path));
                if (e.Spawned)
                {
                    line.Append(";K=").Append(Kind(e));
                    if (e.Sr != null)
                    {
                        line.Append(";L=").Append(Mirror.Esc(e.Sr.sortingLayerName)).Append(',').Append(Mirror.I(e.Sr.sortingOrder));
                    }
                    if (e.Txt != null)
                    {
                        line.Append(";z=").Append(Mirror.I(e.Txt.fontSize));
                    }
                    if (e.Rt != null)
                    {
                        line.Append(";A=").Append(Mirror.I(Mirror.Q(e.Rt.anchorMin.x, 1000f))).Append(',').Append(Mirror.I(Mirror.Q(e.Rt.anchorMin.y, 1000f)))
                            .Append(',').Append(Mirror.I(Mirror.Q(e.Rt.anchorMax.x, 1000f))).Append(',').Append(Mirror.I(Mirror.Q(e.Rt.anchorMax.y, 1000f)))
                            .Append(',').Append(Mirror.I(Mirror.Q(e.Rt.pivot.x, 1000f))).Append(',').Append(Mirror.I(Mirror.Q(e.Rt.pivot.y, 1000f)));
                    }
                    any = true;
                }
            }
            if (all || now.Active != old.Active)
            {
                line.Append(";a=").Append(now.Active ? '1' : '0');
                any = true;
            }
            if (all || now.Px != old.Px || now.Py != old.Py || now.Pz != old.Pz)
            {
                line.Append(";p=").Append(Mirror.I(now.Px)).Append(',').Append(Mirror.I(now.Py)).Append(',').Append(Mirror.I(now.Pz));
                any = true;
            }
            if (all || now.Rz != old.Rz)
            {
                line.Append(";r=").Append(Mirror.I(now.Rz));
                any = true;
            }
            if (all || now.Sx != old.Sx || now.Sy != old.Sy)
            {
                line.Append(";s=").Append(Mirror.I(now.Sx)).Append(',').Append(Mirror.I(now.Sy));
                any = true;
            }
            bool component = e.Sr != null || e.Img != null || e.Txt != null;
            if (component && (all || now.Enabled != old.Enabled))
            {
                line.Append(";e=").Append(now.Enabled ? '1' : '0');
                any = true;
            }
            if ((e.Sr != null || e.Img != null) && (all || now.Sprite != old.Sprite))
            {
                line.Append(";i=").Append(Mirror.Esc(Mirror.SpriteKey(now.Sprite)));
                any = true;
            }
            if (e.Sr != null && (all || now.Flip != old.Flip))
            {
                line.Append(";f=").Append(Mirror.I(now.Flip));
                any = true;
            }
            if (component && (all || now.Color != old.Color))
            {
                line.Append(";c=").Append(now.Color.ToString("x8", CultureInfo.InvariantCulture));
                any = true;
            }
            if (e.Rt != null && (all || now.Dx != old.Dx || now.Dy != old.Dy))
            {
                line.Append(";d=").Append(Mirror.I(now.Dx)).Append(',').Append(Mirror.I(now.Dy));
                any = true;
            }
            if (e.Cam != null && (all || now.Ortho != old.Ortho))
            {
                line.Append(";o=").Append(Mirror.I(now.Ortho));
                any = true;
            }
            if (e.Img != null && (all || now.Fill != old.Fill))
            {
                line.Append(";F=").Append(Mirror.I(now.Fill));
                any = true;
            }
            if (e.Txt != null && (all || !string.Equals(now.Text, old.Text, StringComparison.Ordinal)))
            {
                string text = now.Text ?? string.Empty;
                line.Append(";t=").Append(Mirror.Esc(text.Length > MaxText ? text.Substring(0, MaxText) : text));
                any = true;
            }
            pending = now;
            pendingRemoved = false;
            e.TransformRead = transformToo;
            if (!any)
            {
                // nothing to send, but the state is known now
                e.Sent = now;
                if (transformToo)
                {
                    e.T.hasChanged = false;
                }
                return null;
            }
            return line.ToString();
        }

        private bool pendingRemoved;

        private void Commit(Ent e)
        {
            e.PathSent = true;
            if (pendingRemoved)
            {
                e.Removed = true;
                return;
            }
            e.Sent = pending;
            e.HasSent = true;
            if (e.TransformRead)
            {
                e.T.hasChanged = false;
            }
        }

        private static char Kind(Ent e)
        {
            if (e.Sr != null)
            {
                return 's';
            }
            if (e.Img != null)
            {
                return 'g';
            }
            if (e.Txt != null)
            {
                return 'x';
            }
            return e.Rt != null ? 'r' : 'e';
        }
    }

    /// <summary>
    /// Shows the watched player's scene while the own turn is over (see Mirror). Loads the
    /// discipline scene again, freezes it and applies the VIEW stream.
    /// </summary>
    internal sealed class MirrorView : MonoBehaviour
    {
        private sealed class Ent
        {
            public Transform T;
            public bool Proxy;
            public SpriteRenderer Sr;
            public Image Img;
            public Text Txt;
            public RectTransform Rt;
            public Camera Cam;
            public bool HasTarget;
            public Vector3 Target;

            // original state of scene objects, restored when the watched player changes
            public bool Saved;
            public bool Active;
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 Scale;
            public Vector2 Size;
            public bool Enabled;
            public Sprite Sprite;
            public bool FlipX;
            public bool FlipY;
            public Color Color;
            public float Fill;
            public string Text;
            public float Ortho;
        }

        public static MirrorView Instance { get; private set; }

        /// <summary>The watched scene is on screen.</summary>
        public bool Showing => ready && scene.isLoaded && SceneManager.GetActiveScene() == scene;

        public int Watching { get; private set; }

        /// <summary>The scene is (re)built with the watched player's character (see ActiveCharacterPatch).</summary>
        public static string CharacterOverride { get; private set; }

        private readonly Dictionary<string, Transform> paths = new Dictionary<string, Transform>();
        private readonly Dictionary<int, Ent> ents = new Dictionary<int, Ent>();
        private readonly List<Ent> moving = new List<Ent>();
        private readonly List<GameObject> proxies = new List<GameObject>();
        private Dictionary<string, Sprite> sprites;
        private float spritesAt;
        private Scene scene;
        private bool loading;
        private bool freezing;
        private float loadedAt = -100f;
        private bool ready;
        private float keyAt;
        private bool keyRetried;
        private bool viewSeen;
        private string sceneName;
        private int matchId;
        private readonly int[] v3 = new int[3];
        private readonly int[] v2 = new int[2];

        private static OnlineSession Session => OnlineSession.Instance;

        private void Awake()
        {
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private bool Wanted(OnlineSession s)
        {
            return s != null && Plugin.Spectate.Value && s.WaitingForResults && s.WatchTarget != 0;
        }

        private void Update()
        {
            OnlineSession s = Session;
            if (ready && (!scene.isLoaded || SceneManager.GetActiveScene() != scene))
            {
                Clear();
            }
            if (s == null || !Wanted(s))
            {
                if (!ready)
                {
                    // round over before the scene was up: the next round uses the own figure again
                    CharacterOverride = null;
                    loading = false;
                }
                if (ready && s != null && s.WaitingForResults && Watching != 0)
                {
                    // nobody left to watch: keep the scene, back to how it was loaded
                    Revert();
                    Watching = 0;
                }
                return;
            }
            if (!ready)
            {
                // let the own end screen show for a moment; no reload under the pause menu
                if (!loading && !freezing && Time.unscaledTime - s.FinishedAt > 3f && Time.unscaledTime - loadedAt > 10f
                    && !PauseManager.IsAnyPaused)
                {
                    loading = true;
                    loadedAt = Time.unscaledTime;
                    matchId = s.MatchId;
                    CharacterOverride = WatchedCharacter(s);
                    sceneName = Mirror.SceneFor(s.Discipline);
                    Plugin.Log.LogInfo($"Zuschauen: lade {sceneName} mit Figur {CharacterOverride}");
                    SceneManager.LoadScene(sceneName);
                }
                return;
            }
            if (Watching != s.WatchTarget && WatchedCharacter(s) != CharacterOverride)
            {
                // another figure: the scene has to be built again with it
                Clear();
                loadedAt = -100f;
                return;
            }
            if (Watching != s.WatchTarget)
            {
                Revert();
                Watching = s.WatchTarget;
                keyAt = 0f;
                keyRetried = false;
                viewSeen = false;
            }
            // one request per watched player; a single retry if nothing at all arrived (lost on the way)
            if (keyAt == 0f || (!viewSeen && !keyRetried && Time.unscaledTime - keyAt > 5f))
            {
                keyRetried = keyAt != 0f;
                keyAt = Time.unscaledTime;
                s.SendMirror(Protocol.ViewKey, Protocol.Int(s.MatchId));
            }
        }

        private void OnSceneLoaded(Scene loaded, LoadSceneMode mode)
        {
            if (!loading || loaded.name != sceneName)
            {
                return;
            }
            loading = false;
            freezing = true;
            StartCoroutine(FreezeSoon(loaded));
        }

        /// <summary>Two frames for the scene's scripts to build everything, then they stop.</summary>
        private IEnumerator FreezeSoon(Scene loaded)
        {
            yield return null;
            yield return null;
            freezing = false;
            OnlineSession s = Session;
            if (!loaded.isLoaded || SceneManager.GetActiveScene() != loaded || s == null || s.MatchId != matchId)
            {
                yield break;
            }
            scene = loaded;
            Freeze(scene);
            Mirror.HidePrompts(scene);
            paths.Clear();
            Mirror.Walk(scene, (t, path) => paths[path] = t);
            ready = true;
            Watching = 0;
            Plugin.Log.LogInfo($"Zuschauen: {sceneName} eingefroren, {paths.Count} Objekte");
        }

        /// <summary>
        /// Scripts that only draw: mesh scrollers (they own their runtime materials and
        /// destroy them when disabled; the mirror does not carry their scroll offset either)
        /// and camera image effects such as the pixel filter.
        /// </summary>
        private static bool KeepsRunning(MonoBehaviour mb)
        {
            if (mb.GetComponent<MeshRenderer>() != null)
            {
                return true;
            }
            return mb.GetType().GetMethod("OnRenderImage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) != null;
        }

        private static void Freeze(Scene frozen)
        {
            foreach (GameObject root in frozen.GetRootGameObjects())
            {
                foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || mb.GetType().Assembly != Mirror.GameAssembly || mb is PauseManager || mb is PauseMenuView
                        || mb is IngameSettingsHudVisibility || mb is UnityEngine.EventSystems.UIBehaviour || KeepsRunning(mb))
                    {
                        continue;
                    }
                    mb.StopAllCoroutines();
                    mb.enabled = false;
                }
                foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = false;
                }
                foreach (Rigidbody2D body in root.GetComponentsInChildren<Rigidbody2D>(true))
                {
                    body.simulated = false;
                }
                foreach (AudioSource audio in root.GetComponentsInChildren<AudioSource>(true))
                {
                    audio.Stop();
                    audio.mute = true;
                }
            }
            Time.timeScale = 1f;
        }

        private static string WatchedCharacter(OnlineSession s)
        {
            string id = null;
            foreach (LobbyPlayer p in s.Lobby)
            {
                if (p.Id == s.WatchTarget)
                {
                    id = p.Character;
                }
            }
            return id;
        }

        private void Clear()
        {
            CharacterOverride = null;
            ready = false;
            loading = false;
            freezing = false;
            Watching = 0;
            ents.Clear();
            moving.Clear();
            proxies.Clear();
            paths.Clear();
            sprites = null;
            misses.Clear();
        }

        /// <summary>Puts the scene back the way it was loaded (another player is watched now).</summary>
        private void Revert()
        {
            foreach (Ent e in ents.Values)
            {
                if (e.Proxy || !e.Saved || e.T == null)
                {
                    continue;
                }
                e.T.localPosition = e.Pos;
                e.T.localRotation = e.Rot;
                e.T.localScale = e.Scale;
                if (e.Rt != null)
                {
                    e.Rt.sizeDelta = e.Size;
                }
                if (e.Sr != null)
                {
                    e.Sr.enabled = e.Enabled;
                    e.Sr.sprite = e.Sprite;
                    e.Sr.flipX = e.FlipX;
                    e.Sr.flipY = e.FlipY;
                    e.Sr.color = e.Color;
                }
                if (e.Img != null)
                {
                    e.Img.enabled = e.Enabled;
                    e.Img.sprite = e.Sprite;
                    e.Img.color = e.Color;
                    e.Img.fillAmount = e.Fill;
                }
                if (e.Txt != null)
                {
                    e.Txt.enabled = e.Enabled;
                    e.Txt.text = e.Text;
                    e.Txt.color = e.Color;
                }
                if (e.Cam != null)
                {
                    e.Cam.orthographicSize = e.Ortho;
                }
                e.T.gameObject.SetActive(e.Active);
            }
            foreach (GameObject proxy in proxies)
            {
                if (proxy != null)
                {
                    Destroy(proxy);
                }
            }
            proxies.Clear();
            ents.Clear();
            moving.Clear();
            paths.Clear();
            if (scene.isLoaded)
            {
                Mirror.Walk(scene, (t, path) => paths[path] = t);
            }
        }

        /// <summary>VIEW playerId frame record*</summary>
        public void Apply(string[] f)
        {
            if (!Showing || f.Length < 4 || !Protocol.TryInt(f[1], out int from) || from != Watching || Watching == 0)
            {
                return;
            }
            viewSeen = true;
            for (int i = 3; i < f.Length; i++)
            {
                try
                {
                    ApplyRecord(f[i]);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogDebug("Zuschauen: Eintrag übersprungen: " + ex.Message);
                }
            }
        }

        private void ApplyRecord(string record)
        {
            string[] parts = record.Split(';');
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                return;
            }
            string path = null;
            char kind = '\0';
            string layer = null;
            int fontSize = 0;
            string anchors = null;
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.StartsWith("P=", StringComparison.Ordinal))
                {
                    path = Mirror.Unesc(p.Substring(2));
                }
                else if (p.StartsWith("K=", StringComparison.Ordinal) && p.Length > 2)
                {
                    kind = p[2];
                }
                else if (p.StartsWith("L=", StringComparison.Ordinal))
                {
                    layer = p.Substring(2);
                }
                else if (p.StartsWith("A=", StringComparison.Ordinal))
                {
                    anchors = p.Substring(2);
                }
                else if (p.StartsWith("z=", StringComparison.Ordinal))
                {
                    int.TryParse(p.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out fontSize);
                }
            }
            if (path != null)
            {
                Bind(id, path, kind, layer, fontSize, anchors);
            }
            if (!ents.TryGetValue(id, out Ent e) || e.T == null)
            {
                return;
            }
            Save(e);
            for (int i = 1; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == "x")
                {
                    if (e.Proxy)
                    {
                        Destroy(e.T.gameObject);
                        ents.Remove(id);
                    }
                    else
                    {
                        e.T.gameObject.SetActive(false);
                    }
                    return;
                }
                if (p.Length < 2 || p[1] != '=')
                {
                    continue;
                }
                string value = p.Substring(2);
                switch (p[0])
                {
                    case 'a':
                        e.T.gameObject.SetActive(value == "1");
                        break;
                    case 'p':
                        if (Mirror.TryInts(value, v3))
                        {
                            var target = new Vector3(v3[0] / 100f, v3[1] / 100f, v3[2] / 100f);
                            if (!e.HasTarget)
                            {
                                e.HasTarget = true;
                                moving.Add(e);
                                e.T.localPosition = target;
                            }
                            e.Target = target;
                        }
                        break;
                    case 'r':
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rz))
                        {
                            Vector3 euler = e.T.localEulerAngles;
                            e.T.localEulerAngles = new Vector3(euler.x, euler.y, rz / 10f);
                        }
                        break;
                    case 's':
                        if (Mirror.TryInts(value, v2))
                        {
                            e.T.localScale = new Vector3(v2[0] / 1000f, v2[1] / 1000f, e.T.localScale.z);
                        }
                        break;
                    case 'e':
                        SetEnabled(e, value == "1");
                        break;
                    case 'i':
                        SetSprite(e, LookupSprite(Mirror.Unesc(value)));
                        break;
                    case 'f':
                        if (e.Sr != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flip))
                        {
                            e.Sr.flipX = (flip & 1) != 0;
                            e.Sr.flipY = (flip & 2) != 0;
                        }
                        break;
                    case 'c':
                        if (uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint col))
                        {
                            SetColor(e, Mirror.Unpack(col));
                        }
                        break;
                    case 'd':
                        if (e.Rt != null && Mirror.TryInts(value, v2))
                        {
                            e.Rt.sizeDelta = new Vector2(v2[0] / 100f, v2[1] / 100f);
                        }
                        break;
                    case 'o':
                        if (e.Cam != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ortho))
                        {
                            e.Cam.orthographicSize = ortho / 100f;
                        }
                        break;
                    case 'F':
                        if (e.Img != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fill))
                        {
                            e.Img.fillAmount = fill / 1000f;
                        }
                        break;
                    case 't':
                        if (e.Txt != null)
                        {
                            e.Txt.text = Mirror.Unesc(value);
                        }
                        break;
                }
            }
        }

        private void Bind(int id, string path, char kind, string layer, int fontSize, string anchors)
        {
            if (!paths.TryGetValue(path, out Transform t) || t == null)
            {
                t = CreateProxy(path, kind, layer, fontSize, anchors);
                if (t == null)
                {
                    return;
                }
            }
            if (ents.TryGetValue(id, out Ent known) && known.T == t)
            {
                return;
            }
            var e = new Ent
            {
                T = t,
                Proxy = proxies.Contains(t.gameObject),
                Sr = t.GetComponent<SpriteRenderer>(),
                Img = t.GetComponent<Image>(),
                Txt = t.GetComponent<Text>(),
                Rt = t as RectTransform,
                Cam = t.GetComponent<Camera>(),
            };
            ents[id] = e;
        }

        /// <summary>An object that only exists on the player's side (spawned during play).</summary>
        private readonly int[] v6 = new int[6];

        private Transform CreateProxy(string path, char kind, string layer, int fontSize, string anchors)
        {
            int cut = path.LastIndexOf('/');
            if (cut < 0 || kind == '\0')
            {
                return null;
            }
            string parentPath = path.Substring(0, cut);
            Transform parent = null;
            if (parentPath.Length > 0 && (!paths.TryGetValue(parentPath, out parent) || parent == null))
            {
                if (misses.Add(parentPath) && misses.Count <= 20)
                {
                    Plugin.Log.LogInfo("Zuschauen: Elternobjekt fehlt hier: " + parentPath);
                }
                return null;
            }
            string leaf = path.Substring(cut + 1);
            int hash = leaf.LastIndexOf('#');
            string name = hash > 0 ? leaf.Substring(0, hash) : leaf;
            bool rect = kind == 'g' || kind == 'x' || kind == 'r';
            GameObject go = rect ? new GameObject(name, typeof(RectTransform)) : new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            else
            {
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            if (go.transform is RectTransform rt && anchors != null && Mirror.TryInts(anchors, v6))
            {
                rt.anchorMin = new Vector2(v6[0] / 1000f, v6[1] / 1000f);
                rt.anchorMax = new Vector2(v6[2] / 1000f, v6[3] / 1000f);
                rt.pivot = new Vector2(v6[4] / 1000f, v6[5] / 1000f);
            }
            switch (kind)
            {
                case 's':
                    SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        string[] l = layer.Split(',');
                        sr.sortingLayerName = Mirror.Unesc(l[0]);
                        if (l.Length > 1 && int.TryParse(l[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int order))
                        {
                            sr.sortingOrder = order;
                        }
                    }
                    break;
                case 'g':
                    go.AddComponent<Image>().raycastTarget = false;
                    break;
                case 'x':
                    Text text = go.AddComponent<Text>();
                    text.font = GameBridge.Font;
                    text.fontSize = fontSize > 0 ? fontSize : 14;
                    text.alignment = TextAnchor.MiddleCenter;
                    text.horizontalOverflow = HorizontalWrapMode.Overflow;
                    text.raycastTarget = false;
                    break;
            }
            proxies.Add(go);
            paths[path] = go.transform;
            return go.transform;
        }

        private void Save(Ent e)
        {
            if (e.Saved || e.Proxy)
            {
                return;
            }
            e.Saved = true;
            e.Active = e.T.gameObject.activeSelf;
            e.Pos = e.T.localPosition;
            e.Rot = e.T.localRotation;
            e.Scale = e.T.localScale;
            if (e.Rt != null)
            {
                e.Size = e.Rt.sizeDelta;
            }
            if (e.Sr != null)
            {
                e.Enabled = e.Sr.enabled;
                e.Sprite = e.Sr.sprite;
                e.FlipX = e.Sr.flipX;
                e.FlipY = e.Sr.flipY;
                e.Color = e.Sr.color;
            }
            else if (e.Img != null)
            {
                e.Enabled = e.Img.enabled;
                e.Sprite = e.Img.sprite;
                e.Color = e.Img.color;
                e.Fill = e.Img.fillAmount;
            }
            else if (e.Txt != null)
            {
                e.Enabled = e.Txt.enabled;
                e.Text = e.Txt.text;
                e.Color = e.Txt.color;
            }
            if (e.Cam != null)
            {
                e.Ortho = e.Cam.orthographicSize;
            }
        }

        private static void SetEnabled(Ent e, bool on)
        {
            if (e.Sr != null)
            {
                e.Sr.enabled = on;
            }
            else if (e.Img != null)
            {
                e.Img.enabled = on;
            }
            else if (e.Txt != null)
            {
                e.Txt.enabled = on;
            }
        }

        private static void SetSprite(Ent e, Sprite sprite)
        {
            if (e.Sr != null)
            {
                e.Sr.sprite = sprite;
            }
            else if (e.Img != null)
            {
                e.Img.sprite = sprite;
            }
        }

        private static void SetColor(Ent e, Color32 color)
        {
            if (e.Sr != null)
            {
                e.Sr.color = color;
            }
            else if (e.Img != null)
            {
                e.Img.color = color;
            }
            else if (e.Txt != null)
            {
                e.Txt.color = color;
            }
        }

        private readonly HashSet<string> misses = new HashSet<string>();

        private Sprite LookupSprite(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            // a full scan only for keys not seen missing before, at most every 2 s
            if (sprites == null || (!sprites.ContainsKey(key) && !misses.Contains(key) && Time.unscaledTime - spritesAt > 2f))
            {
                spritesAt = Time.unscaledTime;
                sprites = new Dictionary<string, Sprite>();
                foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    string k = Mirror.SpriteKey(sprite);
                    if (!sprites.ContainsKey(k))
                    {
                        sprites[k] = sprite;
                    }
                }
            }
            if (!sprites.TryGetValue(key, out Sprite found) && misses.Add(key) && misses.Count <= 20)
            {
                Plugin.Log.LogInfo("Zuschauen: Sprite fehlt hier: " + key);
            }
            return found;
        }

        /// <summary>Positions glide towards the streamed ones (10 updates per second).</summary>
        private void LateUpdate()
        {
            if (!Showing || moving.Count == 0)
            {
                return;
            }
            float k = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            for (int i = moving.Count - 1; i >= 0; i--)
            {
                Ent e = moving[i];
                if (e.T == null)
                {
                    moving.RemoveAt(i);
                    continue;
                }
                Vector3 current = e.T.localPosition;
                float snap = e.Rt != null ? 300f : 4f;
                e.T.localPosition = (current - e.Target).sqrMagnitude > snap * snap ? e.Target : Vector3.Lerp(current, e.Target, k);
            }
        }
    }
}
