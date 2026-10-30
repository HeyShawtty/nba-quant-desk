using System;
using System.Collections.Generic;
using System.Linq;

namespace NbaQuant
{
    // What opponents do against a team this season, relative to league average (1.00 = average), shrunk toward 1.00.
    class DefProfile
    {
        public string Team; public int G;
        public double ThreeShare = 1, ThreePct = 1, TwoPct = 1, FtRate = 1, TovRate = 1, OrebAllowed = 1, AstRate = 1;
        public Dictionary<string, double> PosPts = new Dictionary<string, double> { { "G", 1 }, { "F", 1 }, { "C", 1 } };
        // raw (unshrunk) values for display
        public double Raw3Share, Raw3Pct, Raw2Pct, RawOreb, RawPtsPer100;
    }

    static partial class Program
    {
        public static Dictionary<string, DefProfile> Defense = new Dictionary<string, DefProfile>();
        public static DefProfile LeagueDef = new DefProfile { Team = "League" };

        class Acc { public double Fga, Fg3a, Fg3m, Fg2a, Fg2m, Fta, Tov, Oreb, OwnDreb, Ast, Fgm, Pts, Poss; public Dictionary<string, double[]> Pos = new Dictionary<string, double[]>(); public int G; }

        static void DefenseProfiles()
        {
            Defense = new Dictionary<string, DefProfile>();
            var acc = new Dictionary<string, Acc>(); var lg = new Acc();
            foreach (var g in Completed)
                foreach (var side in new[] { Tuple.Create(g.Home, g.Away), Tuple.Create(g.Away, g.Home) })
                {
                    string def = side.Item1, off = side.Item2;
                    var ob = g.Box.Where(b => b.Team == off && b.Played).ToList();
                    var db = g.Box.Where(b => b.Team == def && b.Played).ToList();
                    if (ob.Count == 0 || db.Count == 0) continue;
                    Acc a; if (!acc.TryGetValue(def, out a)) { a = new Acc(); acc[def] = a; }
                    foreach (var x in new[] { a, lg })
                    {
                        x.G++;
                        x.Fga += ob.Sum(b => b.Fga); x.Fg3a += ob.Sum(b => b.Tpa); x.Fg3m += ob.Sum(b => b.Tpm);
                        x.Fg2a += ob.Sum(b => b.Fga - b.Tpa); x.Fg2m += ob.Sum(b => b.Fgm - b.Tpm); x.Fta += ob.Sum(b => b.Fta);
                        x.Tov += ob.Sum(b => b.Tov); x.Oreb += ob.Sum(b => b.Oreb); x.OwnDreb += db.Sum(b => b.Dreb);
                        x.Ast += ob.Sum(b => b.Ast); x.Fgm += ob.Sum(b => b.Fgm); x.Pts += ob.Sum(b => b.Pts);
                        x.Poss += ob.Sum(b => b.Fga) - ob.Sum(b => b.Oreb) + ob.Sum(b => b.Tov) + 0.44 * ob.Sum(b => b.Fta);
                        foreach (var b in ob)
                        {
                            string grp = Data.Group(b.Pos);
                            double[] v; if (!x.Pos.TryGetValue(grp, out v)) { v = new double[2]; x.Pos[grp] = v; }
                            v[0] += b.Pts; v[1] += b.Min;
                        }
                    }
                }
            if (lg.G == 0) return;
            Func<Acc, double> s3 = x => x.Fg3a / x.Fga, p3 = x => x.Fg3m / Math.Max(1, x.Fg3a), p2 = x => x.Fg2m / Math.Max(1, x.Fg2a), ft = x => x.Fta / x.Fga,
                tov = x => x.Tov / x.Poss, oreb = x => x.Oreb / Math.Max(1, x.Oreb + x.OwnDreb), ast = x => x.Ast / Math.Max(1, x.Fgm);
            Func<Acc, string, double> pos = (x, gp) => { double[] v; return x.Pos.TryGetValue(gp, out v) && v[1] > 0 ? v[0] / v[1] : double.NaN; };
            LeagueDef.Raw3Share = s3(lg); LeagueDef.Raw3Pct = p3(lg); LeagueDef.Raw2Pct = p2(lg); LeagueDef.RawOreb = oreb(lg); LeagueDef.RawPtsPer100 = 100 * lg.Pts / lg.Poss;
            double k = S.MatchupShrinkGames;
            foreach (var kv in acc)
            {
                var a = kv.Value; double w = a.G / (a.G + k);
                Func<double, double, double> f = (raw, league) => double.IsNaN(raw) || league <= 0 ? 1 : 1 + (raw / league - 1) * w;
                var d = new DefProfile
                {
                    Team = kv.Key, G = a.G,
                    ThreeShare = f(s3(a), s3(lg)), ThreePct = f(p3(a), p3(lg)), TwoPct = f(p2(a), p2(lg)), FtRate = f(ft(a), ft(lg)),
                    TovRate = f(tov(a), tov(lg)), OrebAllowed = f(oreb(a), oreb(lg)), AstRate = f(ast(a), ast(lg)),
                    Raw3Share = s3(a), Raw3Pct = p3(a), Raw2Pct = p2(a), RawOreb = oreb(a), RawPtsPer100 = 100 * a.Pts / a.Poss
                };
                foreach (var gp in new[] { "G", "F", "C" }) d.PosPts[gp] = f(pos(a, gp), pos(lg, gp));
                Defense[kv.Key] = d;
            }
        }

