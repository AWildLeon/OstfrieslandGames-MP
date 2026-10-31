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
    /// Units: positions and sizes 1/100, rotation 1/10 degree, scale and fill 1/1000.
    /// The wire format is in MirrorWire.
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
            // the tea ceremony's order bubbles are marked as hints by the game, but they are
            // part of the play (what each guest wants); its key legends are prompts
            return promptRoots.Contains(t.GetInstanceID())
                || (t.GetComponent<IngameHudHint>() != null && !t.name.StartsWith("Bubble", StringComparison.Ordinal));
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

        public static uint Pack(Color32 c) => ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;

        public static Color32 Unpack(uint v) => new Color32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>
    /// Wire format of one VIEW payload (base64 in the line): a header byte (bit 0 = stream
    /// reset: string table and object bindings start over, bit 1 = rest is deflated), then
    /// records until the end. A record is: varint id, varint field mask (Mirror.F*), then the
    /// fields in bit order. Numbers are zigzag varints of the difference to the value last
    /// sent for that object (0 after (re)binding, so the first value is absolute). Strings
    /// (paths, sprite keys, sorting layers) go through a string table: varint 2*index for a
    /// known one, 2*index+1 followed by length + UTF-8 for a new one.
    /// </summary>
    internal static class MirrorWire
    {
        public const int FPath = 1 << 0;
        public const int FSpawn = 1 << 1;
        public const int FActive = 1 << 2;
        public const int FActiveOn = 1 << 3;
        public const int FPx = 1 << 4;
        public const int FPy = 1 << 5;
        public const int FPz = 1 << 6;
        public const int FRot = 1 << 7;
        public const int FSx = 1 << 8;
        public const int FSy = 1 << 9;
        public const int FEnabled = 1 << 10;
        public const int FEnabledOn = 1 << 11;
        public const int FSprite = 1 << 12;
        public const int FFlip = 1 << 13;
        public const int FColor = 1 << 14;
        public const int FDx = 1 << 15;
        public const int FDy = 1 << 16;
        public const int FOrtho = 1 << 17;
        public const int FFill = 1 << 18;
        public const int FText = 1 << 19;
        public const int FRemoved = 1 << 20;

        public const byte HeaderReset = 1;
        public const byte HeaderDeflate = 2;

        /// <summary>Raw payload per line; base64 keeps the line below Protocol.MaxViewChars.</summary>
        public const int MaxPayload = 8000;

        private static bool deflateBroken;

        /// <summary>Deflates when that is smaller (and the runtime has zlib); null otherwise.</summary>
        public static byte[] TryDeflate(byte[] data, int offset, int count)
        {
            if (deflateBroken || count < 64)
            {
                return null;
            }
            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                    {
                        z.Write(data, offset, count);
                    }
                    return ms.Length < count ? ms.ToArray() : null;
                }
            }
            catch (Exception ex)
            {
                deflateBroken = true;
                Plugin.Log.LogWarning("Zuschauer-Spiegel: Kompression nicht verfügbar: " + ex.Message);
                return null;
            }
        }

        public static byte[] Inflate(byte[] data, int offset)
        {
            using (var input = new System.IO.MemoryStream(data, offset, data.Length - offset))
            using (var z = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress))
            using (var output = new System.IO.MemoryStream())
            {
                var buffer = new byte[4096];
                int read;
                while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > 1 << 20)
                    {
                        throw new System.IO.InvalidDataException("zu groß");
                    }
                }
                return output.ToArray();
            }
        }
    }

    /// <summary>Growable byte buffer with varints.</summary>
    internal sealed class MirrorWriter
    {
        public byte[] Data = new byte[1024];
        public int Length;

        public void Clear() => Length = 0;

        public void Byte(int b)
        {
            if (Length == Data.Length)
            {
                Array.Resize(ref Data, Data.Length * 2);
            }
            Data[Length++] = (byte)b;
        }

        public void UInt(uint v)
        {
            while (v >= 0x80)
            {
                Byte((int)(v & 0x7F) | 0x80);
                v >>= 7;
            }
            Byte((int)v);
        }

        public void Int(int v) => UInt((uint)((v << 1) ^ (v >> 31)));

        public void Raw32(uint v)
        {
            Byte((int)(v >> 24));
            Byte((int)(v >> 16));
            Byte((int)(v >> 8));
            Byte((int)v);
        }

        public void Text(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            UInt((uint)bytes.Length);
            foreach (byte b in bytes)
            {
                Byte(b);
            }
        }

        public void Append(MirrorWriter other)
        {
            for (int i = 0; i < other.Length; i++)
            {
                Byte(other.Data[i]);
            }
        }
    }

    internal sealed class MirrorReader
    {
        private readonly byte[] data;
        private int pos;

        public MirrorReader(byte[] data, int pos)
        {
            this.data = data;
            this.pos = pos;
        }

        public bool More => pos < data.Length;

        public int Byte()
        {
            if (pos >= data.Length)
            {
                throw new System.IO.EndOfStreamException();
            }
            return data[pos++];
        }

        public uint UInt()
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                int b = Byte();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return v;
                }
            }
            throw new System.IO.InvalidDataException("varint");
        }

        public int Int()
        {
            uint v = UInt();
            return (int)(v >> 1) ^ -(int)(v & 1);
        }

        public uint Raw32() => ((uint)Byte() << 24) | ((uint)Byte() << 16) | ((uint)Byte() << 8) | (uint)Byte();

        public string Text()
        {
            int len = (int)UInt();
            if (len < 0 || pos + len > data.Length)
            {
                throw new System.IO.EndOfStreamException();
            }
            string s = Encoding.UTF8.GetString(data, pos, len);
            pos += len;
            return s;
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
            public MirrorSnap Sent;
            public bool HasSent;
            public bool PathSent;
            public bool Removed;
            public bool TransformRead;

            /// <summary>Values the watcher last got (base of the differences), zero until bound.</summary>
            public MirrorSnap Wire;

            /// <summary>Out of view when last sent; then nothing more until it comes back.</summary>
            public bool SentHidden;
            public bool PendingHidden;

            /// <summary>Mirrored parent; a new object goes out only after its new parent.</summary>
            public Ent Parent;
        }

        private static MirrorSender instance;
        private readonly List<Ent> ents = new List<Ent>();
        private readonly Dictionary<int, Ent> byTransform = new Dictionary<int, Ent>();
        private readonly MirrorWriter frameBuf = new MirrorWriter();
        private readonly MirrorWriter rec = new MirrorWriter();
        private readonly Dictionary<string, int> strings = new Dictionary<string, int>();
        private readonly List<string> newStrings = new List<string>();
        private bool resetPending;
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
            if (t.parent != null && byTransform.TryGetValue(t.parent.GetInstanceID(), out Ent parent))
            {
                e.Parent = parent;
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
                nextScan = now + 0.3f;
                Mirror.Walk(scene, (t, path) => Add(t, path, spawned: true));
            }
            if (now < nextSend)
            {
                return;
            }
            nextSend = now + 0.1f;
            if (fullState)
            {
                // watchers start over: new string table, every object bound again
                fullState = false;
                resetPending = true;
                strings.Clear();
                cursor = 0;
                // everything once, also what looks unchanged: scenes pick some things at random
                // when they start (decoration, texts), so the watcher's copy may differ there
                foreach (Ent e in ents)
                {
                    e.Wire = default;
                    e.PathSent = false;
                    e.HasSent = false;
                    e.Sent = default;
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
            frameBuf.Clear();
            UpdateNearView();
            int count = ents.Count;
            int i = 0;
            for (; i < count; i++)
            {
                int index = (cursor + i) % count;
                Ent e = ents[index];
                rec.Clear();
                newStrings.Clear();
                if (!Encode(e))
                {
                    continue;
                }
                if (rec.Length > MirrorWire.MaxPayload)
                {
                    // never send a line the server would refuse (and drop the player for)
                    Plugin.Log.LogWarning($"Zuschauer-Spiegel: Eintrag zu groß ({rec.Length} Bytes), übersprungen: {e.Path}");
                    Commit(e);
                    continue;
                }
                if (frameBuf.Length + rec.Length > MirrorWire.MaxPayload && frameBuf.Length > 0)
                {
                    break;
                }
                frameBuf.Append(rec);
                foreach (string added in newStrings)
                {
                    strings[added] = strings.Count;
                }
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
            if (frameBuf.Length == 0 && !resetPending)
            {
                return;
            }
            byte header = resetPending ? MirrorWire.HeaderReset : (byte)0;
            resetPending = false;
            byte[] packed = MirrorWire.TryDeflate(frameBuf.Data, 0, frameBuf.Length);
            byte[] payload;
            if (packed != null)
            {
                payload = new byte[packed.Length + 1];
                Buffer.BlockCopy(packed, 0, payload, 1, packed.Length);
                header |= MirrorWire.HeaderDeflate;
            }
            else
            {
                payload = new byte[frameBuf.Length + 1];
                Buffer.BlockCopy(frameBuf.Data, 0, payload, 1, frameBuf.Length);
            }
            payload[0] = header;
            s.SendMirror(Protocol.View, Protocol.Int(s.MatchId), Protocol.Int(++frame), Convert.ToBase64String(payload));
        }

        private MirrorSnap pending;

        /// <summary>Longer texts are cut; nothing on screen needs more.</summary>
        private const int MaxText = 400;

        private bool viewKnown;
        private Rect nearView;

        /// <summary>Camera view plus margin, once per line.</summary>
        private void UpdateNearView()
        {
            Camera cam = Camera.main;
            viewKnown = cam != null && cam.orthographic;
            if (viewKnown)
            {
                float h = cam.orthographicSize;
                float w = h * cam.aspect;
                Vector3 c = cam.transform.position;
                nearView = new Rect(c.x - w * 1.5f, c.y - h * 1.5f, w * 3f, h * 3f);
            }
        }

        private bool NearView(SpriteRenderer sr)
        {
            if (!viewKnown)
            {
                return sr.isVisible;
            }
            Bounds b = sr.bounds;
            return b.max.x >= nearView.xMin && b.min.x <= nearView.xMax && b.max.y >= nearView.yMin && b.min.y <= nearView.yMax;
        }

        private void Str(string value)
        {
            value = value ?? string.Empty;
            if (strings.TryGetValue(value, out int index))
            {
                rec.UInt((uint)index * 2);
                return;
            }
            int pendingIndex = newStrings.IndexOf(value);
            if (pendingIndex >= 0)
            {
                rec.UInt((uint)(strings.Count + pendingIndex) * 2);
                return;
            }
            newStrings.Add(value);
            rec.UInt(1);
            rec.Text(value);
        }

        /// <summary>
        /// Sprites far off screen wait until they come near: their state is sent then (the
        /// watcher could not see them anyway). "Near" is the camera view plus half its size on
        /// every side, so things driving in (cars) are there before they appear. The update
        /// that takes something out of that area still goes out, so nothing stays behind.
        /// </summary>
        private bool OffScreen(Ent e)
        {
            bool hidden = e.Sr != null && e.T.childCount == 0 && e.Sr.enabled && e.T.gameObject.activeInHierarchy && !NearView(e.Sr);
            bool skip = hidden && e.HasSent && e.PathSent && e.SentHidden;
            e.PendingHidden = hidden;
            return skip;
        }

        /// <summary>Writes the record for one object into rec; false = nothing to send.</summary>
        private bool Encode(Ent e)
        {
            if (e.T == null)
            {
                if (e.Removed || (e.Spawned && !e.PathSent))
                {
                    e.Removed = true;
                    return false;
                }
                rec.UInt((uint)e.Id);
                rec.UInt((uint)(MirrorWire.FRemoved | (e.PathSent ? 0 : MirrorWire.FPath)));
                if (!e.PathSent)
                {
                    Str(e.Path);
                }
                pendingRemoved = true;
                return true;
            }
            if (OffScreen(e))
            {
                return false;
            }
            if (!e.PathSent && e.Parent != null && e.Parent.Spawned && !e.Parent.PathSent && !e.Parent.Removed)
            {
                // the watcher has to create the parent first
                return false;
            }
            bool transformToo = e.T.hasChanged || !e.HasSent;
            MirrorSnap now = Read(e, transformToo);
            MirrorSnap old = e.Sent;
            MirrorSnap wire = e.PathSent ? e.Wire : default;
            bool all = !e.HasSent;
            bool component = e.Sr != null || e.Img != null || e.Txt != null;
            int mask = 0;
            if (!e.PathSent)
            {
                mask |= MirrorWire.FPath;
                if (e.Spawned)
                {
                    mask |= MirrorWire.FSpawn;
                }
            }
            if (all || now.Active != old.Active)
            {
                mask |= MirrorWire.FActive | (now.Active ? MirrorWire.FActiveOn : 0);
            }
            if (all || now.Px != old.Px)
            {
                mask |= MirrorWire.FPx;
            }
            if (all || now.Py != old.Py)
            {
                mask |= MirrorWire.FPy;
            }
            if (all || now.Pz != old.Pz)
            {
                mask |= MirrorWire.FPz;
            }
            if (all || now.Rz != old.Rz)
            {
                mask |= MirrorWire.FRot;
            }
            if (all || now.Sx != old.Sx)
            {
                mask |= MirrorWire.FSx;
            }
            if (all || now.Sy != old.Sy)
            {
                mask |= MirrorWire.FSy;
            }
            if (component && (all || now.Enabled != old.Enabled))
            {
                mask |= MirrorWire.FEnabled | (now.Enabled ? MirrorWire.FEnabledOn : 0);
            }
            if ((e.Sr != null || e.Img != null) && (all || now.Sprite != old.Sprite))
            {
                mask |= MirrorWire.FSprite;
            }
            if (e.Sr != null && (all || now.Flip != old.Flip))
            {
                mask |= MirrorWire.FFlip;
            }
            if (component && (all || now.Color != old.Color))
            {
                mask |= MirrorWire.FColor;
            }
            if (e.Rt != null && (all || now.Dx != old.Dx))
            {
                mask |= MirrorWire.FDx;
            }
            if (e.Rt != null && (all || now.Dy != old.Dy))
            {
                mask |= MirrorWire.FDy;
            }
            if (e.Cam != null && (all || now.Ortho != old.Ortho))
            {
                mask |= MirrorWire.FOrtho;
            }
            if (e.Img != null && (all || now.Fill != old.Fill))
            {
                mask |= MirrorWire.FFill;
            }
            if (e.Txt != null && (all || !string.Equals(now.Text, old.Text, StringComparison.Ordinal)))
            {
                mask |= MirrorWire.FText;
            }
            pending = now;
            pendingRemoved = false;
            e.TransformRead = transformToo;
            // the path alone (nothing differs from the loaded scene) needs no record
            if ((mask & ~(MirrorWire.FPath | MirrorWire.FActiveOn | MirrorWire.FEnabledOn)) == 0
                && (mask & MirrorWire.FSpawn) == 0)
            {
                e.Sent = now;
                e.SentHidden = e.PendingHidden;
                if (transformToo)
                {
                    e.T.hasChanged = false;
                }
                return false;
            }
            rec.UInt((uint)e.Id);
            rec.UInt((uint)mask);
            if ((mask & MirrorWire.FPath) != 0)
            {
                Str(e.Path);
            }
            if ((mask & MirrorWire.FSpawn) != 0)
            {
                rec.Byte(Kind(e));
                Str(e.Sr != null ? e.Sr.sortingLayerName : string.Empty);
                rec.Int(e.Sr != null ? e.Sr.sortingOrder : 0);
                rec.UInt((uint)(e.Txt != null ? e.Txt.fontSize : 0));
                if (e.Rt != null)
                {
                    rec.Int(Mirror.Q(e.Rt.anchorMin.x, 1000f));
                    rec.Int(Mirror.Q(e.Rt.anchorMin.y, 1000f));
                    rec.Int(Mirror.Q(e.Rt.anchorMax.x, 1000f));
                    rec.Int(Mirror.Q(e.Rt.anchorMax.y, 1000f));
                    rec.Int(Mirror.Q(e.Rt.pivot.x, 1000f));
                    rec.Int(Mirror.Q(e.Rt.pivot.y, 1000f));
                }
            }
            if ((mask & MirrorWire.FPx) != 0)
            {
                rec.Int(now.Px - wire.Px);
                wire.Px = now.Px;
            }
            if ((mask & MirrorWire.FPy) != 0)
            {
                rec.Int(now.Py - wire.Py);
                wire.Py = now.Py;
            }
            if ((mask & MirrorWire.FPz) != 0)
            {
                rec.Int(now.Pz - wire.Pz);
                wire.Pz = now.Pz;
            }
            if ((mask & MirrorWire.FRot) != 0)
            {
                rec.Int(now.Rz - wire.Rz);
                wire.Rz = now.Rz;
            }
            if ((mask & MirrorWire.FSx) != 0)
            {
                rec.Int(now.Sx - wire.Sx);
                wire.Sx = now.Sx;
            }
            if ((mask & MirrorWire.FSy) != 0)
            {
                rec.Int(now.Sy - wire.Sy);
                wire.Sy = now.Sy;
            }
            if ((mask & MirrorWire.FSprite) != 0)
            {
                Str(Mirror.SpriteKey(now.Sprite));
            }
            if ((mask & MirrorWire.FFlip) != 0)
            {
                rec.Byte(now.Flip);
            }
            if ((mask & MirrorWire.FColor) != 0)
            {
                rec.Raw32(now.Color);
            }
            if ((mask & MirrorWire.FDx) != 0)
            {
                rec.Int(now.Dx - wire.Dx);
                wire.Dx = now.Dx;
            }
            if ((mask & MirrorWire.FDy) != 0)
            {
                rec.Int(now.Dy - wire.Dy);
                wire.Dy = now.Dy;
            }
            if ((mask & MirrorWire.FOrtho) != 0)
            {
                rec.Int(now.Ortho - wire.Ortho);
                wire.Ortho = now.Ortho;
            }
            if ((mask & MirrorWire.FFill) != 0)
            {
                rec.Int(now.Fill - wire.Fill);
                wire.Fill = now.Fill;
            }
            if ((mask & MirrorWire.FText) != 0)
            {
                string text = now.Text ?? string.Empty;
                rec.Text(text.Length > MaxText ? text.Substring(0, MaxText) : text);
            }
            pendingWire = wire;
            return true;
        }

        private bool pendingRemoved;
        private MirrorSnap pendingWire;

        private void Commit(Ent e)
        {
            e.PathSent = true;
            if (pendingRemoved)
            {
                e.Removed = true;
                return;
            }
            e.Sent = pending;
            e.Wire = pendingWire;
            e.HasSent = true;
            e.SentHidden = e.PendingHidden;
            if (e.TransformRead)
            {
                e.T.hasChanged = false;
            }
        }

        private static int Kind(Ent e)
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

            /// <summary>Last streamed values (differences add up on these).</summary>
            public MirrorSnap Wire;

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

        /// <summary>Objects bound to the stream (for the game tests).</summary>
        public int BoundCount => ents.Count;

        /// <summary>Figure the scene was built with.</summary>
        private string builtFigure;

        // the hot seat player whose figure was swapped while the scene was built
        private HotSeatPlayer swappedPlayer;
        private string swappedFigure;

        /// <summary>
        /// Discipline scenes dress the player in the current hot seat player's figure while they
        /// start. For the watcher that slot gets the watched player's figure just for that, and
        /// its own figure back right after (results and the next round use it again).
        /// </summary>
        private void SwapFigure(string figure)
        {
            RestoreFigure();
            HotSeatMatch match = HotSeatFlowController.Instance != null ? HotSeatFlowController.Instance.Match : null;
            if (match == null || match.IsComplete || string.IsNullOrEmpty(figure))
            {
                return;
            }
            swappedPlayer = match.Players[match.CurrentPlayerIndex];
            swappedFigure = swappedPlayer.CharacterId;
            swappedPlayer.CharacterId = figure;
        }

        private void RestoreFigure()
        {
            if (swappedPlayer != null)
            {
                swappedPlayer.CharacterId = swappedFigure;
                swappedPlayer = null;
            }
        }

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
        private readonly List<string> strings = new List<string>();
        private bool synced;
        private int lastFrame;
        private bool keyRetried;
        private bool viewSeen;
        private string sceneName;
        private int matchId;

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
                    RestoreFigure();
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
                    builtFigure = WatchedCharacter(s);
                    SwapFigure(builtFigure);
                    sceneName = Mirror.SceneFor(s.Discipline);
                    Plugin.Log.LogInfo($"Zuschauen: lade {sceneName} mit Figur {builtFigure}");
                    SceneManager.LoadScene(sceneName);
                }
                return;
            }
            if (Watching != s.WatchTarget && WatchedCharacter(s) != builtFigure)
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
                synced = false;
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
            RestoreFigure();
            OnlineSession s = Session;
            if (!loaded.isLoaded || SceneManager.GetActiveScene() != loaded || s == null || s.MatchId != matchId)
            {
                yield break;
            }
            scene = loaded;
            CollectSceneSprites(scene);
            Freeze(scene);
            Mirror.HidePrompts(scene);
            FindTrails(scene);
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
            RestoreFigure();
            builtFigure = null;
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
            sceneSprites.Clear();
            strings.Clear();
            synced = false;
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

        /// <summary>VIEW playerId frame payload (see MirrorWire)</summary>
        public void Apply(string[] f)
        {
            if (!Showing || f.Length < 4 || !Protocol.TryInt(f[1], out int from) || from != Watching || Watching == 0
                || !Protocol.TryInt(f[2], out int frame))
            {
                return;
            }
            viewSeen = true;
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(f[3]);
            }
            catch (FormatException)
            {
                return;
            }
            if (payload.Length == 0)
            {
                return;
            }
            bool reset = (payload[0] & MirrorWire.HeaderReset) != 0;
            if (reset)
            {
                // everything starts over: back to the loaded scene, new string table
                Revert();
                strings.Clear();
                synced = true;
            }
            else if (!synced || frame != lastFrame + 1)
            {
                // a gap: the differences no longer add up, ask for a full state once in a while
                synced = false;
                if (Time.unscaledTime - keyAt > 2f)
                {
                    keyAt = Time.unscaledTime;
                    Session?.SendMirror(Protocol.ViewKey, Protocol.Int(Session.MatchId));
                }
                lastFrame = frame;
                return;
            }
            lastFrame = frame;
            try
            {
                byte[] body = (payload[0] & MirrorWire.HeaderDeflate) != 0 ? MirrorWire.Inflate(payload, 1) : payload;
                var r = new MirrorReader(body, body == payload ? 1 : 0);
                while (r.More)
                {
                    ApplyRecord(r);
                }
            }
            catch (Exception ex)
            {
                synced = false;
                Plugin.Log.LogInfo("Zuschauen: Daten unlesbar, fordere vollständigen Stand an: " + ex.Message);
            }
        }

        private string ReadStr(MirrorReader r)
        {
            uint v = r.UInt();
            if ((v & 1) != 0)
            {
                string s = r.Text();
                strings.Add(s);
                return s;
            }
            int index = (int)(v >> 1);
            if (index >= strings.Count)
            {
                throw new System.IO.InvalidDataException("unbekannter Text");
            }
            return strings[index];
        }

        private void ApplyRecord(MirrorReader r)
        {
            int id = (int)r.UInt();
            int mask = (int)r.UInt();
            if ((mask & MirrorWire.FPath) != 0)
            {
                string path = ReadStr(r);
                char kind = '\0';
                string layer = null;
                int order = 0;
                int fontSize = 0;
                int[] anchors = null;
                if ((mask & MirrorWire.FSpawn) != 0)
                {
                    kind = (char)r.Byte();
                    layer = ReadStr(r);
                    order = r.Int();
                    fontSize = (int)r.UInt();
                    if (kind == 'g' || kind == 'x' || kind == 'r')
                    {
                        anchors = new int[6];
                        for (int i = 0; i < 6; i++)
                        {
                            anchors[i] = r.Int();
                        }
                    }
                }
                Bind(id, path, kind, layer, order, fontSize, anchors);
            }
            ents.TryGetValue(id, out Ent e);
            if (e != null && e.T == null)
            {
                e = null;
            }
            if ((mask & MirrorWire.FRemoved) != 0)
            {
                if (e != null)
                {
                    if (e.Proxy)
                    {
                        Destroy(e.T.gameObject);
                        ents.Remove(id);
                    }
                    else
                    {
                        Save(e);
                        e.T.gameObject.SetActive(false);
                    }
                }
                return;
            }
            // read every field, apply only to known objects
            MirrorSnap w = e != null ? e.Wire : default;
            bool moved = false;
            if ((mask & MirrorWire.FPx) != 0)
            {
                w.Px += r.Int();
                moved = true;
            }
            if ((mask & MirrorWire.FPy) != 0)
            {
                w.Py += r.Int();
                moved = true;
            }
            if ((mask & MirrorWire.FPz) != 0)
            {
                w.Pz += r.Int();
                moved = true;
            }
            bool rotated = (mask & MirrorWire.FRot) != 0;
            if (rotated)
            {
                w.Rz += r.Int();
            }
            bool scaled = (mask & (MirrorWire.FSx | MirrorWire.FSy)) != 0;
            if ((mask & MirrorWire.FSx) != 0)
            {
                w.Sx += r.Int();
            }
            if ((mask & MirrorWire.FSy) != 0)
            {
                w.Sy += r.Int();
            }
            string sprite = (mask & MirrorWire.FSprite) != 0 ? ReadStr(r) : null;
            int flip = (mask & MirrorWire.FFlip) != 0 ? r.Byte() : -1;
            uint color = (mask & MirrorWire.FColor) != 0 ? r.Raw32() : 0;
            bool sized = (mask & (MirrorWire.FDx | MirrorWire.FDy)) != 0;
            if ((mask & MirrorWire.FDx) != 0)
            {
                w.Dx += r.Int();
            }
            if ((mask & MirrorWire.FDy) != 0)
            {
                w.Dy += r.Int();
            }
            if ((mask & MirrorWire.FOrtho) != 0)
            {
                w.Ortho += r.Int();
            }
            if ((mask & MirrorWire.FFill) != 0)
            {
                w.Fill += r.Int();
            }
            string text = (mask & MirrorWire.FText) != 0 ? r.Text() : null;
            if (e == null)
            {
                return;
            }
            e.Wire = w;
            Save(e);
            if ((mask & MirrorWire.FActive) != 0)
            {
                e.T.gameObject.SetActive((mask & MirrorWire.FActiveOn) != 0);
            }
            if (moved)
            {
                // fields not sent keep their streamed value (or the scene's, before the first one)
                Vector3 current = e.HasTarget ? e.Target : e.T.localPosition;
                var target = new Vector3(
                    (mask & MirrorWire.FPx) != 0 ? w.Px / 100f : current.x,
                    (mask & MirrorWire.FPy) != 0 ? w.Py / 100f : current.y,
                    (mask & MirrorWire.FPz) != 0 ? w.Pz / 100f : current.z);
                if (!e.HasTarget)
                {
                    e.HasTarget = true;
                    moving.Add(e);
                    e.T.localPosition = target;
                }
                e.Target = target;
            }
            if (rotated)
            {
                Vector3 euler = e.T.localEulerAngles;
                e.T.localEulerAngles = new Vector3(euler.x, euler.y, w.Rz / 10f);
            }
            if (scaled)
            {
                Vector3 sc = e.T.localScale;
                e.T.localScale = new Vector3((mask & MirrorWire.FSx) != 0 ? w.Sx / 1000f : sc.x, (mask & MirrorWire.FSy) != 0 ? w.Sy / 1000f : sc.y, sc.z);
            }
            if ((mask & MirrorWire.FEnabled) != 0)
            {
                SetEnabled(e, (mask & MirrorWire.FEnabledOn) != 0);
            }
            if (sprite != null)
            {
                SetSprite(e, LookupSprite(sprite));
            }
            if (flip >= 0 && e.Sr != null)
            {
                e.Sr.flipX = (flip & 1) != 0;
                e.Sr.flipY = (flip & 2) != 0;
            }
            if ((mask & MirrorWire.FColor) != 0)
            {
                SetColor(e, Mirror.Unpack(color));
            }
            if (sized && e.Rt != null)
            {
                Vector2 size = e.Rt.sizeDelta;
                e.Rt.sizeDelta = new Vector2((mask & MirrorWire.FDx) != 0 ? w.Dx / 100f : size.x, (mask & MirrorWire.FDy) != 0 ? w.Dy / 100f : size.y);
            }
            if ((mask & MirrorWire.FOrtho) != 0 && e.Cam != null)
            {
                e.Cam.orthographicSize = w.Ortho / 100f;
            }
            if ((mask & MirrorWire.FFill) != 0 && e.Img != null)
            {
                e.Img.fillAmount = w.Fill / 1000f;
            }
            if (text != null && e.Txt != null)
            {
                e.Txt.text = text;
            }
        }

        private void Bind(int id, string path, char kind, string layer, int order, int fontSize, int[] anchors)
        {
            if (!paths.TryGetValue(path, out Transform t) || t == null)
            {
                t = CreateProxy(path, kind, layer, order, fontSize, anchors);
                if (t == null)
                {
                    ents.Remove(id);
                    return;
                }
            }
            if (ents.TryGetValue(id, out Ent known) && known.T == t)
            {
                known.Wire = default;
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
        private Transform CreateProxy(string path, char kind, string layer, int order, int fontSize, int[] anchors)
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
                go.layer = parent.gameObject.layer;
            }
            else
            {
                SceneManager.MoveGameObjectToScene(go, scene);
            }
            if (go.transform is RectTransform rt && anchors != null)
            {
                rt.anchorMin = new Vector2(anchors[0] / 1000f, anchors[1] / 1000f);
                rt.anchorMax = new Vector2(anchors[2] / 1000f, anchors[3] / 1000f);
                rt.pivot = new Vector2(anchors[4] / 1000f, anchors[5] / 1000f);
            }
            switch (kind)
            {
                case 's':
                    SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                    if (!string.IsNullOrEmpty(layer))
                    {
                        sr.sortingLayerName = layer;
                    }
                    sr.sortingOrder = order;
                    break;
                case 'g':
                    go.AddComponent<Image>().raycastTarget = false;
                    break;
                case 'x':
                    Text text = go.AddComponent<Text>();
                    text.font = GameBridge.Font;
                    text.fontSize = fontSize > 0 ? fontSize : 14;
                    text.raycastTarget = false;
                    text.horizontalOverflow = HorizontalWrapMode.Overflow;
                    text.alignment = TextAnchor.MiddleCenter;
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

        /// <summary>Sprites the frozen scene refers to (built with the watched player's figure).</summary>
        private readonly HashSet<Sprite> sceneSprites = new HashSet<Sprite>();

        private static readonly Dictionary<Type, System.Reflection.FieldInfo[]> spriteFields = new Dictionary<Type, System.Reflection.FieldInfo[]>();

        /// <summary>Collects the sprites in the game scripts' fields (animation frames and the like).</summary>
        private void CollectSceneSprites(Scene frozen)
        {
            sceneSprites.Clear();
            foreach (GameObject root in frozen.GetRootGameObjects())
            {
                foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || mb.GetType().Assembly != Mirror.GameAssembly)
                    {
                        continue;
                    }
                    foreach (System.Reflection.FieldInfo field in SpriteFields(mb.GetType()))
                    {
                        object value = field.GetValue(mb);
                        if (value is Sprite one)
                        {
                            sceneSprites.Add(one);
                        }
                        else if (value is IEnumerable<Sprite> many)
                        {
                            foreach (Sprite sprite in many)
                            {
                                if (sprite != null)
                                {
                                    sceneSprites.Add(sprite);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static System.Reflection.FieldInfo[] SpriteFields(Type type)
        {
            if (!spriteFields.TryGetValue(type, out System.Reflection.FieldInfo[] fields))
            {
                var found = new List<System.Reflection.FieldInfo>();
                for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    foreach (System.Reflection.FieldInfo f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType == typeof(Sprite) || typeof(IEnumerable<Sprite>).IsAssignableFrom(f.FieldType))
                        {
                            found.Add(f);
                        }
                    }
                }
                fields = found.ToArray();
                spriteFields[type] = fields;
            }
            return fields;
        }

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
                    // figures share sprite and texture names; the scene's own sets win
                    string k = Mirror.SpriteKey(sprite);
                    if (!sprites.TryGetValue(k, out Sprite known) || (!sceneSprites.Contains(known) && sceneSprites.Contains(sprite)))
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

        private static readonly System.Reflection.MethodInfo PixelTrailMethod =
            HarmonyLib.AccessTools.Method(typeof(BosselnBallVisual), "UpdatePixelTrail");

        private readonly List<BosselnBallVisual> trails = new List<BosselnBallVisual>();

        /// <summary>
        /// Trails are drawn from where the ball has been, so the watcher draws them itself from
        /// the mirrored ball position (the rest of the ball script stays frozen).
        /// </summary>
        private void FindTrails(Scene frozen)
        {
            trails.Clear();
            if (PixelTrailMethod == null)
            {
                return;
            }
            foreach (GameObject root in frozen.GetRootGameObjects())
            {
                trails.AddRange(root.GetComponentsInChildren<BosselnBallVisual>(true));
            }
        }

        private void DrawTrails()
        {
            for (int i = trails.Count - 1; i >= 0; i--)
            {
                BosselnBallVisual ball = trails[i];
                try
                {
                    if (ball == null)
                    {
                        trails.RemoveAt(i);
                    }
                    else if (ball.isActiveAndEnabled || ball.gameObject.activeInHierarchy)
                    {
                        PixelTrailMethod.Invoke(ball, null);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogInfo("Zuschauen: Spur aus: " + (ex.InnerException ?? ex).Message);
                    trails.RemoveAt(i);
                }
            }
        }

        /// <summary>Positions glide towards the streamed ones (10 updates per second).</summary>
        private void LateUpdate()
        {
            if (!Showing)
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
            DrawTrails();
        }
    }
}
