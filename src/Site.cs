using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace NbaQuant
{
    // Website data export: tonight's slate, projected box scores, picks, the season bank, the live track record and the audit,
    // written as site\data.js (window.NBA = {...}) for the NBA Quant Desk website.
    static partial class Report
    {
        static double R1(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 1); }
        static double R3(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 3); }
        static Dictionary<string, object> O(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }


        // The model's favorite line per stat (Favorites.cs), as a compact row:
        // [line, chance, season hits, season games, last-5 hits, last-5 games, reach line, reach chance, floor line, floor chance, bold]
        static object FavJson(PStat p)
        {
            var d = new Dictionary<string, object>();
            foreach (var st in Favorites.Stats)
            {
                var f = Favorites.Of(p, st); if (f == null) continue;
                d[st] = new object[] { f.Line, R3(f.P), f.Hit, f.Gp, f.L5Hit, f.L5N, f.Reach, R3(f.PReach), f.Floor, R3(f.PFloor), f.Bold ? 1 : 0 };
            }
            return d;
        }
        // chance of clearing each rung of the ladder (rungs under 2% are left off)
        static object LadderJson(PStat p)
        {
            var d = new Dictionary<string, object>();
            foreach (var st in Favorites.Stats)
            {
                var dist = Favorites.DistOf(p, st); var l = new List<object>();
                foreach (int r in Favorites.Ladder[st]) { double pr = dist.PAtLeast(r); if (pr < 0.02) break; l.Add(new object[] { r, R3(pr) }); }
                d[st] = l;
            }
            return d;
        }
        static object FavRow(Fav f)
        {
            return O("name", f.Ps.P.Name, "team", f.Ps.P.Team, "stat", f.Stat, "unit", f.Unit, "line", f.Line, "p", R3(f.P), "hit", f.Hit, "gp", f.Gp, "l5hit", f.L5Hit, "l5n", f.L5N,
                "avg", R1(f.SeasonAvg), "mean", R1(Favorites.DistOf(f.Ps, f.Stat).Mean), "reach", f.Reach, "pReach", R3(f.PReach));
        }

        static object ComboJson(Combo c)
        {
            return O("legs", c.Legs.Select(l => l.Desc).ToList(), "legP", c.Legs.Select(l => R3(l.P)).ToList(), "joint", R3(c.Joint), "lift", Math.Round(c.Lift, 2), "fair", Math.Round(U.FairAmerican(c.Joint)));
        }

        // which official injury report a run used (first "# Source:" line of the overrides file written by `injuries`)
        public static string InjuryReportUsed(DateTime d)
        {
            string irPath = Program.P(@"overrides\injuries_" + d.ToString("yyyy-MM-dd") + ".csv");
            return File.Exists(irPath) ? File.ReadLines(irPath).Where(l => l.StartsWith("# Source:")).Select(l => l.Substring(9).Trim()).FirstOrDefault() : null;
        }

        public static string ExportSite(string path, DateTime d)
        {
            var S = Program.S;
            var rot = Rotations().ToDictionary(x => x.Ps, x => x);
            var games = new List<object>();
            foreach (var g in Program.Games)
            {
                var r = g.Res; double n = r.N; var dk = g.Def.DK;
                Func<bool, object> box = home =>
                {
                    var ps = Analysis.Players.Where(p => p.G == g && p.Home == home && p.PPlays * p.MeanMin >= 3).OrderByDescending(p => p.PPlays * p.MeanMin).ToList();
                    int k = 0;
                    return ps.Select(p =>
                    {
                        k++; var f = FormOf(p); RotRow rr; rot.TryGetValue(p, out rr); var bp = Bank.Player(p.P.Pid);
                        return (object)O("name", p.P.Name, "starter", k <= 5, "status", p.P.Status, "play", R3(p.PPlays), "min", R1(p.MeanMin),
                            "pts", R1(p.Pts.Mean), "reb", R1(p.Reb.Mean), "ast", R1(p.Ast.Mean), "tpm", R1(p.Tpm.Mean), "ptsLo", p.Pts.P10, "ptsHi", p.Pts.P90,
                            // the model's favorite line per stat, the full ladder, and the trend learning behind this projection
                            "fav", FavJson(p), "lad", LadderJson(p), "pra", R1(p.Pra.Mean), "med", p.Pts.P50,
                            "rebLo", p.Reb.P10, "rebHi", p.Reb.P90, "astLo", p.Ast.P10, "astHi", p.Ast.P90,
                            "learnPts", R3(p.P.LearnPts), "learnAst", R3(p.P.LearnAst), "minTrend", R1(p.P.MinTrendShift),
                            "l5", p.P.CsBox.Count >= 3 ? O("n", Math.Min(5, p.P.CsBox.Count), "min", R1(p.P.CsBox.Skip(Math.Max(0, p.P.CsBox.Count - 5)).Average(b => b.Min)), "pts", R1(p.P.CsBox.Skip(Math.Max(0, p.P.CsBox.Count - 5)).Average(b => (double)b.Pts)),
                                "reb", R1(p.P.CsBox.Skip(Math.Max(0, p.P.CsBox.Count - 5)).Average(b => (double)b.Reb)), "ast", R1(p.P.CsBox.Skip(Math.Max(0, p.P.CsBox.Count - 5)).Average(b => (double)b.Ast)), "tpm", R1(p.P.CsBox.Skip(Math.Max(0, p.P.CsBox.Count - 5)).Average(b => (double)b.Tpm))) : null,
                            "log", p.P.CsBox.Skip(Math.Max(0, p.P.CsBox.Count - 12)).Select(b => (object)new object[] { Math.Round(b.Min), b.Pts, b.Reb, b.Ast, b.Tpm }).ToList(),
                            "p10", R3(p.Pts.PAtLeast(10)), "p20", R3(p.Pts.PAtLeast(20)), "p25", R3(p.Pts.PAtLeast(25)), "dd", R3(p.DD), "p3", R3(p.Tpm.PAtLeast(3)),
                            "season", f == null ? null : O("gp", f.Gp, "prior", f.FromPrior, "min", R1(f.Mpg), "pts", R1(f.Ppg), "reb", R1(f.Rpg), "ast", R1(f.Apg), "tpm", R1(f.Tpm)),
                            "bias", bp != null && bp.ModelN > 0 ? (object)R1(bp.ModelPtsBias) : null, "biasN", bp != null ? bp.ModelN : 0,
                            "flags", rr != null ? rr.Flags : new List<string>(), "up", rr != null ? rr.Up : 0);
                    }).ToList();
                };
                Func<string, object> outs = t => Program.TeamPlayers[t].Where(x => x.Status == "OUT" && Math.Max(x.CsMpg, x.PsMpg) >= 10).Select(x => x.Name).ToList();
                Func<string, object> qs = t => Program.TeamPlayers[t].Where(x => x.Status == "Q" && Math.Max(x.CsMpg, x.PsMpg) >= 8).Select(x => x.Name + " (" + U.Pct(x.PPlay) + ")").ToList();
                var tqH = Analysis.TeamQs.First(t => t.G == g && t.Home); var tqA = Analysis.TeamQs.First(t => t.G == g && !t.Home);
                double nv = Program.NoVigHome(dk);
                games.Add(O("id", g.Def.EspnId, "away", g.Def.Away, "home", g.Def.Home, "tip", g.Def.Tip ?? "",
                    "awayName", g.Info != null ? g.Info.AwayName : g.Def.Away, "homeName", g.Info != null ? g.Info.HomeName : g.Def.Home,
                    "dk", O("spreadHome", dk.SprH, "total", dk.Total, "mlHome", dk.MlH, "mlAway", dk.MlA),
                    "winHome", R3(g.WinH), "modelWinHome", R3(g.ModelWinH), "marketWinHome", R3(nv),
                    "ptsHome", R1(r.SumPtsH / n), "ptsAway", R1(r.SumPtsA / n), "total", R1(g.MeanTotal), "totalLo", g.TotalPct[0], "totalHi", g.TotalPct[4],
                    "pOver", R3(g.POver), "pCoverHome", R3(g.PCoverH), "pBlowout", R3(g.PBlowout), "pClose", R3(g.PClose), "pOT", R3(g.POT),
                    "evHome", dk.HasMl ? R3(U.EvPerDollar(g.WinH, dk.MlH)) : 0, "evAway", dk.HasMl ? R3(U.EvPerDollar(1 - g.WinH, dk.MlA)) : 0,
                    "b2bHome", g.Def.HomeB2B, "b2bAway", g.Def.AwayB2B,
                    "q3", O("home", O("mean", R1(tqH.Mean), "p100", R3(tqH.P100)), "away", O("mean", R1(tqA.Mean), "p100", R3(tqA.P100))),
                    "boxAway", box(false), "boxHome", box(true),
                    "favs", Favorites.Top(g, 8).Select(FavRow).ToList(), "sdMargin", R1(g.SdMargin), "sdTotal", R1(g.SdTotal), "margin", R1(g.MeanMargin),
                    "teamAway", O("pts", R1(r.SumPtsA / n), "ast", R1(r.SumAstA / n)), "teamHome", O("pts", R1(r.SumPtsH / n), "ast", R1(r.SumAstH / n)),
                    "outAway", outs(g.Def.Away), "outHome", outs(g.Def.Home), "qAway", qs(g.Def.Away), "qHome", qs(g.Def.Home),
                    "boost", Analysis.BoostSgp[g].Take(3).Select(ComboJson).ToList(),
                    "four", O("safest", Analysis.Best4(g, 0.30, 1.0, 2).Select(ComboJson).ToList(), "boost", Analysis.Best4(g, 0.19, 0.26, 2).Select(ComboJson).ToList(), "bigger", Analysis.Best4(g, 0.09, 0.14, 2).Select(ComboJson).ToList())));
            }
            var best = Notes.BestSgp();

            // erupt / dud lists against this season's scoring
            Func<PStat, bool, object> ed = (p, erupt) =>
            {
                RotRow rr; rot.TryGetValue(p, out rr); var fm = FormOf(p);
                return O("name", p.P.Name, "team", p.P.Team, "opp", p.Opp, "season", R1(p.SeasonPpg), "proj", R1(p.Pts.Mean), "fav", Favorites.Of(p, "PTS") != null ? (object)Favorites.Of(p, "PTS").Line : null, "favP", Favorites.Of(p, "PTS") != null ? R3(Favorites.Of(p, "PTS").P) : 0,
                    "line", erupt ? p.EruptLine : p.DudLine, "p", R3(TrackAdjust.Adj(erupt ? "erupt" : "dud", erupt ? p.PErupt : p.PDud)), "check", BankCheck(p, erupt),
                    "seasonMin", fm == null ? 0 : R1(fm.Mpg), "min", R1(p.MeanMin), "flags", rr != null ? rr.Flags : new List<string>(), "bias", Bank.Player(p.P.Pid) != null && Bank.Player(p.P.Pid).ModelN > 0 ? (object)R1(Bank.Player(p.P.Pid).ModelPtsBias) : null, "biasN", Bank.Player(p.P.Pid) != null ? Bank.Player(p.P.Pid).ModelN : 0);
            };
            var eruptJson = Analysis.EruptList().Select(p => ed(p, true)).ToList();
            var dudJson = Analysis.DudList().Select(p => ed(p, false)).ToList();

            // season bank: tonight's teams and every flagged player on them
            var teams = Program.Games.SelectMany(g => new[] { g.Def.Away, g.Def.Home }).ToList();
            var bankTeams = teams.Select(Bank.Team).Where(t => t != null).Select(t => (object)O("team", t.Team, "gp", t.Gp, "w", t.W, "l", t.L, "ppg", R1(t.Ppg), "opp", R1(t.OppPpg), "margin", R1(t.Margin), "pace", R1(t.Pace), "ortg", R1(t.ORtg), "drtg", R1(t.DRtg),
                "atsW", t.AtsW, "atsL", t.AtsL, "overs", t.Overs, "unders", t.Unders, "blowouts", t.Blowouts, "bias", R1(t.ModelMarginBias), "totalBias", R1(t.ModelTotalBias), "flags", t.Flags)).ToList();
            var rosterIds = new HashSet<string>(teams.SelectMany(t => Program.TeamPlayers[t].Select(p => p.Pid)));
            var bankPlayers = Bank.Players.Values.Where(p => p.Gp > 0 && rosterIds.Contains(p.Pid) && p.Mpg >= 10).OrderBy(p => p.Team).ThenByDescending(p => p.Mpg)
                .Select(p => (object)O("name", p.Name, "team", p.Team, "gp", p.Gp, "gs", p.Gs, "min", R1(p.Mpg), "pts", R1(p.Ppg), "reb", R1(p.Rpg), "ast", R1(p.Apg), "tpm", R1(p.Tpm), "ts", R3(p.TsPct),
                    "l3", double.IsNaN(p.L3Pts) ? null : (object)R1(p.L3Pts), "minSlope", double.IsNaN(p.MinSlope) ? null : (object)Math.Round(p.MinSlope, 1),
                    "mins", p.Log.Select(x => Math.Round(x.B.Min)).ToList(), "ptsLog", p.Log.Select(x => x.B.Pts).ToList(),
                    "bias", p.ModelN > 0 ? (object)R1(p.ModelPtsBias) : null, "biasN", p.ModelN, "flags", p.Flags)).ToList();

            // live track record from graded slates
            var live = Ledger.LoadAll();
            var tagRec = live.Where(p => !string.IsNullOrEmpty(p.Tag) && !p.Void && !double.IsNaN(p.Y)).GroupBy(p => p.Tag).OrderBy(gr => gr.Key)
                .Select(gr => (object)O("tag", gr.Key, "n", gr.Count(), "hits", (int)gr.Sum(x => x.Y), "pred", R3(gr.Average(x => x.P)))).ToList();
            var mlLive = live.Where(p => p.Kind == "ML" && !p.Void && !double.IsNaN(p.Y) && !double.IsNaN(p.PMarket)).ToList();
            var slates = live.Select(p => p.Date).Distinct().OrderBy(x => x).Select(dt =>
            {
                var l = live.Where(p => p.Date == dt).ToList();
                var ml = l.Where(p => p.Kind == "ML" && !p.Void && !double.IsNaN(p.Y)).GroupBy(p => p.GameId).Select(gr => gr.OrderByDescending(x => x.P).First()).ToList();
                var pl = l.Where(p => p.Kind == "PLAYER" && !p.Void && !double.IsNaN(p.Y) && PropStats.Contains(p.Stat)).ToList();
                var gamesRows = ml.Select(m =>
                {
                    var mm = l.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "MARGIN" && p.GameId == m.GameId); var mt = l.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "TOTAL" && p.GameId == m.GameId);
                    var to = l.FirstOrDefault(p => p.Kind == "TOTAL" && p.Stat == "OVER" && p.GameId == m.GameId);
                    return (object)O("game", m.Game, "fav", m.Team, "p", R3(m.P), "won", m.Y >= 1, "marginModel", mm != null ? R1(mm.Mean) : 0, "marginActual", mm != null ? R1(mm.Actual) : 0, "home", mm != null ? mm.Team : "",
                        "totalModel", mt != null ? R1(mt.Mean) : 0, "totalDk", to != null ? to.Line : 0, "totalActual", mt != null ? R1(mt.Actual) : 0);
                }).ToList();
                return (object)O("date", dt, "games", ml.Count, "favWon", ml.Count(x => x.Y >= 1), "props", pl.Count, "propPred", pl.Count > 0 ? R3(pl.Average(x => x.P)) : 0, "propObs", pl.Count > 0 ? R3(pl.Average(x => x.Y)) : 0, "rows", gamesRows);
            }).ToList();

            // audit on the walk-forward backtest
            object audit = null, tiers = null;
            string btPath = Program.P(@"ledger\backtest_latest.csv");
            if (File.Exists(btPath))
            {
                var bt = MakeSrc("Backtest", Pred.Load(btPath), Bank.Games);
                var props = PropRows(bt);
                var rel = Grader.Reliability(props).Where(b => b[2] > 0).Select(b => (object)O("lo", b[0], "hi", b[1], "n", (int)b[2], "pred", R3(b[3]), "obs", R3(b[4]))).ToList();
                var segs = PropSegments(bt).Select(s => (object)O("group", s.Group, "name", s.Name, "n", s.N, "pred", R3(s.MeanP), "obs", R3(s.Obs), "skill", R3(s.Skill))).ToList();
                var conf = props.Where(p => p.P >= 0.8).ToList();
                var causes = new[] { "minutes short", "minutes surplus", "blowout", "normal minutes" }.Select(c => (object)O("cause", c, "miss", conf.Count(p => p.Y < 1 && Cause(CtxOf(bt, p)) == c), "hit", conf.Count(p => p.Y >= 1 && Cause(CtxOf(bt, p)) == c))).ToList();
                var sgp = bt.Preds.Where(p => p.Kind == "SGP" && Gradable(p) && !double.IsNaN(p.Mean)).GroupBy(p => p.Tag).Select(gr => (object)O("tag", gr.Key, "n", gr.Count(), "joint", R3(gr.Average(x => x.P)), "indep", R3(gr.Average(x => x.Mean)), "obs", R3(gr.Average(x => x.Y)))).ToList();
                var ml = bt.Preds.Where(p => p.Kind == "ML" && Gradable(p) && !double.IsNaN(p.PMarket)).ToList();
                var gi = bt.G.Values.Where(x => !double.IsNaN(x.Margin) && !double.IsNaN(x.SprH) && !double.IsNaN(x.MktTotal)).ToList();
                var means = bt.C.Values.Where(c => !double.IsNaN(c.ProjPts) && !double.IsNaN(c.ActPts)).ToList();
                audit = O("games", bt.G.Count(x => !double.IsNaN(x.Value.Margin)), "dates", bt.Preds.Select(p => p.Date).Distinct().Count(), "props", props.Count,
                    "propPred", R3(props.Average(p => p.P)), "propObs", R3(props.Average(p => p.Y)), "confN", conf.Count, "confHit", R3(conf.Average(p => p.Y)),
                    "mlBrier", R3(ml.Average(p => (p.P - p.Y) * (p.P - p.Y))), "mlBrierMkt", R3(ml.Average(p => (p.PMarket - p.Y) * (p.PMarket - p.Y))),
                    "marginMae", R1(gi.Average(x => Math.Abs(x.Margin - x.ModelMargin))), "marginMaeMkt", R1(gi.Average(x => Math.Abs(x.Margin + x.SprH))),
                    "totalMae", R1(gi.Average(x => Math.Abs(x.Total - x.ModelTotal))), "totalMaeMkt", R1(gi.Average(x => Math.Abs(x.Total - x.MktTotal))),
                    "blowoutShare", R3(gi.Count(x => Math.Abs(x.Margin) >= 20) / (double)Math.Max(1, gi.Count)),
                    "ptsMae", R1(means.Average(c => Math.Abs(c.ProjPts - c.ActPts))), "minMae", R1(means.Average(c => Math.Abs(c.ProjMin - c.ActMin))),
                    "reliability", rel, "segments", segs, "causes", causes, "sgp", sgp);
                // upset risk: how often the model's favorite actually won, by how strongly it was favored (every backtest game, out of sample)
                var favs = bt.Preds.Where(p => p.Kind == "ML" && Gradable(p)).GroupBy(p => p.Date + "|" + p.GameId).Select(gr => gr.OrderByDescending(x => x.P).First()).ToList();
                Func<double, double, object> band = (lo, hi) =>
                {
                    var b = favs.Where(x => x.P >= lo && x.P < hi).ToList();
                    return O("lo", lo, "hi", hi, "n", b.Count, "won", (int)b.Sum(x => x.Y), "exp", R1(b.Sum(x => x.P)));
                };
                // sides the model liked at 5%+ expected value against the DraftKings price, and how they actually paid
                Func<double, double> pay = o => o > 0 ? o / 100 : 100 / -o;
                var edges = bt.Preds.Where(p => p.Kind == "ML" && Gradable(p) && !double.IsNaN(p.Odds) && p.Odds != 0 && p.P * pay(p.Odds) - (1 - p.P) >= 0.05).ToList();
                tiers = O("games", favs.Count, "bands", new List<object> { band(0.75, 1.01), band(0.65, 0.75), band(0.55, 0.65), band(0, 0.55) }, "parlay", band(0.70, 1.01),
                    "edges", O("n", edges.Count, "won", (int)edges.Sum(x => x.Y), "exp", R1(edges.Sum(x => x.P)), "units", R1(edges.Sum(x => x.Y >= 1 ? pay(x.Odds) : -1))));
            }

            // season trends, the last graded slate and the favorites' record: information panels that must never stop a slate run
            object trends = null, review = null, favRecord = null;
            try
            {
                var outTonight = new HashSet<string>(Program.TeamPlayers.Values.SelectMany(l => l).Where(x => x.Status == "OUT").Select(x => x.Pid));
                var tonightP = new Dictionary<string, double>();
                foreach (var p in Analysis.Players.Where(x => x.MeanMin >= 10))
                    foreach (var st in Favorites.Stats) foreach (int rung in Favorites.Ladder[st]) tonightP[p.P.Pid + "|" + st + "|" + rung] = Favorites.DistOf(p, st).PAtLeast(rung);
                trends = Trends.Json(outTonight, tonightP);
            }
            catch (Exception e) { Program.Warnings.Add("Season trends not exported: " + e.Message); }
            try { review = ReviewJson(); } catch (Exception e) { Program.Warnings.Add("Last-slate review not exported: " + e.Message); }
            try
            {
                Func<List<Pred>, object> rec = l => O("n", l.Count, "hit", l.Count(x => x.Y >= 1), "pred", l.Count > 0 ? R3(l.Average(x => x.P)) : 0);
                var lf = live.Where(p => p.Kind == "FAV" && !p.Void && !double.IsNaN(p.Y)).ToList();
                var bf = File.Exists(btPath) ? Pred.Load(btPath).Where(p => p.Kind == "FAV" && !p.Void && !double.IsNaN(p.Y)).ToList() : new List<Pred>();
                favRecord = O("live", rec(lf), "liveBold", rec(lf.Where(p => p.Tag == "fav_bold").ToList()), "backtest", rec(bf), "backtestBold", rec(bf.Where(p => p.Tag == "fav_bold").ToList()),
                    "byStat", Favorites.Stats.Select(st => (object)O("stat", st, "live", rec(lf.Where(p => p.Stat == st).ToList()), "backtest", rec(bf.Where(p => p.Stat == st).ToList()))).ToList(), "min", S.FavMin);
            }
            catch (Exception e) { Program.Warnings.Add("Favorites record not exported: " + e.Message); }

            string injuryReport = InjuryReportUsed(d);
            var data = O(
                "meta", O("slate", d.ToString("yyyy-MM-dd"), "slateLabel", d.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture), "built", DateTime.Now.ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture),
                    "injuryReport", injuryReport,
                    "sims", Program.NSIM, "bankThrough", Bank.BuiltThrough, "bankGames", Bank.Games.Count, "bankPlayers", Bank.Players.Values.Count(p => p.Gp > 0),
                    "recal", S.LastRecalibration, "settings", O("marketWeight", S.MarketWeight, "alpha", S.Alpha, "sigmaEff", S.SigmaEff, "sigmaPace", S.SigmaPace, "sigmaMin", S.SigmaMin,
                        "csWeight", S.CsWeight, "minUsageSlope", S.MinUsageSlope, "matchup", S.MatchupStrength, "gamma", Program.Gamma, "hca", R1(Program.HCA))),
                "games", games,
                "bestSgp", best == null ? null : O("game", Notes.GameOf(best), "sgp", ComboJson(best)),
                "erupt", eruptJson, "dud", dudJson,
                "bank", O("teams", bankTeams, "players", bankPlayers),
                "record", O("tags", tagRec, "slates", slates, "mlBrier", mlLive.Count > 0 ? (object)R3(mlLive.Average(p => (p.P - p.Y) * (p.P - p.Y))) : null, "mlBrierMkt", mlLive.Count > 0 ? (object)R3(mlLive.Average(p => (p.PMarket - p.Y) * (p.PMarket - p.Y))) : null),
                "audit", audit, "tiers", tiers, "safest", Safest.Json(), "banned", Ban.Json(),
                "trends", trends, "review", review, "favRecord", favRecord,
                "learn", O("minTrend", S.MinTrend, "ptsK", S.LearnPtsK, "astK", S.LearnAstK, "players", Learner.Count, "through", Learner.Through, "hotStart", S.HotStartWeight, "teamForm", S.TeamFormWeight));
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "window.NBA = " + ser.Serialize(data) + ";\n", new UTF8Encoding(false));
            return path;
        }
    }
}
