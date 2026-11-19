using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SerialRf {
    internal sealed class TestPort : IDuplexPort {
        sealed class Item { public byte[] Data; public long Due; }
        readonly ConcurrentQueue<Item> queue = new ConcurrentQueue<Item>();
        readonly AutoResetEvent available = new AutoResetEvent(false);
        volatile bool readCanceled, writeCanceled;
        byte[] current; int offset, sequence;
        readonly bool faults;
        public static IDuplexPort[] WithFaults(TestConfig config) {
            TestPort port = new TestPort(true);
            return new IDuplexPort[] { port, port };
        }
        public TestPort(bool injectFaults) { faults = injectFaults; }
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

    internal sealed class TestDuplexEndpoint : IDuplexPort {
        readonly TestPort incoming,outgoing;
        TestDuplexEndpoint(TestPort incoming,TestPort outgoing) { this.incoming=incoming;this.outgoing=outgoing; }
        public static IDuplexPort[] Pair(bool faultsForward,bool faultsReverse) {
            TestPort ab=new TestPort(faultsForward),ba=new TestPort(faultsReverse);
            return new IDuplexPort[]{new TestDuplexEndpoint(ba,ab),new TestDuplexEndpoint(ab,ba)};
        }
        public int Read(byte[] buffer) { return incoming.Read(buffer); }
        public void Write(byte[] buffer) { outgoing.Write(buffer); }
        public uint Errors(out uint input,out uint output) { input=output=0;return 0; }
        public void CancelRead() { incoming.CancelRead(); }
        public void CancelWrite() { outgoing.CancelWrite(); }
        public void Cancel() { CancelRead();CancelWrite(); }
        public void Dispose() { incoming.Dispose(); }
    }

}
