using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SerialRf;

static class UiTests {
    [DllImport("user32.dll", EntryPoint="SendMessageW")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref Point point);
    const int GetScrollPos=0x0400+221, SetScrollPos=0x0400+222;
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int tests, failures;
    static T Field<T>(MainForm form,string name) { return (T)typeof(MainForm).GetField(name,Private).GetValue(form); }
    static void Call(MainForm form,string name,params object[] args) { typeof(MainForm).GetMethod(name,Private).Invoke(form,args); }
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    static Point Position(RichTextBox box) { Point p=new Point();SendMessage(box.Handle,GetScrollPos,IntPtr.Zero,ref p);return p; }
    static void Scroll(RichTextBox box,int y) { Point p=new Point(0,y);SendMessage(box.Handle,SetScrollPos,IntPtr.Zero,ref p); }
    static void Pump(int milliseconds) {
        Stopwatch watch=Stopwatch.StartNew();
        do { Application.DoEvents();Thread.Sleep(10); } while(watch.ElapsedMilliseconds<milliseconds);
    }
    static Button ButtonNamed(Control parent,string text) {
        foreach(Control c in parent.Controls) {
            Button b=c as Button;if(b!=null&&b.Text==text)return b;
            Button nested=ButtonNamed(c,text);if(nested!=null)return nested;
        }
        return null;
    }
    sealed class TestHost : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    sealed class Fixture : IDisposable {
        readonly TestHost host=new TestHost();
        public readonly MainForm Form=new MainForm(c => new TestSession(c, config => TestDuplexEndpoint.Pair(false,false)));
        public readonly RichTextBox Details;
        public readonly List<string> Events;
        public void ResizeWindow(Size size) { host.ClientSize=size;Pump(30); }
        public Fixture() {
            Field<System.Windows.Forms.Timer>(Form,"timer").Stop();
            host.StartPosition=FormStartPosition.Manual;host.Location=new Point(-30000,-30000);
            host.ClientSize=new Size(1200,810);host.ShowInTaskbar=false;
            Form.TopLevel=false;Form.FormBorderStyle=FormBorderStyle.None;Form.Dock=DockStyle.Fill;
            host.Controls.Add(Form);Form.Show();host.Show();
            Details=Field<RichTextBox>(Form,"details");Events=Field<List<string>>(Form,"events");
            TestSession session=new TestSession(new TestConfig { TxPort="COM1",RxPort="COM2" });
            typeof(MainForm).GetField("session",Private).SetValue(Form,session);
            Call(Form,"UpdateRoute");ButtonNamed(Form,"Details").PerformClick();
            for(int i=0;i<20;i++)Events.Add("12:00:00  Event "+i);
            Render(100);Pump(20);
        }
        public void Render(int count) {
            Snapshot s=Field<TestSession>(Form,"session").Forward.Measure.Get();
            s.Sent=s.Attempted=s.Good=s.Received=count;s.GoodPayloadBytes=count*64;
            Call(Form,"UpdateDetails",s);
        }
        public void Dispose() {
            Field<System.Windows.Forms.Timer>(Form,"timer").Dispose();
            TestSession session=Field<TestSession>(Form,"session");
            if(session!=null)session.Dispose();
            Form.Dispose();host.Dispose();
        }
    }
    static void Test(string name,Action<Fixture> action) {
        tests++;
        try { using(Fixture f=new Fixture())action(f);Console.WriteLine("PASS "+name); }
        catch(Exception ex) { failures++;Console.WriteLine("FAIL "+name+": "+ex.Message); }
    }
    [STAThread]
    static int Main() {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Test("Metric cards fit compact and wide windows with larger text",delegate(Fixture f) {
            Metric[] cards={Field<Metric>(f.Form,"loss"),Field<Metric>(f.Form,"corrupt"),Field<Metric>(f.Form,"speed"),
                Field<Metric>(f.Form,"delay"),Field<Metric>(f.Form,"jitter"),Field<Metric>(f.Form,"valid")};
            TableLayoutPanel grid=(TableLayoutPanel)cards[0].Parent;
            Dictionary<Label,Font> original=new Dictionary<Label,Font>();
            foreach(Metric card in cards)
                foreach(Label label in new[]{card.Title,card.Value,card.Detail})original.Add(label,label.Font);
            Snapshot sample=Field<TestSession>(f.Form,"session").Forward.Measure.Get();
            sample.Good=sample.Sent=1000000;sample.LatencyCount=2;sample.P95Ms=12345.67;sample.P99Ms=23456.78;
            int minColumns=3,maxColumns=0;
            foreach(float factor in new[]{1f,1.25f,1.5f}) {
                List<Font> scaled=new List<Font>();
                try {
                    foreach(KeyValuePair<Label,Font> item in original) {
                        Font font=new Font(item.Value.FontFamily,item.Value.Size*factor,item.Value.Style);
                        scaled.Add(font);item.Key.Font=font;
                    }
                    foreach(Size size in new[]{new Size(1200,810),new Size(1024,650),new Size(860,550),new Size(1366,700)}) {
                        f.ResizeWindow(size);
                        Call(f.Form,"RenderSelected",sample);
                        Pump(20);
                        minColumns=Math.Min(minColumns,grid.ColumnCount);maxColumns=Math.Max(maxColumns,grid.ColumnCount);
                        for(int i=0;i<cards.Length;i++) {
                            Metric card=cards[i];
                            Check(card.Top>=0 && card.Bottom<=grid.ClientSize.Height && card.Right<=grid.ClientSize.Width,"card outside grid");
                            for(int j=0;j<i;j++)Check(!card.Bounds.IntersectsWith(cards[j].Bounds),"cards overlap");
                            Check(card.Title.Bottom<=card.Value.Top && card.Value.Bottom<=card.Detail.Top,"labels overlap");
                            foreach(Label label in new[]{card.Title,card.Value,card.Detail}) {
                                Size needed=label.GetPreferredSize(new Size(label.Width,0));
                                Check(label.Height>=needed.Height && label.Width>=needed.Width,"clipped "+label.Text+" at "+size+" x "+factor
                                    +", available "+label.Size+", needed "+needed+", grid "+grid.Size);
                            }
                        }
                        Panel viewport=Field<Panel>(f.Form,"resultsViewport");
                        Check(!viewport.HorizontalScroll.Visible,"unexpected horizontal scrollbar");
                        Check(f.Details.Height>=3*f.Details.Font.Height,"report squeezed at "+size+" x "+factor
                            +", report height "+f.Details.Height+", minimum canvas "+viewport.AutoScrollMinSize+", actual "+grid.Parent.Size);
                        if(size.Height==550) {
                            Check(viewport.VerticalScroll.Visible,"short window needs vertical scrolling");
                            viewport.ScrollControlIntoView(f.Details);
                            Check(viewport.AutoScrollPosition.Y<0,"report unreachable");
                            Point scrolled=viewport.AutoScrollPosition;
                            sample.Good--;
                            Call(f.Form,"RenderSelected",sample);
                            Pump(20);
                            Check(viewport.AutoScrollPosition==scrolled,"measurement refresh moved dashboard");
                            viewport.AutoScrollPosition=Point.Empty;
                        }
                    }
                } finally {
                    foreach(KeyValuePair<Label,Font> item in original)item.Key.Font=item.Value;
                    foreach(Font font in scaled)font.Dispose();
                }
            }
            Check(minColumns<maxColumns && maxColumns==3,"card grid did not reflow");
        });
        Test("Live report keeps the scrolled viewport across repeated updates",delegate(Fixture f) {
            Scroll(f.Details,300);Point before=Position(f.Details);Check(before.Y>0,"not scrolled");
            for(int n=101;n<126;n++) {
                f.Render(n);Pump(10);
                Check(Position(f.Details)==before,"viewport jumped during live update "+n);
            }
            Check(f.Details.Text.Contains("125 / 125"),"report values stopped updating");
        });
        Test("New events do not pull the reader away from the current position",delegate(Fixture f) {
            Scroll(f.Details,int.MaxValue);Point before=Position(f.Details);Check(before.Y>0,"not scrolled");
            for(int n=20;n<30;n++)f.Events.Add("12:00:01  New event "+n);
            f.Render(120);Pump(20);
            Check(Position(f.Details)==before,"appended events moved viewport");
            Check(f.Details.Text.Contains("New event 29"),"events not rendered");
        });
        Test("Text selection and viewport survive a refresh",delegate(Fixture f) {
            int start=f.Details.Text.IndexOf("LATENCY",StringComparison.Ordinal);
            f.Details.Select(start,7);Scroll(f.Details,250);Point before=Position(f.Details);
            f.Render(110);Pump(20);
            Check(f.Details.SelectionStart==start&&f.Details.SelectedText=="LATENCY","selection lost");
            Check(Position(f.Details)==before,"selection restoration moved viewport");
        });
        Test("Background refresh keeps Details hidden and retains its scroll position",delegate(Fixture f) {
            Scroll(f.Details,300);Point before=Position(f.Details);
            foreach(string tab in new[]{"Live chart","Connection guide"}) {
                ButtonNamed(f.Form,tab).PerformClick();Check(!f.Details.Visible,"tab did not hide Details");
                f.Render(123);Pump(20);Check(!f.Details.Visible,"refresh exposed hidden Details");
                ButtonNamed(f.Form,"Details").PerformClick();Pump(20);
                Check(Position(f.Details)==before,"switching tabs lost viewport");
            }
        });
        Test("Shorter reports clamp old selection and scroll to a valid range",delegate(Fixture f) {
            f.Details.Select(f.Details.TextLength-10,10);Scroll(f.Details,int.MaxValue);
            f.Events.Clear();f.Render(1);Pump(20);
            Check(f.Details.SelectionStart<=f.Details.TextLength,"invalid selection start");
            Check(f.Details.SelectionStart+f.Details.SelectionLength<=f.Details.TextLength,"invalid selection end");
            Check(Position(f.Details).Y>=0,"invalid scroll position");
            Check(f.Details.GetCharIndexFromPosition(new Point(4,4))<f.Details.TextLength,"empty viewport");
        });
        Test("Direction selection updates the report without resetting the viewport",delegate(Fixture f) {
            Scroll(f.Details,300);Point before=Position(f.Details);
            Call(f.Form,"SelectDirection",1);Pump(20);
            Check(f.Details.Text.Contains("DIRECTION B → A"),"wrong direction report");
            Check(Position(f.Details)==before,"direction selection moved viewport");
        });
        Test("Bidirectional session timer, completion and new-test reset",delegate(Fixture f) {
            Field<DarkNumber>(f.Form,"seconds").Value=2;
            Field<DarkNumber>(f.Form,"timeout").Value=100;
            // Release the unstarted fixture session, then use the Start test button with test-only ports.
            Field<TestSession>(f.Form,"session").Dispose();
            typeof(MainForm).GetField("session",Private).SetValue(f.Form,null);
            foreach(string name in new[]{"tx","rx"}) {
                ComboBox box=Field<ComboBox>(f.Form,name);
                string port=name=="tx"?"COM3":"COM4";
                if(!box.Items.Contains(port))box.Items.Add(port);
                box.SelectedItem=port;
            }
            ButtonNamed(f.Form,"Start test").PerformClick();TestSession running=Field<TestSession>(f.Form,"session");
            Check(running!=null&&running.Config.Bidirectional,"test not started");
            System.Windows.Forms.Timer timer=Field<System.Windows.Forms.Timer>(f.Form,"timer");
            timer.Start();Pump(400);Scroll(f.Details,220);Point before=Position(f.Details);
            Check(before.Y>0,"test report not scrolled");
            Stopwatch watch=Stopwatch.StartNew();
            do { Pump(30);Check(Position(f.Details)==before,"timer or completion moved viewport"); }
            while(!Field<bool>(f.Form,"finishing")&&watch.ElapsedMilliseconds<8000);
            Check(running.Done&&Field<bool>(f.Form,"finishing"),"test did not complete");
            Check(running.Forward.Measure.Get().Good>0&&running.Reverse.Measure.Get().Good>0,"test directions did not receive");
            timer.Stop();ButtonNamed(f.Form,"Start test").PerformClick();
            Check(Position(f.Details).Y==0&&f.Details.SelectionLength==0,"new test did not reset report");
        });
        Console.WriteLine("RESULT: "+(tests-failures)+"/"+tests+" UI tests passed");
        return failures==0?0:1;
    }
}
