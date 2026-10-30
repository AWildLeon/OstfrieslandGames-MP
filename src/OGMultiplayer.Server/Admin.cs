using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OGMultiplayer.Server;

/// <summary>Admin commands, read line by line from the server's stdin.</summary>
public sealed partial class GameServer
{
    private string? shutdownReason;
    private CancellationTokenSource? pendingShutdown;

    private static readonly (string Usage, string Help)[] AdminHelp =
    {
        ("help", "diese Hilfe"),
        ("status", "Uptime, Verbindungen, Raeume, Passwort, Bans"),
        ("players", "alle Verbindungen mit ID, IP, Raum und Status"),
        ("rooms", "alle Raeume mit Host und laufender Runde"),
        ("say <text>", "Nachricht an alle Spieler"),
        ("sayroom <raum> <text>", "Nachricht an einen Raum"),
        ("kick <id> [grund]", "Spieler trennen"),
        ("ban <id|ip> [grund]", "IP sperren (bis zum Neustart) und trennen"),
        ("unban <ip>", "Sperre aufheben"),
        ("bans", "gesperrte IPs"),
        ("endmatch <raum>", "laufende Runde beenden (Unfertige = 0 Punkte)"),
        ("closeroom <raum> [grund]", "alle Spieler eines Raums trennen"),
        ("password <neu|off>", "Server-Passwort aendern/abschalten (bis zum Neustart)"),
        ("shutdown [sekunden] [grund]", "Server beenden, optional mit Countdown"),
        ("shutdown cancel", "geplanten Shutdown abbrechen"),
    };

    // ------------------------------------------------------------------ endpoints

    private void StartAdmin(CancellationToken token)
    {
        if (!options.Console)
        {
            return;
        }
        var thread = new Thread(() => ConsoleLoop(token)) { IsBackground = true, Name = "admin-console" };
        thread.Start();
        if (!System.Console.IsInputRedirected)
        {
            Log("Admin-Konsole aktiv, 'help' fuer Befehle");
        }
    }

