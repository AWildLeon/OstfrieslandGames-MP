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
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxConnections { get; set; } = 256;
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Server password; null or empty = open server.</summary>
    public string? Password { get; set; }

    public int MaxAuthFailures { get; set; } = 5;
    public TimeSpan AuthFailureWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Read admin commands from stdin.</summary>
    public bool Console { get; set; } = true;
}

public sealed partial class GameServer
{
    private readonly ServerOptions options;
    private readonly object sync = new();
    private readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    private readonly List<Client> clients = new();
    private readonly Dictionary<IPAddress, List<DateTime>> authFailures = new();
    private readonly HashSet<IPAddress> bans = new();
    private readonly DateTime startedAt = DateTime.UtcNow;
    private CancellationTokenSource shutdown = new();
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
        var listener = new TcpListener(options.Bind, options.Port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
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
                if (ip != null && bans.Contains(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))
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

    private bool PasswordRequired => !string.IsNullOrEmpty(options.Password);

    private async Task ServeAsync(Client client, CancellationToken token)
    {
        Log($"#{client.Id} connected from {client.Remote}");
        client.Send(Protocol.Encode(Protocol.Challenge, Protocol.Int(Protocol.Version), client.Nonce, PasswordRequired ? "1" : "0"));
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
                    client.LastSeen = DateTime.UtcNow;
                    Handle(client, Protocol.Decode(line));
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
        foreach (Client c in clients.ToList())
        {
            if (now - c.LastSeen > options.IdleTimeout)
            {
                Disconnect(c, "timeout");
                c.Close();
            }
            else if (c.Room == null && !c.Closing && now - c.ConnectedAt > options.HandshakeTimeout)
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
            if (now - m.BeganAt > options.MatchTimeout)
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
        if (cmd == Protocol.Hello)
        {
            HandleHello(client, f);
            return;
        }
        Room? room = client.Room;
        if (room == null)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Nicht angemeldet."));
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
            case Protocol.Live:
                HandleLive(client, room, f);
                break;
            case Protocol.Score:
                HandleScore(client, room, f);
                break;
            case Protocol.Leave:
                HandleLeave(client, room, f);
                break;
        }
    }

    private void HandleHello(Client client, string[] f)
    {
        if (client.Room != null)
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
            DateTime now = DateTime.UtcNow;
            if (authFailures.TryGetValue(client.Address, out List<DateTime>? failures)
                && failures.Count(t => now - t <= options.AuthFailureWindow) >= options.MaxAuthFailures)
            {
                Reject(client, "Zu viele falsche Passwoerter. Bitte spaeter erneut versuchen.");
                return;
            }
            if (!ProofMatches(client.Nonce, f[5]))
            {
                if (failures == null)
                {
                    failures = new List<DateTime>();
                    authFailures[client.Address] = failures;
                }
                failures.Add(now);
                Log($"#{client.Id} wrong password from {client.Address}");
                Reject(client, "Falsches Server-Passwort.");
                return;
            }
        }
        string? initials = Protocol.NormalizeInitials(f[2]);
        string? character = Protocol.NormalizeId(f[3]);
        string? roomName = Protocol.NormalizeId(f[4]);
        if (initials == null || character == null || roomName == null)
        {
            Reject(client, "Ungueltige Initialen, Figur oder Raumname.");
            return;
        }
        if (!rooms.TryGetValue(roomName, out Room? room))
        {
            room = new Room(roomName);
            rooms[roomName] = room;
        }
        if (room.Members.Count >= Protocol.MaxPlayers)
        {
            if (room.Members.Count == 0)
            {
                rooms.Remove(roomName);
            }
            Reject(client, $"Raum '{roomName}' ist voll ({Protocol.MaxPlayers} Spieler).");
            return;
        }
        client.Initials = initials;
        client.Character = character;
        client.Room = room;
        room.Members.Add(client);
        Log($"#{client.Id} joined room '{roomName}' as {initials} ({character})");
        client.Send(Protocol.Encode(Protocol.Welcome, Protocol.Int(client.Id)));
        BroadcastLobby(room);
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
            client.Send(Protocol.Encode(Protocol.Error, "Es laeuft bereits eine Partie."));
            return;
        }
        if (f.Length < 2 || !Protocol.TryInt(f[1], out int discipline) || discipline < 0 || discipline >= Protocol.DisciplineCount)
        {
            client.Send(Protocol.Encode(Protocol.Error, "Unbekannte Disziplin."));
            return;
        }
        if (room.Members.Count < Protocol.MinPlayers)
        {
            client.Send(Protocol.Encode(Protocol.Error, $"Mindestens {Protocol.MinPlayers} Spieler noetig."));
            return;
        }
        var match = new Match(++nextMatchId, discipline, DateTime.UtcNow);
        foreach (Client member in room.Members)
        {
            match.Participants.Add(new Participant(member));
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
            p.Client.Send(go);
        }
        Log($"[{room.Name}] match {m.Id}: GO");
        BroadcastLobby(room);
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

    private void BroadcastLobby(Room room)
    {
        Client? host = room.Host;
        var fields = new List<string>
        {
            Protocol.Lobby,
            room.Name,
            Protocol.Int(host?.Id ?? 0),
            room.Match != null ? Protocol.StateMatch : Protocol.StateLobby,
        };
        foreach (Client member in room.Members)
        {
            Participant? p = room.Match?.Find(member);
            string status = p?.Status ?? Protocol.StatusLobby;
            int score = p?.Score ?? 0;
            fields.Add(Protocol.Join(Protocol.Int(member.Id), member.Initials, member.Character, status, Protocol.Int(score)));
        }
        string line = Protocol.Encode(fields.ToArray());
        foreach (Client member in room.Members)
        {
            member.Send(line);
        }
    }

    private bool ProofMatches(string nonce, string proof)
    {
        byte[] expected = Encoding.ASCII.GetBytes(Protocol.AuthProof(options.Password!, nonce));
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
        Room? room = client.Room;
        client.Room = null;
        if (room == null)
        {
            return;
        }
        room.Members.Remove(client);
        Match? m = room.Match;
        Participant? p = m?.Find(client);
        if (m != null && p != null && !p.Finished)
        {
            p.Status = Protocol.StatusLeft;
            p.Score = 0;
        }
        if (room.Members.Count == 0)
        {
            rooms.Remove(room.Name);
            Log($"[{room.Name}] room closed");
            return;
        }
        if (m != null)
        {
            AfterParticipantGone(room, m);
        }
        BroadcastLobby(room);
    }

    internal static void Log(string message)
    {
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
    }
}

internal sealed class Client
{
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
    public string Initials { get; set; } = "???";
    public string Character { get; set; } = "tim";
    public Room? Room { get; set; }

    public void Send(string line)
    {
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
    public Client? Host => Members.Count > 0 ? Members[0] : null;
    public Match? Match { get; set; }
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
    public int Score { get; set; }
    public bool Finished => Status == Protocol.StatusDone || Status == Protocol.StatusLeft;
}
