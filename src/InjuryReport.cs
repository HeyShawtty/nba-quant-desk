using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NbaQuant
{
    // Official NBA injury report -> overrides\injuries_DATE.csv, so cloud runs apply the same statuses a person would.
    // Finds the newest PDF for the date (fixed 15-minute addresses), converts it with pdftotext -layout (poppler) and
    // reads the player rows (columns are separated by runs of spaces in layout mode).
    static class InjuryReport
    {
        static readonly Dictionary<string, string> Teams = new Dictionary<string, string>
        {
            { "Atlanta Hawks", "ATL" }, { "Boston Celtics", "BOS" }, { "Brooklyn Nets", "BKN" }, { "Charlotte Hornets", "CHA" }, { "Chicago Bulls", "CHI" },
            { "Cleveland Cavaliers", "CLE" }, { "Dallas Mavericks", "DAL" }, { "Denver Nuggets", "DEN" }, { "Detroit Pistons", "DET" }, { "Golden State Warriors", "GS" },
            { "Houston Rockets", "HOU" }, { "Indiana Pacers", "IND" }, { "LA Clippers", "LAC" }, { "Los Angeles Lakers", "LAL" }, { "Memphis Grizzlies", "MEM" },
            { "Miami Heat", "MIA" }, { "Milwaukee Bucks", "MIL" }, { "Minnesota Timberwolves", "MIN" }, { "New Orleans Pelicans", "NO" }, { "New York Knicks", "NY" },
            { "Oklahoma City Thunder", "OKC" }, { "Orlando Magic", "ORL" }, { "Philadelphia 76ers", "PHI" }, { "Phoenix Suns", "PHX" }, { "Portland Trail Blazers", "POR" },
            { "Sacramento Kings", "SAC" }, { "San Antonio Spurs", "SA" }, { "Toronto Raptors", "TOR" }, { "Utah Jazz", "UTAH" }, { "Washington Wizards", "WSH" }
        };
        static readonly Dictionary<string, Tuple<string, double>> Status = new Dictionary<string, Tuple<string, double>>
        {
            { "Out", Tuple.Create("OUT", 0.0) }, { "Doubtful", Tuple.Create("Q", 0.25) }, { "Questionable", Tuple.Create("Q", 0.5) },
            { "Probable", Tuple.Create("Q", 0.9) }, { "Available", Tuple.Create("AVAIL", 1.0) }
        };

        public static string Apply(DateTime d, Action<string> log)
        {
            string ds = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string dir = Program.P(@"data\cache\inj"); Directory.CreateDirectory(dir);
            string pdf = Path.Combine(dir, "report_" + ds + ".pdf"), txt = Path.Combine(dir, "report_" + ds + ".txt");
            if (File.Exists(txt)) File.Delete(txt);
            // reports are posted every 15 minutes at a fixed address; the listing page served to the cloud runner was hours
            // stale, so probe the addresses directly, newest first, from now (or the end of the day for past dates)
            var t = d.Date == DateTime.Now.Date ? DateTime.Now : d.Date.AddHours(23).AddMinutes(45);
            t = t.AddMinutes(-(t.Minute % 15)).AddSeconds(-t.Second);
            string label = null;
            using (var wc = new System.Net.WebClient())
            {
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                for (int k = 0; k < 96 && t.Date == d.Date && label == null; k++, t = t.AddMinutes(-15))
                {
                    string stamp = t.ToString("hh_mmtt", CultureInfo.InvariantCulture);
                    try { wc.DownloadFile("https://ak-static.cms.nba.com/referee/injury/Injury-Report_" + ds + "_" + stamp + ".pdf", pdf); label = t.ToString("h:mm tt", CultureInfo.InvariantCulture); }
                    catch (System.Net.WebException) { }
                }
            }
            if (label == null) return "Official injury report: no PDF posted yet for " + ds + "; ESPN statuses only.";
            var best = new { Label = label };
            string exe = Environment.GetEnvironmentVariable("PDFTOTEXT"); if (string.IsNullOrEmpty(exe)) exe = "pdftotext";
            var psi = new ProcessStartInfo(exe, "-layout \"" + pdf + "\" \"" + txt + "\"") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) { p.WaitForExit(60000); }
            if (!File.Exists(txt)) return "Official injury report: pdftotext produced no text; ESPN statuses only.";
            string note;
            var rows = Parse(File.ReadAllLines(txt), d, out note);
            string outPath = Program.P(@"overrides\injuries_" + ds + ".csv");
            var sb = new StringBuilder("team,name,status,p_play,note\n");
            sb.AppendLine("# Source: NBA official injury report " + d.ToString("MM/dd/yy", CultureInfo.InvariantCulture) + " " + best.Label + " ET (read automatically). " + note);
            sb.AppendLine("# status: OUT, Q (p_play: Doubtful 0.25, Questionable 0.5, Probable 0.9) or AVAIL.");
            foreach (var r in rows) sb.AppendLine(string.Join(",", r[0], r[1].Contains(",") ? "\"" + r[1] + "\"" : r[1], r[2], r[3], "Official: " + r[4].Replace(",", ";")));
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            return string.Format(CultureInfo.InvariantCulture, "Official injury report {0} ET: {1} rows written to {2}. {3}", best.Label, rows.Count, outPath, note);
        }

        // rows: team, "First Last", status, p_play, "Status (reason)"
        public static List<string[]> Parse(string[] lines, DateTime d, out string note)
        {
            string target = d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            string curDate = null, curTeam = null;
            var res = new List<string[]>(); var pending = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Replace('\f', ' ');
                var dm = Regex.Match(line, @"^\s*(\d{2}/\d{2}/\d{4})\b");
                if (dm.Success) curDate = dm.Groups[1].Value;
                foreach (var t in Teams) if (line.Contains(t.Key)) curTeam = t.Value;
                if (curDate != target || curTeam == null) continue;
                if (line.Contains("NOT YET SUBMITTED")) { if (!pending.Contains(curTeam)) pending.Add(curTeam); continue; }
                var cols = Regex.Split(line.Trim(), @"\s{2,}");
                for (int i = 0; i + 1 < cols.Length; i++)
                {
                    Tuple<string, double> st;
                    if (!Status.TryGetValue(cols[i + 1].Trim(), out st)) continue;
                    var nm = cols[i].Trim(); int comma = nm.IndexOf(", ", StringComparison.Ordinal);
                    if (comma <= 0) continue;
                    string name = nm.Substring(comma + 2).Trim() + " " + nm.Substring(0, comma).Trim();
                    string reason = i + 2 < cols.Length ? cols[i + 2].Trim() : "";
                    res.Add(new[] { curTeam, name, st.Item1, st.Item2.ToString("0.##", CultureInfo.InvariantCulture), cols[i + 1].Trim() + (reason.Length > 0 ? " (" + reason + ")" : "") });
                    break;
                }
            }
            note = pending.Count > 0 ? "Not yet submitted: " + string.Join(", ", pending) + "." : "All listed teams submitted.";
            return res;
        }
    }
}