    private void ConsoleLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = System.Console.ReadLine();
            }
            catch (IOException)
            {
                return;
            }
            if (line == null)
            {
                return;
            }
            if (line.Trim().Length == 0)
            {
                continue;
            }
            System.Console.WriteLine(ExecuteAdmin(line));
        }
    }

    // ------------------------------------------------------------------ commands

    public string ExecuteAdmin(string line)
    {
        string trimmed = line.Trim();
        int space = trimmed.IndexOf(' ');
        string cmd = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        string rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        if (cmd.Length == 0)
        {
            return string.Empty;
        }
        Log($"[admin] {trimmed}");
        try
        {
            lock (sync)
            {
                return cmd switch
                {
                    "help" or "?" => HelpText(),
                    "status" => StatusText(),
                    "players" or "list" => PlayersText(),
                    "rooms" => RoomsText(),
                    "say" => Say(null, rest),
                    "sayroom" => SayRoom(rest),
                    "kick" => KickCommand(rest),
                    "ban" => Ban(rest),
                    "unban" => Unban(rest),
                    "bans" => bans.Count == 0 ? "Keine Sperren." : string.Join("\n", bans.Select(b => b.ToString())),
                    "endmatch" => EndMatch(rest),
                    "closeroom" => CloseRoom(rest),
                    "password" => SetPassword(rest),
                    "shutdown" or "stop" => ScheduleShutdown(rest),
                    _ => $"Unbekannter Befehl '{cmd}'. 'help' zeigt alle Befehle.",
                };
            }
        }
        catch (Exception ex)
        {
            return "Fehler: " + ex.Message;
        }
    }

    private static string HelpText()
    {
        int width = AdminHelp.Max(h => h.Usage.Length);
        return string.Join("\n", AdminHelp.Select(h => $"  {h.Usage.PadRight(width)}  {h.Help}"));
    }

    private string StatusText()
    {
        TimeSpan up = DateTime.UtcNow - startedAt;
        int inRooms = clients.Count(c => c.Room != null);
        int matches = rooms.Values.Count(r => r.Match != null);
        return string.Join("\n",
            $"Uptime:       {(int)up.TotalDays}d {up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}",
            $"Adresse:      {options.Bind}:{BoundPort} (Protokoll v{Protocol.Version})",
            $"Verbindungen: {clients.Count} ({inRooms} in Raeumen)",
            $"Raeume:       {rooms.Count} ({matches} mit laufender Runde)",
            $"Passwort:     {(PasswordRequired ? "an" : "aus")}",
            $"Bans:         {bans.Count}",
            pendingShutdown != null ? "Shutdown:     geplant" : "Shutdown:     -");
    }

    private string PlayersText()
    {
        if (clients.Count == 0)
        {
            return "Keine Verbindungen.";
        }
        var sb = new StringBuilder();
        sb.Append("  ID  INI  FIGUR       RAUM              STATUS    IP");
        foreach (Client c in clients.OrderBy(c => c.Id))
        {
            string status = c.Room == null ? "anmeld." : c.Room.Match?.Find(c)?.Status ?? Protocol.StatusLobby;
            string host = c.Room != null && c.Room.Host == c ? "*" : " ";
            sb.Append($"\n{c.Id,4}  {(c.Room != null ? c.Initials : "---")}{host} {c.Character,-11} {c.Room?.Name ?? "-",-17} {status,-9} {c.Address}");
        }
        sb.Append("\n(* = Host)");
        return sb.ToString();
    }

    private string RoomsText()
    {
        if (rooms.Count == 0)
        {
            return "Keine Raeume.";
        }
        var lines = new List<string>();
        foreach (Room r in rooms.Values.OrderBy(r => r.Name))
        {
            string match = r.Match == null
                ? "Lobby"
                : $"Runde {r.Match.Id}, Disziplin {r.Match.Discipline}, " + string.Join(", ", r.Match.Participants.Select(p => $"{p.Initials}:{p.Status}:{p.Score}"));
            lines.Add($"{r.Name}  [{r.Members.Count}/{Protocol.MaxPlayers}]  Host {r.Host?.Initials ?? "-"}  {match}");
            lines.Add("    " + string.Join(", ", r.Members.Select(m => $"#{m.Id} {m.Initials}")));
        }
        return string.Join("\n", lines);
    }

    private string Say(Room? room, string text)
    {
        if (text.Length == 0)
        {
            return "Kein Text.";
        }
        string line = Protocol.Encode(Protocol.Msg, text);
        IEnumerable<Client> targets = room?.Members ?? clients.Where(c => c.Room != null);
        int n = 0;
        foreach (Client c in targets)
        {
            c.Send(line);
            n++;
        }
        return $"An {n} Spieler gesendet.";
    }

    private string SayRoom(string rest)
    {
        string[] parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return "Aufruf: sayroom <raum> <text>";
        }
        return rooms.TryGetValue(parts[0].ToLowerInvariant(), out Room? room) ? Say(room, parts[1]) : $"Raum '{parts[0]}' gibt es nicht.";
    }

    private Client? FindClient(string idText)
    {
        return Protocol.TryInt(idText.TrimStart('#'), out int id) ? clients.FirstOrDefault(c => c.Id == id) : null;
    }

    private string KickCommand(string rest)
    {
        string[] parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "Aufruf: kick <id> [grund]";
        }
        Client? c = FindClient(parts[0]);
        if (c == null)
        {
            return $"Spieler '{parts[0]}' nicht gefunden ('players' zeigt die IDs).";
        }
        Kick(c, parts.Length > 1 ? "Vom Admin getrennt: " + parts[1] : "Vom Admin getrennt.");
        return $"#{c.Id} {c.Initials} getrennt.";
    }

    private string Ban(string rest)
    {
        string[] parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "Aufruf: ban <id|ip> [grund]";
        }
        IPAddress? ip = null;
        bool looksLikeIp = parts[0].Contains('.') || parts[0].Contains(':');
        if (looksLikeIp)
        {
            IPAddress.TryParse(parts[0], out ip);
        }
        else
        {
            ip = FindClient(parts[0])?.Address;
        }
        if (ip == null)
        {
            return $"'{parts[0]}' ist weder Spieler-ID noch IP.";
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        bans.Add(ip);
        string reason = parts.Length > 1 ? "Gesperrt: " + parts[1] : "Vom Admin gesperrt.";
        int n = 0;
        foreach (Client c in clients.Where(c => c.Address.Equals(ip)).ToList())
        {
            Kick(c, reason);
            n++;
        }
        return $"{ip} gesperrt, {n} Verbindung(en) getrennt.";
    }

    private string Unban(string rest)
    {
        if (!IPAddress.TryParse(rest, out IPAddress? ip))
        {
            return "Aufruf: unban <ip>";
        }
        authFailures.Remove(ip);
        return bans.Remove(ip) ? $"{ip} entsperrt." : $"{ip} war nicht gesperrt (Fehlversuche zurueckgesetzt).";
    }

    private string EndMatch(string rest)
    {
        if (!rooms.TryGetValue(rest.ToLowerInvariant(), out Room? room))
        {
            return $"Raum '{rest}' gibt es nicht.";
        }
        Match? m = room.Match;
        if (m == null)
        {
            return "In diesem Raum laeuft keine Runde.";
        }
        foreach (Participant p in m.Participants.Where(p => !p.Finished))
        {
            p.Status = Protocol.StatusLeft;
            p.Score = 0;
        }
        CheckFinished(room, m);
        return $"Runde {m.Id} in '{room.Name}' beendet.";
    }

    private string CloseRoom(string rest)
    {
        string[] parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !rooms.TryGetValue(parts[0].ToLowerInvariant(), out Room? room))
        {
            return "Aufruf: closeroom <raum> [grund]";
        }
        string reason = parts.Length > 1 ? "Raum geschlossen: " + parts[1] : "Raum vom Admin geschlossen.";
        int n = 0;
        foreach (Client c in room.Members.ToList())
        {
            Kick(c, reason);
            n++;
        }
        return $"Raum '{room.Name}' geschlossen, {n} Spieler getrennt.";
    }

    private string SetPassword(string rest)
    {
        if (rest.Length == 0)
        {
            return "Aufruf: password <neu|off>";
        }
        if (rest.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            options.Password = null;
            return "Passwort abgeschaltet (gilt fuer neue Verbindungen).";
        }
        options.Password = rest;
        return "Passwort geaendert (gilt fuer neue Verbindungen; verbundene Spieler bleiben).";
    }

    private string ScheduleShutdown(string rest)
    {
        string[] parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (pendingShutdown == null)
            {
                return "Kein Shutdown geplant.";
            }
            pendingShutdown.Cancel();
            pendingShutdown = null;
            Say(null, "Server-Neustart abgebrochen.");
            return "Shutdown abgebrochen.";
        }
        int seconds = 0;
        string? reason = null;
        if (parts.Length > 0)
        {
            if (Protocol.TryInt(parts[0], out int s) && s >= 0)
            {
                seconds = s;
                reason = parts.Length > 1 ? parts[1] : null;
            }
            else
            {
                reason = rest;
            }
        }
        RequestShutdown(reason, TimeSpan.FromSeconds(seconds));
        return seconds > 0 ? $"Server wird in {seconds}s beendet." : "Server wird beendet.";
    }

    /// <summary>
    /// Announces the shutdown, says goodbye to every client and stops the server.
    /// Safe to call from any thread.
    /// </summary>
    public Task RequestShutdown(string? reason, TimeSpan delay)
    {
        CancellationTokenSource cts;
        lock (sync)
        {
            pendingShutdown?.Cancel();
            cts = new CancellationTokenSource();
            pendingShutdown = cts;
            shutdownReason = string.IsNullOrWhiteSpace(reason) ? "Server wird beendet." : reason.Trim();
        }
        return Task.Run(async () =>
        {
            try
            {
                var remaining = delay;
                while (remaining > TimeSpan.Zero)
                {
                    lock (sync)
                    {
                        Say(null, $"Server wird in {(int)Math.Ceiling(remaining.TotalSeconds)}s beendet: {shutdownReason}");
                    }
                    TimeSpan step = remaining > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : remaining;
                    await Task.Delay(step, cts.Token);
                    remaining -= step;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (sync)
            {
                foreach (Client c in clients.ToList())
                {
                    Kick(c, shutdownReason ?? "Server wird beendet.");
                }
            }
            await Task.Delay(500);
            shutdown.Cancel();
        });
    }
}
