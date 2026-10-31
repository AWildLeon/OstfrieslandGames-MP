using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OGMultiplayer;
using OGMultiplayer.Server;

var options = new ServerOptions();

string? envPort = Environment.GetEnvironmentVariable("OGMP_PORT");
string? envBind = Environment.GetEnvironmentVariable("OGMP_BIND");
string? envPassword = Environment.GetEnvironmentVariable("OGMP_PASSWORD");
string? envPasswordFile = Environment.GetEnvironmentVariable("OGMP_PASSWORD_FILE");
string? envState = Environment.GetEnvironmentVariable("OGMP_STATE_FILE");
options.StateFile = string.IsNullOrEmpty(envState) ? "ogmultiplayer-state.json" : envState;
if (!string.IsNullOrEmpty(envPassword))
{
    options.Password = envPassword;
}
if (!string.IsNullOrEmpty(envPasswordFile))
{
    options.Password = ReadPasswordFile(envPasswordFile);
    if (options.Password == null)
    {
        return 2;
    }
}
if (envPort != null && Protocol.TryInt(envPort, out int p))
{
    options.Port = p;
}
if (envBind != null && IPAddress.TryParse(envBind, out IPAddress? b))
{
    options.Bind = b;
}

bool maxRoomsSet = false, maxPerIpSet = false, logIpsSet = false;
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    string? next = i + 1 < args.Length ? args[i + 1] : null;
    switch (arg)
    {
        case "-p":
        case "--port":
            if (next == null || !Protocol.TryInt(next, out int port) || port < 1 || port > 65535)
            {
                return Fail("--port braucht eine Zahl zwischen 1 und 65535");
            }
            options.Port = port;
            i++;
            break;
        case "-b":
        case "--bind":
            if (next == null || !IPAddress.TryParse(next, out IPAddress? bind))
            {
                return Fail("--bind braucht eine IP-Adresse");
            }
            options.Bind = bind;
            i++;
            break;
        case "--ready-timeout":
            if (next == null || !Protocol.TryInt(next, out int rt) || rt < 1)
            {
                return Fail("--ready-timeout braucht Sekunden");
            }
            options.ReadyTimeout = TimeSpan.FromSeconds(rt);
            i++;
            break;
        case "--match-timeout":
            if (next == null || !Protocol.TryInt(next, out int mt) || mt < 1)
            {
                return Fail("--match-timeout braucht Minuten");
            }
            options.MatchTimeout = TimeSpan.FromMinutes(mt);
            i++;
            break;
        case "--password-file":
            if (next == null)
            {
                return Fail("--password-file braucht einen Pfad");
            }
            options.Password = ReadPasswordFile(next);
            if (options.Password == null)
            {
                return 2;
            }
            i++;
            break;
        case "--state-file":
            if (next == null)
            {
                return Fail("--state-file braucht einen Pfad");
            }
            options.StateFile = next;
            i++;
            break;
        case "--public":
            options.Public = true;
            break;
        case "--name":
            if (string.IsNullOrWhiteSpace(next))
            {
                return Fail("--name braucht einen Text");
            }
            options.ServerName = Protocol.Clean(next).Replace("|", "/").Trim();
            i++;
            break;
        case "--motd":
            if (next == null)
            {
                return Fail("--motd braucht einen Text");
            }
            options.Motd = next;
            i++;
            break;
        case "--imprint-file":
        case "--privacy-file":
        case "--rules-file":
            if (next == null || !File.Exists(next))
            {
                return Fail($"{arg}: Datei nicht gefunden: {next}");
            }
            if (arg == "--imprint-file")
            {
                options.ImprintFile = next;
            }
            else if (arg == "--privacy-file")
            {
                options.PrivacyFile = next;
            }
            else
            {
                options.RulesFile = next;
            }
            i++;
            break;
        case "--max-rooms":
            if (next == null || !Protocol.TryInt(next, out int mr) || mr < 0)
            {
                return Fail("--max-rooms braucht eine Zahl (0 = unbegrenzt)");
            }
            options.MaxRooms = mr;
            maxRoomsSet = true;
            i++;
            break;
        case "--max-per-ip":
            if (next == null || !Protocol.TryInt(next, out int mpi) || mpi < 0)
            {
                return Fail("--max-per-ip braucht eine Zahl (0 = unbegrenzt)");
            }
            options.MaxConnectionsPerIp = mpi;
            maxPerIpSet = true;
            i++;
            break;
        case "--log-ips":
            logIpsSet = true;
            options.LogIps = true;
            break;
        case "--no-state":
            options.StateFile = null;
            break;
        case "--no-console":
            options.Console = false;
            break;
        case "-h":
        case "--help":
            Console.WriteLine("""
                OGMultiplayer.Server - Lobby/Relay-Server fuer den Ostfriesland Games Online-Mehrspieler-Mod

                Optionen:
                  -p, --port <port>          TCP-Port (Standard 27420, Env OGMP_PORT)
                  -b, --bind <ip>            Bind-Adresse (Standard 0.0.0.0, Env OGMP_BIND)
                      --ready-timeout <s>    Max. Wartezeit auf alle Spieler vor dem Start (Standard 60)
                      --match-timeout <min>  Max. Dauer einer Runde (Standard 20)
                      --password-file <pfad> Server-Passwort aus Datei (erste Zeile; Env OGMP_PASSWORD_FILE)
                                             Alternativ Env OGMP_PASSWORD. Ohne Passwort ist der Server offen.
                      --state-file <pfad>    Bans und Admin-Passwort dauerhaft speichern
                                             (Standard ./ogmultiplayer-state.json, Env OGMP_STATE_FILE)
                      --no-state             nichts speichern
                      --no-console           keine Admin-Befehle von stdin lesen

                Öffentlicher Server:
                      --public               Public-Modus: Impressum + Datenschutz Pflicht, Spieler stimmen
                                             vor dem Beitritt zu; Standards: 50 Räume, 3 Verbindungen pro IP,
                                             IPs im Log gekürzt
                      --name <text>          Servername (Raumliste)
                      --motd <text>          Nachricht in der Raumliste
                      --imprint-file <pfad>  Impressum (Textdatei)
                      --privacy-file <pfad>  Datenschutzhinweise (Textdatei)
                      --rules-file <pfad>    Regeln/Nutzungsbedingungen (optional)
                      --max-rooms <n>        maximale Raumanzahl (0 = unbegrenzt)
                      --max-per-ip <n>       Verbindungen pro IP (0 = unbegrenzt)
                      --log-ips              volle IP-Adressen ins Log schreiben

                Admin-Befehle (stdin, 'help' fuer alle): status, players, rooms, say, kick, ban,
                endmatch, closeroom, password, shutdown [sekunden] [grund] ...
                """);
            return 0;
        default:
            return Fail($"Unbekannte Option: {arg}");
    }
}

