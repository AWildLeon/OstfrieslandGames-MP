using System.Net;
using System.Net.Sockets;
using System.Text;
using OGMultiplayer;
using OGMultiplayer.Server;
using Xunit;

namespace OGMultiplayer.Server.Tests;

public sealed class TestClient : IDisposable
{
    private readonly TcpClient tcp;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;

    public TestClient(int port)
    {
        tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        reader = new StreamReader(stream, new UTF8Encoding(false));
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        Challenge = Next(Protocol.Challenge);
    }

    public string[] Challenge { get; }

    public int Id { get; private set; }

    public void Send(params string[] fields) => writer.WriteLine(Protocol.Encode(fields));

    public string Nonce => Challenge[2];

    public string[] Hello(string initials, string room = "raum", string? password = null, string consent = "")
    {
        string proof = password == null ? string.Empty : Protocol.AuthProof(Protocol.ServerKey(password), Challenge[2]);
        Send(Protocol.Hello, Protocol.Int(Protocol.Version), initials, "tim", room, proof, consent);
        string[] reply = Next(Protocol.Welcome, Protocol.Error);
        if (reply[0] == Protocol.Welcome)
        {
            Id = int.Parse(reply[1]);
        }
        return reply;
    }

    /// <summary>Reads until a line with one of the given commands arrives (others are skipped).</summary>
    public string[] Next(params string[] commands)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            string? line = reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
            if (line == null)
            {
                return new[] { "EOF" };
            }
            string[] f = Protocol.Decode(line);
            if (commands.Contains(f[0]))
            {
                return f;
            }
        }
    }

    public bool IsClosed()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                if (reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult() == null)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => tcp.Dispose();
}

public sealed class ServerFixture : IAsyncDisposable
{
    public ServerFixture(string? password = null, string? stateFile = null, Action<ServerOptions>? configure = null)
    {
        var options = new ServerOptions
        {
            Bind = IPAddress.Loopback,
            Port = 0,
            Password = password,
            Console = false,
            StateFile = stateFile,
            ReadyTimeout = TimeSpan.FromSeconds(30),
        };
        configure?.Invoke(options);
        Server = new GameServer(options);
        Run = Server.RunAsync(CancellationToken.None);
        Task.WhenAny(Server.Started, Run).Wait(TimeSpan.FromSeconds(5));
    }

    public GameServer Server { get; }

    public Task Run { get; }

    public TestClient Connect() => new(Server.BoundPort);

