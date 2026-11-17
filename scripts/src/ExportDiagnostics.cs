using System.Collections.Generic;
using System.Text;

namespace SerialRf {
    public static partial class Export {
        static object WarningData(Snapshot s) {
            return new Dictionary<string,object> {
                {"types",s.SerialWarningTypes},{"mask",s.PcErrorMask},{"observations",s.PcErrorEvents},
                {"framing",s.FramingWarnings},{"break",s.BreakWarnings},{"overrun",s.OverrunWarnings},
                {"rx_overflow",s.ReceiveOverflowWarnings},{"parity",s.ParityWarnings},{"other",s.OtherWarnings},
                {"stops_test",false},{"scope","Cumulative port-level observations, not affected-byte counts or a claim that the condition is still active."}
            };
        }
        static string DiagnosticsHtml(Snapshot s,string direction) {
            StringBuilder text=new StringBuilder();
            string suffix=string.IsNullOrEmpty(direction)?"":" · "+H(direction);
            if(!string.IsNullOrEmpty(s.Error))text.Append("<section style='background:#3b2028;color:#ff7e7e;border:1px solid #7b414e;border-radius:8px;padding:16px;margin:12px 0'><strong>Test-stopping error"+suffix+"</strong><p>"+H(s.Error)+"</p></section>");
            if(s.PcErrorMask!=0)text.Append("<section style='background:#382e1d;color:#f6c66b;border:1px solid #75603b;border-radius:8px;padding:16px;margin:12px 0'><strong>Serial warnings (non-fatal)"+suffix+"</strong><p>"+H(SerialWarnings.Counts(s))+"<br>Types: "+H(s.SerialWarningTypes)+"</p><div>Recorded during this test; these warnings do not stop it. Counts are port-level observations, not affected bytes or a current connection status.</div></section>");
            else text.Append("<p>Serial warnings"+suffix+": none observed.</p>");
            return text.ToString();
        }
    }
}
