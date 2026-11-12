using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NbaQuant
{
    class BRow
    {
        public string Id, Name, Team, Pos;
        public double G, GS, MP, FG, FGA, FG3, FG3A, FG2, FG2A, FT, FTA, ORB, DRB, TRB, AST, STL, BLK, TOV, PTS;
        public double Obpm, Dbpm, Bpm, Usg;
        public string LastTeam;
    }

    class PlayerP
    {
        public string Pid, Name, Team, Pos, Group;
        public int HeightIn;
        public string Status = "AVAIL"; public double PPlay = 1.0; public string InjNote = "";
        // current season (2026-27) from ESPN box scores
        public int Gp, DnpCd, DnpOther, Starts;
        public double CsMin, CsPts, Cs2a, Cs2m, Cs3a, Cs3m, CsFta, CsFtm, CsTov, CsOreb, CsDreb, CsAst;
        public List<double> CsMinList = new List<double>();
        public List<int> CsPtsList = new List<int>();
        public List<BoxLine> CsBox = new List<BoxLine>();   // this season's games, in date order
        public string MatchupNote = "";                      // how tonight's opponent changes this player's inputs
        // prior season (2025-26) from Basketball-Reference
        public bool HasPs; public bool SameTeam; public BRow Ps; public bool PsStale; // PsStale: prior comes from 2024-25 (missed 2025-26)
        public double Obpm26, Dbpm26, Mp26, Obpm27, Dbpm27, Mp27;
        // projections
        public double BaseMin, PRot, ExpMin;
        public double R2a, R3a, RTrip, RTov, ROreb, RDreb, RAst, P2, P3, Pft, Usage, PickW, Obpm, Dbpm;
        public double StartRate;
        public double LearnPts = 1, LearnAst = 1, MinTrendShift;   // trend learning applied to this projection (Learner)
        public int Confidence;
        public double CsMpg { get { return Gp > 0 ? CsMin / Gp : 0; } }
        public double PsMpg { get { return HasPs && Ps.G > 0 ? Ps.MP / Ps.G : 0; } }
    }

    class GroupPrior
    {
        public double R2a, R3a, RFta, RTov, ROreb, RDreb, RAst, P2, P3, Pft, R2m, R3m;
    }

    class MarketLine
    {
        public string Book; public double MlH, MlA, SprH, SprHOdds, SprAOdds, Total, OverOdds, UnderOdds;
        public bool HasMl { get { return MlH != 0 && MlA != 0; } }
    }

    class GameDef
    {
        public string Home, Away, AnHome, AnAway, Tip, EspnId, AnId;
        public MarketLine DK, Consensus; public List<MarketLine> Books = new List<MarketLine>();
        public bool HomeB2B, AwayB2B; public int HomeRest, AwayRest;
    }

    static class Data
    {
        public static Dictionary<string, string> EspnToBref = new Dictionary<string, string> {
            {"BKN","BRK"},{"NY","NYK"},{"SA","SAS"},{"BOS","BOS"},{"DET","DET"},{"SAC","SAC"},{"POR","POR"},{"LAL","LAL"},
            {"GS","GSW"},{"NO","NOP"},{"UTAH","UTA"},{"WSH","WAS"},{"PHX","PHO"},{"CHA","CHO"} };
        public static Dictionary<string, string> FullName = new Dictionary<string, string> {
            {"BKN","Brooklyn Nets"},{"BOS","Boston Celtics"},{"DET","Detroit Pistons"},{"NY","New York Knicks"},
            {"SAC","Sacramento Kings"},{"SA","San Antonio Spurs"},{"POR","Portland Trail Blazers"},{"LAL","Los Angeles Lakers"} };

        // Basketball-Reference pages were decoded as Latin-1 by PowerShell 5.1; undo that for accented names.
        public static string FixMojibake(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOfAny(new[] { 'Ã', 'Ä', 'Å', 'Ä', 'Å' }) < 0) return s;
            try { return System.Text.Encoding.UTF8.GetString(System.Text.Encoding.GetEncoding(28591).GetBytes(s)); }
            catch { return s; }
        }

        public static string Group(string pos)
        {
            if (string.IsNullOrEmpty(pos)) return "F";
            string p = pos.Split('-')[0].Trim().ToUpperInvariant();
            if (p == "C") return "C";
            if (p == "PG" || p == "SG" || p == "G") return "G";
            return "F";
        }

        public static Dictionary<string, List<BRow>> LoadBref(string totalsPath, string advPath)
        {
            var res = new Dictionary<string, List<BRow>>();
            var t = U.ReadCsv(totalsPath);
            var h = t[0].ToList();
            Func<string[], string, double> g = (r, c) => { int i = h.IndexOf(c); return i >= 0 && i < r.Length ? U.D(r[i]) : 0; };
            Func<string[], string, string> gs = (r, c) => { int i = h.IndexOf(c); return i >= 0 && i < r.Length ? r[i] : ""; };
            foreach (var r in t.Skip(1))
            {
                string id = r[0];
                if (string.IsNullOrEmpty(id)) continue;
                var b = new BRow
                {
                    Id = id, Name = FixMojibake(gs(r, "name_display")), Team = gs(r, "team_name_abbr"), Pos = gs(r, "pos"),
                    G = g(r, "games"), GS = g(r, "games_started"), MP = g(r, "mp"), FG = g(r, "fg"), FGA = g(r, "fga"),
                    FG3 = g(r, "fg3"), FG3A = g(r, "fg3a"), FG2 = g(r, "fg2"), FG2A = g(r, "fg2a"), FT = g(r, "ft"), FTA = g(r, "fta"),
                    ORB = g(r, "orb"), DRB = g(r, "drb"), TRB = g(r, "trb"), AST = g(r, "ast"), STL = g(r, "stl"), BLK = g(r, "blk"),
                    TOV = g(r, "tov"), PTS = g(r, "pts")
                };
                if (!res.ContainsKey(id)) res[id] = new List<BRow>();
                res[id].Add(b);
            }
            if (advPath != null && File.Exists(advPath))
            {
                var a = U.ReadCsv(advPath);
                var ah = a[0].ToList();
                var seen = new HashSet<string>();
                foreach (var r in a.Skip(1))
                {
                    string id = r[0];
                    if (string.IsNullOrEmpty(id) || seen.Contains(id) || !res.ContainsKey(id)) continue;
                    seen.Add(id);
                    var b = res[id][0];
                    b.Obpm = U.D(r[ah.IndexOf("obpm")]); b.Dbpm = U.D(r[ah.IndexOf("dbpm")]);
                    b.Bpm = U.D(r[ah.IndexOf("bpm")]); b.Usg = U.D(r[ah.IndexOf("usg_pct")]);
                }
            }
            foreach (var kv in res) kv.Value[0].LastTeam = kv.Value[kv.Value.Count - 1].Team;
            return res;
        }

        public static Dictionary<string, GroupPrior> Priors(Dictionary<string, List<BRow>> b26)
        {
            var res = new Dictionary<string, GroupPrior>();
            foreach (var grp in new[] { "G", "F", "C" })
            {
                var rows = b26.Values.Select(l => l[0]).Where(r => r.MP >= 500 && Group(r.Pos) == grp).ToList();
                double mp = rows.Sum(r => r.MP);
                var gp = new GroupPrior
                {
                    R2a = rows.Sum(r => r.FG2A) / mp, R3a = rows.Sum(r => r.FG3A) / mp, RFta = rows.Sum(r => r.FTA) / mp,
                    RTov = rows.Sum(r => r.TOV) / mp, ROreb = rows.Sum(r => r.ORB) / mp, RDreb = rows.Sum(r => r.DRB) / mp,
                    RAst = rows.Sum(r => r.AST) / mp,
                    P2 = rows.Sum(r => r.FG2) / rows.Sum(r => r.FG2A), P3 = rows.Sum(r => r.FG3) / rows.Sum(r => r.FG3A),
                    Pft = rows.Sum(r => r.FT) / rows.Sum(r => r.FTA),
                    R2m = rows.Sum(r => r.FG2) / mp, R3m = rows.Sum(r => r.FG3) / mp
                };
                // replacement-level shooting priors are a bit below the 500+ minute average
                gp.P2 -= 0.01; gp.P3 -= 0.01;
                res[grp] = gp;
            }
            return res;
        }

        public static List<MarketLine> LoadOdds(string path, string anAway, string anHome)
        {
            var rows = U.ReadCsv(path);
            var h = rows[0].ToList();
            var res = new List<MarketLine>();
            foreach (var r in rows.Skip(1))
            {
                if (r[0] != anAway || r[1] != anHome) continue;
                res.Add(new MarketLine
                {
                    Book = r[3], MlH = U.D(r[4]), MlA = U.D(r[5]), SprH = U.D(r[6]), SprHOdds = U.D(r[7]), SprAOdds = U.D(r[8]),
                    Total = U.D(r[9]), OverOdds = U.D(r[10]), UnderOdds = U.D(r[11])
                });
            }
            return res;
        }
    }
}
