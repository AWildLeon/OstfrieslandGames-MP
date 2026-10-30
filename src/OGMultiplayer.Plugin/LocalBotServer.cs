using System;
using System.Collections.Generic;
using System.Linq;

namespace OGMultiplayer.Plugin
{
    /// <summary>
    /// Offline "against bots" mode: a tiny in-process stand-in for the server that speaks
    /// the same protocol for one local player plus bots. No network involved.
    /// </summary>
    internal sealed class LocalBotServer : ITransport
    {
        private const int HumanId = 1;

        private sealed class Member
        {
            public int Id;
            public string Initials;
            public string Character;
            public bool IsBot;
            public string Status = Protocol.StatusLobby;
            public int Score;
            public int Target;
            public TimeSpan Duration;
        }

        private readonly Queue<string> outgoing = new Queue<string>();
        private readonly List<Member> members = new List<Member>();
        private readonly Random random = new Random();
        private readonly Func<int, IList<int>> referenceScores;
        private int nextId = HumanId + 1;
        private int botLevel = Bots.DefaultLevel;
        private int matchId;
        private int discipline;
        private bool inMatch;
        private bool goSent;
        private DateTime startedAt;
        private DateTime nextLobby;

        /// <param name="referenceScores">Local real scores per discipline; bots aim at their median.</param>
        public LocalBotServer(Func<int, IList<int>> referenceScores)
        {
            this.referenceScores = referenceScores;
            Reply(Protocol.Challenge, Protocol.Int(Protocol.Version), "offline", "0");
        }

        public string LastError => null;

        public bool TryReceive(out string line)
        {
            if (outgoing.Count > 0)
            {
                line = outgoing.Dequeue();
                return true;
            }
            line = null;
            return false;
        }

        public void Dispose()
        {
            outgoing.Clear();
        }

        private void Reply(params string[] fields)
        {
            outgoing.Enqueue(Protocol.Encode(fields));
        }

        private Member Human => members.FirstOrDefault(m => m.Id == HumanId);

        public void Send(string line)
        {
            string[] f = Protocol.Decode(line);
            if (f.Length == 0)
            {
                return;
            }
            switch (f[0])
            {
                case Protocol.Ping:
                    Reply(Protocol.Pong);
                    break;
                case Protocol.Hello:
                    if (f.Length >= 5 && Human == null)
                    {
                        members.Add(new Member { Id = HumanId, Initials = f[2], Character = f[3] });
                        // start with one opponent so a round can begin right away
                        HandleBot(new[] { Protocol.Bot, Protocol.BotAdd });
                        outgoing.Clear();
                        Reply(Protocol.Welcome, Protocol.Int(HumanId));
                        Lobby();
                    }
                    break;
                case Protocol.Char:
                    if (!inMatch && Human != null && f.Length > 1)
                    {
                        Human.Character = f[1];
                        Lobby();
                    }
                    break;
                case Protocol.Bot:
                    HandleBot(f);
                    break;
                case Protocol.Start:
                    HandleStart(f);
                    break;
                case Protocol.Ready:
                    if (inMatch && Human != null && MatchArg(f))
                    {
                        Human.Status = Protocol.StatusPlaying;
                        if (!goSent)
                        {
                            goSent = true;
                            startedAt = DateTime.UtcNow;
                            foreach (Member bot in members.Where(m => m.IsBot))
                            {
                                bot.Status = Protocol.StatusPlaying;
                            }
                        }
                        Reply(Protocol.Go, Protocol.Int(matchId));
                        Lobby();
                    }
                    break;
                case Protocol.Live:
                    if (inMatch && Human != null && MatchArg(f) && f.Length > 2 && Protocol.TryInt(f[2], out int live))
                    {
                        Human.Score = Math.Max(0, live);
                    }
                    break;
                case Protocol.Score:
                    if (inMatch && Human != null && MatchArg(f) && f.Length > 2 && Protocol.TryInt(f[2], out int score))
                    {
                        Human.Score = Math.Max(0, score);
                        Human.Status = Protocol.StatusDone;
                        Tick();
                    }
                    break;
                case Protocol.Leave:
                    if (inMatch && Human != null && MatchArg(f))
                    {
                        Human.Score = 0;
                        Human.Status = Protocol.StatusLeft;
                        Tick();
                    }
                    break;
            }
        }

