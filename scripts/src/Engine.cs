using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SerialRf {
    public sealed class DemoPort : IDuplexPort {
        sealed class Item { public byte[] Data; public long Due; }
        readonly ConcurrentQueue<Item> queue = new ConcurrentQueue<Item>();
        readonly AutoResetEvent available = new AutoResetEvent(false);
        volatile bool readCanceled, writeCanceled;
        byte[] current; int offset, sequence;
        readonly bool faults;
        public DemoPort(bool injectFaults) { faults = injectFaults; }
        public void Write(byte[] bytes) {
            if (writeCanceled) throw new OperationCanceledException();
            sequence++;
            if (faults && sequence % 29 == 0) return;
            byte[] data = (byte[])bytes.Clone();
            if (faults && sequence % 37 == 0) {
                byte[] raw = Protocol.Decode(data, data.Length - 1);
                raw[36] ^= 0x40; data = Protocol.Encode(raw);
            }
            long due = Stopwatch.GetTimestamp() + (long)((25 + (sequence * 7 % 19)) * Stopwatch.Frequency / 1000.0);
            queue.Enqueue(new Item { Data = data, Due = due });
            if (faults && sequence % 41 == 0) queue.Enqueue(new Item { Data = data, Due = due });
            available.Set();
        }
        public int Read(byte[] bytes) {
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            while (!readCanceled) {
                if (current != null) {
                    int n = Math.Min(Math.Min(17, bytes.Length), current.Length - offset);
                    Buffer.BlockCopy(current, offset, bytes, 0, n); offset += n;
                    if (offset == current.Length) { current = null; offset = 0; }
                    return n;
                }
                Item item;
                if (queue.TryPeek(out item) && Stopwatch.GetTimestamp() >= item.Due && queue.TryDequeue(out item)) { current = item.Data; continue; }
                if (Stopwatch.GetTimestamp() >= deadline) return 0;
                available.WaitOne(2);
            }
            return 0;
        }
        public uint Errors(out uint input, out uint output) { input = output = 0; return 0; }
        public void CancelRead() { readCanceled = true; available.Set(); }
        public void CancelWrite() { writeCanceled = true; }
        public void Cancel() { CancelRead(); CancelWrite(); }
        public void Dispose() { available.Dispose(); }
    }

    public sealed class TestEngine : IDisposable {
        public readonly TestConfig Config;
        public readonly Measurement Measure;
        public readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();
        readonly ManualResetEvent stopSend = new ManualResetEvent(false);
        readonly AutoResetEvent reply = new AutoResetEvent(false);
        volatile bool stopRead, aborted;
        IPort tx, rx;
        Thread reader, coordinator;
        readonly Func<TestConfig, IPort[]> factory;
        PrecisionWaiter pacer;
        public volatile bool Done;
        public TestEngine(TestConfig config, Func<TestConfig, IPort[]> portFactory) { Config = config.Direction(false); Config.Validate(); Measure = new Measurement(Config); factory = portFactory; }
        public TestEngine(TestConfig config) : this(config, null) { }
        public void Start() { coordinator = new Thread(Run) { IsBackground = true, Name = "Serial test coordinator" }; coordinator.Start(); }
        public void Stop() { stopSend.Set(); }
        static void CancelPort(IPort port) { if(port!=null)try { port.Cancel(); } catch(ObjectDisposedException) { } }
        public void Abort() { if(Done)return;aborted = true; Measure.Fail("Test canceled before the receive window closed."); stopRead = true; stopSend.Set(); CancelPort(tx); if(rx!=tx)CancelPort(rx); }
        public bool Wait(int ms) { return coordinator == null || coordinator.Join(ms); }
        internal void FailBeforeStart(string message) { Measure.Fail(message); Measure.EndSend(); Measure.Finish(); Done = true; }
        void Log(string message) { Events.Enqueue(message); }
        void ObservePort(IPort port,string name) {
            uint input,output;uint mask=port.Errors(out input,out output);
            uint firstSeen=Measure.ObservePort(mask,input,output);
            if(firstSeen!=0)Log("Serial warning (non-fatal) · "+(string.IsNullOrEmpty(name)?"port":name)+": "+SerialWarnings.Names(firstSeen)+". Recorded; test continues.");
        }
        void ReadLoop() {
            FrameParser parser = new FrameParser();
            parser.OnFrame = delegate(Frame frame, long stamp) { Measure.Receive(frame, stamp); reply.Set(); };
            parser.OnInvalid = delegate { Measure.InvalidFrame(); };
            byte[] bytes = new byte[4096];
            try {
                while (!stopRead) {
                    int count = rx.Read(bytes);
                    long stamp = Stopwatch.GetTimestamp();
                    if (count > 0) { Measure.AddWireBytes(count); parser.Feed(bytes, count, stamp); }
                    if (!stopRead)ObservePort(rx,Config.Loopback?Config.TxPort:Config.RxPort);
                }
                parser.Finish();
            } catch (Exception ex) {
                if (!stopRead) { Measure.Fail(ex.Message); Log(ex.Message); stopSend.Set(); aborted = true; }
            }
        }
        bool Until(long target) { return pacer.Until(target, stopSend); }
        void Run() {
            try {
                pacer = new PrecisionWaiter();
                Measure.HighResolutionPacing = pacer.HighResolution;
                Log(pacer.HighResolution ? "High-resolution transmit timer enabled." : "Standard timer in use; actual throughput may be below the target.");
                Measure.Phase("Opening ports");
                if (factory != null) { IPort[] pair = factory(Config); tx = pair[0]; rx = pair[1]; }
                else if (Config.Demo) { tx = rx = new DemoPort(true); }
                else {
                    tx = new NativeSerial(Config.TxPort, Config.Baud);
                    rx = Config.Loopback ? tx : new NativeSerial(Config.RxPort, Config.Baud);
                }
                Log(Config.Demo ? "DEMO: simulated results. No COM port was opened." : "Ports open • 8N1 • flow control off");
                // Let pre-existing bytes drain naturally; do not repeatedly purge device buffers.
                reader = new Thread(ReadLoop) { IsBackground = true, Name = "Serial test RX", Priority = ThreadPriority.AboveNormal };
                reader.Start();
                Measure.Phase("Running");
                long deadline = Stopwatch.GetTimestamp() + Config.DurationSeconds * Stopwatch.Frequency;
                long next = Stopwatch.GetTimestamp(); ulong seq = 0;
                while (!stopSend.WaitOne(0) && Stopwatch.GetTimestamp() < deadline) {
                    byte[] frame = Protocol.Make(Measure.RunId, seq, Config.Payload);
                    if (!Until(next) || Stopwatch.GetTimestamp() >= deadline) break;
                    long stamp = Stopwatch.GetTimestamp();
                    int index = Measure.BeginSend(frame.Length, Config.Payload, stamp);
                    tx.Write(frame);
                    Measure.CompleteSend(index); seq++;
                    ObservePort(tx,Config.TxPort);
                    if (Config.Throughput) {
                        double period = frame.Length * 10.0 / Config.Baud * 100.0 / Config.LoadPercent;
                        // No catch-up bursts after scheduler stalls.
                        next = Math.Max(stamp + (long)(period * Stopwatch.Frequency), Stopwatch.GetTimestamp());
                    } else {
                        long timeout = stamp + Config.TimeoutMs * Stopwatch.Frequency / 1000;
                        while (!Measure.HasReply(index) && Stopwatch.GetTimestamp() < timeout && !stopSend.WaitOne(0) && !aborted) reply.WaitOne(10);
                        // Minimum 20 ms between probes; only one unresolved probe until its timeout.
                        next = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50;
                    }
                }
                Measure.EndSend();
                if (!aborted) {
                    Measure.Phase("Waiting for final packets");
                    Log("Transmission stopped; final receive window: " + Config.TimeoutMs + " ms.");
                    long drain = Stopwatch.GetTimestamp() + Config.TimeoutMs * Stopwatch.Frequency / 1000;
                    while (!aborted && Stopwatch.GetTimestamp() < drain) Thread.Sleep(20);
                }
            } catch (Exception ex) {
                if (!(aborted && ex is OperationCanceledException)) { Measure.Fail(ex.Message); Log(ex.Message); }
            } finally {
                Measure.EndSend(); stopRead = true;
                if (rx != null) rx.Cancel();
                if (tx != null && tx != rx) tx.Cancel();
                // Joining is required before native overlapped storage can be released.
                if (reader != null) reader.Join();
                if (rx != null) rx.Dispose();
                if (tx != null && tx != rx) tx.Dispose();
                if (pacer != null) pacer.Dispose();
                Measure.Finish(); Done = true;
                Log("Test finished. Loss is measured at the end of the configured receive window.");
            }
        }
        public void Dispose() {
            if(!Done) { Abort(); if(!Wait(15000))return; }
            stopSend.Dispose(); reply.Dispose();
        }
    }
}
