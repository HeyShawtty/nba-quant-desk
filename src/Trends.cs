using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // Season trends from the 2026-27 data bank (information, not predictions): league and team trajectories, player form,
    // how teammates and teams changed when a regular missed games, records that are still perfect, how often perfect
    // records held up in the next game, how much of a hot or cold stretch carried over, and referee tendencies.
    static class Trends
    {
        public class TG
        {
            public string Date, Id, Team, Opp; public bool Home, B2B; public int Pts, OppPts, Margin, Total;
            public double Spread = double.NaN, DkTotal = double.NaN;   // DraftKings spread from this team's side (negative = favored)
            public int[] Q = new int[4], OppQ = new int[4]; public GameData G;
            public bool Won { get { return Margin > 0; } }
            public double Cover { get { return double.IsNaN(Spread) ? double.NaN : Margin + Spread; } }
        }
        public static Dictionary<string, List<TG>> ByTeam = new Dictionary<string, List<TG>>();

        static Dictionary<string, object> O(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }
        static double R1(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 1); }
        static double R2(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 2); }
        static double R3(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 3); }
        static double Sd(IEnumerable<double> xs) { var l = xs.ToList(); if (l.Count < 2) return double.NaN; double m = l.Average(); return Math.Sqrt(l.Sum(x => (x - m) * (x - m)) / (l.Count - 1)); }
        static string Rec(int w, int l) { return w + "-" + l; }

        public static void Index()
        {
            ByTeam = new Dictionary<string, List<TG>>();
            var teamDates = new HashSet<string>(Bank.Games.SelectMany(g => new[] { g.Home + "|" + g.Date, g.Away + "|" + g.Date }));
            foreach (var g in Bank.Games)
                foreach (bool home in new[] { true, false })
                {
                    string team = home ? g.Home : g.Away;
                    List<TG> l; if (!ByTeam.TryGetValue(team, out l)) ByTeam[team] = l = new List<TG>();
                    string prev = DateTime.ParseExact(g.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1).ToString("yyyy-MM-dd");
                    var t = new TG { Date = g.Date, Id = g.Id, Team = team, Opp = home ? g.Away : g.Home, Home = home, B2B = teamDates.Contains(team + "|" + prev), Pts = home ? g.HomePts : g.AwayPts, OppPts = home ? g.AwayPts : g.HomePts, G = g };
                    t.Margin = t.Pts - t.OppPts; t.Total = t.Pts + t.OppPts;
                    if (!double.IsNaN(g.DkSprH)) t.Spread = home ? g.DkSprH : -g.DkSprH;
                    t.DkTotal = g.DkTotal; t.Q = home ? g.HomeQ : g.AwayQ; t.OppQ = home ? g.AwayQ : g.HomeQ;
                    l.Add(t);
                }
        }

        // current run of one outcome at the end of a list: +n when the last n are true, -n when the last n are false
        static int Run(IEnumerable<bool> xs)
        {
            var l = xs.ToList(); if (l.Count == 0) return 0;
            bool last = l[l.Count - 1]; int n = 0;
            for (int i = l.Count - 1; i >= 0 && l[i] == last; i--) n++;
            return last ? n : -n;
        }

        static double MarginSd()
        {
            double sd = Sd(ByTeam.Values.SelectMany(l => l).Select(t => (double)t.Margin));
            return double.IsNaN(sd) || sd < 8 ? 14 : sd;
        }

        // ------------------------------------------------------------------ league
        static Dictionary<string, object> League()
        {
            var gs = Bank.Games; int n = gs.Count;
            var lined = gs.Where(g => !double.IsNaN(g.DkSprH) && g.DkSprH != 0).ToList();
            var tot = gs.Where(g => !double.IsNaN(g.DkTotal)).ToList();
            var b2b = ByTeam.Values.SelectMany(l => l).Where(t => t.B2B).ToList();
            var weeks = gs.GroupBy(g => (int)(DateTime.ParseExact(g.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) - Program.S.SeasonStart).TotalDays / 7).OrderBy(w => w.Key)
                .Select(w =>
                {
                    var wt = w.Where(g => !double.IsNaN(g.DkTotal)).ToList();
                    return (object)O("week", w.Key + 1, "games", w.Count(), "total", R1(w.Average(g => (double)(g.HomePts + g.AwayPts))), "dk", wt.Count > 0 ? R1(wt.Average(g => g.DkTotal)) : 0,
                        "homeWin", R3(w.Count(g => g.HomePts > g.AwayPts) / (double)w.Count()), "blowouts", w.Count(g => Math.Abs(g.HomePts - g.AwayPts) >= 20));
                }).ToList();
            int comebacks = gs.Count(g => g.HomePts > g.AwayPts ? g.AwayLead >= 15 : g.HomeLead >= 15);
            return O("games", n, "homeW", gs.Count(g => g.HomePts > g.AwayPts), "favN", lined.Count, "favW", lined.Count(g => (g.DkSprH < 0) == (g.HomePts > g.AwayPts)),
                "favCover", lined.Count(g => { double m = g.HomePts - g.AwayPts + g.DkSprH; return g.DkSprH < 0 ? m > 0 : m < 0; }),
                "overs", tot.Count(g => g.HomePts + g.AwayPts > g.DkTotal), "unders", tot.Count(g => g.HomePts + g.AwayPts < g.DkTotal),
                "avgTotal", R1(gs.Average(g => (double)(g.HomePts + g.AwayPts))), "avgDkTotal", tot.Count > 0 ? R1(tot.Average(g => g.DkTotal)) : 0,
                "blowouts", gs.Count(g => Math.Abs(g.HomePts - g.AwayPts) >= 20), "close", gs.Count(g => Math.Abs(g.HomePts - g.AwayPts) <= 5), "ot", gs.Count(g => g.OT > 0),
                "comebacks", comebacks, "b2bW", b2b.Count(t => t.Won), "b2bL", b2b.Count(t => !t.Won), "weeks", weeks);
        }

        // ------------------------------------------------------------------ teams
        public class MlRec { public string Fav, Dog; public double P; public bool Right; }
        static List<MlRec> ModelMl()
        {
            var res = new List<MlRec>();
            try
            {
                foreach (var grp in Ledger.LoadAll().Where(p => p.Kind == "ML" && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P)).GroupBy(p => p.Date + "|" + p.GameId))
                {
                    var l = grp.OrderByDescending(x => x.P).ToList(); if (l.Count < 2) continue;
                    res.Add(new MlRec { Fav = l[0].Team, Dog = l[1].Team, P = l[0].P, Right = l[0].Y >= 1 });
                }
            }
            catch { }
            return res;
        }

        static List<Dictionary<string, object>> Teams(List<MlRec> ml, double sdMargin)
        {
            var res = new List<Dictionary<string, object>>(); var banned = new HashSet<string>(Ban.List().Select(b => b.Team));
            foreach (var team in ByTeam.Keys.OrderBy(t => t))
            {
                var l = ByTeam[team]; int n = l.Count; if (n == 0) continue;
                var l5 = l.Skip(Math.Max(0, n - 5)).ToList(); var early = l.Take(Math.Max(0, n - 5)).ToList();
                double mAll = l.Average(x => (double)x.Margin), m5 = l5.Average(x => (double)x.Margin), mE = early.Count > 0 ? early.Average(x => (double)x.Margin) : double.NaN;
                double z = early.Count >= 3 ? (m5 - mE) / (sdMargin * Math.Sqrt(1.0 / l5.Count + 1.0 / early.Count)) : 0;
                string traj = z >= 1.3 ? "rising" : z <= -1.3 ? "falling" : "steady";
                Func<Func<TG, bool>, string> rec = f => { var s = l.Where(f).ToList(); return Rec(s.Count(x => x.Won), s.Count(x => !x.Won)); };
                var ats = l.Where(x => !double.IsNaN(x.Cover) && x.Cover != 0).ToList(); var ou = l.Where(x => !double.IsNaN(x.DkTotal) && x.Total != x.DkTotal).ToList();
                var ats5 = ats.Skip(Math.Max(0, ats.Count - 5)).ToList();
                var mine = ml.Where(m => m.Fav == team || m.Dog == team).ToList(); var asFav = ml.Where(m => m.Fav == team).ToList();
                var notes = new List<string>();
                int st = Run(l.Select(x => x.Won));
                if (Math.Abs(st) >= 4) notes.Add((st > 0 ? "won " : "lost ") + Math.Abs(st) + " straight");
                int ast = Run(ats.Select(x => x.Cover > 0)); if (Math.Abs(ast) >= 4) notes.Add((ast > 0 ? "covered " : "failed to cover ") + Math.Abs(ast) + " straight");
                int ost = Run(ou.Select(x => x.Total > x.DkTotal)); if (Math.Abs(ost) >= 4) notes.Add((ost > 0 ? "over " : "under ") + Math.Abs(ost) + " straight");
                if (traj != "steady") notes.Add(string.Format(U.IC, "last 5 margin {0:+0.0;-0.0} vs {1:+0.0;-0.0} before", m5, mE));
                double q4 = l.Average(x => (double)(x.Q[3] - x.OppQ[3])), q1 = l.Average(x => (double)(x.Q[0] - x.OppQ[0]));
                if (n >= 6 && Math.Abs(q4) >= 3.5) notes.Add(string.Format(U.IC, "4th quarters {0:+0.0;-0.0} per game", q4));
                if (n >= 6 && Math.Abs(q1) >= 3.5) notes.Add(string.Format(U.IC, "1st quarters {0:+0.0;-0.0} per game", q1));
                res.Add(O("team", team, "banned", banned.Contains(team), "gp", n, "w", l.Count(x => x.Won), "l", l.Count(x => !x.Won), "streak", st,
                    "l5", Rec(l5.Count(x => x.Won), l5.Count(x => !x.Won)), "margin", R1(mAll), "l5margin", R1(m5), "before", R1(mE), "z", R2(z), "traj", traj,
                    "ppg", R1(l.Average(x => (double)x.Pts)), "l5ppg", R1(l5.Average(x => (double)x.Pts)), "opp", R1(l.Average(x => (double)x.OppPts)), "l5opp", R1(l5.Average(x => (double)x.OppPts)),
                    "ats", Rec(ats.Count(x => x.Cover > 0), ats.Count(x => x.Cover < 0)), "l5ats", Rec(ats5.Count(x => x.Cover > 0), ats5.Count(x => x.Cover < 0)),
                    "ou", Rec(ou.Count(x => x.Total > x.DkTotal), ou.Count(x => x.Total < x.DkTotal)),
                    "home", rec(x => x.Home), "away", rec(x => !x.Home), "fav", rec(x => x.Spread < 0), "dog", rec(x => x.Spread > 0), "b2b", rec(x => x.B2B), "close", rec(x => Math.Abs(x.Margin) <= 5),
                    "q1", R1(q1), "q4", R1(q4), "half1", R1(l.Average(x => (double)(x.Q[0] + x.Q[1] - x.OppQ[0] - x.OppQ[1]))), "half2", R1(l.Average(x => (double)(x.Q[2] + x.Q[3] - x.OppQ[2] - x.OppQ[3]))),
                    "margins", l.Select(x => x.Margin).ToList(), "modelN", mine.Count, "modelRight", mine.Count(m => m.Right), "favN", asFav.Count, "favRight", asFav.Count(m => m.Right), "notes", notes));
            }
            return res;
        }

        // ------------------------------------------------------------------ players
        static double Val(BoxLine b, string stat) { return stat == "PTS" ? b.Pts : stat == "REB" ? b.Reb : stat == "AST" ? b.Ast : stat == "MIN" ? b.Min : b.Tpm; }

        static List<Dictionary<string, object>> Players()
        {
            var res = new List<Dictionary<string, object>>();
            foreach (var p in Bank.Players.Values.Where(x => x.Gp >= 5 && x.Mpg >= 18).OrderBy(x => x.Team).ThenByDescending(x => x.Mpg))
            {
                var lg = p.Log; int n = lg.Count; if (!ByTeam.ContainsKey(p.Team)) continue;
                var tl = ByTeam[p.Team];
                var teamLast3 = new HashSet<string>(tl.Skip(Math.Max(0, tl.Count - 3)).Select(t => t.Id));
                bool active = lg.Any(x => teamLast3.Contains(x.GameId));
                var l5 = lg.Skip(Math.Max(0, n - 5)).ToList(); var early = lg.Take(Math.Max(0, n - 5)).ToList();
                Func<List<Bank.PGame>, string, double> avg = (l, s) => l.Count == 0 ? double.NaN : l.Average(x => Val(x.B, s));
                double dPts = avg(l5, "PTS") - avg(early, "PTS"), dMin = avg(l5, "MIN") - avg(early, "MIN");
                double sd = double.IsNaN(p.PtsSd) ? 6 : Math.Max(3, p.PtsSd);
                double z = early.Count >= 3 ? dPts / (sd * Math.Sqrt(1.0 / l5.Count + 1.0 / early.Count)) : 0;
                string traj = z >= 1.5 && dPts >= 3 ? "rising" : z <= -1.5 && dPts <= -3 ? "falling" : "steady";
                string role = early.Count >= 3 && dMin >= 4 ? "minutes up" : early.Count >= 3 && dMin <= -4 ? "minutes down" : "";
                var notes = new List<string>();
                Action<string, int, int, string> streak = (s, line, need, label) => { int r = Run(lg.Select(x => Val(x.B, s) >= line)); if (r >= need) notes.Add(label + " in " + r + " straight"); };
                streak("PTS", 30, 3, "30+ pts"); if (notes.Count == 0) streak("PTS", 20, 4, "20+ pts");
                streak("3PM", 3, 4, "3+ threes"); streak("REB", 10, 4, "10+ reb"); streak("AST", 8, 4, "8+ ast");
                int dd = Run(lg.Select(x => (x.B.Pts >= 10 ? 1 : 0) + (x.B.Reb >= 10 ? 1 : 0) + (x.B.Ast >= 10 ? 1 : 0) >= 2)); if (dd >= 3) notes.Add("double-double in " + dd + " straight");
                res.Add(O("name", p.Name, "team", p.Team, "pid", p.Pid, "gp", n, "active", active, "min", R1(p.Mpg), "l5min", R1(avg(l5, "MIN")), "pts", R1(p.Ppg), "l5pts", R1(avg(l5, "PTS")),
                    "reb", R1(p.Rpg), "l5reb", R1(avg(l5, "REB")), "ast", R1(p.Apg), "l5ast", R1(avg(l5, "AST")), "tpm", R1(p.Tpm), "l5tpm", R1(avg(l5, "3PM")),
                    "dPts", early.Count >= 3 ? R1(dPts) : 0, "dMin", early.Count >= 3 ? R1(dMin) : 0, "z", R2(z), "traj", traj, "role", role,
                    "ptsLog", lg.Select(x => x.B.Pts).ToList(), "minLog", lg.Select(x => (int)Math.Round(x.B.Min)).ToList(), "notes", notes,
                    "bias", p.ModelN > 0 ? (object)R1(p.ModelPtsBias) : null, "biasN", p.ModelN));
            }
            return res;
        }

        // How much of a recent stretch carried into the next game, this season: next game = season average so far
        // + b x (last-5 average - season average so far). b near 0 means the stretch was noise; near 1 means it was the new normal.
        static List<Dictionary<string, object>> CarryOver()
        {
            var res = new List<Dictionary<string, object>>();
            foreach (var st in new[] { "PTS", "MIN", "REB", "AST", "3PM" })
            {
                double sxy = 0, sxx = 0; int n = 0;
                foreach (var p in Bank.Players.Values.Where(x => x.Gp >= 9 && x.Mpg >= 15))
                    for (int i = 8; i < p.Log.Count; i++)
                    {
                        double s = 0, r5 = 0;
                        for (int j = 0; j < i; j++) { double v = Val(p.Log[j].B, st); s += v; if (j >= i - 5) r5 += v; }
                        double x = r5 / 5 - s / i, y = Val(p.Log[i].B, st) - s / i;
                        sxy += x * y; sxx += x * x; n++;
                    }
                res.Add(O("stat", st, "n", n, "b", sxx > 0 ? R2(sxy / sxx) : 0));
            }
            double txy = 0, txx = 0; int tn = 0;
            foreach (var l in ByTeam.Values)
                for (int i = 8; i < l.Count; i++)
                {
                    double s = 0, r5 = 0; for (int j = 0; j < i; j++) { s += l[j].Margin; if (j >= i - 5) r5 += l[j].Margin; }
                    double x = r5 / 5 - s / i, y = l[i].Margin - s / i; txy += x * y; txx += x * x; tn++;
                }
            res.Add(O("stat", "TEAM MARGIN", "n", tn, "b", txx > 0 ? R2(txy / txx) : 0));
            return res;
        }

        // ------------------------------------------------------------------ absences: with / without a regular
        static List<Dictionary<string, object>> Absences(HashSet<string> outTonight)
        {
            var res = new List<Tuple<double, Dictionary<string, object>>>();
            foreach (var team in ByTeam.Keys.OrderBy(t => t))
            {
                var tg = ByTeam[team]; if (tg.Count < 5) continue;
                var roster = Bank.Players.Values.Where(p => p.Log.Any(x => x.Team == team)).ToList();
                foreach (var x in roster)
                {
                    var mine = x.Log.Where(g => g.Team == team).ToList(); if (mine.Count < 3 || mine.Average(g => g.B.Min) < 22) continue;
                    bool stillHere = x.Log[x.Log.Count - 1].Team == team;
                    string first = mine[0].Date, lastDate = stillHere ? tg[tg.Count - 1].Date : mine[mine.Count - 1].Date;
                    var played = new HashSet<string>(mine.Select(g => g.GameId));
                    var window = tg.Where(g => string.CompareOrdinal(g.Date, first) >= 0 && string.CompareOrdinal(g.Date, lastDate) <= 0).ToList();
                    var with = window.Where(g => played.Contains(g.Id)).ToList(); var wo = window.Where(g => !played.Contains(g.Id)).ToList();
                    if (wo.Count < 2 || with.Count < 3) continue;
                    var withIds = new HashSet<string>(with.Select(g => g.Id)); var woIds = new HashSet<string>(wo.Select(g => g.Id));
                    int outRun = stillHere ? Math.Max(0, -Run(window.Select(g => played.Contains(g.Id)))) : 0;   // games missed in a row right now
                    var mates = new List<Tuple<double, Dictionary<string, object>>>();
                    foreach (var y in roster)
                    {
                        if (y == x) continue;
                        var a = y.Log.Where(g => g.Team == team && withIds.Contains(g.GameId)).ToList(); var b = y.Log.Where(g => g.Team == team && woIds.Contains(g.GameId)).ToList();
                        if (a.Count < 2 || b.Count < 2 || a.Concat(b).Average(g => g.B.Min) < 14) continue;
                        Func<List<Bank.PGame>, Func<BoxLine, double>, double> av = (l, f) => l.Average(g => f(g.B));
                        double pa = av(a, q => q.Pts), pb = av(b, q => q.Pts);
                        double pooled = Math.Sqrt((a.Sum(g => (g.B.Pts - pa) * (g.B.Pts - pa)) + b.Sum(g => (g.B.Pts - pb) * (g.B.Pts - pb))) / Math.Max(1, a.Count + b.Count - 2));
                        double sd = Math.Max(Math.Max(pooled, double.IsNaN(y.PtsSd) ? 0 : y.PtsSd), 2.5);
                        double t = (pb - pa) / (sd * Math.Sqrt(1.0 / a.Count + 1.0 / b.Count)), d = pb - pa;
                        string verdict = a.Count >= 3 && b.Count >= 3 && Math.Abs(t) >= 2 && Math.Abs(d) >= 3 ? "worth noticing" : Math.Abs(t) >= 1.3 && Math.Abs(d) >= 2.5 ? "leaning" : "noise so far";
                        Func<BoxLine, double> shots = q => q.Fga + 0.44 * q.Fta;
                        mates.Add(Tuple.Create(Math.Abs(t), O("name", y.Name, "withN", a.Count, "woN", b.Count, "ptsWith", R1(pa), "ptsWo", R1(pb), "dPts", R1(d),
                            "minWith", R1(av(a, q => q.Min)), "minWo", R1(av(b, q => q.Min)), "astWith", R1(av(a, q => q.Ast)), "astWo", R1(av(b, q => q.Ast)),
                            "rebWith", R1(av(a, q => q.Reb)), "rebWo", R1(av(b, q => q.Reb)), "tpmWith", R1(av(a, q => q.Tpm)), "tpmWo", R1(av(b, q => q.Tpm)),
                            "shotsWith", R1(av(a, shots)), "shotsWo", R1(av(b, shots)), "t", R2(t), "verdict", verdict)));
                    }
                    if (mates.Count == 0) continue;
                    double mW = with.Average(g => (double)g.Margin), mO = wo.Average(g => (double)g.Margin);
                    double tt = (mO - mW) / (13.0 * Math.Sqrt(1.0 / with.Count + 1.0 / wo.Count));
                    string tv = with.Count >= 3 && wo.Count >= 3 && Math.Abs(tt) >= 2 ? "worth noticing" : Math.Abs(tt) >= 1.3 ? "leaning" : "noise so far";
                    bool tonight = outTonight != null && outTonight.Contains(x.Pid);
                    double rank = (tonight ? 1000 : 0) + (outRun > 0 ? 100 : 0) + wo.Count + mates.Max(m => m.Item1);
                    res.Add(Tuple.Create(rank, O("team", team, "player", x.Name, "pid", x.Pid, "min", R1(mine.Average(g => g.B.Min)), "pts", R1(mine.Average(g => (double)g.B.Pts)),
                        "played", with.Count, "missed", wo.Count, "outRun", outRun, "outTonight", tonight, "missedDates", wo.Select(g => g.Date.Substring(5)).ToList(),
                        "teamWith", O("rec", Rec(with.Count(g => g.Won), with.Count(g => !g.Won)), "ppg", R1(with.Average(g => (double)g.Pts)), "opp", R1(with.Average(g => (double)g.OppPts)), "margin", R1(mW)),
                        "teamWo", O("rec", Rec(wo.Count(g => g.Won), wo.Count(g => !g.Won)), "ppg", R1(wo.Average(g => (double)g.Pts)), "opp", R1(wo.Average(g => (double)g.OppPts)), "margin", R1(mO)),
                        "teamT", R2(tt), "teamVerdict", tv, "mates", mates.OrderByDescending(m => m.Item1).Take(6).Select(m => m.Item2).ToList())));
                }
            }
            return res.OrderByDescending(r => r.Item1).Take(60).Select(r => r.Item2).ToList();
        }

        // ------------------------------------------------------------------ perfect records
        static readonly Dictionary<string, int[]> Rungs = new Dictionary<string, int[]>
        {
            { "PTS", new[] { 10, 15, 20, 25, 30, 35 } }, { "REB", new[] { 4, 6, 8, 10, 12 } }, { "AST", new[] { 4, 6, 8, 10 } }, { "3PM", new[] { 1, 2, 3, 4 } }
        };
        static string Unit(string s) { return s == "PTS" ? "pts" : s == "REB" ? "reb" : s == "AST" ? "ast" : "threes"; }
        // highest rung reached in every one of the games (or -1)
        static int PerfectRung(IEnumerable<BoxLine> games, string stat)
        {
            double min = games.Min(b => Val(b, stat)); int best = -1;
            foreach (int r in Rungs[stat]) if (min >= r) best = r;
            return best;
        }

        static Dictionary<string, object> Perfect(List<MlRec> ml, Dictionary<string, double> tonightP)
        {
            // players: a line reached in every game this season (8+ games, still playing)
            var pl = new List<Tuple<double, Dictionary<string, object>>>();
            foreach (var p in Bank.Players.Values.Where(x => x.Gp >= 8 && ByTeam.ContainsKey(x.Team)))
            {
                var tl = ByTeam[p.Team];
                var teamLast3 = new HashSet<string>(tl.Skip(Math.Max(0, tl.Count - 3)).Select(t => t.Id));
                if (!p.Log.Any(x => teamLast3.Contains(x.GameId))) continue;
                foreach (var st in Rungs.Keys)
                {
                    int r = PerfectRung(p.Log.Select(x => x.B), st); if (r < 0) continue;
                    double avg = p.Log.Average(x => Val(x.B, st)), low = p.Log.Min(x => Val(x.B, st));
                    double tp; bool has = tonightP.TryGetValue(p.Pid + "|" + st + "|" + r, out tp);
                    pl.Add(Tuple.Create(p.Gp + 3.0 * Array.IndexOf(Rungs[st], r), O("name", p.Name, "team", p.Team, "stat", st, "line", r, "unit", Unit(st), "n", p.Gp, "avg", R1(avg), "low", low, "tonight", has ? (object)R3(tp) : null)));
                }
            }
            // how perfect lines did in the very next game, all season (walk-forward: only the games before it are looked at)
            var chk = new Dictionary<string, int[]>();
            foreach (var p in Bank.Players.Values.Where(x => x.Gp >= 9))
                for (int i = 8; i < p.Log.Count; i++)
                    foreach (var st in Rungs.Keys)
                    {
                        int r = PerfectRung(p.Log.Take(i).Select(x => x.B), st); if (r < 0) continue;
                        int[] c; if (!chk.TryGetValue(st, out c)) chk[st] = c = new int[2];
                        c[0]++; if (Val(p.Log[i].B, st) >= r) c[1]++;
                    }
            var check = chk.OrderBy(kv => kv.Key).Select(kv => O("stat", kv.Key, "n", kv.Value[0], "hit", kv.Value[1])).ToList();

            // teams: situations with a perfect or winless record and long runs
            var tm = new List<Dictionary<string, object>>();
            foreach (var team in ByTeam.Keys.OrderBy(t => t))
            {
                var l = ByTeam[team]; string tname = team;
                Action<string, Func<TG, bool>, int> split = (label, f, need) =>
                {
                    var s = l.Where(f).ToList(); if (s.Count < need) return;
                    if (s.All(x => x.Won)) tm.Add(O("team", tname, "what", s.Count + "-0 " + label, "n", s.Count, "good", true));
                    else if (s.All(x => !x.Won)) tm.Add(O("team", tname, "what", "0-" + s.Count + " " + label, "n", s.Count, "good", false));
                };
                split("at home", x => x.Home, 4); split("on the road", x => !x.Home, 4); split("as the DraftKings favorite", x => x.Spread < 0, 4); split("as the DraftKings underdog", x => x.Spread > 0, 4);
                split("on a back-to-back", x => x.B2B, 3); split("in games decided by 5 or fewer", x => Math.Abs(x.Margin) <= 5, 4);
                var ats = l.Where(x => !double.IsNaN(x.Cover) && x.Cover != 0).ToList(); var ou = l.Where(x => !double.IsNaN(x.DkTotal) && x.Total != x.DkTotal).ToList();
                int st = Run(l.Select(x => x.Won)); if (Math.Abs(st) >= 5) tm.Add(O("team", team, "what", (st > 0 ? "won " : "lost ") + Math.Abs(st) + " straight", "n", Math.Abs(st), "good", st > 0));
                int a = Run(ats.Select(x => x.Cover > 0)); if (Math.Abs(a) >= 5) tm.Add(O("team", team, "what", (a > 0 ? "covered the spread in " : "failed to cover in ") + Math.Abs(a) + " straight", "n", Math.Abs(a), "good", a > 0));
                int o = Run(ou.Select(x => x.Total > x.DkTotal)); if (Math.Abs(o) >= 5) tm.Add(O("team", team, "what", (o > 0 ? "over" : "under") + " the DraftKings total in " + Math.Abs(o) + " straight", "n", Math.Abs(o), "good", o > 0));
                var hOu = ou.Where(x => x.Home).ToList(); if (hOu.Count >= 4 && (hOu.All(x => x.Total > x.DkTotal) || hOu.All(x => x.Total < x.DkTotal))) tm.Add(O("team", team, "what", (hOu[0].Total > hOu[0].DkTotal ? "over" : "under") + " in all " + hOu.Count + " home games", "n", hOu.Count, "good", true));
                var aOu = ou.Where(x => !x.Home).ToList(); if (aOu.Count >= 4 && (aOu.All(x => x.Total > x.DkTotal) || aOu.All(x => x.Total < x.DkTotal))) tm.Add(O("team", team, "what", (aOu[0].Total > aOu[0].DkTotal ? "over" : "under") + " in all " + aOu.Count + " road games", "n", aOu.Count, "good", true));
            }
            // the model's own perfect and winless records by team (live graded slates)
            var mo = new List<Dictionary<string, object>>();
            foreach (var team in ByTeam.Keys.OrderBy(t => t))
            {
                var fav = ml.Where(m => m.Fav == team).ToList(); var any = ml.Where(m => m.Fav == team || m.Dog == team).ToList();
                if (fav.Count >= 4 && fav.All(m => m.Right)) mo.Add(O("team", team, "what", "won all " + fav.Count + " times the model picked them", "n", fav.Count, "good", true));
                if (fav.Count >= 3 && fav.All(m => !m.Right)) mo.Add(O("team", team, "what", "lost all " + fav.Count + " times the model picked them", "n", fav.Count, "good", false));
                if (any.Count >= 5 && any.All(m => m.Right)) mo.Add(O("team", team, "what", "the model called the winner in all " + any.Count + " of their games", "n", any.Count, "good", true));
                if (any.Count >= 4 && any.All(m => !m.Right)) mo.Add(O("team", team, "what", "the model missed the winner in all " + any.Count + " of their games", "n", any.Count, "good", false));
            }
            foreach (var band in new[] { Tuple.Create(0.9, 1.01), Tuple.Create(0.8, 0.9), Tuple.Create(0.7, 0.8) })
            {
                var b = ml.Where(m => m.P >= band.Item1 && m.P < band.Item2).ToList();
                if (b.Count >= 5 && b.All(m => m.Right)) mo.Add(O("team", "", "what", string.Format(U.IC, "model favorites at {0:0}% to {1:0}% won all {2}", 100 * band.Item1, Math.Min(100, 100 * band.Item2), b.Count), "n", b.Count, "good", true));
            }
            return O("players", pl.OrderByDescending(x => x.Item1).Take(80).Select(x => x.Item2).ToList(), "check", check, "teams", tm, "model", mo);
        }

        // ------------------------------------------------------------------ referees
        static double Fouls(GameData g) { return g.Box.Where(b => b.Played).Sum(b => b.Pf); }
        static double Fta(GameData g) { return g.Box.Where(b => b.Played).Sum(b => b.Fta); }
        static List<Dictionary<string, object>> Refs()
        {
            var rows = new Dictionary<string, List<GameData>>();
            foreach (var g in Bank.Games) foreach (var o in g.Officials) { List<GameData> l; if (!rows.TryGetValue(o, out l)) rows[o] = l = new List<GameData>(); l.Add(g); }
            return rows.Where(kv => kv.Value.Count >= 5).OrderByDescending(kv => kv.Value.Average(g => Fouls(g))).Select(kv =>
            {
                var l = kv.Value; var tot = l.Where(g => !double.IsNaN(g.DkTotal)).ToList();
                return O("name", kv.Key, "games", l.Count, "fouls", R1(l.Average(g => Fouls(g))), "fta", R1(l.Average(g => Fta(g))), "total", R1(l.Average(g => (double)(g.HomePts + g.AwayPts))),
                    "overs", tot.Count(g => g.HomePts + g.AwayPts > g.DkTotal), "unders", tot.Count(g => g.HomePts + g.AwayPts < g.DkTotal), "homeW", l.Count(g => g.HomePts > g.AwayPts), "homeL", l.Count(g => g.HomePts < g.AwayPts),
                    "techs", l.Sum(g => g.HomeTech + g.AwayTech), "ejections", l.Sum(g => g.Events.Count(e => e.Type == "ejection")));
            }).ToList();
        }

        // everything, for the website. outTonight: player ids ruled out on tonight's slate; tonightP: "pid|STAT|line" -> model chance tonight
        public static object Json(HashSet<string> outTonight, Dictionary<string, double> tonightP)
        {
            if (Bank.Games.Count == 0) return null;
            Index();
            var ml = ModelMl();
            var withRefs = Bank.Games.Where(g => g.Officials.Count > 0).ToList();
            return O("through", Bank.BuiltThrough, "games", Bank.Games.Count, "league", League(), "teams", Teams(ml, MarginSd()), "players", Players(), "carry", CarryOver(),
                "absences", Absences(outTonight), "perfect", Perfect(ml, tonightP ?? new Dictionary<string, double>()), "refs", Refs(),
                "refAvg", withRefs.Count > 0 ? O("fouls", R1(withRefs.Average(g => Fouls(g))), "fta", R1(withRefs.Average(g => Fta(g)))) : null);
        }

        // nbaquant-cli trends : the same study as text (also saved as ledger\trends.txt)
        public static string Text()
        {
            Bank.Build(DateTime.Now.Date);
            if (Bank.Games.Count == 0) return "No completed games in the data bank yet.";
            Index();
            var ml = ModelMl(); var sb = new StringBuilder();
            var lg = League();
            sb.AppendLine(string.Format(U.IC, "SEASON TRENDS through {0}: {1} games. Home teams {2}-{3}. DraftKings favorites won {4} of {5} and covered {6}. Overs {7}, unders {8} (average total {9} vs DraftKings {10}). {11} games decided by 20+, {12} by 5 or fewer, {13} comebacks from 15+ down. Back-to-backs {14}-{15}.",
                Bank.BuiltThrough, lg["games"], lg["homeW"], (int)lg["games"] - (int)lg["homeW"], lg["favW"], lg["favN"], lg["favCover"], lg["overs"], lg["unders"], lg["avgTotal"], lg["avgDkTotal"], lg["blowouts"], lg["close"], lg["comebacks"], lg["b2bW"], lg["b2bL"]));
            sb.AppendLine();
            sb.AppendLine("TEAMS (record | last 5 | margin season / last 5 / before | ATS | O-U | trajectory | notes)");
            foreach (var t in Teams(ml, MarginSd()))
                sb.AppendLine(string.Format(U.IC, "  {0,-5} {1}-{2} | L5 {3} | {4:+0.0;-0.0} / {5:+0.0;-0.0} / {6:+0.0;-0.0} | ATS {7} | O-U {8} | {9}{10}{11}", t["team"], t["w"], t["l"], t["l5"], t["margin"], t["l5margin"], t["before"], t["ats"], t["ou"], t["traj"],
                    (bool)t["banned"] ? " [banned]" : "", ((List<string>)t["notes"]).Count > 0 ? " | " + string.Join("; ", (List<string>)t["notes"]) : ""));
            sb.AppendLine();
            sb.AppendLine("PLAYERS MOVING (last 5 vs the games before; z = how far outside his usual game-to-game swing)");
            foreach (var p in Players().Where(p => (string)p["traj"] != "steady" || (string)p["role"] != "").OrderByDescending(p => Math.Abs((double)p["z"])).Take(50))
                sb.AppendLine(string.Format(U.IC, "  {0,-24} {1,-5} {2,-8} pts {3} -> {4} (z {5:+0.0;-0.0}), min {6} -> {7} {8}{9}", p["name"], p["team"], p["traj"], R1((double)p["l5pts"] - (double)p["dPts"]), p["l5pts"], p["z"], R1((double)p["l5min"] - (double)p["dMin"]), p["l5min"], p["role"],
                    ((List<string>)p["notes"]).Count > 0 ? " | " + string.Join("; ", (List<string>)p["notes"]) : ""));
            sb.AppendLine();
            sb.AppendLine("CARRY-OVER (share of a last-5 swing that showed up in the next game; 0 = noise, 1 = new normal)");
            foreach (var c in CarryOver()) sb.AppendLine(string.Format(U.IC, "  {0,-12} {1:0.00}  (n={2})", c["stat"], c["b"], c["n"]));
            sb.AppendLine();
            sb.AppendLine("ABSENCES: with vs without a regular (t = size of the change against normal game-to-game swing)");
            foreach (var a in Absences(null).Take(40))
            {
                var tw = (Dictionary<string, object>)a["teamWith"]; var to = (Dictionary<string, object>)a["teamWo"];
                sb.AppendLine(string.Format(U.IC, "  {0} {1} ({2} min, {3} pts): played {4}, missed {5}{6}. Team {7} ({8:+0.0;-0.0}) with, {9} ({10:+0.0;-0.0}) without: {11}.", a["team"], a["player"], a["min"], a["pts"], a["played"], a["missed"],
                    (int)a["outRun"] > 0 ? ", out the last " + a["outRun"] : "", tw["rec"], tw["margin"], to["rec"], to["margin"], a["teamVerdict"]));
                foreach (var m in (List<Dictionary<string, object>>)a["mates"])
                    sb.AppendLine(string.Format(U.IC, "      {0,-24} pts {1} -> {2} ({3:+0.0;-0.0}), min {4} -> {5}, shots {6} -> {7}, ast {8} -> {9} | games {10}/{11} | t {12:+0.0;-0.0} | {13}", m["name"], m["ptsWith"], m["ptsWo"], m["dPts"], m["minWith"], m["minWo"], m["shotsWith"], m["shotsWo"], m["astWith"], m["astWo"], m["withN"], m["woN"], m["t"], m["verdict"]));
            }
            var pf = Perfect(ml, new Dictionary<string, double>());
            sb.AppendLine();
            sb.AppendLine("PERFECT SO FAR: lines reached in every game (8+ games)");
            foreach (var p in (List<Dictionary<string, object>>)pf["players"]) sb.AppendLine(string.Format(U.IC, "  {0,-24} {1,-5} {2}+ {3} in all {4} (average {5}, lowest {6})", p["name"], p["team"], p["line"], p["unit"], p["n"], p["avg"], p["low"]));
            sb.AppendLine("How perfect lines did in the next game, all season: " + string.Join("; ", ((List<Dictionary<string, object>>)pf["check"]).Select(c => string.Format(U.IC, "{0} {1}/{2} ({3:0.0}%)", c["stat"], c["hit"], c["n"], 100.0 * (int)c["hit"] / Math.Max(1, (int)c["n"])))));
            sb.AppendLine();
            sb.AppendLine("PERFECT TEAM RECORDS");
            foreach (var t in (List<Dictionary<string, object>>)pf["teams"]) sb.AppendLine("  " + t["team"] + ": " + t["what"]);
            sb.AppendLine();
            sb.AppendLine("THE MODEL'S PERFECT AND WINLESS RECORDS (live slates)");
            foreach (var t in (List<Dictionary<string, object>>)pf["model"]) sb.AppendLine("  " + ((string)t["team"] != "" ? t["team"] + ": " : "") + t["what"]);
            sb.AppendLine();
            sb.AppendLine("REFEREES (5+ games, most fouls first)");
            foreach (var r in Refs()) sb.AppendLine(string.Format(U.IC, "  {0,-22} {1} games: {2} fouls, {3} FTA, total {4}, O-U {5}-{6}, home {7}-{8}, techs {9}, ejections {10}", r["name"], r["games"], r["fouls"], r["fta"], r["total"], r["overs"], r["unders"], r["homeW"], r["homeL"], r["techs"], r["ejections"]));
            string txt = sb.ToString();
            try { File.WriteAllText(Program.P(@"ledger\trends.txt"), txt, new UTF8Encoding(false)); } catch { }
            return txt;
        }
    }
}
