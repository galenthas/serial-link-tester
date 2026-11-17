using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SerialRf {
    public interface IPort : IDisposable {
        int Read(byte[] buffer);
        void Write(byte[] buffer);
        uint Errors(out uint inputQueue, out uint outputQueue);
        void Cancel();
    }
    public interface IDuplexPort : IPort {
        void CancelRead();
        void CancelWrite();
    }
    public sealed class NativeSerial : IDuplexPort {
        SafeFileHandle handle;
        readonly IoOperation reader = new IoOperation(4096);
        readonly IoOperation writer = new IoOperation(4096);
        volatile bool stoppingRead, stoppingWrite;
        [StructLayout(LayoutKind.Sequential)] struct Dcb {
            public uint Length, Baud, Flags;
            public ushort Reserved, XonLim, XoffLim;
            public byte ByteSize, Parity, StopBits;
            public byte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
            public ushort Reserved1;
        }
        [StructLayout(LayoutKind.Sequential)] struct Timeouts { public uint Interval, Multiplier, Constant, WriteMultiplier, WriteConstant; }
        [StructLayout(LayoutKind.Sequential)] struct ComStat { public uint Flags, InQueue, OutQueue; }
        [StructLayout(LayoutKind.Sequential)] struct Overlap { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetCommState(SafeFileHandle h, ref Dcb dcb);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetCommState(SafeFileHandle h, ref Dcb dcb);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetCommTimeouts(SafeFileHandle h, ref Timeouts timeouts);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetupComm(SafeFileHandle h, uint input, uint output);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint count, out uint read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, uint count, out uint written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out uint count, bool wait);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool ClearCommError(SafeFileHandle h, out uint errors, out ComStat stat);
        sealed class IoOperation : IDisposable {
            public readonly IntPtr Data, Ov;
            public readonly EventWaitHandle Event = new EventWaitHandle(false, EventResetMode.ManualReset);
            public IoOperation(int size) { Data = Marshal.AllocHGlobal(size); Ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlap))); }
            public void Reset() {
                Event.Reset();
                Overlap o = new Overlap { Event = Event.SafeWaitHandle.DangerousGetHandle() };
                Marshal.StructureToPtr(o, Ov, false);
            }
            public void Dispose() { Marshal.FreeHGlobal(Data); Marshal.FreeHGlobal(Ov); Event.Dispose(); }
        }
        public NativeSerial(string name, int baud) {
            try {
                handle = CreateFile("\\\\.\\" + name, 0xc0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), name + " could not be opened. Check the connection and close other applications using this port.");
                Dcb dcb = new Dcb { Length = (uint)Marshal.SizeOf(typeof(Dcb)) };
                Check(GetCommState(handle, ref dcb), "Unable to read port settings");
                dcb.Baud = (uint)baud; dcb.ByteSize = 8; dcb.Parity = 0; dcb.StopBits = 0;
                // Binary mode only: no XON/XOFF, CTS/DSR gating, filtering, or DTR/RTS toggles.
                dcb.Flags = 1; dcb.XonLim = dcb.XoffLim = 128;
                Check(SetCommState(handle, ref dcb), "The port did not accept the baud/8N1 settings");
                Check(SetupComm(handle, 65536, 4096), "Unable to initialize driver buffers");
                Timeouts timeouts = new Timeouts { Interval = uint.MaxValue, Multiplier = uint.MaxValue, Constant = 100, WriteConstant = (uint)Math.Ceiling(4096 * 10000.0 / baud) + 5000 };
                Check(SetCommTimeouts(handle, ref timeouts), "Unable to configure read timeouts");
            } catch { Dispose(); throw; }
        }
        static void Check(bool ok, string message) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), message); }
        int Transfer(IoOperation op, byte[] buffer, bool write, int offset, int count) {
            if (write ? stoppingWrite : stoppingRead) return 0;
            op.Reset(); if (write) Marshal.Copy(buffer, offset, op.Data, count);
            uint done;
            bool ok = write ? WriteFile(handle, op.Data, (uint)count, out done, op.Ov) : ReadFile(handle, op.Data, (uint)count, out done, op.Ov);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error == 997) {
                // Keep unmanaged buffers and OVERLAPPED alive until completion, including cancellation.
                while (!op.Event.WaitOne(50)) if (write ? stoppingWrite : stoppingRead) CancelIoEx(handle, op.Ov);
                ok = GetOverlappedResult(handle, op.Ov, out done, false);
                error = ok ? 0 : Marshal.GetLastWin32Error();
            }
            if (!ok) {
                if (error == 995 && (write ? stoppingWrite : stoppingRead)) return 0;
                throw new Win32Exception(error, write ? "Serial write failed" : "Serial read failed");
            }
            if (!write && done > 0) Marshal.Copy(op.Data, buffer, 0, (int)done);
            return (int)done;
        }
        public int Read(byte[] buffer) { return Transfer(reader, buffer, false, 0, Math.Min(4096, buffer.Length)); }
        public void Write(byte[] buffer) {
            int offset = 0;
            while (offset < buffer.Length) {
                int n = Transfer(writer, buffer, true, offset, Math.Min(4096, buffer.Length-offset));
                if (n == 0) { if (stoppingWrite) throw new OperationCanceledException(); throw new TimeoutException("Write timed out; some data may have been transmitted."); }
                offset += n;
            }
        }
        public uint Errors(out uint inputQueue, out uint outputQueue) {
            uint errors; ComStat stat;
            Check(ClearCommError(handle, out errors, out stat), "Unable to read serial port status");
            inputQueue = stat.InQueue; outputQueue = stat.OutQueue; return errors;
        }
        void CancelOperation(IoOperation operation) { if (handle != null && !handle.IsClosed && !handle.IsInvalid) CancelIoEx(handle, operation.Ov); }
        public void CancelRead() { stoppingRead = true; CancelOperation(reader); }
        public void CancelWrite() { stoppingWrite = true; CancelOperation(writer); }
        public void Cancel() { CancelRead(); CancelWrite(); }
        // Call only after read/write workers have stopped; Cancel alone is not completion.
        public void Dispose() { if (handle != null) { handle.Dispose(); handle = null; } reader.Dispose(); writer.Dispose(); }
    }
}
