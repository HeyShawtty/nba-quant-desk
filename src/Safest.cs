using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NbaQuant
{
    // Top 3 safest picks: the model's three strongest moneyline favorites in each tip window (evening: tips before 9 PM ET,
    // night: 9 PM ET and later) and across the whole slate. Every slate run re-prices the games that have not tipped on the
    // newest official injury report; a game that has tipped keeps the numbers from its last pre-tip run (ledger\safest_DATE.csv),
    // so a window's list is final once its last game has been priced on the report just before its tip.
    static class Safest
    {
        public class Row
        {
            public string GameId, Game, StartUtc, Tip, Window, Team, Opp, At, Report, PricedAt, Ticket;
            public double P, Odds = double.NaN, Ev = double.NaN, TicketP = double.NaN, TicketFair = double.NaN;
            public bool Complete;
        }

        const string Header = "game_id,game,start_utc,tip,window,team,opp,at,p,odds,ev,report,complete,priced_at,ticket,ticket_p,ticket_fair";
        static readonly TimeZoneInfo Et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        public static List<Row> Rows = new List<Row>();

        static string F(double v) { return double.IsNaN(v) ? "" : v.ToString("R", CultureInfo.InvariantCulture); }
        static string Q(string s) { s = s ?? ""; return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "'") + "\"" : s; }
        public static string FilePath(DateTime d) { return Program.P(@"ledger\safest_" + d.ToString("yyyy-MM-dd") + ".csv"); }

        static DateTime StartEt(string utc)
        {
            DateTime dt;
            if (!DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) return DateTime.MinValue;
            return TimeZoneInfo.ConvertTimeFromUtc(dt, Et);
        }
        static string WindowOf(string utc) { var t = StartEt(utc); return t != DateTime.MinValue && t.Hour >= 21 ? "night" : "evening"; }

        static List<Row> Load(string path)
        {
            if (!File.Exists(path)) return new List<Row>();
            return U.ReadCsv(path).Skip(1).Where(r => r.Length >= 17).Select(r => new Row
            {
                GameId = r[0], Game = r[1], StartUtc = r[2], Tip = r[3], Window = r[4], Team = r[5], Opp = r[6], At = r[7], P = U.D(r[8], double.NaN), Odds = U.D(r[9], double.NaN), Ev = U.D(r[10], double.NaN),
                Report = r[11], Complete = r[12] == "1", PricedAt = r[13], Ticket = r[14], TicketP = U.D(r[15], double.NaN), TicketFair = U.D(r[16], double.NaN)
            }).ToList();
        }
        static void Save(string path, List<Row> rows)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[] { Header }.Concat(rows.Select(x => string.Join(",", new[] { x.GameId, Q(x.Game), x.StartUtc, Q(x.Tip), x.Window, x.Team, x.Opp, x.At, F(x.P), F(x.Odds), F(x.Ev),
                Q(x.Report), x.Complete ? "1" : "0", x.PricedAt, Q(x.Ticket), F(x.TicketP), F(x.TicketFair) }))), new UTF8Encoding(false));
        }

        // after a live run: refresh the priced games, keep the tipped ones, pick the top 3s and tag their moneyline
        // predictions for grading (top3_ml = the slate's best three, safe3_evening / safe3_night = the rest of a window's three)
        public static void Update(DateTime d, List<Pred> preds, string injuryReport)
        {
            string path = FilePath(d);
            var rows = Load(path).ToDictionary(r => r.GameId, r => r);
            var events = Store.Events(d).ToDictionary(e => e.Id, e => e);
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            bool complete = injuryReport != null && injuryReport.Contains("All listed teams submitted");
            foreach (var g in Program.Games)
            {
                bool home = g.WinH >= 0.5; var dk = g.Def.DK; EventRef ev; events.TryGetValue(g.Def.EspnId, out ev);
                double odds = dk != null && dk.HasMl ? (home ? dk.MlH : dk.MlA) : double.NaN;
                var four = Analysis.Best4(g, 0.30, 1.0, 1).FirstOrDefault();
                rows[g.Def.EspnId] = new Row
                {
                    GameId = g.Def.EspnId, Game = g.Def.Away + " @ " + g.Def.Home, StartUtc = ev != null ? ev.StartUtc : "", Tip = g.Def.Tip ?? "", Window = WindowOf(ev != null ? ev.StartUtc : ""),
                    Team = home ? g.Def.Home : g.Def.Away, Opp = home ? g.Def.Away : g.Def.Home, At = home ? "vs" : "@", P = home ? g.WinH : 1 - g.WinH,
                    Odds = odds, Ev = double.IsNaN(odds) ? double.NaN : U.EvPerDollar(home ? g.WinH : 1 - g.WinH, odds),
                    Report = injuryReport ?? "", Complete = complete, PricedAt = now,
                    Ticket = four == null ? "" : string.Join(" + ", four.Legs.Select(l => l.Desc)), TicketP = four == null ? double.NaN : four.Joint, TicketFair = four == null ? double.NaN : U.FairAmerican(four.Joint)
                };
            }
            // games already under way when the board was first built: take their last saved pre-tip moneyline
            foreach (var grp in preds.Where(p => p.Kind == "ML" && !rows.ContainsKey(p.GameId)).GroupBy(p => p.GameId))
            {
                var best = grp.OrderByDescending(p => p.P).First(); EventRef ev; events.TryGetValue(best.GameId, out ev);
                var sides = (best.Game ?? "").Split(new[] { " @ " }, StringSplitOptions.None);
                string opp = sides.Length == 2 ? (best.Team == sides[1] ? sides[0] : sides[1]) : "";
                rows[best.GameId] = new Row
                {
                    GameId = best.GameId, Game = best.Game, StartUtc = ev != null ? ev.StartUtc : "", Tip = ev != null ? StartEt(ev.StartUtc).ToString("h:mm tt", CultureInfo.InvariantCulture) + " ET" : "",
                    Window = WindowOf(ev != null ? ev.StartUtc : ""), Team = best.Team, Opp = opp, At = sides.Length == 2 && best.Team == sides[1] ? "vs" : "@", P = best.P, Odds = best.Odds,
                    Ev = double.IsNaN(best.Odds) || best.Odds == 0 ? double.NaN : U.EvPerDollar(best.P, best.Odds), Report = "an earlier run (report not recorded)", PricedAt = ""
                };
            }
            Rows = rows.Values.OrderBy(r => r.StartUtc).ThenBy(r => r.Game).ToList();
            Save(path, Rows);

            var all = Top(Rows); var picked = new Dictionary<string, string>();
            foreach (var w in new[] { "evening", "night" }) foreach (var r in Top(Rows.Where(x => x.Window == w))) picked[r.GameId] = "safe3_" + w;
            foreach (var r in all) picked[r.GameId] = "top3_ml";
            foreach (var p in preds.Where(x => x.Kind == "ML"))
            {
                if (p.Tag == "top3_ml" || (p.Tag ?? "").StartsWith("safe3_")) p.Tag = "";
                string tag; var r = Rows.FirstOrDefault(x => x.GameId == p.GameId);
                if (r != null && p.Team == r.Team && picked.TryGetValue(p.GameId, out tag)) p.Tag = tag;
            }
        }

        static List<Row> Top(IEnumerable<Row> rows) { return rows.OrderByDescending(r => r.P).Take(3).ToList(); }

        // locked: the game has tipped (its numbers are from its last pre-tip run); final: priced 40 minutes or less before tip
        static string Status(Row r)
        {
            if (!Program.Games.Any(g => g.Def.EspnId == r.GameId)) return "locked";
            DateTime at, start = StartEt(r.StartUtc);
            if (DateTime.TryParseExact(r.PricedAt, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at) && start != DateTime.MinValue && at >= start.AddMinutes(-40)) return "final";
            return "provisional";
        }

        static string Clock(string pricedAt)
        {
            DateTime at;
            return DateTime.TryParseExact(pricedAt, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at) ? at.ToString("h:mm tt", CultureInfo.InvariantCulture) : "";
        }

        static object Pick(Row r)
        {
            var start = StartEt(r.StartUtc);
            return new Dictionary<string, object> {
                { "game", r.Game }, { "team", r.Team }, { "opp", r.Opp }, { "at", r.At }, { "tip", r.Tip }, { "window", r.Window }, { "p", Math.Round(r.P, 3) },
                { "ml", double.IsNaN(r.Odds) ? null : (object)r.Odds }, { "ev", double.IsNaN(r.Ev) ? null : (object)Math.Round(r.Ev, 3) },
                { "status", Status(r) }, { "report", r.Report }, { "complete", r.Complete }, { "pricedAt", Clock(r.PricedAt) },
                { "repriceAt", start == DateTime.MinValue ? "" : start.AddMinutes(-25).ToString("h:mm tt", CultureInfo.InvariantCulture) },
                { "ticket", string.IsNullOrEmpty(r.Ticket) ? null : new Dictionary<string, object> { { "legs", r.Ticket.Split(new[] { " + " }, StringSplitOptions.None).ToList() }, { "joint", Math.Round(r.TicketP, 3) }, { "fair", Math.Round(r.TicketFair) } } } };
        }

        // the board for the website, with the live record of the products it is built on
        public static object Json()
        {
            if (Rows.Count == 0) return null;
            Func<string, string, string, object> win = (key, label, range) =>
            {
                var picks = Top(Rows.Where(r => r.Window == key)).ToList();
                return new Dictionary<string, object> { { "key", key }, { "label", label }, { "range", range }, { "games", Rows.Count(r => r.Window == key) },
                    { "final", picks.Count > 0 && Rows.Where(r => r.Window == key).All(r => Status(r) != "provisional") }, { "picks", picks.Select(Pick).ToList() } };
            };
            var live = Ledger.LoadAll().Where(p => !p.Void && !double.IsNaN(p.Y)).ToList();
            Func<IEnumerable<Pred>, object> rec = xs => { var l = xs.ToList(); return new Dictionary<string, object> { { "n", l.Count }, { "hits", (int)l.Sum(x => x.Y) }, { "pred", l.Count > 0 ? Math.Round(l.Average(x => x.P), 3) : 0 } }; };
            var favs = live.Where(p => p.Kind == "ML").GroupBy(p => p.Date + "|" + p.GameId).Select(gr => gr.OrderByDescending(x => x.P).First()).Where(x => x.P >= 0.70);
            return new Dictionary<string, object> {
                { "windows", new List<object> { win("evening", "Evening", "tips before 9 PM ET"), win("night", "Night", "tips 9 PM ET and later") } },
                { "overall", Top(Rows).Select(Pick).ToList() }, { "overallFinal", Rows.All(r => Status(r) != "provisional") },
                { "record", new Dictionary<string, object> { { "top3", rec(live.Where(p => p.Tag == "top3_ml")) }, { "safe3", rec(live.Where(p => p.Tag == "top3_ml" || (p.Tag ?? "").StartsWith("safe3_"))) },
                    { "fav70", rec(favs) }, { "four", rec(live.Where(p => p.Tag == "sgp4_safest")) } } } };
        }
    }
}
