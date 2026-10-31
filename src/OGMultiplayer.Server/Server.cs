using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OGMultiplayer.Server;

public sealed class ServerOptions
{
    public IPAddress Bind { get; set; } = IPAddress.Any;
    public int Port { get; set; } = Protocol.DefaultPort;
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan MatchTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long a paused player may keep the others waiting once nobody else is playing.
    /// Only time spent paused counts; when it is used up, the last live score counts.
    /// </summary>
    public TimeSpan PauseGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Players who are done may watch the best player still playing (scene mirror).</summary>
    public bool Spectate { get; set; } = true;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxConnections { get; set; } = 256;
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Server password; null or empty = open server.</summary>
    public string? Password { get; set; }

    public int MaxAuthFailures { get; set; } = 5;
    public TimeSpan AuthFailureWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>JSON file for bans and admin password changes; null = nothing persists.</summary>
    public string? StateFile { get; set; }

    /// <summary>Public server: legal texts required, consent before joining, stricter limits.</summary>
    public bool Public { get; set; }

    public string ServerName { get; set; } = "Ostfriesland Games Server";

    /// <summary>Message of the day, shown in the room browser.</summary>
    public string? Motd { get; set; }

    public string? ImprintFile { get; set; }
    public string? PrivacyFile { get; set; }
    public string? RulesFile { get; set; }

    /// <summary>0 = unlimited.</summary>
    public int MaxRooms { get; set; }

    /// <summary>Simultaneous connections per IP address; 0 = unlimited.</summary>
    public int MaxConnectionsPerIp { get; set; }

    /// <summary>Write full IP addresses to the log (otherwise the last part is masked).</summary>
    public bool LogIps { get; set; } = true;

    public int MaxLinesPerSecond { get; set; } = 30;

    /// <summary>Read admin commands from stdin.</summary>
    public bool Console { get; set; } = true;

    /// <summary>Unix socket for admin consoles (see AdminSocket.cs); null = none.</summary>
    public string? AdminSocket { get; set; }
}

public sealed partial class GameServer
{
    private readonly ServerOptions options;
    private readonly object sync = new();
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly List<Client> clients = new();
    private readonly Dictionary<IPAddress, List<DateTime>> authFailures = new();
    private Dictionary<IPAddress, BanEntry> bans = new();
    private ServerState state = new();
    private readonly DateTime startedAt = DateTime.UtcNow;
    private CancellationTokenSource shutdown = new();
    private readonly Dictionary<string, string> documents = new(StringComparer.Ordinal);
    private string privacyHash = string.Empty;
    private string lastRoomList = string.Empty;
    private int nextClientId;
    private int nextMatchId;

    /// <summary>Actual TCP port once listening (useful with port 0).</summary>
    public int BoundPort { get; private set; }

    /// <summary>Completed once the server listens.</summary>
    public Task Started => started.Task;

    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GameServer(ServerOptions options)
    {
        this.options = options;
    }

    public async Task RunAsync(CancellationToken externalToken)
    {
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        CancellationToken token = shutdown.Token;
        if (!string.IsNullOrEmpty(options.Password))
        {
            configuredKey = Protocol.ServerKey(options.Password);
            options.Password = null;
        }
        LoadState();
        LoadDocuments();
        var listener = new TcpListener(options.Bind, options.Port);
        if (options.Bind.Equals(IPAddress.IPv6Any))
        {
            // .NET opens IPv6 sockets with IPV6_V6ONLY; "::" is meant to take IPv4 too
            // (clients then arrive as ::ffff:a.b.c.d, mapped back by ServerState.Normalize).
            listener.Server.DualMode = true;
        }
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        StartAdminSocket(token);
        started.TrySetResult();
        Log($"Listening on {options.Bind}:{BoundPort} (protocol v{Protocol.Version}, "
            + (PasswordRequired ? "password required)" : "no password)"));
        _ = Task.Run(() => TickLoop(token), token);
        StartAdmin(token);
        using var reg = token.Register(listener.Stop);
        while (!token.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            Client client;
            lock (sync)
            {
                IPAddress? ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (ip != null && bans.TryGetValue(ServerState.Normalize(ip), out BanEntry? ban) && !ban.IsExpired(DateTime.UtcNow))
                {
                    tcp.Close();
                    continue;
                }
                if (ip != null && options.MaxConnectionsPerIp > 0
                    && clients.Count(c => c.Address.Equals(ServerState.Normalize(ip))) >= options.MaxConnectionsPerIp)
                {
                    tcp.Close();
                    continue;
                }
                if (clients.Count >= options.MaxConnections)
                {
                    tcp.Close();
                    continue;
                }
                client = new Client(++nextClientId, tcp);
                clients.Add(client);
            }
            _ = Task.Run(() => ServeAsync(client, token), token);
        }
        Log("Shutting down");
    }

    /// <summary>Sends BYE, flushes and closes the connection.</summary>
    private void Kick(Client client, string reason)
    {
        client.Send(Protocol.Encode(Protocol.Bye, reason));
        client.Closing = true;
        client.Outbox.Writer.TryComplete();
        Disconnect(client, "kicked: " + reason);
    }

    /// <summary>Admin override from the state file, otherwise the configured password.</summary>
    /// <summary>Key derived from the configured password; the password itself is not kept.</summary>
    private string? configuredKey;

    /// <summary>Admin override from the state file, otherwise the configured password (both as keys).</summary>
    private string? EffectiveKey => state.PasswordDisabled ? null : state.PasswordKeyOverride ?? configuredKey;

    private bool PasswordRequired => !string.IsNullOrEmpty(EffectiveKey);

    private void LoadState()
    {
        if (string.IsNullOrEmpty(options.StateFile))
        {
            return;
        }
        state = ServerState.Load(options.StateFile);
        if (!string.IsNullOrEmpty(state.PasswordOverride))
        {
            // older state files kept the admin password in clear text
            state.PasswordKeyOverride = Protocol.ServerKey(state.PasswordOverride);
            state.PasswordOverride = null;
            SaveState();
        }
        bans = state.Bans.ToDictionary(b => ServerState.Normalize(IPAddress.Parse(b.Ip)));
        PurgeExpiredBans(DateTime.UtcNow);
        Log($"State geladen: {options.StateFile} ({bans.Count} Bans"
            + (state.PasswordDisabled ? ", Passwort per Admin abgeschaltet" : state.PasswordKeyOverride != null ? ", Passwort per Admin gesetzt" : string.Empty) + ")");
    }

