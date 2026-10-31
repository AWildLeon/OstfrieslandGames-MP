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
    /// With a server password, proof = lowercase hex HMAC-SHA256(key: ServerKey(password),
    /// message: nonce). Neither the password nor the key goes over the wire, and the server
    /// only stores the key. Without a password, proof is empty.
    ///
    /// Rooms: after HELLO the client is on the server but in no room (room browser) unless
    /// HELLO named a room. Room passwords never travel in clear text either: the room key is
    /// PBKDF2-SHA256 of the room password salted with the room name (RoomKey), the join
    /// proof is AuthProof(roomKey, nonce) with the same connection nonce. The server
    /// password works the same way: proof = AuthProof(ServerKey(password), nonce).
    ///
    /// Client -> Server
    ///   DOC      privacy | imprint | rules  (allowed before HELLO)
    ///   HELLO    version initials characterId room proof consentHash
    ///   ROOMS                               (ask for the room list)
    ///   CREATE   room roomKey               (empty key = open room; creator becomes host)
    ///   JOIN     room proof
    ///   LEAVEROOM                           (back to the room browser)
    ///   ROOMPW   roomKey                    (host: set/clear the room password)
    ///   KICK     playerId                   (host: remove a player or bot from the room)
    ///   START    discipline                 (host only)
    ///   READY    matchId                    (client is waiting at the turn start)
    ///   LIVE     matchId score              (optional live score while playing)
    ///   SCORE    matchId score              (turn finished)
    ///   LEAVE    matchId                    (aborted the running match)
    ///   PAUSE    matchId 0|1                (game paused / resumed during the own turn)
    ///   CHAR     characterId                (change character while in lobby)
    ///   BOT      ADD | REMOVE | LEVEL n     (host only, while in lobby)
    ///   PING
    ///
    /// Server -> Client
    ///   CHALLENGE version nonce passwordRequired(0|1) public(0|1) privacyHash serverName
    ///   DOCTEXT  kind text                  (text escaped with EscapeText)
    ///   WELCOME  playerId
    ///   SERVERINFO name public(0|1) motd docs   (docs = comma list of available documents)
    ///   ROOMLIST maxRooms room*             (room = name|players|max|state|locked|hostInitials)
    ///   JOINED   room
    ///   ROOMLEFT reason                     (back in the room browser)
    ///   ERROR    message
    ///   LOBBY    room hostId state botLevel locked player*
    ///            (player = id|INI|char|status|score|bot|graceLeft; status "paused" while the
    ///            game is paused; graceLeft = seconds until a paused player's live score
    ///            counts, only while nobody else is still playing, else left out)
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
        /// <summary>Longest line a client may send.</summary>
        public const int MaxLineLength = 4096;

        /// <summary>Longest line a server may send (legal texts).</summary>
        public const int MaxServerLineLength = 65536;

        public const string DocPrivacy = "privacy";
        public const string DocImprint = "imprint";
        public const string DocRules = "rules";

        public const string Hello = "HELLO";
        public const string Start = "START";
        public const string Ready = "READY";
        public const string Live = "LIVE";
        public const string Score = "SCORE";
        public const string Leave = "LEAVE";
        public const string Pause = "PAUSE";
        public const string Char = "CHAR";
        public const string Ping = "PING";
        public const string Doc = "DOC";
        public const string Rooms = "ROOMS";
        public const string Create = "CREATE";
        public const string JoinRoom = "JOIN";
        public const string LeaveRoom = "LEAVEROOM";
        public const string RoomPassword = "ROOMPW";
        public const string KickPlayer = "KICK";
        public const string Bot = "BOT";
        public const string BotAdd = "ADD";
        public const string BotRemove = "REMOVE";
        public const string BotLevel = "LEVEL";

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
        public const string DocText = "DOCTEXT";
        public const string ServerInfo = "SERVERINFO";
        public const string RoomList = "ROOMLIST";
        public const string Joined = "JOINED";
        public const string RoomLeft = "ROOMLEFT";

        public const string StatusLobby = "lobby";
        public const string StatusLoading = "loading";
        public const string StatusReady = "ready";
        public const string StatusPlaying = "playing";
        public const string StatusPaused = "paused";
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

        /// <summary>PBKDF2 rounds for password keys; the server never learns the password itself.</summary>
        public const int KeyIterations = 30000;

        /// <summary>
        /// Room key from a room password: hex PBKDF2-SHA256(password, salt "ogmp-room:" + room).
        /// The room name as salt means equal passwords give different keys in different rooms.
        /// Empty password = empty key (open room).
        /// </summary>
        public static string RoomKey(string password, string room)
        {
            return string.IsNullOrEmpty(password) ? string.Empty : DeriveKey(password, "ogmp-room:" + (room ?? string.Empty));
        }

        /// <summary>Server key from the server password; the server only keeps this key.</summary>
        public static string ServerKey(string password)
        {
            return string.IsNullOrEmpty(password) ? string.Empty : DeriveKey(password, "ogmp-server:v1");
        }

        private static string DeriveKey(string password, string salt)
        {
            return Hex(Pbkdf2Sha256(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), KeyIterations));
        }

        /// <summary>
        /// PBKDF2-HMAC-SHA256 (RFC 8018), one 32-byte block. Written out because the game's Mono
        /// runtime does not honour the hash algorithm argument of Rfc2898DeriveBytes.
        /// </summary>
        public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations)
        {
            using (var hmac = new System.Security.Cryptography.HMACSHA256(password))
            {
                var block = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, block, 0, salt.Length);
                block[block.Length - 1] = 1; // INT(1), big endian
                byte[] u = hmac.ComputeHash(block);
                var result = (byte[])u.Clone();
                for (int i = 1; i < iterations; i++)
                {
                    u = hmac.ComputeHash(u);
                    for (int j = 0; j < result.Length; j++)
                    {
                        result[j] ^= u[j];
                    }
                }
                return result;
            }
        }

        /// <summary>Short fingerprint of a document, used to remember which version was accepted.</summary>
        public static string TextHash(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty))).Substring(0, 16);
            }
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Makes multi-line text fit into one field (backslash, tab and newline escaped).</summary>
        public static string EscapeText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(text.Length + 16);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string UnescapeText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[++i];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
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
