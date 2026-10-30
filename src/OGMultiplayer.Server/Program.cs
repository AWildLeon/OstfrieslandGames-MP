using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OGMultiplayer;
using OGMultiplayer.Server;

var options = new ServerOptions();

string? envPort = Environment.GetEnvironmentVariable("OGMP_PORT");
string? envBind = Environment.GetEnvironmentVariable("OGMP_BIND");
if (envPort != null && Protocol.TryInt(envPort, out int p))
{
    options.Port = p;
}
if (envBind != null && IPAddress.TryParse(envBind, out IPAddress? b))
{
    options.Bind = b;
}

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
        case "-h":
        case "--help":
            Console.WriteLine("""
                OGMultiplayer.Server - Lobby/Relay-Server fuer den Ostfriesland Games Online-Mehrspieler-Mod

                Optionen:
                  -p, --port <port>          TCP-Port (Standard 27420, Env OGMP_PORT)
                  -b, --bind <ip>            Bind-Adresse (Standard 0.0.0.0, Env OGMP_BIND)
                      --ready-timeout <s>    Max. Wartezeit auf alle Spieler vor dem Start (Standard 60)
                      --match-timeout <min>  Max. Dauer einer Runde (Standard 20)
                """);
            return 0;
        default:
            return Fail($"Unbekannte Option: {arg}");
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

await new GameServer(options).RunAsync(cts.Token);
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