    /// <summary>Drops bans past their expiry, so a lifted ban's IP leaves the state file too.</summary>
    private void PurgeExpiredBans(DateTime now)
    {
        List<IPAddress> expired = bans.Where(b => b.Value.IsExpired(now)).Select(b => b.Key).ToList();
        if (expired.Count == 0)
        {
            return;
        }
        foreach (IPAddress ip in expired)
        {
            bans.Remove(ip);
        }
        // no addresses: the log only ever carries masked IPs
        Log($"{expired.Count} Sperre(n) abgelaufen");
        SaveState();
    }

    /// <summary>Persists bans and password changes. Runs under <c>sync</c>.</summary>
    private void SaveState()
    {
        state.Bans = bans.Values.OrderBy(b => b.CreatedUtc).ToList();
        if (string.IsNullOrEmpty(options.StateFile))
        {
            return;
        }
        state.Save(options.StateFile);
    }

    private async Task ServeAsync(Client client, CancellationToken token)
    {
        Log($"#{client.Id} connected from {LogAddress(client)}");
        client.Send(Protocol.Encode(Protocol.Challenge, Protocol.Int(Protocol.Version), client.Nonce, PasswordRequired ? "1" : "0",
            options.Public ? "1" : "0", privacyHash, options.ServerName));
        // not bound to the shutdown token so a final BYE still gets flushed
        var writer = Task.Run(() => client.WriteLoopAsync(CancellationToken.None));
        try
        {
            using var reader = new StreamReader(client.Stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                string? line = await ReadLineLimitedAsync(reader, token);
                if (line == null)
                {
                    break;
                }
                lock (sync)
                {
                    DateTime now = DateTime.UtcNow;
                    client.LastSeen = now;
                    if (now - client.RateWindowStart >= TimeSpan.FromSeconds(1))
                    {
                        client.RateWindowStart = now;
                        client.RateCount = 0;
                    }
                    if (++client.RateCount > options.MaxLinesPerSecond)
                    {
                        Kick(client, "Zu viele Nachrichten.");
                    }
                    else
                    {
                        Handle(client, Protocol.Decode(line));
                    }
                }
                if (client.Closing)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            lock (sync)
            {
                Disconnect(client, "connection closed");
            }
            client.Outbox.Writer.TryComplete();
            try
            {
                await writer;
            }
            catch
            {
            }
            client.Tcp.Close();
        }
    }

    private static async Task<string?> ReadLineLimitedAsync(StreamReader reader, CancellationToken token)
    {
        var sb = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(0, 1), token);
            if (read == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }
            char c = buffer[0];
            if (c == '\n')
            {
                return sb.ToString();
            }
            sb.Append(c);
            if (sb.Length > Protocol.MaxLineLength)
            {
                throw new InvalidDataException("line too long");
            }
        }
    }

    private async Task TickLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (sync)
            {
                Tick(DateTime.UtcNow);
            }
        }
    }

    // ---- everything below runs under `sync` ----

    private void Tick(DateTime now)
    {
        PurgeExpiredBans(now);
        foreach (Room room in rooms.Values.ToList())
        {
            if (room.Match != null)
            {
                TickBots(room, room.Match, now);
            }
        }
        foreach (Client c in clients.ToList())
        {
            if (now - c.LastSeen > options.IdleTimeout)
            {
                Disconnect(c, "timeout");
                c.Close();
            }
            else if (!c.Authed && !c.Closing && now - c.ConnectedAt > (c.ReadingDocuments ? TimeSpan.FromMinutes(15) : options.HandshakeTimeout))
            {
                Disconnect(c, "no handshake");
                c.Close();
            }
        }
        foreach (IPAddress ip in authFailures.Keys.ToList())
        {
            authFailures[ip].RemoveAll(t => now - t > options.AuthFailureWindow);
            if (authFailures[ip].Count == 0)
            {
                authFailures.Remove(ip);
            }
        }
        foreach (Room room in rooms.Values.ToList())
        {
            Match? m = room.Match;
            if (m == null)
            {
                continue;
            }
            if (!m.GoSent && now - m.BeganAt > options.ReadyTimeout && m.Participants.Any(p => p.Status == Protocol.StatusReady))
            {
                Log($"[{room.Name}] match {m.Id}: ready timeout, starting without everyone");
                SendGo(room, m);
            }
            TickPauses(room, m, now);
            if (room.Match == m && now - m.BeganAt > options.MatchTimeout)
            {
                Log($"[{room.Name}] match {m.Id}: match timeout");
                foreach (Participant p in m.Participants.Where(p => !p.Finished))
                {
                    p.Status = Protocol.StatusLeft;
                    p.Score = 0;
                }
                CheckFinished(room, m);
            }
        }
    }

    private void Handle(Client client, string[] f)
    {
        if (f.Length == 0)
        {
            return;
        }
        string cmd = f[0];
        if (cmd == Protocol.Ping)
        {
            client.Send(Protocol.Encode(Protocol.Pong));
            return;
        }
        if (cmd == Protocol.Doc)
        {
            HandleDoc(client, f);
            return;
        }
        if (cmd == Protocol.Hello)
        {
            HandleHello(client, f);
            return;
        }
        if (!client.Authed)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Nicht angemeldet."));
            return;
        }
        switch (cmd)
        {
            case Protocol.ViewPref:
                client.AllowWatchers = f.ElementAtOrDefault(1) != "0";
                if (client.Room != null)
                {
                    UpdateSpectating(client.Room);
                }
                return;
            case Protocol.Rooms:
                client.Send(RoomListLine());
                return;
            case Protocol.Create:
                HandleCreate(client, f);
                return;
            case Protocol.JoinRoom:
                HandleJoin(client, f);
                return;
        }
        Room? room = client.Room;
        if (room == null)
        {
            if (cmd == Protocol.Char && f.Length > 1 && Protocol.NormalizeId(f[1]) is string id)
            {
                client.Character = id;
            }
            return;
        }
        switch (cmd)
        {
            case Protocol.Char:
                HandleChar(client, room, f);
                break;
            case Protocol.Start:
                HandleStart(client, room, f);
                break;
            case Protocol.Ready:
                HandleReady(client, room, f);
                break;
            case Protocol.Pause:
                HandlePause(client, room, f);
                break;
            case Protocol.View:
                HandleView(client, room, f);
                break;
            case Protocol.ViewKey:
                HandleViewKey(client, room, f);
                break;
            case Protocol.Live:
                HandleLive(client, room, f);
                break;
            case Protocol.Score:
                HandleScore(client, room, f);
                break;
            case Protocol.Leave:
                HandleLeave(client, room, f);
                break;
            case Protocol.Bot:
                HandleBot(client, room, f);
                break;
            case Protocol.LeaveRoom:
                LeaveRoom(client, "Raum verlassen.", notify: true);
                break;
            case Protocol.RoomPassword:
                HandleRoomPassword(client, room, f);
                break;
            case Protocol.KickPlayer:
                HandleKick(client, room, f);
                break;
        }
    }

    // ------------------------------------------------------------------ legal texts

    private void LoadDocuments()
    {
        documents.Clear();
        void Load(string kind, string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            string text = File.ReadAllText(path).Replace("\r\n", "\n").Trim();
            if (text.Length == 0)
            {
                throw new InvalidDataException($"Datei '{path}' ist leer.");
            }
            if (Protocol.EscapeText(text).Length > Protocol.MaxServerLineLength - 64)
            {
                throw new InvalidDataException($"Datei '{path}' ist zu lang (max. ca. 60 000 Zeichen).");
            }
            documents[kind] = text;
        }
        Load(Protocol.DocImprint, options.ImprintFile);
        Load(Protocol.DocPrivacy, options.PrivacyFile);
        Load(Protocol.DocRules, options.RulesFile);
        if (options.Public && (!documents.ContainsKey(Protocol.DocImprint) || !documents.ContainsKey(Protocol.DocPrivacy)))
        {
            throw new InvalidDataException("Ein Public-Server braucht --imprint-file und --privacy-file (Impressum und Datenschutzhinweise).");
        }
        privacyHash = documents.TryGetValue(Protocol.DocPrivacy, out string? privacy) ? Protocol.TextHash(privacy) : string.Empty;
        if (documents.Count > 0)
        {
            Log("Rechtstexte geladen: " + string.Join(", ", documents.Keys) + (privacyHash.Length > 0 ? $" (Datenschutz-Fassung {privacyHash})" : string.Empty));
        }
    }

    private void HandleDoc(Client client, string[] f)
    {
        string kind = f.Length > 1 ? f[1] : string.Empty;
        client.ReadingDocuments = true;
        if (!documents.TryGetValue(kind, out string? text))
        {
            client.Send(Protocol.Encode(Protocol.DocText, kind, string.Empty));
            return;
        }
        client.Send(Protocol.Encode(Protocol.DocText, kind, Protocol.EscapeText(text)));
    }

    private string DocumentList() => string.Join(",", documents.Keys.OrderBy(k => k));

    /// <summary>IP for the log; masked on public servers unless --log-ips.</summary>
    private string LogAddress(Client client) => options.LogIps ? client.Remote : MaskAddress(client.Address);

    /// <summary>The address as the log may show it: IPv4 without its last 8 bits, IPv6 without its last 80.</summary>
    private string MaskAddress(IPAddress address)
    {
        if (options.LogIps)
        {
            return address.ToString();
        }
        byte[] bytes = ServerState.Normalize(address).GetAddressBytes();
        if (bytes.Length == 4)
        {
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.x";
        }
        if (bytes.Length == 16)
        {
            return $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}::x";
        }
        return "?";
    }

    // ------------------------------------------------------------------ login and rooms

    private void HandleHello(Client client, string[] f)
    {
        if (client.Authed)
        {
            return;
        }
        if (f.Length < 6 || !Protocol.TryInt(f[1], out int version) || version != Protocol.Version)
        {
            Reject(client, $"Falsche Protokollversion (Server: v{Protocol.Version}). Bitte Mod und Server aktualisieren.");
            return;
        }
        if (PasswordRequired)
        {
            if (Locked(client))
            {
                Reject(client, "Zu viele falsche Passwörter. Bitte später erneut versuchen.");
                return;
            }
            if (!ProofMatches(client.Nonce, f[5]))
            {
                RecordFailure(client);
                Log($"#{client.Id} wrong password from {LogAddress(client)}");
                Reject(client, "Falsches Server-Passwort.");
                return;
            }
        }
        if (options.Public && privacyHash.Length > 0 && (f.Length < 7 || f[6] != privacyHash))
        {
            Reject(client, "Bitte zuerst die Datenschutzhinweise des Servers lesen und akzeptieren.");
            return;
        }
        string? initials = Protocol.NormalizeInitials(f[2]);
        string? character = Protocol.NormalizeId(f[3]);
        string roomField = f[4].Trim();
        string? roomName = roomField.Length == 0 ? string.Empty : Protocol.NormalizeId(roomField);
        if (initials == null || character == null || roomName == null)
        {
            Reject(client, "Ungültige Initialen, Figur oder Raumname.");
            return;
        }
        client.Initials = initials;
        client.Character = character;
        client.Authed = true;
        Log($"#{client.Id} logged in as {initials} ({character})" + (options.Public && privacyHash.Length > 0 ? $", Datenschutz {privacyHash} akzeptiert" : string.Empty));
        client.Send(Protocol.Encode(Protocol.Welcome, Protocol.Int(client.Id)));
        client.Send(Protocol.Encode(Protocol.ServerInfo, options.ServerName, options.Public ? "1" : "0", options.Motd ?? string.Empty, DocumentList()));
        if (roomName.Length == 0)
        {
            client.Send(RoomListLine());
            return;
        }
        // quick join by name (also creates the room), as in older clients
        if (rooms.TryGetValue(roomName, out Room? existing))
        {
            if (existing.Locked)
            {
                client.Send(Protocol.Encode(Protocol.Error, $"Raum '{roomName}' ist passwortgeschützt."));
                client.Send(RoomListLine());
                return;
            }
            JoinRoom(client, existing);
        }
        else
        {
            CreateRoom(client, roomName, string.Empty);
        }
    }

    private bool Locked(Client client)
    {
        DateTime now = DateTime.UtcNow;
        return authFailures.TryGetValue(client.Address, out List<DateTime>? failures)
            && failures.Count(t => now - t <= options.AuthFailureWindow) >= options.MaxAuthFailures;
    }

    private void RecordFailure(Client client)
    {
        if (!authFailures.TryGetValue(client.Address, out List<DateTime>? failures))
        {
            failures = new List<DateTime>();
            authFailures[client.Address] = failures;
        }
        failures.Add(DateTime.UtcNow);
    }

    private static bool ValidKey(string key) => key.Length == 0 || (key.Length == 64 && key.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));

    private void HandleCreate(Client client, string[] f)
    {
        if (client.Room != null)
        {
            return;
        }
        string? name = f.Length > 1 ? Protocol.NormalizeId(f[1]) : null;
        string key = f.Length > 2 ? f[2].Trim().ToLowerInvariant() : string.Empty;
        if (name == null || !ValidKey(key))
        {
            client.Send(Protocol.Encode(Protocol.Error, "Raumname: 1-32 Zeichen a-z, 0-9, - oder _."));
            return;
        }
        if (rooms.ContainsKey(name))
        {
            client.Send(Protocol.Encode(Protocol.Error, $"Raum '{name}' gibt es schon."));
            return;
        }
        CreateRoom(client, name, key);
    }

    private void CreateRoom(Client client, string name, string key)
    {
        if (options.MaxRooms > 0 && rooms.Count >= options.MaxRooms)
        {
            client.Send(Protocol.Encode(Protocol.Error, $"Der Server hat schon {options.MaxRooms} Räume. Bitte einem bestehenden beitreten."));
            client.Send(RoomListLine());
            return;
        }
        var room = new Room(name) { Key = key.Length > 0 ? key : null };
        rooms[name] = room;
        Log($"[{name}] room created by #{client.Id} {client.Initials}" + (room.Locked ? " (password)" : string.Empty));
        JoinRoom(client, room);
    }

    private void HandleJoin(Client client, string[] f)
    {
        if (client.Room != null)
        {
            return;
        }
        string? name = f.Length > 1 ? Protocol.NormalizeId(f[1]) : null;
        if (name == null || !rooms.TryGetValue(name, out Room? room))
        {
            client.Send(Protocol.Encode(Protocol.Error, "Diesen Raum gibt es nicht mehr."));
            client.Send(RoomListLine());
            return;
        }
        if (room.Locked)
        {
            if (Locked(client))
            {
                client.Send(Protocol.Encode(Protocol.Error, "Zu viele falsche Passwörter. Bitte später erneut versuchen."));
                return;
            }
            string proof = f.Length > 2 ? f[2] : string.Empty;
            byte[] expected = Encoding.ASCII.GetBytes(Protocol.AuthProof(room.Key!, client.Nonce));
            byte[] actual = Encoding.ASCII.GetBytes(proof.Trim().ToLowerInvariant());
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                RecordFailure(client);
                client.Send(Protocol.Encode(Protocol.Error, "Falsches Raum-Passwort."));
                return;
            }
        }
        JoinRoom(client, room);
    }

    private void JoinRoom(Client client, Room room)
    {
        if (room.Members.Count >= Protocol.MaxPlayers)
        {
            client.Send(Protocol.Encode(Protocol.Error, $"Raum '{room.Name}' ist voll ({Protocol.MaxPlayers} Spieler)."));
            client.Send(RoomListLine());
            return;
        }
        client.Room = room;
        room.Members.Add(client);
        Log($"#{client.Id} joined room '{room.Name}' as {client.Initials} ({client.Character})");
        client.Send(Protocol.Encode(Protocol.Joined, room.Name));
        BroadcastLobby(room);
    }

    /// <summary>Takes a client out of its room (leave, kick, disconnect). Empty rooms close.</summary>
    private void LeaveRoom(Client client, string reason, bool notify)
    {
        Room? room = client.Room;
        if (room == null)
        {
            return;
        }
        client.Room = null;
        room.Members.Remove(client);
        Match? m = room.Match;
        Participant? p = m?.Find(client);
        if (m != null && p != null && !p.Finished)
        {
            p.Status = Protocol.StatusLeft;
            p.Score = 0;
        }
        if (notify)
        {
            client.Send(Protocol.Encode(Protocol.RoomLeft, reason));
        }
        if (!room.Members.Any(x => !x.IsBot))
        {
            rooms.Remove(room.Name);
            Log($"[{room.Name}] room closed");
        }
        else
        {
            if (m != null)
            {
                AfterParticipantGone(room, m);
            }
            BroadcastLobby(room);
        }
        BroadcastRoomList();
    }

    private void HandleRoomPassword(Client client, Room room, string[] f)
    {
        if (room.Host != client)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Nur der Host kann das Raum-Passwort ändern."));
            return;
        }
        string key = f.Length > 1 ? f[1].Trim().ToLowerInvariant() : string.Empty;
        if (!ValidKey(key))
        {
            return;
        }
        room.Key = key.Length > 0 ? key : null;
        Log($"[{room.Name}] password {(room.Locked ? "set" : "removed")} by host");
        BroadcastLobby(room);
    }

    private void HandleKick(Client client, Room room, string[] f)
    {
        if (room.Host != client)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Nur der Host kann Spieler entfernen."));
            return;
        }
        if (f.Length < 2 || !Protocol.TryInt(f[1], out int id))
        {
            return;
        }
        Client? target = room.Members.FirstOrDefault(m => m.Id == id && m != client);
        if (target == null)
        {
            return;
        }
        if (room.Match != null)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Während einer Runde geht das nicht."));
            return;
        }
        Log($"[{room.Name}] host removed #{target.Id} {target.Initials}");
        if (target.IsBot)
        {
            room.Members.Remove(target);
            target.Room = null;
            BroadcastLobby(room);
            return;
        }
        LeaveRoom(target, "Der Host hat dich aus dem Raum entfernt.", notify: true);
    }

    private string RoomListLine()
    {
        var fields = new List<string> { Protocol.RoomList, Protocol.Int(options.MaxRooms) };
        foreach (Room r in rooms.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            fields.Add(Protocol.Join(r.Name, Protocol.Int(r.Members.Count), Protocol.Int(Protocol.MaxPlayers),
                r.Match != null ? Protocol.StateMatch : Protocol.StateLobby, r.Locked ? "1" : "0", r.Host?.Initials ?? "-"));
        }
        return Protocol.Encode(fields.ToArray());
    }

    /// <summary>Pushes the room list to everybody in the room browser when it changed.</summary>
    private void BroadcastRoomList()
    {
        string line = RoomListLine();
        if (line == lastRoomList)
        {
            return;
        }
        lastRoomList = line;
        foreach (Client c in clients.Where(c => c.Authed && c.Room == null))
        {
            c.Send(line);
        }
    }

    private void HandleChar(Client client, Room room, string[] f)
    {
        string? character = f.Length > 1 ? Protocol.NormalizeId(f[1]) : null;
        if (character == null || room.Match?.Find(client) != null)
        {
            return;
        }
        client.Character = character;
        BroadcastLobby(room);
    }

    private void HandleStart(Client client, Room room, string[] f)
    {
        if (room.Host != client)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Nur der Host kann eine Disziplin starten."));
            return;
        }
        if (room.Match != null)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Es läuft bereits eine Partie."));
            return;
        }
        if (f.Length < 2 || !Protocol.TryInt(f[1], out int discipline) || discipline < 0 || discipline >= Protocol.DisciplineCount)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Unbekannte Disziplin."));
            return;
        }
        if (room.Members.Count < Protocol.MinPlayers)
        {
            client.Send(Protocol.Encode(Protocol.Error, $"Mindestens {Protocol.MinPlayers} Spieler nötig."));
            return;
        }
        var match = new Match(++nextMatchId, discipline, DateTime.UtcNow);
        int reference = Bots.Reference(humanScores.TryGetValue(discipline, out List<int>? seen) ? seen : new List<int>());
        foreach (Client member in room.Members)
        {
            var participant = new Participant(member);
            if (member.IsBot)
            {
                participant.Status = Protocol.StatusReady;
                participant.BotTarget = Bots.TargetScore(member.BotLevel, reference, random);
                participant.BotDuration = Bots.Duration(random);
            }
            match.Participants.Add(participant);
        }
        room.Match = match;
        var fields = new List<string> { Protocol.Begin, Protocol.Int(match.Id), Protocol.Int(discipline) };
        fields.AddRange(match.Participants.Select(p => Protocol.Join(Protocol.Int(p.Client.Id), p.Initials, p.Character)));
        string begin = Protocol.Encode(fields.ToArray());
        foreach (Client member in room.Members)
        {
            member.Send(begin);
        }
        Log($"[{room.Name}] match {match.Id} started: discipline {discipline}, {match.Participants.Count} players");
        BroadcastLobby(room);
    }

    private Participant? ParticipantFor(Client client, Room room, string[] f)
    {
        Match? m = room.Match;
        if (m == null || f.Length < 2 || !Protocol.TryInt(f[1], out int matchId) || matchId != m.Id)
        {
            return null;
        }
        return m.Find(client);
    }

    private void HandleReady(Client client, Room room, string[] f)
    {
        Participant? p = ParticipantFor(client, room, f);
        Match? m = room.Match;
        if (p == null || m == null || p.Finished)
        {
            return;
        }
        if (m.GoSent)
        {
            p.Status = Protocol.StatusPlaying;
            client.Send(Protocol.Encode(Protocol.Go, Protocol.Int(m.Id)));
            BroadcastLobby(room);
            return;
        }
        p.Status = Protocol.StatusReady;
        if (m.Participants.All(x => x.Status == Protocol.StatusReady || x.Finished))
        {
            SendGo(room, m);
        }
        else
        {
            BroadcastLobby(room);
        }
    }

    private void SendGo(Room room, Match m)
    {
        m.GoSent = true;
        string go = Protocol.Encode(Protocol.Go, Protocol.Int(m.Id));
        foreach (Participant p in m.Participants.Where(x => x.Status == Protocol.StatusReady))
        {
            p.Status = Protocol.StatusPlaying;
            p.BotStartedAt = DateTime.UtcNow;
            p.Client.Send(go);
        }
        Log($"[{room.Name}] match {m.Id}: GO");
        BroadcastLobby(room);
    }

    // ------------------------------------------------------------------ watching

    /// <summary>
    /// Picks whom the finished players watch: the human with the best live score among those
    /// still playing. The pick is sticky so the view does not flip on every score change:
    /// it moves when the target is done, or when someone else leads by 15% for 5 s.
    /// Only the target streams, and only while somebody watches.
    /// </summary>
    private void UpdateSpectating(Room room)
    {
        Match? m = room.Match;
        if (!options.Spectate || m == null || !m.GoSent)
        {
            return;
        }
        DateTime now = DateTime.UtcNow;
        var candidates = m.Participants
            .Where(p => !p.Client.IsBot && p.Client.AllowWatchers && (p.Status == Protocol.StatusPlaying || p.Status == Protocol.StatusPaused) && room.Members.Contains(p.Client))
            .OrderByDescending(p => p.Score).ThenBy(p => p.Client.Id).ToList();
        Participant? best = candidates.FirstOrDefault();
        Participant? target = m.StreamTarget;
        bool targetGone = target == null || !candidates.Contains(target);
        if (targetGone)
        {
            target = best;
        }
        else if (best != null && best != target && best.Score > target!.Score * 1.15 + 10 && now - m.TargetSince > TimeSpan.FromSeconds(5))
        {
            target = best;
        }
        if (target != m.StreamTarget)
        {
            if (m.StreamTarget != null && m.StreamTarget.Client.StreamingMatch == m.Id)
            {
                m.StreamTarget.Client.StreamingMatch = 0;
                m.StreamTarget.Client.Send(Protocol.Encode(Protocol.Stream, Protocol.Int(m.Id), "0"));
            }
            m.StreamTarget = target;
            m.TargetSince = now;
        }
        int targetId = target?.Client.Id ?? 0;
        foreach (Client member in room.Members.Where(c => !c.IsBot))
        {
            Participant? p = m.Find(member);
            bool canWatch = p == null || p.Finished;
            int watch = canWatch ? targetId : 0;
            bool known = member.WatchMatch == m.Id;
            if ((known && member.WatchTarget != watch) || (!known && watch != 0))
            {
                member.WatchMatch = m.Id;
                member.WatchTarget = watch;
                member.Watching = false;
                member.Send(Protocol.Encode(Protocol.Watch, Protocol.Int(m.Id), Protocol.Int(watch)));
            }
        }
        if (target != null)
        {
            bool watched = Watchers(room, m).Any();
            int streaming = watched ? m.Id : 0;
            if (target.Client.StreamingMatch != streaming)
            {
                target.Client.StreamingMatch = streaming;
                target.Client.Send(Protocol.Encode(Protocol.Stream, Protocol.Int(m.Id), watched ? "1" : "0"));
            }
        }
    }

    /// <summary>Ends every stream and tells the watchers there is nobody to watch.</summary>
    private void StopSpectating()
    {
        foreach (Room room in rooms.Values)
        {
            Match? m = room.Match;
            if (m == null)
            {
                continue;
            }
            if (m.StreamTarget != null && m.StreamTarget.Client.StreamingMatch == m.Id)
            {
                m.StreamTarget.Client.StreamingMatch = 0;
                m.StreamTarget.Client.Send(Protocol.Encode(Protocol.Stream, Protocol.Int(m.Id), "0"));
            }
            m.StreamTarget = null;
            foreach (Client member in room.Members.Where(c => c.WatchMatch == m.Id && c.WatchTarget != 0))
            {
                member.WatchTarget = 0;
                member.Watching = false;
                member.Send(Protocol.Encode(Protocol.Watch, Protocol.Int(m.Id), "0"));
            }
        }
    }

    private static IEnumerable<Client> Watchers(Room room, Match m)
    {
        int targetId = m.StreamTarget?.Client.Id ?? 0;
        return room.Members.Where(c => c.Watching && c.WatchMatch == m.Id && c.WatchTarget == targetId && targetId != 0);
    }

    private void HandleViewKey(Client client, Room room, string[] f)
    {
        Match? m = room.Match;
        if (!options.Spectate || m == null || f.Length < 2 || !Protocol.TryInt(f[1], out int matchId) || matchId != m.Id
            || client.WatchMatch != m.Id || client.WatchTarget == 0)
        {
            return;
        }
        client.Watching = true;
        Participant? target = m.StreamTarget;
        if (target == null || target.Client.Id != client.WatchTarget)
        {
            return;
        }
        // (re)start the stream with a full state for the new watcher
        target.Client.StreamingMatch = m.Id;
        target.Client.Send(Protocol.Encode(Protocol.Stream, Protocol.Int(m.Id), "1"));
    }

    private void HandleView(Client client, Room room, string[] f)
    {
        Match? m = room.Match;
        if (m == null || m.StreamTarget?.Client != client || f.Length < 3 || !Protocol.TryInt(f[1], out int matchId) || matchId != m.Id)
        {
            return;
        }
        var fields = new string[f.Length];
        fields[0] = Protocol.View;
        fields[1] = Protocol.Int(client.Id);
        Array.Copy(f, 2, fields, 2, f.Length - 2);
        string line = Protocol.Encode(fields);
        foreach (Client watcher in Watchers(room, m))
        {
            watcher.Send(line);
        }
    }

    private void HandlePause(Client client, Room room, string[] f)
    {
        Participant? p = ParticipantFor(client, room, f);
        if (p == null || p.Finished || f.Length < 3 || (p.Status != Protocol.StatusPlaying && p.Status != Protocol.StatusPaused))
        {
            return;
        }
        string status = f[2] == "1" ? Protocol.StatusPaused : Protocol.StatusPlaying;
        if (status == p.Status)
        {
            return;
        }
        p.Status = status;
        p.PauseAccruedAt = DateTime.UtcNow;
        BroadcastLobby(room);
    }

    /// <summary>
    /// A paused player uses up PauseGrace only while nobody else is still playing, so a
    /// pause cannot hold the room forever. When it is used up, the last live score counts.
    /// </summary>
    private void TickPauses(Room room, Match m, DateTime now)
    {
        if (!m.GoSent || !m.Participants.Any(x => x.Finished))
        {
            return;
        }
        bool counting = m.Participants.All(x => x.Finished || x.Status == Protocol.StatusPaused);
        bool changed = false;
        foreach (Participant p in m.Participants.Where(x => x.Status == Protocol.StatusPaused))
        {
            if (counting)
            {
                int before = GraceLeftSeconds(p);
                p.PauseUsed += now - p.PauseAccruedAt;
                changed |= GraceLeftSeconds(p) != before;
                if (p.PauseUsed >= options.PauseGrace)
                {
                    p.Status = Protocol.StatusDone;
                    RememberHumanScore(m.Discipline, p.Score);
                    Log($"[{room.Name}] match {m.Id}: {p.Initials} paused too long, counts with {p.Score}");
                    changed = true;
                }
            }
            p.PauseAccruedAt = now;
        }
        if (changed)
        {
            CheckFinished(room, m);
        }
    }

    private int GraceLeftSeconds(Participant p)
    {
        double left = (options.PauseGrace - p.PauseUsed).TotalSeconds;
        return Math.Max(0, (int)Math.Ceiling(left));
    }

    /// <summary>Remaining pause grace for the lobby line, empty while it is not running down.</summary>
    private string GraceField(Match? m, Participant? p)
    {
        if (m == null || p == null || p.Status != Protocol.StatusPaused || !m.GoSent
            || !m.Participants.Any(x => x.Finished)
            || !m.Participants.All(x => x.Finished || x.Status == Protocol.StatusPaused))
        {
            return string.Empty;
        }
        return Protocol.Int(GraceLeftSeconds(p));
    }

    private void HandleLive(Client client, Room room, string[] f)
    {
        Participant? p = ParticipantFor(client, room, f);
        if (p == null || p.Finished || f.Length < 3 || !Protocol.TryInt(f[2], out int score))
        {
            return;
        }
        p.Score = Math.Max(0, score);
        BroadcastLobby(room);
    }

    private void HandleScore(Client client, Room room, string[] f)
    {
        Participant? p = ParticipantFor(client, room, f);
        Match? m = room.Match;
        if (p == null || m == null || p.Finished || f.Length < 3 || !Protocol.TryInt(f[2], out int score))
        {
            return;
        }
        p.Score = Math.Max(0, score);
        p.Status = Protocol.StatusDone;
        RememberHumanScore(m.Discipline, p.Score);
        Log($"[{room.Name}] match {m.Id}: {p.Initials} finished with {p.Score}");
        CheckFinished(room, m);
    }

    private void HandleLeave(Client client, Room room, string[] f)
    {
        Participant? p = ParticipantFor(client, room, f);
        Match? m = room.Match;
        if (p == null || m == null || p.Finished)
        {
            return;
        }
        p.Status = Protocol.StatusLeft;
        p.Score = 0;
        Log($"[{room.Name}] match {m.Id}: {p.Initials} left the match");
        AfterParticipantGone(room, m);
    }

    private void AfterParticipantGone(Room room, Match m)
    {
        if (!m.GoSent && m.Participants.Any(x => x.Status == Protocol.StatusReady)
            && m.Participants.All(x => x.Status == Protocol.StatusReady || x.Finished))
        {
            SendGo(room, m);
        }
        CheckFinished(room, m);
    }

    private void CheckFinished(Room room, Match m)
    {
        if (room.Match != m)
        {
            return;
        }
        if (!m.Participants.All(p => p.Finished))
        {
            BroadcastLobby(room);
            return;
        }
        var fields = new List<string> { Protocol.Result, Protocol.Int(m.Id) };
        fields.AddRange(m.Participants.Select(p => Protocol.Join(Protocol.Int(p.Client.Id), Protocol.Int(p.Score))));
        string result = Protocol.Encode(fields.ToArray());
        foreach (Client member in room.Members)
        {
            member.Send(result);
        }
        room.Match = null;
        Log($"[{room.Name}] match {m.Id} finished: " + string.Join(", ", m.Participants.Select(p => $"{p.Initials}={p.Score}")));
        BroadcastLobby(room);
    }

    // ------------------------------------------------------------------ bots

    private readonly Random random = new();

    /// <summary>Recent real scores per discipline; bots aim at their median.</summary>
    private readonly Dictionary<int, List<int>> humanScores = new();

    private void RememberHumanScore(int discipline, int score)
    {
        if (score <= 0)
        {
            return;
        }
        if (!humanScores.TryGetValue(discipline, out List<int>? list))
        {
            list = new List<int>();
            humanScores[discipline] = list;
        }
        list.Add(score);
        if (list.Count > 50)
        {
            list.RemoveAt(0);
        }
    }

    private void HandleBot(Client client, Room room, string[] f)
    {
        if (room.Host != client)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Nur der Host kann Bots verwalten."));
            return;
        }
        if (room.Match != null)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Während einer Runde geht das nicht."));
            return;
        }
        string sub = f.Length > 1 ? f[1] : string.Empty;
        switch (sub)
        {
            case Protocol.BotAdd:
                if (room.Members.Count >= Protocol.MaxPlayers)
                {
                    client.Send(Protocol.Encode(Protocol.Error, $"Raum ist voll ({Protocol.MaxPlayers} Spieler)."));
                    return;
                }
                Client bot = Client.CreateBot(++nextClientId, Bots.PickName(room.Members.Select(m => m.Initials), random), Bots.PickCharacter(random), room.BotLevel);
                bot.Room = room;
                room.Members.Add(bot);
                Log($"[{room.Name}] bot {bot.Initials} added ({Bots.LevelName(bot.BotLevel)})");
                break;
            case Protocol.BotRemove:
                Client? last = room.Members.LastOrDefault(m => m.IsBot);
                if (last == null)
                {
                    return;
                }
                room.Members.Remove(last);
                last.Room = null;
                break;
            case Protocol.BotLevel:
                if (f.Length < 3 || !Protocol.TryInt(f[2], out int level))
                {
                    return;
                }
                room.BotLevel = Bots.ClampLevel(level);
                foreach (Client b in room.Members.Where(m => m.IsBot))
                {
                    b.BotLevel = room.BotLevel;
                }
                break;
            default:
                return;
        }
        BroadcastLobby(room);
    }

    /// <summary>Advances the bots of a running match; they finish at the latest shortly after the last human.</summary>
    private void TickBots(Room room, Match m, DateTime now)
    {
        List<Participant> bots = m.Participants.Where(p => p.Client.IsBot && p.Status == Protocol.StatusPlaying).ToList();
        if (bots.Count == 0)
        {
            return;
        }
        bool humansDone = m.Participants.Where(p => !p.Client.IsBot).All(p => p.Finished);
        foreach (Participant bot in bots)
        {
            TimeSpan elapsed = now - bot.BotStartedAt;
            if (humansDone || elapsed >= bot.BotDuration)
            {
                bot.Score = bot.BotTarget;
                bot.Status = Protocol.StatusDone;
            }
            else
            {
                bot.Score = Bots.LiveScore(bot.BotTarget, elapsed, bot.BotDuration);
            }
        }
        CheckFinished(room, m);
    }

    private void BroadcastLobby(Room room)
    {
        Client? host = room.Host;
        var fields = new List<string>
        {
            Protocol.Lobby,
            room.Name,
            Protocol.Int(host?.Id ?? 0),
            room.Match != null ? Protocol.StateMatch : Protocol.StateLobby,
            Protocol.Int(room.BotLevel),
            room.Locked ? "1" : "0",
        };
        foreach (Client member in room.Members)
        {
            Participant? p = room.Match?.Find(member);
            string status = p?.Status ?? Protocol.StatusLobby;
            int score = p?.Score ?? 0;
            string grace = GraceField(room.Match, p);
            fields.Add(grace.Length == 0
                ? Protocol.Join(Protocol.Int(member.Id), member.Initials, member.Character, status, Protocol.Int(score), member.IsBot ? "1" : "0")
                : Protocol.Join(Protocol.Int(member.Id), member.Initials, member.Character, status, Protocol.Int(score), member.IsBot ? "1" : "0", grace));
        }
        string line = Protocol.Encode(fields.ToArray());
        foreach (Client member in room.Members)
        {
            member.Send(line);
        }
        UpdateSpectating(room);
        BroadcastRoomList();
    }

    private bool ProofMatches(string nonce, string proof)
    {
        byte[] expected = Encoding.ASCII.GetBytes(Protocol.AuthProof(EffectiveKey!, nonce));
        byte[] actual = Encoding.ASCII.GetBytes((proof ?? string.Empty).Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private void Reject(Client client, string message)
    {
        client.Send(Protocol.Encode(Protocol.Error, message));
        client.Closing = true;
        client.Outbox.Writer.TryComplete();
    }

    private void Disconnect(Client client, string reason)
    {
        if (!clients.Remove(client))
        {
            return;
        }
        Log($"#{client.Id} disconnected ({reason})");
        LeaveRoom(client, reason, notify: false);
    }

    internal void Log(string message) => Log(message, null);

    /// <summary>Writes to stdout and to the admin sessions, except the one named.</summary>
    private void Log(string message, AdminSession? except)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        ForwardLog(line, except);
    }
}

