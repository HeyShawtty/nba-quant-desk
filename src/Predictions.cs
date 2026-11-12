using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // One probabilistic statement the model made before tip-off, in a form the grader can resolve from box scores.
    class Pred
    {
        public string Date, GameId, Game, Kind, Stat, Team, Pid, Player, Legs, Tag, Selection;
        public double Line, P, PMarket = double.NaN, Odds = double.NaN, Mean = double.NaN;
        // filled by the grader
        public double Y = double.NaN; public bool Void; public double Actual = double.NaN;

        public static readonly string Header = "date,game_id,game,kind,stat,team,pid,player,line,p,p_market,odds,mean,legs,tag,selection,y,void,actual";
        static string F(double v) { return double.IsNaN(v) ? "" : v.ToString("R", CultureInfo.InvariantCulture); }
        static string Q(string s) { s = s ?? ""; return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "'") + "\"" : s; }
        public string ToCsv()
        {
            return string.Join(",", new[] { Date, GameId, Game, Kind, Stat, Team, Pid, Q(Player), F(Line), F(P), F(PMarket), F(Odds), F(Mean), Q(Legs), Tag, Q(Selection), F(Y), Void ? "1" : "0", F(Actual) });
        }
        public static Pred FromCsv(string[] r)
        {
            return new Pred
            {
                Date = r[0], GameId = r[1], Game = r[2], Kind = r[3], Stat = r[4], Team = r[5], Pid = r[6], Player = r[7], Line = U.D(r[8], double.NaN), P = U.D(r[9], double.NaN),
                PMarket = U.D(r[10], double.NaN), Odds = U.D(r[11], double.NaN), Mean = U.D(r[12], double.NaN), Legs = r[13], Tag = r[14], Selection = r[15],
                Y = r.Length > 16 ? U.D(r[16], double.NaN) : double.NaN, Void = r.Length > 17 && r[17] == "1", Actual = r.Length > 18 ? U.D(r[18], double.NaN) : double.NaN
            };
        }
        public static List<Pred> Load(string path) { return U.ReadCsv(path).Skip(1).Select(FromCsv).ToList(); }
        public static void Save(string path, IEnumerable<Pred> preds)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[] { Header }.Concat(preds.Select(p => p.ToCsv())), new UTF8Encoding(false));
        }
    }

    static class Predictions
    {
        public static string LegCode(Leg l)
        {
            if (l.Ps != null) return l.Kind + "||" + l.Ps.P.Pid + "|" + l.Line.ToString(CultureInfo.InvariantCulture);
            return l.Kind + "|" + l.Team + "||" + l.LineD.ToString("R", CultureInfo.InvariantCulture);
        }

        public static List<Pred> Collect(string date)
        {
            var res = new List<Pred>();
            Func<GameCtx, string> gl = g => g.Def.Away + " @ " + g.Def.Home;
            var mlRows = Report.MlRows();
            int rank = 0;
            foreach (var r in mlRows)
            {
                rank++;
                res.Add(new Pred { Date = date, GameId = r.G.Def.EspnId, Game = gl(r.G), Kind = "ML", Team = r.Team, P = r.P, PMarket = r.PMkt, Odds = r.DkMl, Selection = r.Team + " ML", Tag = rank <= 3 ? "top3_ml" : "" });
            }
            foreach (var g in Program.Games)
            {
                var d = g.Def.DK;
                res.Add(new Pred { Date = date, GameId = d == null ? g.Def.EspnId : g.Def.EspnId, Game = gl(g), Kind = "SPREAD", Team = g.Def.Home, Line = d.SprH, P = g.PCoverH, Odds = d.SprHOdds, Selection = g.Def.Home + " " + d.SprH.ToString(CultureInfo.InvariantCulture) });
                res.Add(new Pred { Date = date, GameId = g.Def.EspnId, Game = gl(g), Kind = "SPREAD", Team = g.Def.Away, Line = -d.SprH, P = 1 - g.PCoverH, Odds = d.SprAOdds, Selection = g.Def.Away + " " + (-d.SprH).ToString(CultureInfo.InvariantCulture) });
                res.Add(new Pred { Date = date, GameId = g.Def.EspnId, Game = gl(g), Kind = "TOTAL", Stat = "OVER", Line = d.Total, P = g.POver, Odds = d.OverOdds, Selection = "Over " + d.Total.ToString(CultureInfo.InvariantCulture) });
                res.Add(new Pred { Date = date, GameId = g.Def.EspnId, Game = gl(g), Kind = "TOTAL", Stat = "UNDER", Line = d.Total, P = 1 - g.POver, Odds = d.UnderOdds, Selection = "Under " + d.Total.ToString(CultureInfo.InvariantCulture) });
                res.Add(new Pred { Date = date, GameId = g.Def.EspnId, Game = gl(g), Kind = "MEAN", Stat = "MARGIN", Team = g.Def.Home, Mean = g.MeanMargin, Selection = "home margin" });
                res.Add(new Pred { Date = date, GameId = g.Def.EspnId, Game = gl(g), Kind = "MEAN", Stat = "TOTAL", Mean = g.MeanTotal, Selection = "game total" });
            }
            var q3top = Analysis.TeamQs.OrderByDescending(t => t.P100).Take(3).ToList();
            foreach (var t in Analysis.TeamQs)
                foreach (var kv in new[] { Tuple.Create(90, t.P90), Tuple.Create(95, t.P95), Tuple.Create(100, t.P100), Tuple.Create(105, t.P105) })
                    res.Add(new Pred { Date = date, GameId = t.G.Def.EspnId, Game = gl(t.G), Kind = "TEAMQ3", Team = t.Team, Line = kv.Item1, P = kv.Item2, Selection = t.Team + " " + kv.Item1 + "+ thru Q3", Tag = kv.Item1 == 100 && q3top.Contains(t) ? "top3_q3" : "" });
            var top25 = Analysis.Players.OrderByDescending(p => p.Pts.PAtLeast(25)).Take(5).ToList();
            var topQ = Analysis.Players.Where(x => x.MeanMin >= 15).OrderByDescending(x => x.Q3All).Take(5).ToList();
            var topF = Analysis.Players.OrderByDescending(x => x.FirstFG).Take(5).ToList();
            var topDd = Analysis.Players.Where(p => p.MeanMin >= 20 && p.P.Status != "Q" && p.DD <= 0.95).OrderByDescending(p => p.DD).Take(5).ToList();
            var top3s = Analysis.Players.Where(p => p.MeanMin >= 20 && p.P.Status != "Q").OrderByDescending(p => p.Tpm.PAtLeast(3)).Take(5).ToList();
            foreach (var p in Analysis.Players.Where(x => x.MeanMin >= 10))
            {
                Func<string, double, double, string, Pred> mk = (stat, line, prob, tag) => new Pred { Date = date, GameId = p.G.Def.EspnId, Game = gl(p.G), Kind = "PLAYER", Stat = stat, Team = p.P.Team, Pid = p.P.Pid, Player = p.P.Name, Line = line, P = prob, Tag = tag, Selection = p.P.Name + " " + stat + (line > 0 ? " " + line + "+" : "") };
                foreach (int t in Analysis.PtsT) res.Add(mk("PTS", t, p.Pts.PAtLeast(t), t == 25 && top25.Contains(p) ? "top5_25pts" : ""));
                foreach (int t in Analysis.RebT) res.Add(mk("REB", t, p.Reb.PAtLeast(t), ""));
                foreach (int t in Analysis.AstT) res.Add(mk("AST", t, p.Ast.PAtLeast(t), ""));
                foreach (int t in Analysis.TpmT) res.Add(mk("3PM", t, p.Tpm.PAtLeast(t), t == 3 && top3s.Contains(p) ? "top5_3pm" : ""));
                foreach (int t in Analysis.PraT) res.Add(mk("PRA", t, p.Pra.PAtLeast(t), ""));
                var ddPred = mk("DD", 0, p.DD, topDd.Contains(p) ? "top5_dd" : ""); ddPred.Mean = p.DDRaw; res.Add(ddPred); // Mean keeps the raw share for refitting
                res.Add(mk("TD", 0, p.TD, ""));
                res.Add(mk("Q3ALL", 0, p.Q3All, topQ.Contains(p) ? "top5_q3all" : ""));
                res.Add(mk("FFG", 0, p.FirstFG, topF.Contains(p) ? "top5_ffg" : ""));
                foreach (var s in new[] { Tuple.Create("PTS", p.Pts.Mean), Tuple.Create("REB", p.Reb.Mean), Tuple.Create("AST", p.Ast.Mean), Tuple.Create("3PM", p.Tpm.Mean), Tuple.Create("MIN", p.MeanMin) })
                {
                    // PMarket on a projection row = the trend-learning factor it was made with (Learner divides it back out)
                    double lf = s.Item1 == "PTS" ? p.P.LearnPts : s.Item1 == "AST" ? p.P.LearnAst : 1;
                    res.Add(new Pred { Date = date, GameId = p.G.Def.EspnId, Game = gl(p.G), Kind = "MEAN", Stat = s.Item1, Team = p.P.Team, Pid = p.P.Pid, Player = p.P.Name, Mean = s.Item2, Line = p.P.Usage, PMarket = Math.Abs(lf - 1) > 1e-9 ? lf : double.NaN, Selection = p.P.Name + " mean " + s.Item1 });
                }
            }
            // erupt (scores eruption line or more) and dud (half his season scoring or less), graded like any prop
            foreach (var p in Analysis.EruptList())
                res.Add(new Pred { Date = date, GameId = p.G.Def.EspnId, Game = gl(p.G), Kind = "PLAYER", Stat = "PTS", Team = p.P.Team, Pid = p.P.Pid, Player = p.P.Name, Line = p.EruptLine, P = p.PErupt, Mean = p.SeasonPpg, Tag = "erupt", Selection = p.P.Name + " erupts: " + p.EruptLine + "+ pts [" + Report.BankCheck(p, true) + "]" });
            foreach (var p in Analysis.DudList())
                res.Add(new Pred { Date = date, GameId = p.G.Def.EspnId, Game = gl(p.G), Kind = "PLAYER", Stat = "PTSU", Team = p.P.Team, Pid = p.P.Pid, Player = p.P.Name, Line = p.DudLine, P = p.PDud, Mean = p.SeasonPpg, Tag = "dud", Selection = p.P.Name + " dud: " + p.DudLine + " pts or fewer [" + Report.BankCheck(p, false) + "]" });
            // the model's favorite lines (highest ladder rung at FavMin or better), graded like any prop. Kind FAV keeps them
            // out of the calibration statistics, which already count every threshold once. Mean = his season average.
            foreach (var g in Program.Games)
                foreach (var f in Favorites.ForGame(g))
                    res.Add(new Pred { Date = date, GameId = g.Def.EspnId, Game = gl(g), Kind = "FAV", Stat = f.Stat, Team = f.Ps.P.Team, Pid = f.Ps.P.Pid, Player = f.Ps.P.Name, Line = f.Line, P = f.P, Mean = f.SeasonAvg, Tag = f.Bold ? "fav_bold" : "fav", Selection = f.Desc });
            var best = Notes.BestSgp();
            foreach (var g in Program.Games)
            {
                var combos = new List<Tuple<string, Combo>>();
                foreach (var c in Analysis.BoostSgp[g]) combos.Add(Tuple.Create(c == best ? "best_sgp" : "boost_candidate", c));
                if (Analysis.BestHitSgp[g] != null) combos.Add(Tuple.Create("sgp_best_hit", Analysis.BestHitSgp[g]));
                if (Analysis.LiftSgp[g] != null) combos.Add(Tuple.Create("sgp_most_correlated", Analysis.LiftSgp[g]));
                if (Analysis.RiskySgp[g] != null) combos.Add(Tuple.Create("sgp_4leg", Analysis.RiskySgp[g]));
                foreach (var c in combos)
                    res.Add(new Pred { Date = date, GameId = g.Def.EspnId, Game = gl(g), Kind = "SGP", P = c.Item2.Joint, Mean = c.Item2.Product, Legs = string.Join(";", c.Item2.Legs.Select(LegCode)), Tag = c.Item1, Selection = string.Join(" + ", c.Item2.Legs.Select(l => l.Desc)) });
            }
            foreach (var grp in Analysis.Leaders.GroupBy(l => l.Category))
                foreach (var l in grp.OrderByDescending(x => x.P).Take(10))
                    res.Add(new Pred { Date = date, GameId = l.Ps != null ? l.Ps.G.Def.EspnId : "", Game = "slate", Kind = "LEADER", Stat = grp.Key, Team = l.Team, Pid = l.Ps != null ? l.Ps.P.Pid : "", Player = l.Ps != null ? l.Ps.P.Name : "", P = l.P, Selection = grp.Key + ": " + (l.Ps != null ? l.Ps.P.Name : l.Team) });
            return res;
        }
    }

    static class Grader
    {
        static int Stat(BoxLine b, string s)
        {
            switch (s)
            {
                case "PTS": case "PTSU": return b.Pts; case "REB": return b.Reb; case "AST": return b.Ast; case "3PM": return b.Tpm;
                case "PRA": return b.Pts + b.Reb + b.Ast;
                case "MIN": return (int)Math.Round(b.Min);
            }
            return 0;
        }
        static int DdCount(BoxLine b) { return (b.Pts >= 10 ? 1 : 0) + (b.Reb >= 10 ? 1 : 0) + (b.Ast >= 10 ? 1 : 0); }

        // resolves one leg / prediction; returns 1, 0, or NaN for void/push
        public static double Resolve(string kind, string stat, string team, string pid, double line, GameData g)
        {
            switch (kind)
            {
                case "ML":
                    return (team == g.Home ? g.HomePts > g.AwayPts : g.AwayPts > g.HomePts) ? 1 : 0;
                case "SPREAD": case "SPR":
                    {
                        double m = team == g.Home ? g.HomePts - g.AwayPts : g.AwayPts - g.HomePts;
                        return m + line > 0 ? 1 : m + line < 0 ? 0 : double.NaN;
                    }
                case "TOTAL": case "TOT":
                    {
                        double t = g.HomePts + g.AwayPts;
                        if (t == line) return double.NaN;
                        bool over = t > line;
                        string side = kind == "TOT" ? team : stat;
                        return (side == "OVER") == over ? 1 : 0;
                    }
                case "TEAMQ3":
                    return (team == g.Home ? g.HomeQ123 : g.AwayQ123) >= line ? 1 : 0;
            }
            var b = g.Box.FirstOrDefault(x => x.Pid == pid);
            if (b == null || !b.Played) return double.NaN;
            string st = kind == "PLAYER" || kind == "FAV" ? stat : kind;
            switch (st)
            {
                case "DD": return DdCount(b) >= 2 ? 1 : 0;
                case "TD": return DdCount(b) >= 3 ? 1 : 0;
                case "Q3ALL": return b.QPts.All(v => v >= 3) ? 1 : 0;
                case "FFG": return g.FirstFgPid == pid ? 1 : 0;
                case "PTSU": return b.Pts <= line ? 1 : 0;   // dud: at or under the line
                default: return Stat(b, st) >= line ? 1 : 0;
            }
        }

        public static List<Pred> Grade(List<Pred> preds, Func<string, GameData> game)
        {
            foreach (var p in preds)
            {
                p.Y = double.NaN; p.Void = false; p.Actual = double.NaN;
                if (p.Kind == "LEADER") continue;
                var g = game(p.GameId);
                if (g == null || !g.Completed) { p.Void = true; continue; }
                if (p.Kind == "MEAN")
                {
                    if (p.Stat == "MARGIN") p.Actual = g.HomePts - g.AwayPts;
                    else if (p.Stat == "TOTAL") p.Actual = g.HomePts + g.AwayPts;
                    else { var b = g.Box.FirstOrDefault(x => x.Pid == p.Pid); if (b != null && b.Played) p.Actual = Stat(b, p.Stat); else p.Void = true; }
                    continue;
                }
                if (p.Kind == "SGP")
                {
                    double y = 1;
                    foreach (var leg in p.Legs.Split(';'))
                    {
                        var f = leg.Split('|');
                        double r = Resolve(f[0], f[0], f[1], f[2], U.D(f[3]), g);
                        if (double.IsNaN(r)) { y = double.NaN; break; }
                        y = Math.Min(y, r);
                    }
                    if (double.IsNaN(y)) p.Void = true; else p.Y = y;
                    continue;
                }
                double res = Resolve(p.Kind, p.Stat, p.Team, p.Pid, p.Line, g);
                if (double.IsNaN(res)) p.Void = true; else p.Y = res;
                if (p.Kind == "PLAYER" || p.Kind == "FAV") { var b = g.Box.FirstOrDefault(x => x.Pid == p.Pid); if (b != null && b.Played) p.Actual = p.Stat == "Q3ALL" ? b.QPts.Min() : p.Stat == "FFG" ? (g.FirstFgPid == p.Pid ? 1 : 0) : p.Stat == "DD" || p.Stat == "TD" ? DdCount(b) : Stat(b, p.Stat); }
            }
            // slate leaders: dead-heat share among all players/teams on the slate
            var leaders = preds.Where(p => p.Kind == "LEADER").ToList();
            if (leaders.Count > 0)
            {
                var games = preds.Where(p => p.Kind == "ML").Select(p => p.GameId).Distinct().Select(game).Where(g => g != null && g.Completed).ToList();
                if (games.Count == preds.Where(p => p.Kind == "ML").Select(p => p.GameId).Distinct().Count())
                {
                    var box = games.SelectMany(g => g.Box.Where(b => b.Played)).ToList();
                    foreach (var p in leaders)
                    {
                        if (p.Stat.Contains("(player)"))
                        {
                            Func<BoxLine, int> v = p.Stat.Contains("points") ? (Func<BoxLine, int>)(b => b.Pts) : p.Stat.Contains("rebounds") ? (Func<BoxLine, int>)(b => b.Reb) : (b => b.Ast);
                            int best = box.Max(v); var top = box.Where(b => v(b) == best).ToList();
                            p.Y = top.Any(b => b.Pid == p.Pid) ? 1.0 / top.Count : 0; p.Actual = best;
                        }
                        else
                        {
                            var vals = new Dictionary<string, int>();
                            foreach (var g in games)
                            {
                                bool pts = p.Stat.Contains("points");
                                vals[g.Home] = pts ? g.HomePts : g.Box.Where(b => b.Team == g.Home && b.Played).Sum(b => b.Tpm);
                                vals[g.Away] = pts ? g.AwayPts : g.Box.Where(b => b.Team == g.Away && b.Played).Sum(b => b.Tpm);
                            }
                            int best = vals.Values.Max(); int cnt = vals.Values.Count(x => x == best);
                            p.Y = vals.ContainsKey(p.Team) && vals[p.Team] == best ? 1.0 / cnt : 0; p.Actual = best;
                        }
                    }
                }
            }
            return preds;
        }

        public class Score { public string Group; public int N; public double MeanP, HitRate, Brier, LogLoss, BrierMkt = double.NaN, Profit = double.NaN; }

        public static List<Score> Summarize(List<Pred> preds)
        {
            var res = new List<Score>();
            var gradable = preds.Where(p => !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P) && p.Kind != "MEAN").ToList();
            Func<string, IEnumerable<Pred>, Score> sc = (name, xs) =>
            {
                var l = xs.ToList(); if (l.Count == 0) return null;
                var s = new Score { Group = name, N = l.Count, MeanP = l.Average(x => x.P), HitRate = l.Average(x => x.Y), Brier = l.Average(x => (x.P - x.Y) * (x.P - x.Y)) };
                s.LogLoss = l.Average(x => { double pp = Math.Min(0.999, Math.Max(0.001, x.P)); return -(x.Y * Math.Log(pp) + (1 - x.Y) * Math.Log(1 - pp)); });
                var m = l.Where(x => !double.IsNaN(x.PMarket)).ToList();
                if (m.Count == l.Count) s.BrierMkt = m.Average(x => (x.PMarket - x.Y) * (x.PMarket - x.Y));
                var o = l.Where(x => !double.IsNaN(x.Odds)).ToList();
                if (o.Count == l.Count) s.Profit = o.Sum(x => x.Y >= 1 ? U.NetPayout(x.Odds) : -1); // flat $1 on every listed side (diagnostic only)
                return s;
            };
            foreach (var k in new[] { "ML", "SPREAD", "TOTAL", "TEAMQ3", "PLAYER", "SGP", "LEADER" })
            {
                var s = sc(k, gradable.Where(p => p.Kind == k)); if (s != null) res.Add(s);
            }
            foreach (var st in new[] { "PTS", "REB", "AST", "3PM", "PRA", "DD", "TD", "Q3ALL", "FFG" })
            {
                var s = sc("PLAYER " + st, gradable.Where(p => p.Kind == "PLAYER" && p.Stat == st)); if (s != null) res.Add(s);
            }
            foreach (var tag in gradable.Where(p => !string.IsNullOrEmpty(p.Tag)).Select(p => p.Tag).Distinct())
            {
                var s = sc("tag:" + tag, gradable.Where(p => p.Tag == tag)); if (s != null) res.Add(s);
            }
            return res;
        }

        // reliability table: predicted-probability bins vs observed frequency
        public static List<double[]> Reliability(IEnumerable<Pred> preds, int bins = 10)
        {
            var l = preds.Where(p => !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.P) && p.Kind != "MEAN" && p.Kind != "LEADER").ToList();
            var res = new List<double[]>();
            for (int b = 0; b < bins; b++)
            {
                double lo = (double)b / bins, hi = (double)(b + 1) / bins;
                var xs = l.Where(p => p.P >= lo && (p.P < hi || (b == bins - 1 && p.P <= 1))).ToList();
                res.Add(new double[] { lo, hi, xs.Count, xs.Count > 0 ? xs.Average(x => x.P) : double.NaN, xs.Count > 0 ? xs.Average(x => x.Y) : double.NaN });
            }
            return res;
        }
    }
}
