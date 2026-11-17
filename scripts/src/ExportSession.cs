using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SerialRf {
    public static partial class Export {
        public static void Save(string directory,TestSession session) {
            if(!session.Done)throw new InvalidOperationException("Wait for both directions to finish before exporting.");
            if(!session.Config.Bidirectional) { Save(directory,session.Forward);return; }
            Directory.CreateDirectory(directory);
            string[] names={"A_to_B","B_to_A"};TestEngine[] engines={session.Forward,session.Reverse};
            List<object> directions=new List<object>();
            using(StreamWriter combined=new StreamWriter(Path.Combine(directory,"packets.csv"),false,new UTF8Encoding(true))) {
                for(int i=0;i<2;i++) {
                    string folder=Path.Combine(directory,names[i]);Save(folder,engines[i]);
                    object report=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(folder,"summary.json")));
                    directions.Add(new Dictionary<string,object> { {"direction",names[i]},{"report",report} });
                    using(StreamReader rows=new StreamReader(Path.Combine(folder,"packets.csv"),Encoding.UTF8)) {
                        string header=rows.ReadLine();if(i==0)combined.WriteLine("direction,tx_port,rx_port,"+header);
                        string row;while((row=rows.ReadLine())!=null)combined.WriteLine(names[i]+","+engines[i].Config.TxPort+","+engines[i].Config.RxPort+","+row);
                    }
                }
            }
            var root=new Dictionary<string,object> {
                {"application","Serial Link Tester " + BuildInfo.Version},{"configuration",session.Config},{"directions",directions},
                {"load_scope","Target load applies independently to each direction. Timing and loss remain separate."},
                {"serial_warning_scope","Serial warnings and queue samples are port-level observations on shared endpoints; do not sum them as independent directional faults. Test-stopping errors are separate."},
                {"csv_scope","Rows are grouped by direction. Each direction has its own sequence, session ID and start-relative timestamps; latency uses the common PC clock."}
            };
            File.WriteAllText(Path.Combine(directory,"summary.json"),new JavaScriptSerializer().Serialize(root),new UTF8Encoding(false));
            Snapshot a=engines[0].Measure.Get(),b=engines[1].Measure.Get();TestConfig c=session.Config;
            StringBuilder html=new StringBuilder("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Serial Link Tester — bidirectional report</title><style>body{font:16px/1.6 Segoe UI,Arial;color-scheme:dark;background:#0d121a;color:#e9f0f7;margin:32px auto;max-width:1100px;padding:0 20px}header,main{background:#151d28;border:1px solid #2f3f50;border-radius:12px;padding:26px;margin:20px 0}h1{margin:0}table{width:100%;border-collapse:collapse}td,th{text-align:right;padding:12px 8px;border-bottom:1px solid #2f3f50}td:first-child,th:first-child{text-align:left}a{color:#43d8be}.note{background:#382e1d;color:#f6c66b;padding:16px;border-radius:8px}.scroll{overflow:auto}small{color:#9eb1c3}@media print{body,header,main,.note{background:white;color:black}a{color:black}}</style><header><h1>Serial Link Tester</h1><div>Two ports / bidirectional · ");
            html.Append(H(c.Mode)+"</div></header><main>");
            if(c.Demo)html.Append("<p class='note'>DEMO · SIMULATED RESULTS. No physical hardware was used.</p>");
            html.Append(DiagnosticsHtml(a,"A → B"));html.Append(DiagnosticsHtml(b,"B → A"));
            html.Append("<p>Port A: <b>"+H(c.TxPort)+"</b> · Port B: <b>"+H(c.RxPort)+"</b><br>"+c.Baud+" baud · 8N1 · "+c.Payload+" payload bytes · "+c.DurationSeconds+" s<br>"+(c.Throughput?"Target load: <b>"+c.LoadPercent+"% per direction</b>":"One unresolved probe per direction; both directions run concurrently.")+"<br>Notes: "+H(c.Note)+"</p><div class='scroll'><table><tr><th>Direction</th><th>Sent</th><th>Valid</th><th>Missing</th><th>Corruption</th><th>Goodput kbit/s</th><th>Mean ms</th><th>Jitter ms</th></tr>");
            ComparisonRow(html,"A → B",names[0],a);ComparisonRow(html,"B → A",names[1],b);
            html.Append("</table></div><p>Goodput is the final active receive average of verified payload. Corruption combines payload errors and framing events. Missing packets are finalized after each direction's receive window.</p><p class='note'>Both directions run simultaneously. Latency includes Windows, adapter buffers, UART serialization and the link. The two direction delays are not combined into RTT. Serial warning observations refer to shared physical ports.</p><p>Open a direction above for its percentiles, queue observations, detailed CSV and JSON. The combined <a href='packets.csv'>packets.csv</a> includes a direction column; <a href='summary.json'>summary.json</a> contains both results. CSV rows are grouped by direction, with timestamps relative to that direction's start.</p><small>Serial Link Tester " + BuildInfo.Version + "</small></main></html>");
            File.WriteAllText(Path.Combine(directory,"report.html"),html.ToString(),new UTF8Encoding(false));
        }
        static void ComparisonRow(StringBuilder html,string label,string folder,Snapshot s) {
            html.Append("<tr><td><a href='"+folder+"/report.html'>"+label+"</a></td><td>"+s.Sent+"</td><td>"+s.Good+"</td><td>"+s.Missing+"</td><td>"+(s.Corrupt+s.Invalid)+"</td><td>"+(s.Good>1?N(s.ActivePayloadBps/1000):"—")+"</td><td>"+(s.LatencyCount>0?N(s.MeanMs):"—")+"</td><td>"+(s.LatencyCount>1?N(s.JitterMs):"—")+"</td></tr>");
        }
    }
}
