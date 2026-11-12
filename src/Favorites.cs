using System;
using System.Collections.Generic;
using System.Linq;

namespace NbaQuant
{
    // One favorite line: the highest rung of a DraftKings-style ladder that a player clears in at least FavMin (60%) of the
    // simulations he plays in. It is neither the safest line nor the single most common value: it is the boldest line the
    // simulations still back. Reach = the next rung up, Floor = the highest rung at 85% or better.
    class Fav
    {
        public PStat Ps; public string Stat; public int Line; public double P;
        public int Reach = -1, Floor = -1; public double PReach, PFloor;
        public int Hit, Gp, L5Hit, L5N; public double SeasonAvg = double.NaN;   // how often he has reached the line this season / last five
        public bool Bold;                                                       // the line is at least 80% of his season average
        public string Unit { get { return Stat == "PTS" ? "pts" : Stat == "REB" ? "reb" : Stat == "AST" ? "ast" : "threes"; } }
        public string Desc { get { return Ps.P.Name + " " + Line + "+ " + Unit; } }
    }

    static class Favorites
    {
        public static readonly string[] Stats = { "PTS", "REB", "AST", "3PM" };
        public static readonly Dictionary<string, int[]> Ladder = new Dictionary<string, int[]>
        {
            { "PTS", new[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50 } }, { "REB", new[] { 2, 4, 6, 8, 10, 12, 14, 16 } },
            { "AST", new[] { 2, 4, 6, 8, 10, 12, 14 } }, { "3PM", new[] { 1, 2, 3, 4, 5, 6, 7 } }
        };

        public static Dist DistOf(PStat p, string stat) { return stat == "PTS" ? p.Pts : stat == "REB" ? p.Reb : stat == "AST" ? p.Ast : p.Tpm; }
        public static int Val(BoxLine b, string stat) { return stat == "PTS" ? b.Pts : stat == "REB" ? b.Reb : stat == "AST" ? b.Ast : b.Tpm; }

        // null when even the lowest rung falls short of FavMin
        public static Fav Of(PStat p, string stat)
        {
            var d = DistOf(p, stat); var lad = Ladder[stat]; double min = Program.S.FavMin;
            int k = -1;
            for (int i = 0; i < lad.Length; i++) { if (d.PAtLeast(lad[i]) >= min) k = i; else break; }
            if (k < 0) return null;
            var f = new Fav { Ps = p, Stat = stat, Line = lad[k], P = d.PAtLeast(lad[k]) };
            if (k + 1 < lad.Length) { f.Reach = lad[k + 1]; f.PReach = d.PAtLeast(lad[k + 1]); }
            for (int i = k; i >= 0; i--) if (d.PAtLeast(lad[i]) >= 0.85) { f.Floor = lad[i]; f.PFloor = d.PAtLeast(lad[i]); break; }
            var box = p.P.CsBox; f.Gp = box.Count;
            if (box.Count > 0)
            {
                f.Hit = box.Count(b => Val(b, stat) >= f.Line); f.SeasonAvg = box.Average(b => (double)Val(b, stat));
                var l5 = box.Skip(Math.Max(0, box.Count - 5)).ToList(); f.L5N = l5.Count; f.L5Hit = l5.Count(b => Val(b, stat) >= f.Line);
            }
            f.Bold = f.Gp >= 3 && f.Line >= 0.8 * f.SeasonAvg;
            return f;
        }

        // every favorite in a game (players projected for 10+ minutes)
        public static List<Fav> ForGame(GameCtx g)
        {
            var res = new List<Fav>();
            foreach (var p in Analysis.Players.Where(x => x.G == g && x.MeanMin >= 10))
                foreach (var st in Stats) { var f = Of(p, st); if (f != null) res.Add(f); }
            return res;
        }

        // The short list shown for a game: the lines that matter, not the easiest ones. Healthy players projected for 20+
        // minutes, lines of real size (10+ pts, 6+ reb, 4+ ast, 2+ threes), ranked by chance x size of the line (a 30-point
        // line counts twice a 15-point line; 6 rebounds, 4 assists and 2 threes count like 15 points), two per player at most.
        static readonly Dictionary<string, double> Size = new Dictionary<string, double> { { "PTS", 15 }, { "REB", 6 }, { "AST", 4 }, { "3PM", 2 } };
        static readonly Dictionary<string, int> MinLine = new Dictionary<string, int> { { "PTS", 10 }, { "REB", 6 }, { "AST", 4 }, { "3PM", 2 } };
        public static List<Fav> Top(GameCtx g, int take = 8)
        {
            var res = new List<Fav>(); var per = new Dictionary<string, int>();
            foreach (var f in ForGame(g).Where(f => f.Ps.MeanMin >= 20 && f.Ps.PPlays >= 0.9 && f.Ps.P.Status != "Q" && f.Line >= MinLine[f.Stat]).OrderByDescending(f => f.P * f.Line / Size[f.Stat]))
            {
                int c; per.TryGetValue(f.Ps.P.Pid, out c); if (c >= 2) continue;
                per[f.Ps.P.Pid] = c + 1; res.Add(f); if (res.Count >= take) break;
            }
            return res;
        }
    }
}
