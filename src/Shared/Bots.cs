#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OGMultiplayer
{
    /// <summary>Shared bot behaviour for the server and the plugin's offline mode.</summary>
    public static class Bots
    {
        public const int MinLevel = 1;
        public const int MaxLevel = 3;
        public const int DefaultLevel = 2;

        /// <summary>
        /// Used when nobody has a real score yet: the middle of the scores the game seeds
        /// its own leaderboards with (1500, 1300, 1100, 900, 600).
        /// </summary>
        public const int DefaultReference = 1050;

        public static readonly string[] Names = { "OMA", "OPA", "KAP", "JAN", "TJO", "ELK", "FIE", "HAJ", "MOI", "WAT", "DEI", "KOB" };

        /// <summary>Playable character ids of the game (unknown ids fall back on the client).</summary>
        public static readonly string[] Characters = { "tim", "karin", "svea", "luca", "folkert", "tjark" };

        public static string LevelName(int level)
        {
            switch (level)
            {
                case 1:
                    return "LEICHT";
                case 3:
                    return "SCHWER";
                default:
                    return "MITTEL";
            }
        }

        public static int ClampLevel(int level)
        {
            return Math.Max(MinLevel, Math.Min(MaxLevel, level));
        }

        /// <summary>Final score: a share of the reference score (median of real results) with some spread.</summary>
        public static int TargetScore(int level, int reference, Random rng)
        {
            double factor;
            switch (ClampLevel(level))
            {
                case 1:
                    factor = 0.55;
                    break;
                case 3:
                    factor = 1.2;
                    break;
                default:
                    factor = 0.88;
                    break;
            }
            double spread = 0.75 + rng.NextDouble() * 0.5;
            return Math.Max(10, (int)Math.Round(Math.Max(100, reference) * factor * spread / 5.0) * 5);
        }

        /// <summary>How long the bot "plays" before it reports its score.</summary>
        public static TimeSpan Duration(Random rng)
        {
            return TimeSpan.FromSeconds(20 + rng.NextDouble() * 40);
        }

        /// <summary>Score shown while playing: grows with the elapsed time.</summary>
        public static int LiveScore(int target, TimeSpan elapsed, TimeSpan duration)
        {
            double t = duration.TotalSeconds <= 0 ? 1 : Math.Min(1, Math.Max(0, elapsed.TotalSeconds / duration.TotalSeconds));
            return (int)(target * t);
        }

        /// <summary>Median of recent real scores, or the default.</summary>
        public static int Reference(IList<int> scores)
        {
            List<int> valid = scores?.Where(s => s > 0).OrderBy(s => s).ToList() ?? new List<int>();
            return valid.Count == 0 ? DefaultReference : valid[valid.Count / 2];
        }

        public static string PickName(IEnumerable<string> taken, Random rng)
        {
            var used = new HashSet<string>(taken ?? Enumerable.Empty<string>());
            List<string> free = Names.Where(n => !used.Contains(n)).ToList();
            return free.Count > 0 ? free[rng.Next(free.Count)] : Names[rng.Next(Names.Length)];
        }

        public static string PickCharacter(Random rng)
        {
            return Characters[rng.Next(Characters.Length)];
        }
    }
}