        private bool MatchArg(string[] f)
        {
            return f.Length > 1 && Protocol.TryInt(f[1], out int id) && id == matchId;
        }

        private void HandleBot(string[] f)
        {
            if (inMatch || f.Length < 2)
            {
                return;
            }
            switch (f[1])
            {
                case Protocol.BotAdd:
                    if (members.Count >= Protocol.MaxPlayers)
                    {
                        Reply(Protocol.Error, $"Maximal {Protocol.MaxPlayers} Spieler.");
                        return;
                    }
                    members.Add(new Member
                    {
                        Id = nextId++,
                        Initials = Bots.PickName(members.Select(m => m.Initials), random),
                        Character = Bots.PickCharacter(random),
                        IsBot = true
                    });
                    break;
                case Protocol.BotRemove:
                    Member last = members.LastOrDefault(m => m.IsBot);
                    if (last != null)
                    {
                        members.Remove(last);
                    }
                    break;
                case Protocol.BotLevel:
                    if (f.Length > 2 && Protocol.TryInt(f[2], out int level))
                    {
                        botLevel = Bots.ClampLevel(level);
                    }
                    break;
            }
            Lobby();
        }

        private void HandleStart(string[] f)
        {
            if (inMatch || f.Length < 2 || !Protocol.TryInt(f[1], out int d) || d < 0 || d >= Protocol.DisciplineCount)
            {
                return;
            }
            if (members.Count < Protocol.MinPlayers)
            {
                Reply(Protocol.Error, "Erst mindestens einen Bot hinzufuegen.");
                return;
            }
            matchId++;
            discipline = d;
            inMatch = true;
            goSent = false;
            int reference = Bots.Reference(referenceScores(d));
            foreach (Member m in members)
            {
                m.Score = 0;
                m.Status = m.IsBot ? Protocol.StatusReady : Protocol.StatusLoading;
                if (m.IsBot)
                {
                    m.Target = Bots.TargetScore(botLevel, reference, random);
                    m.Duration = Bots.Duration(random);
                }
            }
            var fields = new List<string> { Protocol.Begin, Protocol.Int(matchId), Protocol.Int(d) };
            fields.AddRange(members.Select(m => Protocol.Join(Protocol.Int(m.Id), m.Initials, m.Character)));
            Reply(fields.ToArray());
            Lobby();
        }

        public void Tick()
        {
            if (!inMatch || !goSent)
            {
                return;
            }
            DateTime now = DateTime.UtcNow;
            Member human = Human;
            bool humanDone = human == null || human.Status == Protocol.StatusDone || human.Status == Protocol.StatusLeft;
            foreach (Member bot in members.Where(m => m.IsBot && m.Status == Protocol.StatusPlaying))
            {
                TimeSpan elapsed = now - startedAt;
                if (humanDone || elapsed >= bot.Duration)
                {
                    bot.Score = bot.Target;
                    bot.Status = Protocol.StatusDone;
                }
                else
                {
                    bot.Score = Bots.LiveScore(bot.Target, elapsed, bot.Duration);
                }
            }
            if (members.All(m => m.Status == Protocol.StatusDone || m.Status == Protocol.StatusLeft))
            {
                var fields = new List<string> { Protocol.Result, Protocol.Int(matchId) };
                fields.AddRange(members.Select(m => Protocol.Join(Protocol.Int(m.Id), Protocol.Int(m.Score))));
                Reply(fields.ToArray());
                inMatch = false;
                foreach (Member m in members)
                {
                    m.Status = Protocol.StatusLobby;
                }
                Lobby();
            }
            else if (now >= nextLobby)
            {
                nextLobby = now.AddSeconds(1);
                Lobby();
            }
        }

        private void Lobby()
        {
            var fields = new List<string> { Protocol.Lobby, "offline", Protocol.Int(HumanId), inMatch ? Protocol.StateMatch : Protocol.StateLobby, Protocol.Int(botLevel) };
            fields.AddRange(members.Select(m => Protocol.Join(Protocol.Int(m.Id), m.Initials, m.Character, m.Status, Protocol.Int(m.Score), m.IsBot ? "1" : "0")));
            Reply(fields.ToArray());
        }
    }
}
