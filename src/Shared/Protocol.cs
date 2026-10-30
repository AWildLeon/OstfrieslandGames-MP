#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OGMultiplayer
{
    /// <summary>
    /// Line based wire protocol shared by plugin and server.
    /// Every message is one UTF-8 line, fields separated by TAB.
    /// The first field is the command.
    ///
    /// Handshake: on connect the server sends CHALLENGE; the client answers with HELLO.
    /// With a server password, proof = lowercase hex HMAC-SHA256(key: UTF-8 password,
    /// message: nonce). The password itself never goes over the wire. Without a
    /// password, proof is empty.
    ///
    /// Client -> Server
    ///   HELLO    version initials characterId room proof
    ///   START    discipline                 (host only)
    ///   READY    matchId                    (client is waiting at the turn start)
    ///   LIVE     matchId score              (optional live score while playing)
    ///   SCORE    matchId score              (turn finished)
    ///   LEAVE    matchId                    (aborted the running match)
    ///   CHAR     characterId                (change character while in lobby)
    ///   PING
    ///
    /// Server -> Client
    ///   CHALLENGE version nonce passwordRequired(0|1)
    ///   WELCOME  playerId
    ///   ERROR    message
    ///   LOBBY    room hostId state player*  (player = id|INI|char|status|score)
    ///   BEGIN    matchId discipline player* (player = id|INI|char)
    ///   GO       matchId
    ///   RESULT   matchId score*             (score = id|points)
    ///   PONG
    ///   MSG      text                       (message from the server admin)
    ///   BYE      reason                     (server closes the connection)
    /// </summary>
    public static class Protocol
    {
        public const int Version = 1;
        public const int DefaultPort = 27420;
        public const int MinPlayers = 2;
        public const int MaxPlayers = 4;
        public const int DisciplineCount = 5;
        public const int MaxLineLength = 4096;

        public const string Hello = "HELLO";
        public const string Start = "START";
        public const string Ready = "READY";
        public const string Live = "LIVE";
        public const string Score = "SCORE";
        public const string Leave = "LEAVE";
        public const string Char = "CHAR";
        public const string Ping = "PING";

        public const string Challenge = "CHALLENGE";
        public const string Welcome = "WELCOME";
        public const string Error = "ERROR";
        public const string Lobby = "LOBBY";
        public const string Begin = "BEGIN";
        public const string Go = "GO";
        public const string Result = "RESULT";
        public const string Pong = "PONG";
        public const string Msg = "MSG";
        public const string Bye = "BYE";

        public const string StatusLobby = "lobby";
        public const string StatusLoading = "loading";
        public const string StatusReady = "ready";
        public const string StatusPlaying = "playing";
        public const string StatusDone = "done";
        public const string StatusLeft = "left";

        public const string StateLobby = "lobby";
        public const string StateMatch = "match";

        public static string Encode(params string[] fields)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append('\t');
                }
                sb.Append(Clean(fields[i]));
            }
            return sb.ToString();
        }

        public static string[] Decode(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return new string[0];
            }
            return line.TrimEnd('\r').Split('\t');
        }

        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c != '\t' && c != '\r' && c != '\n')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>Lowercase hex HMAC-SHA256 of the nonce, keyed with the password.</summary>
        public static string AuthProof(string password, string nonce)
        {
            using (var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(password ?? string.Empty)))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(nonce ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        public static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Three letters A-Z, upper case. Returns null when invalid.</summary>
        public static string NormalizeInitials(string text)
        {
            if (text == null)
            {
                return null;
            }
            string t = text.Trim().ToUpperInvariant();
            if (t.Length != 3)
            {
                return null;
            }
            foreach (char c in t)
            {
                if (c < 'A' || c > 'Z')
                {
                    return null;
                }
            }
            return t;
        }

        /// <summary>Character ids / room names: [a-z0-9_-], 1..32 chars. Returns null when invalid.</summary>
        public static string NormalizeId(string text)
        {
            if (text == null)
            {
                return null;
            }
            string t = text.Trim().ToLowerInvariant();
            if (t.Length < 1 || t.Length > 32)
            {
                return null;
            }
            foreach (char c in t)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                {
                    return null;
                }
            }
            return t;
        }

        /// <summary>Joins sub fields of one field with '|'. Sub fields must not contain '|'.</summary>
        public static string Join(params string[] parts)
        {
            var cleaned = new List<string>(parts.Length);
            foreach (string p in parts)
            {
                cleaned.Add(Clean(p).Replace("|", string.Empty));
            }
            return string.Join("|", cleaned);
        }

        public static string[] SplitPart(string part)
        {
            return (part ?? string.Empty).Split('|');
        }
    }
}
