using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // 2026-27 data bank: strictly this season. Every completed game, every player's game log, season and recent trends,
    // team trends, and the model's own record on each player and team (walk-forward backtest + graded live slates).
    static class Bank
    {
        public class PGame { public string Date, GameId, Team, Opp; public bool Home, Starter, B2B; public BoxLine B; public int TeamMargin; public double ProjMin = double.NaN, ProjPts = double.NaN; }
        public class PlayerRow
        {
            public string Pid, Name, Team, Pos; public int Gp, Gs, DnpCd, DnpOther, DdGames, Pts20;
            public double Mpg, Ppg, Rpg, Apg, Tpm, Tpa, Spg, Bpg, Tovpg, FgPct, TpPct, FtPct, TsPct, Per36Pts, Per36Reb, Per36Ast, PtsSd;
            public double L3Min = double.NaN, L3Pts = double.NaN, L3Reb = double.NaN, L3Ast = double.NaN, L3Tpm = double.NaN, MinSlope = double.NaN, HomePpg = double.NaN, AwayPpg = double.NaN;
            public int ModelN; public double ModelPtsBias = double.NaN, ModelMinBias = double.NaN;
            public List<string> Flags = new List<string>(); public List<PGame> Log = new List<PGame>();
        }
        public class TeamRow
        {
            public string Team; public int Gp, W, L, AtsW, AtsL, Overs, Unders, Blowouts, ModelN;
            public double Ppg, OppPpg, Pace, ORtg, DRtg, Margin, Q123, OppQ123, BenchPpg, Rpg, Apg, Tpm, ModelMarginBias = double.NaN, ModelTotalBias = double.NaN;
            public List<string> Flags = new List<string>();
        }

        public static Dictionary<string, PlayerRow> Players = new Dictionary<string, PlayerRow>();
        public static Dictionary<string, TeamRow> Teams = new Dictionary<string, TeamRow>();
        public static List<GameData> Games = new List<GameData>();
        public static string BuiltThrough = "";

        static double Poss(GameData g, string team)
        {
            var b = g.Box.Where(x => x.Team == team && x.Played).ToList();
            return b.Sum(x => x.Fga) - b.Sum(x => x.Oreb) + b.Sum(x => x.Tov) + 0.44 * b.Sum(x => x.Fta);
        }

        public static string Build(DateTime through)
        {
            var S = Program.S;
            Games = Store.CompletedBefore(through.Date.AddDays(1), S.SeasonStart).OrderBy(g => g.Date).ThenBy(g => g.Id).ToList();
            BuiltThrough = Games.Count > 0 ? Games.Last().Date : "";
            Players = new Dictionary<string, PlayerRow>(); Teams = new Dictionary<string, TeamRow>();
            var teamDates = new HashSet<string>(Games.SelectMany(g => new[] { g.Home + "|" + g.Date, g.Away + "|" + g.Date }));

            // model's projections for each player-game: walk-forward backtest first, then graded live slates fill gaps
            var proj = new Dictionary<string, double[]>();   // date|pid -> {min, pts}
            var gameProj = new Dictionary<string, double[]>(); // gameId -> {home margin, total}
            var src = new List<Pred>();
            string bt = Program.P(@"ledger\backtest_latest.csv");
            if (File.Exists(bt)) src.AddRange(Pred.Load(bt));
            src.AddRange(Ledger.LoadAll());
            foreach (var p in src.Where(x => x.Kind == "MEAN"))
            {
                if (!string.IsNullOrEmpty(p.Pid) && (p.Stat == "MIN" || p.Stat == "PTS"))
                {
                    string k = p.Date + "|" + p.Pid; double[] v;
                    if (!proj.TryGetValue(k, out v)) proj[k] = v = new[] { double.NaN, double.NaN };
                    int i = p.Stat == "MIN" ? 0 : 1; if (double.IsNaN(v[i])) v[i] = p.Mean;
                }
                else if (p.Stat == "MARGIN" || p.Stat == "TOTAL")
                {
                    double[] v; if (!gameProj.TryGetValue(p.GameId, out v)) gameProj[p.GameId] = v = new[] { double.NaN, double.NaN };
                    int i = p.Stat == "MARGIN" ? 0 : 1; if (double.IsNaN(v[i])) v[i] = p.Mean;
                }
            }

            foreach (var g in Games)
            {
                int margin = g.HomePts - g.AwayPts;
                foreach (bool home in new[] { true, false })
                {
                    string team = home ? g.Home : g.Away, opp = home ? g.Away : g.Home;
                    TeamRow t; if (!Teams.TryGetValue(team, out t)) Teams[team] = t = new TeamRow { Team = team };
                    int tm = home ? margin : -margin;
                    t.Gp++; if (tm > 0) t.W++; else t.L++;
                    t.Ppg += home ? g.HomePts : g.AwayPts; t.OppPpg += home ? g.AwayPts : g.HomePts; t.Margin += tm;
                    double pos = (Poss(g, team) + Poss(g, opp)) / 2; t.Pace += pos * 48.0 / (48 + 5 * g.OT);
                    t.ORtg += 100.0 * (home ? g.HomePts : g.AwayPts) / Math.Max(1, pos); t.DRtg += 100.0 * (home ? g.AwayPts : g.HomePts) / Math.Max(1, pos);
                    t.Q123 += home ? g.HomeQ123 : g.AwayQ123; t.OppQ123 += home ? g.AwayQ123 : g.HomeQ123;
                    var tb = g.Box.Where(x => x.Team == team && x.Played).ToList();
                    t.BenchPpg += tb.Where(x => !x.Starter).Sum(x => x.Pts); t.Rpg += tb.Sum(x => x.Reb); t.Apg += tb.Sum(x => x.Ast); t.Tpm += tb.Sum(x => x.Tpm);
                    if (Math.Abs(margin) >= 20) t.Blowouts++;
                    if (!double.IsNaN(g.DkSprH)) { double cover = tm + (home ? g.DkSprH : -g.DkSprH); if (cover > 0) t.AtsW++; else if (cover < 0) t.AtsL++; }
                    if (!double.IsNaN(g.DkTotal)) { int tot = g.HomePts + g.AwayPts; if (tot > g.DkTotal) t.Overs++; else if (tot < g.DkTotal) t.Unders++; }
                    double[] gp;
                    if (gameProj.TryGetValue(g.Id, out gp) && !double.IsNaN(gp[0]))
                    {
                        double mb = (home ? gp[0] : -gp[0]) - tm, tb2 = gp[1] - (g.HomePts + g.AwayPts);
                        t.ModelMarginBias = double.IsNaN(t.ModelMarginBias) ? mb : t.ModelMarginBias + mb; t.ModelTotalBias = double.IsNaN(t.ModelTotalBias) ? tb2 : t.ModelTotalBias + tb2; t.ModelN++;
                    }
                    string prev = DateTime.ParseExact(g.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1).ToString("yyyy-MM-dd");
                    bool b2b = teamDates.Contains(team + "|" + prev);
                    foreach (var b in g.Box.Where(x => x.Team == team))
                    {
                        PlayerRow pr; if (!Players.TryGetValue(b.Pid, out pr)) Players[b.Pid] = pr = new PlayerRow { Pid = b.Pid, Name = b.Name, Pos = b.Pos };
                        pr.Team = team;
                        if (!b.Played) { if (b.Reason.ToUpperInvariant().Contains("COACH")) pr.DnpCd++; else pr.DnpOther++; continue; }
                        var pg = new PGame { Date = g.Date, GameId = g.Id, Team = team, Opp = opp, Home = home, Starter = b.Starter, B2B = b2b, B = b, TeamMargin = tm };
                        double[] v; if (proj.TryGetValue(g.Date + "|" + b.Pid, out v)) { pg.ProjMin = v[0]; pg.ProjPts = v[1]; }
                        pr.Log.Add(pg);
                    }
                }
            }
            foreach (var t in Teams.Values)
            {
                double n = t.Gp; t.Ppg /= n; t.OppPpg /= n; t.Margin /= n; t.Pace /= n; t.ORtg /= n; t.DRtg /= n; t.Q123 /= n; t.OppQ123 /= n; t.BenchPpg /= n; t.Rpg /= n; t.Apg /= n; t.Tpm /= n;
                if (t.ModelN > 0) { t.ModelMarginBias /= t.ModelN; t.ModelTotalBias /= t.ModelN; }
                if (t.ModelN >= 3 && Math.Abs(t.ModelMarginBias) >= 6) t.Flags.Add(string.Format(U.IC, "model margin off by {0:+0.0;-0.0} per game ({1} games): {2}", t.ModelMarginBias, t.ModelN, t.ModelMarginBias < 0 ? "better than projected" : "worse than projected"));
                if (t.ModelN >= 3 && Math.Abs(t.ModelTotalBias) >= 8) t.Flags.Add(string.Format(U.IC, "their games land {0:0.0} pts {1} the model total", Math.Abs(t.ModelTotalBias), t.ModelTotalBias > 0 ? "under" : "over"));
                if (t.Gp >= 3 && t.Blowouts * 2 >= t.Gp) t.Flags.Add(string.Format(U.IC, "{0} of {1} games decided by 20+ (starter minutes risk)", t.Blowouts, t.Gp));
                if (t.Overs + t.Unders >= 3 && (t.Overs == 0 || t.Unders == 0)) t.Flags.Add(string.Format(U.IC, "totals {0}-{1} over/under vs DK", t.Overs, t.Unders));
            }
            foreach (var p in Players.Values)
            {
                var l = p.Log; p.Gp = l.Count; if (p.Gp == 0) continue;
                p.Gs = l.Count(x => x.Starter);
                Func<Func<BoxLine, double>, double> avg = f => l.Average(x => f(x.B));
                p.Mpg = avg(b => b.Min); p.Ppg = avg(b => b.Pts); p.Rpg = avg(b => b.Reb); p.Apg = avg(b => b.Ast); p.Tpm = avg(b => b.Tpm); p.Tpa = avg(b => b.Tpa);
                p.Spg = avg(b => b.Stl); p.Bpg = avg(b => b.Blk); p.Tovpg = avg(b => b.Tov);
                int fga = l.Sum(x => x.B.Fga), tpa = l.Sum(x => x.B.Tpa), fta = l.Sum(x => x.B.Fta), pts = l.Sum(x => x.B.Pts); double min = l.Sum(x => x.B.Min);
                p.FgPct = fga > 0 ? (double)l.Sum(x => x.B.Fgm) / fga : double.NaN; p.TpPct = tpa > 0 ? (double)l.Sum(x => x.B.Tpm) / tpa : double.NaN; p.FtPct = fta > 0 ? (double)l.Sum(x => x.B.Ftm) / fta : double.NaN;
                p.TsPct = fga + 0.44 * fta > 0 ? pts / (2 * (fga + 0.44 * fta)) : double.NaN;
                p.Per36Pts = min > 0 ? 36 * pts / min : 0; p.Per36Reb = min > 0 ? 36 * l.Sum(x => x.B.Reb) / min : 0; p.Per36Ast = min > 0 ? 36 * l.Sum(x => x.B.Ast) / min : 0;
                p.PtsSd = p.Gp >= 2 ? Math.Sqrt(l.Sum(x => (x.B.Pts - p.Ppg) * (x.B.Pts - p.Ppg)) / (p.Gp - 1)) : double.NaN;
                p.DdGames = l.Count(x => (x.B.Pts >= 10 ? 1 : 0) + (x.B.Reb >= 10 ? 1 : 0) + (x.B.Ast >= 10 ? 1 : 0) >= 2); p.Pts20 = l.Count(x => x.B.Pts >= 20);
                var l3 = l.Skip(Math.Max(0, l.Count - 3)).ToList();
                if (p.Gp >= 3) { p.L3Min = l3.Average(x => x.B.Min); p.L3Pts = l3.Average(x => (double)x.B.Pts); p.L3Reb = l3.Average(x => (double)x.B.Reb); p.L3Ast = l3.Average(x => (double)x.B.Ast); p.L3Tpm = l3.Average(x => (double)x.B.Tpm); }
                if (p.Gp >= 3)
                {   // least-squares minutes slope per game played
                    double mx = (p.Gp - 1) / 2.0, my = p.Mpg, sxy = 0, sxx = 0;
                    for (int i = 0; i < p.Gp; i++) { sxy += (i - mx) * (l[i].B.Min - my); sxx += (i - mx) * (i - mx); }
                    p.MinSlope = sxx > 0 ? sxy / sxx : 0;
                }
                var hm = l.Where(x => x.Home).ToList(); var aw = l.Where(x => !x.Home).ToList();
                if (hm.Count > 0) p.HomePpg = hm.Average(x => (double)x.B.Pts); if (aw.Count > 0) p.AwayPpg = aw.Average(x => (double)x.B.Pts);
                var withProj = l.Where(x => !double.IsNaN(x.ProjPts)).ToList();
                p.ModelN = withProj.Count;
                if (p.ModelN > 0) { p.ModelPtsBias = withProj.Average(x => x.ProjPts - x.B.Pts); p.ModelMinBias = withProj.Where(x => !double.IsNaN(x.ProjMin)).Select(x => x.ProjMin - x.B.Min).DefaultIfEmpty(double.NaN).Average(); }
                // trends created this season
                string path = string.Join(" > ", l.Select(x => Math.Round(x.B.Min).ToString("0", U.IC)));
                if (p.Gp >= 3 && p.MinSlope >= 2) p.Flags.Add(string.Format(U.IC, "minutes rising {0:+0.0} per game ({1})", p.MinSlope, path));
                if (p.Gp >= 3 && p.MinSlope <= -2) p.Flags.Add(string.Format(U.IC, "minutes falling {0:0.0} per game ({1})", p.MinSlope, path));
                if (p.Gp >= 4 && p.L3Pts >= p.Ppg + 3) p.Flags.Add(string.Format(U.IC, "scoring up: {0:0.0} over last 3 vs {1:0.0}", p.L3Pts, p.Ppg));
                if (p.Gp >= 4 && p.L3Pts <= p.Ppg - 3) p.Flags.Add(string.Format(U.IC, "scoring down: {0:0.0} over last 3 vs {1:0.0}", p.L3Pts, p.Ppg));
                int recentStarts = l.Skip(Math.Max(0, l.Count - 2)).Count(x => x.Starter), earlyStarts = l.Take(Math.Max(0, l.Count - 2)).Count(x => x.Starter);
                if (p.Gp >= 3 && recentStarts == 2 && earlyStarts == 0) p.Flags.Add("moved into the starting lineup");
                if (p.Gp >= 3 && recentStarts == 0 && earlyStarts == p.Gp - 2 && earlyStarts > 0) p.Flags.Add("moved to the bench");
                if (p.ModelN >= 3 && Math.Abs(p.ModelPtsBias) >= 3) p.Flags.Add(string.Format(U.IC, "model has {0} him by {1:0.0} pts a game ({2} games)", p.ModelPtsBias < 0 ? "under-projected" : "over-projected", Math.Abs(p.ModelPtsBias), p.ModelN));
                if (p.Gp >= 3 && !double.IsNaN(p.PtsSd) && p.Ppg >= 10 && p.PtsSd <= 0.2 * p.Ppg) p.Flags.Add(string.Format(U.IC, "steady scorer (SD {0:0.0})", p.PtsSd));
            }
            Save();
            return string.Format(U.IC, "Data bank: {0} games through {1}, {2} players, {3} teams; {4} player trend flags, {5} team flags.", Games.Count, BuiltThrough, Players.Values.Count(p => p.Gp > 0), Teams.Count,
                Players.Values.Sum(p => p.Flags.Count), Teams.Values.Sum(t => t.Flags.Count));
        }

        static string F(double v, string fmt) { return double.IsNaN(v) ? "" : v.ToString(fmt, U.IC); }
        static string Q(string s) { s = s ?? ""; return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "'") + "\"" : s; }

        static void Save()
        {
            string dir = Program.P(@"data\bank"); Directory.CreateDirectory(dir);
            var enc = new UTF8Encoding(false);
            File.WriteAllLines(Path.Combine(dir, "games.csv"), new[] { "date,game_id,away,home,away_pts,home_pts,ot,away_q1,away_q2,away_q3,away_q4,home_q1,home_q2,home_q3,home_q4,dk_spread_home,dk_total,margin_home,total" }
                .Concat(Games.Select(g => string.Join(",", g.Date, g.Id, g.Away, g.Home, g.AwayPts, g.HomePts, g.OT, g.AwayQ[0], g.AwayQ[1], g.AwayQ[2], g.AwayQ[3], g.HomeQ[0], g.HomeQ[1], g.HomeQ[2], g.HomeQ[3], F(g.DkSprH, "0.0"), F(g.DkTotal, "0.0"), g.HomePts - g.AwayPts, g.HomePts + g.AwayPts))), enc);
            File.WriteAllLines(Path.Combine(dir, "player_games.csv"), new[] { "date,game_id,pid,player,team,opp,home,starter,b2b,min,pts,reb,ast,stl,blk,tov,fgm,fga,tpm,tpa,ftm,fta,oreb,dreb,pf,q1,q2,q3,q4,team_margin,proj_min,proj_pts" }
                .Concat(Players.Values.SelectMany(p => p.Log.Select(x => string.Join(",", x.Date, x.GameId, p.Pid, Q(p.Name), x.Team, x.Opp, x.Home ? 1 : 0, x.Starter ? 1 : 0, x.B2B ? 1 : 0, x.B.Min.ToString("0", U.IC), x.B.Pts, x.B.Reb, x.B.Ast, x.B.Stl, x.B.Blk, x.B.Tov,
                    x.B.Fgm, x.B.Fga, x.B.Tpm, x.B.Tpa, x.B.Ftm, x.B.Fta, x.B.Oreb, x.B.Dreb, x.B.Pf, x.B.QPts[0], x.B.QPts[1], x.B.QPts[2], x.B.QPts[3], x.TeamMargin, F(x.ProjMin, "0.0"), F(x.ProjPts, "0.0"))))), enc);
            File.WriteAllLines(Path.Combine(dir, "players.csv"), new[] { "pid,player,team,pos,gp,gs,dnp_coach,dnp_other,mpg,ppg,rpg,apg,tpm,tpa,spg,bpg,tov,fg_pct,tp_pct,ft_pct,ts_pct,pts36,reb36,ast36,pts_sd,l3_min,l3_pts,l3_reb,l3_ast,l3_tpm,min_slope,home_ppg,away_ppg,dd_games,games_20pts,model_games,model_pts_bias,model_min_bias,flags" }
                .Concat(Players.Values.Where(p => p.Gp > 0).OrderBy(p => p.Team).ThenByDescending(p => p.Mpg).Select(p => string.Join(",", p.Pid, Q(p.Name), p.Team, p.Pos, p.Gp, p.Gs, p.DnpCd, p.DnpOther, F(p.Mpg, "0.0"), F(p.Ppg, "0.0"), F(p.Rpg, "0.0"), F(p.Apg, "0.0"), F(p.Tpm, "0.0"), F(p.Tpa, "0.0"),
                    F(p.Spg, "0.0"), F(p.Bpg, "0.0"), F(p.Tovpg, "0.0"), F(p.FgPct, "0.000"), F(p.TpPct, "0.000"), F(p.FtPct, "0.000"), F(p.TsPct, "0.000"), F(p.Per36Pts, "0.0"), F(p.Per36Reb, "0.0"), F(p.Per36Ast, "0.0"), F(p.PtsSd, "0.0"),
                    F(p.L3Min, "0.0"), F(p.L3Pts, "0.0"), F(p.L3Reb, "0.0"), F(p.L3Ast, "0.0"), F(p.L3Tpm, "0.0"), F(p.MinSlope, "0.00"), F(p.HomePpg, "0.0"), F(p.AwayPpg, "0.0"), p.DdGames, p.Pts20, p.ModelN, F(p.ModelPtsBias, "0.0"), F(p.ModelMinBias, "0.0"), Q(string.Join("; ", p.Flags))))), enc);
            File.WriteAllLines(Path.Combine(dir, "teams.csv"), new[] { "team,gp,w,l,ppg,opp_ppg,margin,pace,ortg,drtg,q123,opp_q123,bench_ppg,rpg,apg,tpm,blowouts,ats_w,ats_l,overs,unders,model_games,model_margin_bias,model_total_bias,flags" }
                .Concat(Teams.Values.OrderBy(t => t.Team).Select(t => string.Join(",", t.Team, t.Gp, t.W, t.L, F(t.Ppg, "0.0"), F(t.OppPpg, "0.0"), F(t.Margin, "0.0"), F(t.Pace, "0.0"), F(t.ORtg, "0.0"), F(t.DRtg, "0.0"), F(t.Q123, "0.0"), F(t.OppQ123, "0.0"), F(t.BenchPpg, "0.0"),
                    F(t.Rpg, "0.0"), F(t.Apg, "0.0"), F(t.Tpm, "0.0"), t.Blowouts, t.AtsW, t.AtsL, t.Overs, t.Unders, t.ModelN, F(t.ModelMarginBias, "0.0"), F(t.ModelTotalBias, "0.0"), Q(string.Join("; ", t.Flags))))), enc);
        }

        public static PlayerRow Player(string pid) { PlayerRow p; return pid != null && Players.TryGetValue(pid, out p) ? p : null; }
        public static TeamRow Team(string abbr) { TeamRow t; return abbr != null && Teams.TryGetValue(abbr, out t) ? t : null; }
    }

    static partial class Report
    {
        static readonly string[] BankTeamHdr = { "Team", "GP", "W-L", "PPG", "Opp PPG", "Margin", "Pace", "ORtg", "DRtg", "Q1-Q3 for", "Q1-Q3 vs", "Bench PPG", "3PM", "20+ blowouts", "ATS vs DK", "O/U vs DK", "Model margin bias", "Model total bias", "Trends" };
        static readonly string[] BankPlayerHdr = { "Player", "Team", "GP", "GS", "MPG", "PPG", "RPG", "APG", "3PM", "FG%", "3P%", "TS%", "PTS/36", "PTS SD", "L3 MIN", "L3 PTS", "L3 REB", "L3 AST", "MIN trend/gm", "DD games", "20+ games", "Model PTS bias (n)", "Trends this season" };

        static void BankTeamRow(Sheet sh, Bank.TeamRow t)
        {
            var r = sh.Row();
            r.Add(Cell.Str(t.Team, Styles.Bold)); r.Add(Cell.Num(t.Gp, Styles.Int)); r.Add(Cell.Str(t.W + "-" + t.L));
            foreach (var v in new[] { t.Ppg, t.OppPpg, t.Margin, t.Pace, t.ORtg, t.DRtg, t.Q123, t.OppQ123, t.BenchPpg, t.Tpm }) r.Add(Cell.Num(v, Styles.N1));
            r.Add(Cell.Num(t.Blowouts, Styles.Int)); r.Add(Cell.Str(t.AtsW + "-" + t.AtsL)); r.Add(Cell.Str(t.Overs + "-" + t.Unders));
            r.Add(ANum(t.ModelMarginBias, Styles.N1)); r.Add(ANum(t.ModelTotalBias, Styles.N1)); r.Add(Cell.Str(string.Join("; ", t.Flags), Styles.Wrap));
        }
        static void BankPlayerRow(Sheet sh, Bank.PlayerRow p)
        {
            var r = sh.Row();
            r.Add(Cell.Str(p.Name, p.Flags.Count > 0 ? Styles.Bold : Styles.Def)); r.Add(Cell.Str(p.Team)); r.Add(Cell.Num(p.Gp, Styles.Int)); r.Add(Cell.Num(p.Gs, Styles.Int));
            foreach (var v in new[] { p.Mpg, p.Ppg, p.Rpg, p.Apg, p.Tpm }) r.Add(Cell.Num(v, Styles.N1));
            foreach (var v in new[] { p.FgPct, p.TpPct, p.TsPct }) r.Add(ANum(v, Styles.Pct));
            foreach (var v in new[] { p.Per36Pts, p.PtsSd, p.L3Min, p.L3Pts, p.L3Reb, p.L3Ast }) r.Add(ANum(v, Styles.N1));
            r.Add(ANum(p.MinSlope, p.MinSlope >= 2 ? Styles.Good : p.MinSlope <= -2 ? Styles.Bad : Styles.N1));
            r.Add(Cell.Num(p.DdGames, Styles.Int)); r.Add(Cell.Num(p.Pts20, Styles.Int));
            r.Add(Cell.Str(p.ModelN > 0 ? string.Format(U.IC, "{0:+0.0;-0.0} ({1})", p.ModelPtsBias, p.ModelN) : "-"));
            r.Add(Cell.Str(string.Join("; ", p.Flags), Styles.Wrap));
        }

        // tonight's teams only, inside the slate report
        static void SeasonBankSheet()
        {
            if (Bank.Players.Count == 0) return;
            var sh = New("Season Bank", "2026-27 data bank for tonight's teams (this season only, through " + Bank.BuiltThrough + ")",
                "Built from every completed 2026-27 box score. Model bias = model projection minus actual (negative = the model has been too low on him), from the walk-forward backtest and graded slates. Trends are flags created by this season's games.",
                24, 6, 5, 5, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 7, 7, 12, 70);
            var teams = Program.Games.SelectMany(g => new[] { g.Def.Away, g.Def.Home }).ToList();
            Section(sh, "Team trends", BankTeamHdr.Length); Hdr(sh, BankTeamHdr);
            foreach (var t in teams.Select(Bank.Team).Where(t => t != null)) BankTeamRow(sh, t);
            foreach (var team in teams)
            {
                Section(sh, team + " players (this season)", BankPlayerHdr.Length); Hdr(sh, BankPlayerHdr);
                var roster = new HashSet<string>(Program.TeamPlayers[team].Select(p => p.Pid));
                foreach (var p in Bank.Players.Values.Where(p => p.Gp > 0 && roster.Contains(p.Pid)).OrderByDescending(p => p.Mpg)) BankPlayerRow(sh, p);
            }
        }

        // the whole league, as its own workbook
        public static void BuildBankWorkbook(string path)
        {
            Sheets = new List<Sheet>();
            var tr = New("Teams", "2026-27 team trends (this season only, through " + Bank.BuiltThrough + ")", "Pace and ratings from box-score possessions. ATS and O/U against the DraftKings line on ESPN. Model bias = model minus actual.",
                8, 5, 7, 7, 7, 7, 7, 7, 7, 8, 8, 8, 6, 8, 8, 8, 10, 10, 70);
            Hdr(tr, BankTeamHdr);
            foreach (var t in Bank.Teams.Values.OrderByDescending(t => t.Margin)) BankTeamRow(tr, t);
            var fl = New("Trends", "Trends created this season (players with flags)", null, 24, 6, 5, 5, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 7, 7, 12, 70);
            Hdr(fl, BankPlayerHdr);
            foreach (var p in Bank.Players.Values.Where(p => p.Gp > 0 && p.Flags.Count > 0).OrderBy(p => p.Team).ThenByDescending(p => p.Mpg)) BankPlayerRow(fl, p);
            var pl = New("Players", "Every player this season", null, 24, 6, 5, 5, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 7, 7, 12, 70);
            Hdr(pl, BankPlayerHdr);
            foreach (var p in Bank.Players.Values.Where(p => p.Gp > 0).OrderBy(p => p.Team).ThenByDescending(p => p.Mpg)) BankPlayerRow(pl, p);
            var gl = New("Games", "Every completed game", null, 11, 7, 7, 7, 7, 14, 14, 9, 8, 8, 8);
            Hdr(gl, "Date", "Away", "Away pts", "Home", "Home pts", "Away Q1-Q4", "Home Q1-Q4", "DK spread (home)", "DK total", "Margin", "Total");
            foreach (var g in Bank.Games)
            {
                var r = gl.Row();
                r.Add(Cell.Str(g.Date)); r.Add(Cell.Str(g.Away)); r.Add(Cell.Num(g.AwayPts, Styles.Int)); r.Add(Cell.Str(g.Home)); r.Add(Cell.Num(g.HomePts, Styles.Int));
                r.Add(Cell.Str(string.Join("-", g.AwayQ))); r.Add(Cell.Str(string.Join("-", g.HomeQ))); r.Add(ANum(g.DkSprH, Styles.N1)); r.Add(ANum(g.DkTotal, Styles.N1));
                r.Add(Cell.Num(g.HomePts - g.AwayPts, Styles.Int)); r.Add(Cell.Num(g.HomePts + g.AwayPts, Styles.Int));
            }
            var lg = New("Game logs", "Every player-game this season", "Proj = the model's walk-forward or published projection for that game, where one exists.", 11, 22, 6, 6, 6, 6, 6, 6, 6, 6, 9, 9, 8, 8, 8, 8);
            Hdr(lg, "Date", "Player", "Team", "Opp", "Home", "Start", "MIN", "PTS", "REB", "AST", "3PM-3PA", "FGM-FGA", "Proj MIN", "Proj PTS", "Team margin", "B2B");
            foreach (var x in Bank.Players.Values.SelectMany(p => p.Log.Select(l => Tuple.Create(p, l))).OrderBy(t => t.Item2.Date).ThenBy(t => t.Item2.Team).ThenByDescending(t => t.Item2.B.Min))
            {
                var l = x.Item2; var b = l.B; var r = lg.Row();
                r.Add(Cell.Str(l.Date)); r.Add(Cell.Str(x.Item1.Name)); r.Add(Cell.Str(l.Team)); r.Add(Cell.Str(l.Opp)); r.Add(Cell.Str(l.Home ? "H" : "A")); r.Add(Cell.Str(l.Starter ? "S" : ""));
                r.Add(Cell.Num(Math.Round(b.Min), Styles.Int)); r.Add(Cell.Num(b.Pts, Styles.Int)); r.Add(Cell.Num(b.Reb, Styles.Int)); r.Add(Cell.Num(b.Ast, Styles.Int));
                r.Add(Cell.Str(b.Tpm + "-" + b.Tpa)); r.Add(Cell.Str(b.Fgm + "-" + b.Fga)); r.Add(ANum(l.ProjMin, Styles.N1)); r.Add(ANum(l.ProjPts, Styles.N1)); r.Add(Cell.Num(l.TeamMargin, Styles.Int)); r.Add(Cell.Str(l.B2B ? "yes" : ""));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Xlsx.Save(path, Sheets);
        }
    }
}
