using System;
using System.Collections.Generic;

namespace SerialRf {
    public static class SerialWarnings {
        // ClearCommError CE_* flags. These are observations, not affected-byte counts.
        public const uint ReceiveOverflow=0x01, Overrun=0x02, Parity=0x04, Framing=0x08, Break=0x10, KnownMask=0x1f;
        public static string Names(uint mask) {
            List<string> names=new List<string>();
            if((mask&Framing)!=0)names.Add("Framing");
            if((mask&Break)!=0)names.Add("Break");
            if((mask&Overrun)!=0)names.Add("Overrun");
            if((mask&ReceiveOverflow)!=0)names.Add("RX overflow");
            if((mask&Parity)!=0)names.Add("Parity");
            if((mask&~KnownMask)!=0)names.Add("Other (0x"+(mask&~KnownMask).ToString("X")+")");
            return names.Count==0?"None":string.Join(", ",names.ToArray());
        }
        public static string Counts(Snapshot s) {
            List<string> counts=new List<string>();
            Add(counts,"Framing",s.FramingWarnings);Add(counts,"Break",s.BreakWarnings);
            Add(counts,"Overrun",s.OverrunWarnings);Add(counts,"RX overflow",s.ReceiveOverflowWarnings);
            Add(counts,"Parity",s.ParityWarnings);Add(counts,"Other",s.OtherWarnings);
            return counts.Count==0?"None observed":string.Join(" · ",counts.ToArray());
        }
        static void Add(List<string> counts,string label,long count) { if(count>0)counts.Add(label+": "+count); }
    }
}
