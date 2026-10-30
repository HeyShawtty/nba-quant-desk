using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NbaQuant
{
    class MainForm : Form
    {
        readonly string root;
        DateTimePicker dtp; NumericUpDown nudSims;
        Button btnRun, btnGrade, btnRecal, btnOpen, btnInj, btnOfficial, btnFolder;
        TextBox txtLog; Label status;
        DataGridView gridMl, gridPicks, gridSgp, gridTrack, gridStand, gridDd;
        TextBox calcP, calcOdds, calcBoost, calcStake; Label calcOut;
        bool ready;
        TabControl tabs;
        readonly string selfTestDate, selfTestDir;   // diagnostic: run a slate, screenshot every tab, exit

        public MainForm(string root, string selfTestDate = null, string selfTestDir = null)
        {
            this.root = root; this.selfTestDate = selfTestDate; this.selfTestDir = selfTestDir;
            Text = "NBA Quant Desk";
            Width = 1280; Height = 820; StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 4), WrapContents = false, AutoScroll = true };
            top.Controls.Add(new Label { Text = "Slate date", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            dtp = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110 };
            top.Controls.Add(dtp);
            top.Controls.Add(new Label { Text = "Simulations", AutoSize = true, Padding = new Padding(10, 6, 0, 0) });
            nudSims = new NumericUpDown { Minimum = 10000, Maximum = 300000, Increment = 10000, Value = 100000, Width = 90, ThousandsSeparator = true };
            top.Controls.Add(nudSims);
            btnRun = Btn("Run slate", RunClick, true); btnGrade = Btn("Grade results", GradeClick, false); btnRecal = Btn("Recalibrate", RecalClick, false);
            btnOpen = Btn("Open report", (s, e) => OpenPath(AppOps.ReportPath(dtp.Value.Date)), false);
            btnInj = Btn("Edit injuries", (s, e) => OpenPath(Program.OverridePath(dtp.Value.Date)), false);
            btnOfficial = Btn("Official injury report", (s, e) => Process.Start("https://official.nba.com/nba-injury-report-2026-27-season/"), false);
            btnFolder = Btn("Open folder", (s, e) => Process.Start(root), false);
            foreach (var b in new[] { btnRun, btnGrade, btnRecal, btnOpen, btnInj, btnOfficial, btnFolder }) top.Controls.Add(b);

            tabs = new TabControl { Dock = DockStyle.Fill };
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), WordWrap = false, BackColor = Color.White };
            tabs.TabPages.Add(Page("Log", txtLog));
            gridMl = Grid(); tabs.TabPages.Add(Page("Moneylines", gridMl));
            gridPicks = Grid(); tabs.TabPages.Add(Page("Top picks", gridPicks));
            gridStand = Grid(); tabs.TabPages.Add(Page("Standouts", gridStand));
            gridDd = Grid(); tabs.TabPages.Add(Page("DD & 3PT", gridDd));
            gridSgp = Grid();
            var sgpPage = Page("Boost SGP", gridSgp);
            sgpPage.Controls.Add(CalcPanel());
            tabs.TabPages.Add(sgpPage);
            gridTrack = Grid(); tabs.TabPages.Add(Page("Track record", gridTrack));

            status = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 3, 0, 0), BackColor = Color.FromArgb(240, 240, 240) };
            Controls.Add(tabs); Controls.Add(top); Controls.Add(status);
            Load += OnLoad;
        }

        Button Btn(string text, EventHandler h, bool primary)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(6, 0, 0, 0), Enabled = false };
            if (primary) { b.BackColor = Color.FromArgb(31, 56, 100); b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; }
            b.Click += h; return b;
        }
        static TabPage Page(string text, Control c) { var p = new TabPage(text); p.Controls.Add(c); return p; }
        static DataGridView Grid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
        }

        Control CalcPanel()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(8), BackColor = Color.FromArgb(255, 255, 230) };
            p.Controls.Add(new Label { Text = "Boost calculator:  model prob %", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            calcP = new TextBox { Width = 60, Text = "22" }; p.Controls.Add(calcP);
            p.Controls.Add(new Label { Text = "DK odds", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            calcOdds = new TextBox { Width = 60, Text = "+300" }; p.Controls.Add(calcOdds);
            p.Controls.Add(new Label { Text = "profit boost %", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            calcBoost = new TextBox { Width = 50, Text = "50" }; p.Controls.Add(calcBoost);
            p.Controls.Add(new Label { Text = "stake $", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            calcStake = new TextBox { Width = 50, Text = "5" }; p.Controls.Add(calcStake);
            calcOut = new Label { AutoSize = true, Padding = new Padding(12, 6, 0, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            p.Controls.Add(calcOut);
            foreach (var t in new[] { calcP, calcOdds, calcBoost, calcStake }) t.TextChanged += (s, e) => Calc();
            Calc();
            return p;
        }

        void Calc()
        {
            double p = U.D(calcP.Text, double.NaN) / 100, o = U.D(calcOdds.Text.Replace("+", ""), double.NaN), b = U.D(calcBoost.Text, 0) / 100, st = U.D(calcStake.Text, 5);
            if (double.IsNaN(p) || double.IsNaN(o) || Math.Abs(o) < 100 || p <= 0 || p >= 1) { calcOut.Text = "enter a probability and American odds"; return; }
            double profit = U.NetPayout(o) * (1 + b);
            double ev = p * profit - (1 - p);
            double be = 1 / (1 + profit);
            calcOut.Text = string.Format(CultureInfo.InvariantCulture, "boosted odds {0}   break-even {1:0.0}%   EV {2:+0.0%;-0.0%} per $1  ({3:+$0.00;-$0.00} on ${4:0.##})", U.Am(profit * 100), 100 * be, ev, ev * st, st);
        }

        void Log(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + s.Replace("\n", Environment.NewLine + "         ") + Environment.NewLine);
        }

        void Busy(bool busy, string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool, string>(Busy), busy, msg); return; }
            foreach (var b in new[] { btnRun, btnGrade, btnRecal, btnOpen, btnInj, btnOfficial, btnFolder }) b.Enabled = !busy && ready;
            status.Text = msg; UseWaitCursor = busy;
        }

        void Background(string msg, Action work, Action done)
        {
            Busy(true, msg);
            Task.Factory.StartNew(() =>
            {
                try { work(); BeginInvoke(new Action(() => { if (done != null) done(); Busy(false, "Ready"); })); }
                catch (Exception ex) { Log("ERROR: " + ex.Message); File.AppendAllText(Path.Combine(root, "error.log"), DateTime.Now + " " + ex + Environment.NewLine); Busy(false, "Error: " + ex.Message); }
            });
        }

        void OnLoad(object sender, EventArgs e)
        {
            Program.Log = Log;
            DateTime next = DateTime.Now.Date;
            Background("Loading data ...", () =>
            {
                Program.Init(root);
                next = AppOps.NextSlateDate();
                Log("Ready. Next slate: " + next.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture) + ". " + Ledger.Summary());
            }, () =>
            {
                ready = true; dtp.Value = next; nudSims.Value = Math.Max(nudSims.Minimum, Math.Min(nudSims.Maximum, Program.S.Sims)); ShowTrack();
                if (selfTestDate != null) SelfTest();
            });
        }

        void SelfTest()
        {
            dtp.Value = DateTime.ParseExact(selfTestDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var d = dtp.Value.Date; int sims = (int)nudSims.Value;
            Background("Self-test run ...", () => AppOps.RunSlate(d, sims), () =>
            {
                ShowResults();
                Directory.CreateDirectory(selfTestDir);
                for (int i = 0; i < tabs.TabPages.Count; i++)
                {
                    tabs.SelectedIndex = i; Application.DoEvents();
                    using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                        bmp.Save(Path.Combine(selfTestDir, "tab_" + i + "_" + tabs.TabPages[i].Text.Replace(" ", "") + ".png"));
                    }
                }
                Log("Self-test screenshots saved to " + selfTestDir);
                BeginInvoke(new Action(Close));
            });
        }

        void RunClick(object s, EventArgs e)
        {
            var d = dtp.Value.Date; int sims = (int)nudSims.Value; string path = null;
            Background("Running slate " + d.ToString("yyyy-MM-dd") + " ...", () => { path = AppOps.RunSlate(d, sims); }, () => { if (path != null) { ShowResults(); Log("Report: " + path); } });
        }
        void GradeClick(object s, EventArgs e)
        {
            var d = dtp.Value.Date; string res = "";
            Background("Grading " + d.ToString("yyyy-MM-dd") + " ...", () => { res = AppOps.Grade(d); }, () => { Log(res); ShowTrack(); });
        }
        void RecalClick(object s, EventArgs e)
        {
            if (MessageBox.Show("Re-run the walk-forward backtest over every completed game this season and refit the variance settings? This takes several minutes.", "Recalibrate", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            Background("Recalibrating (walk-forward backtest) ...", () => AppOps.Recalibrate(), () => Log("Recalibration saved to settings.ini. Re-run the slate to use it."));
        }

        void OpenPath(string path)
        {
            if (File.Exists(path)) Process.Start(path);
            else MessageBox.Show("Not found yet: " + path + "\nRun the slate first.", "NBA Quant Desk");
        }

        static void Fill(DataGridView g, string[] cols, IEnumerable<object[]> rows)
        {
            g.Columns.Clear(); g.Rows.Clear();
            foreach (var c in cols) g.Columns.Add(c.Replace(" ", ""), c);
            foreach (var r in rows) g.Rows.Add(r);
        }
        static string Pc(double p) { return double.IsNaN(p) ? "-" : (100 * p).ToString("0.0", CultureInfo.InvariantCulture) + "%"; }
        static string Od(double o) { return double.IsNaN(o) || Math.Abs(o) < 100 ? "-" : U.Am(o); }

        void ShowResults()
        {
            var ml = Report.MlRows();
            Fill(gridMl, new[] { "Team", "Opp", "Win %", "Model-only", "DK no-vig", "Fair ML", "DK ML", "EV @ DK", "Best ML", "Book", "EV @ best", "Confidence", "Notes" },
                ml.Select(r => new object[] { r.Team, (r.Home ? "vs " : "@ ") + r.Opp, Pc(r.P), Pc(r.PModel), Pc(r.PMkt), U.Am(U.FairAmerican(r.P)), Od(r.DkMl),
                    Math.Abs(r.DkMl) >= 100 ? Pc(U.EvPerDollar(r.P, r.DkMl)) : "-", Od(r.BestMl), r.BestBook, Math.Abs(r.BestMl) >= 100 ? Pc(U.EvPerDollar(r.P, r.BestMl)) : "-", r.Conf, r.ConfNote }));
            var picks = new List<object[]>();
            foreach (var r in ml.Take(3)) picks.Add(new object[] { "Most likely ML winner", r.Team + (r.Home ? " vs " : " @ ") + r.Opp, Pc(r.P), U.Am(U.FairAmerican(r.P)), "DK " + Od(r.DkMl) + ", EV " + (Math.Abs(r.DkMl) >= 100 ? Pc(U.EvPerDollar(r.P, r.DkMl)) : "-") });
            foreach (var t in Report.AllThresholds().Where(x => x.P <= 0.95 && x.Ps.MeanMin >= 24 && x.Ps.P.Status != "Q").OrderByDescending(x => x.P).Take(5))
                picks.Add(new object[] { "Most likely prop", t.Ps.P.Name + " " + t.Market + " " + t.Line + "+", Pc(t.P), U.Am(U.FairAmerican(t.P)), "confidence " + t.Ps.Conf });
            foreach (var t in Analysis.TeamQs.OrderByDescending(x => x.P100).Take(3)) picks.Add(new object[] { "100+ through Q3", t.Team + " vs " + t.Opp, Pc(t.P100), U.Am(U.FairAmerican(t.P100)), "expected " + t.Mean.ToString("0.0", CultureInfo.InvariantCulture) });
            foreach (var p in Analysis.Players.OrderByDescending(x => x.Pts.PAtLeast(25)).Take(5)) picks.Add(new object[] { "25+ points", p.P.Name + " (" + p.P.Team + ")", Pc(p.Pts.PAtLeast(25)), U.Am(U.FairAmerican(p.Pts.PAtLeast(25))), "mean " + p.Pts.Mean.ToString("0.0", CultureInfo.InvariantCulture) });
            foreach (var p in Analysis.Players.Where(x => x.MeanMin >= 15).OrderByDescending(x => x.Q3All).Take(5)) picks.Add(new object[] { "3+ every quarter", p.P.Name + " (" + p.P.Team + ")", Pc(p.Q3All), U.Am(U.FairAmerican(p.Q3All)), "" });
            foreach (var p in Analysis.Players.OrderByDescending(x => x.FirstFG).Take(5)) picks.Add(new object[] { "First field goal", p.P.Name + " (" + p.P.Team + ")", Pc(p.FirstFG), U.Am(U.FairAmerican(p.FirstFG)), "" });
            foreach (var e in Report.PropEdges().Where(x => !double.IsNaN(x.EvOver) || !double.IsNaN(x.EvUnder)).OrderByDescending(x => Math.Max(double.IsNaN(x.EvOver) ? -9 : x.EvOver, double.IsNaN(x.EvUnder) ? -9 : x.EvUnder)).Take(8))
            {
                bool over = double.IsNaN(e.EvUnder) || e.EvOver >= e.EvUnder;
                picks.Add(new object[] { "Prop vs book (unvalidated)", e.Ps.P.Name + " " + e.L.Stat + (over ? " over " : " under ") + e.L.Line.ToString(CultureInfo.InvariantCulture) + " " + Od(over ? e.L.OverOdds : e.L.UnderOdds) + " " + e.L.Book, Pc(over ? e.POver : 1 - e.POver), "", "EV " + Pc(over ? e.EvOver : e.EvUnder) });
            }
            Fill(gridPicks, new[] { "Category", "Selection", "Probability", "Fair odds", "Note" }, picks);
            Func<double, string> f1 = v => double.IsNaN(v) ? "-" : v.ToString("0.0", CultureInfo.InvariantCulture);
            Func<double, string> sg = v => (v >= 0 ? "+" : "") + v.ToString("0.0", CultureInfo.InvariantCulture);
            var forms = Report.Forms();
            var stand = forms.Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(15).Select(x => new { tag = "Rising", f = x })
                .Concat(forms.Where(x => x.Score < 0).OrderBy(x => x.Score).Take(8).Select(x => new { tag = "Falling", f = x }))
                .Select(o => new object[] { o.tag, o.f.Ps.P.Name, o.f.Ps.P.Team + " vs " + o.f.Ps.Opp, o.f.Gp, f1(o.f.Ppg) + " / " + f1(o.f.Rpg) + " / " + f1(o.f.Apg) + " / " + f1(o.f.Tpm),
                    f1(o.f.Ps.Pts.Mean) + " / " + f1(o.f.Ps.Reb.Mean) + " / " + f1(o.f.Ps.Ast.Mean) + " / " + f1(o.f.Ps.Tpm.Mean),
                    sg(o.f.DPts) + " / " + sg(o.f.DReb) + " / " + sg(o.f.DAst) + " / " + sg(o.f.DTpm), o.f.Lead, Pc(o.f.Ps.DD), o.f.Why });
            Fill(gridStand, new[] { "", "Player", "Matchup", "GP", "Season P/R/A/3PM", "Projected P/R/A/3PM", "Gap", "Lead stat", "P(DD)", "Why" }, stand);
            var dd = Analysis.Players.Where(p => p.MeanMin >= 15 && p.P.Status != "OUT").OrderByDescending(p => p.DD).Take(15)
                .Select(p => new object[] { "Double-double", p.P.Name + " (" + p.P.Team + ") vs " + p.Opp, Pc(p.DD), U.Am(U.FairAmerican(Math.Max(0.001, p.DD))), f1(p.Pts.Mean) + " pts / " + f1(p.Reb.Mean) + " reb / " + f1(p.Ast.Mean) + " ast", p.P.MatchupNote });
            var th = Analysis.Players.Where(p => p.MeanMin >= 15 && p.P.Status != "OUT").OrderByDescending(p => p.Tpm.Mean).Take(20)
                .Select(p => new object[] { "Threes", p.P.Name + " (" + p.P.Team + ") vs " + p.Opp, "2+ " + Pc(p.Tpm.PAtLeast(2)) + " | 3+ " + Pc(p.Tpm.PAtLeast(3)) + " | 4+ " + Pc(p.Tpm.PAtLeast(4)), "3+ " + U.Am(U.FairAmerican(Math.Max(0.001, p.Tpm.PAtLeast(3)))), "proj " + f1(p.Tpm.Mean) + " made", p.P.MatchupNote });
            Fill(gridDd, new[] { "Market", "Player", "Probability", "Fair odds", "Projection", "Matchup" }, dd.Concat(th));
            var best = Notes.BestSgp();
            var sgps = new List<object[]>();
            foreach (var g in Program.Games)
                foreach (var c in Analysis.BoostSgp[g])
                    sgps.Add(new object[] { c == best ? "BEST" : "", g.Def.Away + " @ " + g.Def.Home, string.Join(" + ", c.Legs.Select(l => l.Desc)), Pc(c.Joint), U.Am(U.FairAmerican(c.Joint)), c.Lift.ToString("0.00", CultureInfo.InvariantCulture), Pc(c.Joint * 4.5 - (1 - c.Joint)) });
            Fill(gridSgp, new[] { "", "Game", "Legs", "Joint prob", "Fair odds", "Lift", "EV if DK shows +300 (50% boost)" }, sgps.OrderByDescending(r => (string)r[0]));
            if (best != null) calcP.Text = (100 * best.Joint).ToString("0.0", CultureInfo.InvariantCulture);
            ShowTrack();
        }

        void ShowTrack()
        {
            var graded = Ledger.LoadAll();
            var rows = Grader.Summarize(graded).Where(s => !s.Group.StartsWith("PLAYER ")).Select(s => new object[] { s.Group, s.N, Pc(s.MeanP), Pc(s.HitRate), s.Brier.ToString("0.000", CultureInfo.InvariantCulture), double.IsNaN(s.BrierMkt) ? "" : s.BrierMkt.ToString("0.000", CultureInfo.InvariantCulture) }).ToList();
            foreach (var p in graded.Where(x => x.Tag != null && (x.Tag.StartsWith("top") || x.Tag == "best_sgp")).OrderByDescending(x => x.Date))
                rows.Add(new object[] { p.Date + " " + p.Tag, 1, Pc(p.P), p.Void ? "VOID" : p.Y >= 1 ? "HIT" : "MISS", p.Selection, "" });
            Fill(gridTrack, new[] { "Group / pick", "N", "Predicted", "Hit rate / result", "Brier / selection", "Market Brier" }, rows);
        }
    }
}