    public async ValueTask DisposeAsync()
    {
        await Server.RequestShutdown(null, TimeSpan.Zero);
        await Run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class ProtocolTests
{
    [Fact]
    public void AuthProofIsHmacSha256Hex()
    {
        Assert.Equal(
            "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            Protocol.AuthProof("key", "The quick brown fox jumps over the lazy dog"));
    }

    [Theory]
    [InlineData("abc", "ABC")]
    [InlineData(" xyz ", "XYZ")]
    [InlineData("ab", null)]
    [InlineData("a1c", null)]
    [InlineData("ÄBC", null)]
    public void NormalizeInitials(string input, string? expected) => Assert.Equal(expected, Protocol.NormalizeInitials(input));

    [Fact]
    public void EncodeStripsSeparators()
    {
        Assert.Equal(new[] { "MSG", "ab c" }, Protocol.Decode(Protocol.Encode("MSG", "a\tb\n c")));
    }
}

public class ServerTests
{
    [Fact]
    public async Task OpenServerAcceptsPlayers()
    {
        await using var fx = new ServerFixture();
        using var a = fx.Connect();
        Assert.Equal("0", a.Challenge[3]);
        Assert.Equal(Protocol.Welcome, a.Hello("AAA")[0]);
        string[] lobby = a.Next(Protocol.Lobby);
        Assert.Equal("raum", lobby[1]);
        Assert.Equal(a.Id.ToString(), lobby[2]);
    }

    [Fact]
    public async Task IPv6AnyAcceptsIPv4()
    {
        await using var fx = new ServerFixture(configure: o => o.Bind = IPAddress.IPv6Any);
        using var a = fx.Connect(); // connects to 127.0.0.1
        Assert.Equal(Protocol.Welcome, a.Hello("AAA")[0]);
    }

    [Fact]
    public async Task PasswordIsChecked()
    {
        await using var fx = new ServerFixture("geheim");
        using var ok = fx.Connect();
        Assert.Equal("1", ok.Challenge[3]);
        Assert.Equal(Protocol.Welcome, ok.Hello("AAA", password: "geheim")[0]);

        using var wrong = fx.Connect();
        Assert.Equal(Protocol.Error, wrong.Hello("BBB", password: "falsch")[0]);
        Assert.True(wrong.IsClosed());

        using var missing = fx.Connect();
        Assert.Equal(Protocol.Error, missing.Hello("CCC")[0]);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsLockTheAddress()
    {
        await using var fx = new ServerFixture("geheim");
        for (int i = 0; i < 5; i++)
        {
            using var c = fx.Connect();
            c.Hello("AAA", password: "falsch");
        }
        using var right = fx.Connect();
        string[] reply = right.Hello("AAA", password: "geheim");
        Assert.Equal(Protocol.Error, reply[0]);
        Assert.Contains("Zu viele", reply[1]);

        fx.Server.ExecuteAdmin("unban 127.0.0.1");
        using var again = fx.Connect();
        Assert.Equal(Protocol.Welcome, again.Hello("AAA", password: "geheim")[0]);
    }

    [Fact]
    public async Task FullMatchFlow()
    {
        await using var fx = new ServerFixture();
        using var host = fx.Connect();
        using var guest = fx.Connect();
        host.Hello("HST");
        guest.Hello("GST");

        guest.Send(Protocol.Start, "0");
        Assert.Equal(Protocol.Error, guest.Next(Protocol.Error)[0]);

        host.Send(Protocol.Start, "4");
        string[] begin = host.Next(Protocol.Begin);
        guest.Next(Protocol.Begin);
        Assert.Equal("4", begin[2]);
        Assert.Equal(4, begin.Length - 1);
        string matchId = begin[1];

        host.Send(Protocol.Ready, matchId);
        guest.Send(Protocol.Ready, matchId);
        Assert.Equal(matchId, host.Next(Protocol.Go)[1]);
        Assert.Equal(matchId, guest.Next(Protocol.Go)[1]);

        host.Send(Protocol.Score, matchId, "1200");
        guest.Send(Protocol.Score, matchId, "-5");
        string[] result = host.Next(Protocol.Result);
        Assert.Contains($"{host.Id}|1200", result);
        Assert.Contains($"{guest.Id}|0", result);
        guest.Next(Protocol.Result);
    }

    [Fact]
    public async Task LeavingPlayerScoresZero()
    {
        await using var fx = new ServerFixture();
        using var host = fx.Connect();
        using var guest = fx.Connect();
        host.Hello("HST");
        guest.Hello("GST");
        host.Send(Protocol.Start, "0");
        string matchId = host.Next(Protocol.Begin)[1];
        host.Send(Protocol.Ready, matchId);
        guest.Send(Protocol.Leave, matchId);
        host.Next(Protocol.Go);
        host.Send(Protocol.Score, matchId, "300");
        string[] result = host.Next(Protocol.Result);
        Assert.Contains($"{guest.Id}|0", result);
    }

    [Fact]
    public async Task RoomIsLimitedToFourPlayers()
    {
        await using var fx = new ServerFixture();
        var clients = Enumerable.Range(0, 4).Select(_ => fx.Connect()).ToList();
        foreach (var (c, i) in clients.Select((c, i) => (c, i)))
        {
            Assert.Equal(Protocol.Welcome, c.Hello("AA" + (char)('A' + i))[0]);
        }
        using var fifth = fx.Connect();
        Assert.Equal(Protocol.Welcome, fifth.Hello("EEE")[0]);
        Assert.Contains("voll", fifth.Next(Protocol.Error)[1]);
        fifth.Next(Protocol.RoomList); // stays in the room browser
        clients.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task AdminKickBanAndSay()
    {
        await using var fx = new ServerFixture();
        using var a = fx.Connect();
        using var b = fx.Connect();
        a.Hello("AAA");
        b.Hello("BBB");

        Assert.Contains("An 2 Spieler", fx.Server.ExecuteAdmin("say Hallo"));
        Assert.Equal("Hallo", a.Next(Protocol.Msg)[1]);

        Assert.Contains("getrennt", fx.Server.ExecuteAdmin($"kick {b.Id} spam"));
        Assert.Contains("spam", b.Next(Protocol.Bye)[1]);

        Assert.Contains("127.0.0.1 gesperrt", fx.Server.ExecuteAdmin($"ban {a.Id}"));
        Assert.Equal(Protocol.Bye, a.Next(Protocol.Bye)[0]);
        Assert.Contains("127.0.0.1", fx.Server.ExecuteAdmin("bans"));
        Assert.Contains("entsperrt", fx.Server.ExecuteAdmin("unban 127.0.0.1"));

        Assert.Contains("Unbekannter Befehl", fx.Server.ExecuteAdmin("quatsch"));
        Assert.Contains("shutdown", fx.Server.ExecuteAdmin("help"));
    }

    [Fact]
    public async Task AdminEndMatchAndPassword()
    {
        await using var fx = new ServerFixture();
        using var host = fx.Connect();
        using var guest = fx.Connect();
        host.Hello("HST");
        guest.Hello("GST");
        host.Send(Protocol.Start, "1");
        host.Next(Protocol.Begin);
        Assert.Contains("beendet", fx.Server.ExecuteAdmin("endmatch raum"));
        Assert.Equal(Protocol.Result, host.Next(Protocol.Result)[0]);

        fx.Server.ExecuteAdmin("password neu");
        using var c = fx.Connect();
        Assert.Equal("1", c.Challenge[3]);
        Assert.Equal(Protocol.Welcome, c.Hello("CCC", "anderer", "neu")[0]);
    }

    [Fact]
    public async Task ShutdownSaysGoodbye()
    {
        var fx = new ServerFixture();
        using var a = fx.Connect();
        a.Hello("AAA");
        fx.Server.ExecuteAdmin("shutdown 1 Wartung");
        Assert.Contains("Wartung", a.Next(Protocol.Msg)[1]);
        Assert.Equal("Wartung", a.Next(Protocol.Bye)[1]);
        await fx.Run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class StateTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ogmp-state-").FullName;

    private string StateFile => Path.Combine(dir, "state.json");

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public async Task BansSurviveRestart()
    {
        await using (var fx = new ServerFixture(stateFile: StateFile))
        {
            using var a = fx.Connect();
            a.Hello("AAA");
            Assert.Contains("gesperrt", fx.Server.ExecuteAdmin($"ban {a.Id} cheater"));
            fx.Server.ExecuteAdmin("ban 203.0.113.7");
        }
        Assert.True(File.Exists(StateFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StateFile));
        }

        await using (var fx = new ServerFixture(stateFile: StateFile))
        {
            string bans = fx.Server.ExecuteAdmin("bans");
            Assert.Contains("127.0.0.1", bans);
            Assert.Contains("AAA", bans);
            Assert.Contains("cheater", bans);
            Assert.Contains("203.0.113.7", bans);
            Assert.True(IsRefused(fx), "gesperrte IP darf nicht verbinden");
            fx.Server.ExecuteAdmin("unban 127.0.0.1");
        }

        await using (var fx = new ServerFixture(stateFile: StateFile))
        {
            using var b = fx.Connect();
            Assert.Equal(Protocol.Welcome, b.Hello("BBB")[0]);
            Assert.DoesNotContain("127.0.0.1", fx.Server.ExecuteAdmin("bans"));
        }
    }

    private static bool IsRefused(ServerFixture fx)
    {
        try
        {
            using var c = fx.Connect();
            return c.Challenge[0] == "EOF";
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Fact]
    public async Task PasswordChangesSurviveRestart()
    {
        await using (var fx = new ServerFixture(password: "konfig", stateFile: StateFile))
        {
            fx.Server.ExecuteAdmin("password admin");
        }
        await using (var fx = new ServerFixture(password: "konfig", stateFile: StateFile))
        {
            using var c = fx.Connect();
            Assert.Equal(Protocol.Welcome, c.Hello("CCC", password: "admin")[0]);
            fx.Server.ExecuteAdmin("password off");
        }
        await using (var fx = new ServerFixture(password: "konfig", stateFile: StateFile))
        {
            using var c = fx.Connect();
            Assert.Equal("0", c.Challenge[3]);
            fx.Server.ExecuteAdmin("password reset");
        }
        await using (var fx = new ServerFixture(password: "konfig", stateFile: StateFile))
        {
            using var c = fx.Connect();
            Assert.Equal(Protocol.Welcome, c.Hello("CCC", password: "konfig")[0]);
        }
    }

    [Fact]
    public async Task BrokenStateFileStopsStartup()
    {
        File.WriteAllText(StateFile, "{ kaputt");
        var fx = new ServerFixture(stateFile: StateFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => fx.Run);
    }
}

public class BotTests
{
    [Fact]
    public async Task HostCanPlayAgainstBots()
    {
        await using var fx = new ServerFixture();
        using var host = fx.Connect();
        host.Hello("HST");
        host.Send(Protocol.Bot, Protocol.BotLevel, "3");
        host.Send(Protocol.Bot, Protocol.BotAdd);
        host.Send(Protocol.Bot, Protocol.BotAdd);
        string[] lobby;
        do
        {
            lobby = host.Next(Protocol.Lobby);
        }
        while (lobby.Length < 6 + 3);
        Assert.Equal("3", lobby[4]);
        Assert.Equal(2, lobby.Skip(6).Count(p => p.EndsWith("|1")));

        host.Send(Protocol.Start, "0");
        string[] begin = host.Next(Protocol.Begin);
        Assert.Equal(3, begin.Length - 3);
        string matchId = begin[1];
        host.Send(Protocol.Ready, matchId);
        host.Next(Protocol.Go);
        host.Send(Protocol.Score, matchId, "900");
        string[] result = host.Next(Protocol.Result);
        Assert.Equal(3, result.Length - 2);
        foreach (string part in result.Skip(2))
        {
            Assert.True(int.Parse(part.Split('|')[1]) > 0, part);
        }
    }

    [Fact]
    public async Task OnlyHostManagesBotsAndRoomLimitCounts()
    {
        await using var fx = new ServerFixture();
        using var host = fx.Connect();
        using var guest = fx.Connect();
        host.Hello("HST");
        guest.Hello("GST");
        guest.Send(Protocol.Bot, Protocol.BotAdd);
        Assert.Equal(Protocol.Error, guest.Next(Protocol.Error)[0]);
        host.Send(Protocol.Bot, Protocol.BotAdd);
        host.Send(Protocol.Bot, Protocol.BotAdd);
        host.Send(Protocol.Bot, Protocol.BotAdd);
        Assert.Contains("voll", host.Next(Protocol.Error)[1]);
        using var late = fx.Connect();
        late.Hello("LAT");
        Assert.Contains("voll", late.Next(Protocol.Error)[1]);
        host.Send(Protocol.Bot, Protocol.BotRemove);
        Thread.Sleep(200);
        late.Send(Protocol.JoinRoom, "raum", string.Empty);
        Assert.Equal("raum", late.Next(Protocol.Joined)[1]);
    }

    [Fact]
    public async Task RoomWithOnlyBotsCloses()
    {
        await using var fx = new ServerFixture();
        using (var host = fx.Connect())
        {
            host.Hello("HST");
            host.Send(Protocol.Bot, Protocol.BotAdd);
            host.Next(Protocol.Lobby);
        }
        Thread.Sleep(500);
        Assert.Equal("Keine Raeume.", fx.Server.ExecuteAdmin("rooms"));
    }

    [Fact]
    public void BotScoresFollowLevelAndReference()
    {
        var rng = new Random(1);
        double Avg(int level) => Enumerable.Range(0, 200).Average(_ => Bots.TargetScore(level, 1000, rng));
        Assert.True(Avg(1) < Avg(2));
        Assert.True(Avg(2) < Avg(3));
        Assert.Equal(Bots.DefaultReference, Bots.Reference(new List<int>()));
        Assert.Equal(500, Bots.Reference(new List<int> { 100, 500, 900 }));
    }
}

public class RoomTests
{
    [Fact]
    public async Task BrowserCreateJoinWithPasswordKickAndLeave()
    {
        await using var fx = new ServerFixture();
        using var host = fx.Connect();
        using var guest = fx.Connect();
        host.Hello("HST", room: "");
        Assert.Equal(Protocol.RoomList, host.Next(Protocol.RoomList)[0]);
        guest.Hello("GST", room: "");
        guest.Next(Protocol.RoomList);

        host.Send(Protocol.Create, "geheim", Protocol.RoomKey("pw123", "geheim"));
        Assert.Equal("geheim", host.Next(Protocol.Joined)[1]);
        string[] list = guest.Next(Protocol.RoomList);
        Assert.Contains("geheim|1|4|lobby|1|HST", list);

        guest.Send(Protocol.JoinRoom, "geheim", Protocol.AuthProof(Protocol.RoomKey("falsch", "geheim"), guest.Nonce));
        Assert.Contains("Raum-Passwort", guest.Next(Protocol.Error)[1]);
        guest.Send(Protocol.JoinRoom, "geheim", Protocol.AuthProof(Protocol.RoomKey("pw123", "geheim"), guest.Nonce));
        Assert.Equal("geheim", guest.Next(Protocol.Joined)[1]);
        string[] lobby = host.Next(Protocol.Lobby);
        Assert.Equal("1", lobby[5]);

        host.Send(Protocol.KickPlayer, Protocol.Int(guest.Id));
        Assert.Contains("entfernt", guest.Next(Protocol.RoomLeft)[1]);
        guest.Next(Protocol.RoomList);

        host.Send(Protocol.RoomPassword, string.Empty);
        guest.Send(Protocol.JoinRoom, "geheim", string.Empty);
        Assert.Equal("geheim", guest.Next(Protocol.Joined)[1]);

        guest.Send(Protocol.LeaveRoom);
        guest.Next(Protocol.RoomLeft);
        host.Send(Protocol.LeaveRoom);
        host.Next(Protocol.RoomLeft);
        Thread.Sleep(200);
        Assert.Equal("Keine Raeume.", fx.Server.ExecuteAdmin("rooms"));
    }

    [Fact]
    public async Task RoomLimitAndDuplicateNames()
    {
        await using var fx = new ServerFixture(configure: o => o.MaxRooms = 1);
        using var a = fx.Connect();
        using var b = fx.Connect();
        a.Hello("AAA", room: "");
        b.Hello("BBB", room: "");
        a.Send(Protocol.Create, "eins", string.Empty);
        a.Next(Protocol.Joined);
        b.Send(Protocol.Create, "eins", string.Empty);
        Assert.Contains("schon", b.Next(Protocol.Error)[1]);
        b.Send(Protocol.Create, "zwei", string.Empty);
        Assert.Contains("1 Räume", b.Next(Protocol.Error)[1]);
    }

    [Fact]
    public async Task ConnectionsPerIpAreLimited()
    {
        await using var fx = new ServerFixture(configure: o => o.MaxConnectionsPerIp = 2);
        using var a = fx.Connect();
        using var b = fx.Connect();
        bool refused;
        try
        {
            using var c = fx.Connect();
            refused = c.Challenge[0] == "EOF";
        }
        catch (IOException)
        {
            refused = true;
        }
        Assert.True(refused);
    }
}

public class PublicServerTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ogmp-legal-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task PublicServerNeedsLegalTexts()
    {
        var fx = new ServerFixture(configure: o => o.Public = true);
        await Assert.ThrowsAsync<InvalidDataException>(() => fx.Run);
    }

    [Fact]
    public async Task ConsentIsRequiredAndDocumentsAreServed()
    {
        string privacy = "Datenschutz\nWir speichern nichts.\tWirklich.";
        await using var fx = new ServerFixture(configure: o =>
        {
            o.Public = true;
            o.ServerName = "Test-Server";
            o.Motd = "Moin!";
            o.ImprintFile = Write("impressum.txt", "Impressum\nMax Mustermann");
            o.PrivacyFile = Write("datenschutz.txt", privacy);
        });
        using var c = fx.Connect();
        Assert.Equal("1", c.Challenge[4]);
        string hash = c.Challenge[5];
        Assert.Equal(Protocol.TextHash(privacy), hash);
        Assert.Equal("Test-Server", c.Challenge[6]);

        c.Send(Protocol.Doc, Protocol.DocPrivacy);
        string[] doc = c.Next(Protocol.DocText);
        Assert.Equal(privacy, Protocol.UnescapeText(doc[2]));

        Assert.Equal(Protocol.Error, c.Hello("AAA", room: "")[0]);

        using var ok = fx.Connect();
        Assert.Equal(Protocol.Welcome, ok.Hello("BBB", room: "", consent: hash)[0]);
        string[] info = ok.Next(Protocol.ServerInfo);
        Assert.Equal(new[] { "Test-Server", "1", "Moin!", "imprint,privacy" }, info.Skip(1).ToArray());
    }

    [Fact]
    public void TextEscapingRoundTrips()
    {
        string text = "a\\b\tc\nd\\ne";
        Assert.Equal(text, Protocol.UnescapeText(Protocol.EscapeText(text)));
        Assert.DoesNotContain("\n", Protocol.EscapeText(text));
        Assert.DoesNotContain("\t", Protocol.EscapeText(text));
    }
}

public class PasswordStorageTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("ogmp-pw-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public async Task PasswordsAreNeverStoredInClearText()
    {
        string stateFile = Path.Combine(dir, "state.json");
        await using (var fx = new ServerFixture(stateFile: stateFile))
        {
            fx.Server.ExecuteAdmin("password SuperGeheim42");
            using var host = fx.Connect();
            host.Hello("HST", room: "", password: "SuperGeheim42");
            host.Send(Protocol.Create, "raum2", Protocol.RoomKey("RaumPW99", "raum2"));
            host.Next(Protocol.Joined);
        }
        string json = File.ReadAllText(stateFile);
        Assert.DoesNotContain("SuperGeheim42", json);
        Assert.Contains(Protocol.ServerKey("SuperGeheim42"), json);
    }

    [Fact]
    public async Task OldClearTextStateIsMigrated()
    {
        string stateFile = Path.Combine(dir, "old.json");
        File.WriteAllText(stateFile, "{\"version\":1,\"bans\":[],\"passwordOverride\":\"altesPw\"}");
        await using (var fx = new ServerFixture(stateFile: stateFile))
        {
            using var c = fx.Connect();
            Assert.Equal(Protocol.Welcome, c.Hello("AAA", password: "altesPw")[0]);
        }
        Assert.DoesNotContain("altesPw", File.ReadAllText(stateFile));
    }

    [Fact]
    public void Pbkdf2MatchesReferenceVectorAndFramework()
    {
        // RFC 7914 section 11 test vector for PBKDF2-HMAC-SHA256 (P="passwd", S="salt", c=1)
        byte[] p = System.Text.Encoding.ASCII.GetBytes("passwd");
        byte[] salt = System.Text.Encoding.ASCII.GetBytes("salt");
        Assert.Equal("55ac046e56e3089fec1691c22544b605", Convert.ToHexString(Protocol.Pbkdf2Sha256(p, salt, 1))[..32].ToLowerInvariant());
        byte[] framework = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(p, salt, 1000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        Assert.Equal(framework, Protocol.Pbkdf2Sha256(p, salt, 1000));
    }

    [Fact]
    public void RoomKeysDependOnRoomName()
    {
        Assert.NotEqual(Protocol.RoomKey("pw", "a"), Protocol.RoomKey("pw", "b"));
        Assert.Equal(64, Protocol.RoomKey("pw", "a").Length);
        Assert.Equal(string.Empty, Protocol.RoomKey("", "a"));
    }
}
