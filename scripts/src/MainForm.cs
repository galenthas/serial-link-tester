using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Windows.Forms;

namespace SerialRf {
    sealed class Metric : Panel {
        public readonly Label Title = new Label(), Value = new Label(), Detail = new Label();
        public Metric(string title, string detail) {
            BackColor = Theme.Surface; Dock = DockStyle.Fill; Margin = new Padding(5); Padding = new Padding(15,12,10,10);
            Title.Text = title; Title.Dock = DockStyle.Top; Title.Height = 22; Title.ForeColor = Theme.Muted;
            Value.Text = "—"; Value.Dock = DockStyle.Top; Value.Height = 42; Value.Font = new Font("Segoe UI", 25, FontStyle.Bold); Value.ForeColor = Theme.Navy;
            Detail.Text = detail; Detail.Dock = DockStyle.Fill; Detail.ForeColor = Theme.Muted; Detail.Font = new Font("Segoe UI", 8.5f);
            Controls.Add(Detail); Controls.Add(Value); Controls.Add(Title);
        }
    }
    sealed class LiveChart : Control {
        readonly List<double> values = new List<double>();
        public string Unit = "ms", Caption = "Last 120 seconds · valid packet latency";
        public LiveChart() { DoubleBuffered = true; BackColor = Theme.Surface; Dock = DockStyle.Fill; }
        public void Add(double value) { values.Add(value); if (values.Count > 120) values.RemoveAt(0); Invalidate(); }
        public void Reset() { values.Clear(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush text = new SolidBrush(Theme.Muted)) {
                g.DrawString(Caption, Font, text, 18, 12);
                if (values.Count < 2) { g.DrawString("Start a test to see your measurements here.", Font, text, 45, Height/2); return; }
                Rectangle plot = new Rectangle(66, 36, Math.Max(50, Width - 92), Math.Max(8, Height - 60));
                double high = 1; foreach (double v in values) if (!double.IsNaN(v)) high = Math.Max(high, v);
                high *= 1.15;
                using (Pen grid = new Pen(Theme.Border)) {
                    for (int i=0;i<=4;i++) {
                        float y = plot.Bottom - plot.Height*i/4f;
                        g.DrawLine(grid, plot.Left,y,plot.Right,y);
                        using(Font axisFont=new Font("Segoe UI",8))g.DrawString((high*i/4).ToString("0.#") + " " + Unit, axisFont,text,3,y-7);
                    }
                }
                using (Pen pen = new Pen(Theme.Teal,2.5f)) {
                    PointF? last = null;
                    for(int i=0;i<values.Count;i++) {
                        if (double.IsNaN(values[i])) { last=null; continue; }
                        PointF p = new PointF(plot.Left + i*plot.Width/(float)Math.Max(119,values.Count-1), plot.Bottom - (float)(values[i]/high)*plot.Height);
                        if(last.HasValue) g.DrawLine(pen,last.Value,p);
                        using(Brush dot=new SolidBrush(Theme.Teal))g.FillEllipse(dot,p.X-2,p.Y-2,4,4); last=p;
                    }
                }
                g.DrawString("History",Font,text,plot.Left,plot.Bottom+4);
                g.DrawString("1 sample / second",Font,text,Math.Max(plot.Left,plot.Right-130),plot.Bottom+4);
            }
        }
    }

    public sealed class MainForm : Form {
        readonly ComboBox setup = Combo(false), tx = Combo(false), rx = Combo(false), baud = Combo(true), mode = Combo(false), chartMode = Combo(false);
        readonly DarkNumber payload = Number(1,2048,64), seconds = Number(1,3600,60), load = Number(1,100,90), timeout = Number(100,60000,TestConfig.DefaultTimeoutMs);
        readonly TextBox note = new TextBox();
        readonly Label baudNote = new Label(), route = new Label(), status = new Label(), warningStatus = new Label(), elapsed = new Label(), scope = new Label();
        readonly Button start = Button("Start test",true), stop = Button("Stop",false), demo = Button("Try demo",false), save = Button("Export report",false), refresh = Button("Refresh ports",false);
        readonly Metric loss = new Metric("Pending packets","Finalized when reception ends"), corrupt = new Metric("Corruption","Payload / packet format errors"), speed = new Metric("Goodput","Verified payload only"), delay = new Metric("Mean latency","Application-to-application"), jitter = new Metric("Jitter","Consecutive delay variation"), valid = new Metric("Valid packets","Sequence + CRC + pattern");
        readonly RichTextBox details = new RichTextBox(), help = new RichTextBox();
        sealed class DirectionView {
            public readonly LiveChart Chart=new LiveChart();
            public long Bytes,LatencyCount;
            public double Elapsed,LatencySum,LiveRate;
            public void Reset() { Chart.Reset();Bytes=LatencyCount=0;Elapsed=LatencySum=LiveRate=0; }
        }
        readonly DirectionView[] directions={new DirectionView(),new DirectionView()};
        readonly TableLayoutPanel overview=new TableLayoutPanel();
        readonly DarkButton[] directionButtons={new DarkButton(),new DarkButton()};
        readonly Label[,] directionValues=new Label[2,6];
        readonly Label portLabels=new Label(),loadLabel=new Label();
        RowStyle routeRow;
        int selectedDirection;
        readonly DarkProgress progress = new DarkProgress();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolTip tips = new ToolTip();
        Panel settings;
        TestSession session;
        TestEngine engine { get { return session==null?null:selectedDirection==1?session.Reverse:session.Forward; } }
        bool finishing, saving;
        DateTime nextChart = DateTime.MinValue;
        readonly List<string> events = new List<string>();

