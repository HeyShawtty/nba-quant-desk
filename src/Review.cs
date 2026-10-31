using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // Game-by-game review of a graded slate: what the model called right and wrong, player by player, and why.
    static partial class Report
    {
        class RvLine
        {
            public string Pid, Name, Team; public BoxLine B;
            public double PMin = double.NaN, PPts = double.NaN, PReb = double.NaN, PAst = double.NaN, P3 = double.NaN;
            public bool Projected { get { return !double.IsNaN(PMin); } }
            public double DPts { get { return B != null && B.Played && Projected ? B.Pts - PPts : double.NaN; } }
        }

        static string RvCause(RvLine x, double teamMargin)
        {
            var b = x.B;
            if (!x.Projected) return b != null && b.Played && b.Min >= 15 ? "not in the projection (injury replacement / rotation change)" : "";
            if (b == null || !b.Played) return x.PMin >= 12 ? "did not play (late scratch or coach's decision)" : "did not play";
            double dm = b.Min - x.PMin, dp = b.Pts - x.PPts;
            if (dm <= -6 && b.Pf >= 5) return string.Format(U.IC, "minutes short ({0:0} vs {1:0}): foul trouble ({2} fouls{3})", b.Min, x.PMin, b.Pf, b.Pf >= 6 ? ", fouled out" : "");
            if (dm <= -6) return Math.Abs(teamMargin) >= 15 ? string.Format(U.IC, "minutes short ({0:0} vs {1:0}): blowout", b.Min, x.PMin) : string.Format(U.IC, "minutes short ({0:0} vs {1:0}): fouls, injury or rotation", b.Min, x.PMin);
            if (dm >= 6) return string.Format(U.IC, "minutes surplus ({0:0} vs {1:0})", b.Min, x.PMin);
            if (dp >= 7) return string.Format(U.IC, "hot shooting ({0}-{1} FG, {2}-{3} 3PT)", b.Fgm, b.Fga, b.Tpm, b.Tpa);
            if (dp <= -7) return string.Format(U.IC, "cold shooting ({0}-{1} FG, {2}-{3} 3PT)", b.Fgm, b.Fga, b.Tpm, b.Tpa);
            return "on track";
        }

        public static string BuildReview(string date, string path)
        {
            Sheets = new List<Sheet>();
            var preds = Pred.Load(Program.P(@"ledger\graded_" + date + ".csv"));
            var txt = new StringBuilder();
            var ids = preds.Where(p => p.Kind == "ML").Select(p => p.GameId).Distinct().ToList();
            var vs = New("Slate verdict", "Game-by-game review: " + date, "Model = the published pre-tip simulation (75% DraftKings market, 25% independent model). Picks hit counts every tagged headline pick and saved parlay for the game.",
                14, 16, 10, 9, 9, 8, 22, 22, 9, 16, 10, 50, 60);
            Hdr(vs, "Game", "Final", "Model fav", "Win prob", "DK no-vig", "Result", "Margin home (model / DK / actual)", "Total (model / DK / actual)", "O/U", "Q1-Q3 away / home (P100+)", "Picks hit", "Right", "Wrong");
            var pl = New("Player lines", "Every rotation player: projection vs actual", "Projection = average over the simulations he plays in. Cause is assigned in order: did not play, minutes 6+ off, shooting 7+ points off, otherwise on track.",
                24, 6, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 52);
            var pk = New("Picks & parlays", "Every headline pick and saved parlay, leg by leg", null, 14, 20, 70, 9, 9, 50);
            var all = new List<Tuple<string, RvLine, double>>();
            int favWins = 0, nGames = 0, overs = 0, picksHit = 0, picksN = 0; double mTot = 0, mTotDk = 0, mMar = 0, mMarDk = 0;
            foreach (var id in ids)
            {
                var g = Store.Summary(id, true); if (g == null || !g.Completed) continue;
                nGames++;
                var gp = preds.Where(p => p.GameId == id).ToList();
                string label = g.Away + " @ " + g.Home;
                int margin = g.HomePts - g.AwayPts, total = g.HomePts + g.AwayPts;
                var ml = gp.Where(p => p.Kind == "ML").OrderByDescending(p => p.P).First();
                bool favWon = ml.Y >= 1; if (favWon) favWins++;
                var mm = gp.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "MARGIN"); var mt = gp.FirstOrDefault(p => p.Kind == "MEAN" && p.Stat == "TOTAL");
                var spH = gp.FirstOrDefault(p => p.Kind == "SPREAD" && p.Team == g.Home); var to = gp.FirstOrDefault(p => p.Kind == "TOTAL" && p.Stat == "OVER");
                double modelMargin = mm != null ? mm.Mean : double.NaN, dkMargin = spH != null ? -spH.Line : double.NaN, modelTotal = mt != null ? mt.Mean : double.NaN, dkTotal = to != null ? to.Line : double.NaN;
                mTot += Math.Abs(total - modelTotal); mTotDk += Math.Abs(total - dkTotal); mMar += Math.Abs(margin - modelMargin); mMarDk += Math.Abs(margin - dkMargin);
                bool over = total > dkTotal; if (over) overs++;
                var q3 = gp.Where(p => p.Kind == "TEAMQ3" && p.Line == 100).ToDictionary(p => p.Team, p => p.P);
                var tagged = gp.Where(p => !string.IsNullOrEmpty(p.Tag) && !p.Void && !double.IsNaN(p.Y) && (p.Tag.StartsWith("top") || p.Tag.StartsWith("sgp") || p.Tag == "best_sgp" || p.Tag == "boost_candidate")).ToList();
                int hits = tagged.Count(p => p.Y >= 1); picksHit += hits; picksN += tagged.Count;

                // players: projections (MEAN rows) joined to the final box
                var lines = new Dictionary<string, RvLine>();
                foreach (var p in gp.Where(p => p.Kind == "MEAN" && !string.IsNullOrEmpty(p.Pid)))
                {
                    RvLine x; if (!lines.TryGetValue(p.Pid, out x)) lines[p.Pid] = x = new RvLine { Pid = p.Pid, Name = p.Player, Team = p.Team };
                    if (p.Stat == "MIN") x.PMin = p.Mean; else if (p.Stat == "PTS") x.PPts = p.Mean; else if (p.Stat == "REB") x.PReb = p.Mean; else if (p.Stat == "AST") x.PAst = p.Mean; else if (p.Stat == "3PM") x.P3 = p.Mean;
                }
                foreach (var b in g.Box)
                {
                    RvLine x;
                    if (lines.TryGetValue(b.Pid, out x)) x.B = b;
                    else if (b.Played && b.Min >= 10) lines[b.Pid] = new RvLine { Pid = b.Pid, Name = b.Name, Team = b.Team, B = b };
                }
                Func<RvLine, double> tm = x => x.Team == g.Home ? margin : -margin;
                foreach (var x in lines.Values) all.Add(Tuple.Create(label, x, tm(x)));

                // right / wrong, in words
                var right = new List<string>(); var wrong = new List<string>();
                if (favWon) right.Add(string.Format(U.IC, "{0} won as the model's favorite ({1})", ml.Team, U.Pct(ml.P))); else wrong.Add(string.Format(U.IC, "{0} lost at {1}", ml.Team, U.Pct(ml.P)));
                if (Math.Abs(total - modelTotal) <= 6) right.Add(string.Format(U.IC, "total within {0:0} pts", Math.Abs(total - modelTotal)));
                else if (Math.Abs(total - modelTotal) >= 12) wrong.Add(string.Format(U.IC, "total {0} vs {1:0} projected ({2:+0;-0})", total, modelTotal, total - modelTotal));
                if (Math.Abs(margin - modelMargin) <= 5) right.Add("margin within 5");
                else if (Math.Abs(margin - modelMargin) >= 15) wrong.Add(string.Format(U.IC, "margin {0:+0;-0} vs {1:+0.0;-0.0} projected{2}", margin, modelMargin, Math.Abs(margin) >= 20 ? " (blowout)" : ""));
                var proj = lines.Values.Where(x => x.Projected && x.PMin >= 24 && x.B != null && x.B.Played).ToList();
                int close = proj.Count(x => Math.Abs(x.DPts) <= 4);
                if (proj.Count > 0) (close * 2 >= proj.Count ? right : wrong).Add(string.Format(U.IC, "{0} of {1} main players within 4 pts of projection", close, proj.Count));
                foreach (var x in lines.Values.Where(x => x.Projected && x.B != null && x.B.Played && Math.Abs(x.DPts) >= 8).OrderByDescending(x => Math.Abs(x.DPts)).Take(3))
                    wrong.Add(string.Format(U.IC, "{0} {1} pts vs {2:0.0} ({3})", x.Name, x.B.Pts, x.PPts, RvCause(x, tm(x))));
                foreach (var x in lines.Values.Where(x => !x.Projected && x.B != null && x.B.Played && x.B.Min >= 20)) wrong.Add(string.Format(U.IC, "{0} played {1:0} min unprojected ({2} pts)", x.Name, x.B.Min, x.B.Pts));
                foreach (var x in lines.Values.Where(x => x.Projected && x.PMin >= 15 && (x.B == null || !x.B.Played))) wrong.Add(string.Format(U.IC, "{0} projected {1:0} min but did not play", x.Name, x.PMin));
                if (hits > 0) right.Add(string.Format(U.IC, "{0} of {1} picks/parlays hit", hits, tagged.Count));

                var r = vs.Row();
                r.Add(Cell.Str(label, Styles.Bold)); r.Add(Cell.Str(string.Format(U.IC, "{0} {1} - {2} {3}{4}", g.Away, g.AwayPts, g.Home, g.HomePts, g.OT > 0 ? " OT" : "")));
                r.Add(Cell.Str(ml.Team)); r.Add(Cell.Num(ml.P, Styles.Pct)); r.Add(ANum(ml.PMarket, Styles.Pct)); r.Add(Cell.Str(favWon ? "RIGHT" : "WRONG", favWon ? Styles.Good : Styles.Bad));
                r.Add(Cell.Str(string.Format(U.IC, "{0:+0.0;-0.0} / {1:+0.0;-0.0} / {2:+0;-0}", modelMargin, dkMargin, margin)));
                r.Add(Cell.Str(string.Format(U.IC, "{0:0.0} / {1} / {2}", modelTotal, dkTotal, total))); r.Add(Cell.Str(over ? "Over" : "Under"));
                double qa, qh; q3.TryGetValue(g.Away, out qa); q3.TryGetValue(g.Home, out qh);
                r.Add(Cell.Str(string.Format(U.IC, "{0} ({1:0}%) / {2} ({3:0}%)", g.AwayQ123, 100 * qa, g.HomeQ123, 100 * qh)));
                r.Add(Cell.Str(hits + " / " + tagged.Count)); r.Add(Cell.Str(string.Join("; ", right), Styles.Wrap)); r.Add(Cell.Str(string.Join("; ", wrong), Styles.Wrap));
                txt.AppendLine(string.Format(U.IC, "=== {0}: {1} {2}-{3} {4} | fav {5} {6} {7} | margin model {8:+0.0;-0.0} DK {9:+0.0;-0.0} actual {10:+0;-0} | total model {11:0.0} DK {12} actual {13} | Q1-3 {14}/{15} | picks {16}/{17}",
                    label, g.Away, g.AwayPts, g.HomePts, g.Home, ml.Team, U.Pct(ml.P), favWon ? "RIGHT" : "WRONG", modelMargin, dkMargin, margin, modelTotal, dkTotal, total, g.AwayQ123, g.HomeQ123, hits, tagged.Count));
                txt.AppendLine("  RIGHT: " + string.Join("; ", right));
                txt.AppendLine("  WRONG: " + string.Join("; ", wrong));

                // player sheet section
                Section(pl, label + string.Format(U.IC, "  (final {0} {1} - {2} {3})", g.Away, g.AwayPts, g.Home, g.HomePts), 14);
                Hdr(pl, "Player", "Team", "Proj MIN", "Act MIN", "Proj PTS", "Act PTS", "PTS diff", "Proj REB", "Act REB", "Proj AST", "Act AST", "Proj 3PM", "Act 3PM", "Cause");
                foreach (var x in lines.Values.OrderBy(x => x.Team == g.Home).ThenByDescending(x => x.B != null && x.B.Played ? x.B.Min : -1).ThenByDescending(x => x.PMin))
                {
                    if (x.Projected && x.PMin < 8 && (x.B == null || !x.B.Played || x.B.Min < 8)) continue;
                    var b = x.B; bool played = b != null && b.Played;
                    var row = pl.Row();
                    row.Add(Cell.Str(x.Name)); row.Add(Cell.Str(x.Team));
                    row.Add(ANum(x.PMin, Styles.N1)); row.Add(played ? Cell.Num(b.Min, Styles.Int) : Cell.Str("DNP"));
                    row.Add(ANum(x.PPts, Styles.N1)); row.Add(played ? Cell.Num(b.Pts, Styles.Int) : Cell.Str("")); row.Add(ANum(x.DPts, Math.Abs(x.DPts) >= 7 ? (x.DPts > 0 ? Styles.Good : Styles.Bad) : Styles.N1));
                    row.Add(ANum(x.PReb, Styles.N1)); row.Add(played ? Cell.Num(b.Reb, Styles.Int) : Cell.Str(""));
                    row.Add(ANum(x.PAst, Styles.N1)); row.Add(played ? Cell.Num(b.Ast, Styles.Int) : Cell.Str(""));
                    row.Add(ANum(x.P3, Styles.N1)); row.Add(played ? Cell.Num(b.Tpm, Styles.Int) : Cell.Str(""));
                    row.Add(Cell.Str(RvCause(x, tm(x))));
                }

                // picks and parlays, leg by leg
                Section(pk, label, 6);
                Hdr(pk, "Game", "Tag", "Selection", "Model", "Result", "Detail");
                foreach (var p in tagged.OrderBy(p => p.Tag))
                {
                    string detail = "";
                    if (p.Kind == "SGP" && !string.IsNullOrEmpty(p.Legs))
                    {
                        var codes = p.Legs.Split(';'); var descs = p.Selection.Split(new[] { " + " }, StringSplitOptions.None);
                        var missed = new List<string>(); int legHits = 0;
                        for (int i = 0; i < codes.Length; i++)
                        {
                            var f = codes[i].Split('|');
                            double y = Grader.Resolve(f[0], f[0], f[1], f[2], U.D(f[3]), g);
                            if (y >= 1) legHits++; else missed.Add(i < descs.Length ? descs[i] : codes[i]);
                        }
                        detail = string.Format(U.IC, "{0}/{1} legs{2}", legHits, codes.Length, missed.Count > 0 ? "; missed: " + string.Join(", ", missed) : "");
                    }
                    else if (!double.IsNaN(p.Actual)) detail = "actual " + p.Actual.ToString("0", U.IC);
                    var row = pk.Row();
                    row.Add(Cell.Str(label)); row.Add(Cell.Str(p.Tag)); row.Add(Cell.Str(p.Selection, Styles.Wrap)); row.Add(Cell.Num(p.P, Styles.Pct));
                    row.Add(Cell.Str(p.Y >= 1 ? "HIT" : "MISS", p.Y >= 1 ? Styles.Good : Styles.Bad)); row.Add(Cell.Str(detail, Styles.Wrap));
                    if (p.Tag.StartsWith("top") || p.Tag == "best_sgp" || p.Tag.StartsWith("sgp4")) txt.AppendLine(string.Format(U.IC, "  [{0}] {1} ({2}) {3} {4}", p.Tag, p.Selection, U.Pct(p.P), p.Y >= 1 ? "HIT" : "MISS", detail));
                }
            }

            // slate-wide surprises and summary
            var su = New("Surprises", "Biggest surprises across the slate", null, 14, 24, 6, 9, 9, 9, 9, 50);
            Action<string, IEnumerable<Tuple<string, RvLine, double>>> list = (title, xs) =>
            {
                Section(su, title, 8); Hdr(su, "Game", "Player", "Team", "Proj MIN", "Act MIN", "Proj PTS", "Act PTS", "Cause");
                foreach (var t in xs)
                {
                    var x = t.Item2; bool played = x.B != null && x.B.Played; var row = su.Row();
                    row.Add(Cell.Str(t.Item1)); row.Add(Cell.Str(x.Name)); row.Add(Cell.Str(x.Team)); row.Add(ANum(x.PMin, Styles.N1)); row.Add(played ? Cell.Num(x.B.Min, Styles.Int) : Cell.Str("DNP"));
                    row.Add(ANum(x.PPts, Styles.N1)); row.Add(played ? Cell.Num(x.B.Pts, Styles.Int) : Cell.Str("")); row.Add(Cell.Str(RvCause(x, t.Item3)));
                }
            };
            var projPlayed = all.Where(t => t.Item2.Projected && t.Item2.B != null && t.Item2.B.Played).ToList();
            list("Biggest over-performances (points vs projection)", projPlayed.OrderByDescending(t => t.Item2.DPts).Take(12));
            list("Biggest under-performances (points vs projection)", projPlayed.OrderBy(t => t.Item2.DPts).Take(12));
            list("Not in the projection but played 15+ minutes", all.Where(t => !t.Item2.Projected && t.Item2.B != null && t.Item2.B.Played && t.Item2.B.Min >= 15).OrderByDescending(t => t.Item2.B.Min));
            list("Projected 12+ minutes but did not play", all.Where(t => t.Item2.Projected && t.Item2.PMin >= 12 && (t.Item2.B == null || !t.Item2.B.Played)));

            var main = projPlayed.Where(t => t.Item2.PMin >= 15).ToList();
            var causes = main.GroupBy(t => { var c = RvCause(t.Item2, t.Item3); return c.StartsWith("minutes short") ? "minutes short" : c.StartsWith("minutes surplus") ? "minutes surplus" : c.StartsWith("hot") ? "hot shooting" : c.StartsWith("cold") ? "cold shooting" : "on track"; })
                .ToDictionary(gr => gr.Key, gr => gr.Count());
            string head = string.Format(U.IC, "SLATE {0}: {1} games | model favorite won {2}/{1} | {3} overs, {4} unders vs DK | total MAE model {5:0.0} vs DK {6:0.0} | margin MAE model {7:0.0} vs DK {8:0.0} | headline picks & parlays {9}/{10} | players projected 15+ min: {11} | pts MAE {12:0.0}, within 5 pts {13} | causes: {14}",
                date, nGames, favWins, overs, nGames - overs, mTot / nGames, mTotDk / nGames, mMar / nGames, mMarDk / nGames, picksHit, picksN, main.Count, main.Average(t => Math.Abs(t.Item2.DPts)), U.Pct(main.Count(t => Math.Abs(t.Item2.DPts) <= 5) / (double)main.Count),
                string.Join(", ", causes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)));
            vs.Rows.Insert(2, new List<Cell> { Cell.Str(head, Styles.Wrap) });
            vs.Merges.Add("A3:M3");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Xlsx.Save(path, Sheets);
            File.WriteAllText(Program.P(@"ledger\review_" + date + ".txt"), head + Environment.NewLine + txt);
            return head;
        }
    }
}