internal sealed class Client
{
    /// <summary>A server-side bot: lives only in a room, has no connection.</summary>
    public static Client CreateBot(int id, string initials, string character, int level)
    {
        return new Client(id, initials, character, level);
    }

    private Client(int id, string initials, string character, int level)
    {
        Id = id;
        IsBot = true;
        BotLevel = level;
        Initials = initials;
        Character = character;
        Tcp = null!;
        Stream = null!;
        Remote = "bot";
        Address = IPAddress.None;
        LastSeen = ConnectedAt = DateTime.UtcNow;
        Nonce = string.Empty;
        Outbox.Writer.TryComplete();
    }

    public bool IsBot { get; }

    // watching (scene mirror)
    public int WatchMatch { get; set; }
    public int WatchTarget { get; set; }
    public bool Watching { get; set; }
    public int StreamingMatch { get; set; }

    /// <summary>Opt-out: false = this player is never streamed to others.</summary>
    public bool AllowWatchers { get; set; } = true;

    public int BotLevel { get; set; }

    public Client(int id, TcpClient tcp)
    {
        Id = id;
        Tcp = tcp;
        Tcp.NoDelay = true;
        Stream = tcp.GetStream();
        Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        Address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (Address.IsIPv4MappedToIPv6)
        {
            Address = Address.MapToIPv4();
        }
        LastSeen = DateTime.UtcNow;
        ConnectedAt = LastSeen;
        Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public IPAddress Address { get; }
    public DateTime ConnectedAt { get; }

    /// <summary>Random per-connection challenge for the password proof.</summary>
    public string Nonce { get; }

    public int Id { get; }
    public TcpClient Tcp { get; }
    public NetworkStream Stream { get; }
    public string Remote { get; }
    public Channel<string> Outbox { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
    public DateTime LastSeen { get; set; }
    public bool Closing { get; set; }

    /// <summary>Passed HELLO (password, consent); may be in the room browser or a room.</summary>
    public bool Authed { get; set; }

    /// <summary>Asked for a legal text before HELLO; gets more time for the handshake.</summary>
    public bool ReadingDocuments { get; set; }

    public DateTime RateWindowStart { get; set; }
    public int RateCount { get; set; }
    public string Initials { get; set; } = "???";
    public string Character { get; set; } = "tim";
    public Room? Room { get; set; }

    public void Send(string line)
    {
        if (IsBot)
        {
            return;
        }
        Outbox.Writer.TryWrite(line);
    }

    public void Close()
    {
        Closing = true;
        Outbox.Writer.TryComplete();
        try
        {
            Tcp.Client.Shutdown(SocketShutdown.Both);
        }
        catch
        {
        }
    }

    public async Task WriteLoopAsync(CancellationToken token)
    {
        var encoding = new UTF8Encoding(false);
        try
        {
            await foreach (string line in Outbox.Reader.ReadAllAsync(token))
            {
                byte[] bytes = encoding.GetBytes(line + "\n");
                await Stream.WriteAsync(bytes, token);
            }
            await Stream.FlushAsync(token);
        }
        catch
        {
        }
        if (Closing)
        {
            try
            {
                Tcp.Client.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }
        }
    }
}

internal sealed class Room
{
    public Room(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<Client> Members { get; } = new();
    public Client? Host => Members.FirstOrDefault(m => !m.IsBot);
    public int BotLevel { get; set; } = Bots.DefaultLevel;
    public Match? Match { get; set; }

    /// <summary>Room key (hex SHA-256 of the room password), null = open room.</summary>
    public string? Key { get; set; }

    public bool Locked => !string.IsNullOrEmpty(Key);
}

internal sealed class Match
{
    public Match(int id, int discipline, DateTime beganAt)
    {
        Id = id;
        Discipline = discipline;
        BeganAt = beganAt;
    }

    public int Id { get; }
    public int Discipline { get; }
    public DateTime BeganAt { get; }
    public bool GoSent { get; set; }
    public List<Participant> Participants { get; } = new();

    /// <summary>The player the others watch (scene mirror), null = nobody.</summary>
    public Participant? StreamTarget { get; set; }
    public DateTime TargetSince { get; set; }

    public Participant? Find(Client client) => Participants.FirstOrDefault(p => p.Client == client);
}

internal sealed class Participant
{
    public Participant(Client client)
    {
        Client = client;
        Initials = client.Initials;
        Character = client.Character;
    }

    public Client Client { get; }
    public string Initials { get; }
    public string Character { get; }
    public string Status { get; set; } = Protocol.StatusLoading;

    // bots only
    public int BotTarget { get; set; }
    public TimeSpan BotDuration { get; set; }
    public DateTime BotStartedAt { get; set; }
    public int Score { get; set; }

    /// <summary>Pause time used up while nobody else was playing.</summary>
    public TimeSpan PauseUsed { get; set; }
    public DateTime PauseAccruedAt { get; set; }
    public bool Finished => Status == Protocol.StatusDone || Status == Protocol.StatusLeft;
}
