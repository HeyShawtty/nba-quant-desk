using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // Banned teams (overrides\banned_teams.csv: team,since,reason). A banned team's games are left out of every simulation:
    // live slates, four-leg and custom tickets, stress tests and walk-forward backtests, so they never reach a pick, a parlay
    // or a recalibration. Their information is still collected (ESPN scores and box scores, DraftKings lines, official injury
    // reports, the season bank), and Watch() tracks their results so the ban can be lifted once a steady trend shows.
    //   nbaquant-cli ban CLE: reason   |   nbaquant-cli unban CLE   |   nbaquant-cli banned (the watch report)
    static class Ban
    {
        public class Entry { public string Team, Since, Reason; }
        class GameLine { public string Id, Date, Opp, Where; public int Pts, OppPts; public double Spread = double.NaN, WinP = double.NaN; }

        static List<Entry> cache;
        static string FilePath { get { return Program.P(@"overrides\banned_teams.csv"); } }
        static string LogPath { get { return Program.P(@"ledger\ban_log.csv"); } }
        static string WatchPath { get { return Program.P(@"ledger\banned_watch.csv"); } }

        public static List<Entry> List()
        {
            if (cache != null) return cache;
            cache = new List<Entry>();
            if (File.Exists(FilePath))
                foreach (var r in U.ReadCsv(FilePath).Skip(1))
                {
                    string t = r[0].Trim().ToUpperInvariant();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    cache.Add(new Entry { Team = t, Since = r.Length > 1 ? r[1].Trim() : "", Reason = r.Length > 2 ? r[2].Trim() : "" });
                }
            return cache;
        }
        public static bool IsBanned(string team) { return !string.IsNullOrEmpty(team) && List().Any(e => e.Team == team.ToUpperInvariant()); }
        public static bool Involves(string away, string home) { return IsBanned(away) || IsBanned(home); }
        public static string Who(string away, string home) { return string.Join(" and ", new[] { away, home }.Where(IsBanned)); }

        // ESPN codes as the app uses them; Basketball Reference and common alternates are accepted too (NYK, GSW, PHO ...)
        static string Code(string s)
        {
            s = (s ?? "").Trim().ToUpperInvariant();
            if (Program.EspnToBref.ContainsKey(s)) return s;
            var fromBref = Program.EspnToBref.FirstOrDefault(kv => kv.Value == s).Key;
            if (fromBref != null) return fromBref;
            var alt = new Dictionary<string, string> { { "NOP", "NO" }, { "NYK", "NY" }, { "GSW", "GS" }, { "SAS", "SA" }, { "UTA", "UTAH" }, { "WAS", "WSH" }, { "PHO", "PHX" }, { "BRK", "BKN" }, { "CHO", "CHA" } };
            string a; return alt.TryGetValue(s, out a) ? a : null;
        }
        // "CLE", "CLE: reason" or "CLE reason"
        static void Split(string spec, out string team, out string reason)
        {
            spec = (spec ?? "").Trim();
            int c = spec.IndexOfAny(new[] { ':', ' ' });
            team = Code(c < 0 ? spec : spec.Substring(0, c));
            reason = c < 0 ? "" : spec.Substring(c + 1).Trim().TrimStart(':').Trim().Replace(",", ";").Replace("\"", "'");
        }

        static void Save(List<Entry> list)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, new[] { "team,since,reason" }.Concat(list.Select(e => e.Team + "," + e.Since + "," + e.Reason)), new UTF8Encoding(false));
            cache = null;
        }
        static void Record(string action, string team, string reason)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            if (!File.Exists(LogPath)) File.WriteAllText(LogPath, "when,action,team,reason\n", new UTF8Encoding(false));
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "," + action + "," + team + "," + reason + "\n", new UTF8Encoding(false));
        }

        public static string Add(string spec)
        {
            string team, reason; Split(spec, out team, out reason);
            if (team == null) return "Unknown team: " + spec + ". Use the ESPN code, e.g. CLE.";
            var list = List().ToList();
            if (list.Any(e => e.Team == team)) return team + " is already banned (since " + list.First(e => e.Team == team).Since + ").";
            list.Add(new Entry { Team = team, Since = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Reason = reason });
            Save(list); Record("ban", team, reason);
            return team + " banned from " + list.Last().Since + (reason.Length > 0 ? " (" + reason + ")" : "") + ": their games are no longer simulated or priced " +
                "(slates, tickets, stress tests, backtests). Their scores, box scores, DraftKings lines and injury reports are still collected. Banned now: " + string.Join(", ", List().Select(e => e.Team)) + ".";
        }

        public static string Remove(string spec)
        {
            string team, reason; Split(spec, out team, out reason);
            if (team == null) return "Unknown team: " + spec + ".";
            var list = List().ToList();
            var e = list.FirstOrDefault(x => x.Team == team);
            if (e == null) return team + " is not banned.";
            list.Remove(e); Save(list); Record("unban", team, reason);
            return team + " unbanned (banned " + e.Since + " to " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "): the next runs simulate and price their games again." +
                (list.Count > 0 ? " Still banned: " + string.Join(", ", list.Select(x => x.Team)) + "." : " No teams banned.");
        }

        // every completed game of the team this season, with DraftKings' closing spread and no-vig win chance (information only)
        static List<GameLine> Games(string team)
        {
            var res = new List<GameLine>();
            for (var day = Program.S.SeasonStart; day <= DateTime.Now.Date; day = day.AddDays(1))
                foreach (var e in Store.Events(day).Where(x => x.Completed && (x.Home == team || x.Away == team)))
                {
                    var g = Store.Summary(e.Id, true); bool home = g.Home == team;
                    var l = new GameLine { Id = g.Id, Date = g.Date, Opp = home ? g.Away : g.Home, Where = home ? "vs" : "@", Pts = home ? g.HomePts : g.AwayPts, OppPts = home ? g.AwayPts : g.HomePts };
                    if (!double.IsNaN(g.DkSprH)) l.Spread = home ? g.DkSprH : -g.DkSprH;
                    if (!double.IsNaN(g.DkMlH) && !double.IsNaN(g.DkMlA) && g.DkMlH != 0 && g.DkMlA != 0)
                    { double h = U.ImpliedProb(g.DkMlH), a = U.ImpliedProb(g.DkMlA), ph = h / (h + a); l.WinP = home ? ph : 1 - ph; }
                    res.Add(l);
                }
            return res;
        }

        class Roll { public int N, W, L, AtsW, AtsL, AtsP, Lined; public double Margin, VsLine, ExpW; }
        static Roll Sum(IEnumerable<GameLine> gs)
        {
            var r = new Roll();
            foreach (var g in gs)
            {
                int m = g.Pts - g.OppPts; r.N++; if (m > 0) r.W++; else r.L++; r.Margin += m;
                if (!double.IsNaN(g.Spread)) { double v = m + g.Spread; r.VsLine += v; r.Lined++; if (v > 0) r.AtsW++; else if (v < 0) r.AtsL++; else r.AtsP++; }
                if (!double.IsNaN(g.WinP)) r.ExpW += g.WinP;
            }
            if (r.N > 0) r.Margin /= r.N;
            if (r.Lined > 0) r.VsLine /= r.Lined;
            return r;
        }
        static string Text(Roll r)
        {
            if (r.N == 0) return "no games yet";
            return string.Format(U.IC, "{0}-{1}, ATS {2}-{3}{4}, avg margin {5:+0.0;-0.0}, {6:+0.0;-0.0} vs the DraftKings spread, {0} wins vs {7:0.0} expected by DraftKings",
                r.W, r.L, r.AtsW, r.AtsL, r.AtsP > 0 ? "-" + r.AtsP : "", r.Margin, r.VsLine, r.ExpW);
        }
        static object Json(Roll r)
        {
            return new Dictionary<string, object> { { "n", r.N }, { "w", r.W }, { "l", r.L }, { "atsW", r.AtsW }, { "atsL", r.AtsL }, { "atsP", r.AtsP },
                { "margin", Math.Round(r.Margin, 1) }, { "vsLine", Math.Round(r.VsLine, 1) }, { "expW", Math.Round(r.ExpW, 1) } };
        }

        // the model's graded moneyline record on the team's games (all from before the ban: banned games are not priced)
        class ModelRec { public int N, Right; public double Brier, BrierMkt; public int NMkt; }
        static ModelRec Model(string team)
        {
            var r = new ModelRec();
            foreach (var p in Ledger.LoadAll().Where(x => x.Kind == "ML" && !x.Void && !double.IsNaN(x.Y) && (x.Team == team || (x.Selection ?? "") == team + " ML")))
            {
                r.N++; if ((p.P >= 0.5) == (p.Y >= 1)) r.Right++; r.Brier += (p.P - p.Y) * (p.P - p.Y);
                if (!double.IsNaN(p.PMarket)) { r.BrierMkt += (p.PMarket - p.Y) * (p.PMarket - p.Y); r.NMkt++; }
            }
            if (r.N > 0) r.Brier /= r.N;
            if (r.NMkt > 0) r.BrierMkt /= r.NMkt;
            return r;
        }

        // nbaquant-cli banned : the watch report, also written to ledger\banned_watch.csv
        public static string Watch()
        {
            var list = List();
            if (list.Count == 0) return "No teams banned.";
            var sb = new StringBuilder();
            var csv = new List<string> { "team,date,where,opp,pts,opp_pts,margin,dk_spread,vs_line,dk_win_p,result,ats,since_ban" };
            Func<double, string> f = v => double.IsNaN(v) ? "" : v.ToString("0.###", U.IC);
            foreach (var b in list)
            {
                var gs = Games(b.Team); var since = gs.Where(g => string.CompareOrdinal(g.Date, b.Since) >= 0).ToList(); var mr = Model(b.Team);
                sb.AppendLine(b.Team + ": banned since " + b.Since + (b.Reason.Length > 0 ? " (" + b.Reason + ")" : "") + ". Not simulated or priced; information only.");
                sb.AppendLine("  Season:    " + Text(Sum(gs)));
                sb.AppendLine("  Last 10:   " + Text(Sum(gs.Skip(Math.Max(0, gs.Count - 10)))));
                sb.AppendLine("  Since ban: " + Text(Sum(since)));
                if (mr.N > 0) sb.AppendLine(string.Format(U.IC, "  Model on {0} games before the ban: picked the winner in {1} of {2}; Brier {3:0.000} vs DraftKings {4:0.000}", b.Team, mr.Right, mr.N, mr.Brier, mr.BrierMkt));
                foreach (var g in gs.Skip(Math.Max(0, gs.Count - 5)))
                    sb.AppendLine(string.Format(U.IC, "    {0} {1} {2,-4} {3}-{4} {5}{6}", g.Date, g.Where, g.Opp, g.Pts, g.OppPts, g.Pts > g.OppPts ? "W" : "L",
                        double.IsNaN(g.Spread) ? "" : string.Format(U.IC, " | DK {0:+0.0;-0.0}, {1}", g.Spread, g.Pts - g.OppPts + g.Spread > 0 ? "covered" : g.Pts - g.OppPts + g.Spread < 0 ? "did not cover" : "push")));
                foreach (var g in gs)
                {
                    int m = g.Pts - g.OppPts; double v = double.IsNaN(g.Spread) ? double.NaN : m + g.Spread;
                    csv.Add(string.Join(",", b.Team, g.Date, g.Where, g.Opp, g.Pts, g.OppPts, m, f(g.Spread), f(v), f(g.WinP), m > 0 ? "W" : "L",
                        double.IsNaN(v) ? "" : v > 0 ? "W" : v < 0 ? "L" : "P", string.CompareOrdinal(g.Date, b.Since) >= 0 ? "1" : "0"));
                }
            }
            try { File.WriteAllLines(WatchPath, csv, new UTF8Encoding(false)); sb.AppendLine("Saved " + WatchPath); } catch (Exception e) { sb.AppendLine("Watch file not saved: " + e.Message); }
            return sb.ToString().TrimEnd();
        }

        // website: who is banned, tonight's games left out, and each team's watch numbers
        public static object Json()
        {
            var teams = new List<object>();
            foreach (var b in List())
            {
                var gs = Games(b.Team); var mr = Model(b.Team);
                teams.Add(new Dictionary<string, object> {
                    { "team", b.Team }, { "since", b.Since }, { "reason", b.Reason },
                    { "season", Json(Sum(gs)) }, { "last10", Json(Sum(gs.Skip(Math.Max(0, gs.Count - 10)))) }, { "sinceBan", Json(Sum(gs.Where(g => string.CompareOrdinal(g.Date, b.Since) >= 0))) },
                    { "model", mr.N > 0 ? new Dictionary<string, object> { { "n", mr.N }, { "right", mr.Right }, { "brier", Math.Round(mr.Brier, 3) }, { "brierMkt", Math.Round(mr.BrierMkt, 3) } } : null },
                    { "recent", gs.Skip(Math.Max(0, gs.Count - 5)).Reverse().Select(g => (object)new Dictionary<string, object> { { "date", g.Date }, { "where", g.Where }, { "opp", g.Opp }, { "pts", g.Pts }, { "oppPts", g.OppPts },
                        { "spread", double.IsNaN(g.Spread) ? null : (object)g.Spread } }).ToList() } });
            }
            return new Dictionary<string, object> { { "teams", teams }, { "tonight", Program.SkippedBanned.ToList() } };
        }
    }
}
