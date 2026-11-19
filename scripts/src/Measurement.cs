using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SerialRf {
    public sealed class TestConfig {
        public const int DefaultTimeoutMs=1000;
        public string TxPort = "", RxPort = "", Setup = "Two ports / bidirectional", Mode = "Latency and jitter", Note = "";
        public int Baud = 115200, Payload = 64, DurationSeconds = 60, LoadPercent = 90, TimeoutMs = DefaultTimeoutMs;
        public bool Loopback, Throughput;
        public bool Bidirectional=true;
        public TestConfig Direction(bool reverse) {
            TestConfig c = (TestConfig)MemberwiseClone();
            c.Bidirectional = false;
            if (reverse) { c.TxPort = RxPort; c.RxPort = TxPort; }
            return c;
        }
        public void Validate() {
            if (Loopback && Bidirectional) throw new ArgumentException("Bidirectional testing requires two different ports.");
            Setup = Bidirectional ? "Two ports / bidirectional" : Loopback ? "One port / round trip" : "Two ports / one way";
            Mode = Throughput ? "Integrity and throughput" : "Latency and jitter";
            if (Baud < 1200 || Baud > 1000000) throw new ArgumentException("Baud rate must be between 1200 and 1000000.");
            if (Payload < 1 || Payload > Protocol.MaxPayload) throw new ArgumentException("Payload must be between 1 and 2048 bytes.");
            if (DurationSeconds < 1 || DurationSeconds > 3600) throw new ArgumentException("Duration must be between 1 and 3600 seconds.");
            if (LoadPercent < 1 || LoadPercent > 100 || TimeoutMs < 100 || TimeoutMs > 60000) throw new ArgumentException("Invalid load or receive timeout.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(TxPort, "^COM[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new ArgumentException("Select a transmit COM port.");
            if (!Loopback && !System.Text.RegularExpressions.Regex.IsMatch(RxPort, "^COM[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new ArgumentException("Select a receive COM port.");
            if (!Loopback && TxPort.Equals(RxPort, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose different COM ports for a two-port test, or select the one-port loopback setup.");
        }
    }
    public struct PacketRecord {
        public long TxTicks, RxTicks;
        public int WireBytes, PayloadBytes;
        public bool Completed, Received, Valid, TimedOut;
    }
    public sealed class Snapshot {
        public string Phase, Error, RunId, StartedUtc;
        public bool Done;
        public bool HighResolutionPacing;
        public long Attempted, Sent, Received, Good, Corrupt, Invalid, Duplicates, Reordered, Foreign, Late, Missing, Overdue;
        public long TxWireBytes, RxWireBytes, GoodPayloadBytes, PcErrorEvents;
        public long FramingWarnings, BreakWarnings, OverrunWarnings, ReceiveOverflowWarnings, ParityWarnings, OtherWarnings;
        public string SerialWarningTypes;
        public uint PcErrorMask, MaxRxQueue, MaxTxQueue;
        public double Elapsed, SendSeconds, ReceiveSpanSeconds, AveragePayloadBps, ActivePayloadBps, MeanMs, MinMs, MaxMs, P50Ms, P95Ms, P99Ms, StdDevMs, JitterMs;
        public long LatencyCount;
    }
    public sealed class Measurement {
        public const int FrameLimit = 1000000;
        readonly object gate = new object();
        readonly List<PacketRecord> records = new List<PacketRecord>();
        readonly int[] histogram = new int[600001]; // 0.1 ms bins, final bin >= 60 s.
        readonly TestConfig config;
        public readonly Guid RunId = Guid.NewGuid();
        public bool HighResolutionPacing;
        public readonly long Started = Stopwatch.GetTimestamp();
        readonly DateTime utc = DateTime.UtcNow;
        string phase = "Preparing", error = "";
        bool done; long end, sentEnd, firstRx, lastRx;
        long sent, received, receivedCompleted, good, corrupt, invalid, duplicate, reorder, foreign, late, overdue, txBytes, rxBytes, payloadBytes, errors, latencyCount;
        uint errorMask, maxRxQueue, maxTxQueue;
        long framingWarnings,breakWarnings,overrunWarnings,receiveOverflowWarnings,parityWarnings,otherWarnings;
        int expireIndex, highestReceived = -1, maxBin;
        double mean, m2, min = double.MaxValue, max, prevLatency, jitterSum;
        public Measurement(TestConfig c) { config = c; }
        public static double Seconds(long ticks) { return (double)ticks / Stopwatch.Frequency; }
        public static double Milliseconds(long ticks) { return Seconds(ticks) * 1000.0; }
        public void Phase(string value) { lock (gate) phase = value; }
        public int BeginSend(int wire, int payload, long stamp) {
            lock (gate) {
                if (records.Count >= FrameLimit) throw new InvalidOperationException("The one-million-packet session limit was reached. Export your results and start a new test.");
                records.Add(new PacketRecord { TxTicks = stamp, WireBytes = wire, PayloadBytes = payload });
                return records.Count - 1;
            }
        }
        public void CompleteSend(int index) {
            lock (gate) {
                PacketRecord r = records[index];
                if (!r.Completed) { r.Completed = true; sent++; if(r.Received)receivedCompleted++; txBytes += r.WireBytes; records[index] = r; }
            }
        }
        public bool HasReply(int index) { lock (gate) return index < records.Count && records[index].Received; }
        public void InvalidFrame() { lock (gate) invalid++; }
        public void AddWireBytes(int count) { lock (gate) rxBytes += count; }
        public uint ObservePort(uint mask, uint inQueue, uint outQueue) {
            lock (gate) {
                uint firstSeen=mask&~errorMask;
                if (mask != 0) {
                    errors++;errorMask|=mask;
                    if((mask&SerialWarnings.Framing)!=0)framingWarnings++;
                    if((mask&SerialWarnings.Break)!=0)breakWarnings++;
                    if((mask&SerialWarnings.Overrun)!=0)overrunWarnings++;
                    if((mask&SerialWarnings.ReceiveOverflow)!=0)receiveOverflowWarnings++;
                    if((mask&SerialWarnings.Parity)!=0)parityWarnings++;
                    if((mask&~SerialWarnings.KnownMask)!=0)otherWarnings++;
                }
                maxRxQueue=Math.Max(maxRxQueue,inQueue);maxTxQueue=Math.Max(maxTxQueue,outQueue);
                return firstSeen;
            }
        }
        public void Receive(Frame frame, long stamp) {
            lock (gate) {
                if (frame.Run != RunId || frame.Sequence >= (ulong)records.Count) { foreign++; return; }
                int index = (int)frame.Sequence;
                PacketRecord r = records[index];
                if (r.Received) { duplicate++; return; }
                if (frame.PayloadLength != r.PayloadBytes) { invalid++; return; }
                r.Received = true; r.Valid = frame.PayloadOk; r.RxTicks = stamp;
                if (r.TimedOut) overdue--;
                if (Milliseconds(stamp - r.TxTicks) > config.TimeoutMs) late++;
                records[index] = r; received++;
                if(r.Completed)receivedCompleted++;
                if (index < highestReceived) reorder++;
                highestReceived = Math.Max(highestReceived, index);
                if (!frame.PayloadOk) { corrupt++; return; }
                good++; payloadBytes += r.PayloadBytes;
                if (firstRx == 0) firstRx = stamp;
                lastRx = stamp;
                double ms = Math.Max(0, Milliseconds(stamp - r.TxTicks));
                latencyCount++;
                double delta = ms - mean; mean += delta / latencyCount; m2 += delta * (ms - mean);
                if (latencyCount > 1) jitterSum += Math.Abs(ms - prevLatency);
                prevLatency = ms; min = Math.Min(min, ms); max = Math.Max(max, ms);
                int bin = (int)Math.Min(600000, Math.Ceiling(ms * 10));
                histogram[bin]++; maxBin = Math.Max(maxBin, bin);
            }
        }
        public void EndSend() { lock (gate) if (sentEnd == 0) sentEnd = Stopwatch.GetTimestamp(); }
        public void Fail(string message) { lock (gate) { if (error.Length == 0) error = message; phase = "Error"; } }
        public bool HasError { get { lock(gate)return error.Length>0; } }
        public void Finish() { lock (gate) { end = Stopwatch.GetTimestamp(); done = true; phase = error.Length == 0 ? "Complete" : "Finished with errors"; } }
        double Percentile(double fraction) {
            if (latencyCount == 0) return 0;
            long target = (long)Math.Ceiling(latencyCount * fraction), sum = 0;
            for (int i = 0; i <= maxBin; i++) { sum += histogram[i]; if (sum >= target) return i == 600000 ? double.PositiveInfinity : i / 10.0; }
            return max;
        }
        public Snapshot Get() {
            lock (gate) {
                long now = done ? end : Stopwatch.GetTimestamp();
                while (expireIndex < records.Count && Milliseconds(now - records[expireIndex].TxTicks) >= config.TimeoutMs) {
                    PacketRecord r = records[expireIndex];
                    if(!r.Completed&&!done)break;
                    if (!r.Received && r.Completed) { r.TimedOut = true; overdue++; records[expireIndex] = r; }
                    expireIndex++;
                }
                double elapsed = Seconds(now - Started);
                double receiveSpan = firstRx == 0 ? 0 : Seconds(lastRx - firstRx);
                return new Snapshot {
                    Phase = phase, Error = error, RunId = RunId.ToString(), StartedUtc = utc.ToString("o"), Done = done, HighResolutionPacing = HighResolutionPacing,
                    Attempted = records.Count, Sent = sent, Received = received, Good = good, Corrupt = corrupt, Invalid = invalid, Duplicates = duplicate, Reordered = reorder, Foreign = foreign, Late = late,
                    Missing = Math.Max(0, sent - receivedCompleted), Overdue = overdue, TxWireBytes = txBytes, RxWireBytes = rxBytes, GoodPayloadBytes = payloadBytes,
                    PcErrorEvents = errors, PcErrorMask = errorMask, MaxRxQueue = maxRxQueue, MaxTxQueue = maxTxQueue,
                    FramingWarnings=framingWarnings,BreakWarnings=breakWarnings,OverrunWarnings=overrunWarnings,ReceiveOverflowWarnings=receiveOverflowWarnings,ParityWarnings=parityWarnings,OtherWarnings=otherWarnings,SerialWarningTypes=SerialWarnings.Names(errorMask),
                    Elapsed = elapsed, SendSeconds = Seconds((sentEnd == 0 ? now : sentEnd) - Started), ReceiveSpanSeconds = receiveSpan,
                    AveragePayloadBps = elapsed > 0 ? payloadBytes * 8.0 / elapsed : 0,
                    ActivePayloadBps = receiveSpan > 0 ? Math.Max(0, payloadBytes - config.Payload) * 8.0 / receiveSpan : 0,
                    LatencyCount = latencyCount, MeanMs = mean, MinMs = latencyCount == 0 ? 0 : min, MaxMs = max,
                    P50Ms = Percentile(.50), P95Ms = Percentile(.95), P99Ms = Percentile(.99), StdDevMs = latencyCount > 1 ? Math.Sqrt(m2 / (latencyCount - 1)) : 0,
                    JitterMs = latencyCount > 1 ? jitterSum / (latencyCount - 1) : 0
                };
            }
        }
        public PacketRecord[] Records() { lock (gate) return records.ToArray(); }
    }
}