Console.WriteLine("OGMultiplayer.Server - inoffizieller Fan-Server, keine Verbindung zu den Entwicklern von Ostfriesland Games.");
if (options.Public)
{
    if (!maxRoomsSet)
    {
        options.MaxRooms = 50;
    }
    if (!maxPerIpSet)
    {
        options.MaxConnectionsPerIp = 3;
    }
    if (!logIpsSet)
    {
        options.LogIps = false;
    }
}

var server = new GameServer(options);
Task run = server.RunAsync(CancellationToken.None);
try
{
    // a broken state file or a busy port ends RunAsync before it listens
    if (await Task.WhenAny(server.Started, run) == run)
    {
        await run;
    }
}
catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
{
    return Fail(ex.Message);
}
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    _ = server.RequestShutdown(null, TimeSpan.Zero);
};
// SIGTERM (systemctl stop): say goodbye to the clients before the process exits
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (!run.IsCompleted)
    {
        _ = server.RequestShutdown(null, TimeSpan.Zero);
        run.Wait(TimeSpan.FromSeconds(3));
    }
};

await run;
return 0;

static string? ReadPasswordFile(string path)
{
    try
    {
        string password = (File.ReadAllLines(path).FirstOrDefault() ?? string.Empty).Trim();
        if (password.Length == 0)
        {
            Console.Error.WriteLine($"Passwortdatei '{path}' ist leer");
            return null;
        }
        return password;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Passwortdatei '{path}' nicht lesbar: {ex.Message}");
        return null;
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
