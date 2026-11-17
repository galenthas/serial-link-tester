using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SerialRf;

static class CoreTests {
    sealed class TrackedPort : IDuplexPort {
        readonly IDuplexPort inner;
        readonly ManualResetEvent releaseWrite=new ManualResetEvent(false);
        public int ReadCancels,WriteCancels,Disposals,Active,Writes;
        public long FirstWrite,LastWrite;
        public bool FailWrites,DropWrites,BlockWrites;
        public uint WarningMask;
        public int FailAtWrite;
        public readonly ManualResetEvent WriteEntered=new ManualResetEvent(false);
        public TrackedPort(IDuplexPort inner) { this.inner=inner; }
        public int Read(byte[] bytes) { Interlocked.Increment(ref Active);try{return inner.Read(bytes);}finally{Interlocked.Decrement(ref Active);} }
        public void Write(byte[] bytes) {
            Interlocked.Increment(ref Active);
            try {
                long now=Stopwatch.GetTimestamp();Interlocked.CompareExchange(ref FirstWrite,now,0);LastWrite=now;Writes++;WriteEntered.Set();
                if(FailWrites||(FailAtWrite>0&&Writes>=FailAtWrite))throw new IOException("injected reverse write failure");
                if(BlockWrites) { releaseWrite.WaitOne();throw new OperationCanceledException(); }
                if(!DropWrites)inner.Write(bytes);
            } finally { Interlocked.Decrement(ref Active); }
        }
        public uint Errors(out uint input,out uint output) { return inner.Errors(out input,out output)|WarningMask; }
        public void CancelRead() { Interlocked.Increment(ref ReadCancels);inner.CancelRead(); }
        public void CancelWrite() { Interlocked.Increment(ref WriteCancels);inner.CancelWrite();releaseWrite.Set(); }
        public void Cancel() { CancelRead();CancelWrite(); }
        public void Dispose() { Check(Active==0,"physical endpoint disposed while I/O active");Check(Interlocked.Increment(ref Disposals)==1,"physical endpoint disposed twice");inner.Dispose();WriteEntered.Dispose();releaseWrite.Dispose(); }
    }
    static int failures, tests;
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    static void Test(string name,Action test) { tests++;try{test();Console.WriteLine("PASS "+name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex);}}
    static Frame Frame(Guid id,int seq,int size,bool valid) { return new Frame { Run=id,Sequence=(ulong)seq,PayloadLength=size,PayloadOk=valid }; }
    static byte[] Join(params byte[][] arrays) { return arrays.SelectMany(x=>x).ToArray(); }
    static long T(double ms) { return (long)(ms*Stopwatch.Frequency/1000.0); }
    static int Main(string[] args) {
        Test("CRC32 standard known vector",delegate { Check(Protocol.Crc(Encoding.ASCII.GetBytes("123456789"),0,9)==0xcbf43926U,"CRC polynomial/order"); });
        Test("COBS arbitrary bytes and run boundaries",delegate {
            Random rng=new Random(71);
            foreach(int size in new[]{0,1,253,254,255,508,2048,2090})for(int pattern=0;pattern<3;pattern++) {
                byte[] raw=new byte[size];if(pattern==1)rng.NextBytes(raw);if(pattern==2)for(int i=0;i<size;i++)raw[i]=255;
                byte[] enc=Protocol.Encode(raw);Check(enc[enc.Length-1]==0,"delimiter");Check(!enc.Take(enc.Length-1).Contains((byte)0),"embedded delimiter");
                Check(raw.SequenceEqual(Protocol.Decode(enc,enc.Length-1)),"roundtrip size "+size);
            }
            Check(Protocol.Decode(new byte[]{4,1,2},3)==null,"reject overrun");
        });
        Test("Protocol limits and split/coalesced stream",delegate {
            Guid id=Guid.NewGuid();List<Frame> got=new List<Frame>();int bad=0;
            FrameParser p=new FrameParser { OnFrame=delegate(Frame f,long t){got.Add(f);},OnInvalid=delegate{bad++;} };
            byte[] data=Join(Protocol.Make(id,0,1),Protocol.Make(id,1,254),Protocol.Make(id,2,2048));
            for(int i=0;i<data.Length;) { int n=Math.Min(1+i%31,data.Length-i);byte[] b=new byte[n];Buffer.BlockCopy(data,i,b,0,n);p.Feed(b,n,1000);i+=n; }
            Check(got.Count==3&&bad==0&&got.All(x=>x.PayloadOk)&&got[2].Sequence==2,"fragmentation");
        });
        Test("Separate header and payload integrity",delegate {
            Guid id=Guid.NewGuid();byte[] enc=Protocol.Make(id,42,64);byte[] raw=Protocol.Decode(enc,enc.Length-1);
            raw[38]^=0x40;Frame f=Protocol.Parse(raw);Check(f!=null&&!f.PayloadOk&&f.Sequence==42,"payload bad still attributable");
            raw[24]^=1;Check(Protocol.Parse(raw)==null,"corrupt identity rejected");
        });
        Test("Pattern check detects payload with recomputed CRC",delegate {
            byte[] e=Protocol.Make(Guid.NewGuid(),0,64);byte[] r=Protocol.Decode(e,e.Length-1);r[36]^=1;Protocol.Put32(r,100,Protocol.Crc(r,36,64));
            Check(!Protocol.Parse(r).PayloadOk,"pattern not verified");
        });
        Test("Byte deletion, noise, bounded oversized frame and resync",delegate {
            Guid id=Guid.NewGuid();byte[] e=Protocol.Make(id,0,64);byte[] damaged=e.Where((b,i)=>i!=20).ToArray();
            byte[] oversized=Enumerable.Repeat((byte)55,5000).Concat(new byte[]{0}).ToArray();
            int good=0,bad=0;FrameParser p=new FrameParser { OnFrame=delegate(Frame f,long t){if(f.PayloadOk)good++;},OnInvalid=delegate{bad++;} };
            byte[] stream=Join(damaged,new byte[]{3,13,0},oversized,Protocol.Make(id,1,64));p.Feed(stream,stream.Length,0);p.Finish();
            Check(good==1&&bad==3,"recovery good="+good+" bad="+bad);
        });
        Test("Trailing partial frame is reported once",delegate {
            int bad=0;FrameParser p=new FrameParser { OnInvalid=delegate{bad++;} };byte[] e=Protocol.Make(Guid.NewGuid(),0,64);p.Feed(e,10,0);p.Finish();p.Finish();Check(bad==1,"truncated tail");
        });
        Test("Loss including missing tail, corruption, duplicates and reordering",delegate {
            Measurement m=new Measurement(new TestConfig());long now=Stopwatch.GetTimestamp();
            for(int i=0;i<5;i++){int index=m.BeginSend(100,64,now);m.CompleteSend(index);}
            m.Receive(Frame(m.RunId,2,64,true),now+T(30));m.Receive(Frame(m.RunId,0,64,true),now+T(10));m.Receive(Frame(m.RunId,0,64,true),now+T(12));m.Receive(Frame(m.RunId,1,64,false),now+T(20));
            m.Receive(Frame(Guid.NewGuid(),3,64,true),now+T(20));m.InvalidFrame();m.Finish();Snapshot s=m.Get();
            Check(s.Sent==5&&s.Received==3&&s.Good==2&&s.Corrupt==1&&s.Missing==2,"loss arithmetic");
            Check(s.Duplicates==1&&s.Reordered==2&&s.Foreign==1&&s.Invalid==1,"classification");
        });
        Test("Known latency distribution and jitter definition",delegate {
            Measurement m=new Measurement(new TestConfig());long now=Stopwatch.GetTimestamp();
            double[] values={10,20,40};for(int i=0;i<3;i++){m.CompleteSend(m.BeginSend(100,64,now));m.Receive(Frame(m.RunId,i,64,true),now+T(values[i]));}
            Snapshot s=m.Get();Check(Math.Abs(s.MeanMs-70.0/3)<.001,"mean");Check(Math.Abs(s.JitterMs-15)<.001,"jitter");Check(s.P50Ms>=20&&s.P50Ms<=20.1&&s.P99Ms>=40&&s.P99Ms<=40.1,"quantiles");
            Check(Math.Abs(s.StdDevMs-Math.Sqrt(233.33333333))<.001,"sample stddev");
        });
        Test("Late reply removes overdue status but is retained",delegate {
            TestConfig c=new TestConfig { TimeoutMs=100 };Measurement m=new Measurement(c);long start=Stopwatch.GetTimestamp()-T(200);
            m.CompleteSend(m.BeginSend(100,64,start));Check(m.Get().Overdue==1,"expiry");m.Receive(Frame(m.RunId,0,64,true),Stopwatch.GetTimestamp());Snapshot s=m.Get();Check(s.Missing==0&&s.Late==1&&s.Overdue==0,"late reply accounting");
        });
        Test("Early response before WriteFile completion and failed-write isolation",delegate {
            Measurement m=new Measurement(new TestConfig());long now=Stopwatch.GetTimestamp();
            m.BeginSend(100,64,now);m.Receive(Frame(m.RunId,0,64,true),now+T(30));m.CompleteSend(0);
            Check(m.Get().Missing==0,"early response double counted");
            m.BeginSend(100,64,now);m.Receive(Frame(m.RunId,1,64,true),now+T(40));
            m.CompleteSend(m.BeginSend(100,64,now));
            Check(m.Get().Missing==1,"a failed write response masked another missing packet");
        });
        Test("Repeated start-stop releases software sessions",delegate {
            for(int i=0;i<4;i++)using(TestEngine e=new TestEngine(new TestConfig { Demo=true,DurationSeconds=60,TimeoutMs=100 })) {
                e.Start();Thread.Sleep(25);e.Abort();Check(e.Wait(2000),"session did not close");Check(e.Measure.Get().Error.Length>0,"abort must be incomplete");
            }
        });
        Test("Native backend rejects nonexistent COM without a process crash",delegate {
            bool thrown=false;try{using(NativeSerial port=new NativeSerial("COM999999",115200)){} }catch(System.ComponentModel.Win32Exception){thrown=true;}
            Check(thrown,"invalid device unexpectedly accepted");
        });
        Test("Invalid configuration rejected before opening ports",delegate {
            foreach(TestConfig c in new[]{new TestConfig { TxPort="COM1",RxPort="COM1" },new TestConfig { Demo=true,Baud=0 },new TestConfig { Demo=true,Payload=2049 },new TestConfig { Demo=true,TimeoutMs=0 }}) {
                bool thrown=false;try{c.Validate();}catch(ArgumentException){thrown=true;}Check(thrown,"bad config accepted");
            }
        });
        Test("Engine clean end-to-end, fragmented transport, report export",delegate {
            TestConfig c=new TestConfig { Demo=true,DurationSeconds=1,TimeoutMs=100,Throughput=true,LoadPercent=90 };
            TestEngine e=new TestEngine(c,delegate(TestConfig x){IPort port=new DemoPort(false);return new IPort[]{port,port};});e.Start();Check(e.Wait(5000),"engine hung");Snapshot s=e.Measure.Get();
            Check(e.Done&&s.Error==""&&s.Sent>20&&s.Missing==0&&s.Corrupt==0&&s.Invalid==0&&s.Good==s.Sent&&s.LatencyCount==s.Good,"engine clean path");
            Console.WriteLine("INFO Native Windows pacing: high_resolution="+s.HighResolutionPacing+", completed_frames="+s.Sent+", duration_s="+s.SendSeconds.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture));
            string dir=args.Length>0?args[0]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test_report");Export.Save(dir,e);
            Check(File.Exists(Path.Combine(dir,"report.html"))&&File.ReadAllText(Path.Combine(dir,"summary.json")).Contains("Demo\":true"),"report/demo marker");
            Check(File.ReadAllLines(Path.Combine(dir,"packets.csv")).Length==s.Attempted+1,"CSV completeness");
        });
        Test("Engine detects deterministic losses corruption and repeats",delegate {
            TestConfig c=new TestConfig { Demo=true,DurationSeconds=1,TimeoutMs=100,Throughput=true,LoadPercent=100 };
            TestEngine e=new TestEngine(c);e.Start();Check(e.Wait(5000),"fault test hung");Snapshot s=e.Measure.Get();
            Check(s.Error==""&&s.Missing>0&&s.Corrupt>0&&s.Duplicates>0,"fault counters missing");
            Check(s.Good+s.Corrupt+s.Missing==s.Sent,"partition");
        });
        Test("Stop preserves receive window; abort terminates promptly",delegate {
            TestConfig c=new TestConfig { Demo=true,DurationSeconds=60,TimeoutMs=300,Throughput=false };
            TestEngine e=new TestEngine(c);e.Start();Thread.Sleep(80);Stopwatch sw=Stopwatch.StartNew();e.Stop();Check(e.Wait(3000),"stop hung");Check(sw.ElapsedMilliseconds>=250,"receive window skipped");
            TestEngine a=new TestEngine(c);a.Start();Thread.Sleep(40);a.Abort();Check(a.Wait(2000)&&a.Done,"abort hung");
        });
        Test("Open failure becomes a visible completed error",delegate {
            TestEngine e=new TestEngine(new TestConfig { Demo=true },delegate(TestConfig c){throw new IOException("expected open failure");});e.Start();Check(e.Wait(2000),"open failure hung");Check(e.Measure.Get().Error=="expected open failure"&&e.Done,"error propagation");
        });
        Test("Duplex configuration rejects loopback and identical ports",delegate {
            foreach(TestConfig c in new[]{new TestConfig { Demo=true,Bidirectional=true,Loopback=true },new TestConfig { Bidirectional=true,TxPort="COM3",RxPort="COM3" }}) {
                bool rejected=false;try{new TestSession(c);}catch(ArgumentException){rejected=true;}Check(rejected,"unsafe duplex setup accepted");
            }
        });
        Test("Direction leases cancel only their own operation and do not own handles",delegate {
            IDuplexPort[] pair=DuplexDemoEndpoint.Pair(false,false);TrackedPort p=new TrackedPort(pair[0]);
            DirectionPort receive=new DirectionPort(p,true),transmit=new DirectionPort(p,false);
            receive.Cancel();receive.Dispose();Check(p.ReadCancels==1&&p.WriteCancels==0&&p.Disposals==0,"RX cancellation affected TX or lifetime");
            transmit.Cancel();transmit.Dispose();Check(p.ReadCancels==1&&p.WriteCancels==1&&p.Disposals==0,"TX cancellation affected RX or lifetime");p.Dispose();pair[1].Dispose();
        });
        Test("Duplex concurrent clean streams open once, overlap and export independently",delegate {
            TestConfig c=new TestConfig { Demo=true,Bidirectional=true,TxPort="COM3",RxPort="COM4",DurationSeconds=1,TimeoutMs=150,Throughput=true,LoadPercent=90,Note="<test & conditions>" };
            int opens=0;TrackedPort a=null,b=null;
            using(TestSession session=new TestSession(c,delegate(TestConfig x) { opens++;IDuplexPort[] pair=DuplexDemoEndpoint.Pair(false,false);a=new TrackedPort(pair[0]);b=new TrackedPort(pair[1]);return new IDuplexPort[]{a,b}; })) {
                session.Start();Check(session.Wait(5000),"duplex hung");Snapshot f=session.Forward.Measure.Get(),r=session.Reverse.Measure.Get();
                Check(opens==1&&a.Disposals==1&&b.Disposals==1,"physical handles not shared/closed exactly once");
                Check(f.RunId!=r.RunId&&session.Reverse.Config.TxPort=="COM4"&&session.Reverse.Config.RxPort=="COM3","direction identity");
                Check(a.FirstWrite<b.LastWrite&&b.FirstWrite<a.LastWrite,"directions did not overlap");
                foreach(Snapshot s in new[]{f,r})Check(s.Sent>30&&s.Error==""&&s.Good==s.Sent&&s.Missing==0&&s.Corrupt==0&&s.Invalid==0&&s.Foreign==0,"clean direction not intact");
                string dir=Path.Combine(args.Length>0?args[0]:AppDomain.CurrentDomain.BaseDirectory,"duplex");Export.Save(dir,session);
                string[] rows=File.ReadAllLines(Path.Combine(dir,"packets.csv"));Check(rows.Length==f.Attempted+r.Attempted+1,"combined CSV row count");
                Check(rows[0].StartsWith("direction,tx_port,rx_port,sequence,"),"CSV direction missing");
                Check(rows.Count(x=>x.StartsWith("A_to_B,COM3,COM4,"))==f.Attempted&&rows.Count(x=>x.StartsWith("B_to_A,COM4,COM3,"))==r.Attempted,"CSV direction/ports mixed");
                string json=File.ReadAllText(Path.Combine(dir,"summary.json")),html=File.ReadAllText(Path.Combine(dir,"report.html"));
                Check(json.Contains(f.RunId)&&json.Contains(r.RunId)&&json.Contains("\"Bidirectional\":true"),"JSON omitted a direction/config");
                Check(html.Contains("SIMULATED")&&html.Contains("90% per direction")&&html.Contains("&lt;test &amp; conditions&gt;")&&html.Contains("B_to_A/report.html"),"comparison report scope/link/escaping");
                Check(File.Exists(Path.Combine(dir,"A_to_B","packets.csv"))&&File.Exists(Path.Combine(dir,"B_to_A","summary.json")),"direction reports absent");
            }
        });
        Test("Duplex asymmetric corruption and loss never contaminate clean direction",delegate {
            using(TestSession session=new TestSession(new TestConfig { Demo=true,Bidirectional=true,DurationSeconds=1,TimeoutMs=150,Throughput=true,LoadPercent=100 },delegate(TestConfig c){return DuplexDemoEndpoint.Pair(false,true);})) {
                session.Start();Check(session.Wait(5000),"asymmetric test hung");Snapshot f=session.Forward.Measure.Get(),r=session.Reverse.Measure.Get();
                Check(f.Good==f.Sent&&f.Missing==0&&f.Corrupt==0&&f.Invalid==0&&f.Foreign==0,"reverse faults contaminated forward");
                Check(r.Missing>0&&r.Corrupt>0&&r.Duplicates>0&&r.Foreign==0&&r.Good+r.Corrupt+r.Missing==r.Sent,"reverse fault classification");
            }
        });
        Test("Duplex latency probes progress independently when reverse path is absent",delegate {
            using(TestSession session=new TestSession(new TestConfig { Demo=true,Bidirectional=true,DurationSeconds=1,TimeoutMs=300 },delegate(TestConfig c){IDuplexPort[] pair=DuplexDemoEndpoint.Pair(false,false);return new IDuplexPort[]{pair[0],new TrackedPort(pair[1]) { DropWrites=true }};})) {
                session.Start();Check(session.Wait(5000),"missing direction blocked session");Snapshot f=session.Forward.Measure.Get(),r=session.Reverse.Measure.Get();
                Check(f.Good>10&&f.Good==f.Sent&&f.Missing==0,"reverse timeout stalled forward");
                Check(r.Sent>0&&r.Good==0&&r.Missing==r.Sent&&r.Error=="","absent reverse path not reported as loss");
            }
        });
        Test("Duplex Stop drains both directions and Abort cancels blocked writes safely",delegate {
            using(TestSession session=new TestSession(new TestConfig { Demo=true,Bidirectional=true,DurationSeconds=60,TimeoutMs=300 })) {
                session.Start();Thread.Sleep(100);Stopwatch sw=Stopwatch.StartNew();session.Stop();Check(session.Wait(4000),"duplex stop hung");
                Check(sw.ElapsedMilliseconds>=250&&session.Forward.Measure.Get().Error==""&&session.Reverse.Measure.Get().Error=="","stop skipped drain or marked abort");
            }
            TrackedPort a=null,b=null;
            using(TestSession session=new TestSession(new TestConfig { Demo=true,Bidirectional=true,DurationSeconds=60,TimeoutMs=300 },delegate(TestConfig c){IDuplexPort[] pair=DuplexDemoEndpoint.Pair(false,false);a=new TrackedPort(pair[0]) { BlockWrites=true };b=new TrackedPort(pair[1]) { BlockWrites=true };return new IDuplexPort[]{a,b};})) {
                session.Start();Stopwatch started=Stopwatch.StartNew();while((a==null||b==null)&&started.ElapsedMilliseconds<2000)Thread.Sleep(5);
                Check(a!=null&&b!=null&&a.WriteEntered.WaitOne(2000)&&b.WriteEntered.WaitOne(2000),"writes did not start");session.Abort();Check(session.Wait(3000),"blocked I/O not canceled");
                Check(a.Disposals==1&&b.Disposals==1&&a.Active==0&&b.Active==0,"unsafe endpoint cleanup");
                Check(session.Forward.Measure.HasError&&session.Reverse.Measure.HasError,"abort not marked incomplete in both directions");
            }
        });
        Test("Duplex port/write failure finishes both directions and releases endpoints",delegate {
            using(TestSession session=new TestSession(new TestConfig { Demo=true,Bidirectional=true },delegate(TestConfig c){throw new IOException("expected duplex open failure");})) {
                session.Start();Check(session.Wait(2000)&&session.Forward.Done&&session.Reverse.Done,"open failure hung");Check(session.Forward.Measure.Get().Error=="expected duplex open failure"&&session.Reverse.Measure.HasError,"open error hidden");
            }
            TrackedPort a=null,b=null;
            using(TestSession session=new TestSession(new TestConfig { Demo=true,Bidirectional=true,DurationSeconds=60,TimeoutMs=300 },delegate(TestConfig c){IDuplexPort[] pair=DuplexDemoEndpoint.Pair(false,false);a=new TrackedPort(pair[0]);b=new TrackedPort(pair[1]) { FailWrites=true };return new IDuplexPort[]{a,b};})) {
                session.Start();Check(session.Wait(3000),"write failure did not stop peer");Check(session.Reverse.Measure.Get().Error=="injected reverse write failure"&&session.Forward.Measure.HasError,"direction error not preserved");
                Check(a.Disposals==1&&b.Disposals==1,"failure leaked shared endpoints");
            }
        });
        Test("Single-direction session retains legacy behavior",delegate {
            using(TestSession session=new TestSession(new TestConfig { Demo=true,Bidirectional=false,DurationSeconds=1,TimeoutMs=100 })) { session.Start();Check(session.Wait(4000)&&session.Reverse==null&&session.Forward.Measure.Get().Good>5&&!session.Forward.Measure.HasError,"one-way regression"); }
        });
        Test("Startup defaults run both directions and preserve a one-second final window",delegate {
            using(TestSession session=new TestSession(new TestConfig { Demo=true,DurationSeconds=1 },delegate(TestConfig c){return DuplexDemoEndpoint.Pair(false,false);})) {
                session.Start();Check(session.Wait(5000)&&session.Reverse!=null,"default setup is not bidirectional");
                foreach(TestEngine engine in new[]{session.Forward,session.Reverse}) {
                    Snapshot s=engine.Measure.Get();Check(s.Good>5&&s.Good==s.Sent&&s.Error=="","default stream did not finish");
                    Check(engine.Config.TimeoutMs==1000&&s.Elapsed-s.SendSeconds>=.95&&s.Elapsed-s.SendSeconds<2,"default final receive window not one second");
                }
            }
        });
        Test("Serial warning bitmasks decode combined and unknown flags without becoming fatal",delegate {
            Measurement m=new Measurement(new TestConfig());
            Check(m.ObservePort(0x18,2,3)==0x18,"initial framing/break flags");
            Check(m.ObservePort(0x08,4,5)==0,"repeated warning logged as a new type");
            Check(m.ObservePort(0x47,0,0)==0x47,"new flags hidden");m.ObservePort(0,0,0);
            Snapshot s=m.Get();Check(s.FramingWarnings==2&&s.BreakWarnings==1&&s.OverrunWarnings==1&&s.ReceiveOverflowWarnings==1&&s.ParityWarnings==1&&s.OtherWarnings==1,"per-type observation counts");
            Check(s.PcErrorEvents==3&&s.PcErrorMask==0x5f&&s.MaxRxQueue==4&&s.MaxTxQueue==5,"history/mask/queues");
            Check(s.SerialWarningTypes=="Framing, Break, Overrun, RX overflow, Parity, Other (0x40)","mask names/order or unknown flags");
            Check(!m.HasError&&s.Error==""&&SerialWarnings.Names(0)=="None","warning treated as fatal");
            Measurement next=new Measurement(new TestConfig());Check(next.Get().PcErrorEvents==0&&next.Get().SerialWarningTypes=="None","warnings leaked into a new test");
        });
        Test("Framing and break warnings allow both directions to finish and export amber diagnostics",delegate {
            using(TestSession session=new TestSession(new TestConfig { Demo=true,DurationSeconds=1,TimeoutMs=100,Throughput=true },delegate(TestConfig c){IDuplexPort[] pair=DuplexDemoEndpoint.Pair(false,false);return new IDuplexPort[]{pair[0],new TrackedPort(pair[1]) { WarningMask=0x18 }};})) {
                session.Start();Check(session.Wait(5000),"warning-only session hung");
                foreach(TestEngine engine in new[]{session.Forward,session.Reverse}) { Snapshot s=engine.Measure.Get();Check(s.Good==s.Sent&&s.Sent>30&&s.Missing==0&&s.Error==""&&s.Phase=="Complete","serial warning aborted or damaged the stream"); }
                Check((session.Forward.Measure.Get().PcErrorMask|session.Reverse.Measure.Get().PcErrorMask)==0x18,"warnings missing from session");
                string dir=Path.Combine(args.Length>0?args[0]:AppDomain.CurrentDomain.BaseDirectory,"serial_warnings");Export.Save(dir,session);
                string html=File.ReadAllText(Path.Combine(dir,"report.html")),json=File.ReadAllText(Path.Combine(dir,"summary.json"));
                Check(html.Contains("Serial warnings (non-fatal)")&&html.Contains("Framing")&&html.Contains("Break")&&html.Contains("#f6c66b")&&!html.Contains("Test-stopping error"),"warning-only report severity incorrect");
                Check(json.Contains("\"serial_warnings\"")&&json.Contains("\"stops_test\":false")&&json.Contains("\"test_stopping_error\":\"\""),"JSON warning/error separation missing");
            }
        });
        Test("A write failure remains distinct from previously recorded serial warnings",delegate {
            using(TestEngine engine=new TestEngine(new TestConfig { Demo=true,Bidirectional=false,DurationSeconds=60,TimeoutMs=100,Throughput=true },delegate(TestConfig c){IPort port=new TrackedPort(new DemoPort(false)) { WarningMask=0x18,FailAtWrite=2 };return new[]{port,port};})) {
                engine.Start();Check(engine.Wait(3000),"fatal test hung");Snapshot s=engine.Measure.Get();
                Check(s.Error=="injected reverse write failure"&&s.PcErrorMask==0x18&&s.Phase=="Finished with errors","fatal error or warning history lost");
                string dir=Path.Combine(args.Length>0?args[0]:AppDomain.CurrentDomain.BaseDirectory,"fatal_with_warnings");Export.Save(dir,engine);
                string html=File.ReadAllText(Path.Combine(dir,"report.html")),json=File.ReadAllText(Path.Combine(dir,"summary.json"));
                Check(html.Contains("Test-stopping error")&&html.Contains("Serial warnings (non-fatal)")&&html.Contains("#ff7e7e")&&html.Contains("#f6c66b"),"fatal/warning sections not separate");
                Check(json.Contains("\"test_stopping_error\":\"injected reverse write failure\"")&&json.Contains("\"framing\":"),"fatal JSON overwrote warnings");
            }
        });
        Console.WriteLine("RESULT "+(tests-failures)+"/"+tests+" passed; no physical serial hardware used.");
        return failures==0?0:1;
    }
}