        static double Str(double f) { return 1 + (f - 1) * S.MatchupStrength; }

        // Reshapes each player's inputs for tonight's opponent. Team scoring is still calibrated to the blended target
        // afterwards, so the profile mostly moves production between shot types, stats and players.
        static void ApplyMatchups(GameCtx ctx)
        {
            foreach (var side in new[] { Tuple.Create(ctx.Def.Home, ctx.Def.Away), Tuple.Create(ctx.Def.Away, ctx.Def.Home) })
            {
                DefProfile d; if (!Defense.TryGetValue(side.Item2, out d)) continue;
                foreach (var p in TeamPlayers[side.Item1])
                {
                    double shots = p.R2a + p.R3a;
                    double new3 = Math.Min(shots * 0.9, p.R3a * Str(d.ThreeShare));
                    p.R2a = Math.Max(0, shots - new3); p.R3a = new3;
                    p.P3 *= Str(d.ThreePct); p.P2 *= Str(d.TwoPct);
                    p.RTrip *= Str(d.FtRate); p.RTov *= Str(d.TovRate); p.RAst *= Str(d.AstRate);
                    p.Usage = p.R2a + p.R3a + p.RTrip + p.RTov;
                    double posF = Str(d.PosPts[p.Group]);
                    p.PickW = p.Usage * Math.Pow(p.Usage / 0.5, Gamma - 1) * posF;
                    var notes = new List<string>();
                    // describe the (shrunk) defensive tendency even when it is not applied, so the reader sees the matchup
                    Action<string, double> add = (label, f) => { double e = f - 1; if (Math.Abs(e) >= 0.03) notes.Add(string.Format(U.IC, "{0} {1:+0%;-0%}", label, e)); };
                    add("3PA share allowed", d.ThreeShare); add("3P% allowed", d.ThreePct); add("2P% allowed", d.TwoPct); add("FT rate allowed", d.FtRate);
                    add("turnovers forced", d.TovRate); add("assist rate allowed", d.AstRate); add(p.Group + " scoring allowed", d.PosPts[p.Group]);
                    p.MatchupNote = notes.Count == 0 ? "vs " + side.Item2 + ": no notable defensive tendencies (sample " + d.G + " games)" : "vs " + side.Item2 + " (" + d.G + " games): " + string.Join(", ", notes)
                        + (S.MatchupStrength <= 0 ? " [context only, not in the probabilities]" : S.MatchupStrength < 1 ? string.Format(U.IC, " [applied at {0:0%}]", S.MatchupStrength) : "");
                }
            }
        }

        static double OrebFactor(string defTeam) { DefProfile d; return Defense.TryGetValue(defTeam, out d) ? Str(d.OrebAllowed) : 1; }
    }
}
