using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SerialRf {
    // Avoid imposing the coarse Sleep tick on requested serial load. Not wire timing.
    public sealed class PrecisionWaiter : IDisposable {
        readonly SafeWaitHandle timer;
        public readonly bool HighResolution;
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetWaitableTimer(SafeWaitHandle h, ref long due, int period, IntPtr callback, IntPtr argument, bool resume);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint timeout);
        public PrecisionWaiter() {
            timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1f0003);
            HighResolution = !timer.IsInvalid;
            if (!HighResolution) { timer.Dispose(); timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1f0003); }
            if(timer.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Unable to create the transmit timer.");
        }
        public bool Until(long target, WaitHandle stop) {
            IntPtr[] handles = { stop.SafeWaitHandle.DangerousGetHandle(), timer.DangerousGetHandle() };
            while(!stop.WaitOne(0)) {
                double ms = Measurement.Milliseconds(target-Stopwatch.GetTimestamp());
                if(ms<=0)return true;
                if(ms>0.4) {
                    long due = -(long)((ms-0.2)*10000);
                    if(!SetWaitableTimer(timer,ref due,0,IntPtr.Zero,IntPtr.Zero,false))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    uint result=WaitForMultipleObjects(2,handles,false,uint.MaxValue);
                    if(result==0)return false;
                    if(result!=1)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                } else Thread.SpinWait(60);
            }
            return false;
        }
        public void Dispose() { timer.Dispose(); }
    }
}
