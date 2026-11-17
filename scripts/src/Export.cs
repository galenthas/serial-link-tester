using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace SerialRf {
    public static partial class Export {
        static string N(double n) { return double.IsInfinity(n) ? ">=60000" : n.ToString("0.###", CultureInfo.InvariantCulture); }
        static string H(string s) { return System.Net.WebUtility.HtmlEncode(s ?? ""); }
        public static void Save(string directory, TestEngine engine) {
            if (!engine.Done) throw new InvalidOperationException("Wait for the test to finish before exporting.");
            Directory.CreateDirectory(directory);
            Snapshot s = engine.Measure.Get(); TestConfig c = engine.Config;
            Dictionary<string, object> values = new Dictionary<string, object>();
            foreach (FieldInfo field in typeof(Snapshot).GetFields()) {
                object value = field.GetValue(s);
                if (value is double && (double.IsInfinity((double)value) || double.IsNaN((double)value))) value = ">=60000 ms";
                values[field.Name] = value;
            }
            var report = new Dictionary<string, object> {
                { "application", "Serial Link Tester " + BuildInfo.Version }, { "configuration", c }, { "measurement", values },
                { "serial_warnings", WarningData(s) }, { "test_stopping_error", s.Error },
                { "scope", "PC write submission to complete valid frame received; includes Windows, USB, UART serialization and device or link buffering. No hardware timestamp." },
                { "loss_definition", "Successfully written frames with no trusted-header response before the receive window closed. Corrupt headers may count both as invalid framing and missing." },
                { "jitter_definition", "Mean absolute difference between consecutive valid frame delays in receive order." },
                { "percentiles", "Upper edges of 0.1 ms histogram bins; >=60000 ms overflow." },
                { "hardware_validation", "Demo and automated tests do not validate physical hardware." },
                { "platform", Environment.OSVersion.ToString() + "; CLR " + Environment.Version }
            };
            File.WriteAllText(Path.Combine(directory, "summary.json"), new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            PacketRecord[] records = engine.Measure.Records();
            using (StreamWriter w = new StreamWriter(Path.Combine(directory, "packets.csv"), false, new UTF8Encoding(true))) {
                w.WriteLine("sequence,write_completed,received,payload_valid,tx_from_start_ms,rx_from_start_ms,latency_ms,payload_bytes,wire_bytes,status");
                for (int i = 0; i < records.Length; i++) {
                    PacketRecord r = records[i];
                    string state = !r.Completed ? "write_failed_or_partial" : !r.Received ? "missing_at_close" : r.Valid ? "valid" : "corrupt_payload";
                    w.WriteLine(i + "," + (r.Completed ? "1" : "0") + "," + (r.Received ? "1" : "0") + "," + (r.Received && r.Valid ? "1" : "0") + "," +
                        N(Measurement.Milliseconds(r.TxTicks - engine.Measure.Started)) + "," + (r.Received ? N(Measurement.Milliseconds(r.RxTicks - engine.Measure.Started)) : "") + "," +
                        (r.Received && r.Valid ? N(Measurement.Milliseconds(r.RxTicks - r.TxTicks)) : "") + "," + r.PayloadBytes + "," + r.WireBytes + "," + state);
                }
            }
            StringBuilder html = new StringBuilder("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Serial Link Tester — report</title><style>body{font:16px Segoe UI,Arial;color-scheme:dark;background:#0d121a;color:#e9f0f7;margin:40px auto;max-width:980px;padding:0 24px}header{background:#151d28;color:#e9f0f7;border:1px solid #2f3f50;padding:28px;border-radius:14px}h1{margin:0 0 8px}main{background:#151d28;border-radius:14px;padding:28px;margin-top:20px}table{width:100%;border-collapse:collapse}td{padding:10px;border-bottom:1px solid #2f3f50}td:last-child{text-align:right;font-weight:600}.note{background:#382e1d;color:#f6c66b;padding:16px;border-radius:8px}small{color:#9eb1c3}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;margin:24px 0}.metric{background:#1d2836;border:1px solid #2f3f50;padding:18px;border-radius:8px}.metric strong{display:block;font-size:28px;margin-top:8px}@media(max-width:640px){.grid{grid-template-columns:repeat(2,1fr)}body{margin:20px auto;padding:0 12px}main{padding:16px}}@media print{body,main,header,.metric{background:white;color:black}.note{background:#eee;color:black}}</style>");
            html.Append("<header><h1>Serial Link Tester</h1><div>" + (c.Demo ? "DEMO · SIMULATED RESULTS" : H(c.Setup)) + " · " + H(c.Mode) + "</div></header><main>");
            if (c.Demo) html.Append("<p class='note'>This report contains simulated data. No physical device or link was measured.</p>");
            html.Append(DiagnosticsHtml(s,""));
            html.Append("<p>Started (UTC): " + H(s.StartedUtc) + "<br>Session ID: " + H(s.RunId) + "<br>Ports: " + H(c.TxPort) + " → " + H(c.Loopback ? c.TxPort : c.RxPort) + " · " + c.Baud + " baud · 8N1<br>Notes: " + H(c.Note) + "</p><div class='grid'>");
            string[] labels = { "Missing", "Corruption", "Active goodput", "Mean latency", "Jitter", "Valid packets" };
            string[] nums = { s.Missing.ToString(), (s.Corrupt + s.Invalid).ToString(), s.Good > 1 ? N(s.ActivePayloadBps / 1000) + " kbit/s" : "No samples", s.LatencyCount > 0 ? N(s.MeanMs) + " ms" : "No samples", s.LatencyCount > 1 ? N(s.JitterMs) + " ms" : "No samples", s.Good.ToString() };
            for (int i=0;i<labels.Length;i++) html.Append("<div class='metric'>" + labels[i] + "<strong>" + nums[i] + "</strong></div>");
            html.Append("</div><table>");
            Row(html, "Sent / identifiable received", s.Sent + " / " + s.Received);
            Row(html, "Missing rate", s.Sent > 0 ? N(100.0*s.Missing/s.Sent) + " %" : "No samples");
            Row(html, "Corrupt payload / invalid frames", s.Corrupt + " / " + s.Invalid);
            Row(html, "Duplicate / out of order / late", s.Duplicates + " / " + s.Reordered + " / " + s.Late);
            Row(html, "Serial warnings (non-fatal)", SerialWarnings.Counts(s));
            Row(html, "Serial warning observations / mask", s.PcErrorEvents + " / 0x" + s.PcErrorMask.ToString("X"));
            Row(html, "Max observed RX / TX queue", s.MaxRxQueue + " / " + s.MaxTxQueue + " bytes");
            Row(html, "Min / median / p95 / p99 / max latency (ms)", s.LatencyCount > 0 ? N(s.MinMs) + " / " + N(s.P50Ms) + " / " + N(s.P95Ms) + " / " + N(s.P99Ms) + " / " + N(s.MaxMs) : "No samples");
            Row(html, "Latency standard deviation", s.LatencyCount > 1 ? N(s.StdDevMs) + " ms" : "No samples");
            Row(html, "Total duration / transmit duration", N(s.Elapsed) + " / " + N(s.SendSeconds) + " s");
            Row(html, "Whole-session average (including final receive window)", N(s.AveragePayloadBps / 1000) + " kbit/s");
            Row(html, "Final receive window", c.TimeoutMs + " ms");
            html.Append("</table><p class='note'>Timing includes Windows, the serial adapter, serialization and the device or link under test. " + (c.Loopback ? "This is round-trip timing. Halving it does not establish one-way latency." : "This is one-way application-to-application timing between two ports on the same PC.") + "</p><p>Jitter is the mean absolute difference between consecutive valid packet delays. Percentiles use 0.1 ms histogram bins. Active goodput needs at least two valid packets. An unreadable header can count as both an invalid frame and a missing packet; do not add these counts. Data arriving after the final receive window is excluded. Host errors in an incomplete test cannot be attributed solely to the link.</p><small>Serial Link Tester " + BuildInfo.Version + " · Detailed data: packets.csv and summary.json</small></main></html>");
            File.WriteAllText(Path.Combine(directory, "report.html"), html.ToString(), new UTF8Encoding(false));
        }
        static void Row(StringBuilder html, string label, string value) { html.Append("<tr><td>" + H(label) + "</td><td>" + H(value) + "</td></tr>"); }
    }
}