        static ComboBox Combo(bool editable) { return new DarkCombo { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, IntegralHeight = false, DropDownHeight = 250 }; }
        static DarkNumber Number(int min,int max,int value) { return new DarkNumber(min,max,value) { Dock=DockStyle.Fill }; }
        static Button Button(string text,bool primary) {
            Button b = new DarkButton { Primary=primary, Text=text, Height=36, FlatStyle=FlatStyle.Flat, BackColor=primary?Theme.Teal:Theme.Field, ForeColor=primary?Theme.Bg:Theme.Navy, Cursor=Cursors.Hand, Margin=new Padding(4), AutoSize=false };
            b.FlatAppearance.BorderColor = primary ? Theme.Teal : Theme.Border; return b;
        }
        static Label Label(string text,int size,Color color) { return new Label { Text=text,AutoSize=false,Font=new Font("Segoe UI",size),ForeColor=color,Dock=DockStyle.Top }; }
        public MainForm() {
            Text = "Serial Link Tester"; Font = new Font("Segoe UI",9.5f); BackColor = Theme.Bg; ForeColor = Theme.Navy;
            AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(1200,810); MinimumSize = new Size(1040,760); StartPosition = FormStartPosition.CenterScreen;
            using(Stream iconStream=typeof(MainForm).Assembly.GetManifestResourceStream("SerialLinkTester.ico"))
            using(Icon sourceIcon=new Icon(iconStream,new Size(32,32)))Icon=(Icon)sourceIcon.Clone();
            Build(); RefreshPorts(); Configure();
            HandleCreated += delegate { Theme.WindowChrome(this); };
            timer.Interval=250; timer.Tick += delegate { RefreshMeasurement(); }; timer.Start();
            start.Click += delegate { StartTest(false); }; demo.Click += delegate { StartTest(true); };
            stop.Click += delegate { if(session != null && !session.Done) { session.Stop(); stop.Enabled=false; status.Text="Stopping transmission · waiting for final packets"; } };
            save.Click += delegate { SaveReport(); }; refresh.Click += delegate { RefreshPorts(); };
            setup.SelectedIndexChanged += delegate { Configure(); }; mode.SelectedIndexChanged += delegate { Configure(); }; baud.TextChanged += delegate { ConfigureBaud(); };
            tx.SelectedIndexChanged += delegate { UpdateRoute(); }; rx.SelectedIndexChanged += delegate { UpdateRoute(); };
            chartMode.SelectedIndexChanged += delegate { foreach(DirectionView v in directions)v.Chart.Reset();ConfigureChart(); };
            FormClosing += OnClosing;
        }
        void Build() {
            Panel header = new Panel { Dock=DockStyle.Top,Height=94,BackColor=Theme.Bg,Padding=new Padding(25,13,25,10) };
            Label eyebrow = Label("SERIAL DIAGNOSTICS",9,Color.FromArgb(114,216,202)); eyebrow.Height=20;
            Label title = Label("Serial Link Tester",24,Color.White); title.Height=42;
            Label sub = Label("Integrity, throughput and timing. One clear view.",10,Color.FromArgb(197,214,224)); sub.Height=23;
            header.Controls.Add(sub); header.Controls.Add(title); header.Controls.Add(eyebrow);
            Panel bottom = new Panel { Dock=DockStyle.Bottom,Height=80,Padding=new Padding(20,10,20,10),BackColor=Theme.Surface };
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock=DockStyle.Right,Width=440,FlowDirection=FlowDirection.RightToLeft,WrapContents=false };
            save.Width=145; demo.Width=135; stop.Width=115; buttons.Controls.Add(save);buttons.Controls.Add(demo);buttons.Controls.Add(stop);save.Enabled=stop.Enabled=false;
            Panel stateArea=new Panel { Dock=DockStyle.Fill };
            status.Dock=DockStyle.Top;status.Height=28;status.AutoEllipsis=true;status.Text="Ready · choose ports or try the demo";status.TextAlign=ContentAlignment.MiddleLeft;status.Font=new Font("Segoe UI",10,FontStyle.Bold);
            warningStatus.Dock=DockStyle.Fill;warningStatus.AutoEllipsis=true;warningStatus.TextAlign=ContentAlignment.MiddleLeft;warningStatus.ForeColor=Theme.Muted;warningStatus.Font=new Font("Segoe UI",8.5f);warningStatus.Text="Serial warnings: none observed";
            stateArea.Controls.Add(warningStatus);stateArea.Controls.Add(status);bottom.Controls.Add(stateArea);bottom.Controls.Add(buttons);
            TableLayoutPanel body = new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(18,15,18,12),ColumnCount=2,RowCount=1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,302));body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            settings = new Panel { Dock=DockStyle.Fill,BackColor=Theme.Surface,Padding=new Padding(17),Margin=new Padding(0,0,12,0),AutoScroll=true };
            TableLayoutPanel fields = new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Padding=new Padding(0,0,0,8) };
            Label settingTitle=Label("01   Configure your test",14,Theme.Navy);settingTitle.Height=35;fields.Controls.Add(settingTitle);
            tx.AccessibleName="Port A / transmit port";rx.AccessibleName="Port B / receive port";payload.AccessibleName="Payload bytes";seconds.AccessibleName="Duration seconds";load.AccessibleName="Target load per direction";timeout.AccessibleName="Receive timeout";
            setup.Items.AddRange(new object[]{"Two ports / one way","One port / round trip","Two ports / bidirectional"});setup.SelectedIndex=new TestConfig().Bidirectional?2:0;
            mode.Items.AddRange(new object[]{"Latency and jitter","Integrity and throughput"});mode.SelectedIndex=0;
            baud.Items.AddRange(new object[]{"1200","2400","4800","9600","19200","28800","38400","56000","57600","115200","128000","230400","256000","460800","921600"});baud.Text="115200";
            AddField(fields,"Connection setup",setup);
            TableLayoutPanel ports=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,Height=32,Margin=new Padding(0) };
            ports.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));ports.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));tx.Margin=new Padding(0,0,6,0);rx.Margin=new Padding(0);ports.Controls.Add(tx);ports.Controls.Add(rx);
            AddField(fields,"Transmit port                  Receive port",ports,portLabels);
            refresh.Dock=DockStyle.Top;refresh.Height=29;refresh.Margin=new Padding(0,2,0,4);fields.Controls.Add(refresh);
            AddField(fields,"Baud rate · select or type a value",baud);
            baudNote.Dock=DockStyle.Top;baudNote.Height=33;baudNote.Font=new Font("Segoe UI",8);baudNote.ForeColor=Theme.Muted;fields.Controls.Add(baudNote);
            AddField(fields,"Test mode",mode);
            TableLayoutPanel sizes=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,Height=30,Margin=new Padding(0) };
            sizes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));sizes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));payload.Margin=new Padding(0,0,6,0);seconds.Margin=new Padding(0);sizes.Controls.Add(payload);sizes.Controls.Add(seconds);
            AddField(fields,"Payload (bytes)                Duration (s)",sizes);
            AddField(fields,"Target load (%) · throughput mode",load,loadLabel);
            AddField(fields,"Response / final receive window (ms)",timeout);
            note.BackColor=Theme.Field;note.ForeColor=Theme.Navy;note.BorderStyle=BorderStyle.FixedSingle;note.Dock=DockStyle.Fill;note.MaxLength=200;AddField(fields,"Test notes · device / link / conditions",note);
            start.Dock=DockStyle.Top;start.Height=42;start.Margin=new Padding(0,14,0,3);fields.Controls.Add(start);
            settings.Controls.Add(fields);body.Controls.Add(settings,0,0);
            TableLayoutPanel results=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=5,ColumnCount=1,Margin=new Padding(0) };
            routeRow=new RowStyle(SizeType.Absolute,62);results.RowStyles.Add(routeRow);results.RowStyles.Add(new RowStyle(SizeType.Absolute,238));results.RowStyles.Add(new RowStyle(SizeType.Percent,100));results.RowStyles.Add(new RowStyle(SizeType.Absolute,45));results.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
            Panel routeArea=new Panel { Dock=DockStyle.Fill };
            route.Dock=DockStyle.Top;route.Height=51;route.Font=new Font("Segoe UI",10);route.Padding=new Padding(7,4,4,2);
            overview.Dock=DockStyle.Fill;overview.ColumnCount=7;overview.RowCount=3;overview.Padding=new Padding(5,0,5,0);overview.BackColor=Theme.Bg;
            overview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,108));for(int i=0;i<6;i++)overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/6));
            overview.RowStyles.Add(new RowStyle(SizeType.Absolute,20));overview.RowStyles.Add(new RowStyle(SizeType.Absolute,29));overview.RowStyles.Add(new RowStyle(SizeType.Absolute,29));
            string[] headings={"View direction","Missing","Corrupt","kbit/s","Delay ms","Jitter ms","Valid"};
            for(int i=0;i<7;i++)overview.Controls.Add(new Label { Text=headings[i],Dock=DockStyle.Fill,ForeColor=Theme.Muted,Font=new Font("Segoe UI",8),TextAlign=ContentAlignment.MiddleCenter,Margin=new Padding(1) },i,0);
            for(int row=0;row<2;row++) {
                int index=row;DarkButton b=directionButtons[row];b.Text=row==0?"A → B":"B → A";b.Dock=DockStyle.Fill;b.Margin=new Padding(1);b.Selected=row==0;b.Click+=delegate { SelectDirection(index); };overview.Controls.Add(b,0,row+1);
                for(int col=0;col<6;col++) { Label value=new Label { Text="—",Dock=DockStyle.Fill,BackColor=Theme.Surface,ForeColor=Theme.Navy,TextAlign=ContentAlignment.MiddleCenter,Font=new Font("Segoe UI",9),Margin=new Padding(1) };directionValues[row,col]=value;overview.Controls.Add(value,col+1,row+1); }
            }
            routeArea.Controls.Add(overview);routeArea.Controls.Add(route);results.Controls.Add(routeArea,0,0);
            TableLayoutPanel metrics=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3,RowCount=2,Margin=new Padding(0) };
            for(int i=0;i<3;i++)metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f));for(int i=0;i<2;i++)metrics.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            metrics.Controls.Add(loss,0,0);metrics.Controls.Add(corrupt,1,0);metrics.Controls.Add(speed,2,0);metrics.Controls.Add(delay,0,1);metrics.Controls.Add(jitter,1,1);metrics.Controls.Add(valid,2,1);results.Controls.Add(metrics,0,1);
            Panel view=new Panel { Dock=DockStyle.Fill,BackColor=Theme.Surface,Margin=new Padding(5,10,5,0),Padding=new Padding(10) };
            FlowLayoutPanel nav=new FlowLayoutPanel { Dock=DockStyle.Top,Height=41,WrapContents=false,BackColor=Theme.Surface };
            Panel pages=new Panel { Dock=DockStyle.Fill,BackColor=Theme.Surface };
            Panel graphPage=new Panel { Dock=DockStyle.Fill,BackColor=Theme.Surface };
            Panel chartBar=new Panel { Dock=DockStyle.Top,Height=35 };chartMode.Dock=DockStyle.Right;chartMode.Width=200;chartMode.Items.AddRange(new object[]{"Latency (ms)","Goodput (kbit/s)"});chartMode.SelectedIndex=0;chartBar.Controls.Add(chartMode);graphPage.Controls.Add(directions[0].Chart);graphPage.Controls.Add(directions[1].Chart);directions[1].Chart.Visible=false;graphPage.Controls.Add(chartBar);
            details.Dock=help.Dock=DockStyle.Fill;details.ReadOnly=help.ReadOnly=true;details.BorderStyle=help.BorderStyle=BorderStyle.None;details.BackColor=help.BackColor=Theme.Surface;details.ForeColor=help.ForeColor=Theme.Navy;
            details.Font=new Font("Consolas",10);help.Font=new Font("Segoe UI",10);help.Text=HelpText;
            Control[] contents={graphPage,details,help};DarkButton[] navigation=new DarkButton[3];string[] titles={"Live chart","Details","Connection guide"};
            for(int i=0;i<3;i++) {
                int index=i;contents[i].Visible=i==0;pages.Controls.Add(contents[i]);
                navigation[i]=new DarkButton { Text=titles[i],Width=i==2?155:110,Height=32,Margin=new Padding(0,0,7,0),Selected=i==0 };
                navigation[i].Click+=delegate { for(int k=0;k<3;k++){contents[k].Visible=k==index;navigation[k].Selected=k==index;navigation[k].Invalidate();}contents[index].BringToFront(); };
                nav.Controls.Add(navigation[i]);
            }
            view.Controls.Add(pages);view.Controls.Add(nav);results.Controls.Add(view,0,2);
            scope.Dock=DockStyle.Fill;scope.ForeColor=Theme.Muted;scope.Font=new Font("Segoe UI",8.5f);scope.Padding=new Padding(8,9,2,0);scope.Text="Timing includes Windows, the serial adapter and the link under test.\nJitter = mean absolute difference between consecutive valid packet delays.";results.Controls.Add(scope,0,3);
            Panel track=new Panel { Dock=DockStyle.Fill,Padding=new Padding(6,3,6,0) };elapsed.Dock=DockStyle.Right;elapsed.Width=220;elapsed.TextAlign=ContentAlignment.MiddleRight;elapsed.Text="0 / 60 s";progress.Dock=DockStyle.Fill;track.Controls.Add(progress);track.Controls.Add(elapsed);results.Controls.Add(track,0,4);
            body.Controls.Add(results,1,0);
            Controls.Add(body);Controls.Add(bottom);Controls.Add(header);
            tips.BackColor=Theme.Field;tips.ForeColor=Theme.Navy;
            tips.SetToolTip(loss,"Pending packets are shown during a test. Missing packets are finalized after the receive window closes.");
            tips.SetToolTip(jitter,"Mean absolute difference between consecutive valid packet delays. Latency standard deviation is available in Details.");
            tips.SetToolTip(speed,"Live: verified payload bits per sample interval. Final: active goodput between the first and last valid packets. Protocol overhead is excluded.");
            tips.SetToolTip(timeout,"Response threshold and final receive window. Interpret missing packets together with this limit.");
            tips.SetToolTip(setup,"Bidirectional runs A to B and B to A at the same time. Both transmit and receive paths must be wired; the link must support simultaneous traffic.");
            tips.SetToolTip(load,"In bidirectional mode, this target applies independently to each direction, not a shared total.");
            tips.SetToolTip(warningStatus,"Amber serial warnings are recorded over the whole test and do not stop it. Red test-stopping errors are shown separately. Details lists warning types and observation counts.");
        }
        static void AddField(TableLayoutPanel panel,string label,Control control) {
            AddField(panel,label,control,Label(label,9,Theme.Muted));
        }
        static void AddField(TableLayoutPanel panel,string label,Control control,Label l) {
            l.Text=label;l.ForeColor=Theme.Muted;l.Font=new Font("Segoe UI",9);l.Dock=DockStyle.Top;l.Height=18;l.Margin=new Padding(0,4,0,0);panel.Controls.Add(l);
            control.Height=27;control.Margin=new Padding(0);control.Dock=DockStyle.Top;panel.Controls.Add(control);
        }
        void RefreshPorts() {
            string oldTx=tx.Text,oldRx=rx.Text;string[] ports;
            try { ports=SerialPort.GetPortNames(); Array.Sort(ports,StringComparer.OrdinalIgnoreCase); }
            catch(Exception ex) { status.Text="Unable to list ports: "+ex.Message;return; }
            tx.Items.Clear();rx.Items.Clear();tx.Items.AddRange(ports);rx.Items.AddRange(ports);
            if(ports.Length>0) { tx.SelectedIndex=Math.Max(0,tx.Items.IndexOf(oldTx));rx.SelectedIndex=rx.Items.Contains(oldRx)?rx.Items.IndexOf(oldRx):Math.Min(1,ports.Length-1); }
            if(ports.Length==0)status.Text="No COM ports found · connect an adapter or try the demo";
            UpdateRoute();
        }
        void Configure() { rx.Enabled=setup.SelectedIndex!=1;load.Enabled=mode.SelectedIndex==1;portLabels.Text=setup.SelectedIndex==2?"Port A                               Port B":"Transmit port                  Receive port";loadLabel.Text=setup.SelectedIndex==2?"Target load (%) · each direction":"Target load (%) · throughput mode";ConfigureBaud();UpdateRoute(); }
        void ConfigureBaud() {
            baudNote.Text="8 data bits · no parity · 1 stop bit\nFlow control off · match both endpoints";
            baudNote.ForeColor=Theme.Muted;
        }
        void UpdateRoute() {
            if(route==null)return;
            bool duplex=session!=null?session.Config.Bidirectional:setup.SelectedIndex==2,loop=session!=null?session.Config.Loopback:setup.SelectedIndex==1;
            string a=session!=null?session.Config.TxPort:tx.Text,b=session!=null?session.Config.RxPort:rx.Text;
            if(string.IsNullOrEmpty(a))a="COM A";if(string.IsNullOrEmpty(b))b="COM B";
            overview.Visible=duplex;if(routeRow!=null)routeRow.Height=duplex?136:62;
            route.Text=duplex?"02   Both directions · cards and chart: "+(selectedDirection==0?"A → B":"B → A")+"\nA: "+a+"  ↔  Device or link under test  ↔  B: "+b:!loop?"02   Monitor your link\n"+a+"  →  Device or link under test  →  "+b:"02   Monitor your link\n"+a+"  →  Link  →  Remote TX/RX loopback  →  "+a;
        }
        void ConfigureChart() { for(int i=0;i<2;i++) { LiveChart plot=directions[i].Chart;plot.Unit=chartMode.SelectedIndex==0?"ms":"kb/s";plot.Caption=(session!=null&&session.Config.Bidirectional?(i==0?"A → B · ":"B → A · "):"")+(chartMode.SelectedIndex==0?"Last 120 seconds · interval mean latency":"Last 120 seconds · verified payload");plot.Invalidate(); } }
        void SelectDirection(int index) {
            if(session==null||!session.Config.Bidirectional)return;
            selectedDirection=index;for(int i=0;i<2;i++){directions[i].Chart.Visible=i==index;directionButtons[i].Selected=i==index;directionButtons[i].Invalidate();}
            UpdateRoute();RenderSelected(engine.Measure.Get());UpdateDetails(engine.Measure.Get());
        }
        TestConfig Configuration(bool isDemo) {
            int b;if(!int.TryParse(baud.Text,out b))throw new ArgumentException("Enter a numeric baud rate.");
            return new TestConfig { Baud=b,TxPort=tx.Text,RxPort=rx.Text,Loopback=setup.SelectedIndex==1,Bidirectional=setup.SelectedIndex==2,Setup=setup.Text,Mode=mode.Text,Throughput=mode.SelectedIndex==1,Payload=(int)payload.Value,DurationSeconds=(int)seconds.Value,LoadPercent=(int)load.Value,TimeoutMs=(int)timeout.Value,Demo=isDemo,Note=note.Text };
        }
        void StartTest(bool isDemo) {
            if(session!=null&&!session.Done)return;
            try {
                TestConfig c=Configuration(isDemo);c.Validate();
                if(session!=null)session.Dispose();
                session=new TestSession(c);selectedDirection=0;events.Clear();details.Clear();foreach(DirectionView v in directions)v.Reset();directions[0].Chart.Visible=true;directions[1].Chart.Visible=false;directionButtons[0].Selected=true;directionButtons[1].Selected=false;foreach(DarkButton b in directionButtons)b.Invalidate();ConfigureChart();UpdateRoute();nextChart=DateTime.MinValue;finishing=false;
                settings.Enabled=false;demo.Enabled=false;save.Enabled=false;stop.Enabled=true;
                status.ForeColor=isDemo?Theme.Amber:Theme.Teal;status.Text=isDemo?"DEMO · Simulated test running":"Opening ports…";
                warningStatus.Text="Serial warnings: none observed";warningStatus.ForeColor=Theme.Muted;
                scope.Text=(isDemo?"DEMO: simulated results. ":"")+(c.Bidirectional?"Simultaneous traffic · target load applies to EACH direction.":c.Loopback?"Round-trip latency; do not halve it to claim one-way timing.":"One-way application latency; includes Windows, adapter and link.")+"\n"+(c.Bidirectional?"Each direction includes Windows, adapters and link. Select A → B or B → A for details.":"Jitter = mean absolute delay difference. Throughput mode measures timing under load.");
                session.Start();
            } catch(Exception ex) { Theme.Notice(this,ex.Message,"Unable to start test"); }
        }
        static string N(double value) { return double.IsInfinity(value)?">60 s":value.ToString("0.##",CultureInfo.InvariantCulture); }
        void RefreshMeasurement() {
            if(session==null||(session.Done&&finishing))return;
            Snapshot forward=session.Forward.Measure.Get(),reverse=session.Reverse==null?null:session.Reverse.Measure.Get();
            string message;while(session.Events.TryDequeue(out message)) { events.Add(DateTime.Now.ToString("HH:mm:ss")+"  "+message);if(events.Count>30)events.RemoveAt(0); }
            bool draw=DateTime.UtcNow>=nextChart;
            if(draw) {
                Sample(forward,0);if(reverse!=null)Sample(reverse,1);nextChart=DateTime.UtcNow.AddSeconds(1);
            }
            Snapshot s=selectedDirection==0?forward:reverse;RenderSelected(s);
            if(reverse!=null) { RenderOverview(forward,0);RenderOverview(reverse,1);overview.GetControlFromPosition(1,0).Text=session.Done?"Missing":"Pending"; }
            double sentSeconds=reverse==null?forward.SendSeconds:Math.Max(forward.SendSeconds,reverse.SendSeconds);
            progress.Value=(int)Math.Min(100,sentSeconds/session.Config.DurationSeconds*100);
            string phase=session.Done?"Complete":reverse==null?forward.Phase:"A → B: "+forward.Phase+" · B → A: "+reverse.Phase;
            bool error=forward.Error.Length>0||(reverse!=null&&reverse.Error.Length>0);
            uint warningMask=forward.PcErrorMask|(reverse==null?0:reverse.PcErrorMask);
            if(error) {
                string failure=forward.Error;
                if(reverse!=null&&(failure.Length==0||failure.StartsWith("Test canceled",StringComparison.Ordinal)))failure=reverse.Error;
                phase="TEST STOPPED · "+failure;
            }
            elapsed.Text=N(sentSeconds)+" / "+session.Config.DurationSeconds+" s · "+(session.Done?(error?"Errors":"Complete"):"Running");
            status.Text=(s.Demo?"DEMO · ":"")+phase;status.ForeColor=error?Theme.Red:s.Demo?Theme.Amber:Theme.Teal;
            tips.SetToolTip(status,error?(reverse==null?forward.Error:"A → B: "+forward.Error+"\nB → A: "+reverse.Error):phase);
            warningStatus.Text=warningMask==0?"Serial warnings: none observed":"Serial warnings (recorded): "+SerialWarnings.Names(warningMask);
            warningStatus.ForeColor=warningMask==0?Theme.Muted:Theme.Amber;
            if(draw||session.Done&&!finishing)UpdateDetails(s);
            if(session.Done&&!finishing) { finishing=true;settings.Enabled=true;Configure();demo.Enabled=true;save.Enabled=true;stop.Enabled=false; }
        }
        void Sample(Snapshot s,int index) {
            DirectionView v=directions[index];double dt=s.Elapsed-v.Elapsed;v.LiveRate=dt>0?(s.GoodPayloadBytes-v.Bytes)*8.0/dt/1000:0;
            double sum=s.MeanMs*s.LatencyCount;long count=s.LatencyCount-v.LatencyCount;
            v.Chart.Add(chartMode.SelectedIndex==1?v.LiveRate:count>0?(sum-v.LatencySum)/count:double.NaN);
            v.Bytes=s.GoodPayloadBytes;v.Elapsed=s.Elapsed;v.LatencySum=sum;v.LatencyCount=s.LatencyCount;
        }
        void RenderOverview(Snapshot s,int row) {
            string[] values={s.Missing.ToString(),(s.Corrupt+s.Invalid).ToString(),s.Done&&s.Good<2?"—":N(s.Done?s.ActivePayloadBps/1000:directions[row].LiveRate),s.LatencyCount>0?N(s.MeanMs):"—",s.LatencyCount>1?N(s.JitterMs):"—",s.Good.ToString()};
            for(int col=0;col<6;col++) { directionValues[row,col].Text=values[col];directionValues[row,col].ForeColor=(col==0&&s.Done&&s.Missing>0)||(col==1&&s.Corrupt+s.Invalid>0)?Theme.Red:Theme.Navy; }
            directionButtons[row].Text=(row==0?"A → B":"B → A")+(s.Error.Length>0?" !":"");
        }
        void RenderSelected(Snapshot s) {
            loss.Title.Text=s.Done?"Missing packets":"Pending packets";loss.Value.Text=s.Missing.ToString("N0");loss.Value.ForeColor=s.Done&&s.Missing>0?Theme.Red:Theme.Navy;
            loss.Detail.Text=s.Done?(s.Sent>0?N(100.0*s.Missing/s.Sent)+"% · receive window closed":"No packets sent"):s.Overdue+" past response timeout";
            corrupt.Value.Text=(s.Corrupt+s.Invalid).ToString("N0");corrupt.Value.ForeColor=s.Corrupt+s.Invalid>0?Theme.Red:Theme.Navy;corrupt.Detail.Text=s.Corrupt+" payload / "+s.Invalid+" packet format";
            speed.Value.Text=s.Done&&s.Good<2?"—":N(s.Done?s.ActivePayloadBps/1000:directions[selectedDirection].LiveRate);speed.Detail.Text=s.Done?"kbit/s · active receive average":"kbit/s · latest interval";
            delay.Value.Text=s.LatencyCount>0?N(s.MeanMs):"—";delay.Detail.Text=s.LatencyCount>0?"ms · p95 "+N(s.P95Ms)+" / p99 "+N(s.P99Ms):"Waiting for a valid packet";
            jitter.Value.Text=s.LatencyCount>1?N(s.JitterMs):"—";jitter.Detail.Text="ms · consecutive delay variation";
            valid.Value.Text=s.Good.ToString("N0");valid.Detail.Text=s.Sent.ToString("N0")+" packets transmitted";
        }
        void UpdateDetails(Snapshot s) {
            string heading=session.Config.Bidirectional?"  DIRECTION "+(selectedDirection==0?"A → B":"B → A")+" · "+engine.Config.TxPort+" → "+engine.Config.RxPort+"\n  Serial warnings are port-level; shared by both directions.\n\n":"";
            details.Text="  SESSION SUMMARY\n\n  Write attempts / completed     : "+s.Attempted+" / "+s.Sent+"\n  Received / valid / corrupt     : "+s.Received+" / "+s.Good+" / "+s.Corrupt+"\n  Invalid frames                 : "+s.Invalid+"\n  Duplicates / out of order       : "+s.Duplicates+" / "+s.Reordered+"\n  Packets from another session   : "+s.Foreign+"\n  Late packets                   : "+s.Late+"\n  Serial warning observations/mask  : "+s.PcErrorEvents+" / 0x"+s.PcErrorMask.ToString("X")+"\n  Max observed RX / TX queue      : "+s.MaxRxQueue+" / "+s.MaxTxQueue+" bytes\n\n  LATENCY (ms)\n\n  Min / median / p95 / p99 / max : "+(s.LatencyCount>0?N(s.MinMs)+" / "+N(s.P50Ms)+" / "+N(s.P95Ms)+" / "+N(s.P99Ms)+" / "+N(s.MaxMs):"No samples")+"\n  Standard deviation             : "+(s.LatencyCount>1?N(s.StdDevMs):"—")+"\n  Percentile bin width           : 0.1 ms\n\n  TRANSFER\n\n  Total TX / RX wire bytes       : "+s.TxWireBytes+" / "+s.RxWireBytes+"\n  Verified payload bytes         : "+s.GoodPayloadBytes+"\n  Whole-session average          : "+N(s.AveragePayloadBps/1000)+" kbit/s\n  (Includes port opening and the final receive window.)\n\n  EVENTS\n\n  "+string.Join("\n  ",events.ToArray());
            string warnings="  SERIAL WARNINGS · non-fatal / recorded during this test\n  "+SerialWarnings.Counts(s)+"\n  Types: "+s.SerialWarningTypes+"\n  Counts are observations, not affected bytes. These warnings do not stop the test.\n\n";
            details.Text=heading+(s.Error.Length>0?"  TEST-STOPPING ERROR\n  "+s.Error+"\n\n":"")+warnings+details.Text;
        }
        void SaveReport() {
            if(session==null||!session.Done||saving)return;
            using(FolderBrowserDialog dialog=new FolderBrowserDialog { Description="Choose a report location. A dated subfolder will be created.",ShowNewFolderButton=true }) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string dir=Path.Combine(dialog.SelectedPath,"SerialTest_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+engine.Measure.RunId.ToString("N").Substring(0,6));
                saving=true;save.Enabled=false;demo.Enabled=false;start.Enabled=false;
                TestSession current=session;
                ThreadPool.QueueUserWorkItem(delegate {
                    string error=null;try { Export.Save(dir,current); }catch(Exception ex){error=ex.Message;}
                    if(!IsDisposed)BeginInvoke((Action)delegate { saving=false;save.Enabled=true;demo.Enabled=true;start.Enabled=true;if(error==null)Theme.Notice(this,"Report saved:\n"+dir+"\n\nreport.html · packets.csv · summary.json","Report ready");else Theme.Notice(this,error,"Unable to save report"); });
                });
            }
        }
        void OnClosing(object sender,FormClosingEventArgs e) {
            if(saving) { e.Cancel=true;status.Text="Please wait for the report to finish saving.";return; }
            if(session!=null&&!session.Done) {
                e.Cancel=true;session.Abort();status.Text="Closing ports…";
                ThreadPool.QueueUserWorkItem(delegate { session.Wait(15000);if(!IsDisposed&&session.Done)BeginInvoke((Action)Close); });
            }
        }
        const string HelpText = @"  QUICK START

  1. Connect your serial adapter and install its Windows driver if needed.
  2. Match the electrical interface and baud rate at both ends of the link.
  3. Refresh ports, select your setup, then choose transmit and receive ports.
  4. Start with Latency and jitter; repeat with 50%, 90% and 100% target load.

  TWO PORTS / ONE WAY
  Same PC: transmit port → device or link under test → receive port.
  The device or link must pass the binary test stream through unchanged.
  Swap the port selections to test the opposite direction if both paths are wired.

  TWO PORTS / BIDIRECTIONAL
  Select Port A and Port B on the same PC. Connect both TX and RX paths.
  A → B and B → A run simultaneously, each with its own packets and measurements.
  Target load applies to EACH direction: 100% means full target load both ways.
  The overview shows both directions; select A → B or B → A for cards and chart.
  The link and wiring must support simultaneous traffic. No half-duplex bus
  arbitration or two-wire RS-485 direction control is provided.

  ONE PORT / ROUND TRIP
  Return the remote serial TX output to its RX input using a suitable cable.
  This measures round-trip time. Do not divide it by two to claim one-way delay.
  A local TX/RX loopback provides a baseline for the PC and serial adapter.
  Use wiring appropriate to your electrical interface: RS-232, RS-422 or RS-485.

  SERIAL SETTINGS
  • 8 data bits, no parity, 1 stop bit; hardware/software flow control off.
  • Choose a listed baud rate or type a custom value supported by both ends.
  • Driver FIFO and buffering options vary by adapter. Record them in Test notes.
  • Compare driver settings with a direct cable before evaluating the full link.
  • A completed write is not a physical wire timestamp. The app does not change
    adapter-specific driver settings or repeatedly purge buffers during a test.

  UNDERSTANDING THE RESULTS
  • Missing: completed writes without an identifiable response when the final
    receive window closes. While running, the card shows pending packets.
  • Corruption: failed payload integrity or invalid framing events. An unreadable
    header may count as both a framing error and a missing packet; do not add them.
  • Goodput: verified payload bits per second, excluding protocol overhead.
  • Latency: from write submission to reception of a complete valid packet.
    This includes Windows, adapter, serialization and the device or link under test.
  • Jitter: mean absolute difference between consecutive valid packet delays.
    Latency standard deviation is listed separately in Details.
  • The throughput test measures timing under load; it is not idle-link latency.
  • Serial errors are driver observations, not counts of affected bytes.

  SERIAL WARNINGS AND TEST-STOPPING ERRORS
  Amber warnings name Framing, Break, Overrun, RX overflow or Parity events.
  These warnings do not stop the test. Their counts are cumulative observations,
  so reconnecting a cable does not erase them; a new test starts from zero.
  A red TEST STOPPED message identifies an I/O failure or canceled/incomplete test.
  Details and exported reports keep these errors separate from serial warnings.
  Packet format errors in the Corruption card are distinct from UART Framing.

  STOPPING AND EXPORTING
  After transmission stops, reception stays open for the selected timeout.
  The default response threshold / final receive window is 1000 ms (1 second).
  Packets arriving after that window are missing for this measurement.
  Export report creates report.html, packets.csv and summary.json.
  Bidirectional exports include a comparison and separate A_to_B / B_to_A reports.
  Demo reports are clearly labeled SIMULATED. No hardware is used in demo mode.

  SCOPE
  This version uses one PC as the time reference. It does not synchronize clocks
  across separate PCs or measure raw physical-layer bit error rate.
";
    }
}
