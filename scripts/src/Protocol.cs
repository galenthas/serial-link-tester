using System;
using System.Collections.Generic;

namespace SerialRf {
    public sealed class Frame {
        public Guid Run;
        public ulong Sequence;
        public int PayloadLength;
        public bool PayloadOk;
    }

    // COBS boundaries let the receiver recover after deleted/inserted bytes.
    // A separate header CRC protects the identity of a payload-corrupt packet.
    public static class Protocol {
        public const int MaxPayload = 2048;
        public static uint Crc(byte[] bytes, int offset, int count) {
            uint crc = 0xffffffff;
            for (int i = offset; i < offset + count; i++) {
                crc ^= bytes[i];
                for (int b = 0; b < 8; b++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0);
            }
            return ~crc;
        }
        public static byte[] Make(Guid run, ulong seq, int payload) {
            if (payload < 1 || payload > MaxPayload) throw new ArgumentOutOfRangeException("payload");
            byte[] raw = new byte[40 + payload];
            raw[0] = 83; raw[1] = 82; raw[2] = 70; raw[3] = 49;
            raw[4] = 1; raw[5] = 1;
            Put16(raw, 6, payload);
            Buffer.BlockCopy(run.ToByteArray(), 0, raw, 8, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, raw, 24, 8);
            Put32(raw, 32, Crc(raw, 0, 32));
            uint pattern = (uint)seq ^ (uint)(seq >> 32) ^ 0x9e3779b9U;
            for (int i = 0; i < payload; i++) raw[36 + i] = Next(ref pattern);
            Put32(raw, 36 + payload, Crc(raw, 36, payload));
            return Encode(raw);
        }
        static byte Next(ref uint p) { p ^= p << 13; p ^= p >> 17; p ^= p << 5; return (byte)p; }
        public static void Put32(byte[] d, int o, uint v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, d, o, 4); }
        static void Put16(byte[] d, int o, int v) { d[o] = (byte)v; d[o+1] = (byte)(v >> 8); }
        public static byte[] Encode(byte[] raw) {
            byte[] output = new byte[raw.Length + raw.Length / 254 + 3];
            int codeAt = 0, write = 1; byte code = 1;
            foreach (byte b in raw) {
                if (b == 0) { output[codeAt] = code; codeAt = write++; code = 1; }
                else {
                    output[write++] = b;
                    code++;
                    if (code == 255) { output[codeAt] = code; codeAt = write++; code = 1; }
                }
            }
            output[codeAt] = code;
            output[write++] = 0;
            Array.Resize(ref output, write);
            return output;
        }
        // Input excludes the zero delimiter.
        public static byte[] Decode(byte[] encoded, int count) {
            byte[] raw = new byte[count]; int r = 0, w = 0;
            while (r < count) {
                int code = encoded[r++];
                if (code == 0 || r + code - 1 > count) return null;
                for (int k = 1; k < code; k++) { if (encoded[r] == 0) return null; raw[w++] = encoded[r++]; }
                if (code != 255 && r < count) raw[w++] = 0;
            }
            Array.Resize(ref raw, w); return raw;
        }
        public static Frame Parse(byte[] raw) {
            if (raw == null || raw.Length < 41 || raw[0] != 83 || raw[1] != 82 || raw[2] != 70 || raw[3] != 49 || raw[4] != 1 || raw[5] != 1) return null;
            int length = raw[6] | (raw[7] << 8);
            if (length < 1 || length > MaxPayload || raw.Length != 40 + length || BitConverter.ToUInt32(raw, 32) != Crc(raw, 0, 32)) return null;
            byte[] id = new byte[16]; Buffer.BlockCopy(raw, 8, id, 0, 16);
            Frame f = new Frame { Run = new Guid(id), Sequence = BitConverter.ToUInt64(raw, 24), PayloadLength = length, PayloadOk = true };
            uint pattern = (uint)f.Sequence ^ (uint)(f.Sequence >> 32) ^ 0x9e3779b9U;
            for (int i = 0; i < length; i++) if (raw[36+i] != Next(ref pattern)) f.PayloadOk = false;
            if (BitConverter.ToUInt32(raw, 36 + length) != Crc(raw, 36, length)) f.PayloadOk = false;
            return f;
        }
    }

    public sealed class FrameParser {
        readonly byte[] buffer = new byte[2200]; int count; bool overflow;
        public Action<Frame, long> OnFrame;
        public Action OnInvalid;
        public void Feed(byte[] bytes, int size, long stamp) {
            for (int i = 0; i < size; i++) {
                if (bytes[i] == 0) {
                    if (count > 0 || overflow) {
                        Frame frame = overflow ? null : Protocol.Parse(Protocol.Decode(buffer, count));
                        if (frame == null) { if (OnInvalid != null) OnInvalid(); }
                        else if (OnFrame != null) OnFrame(frame, stamp);
                    }
                    count = 0; overflow = false;
                } else if (!overflow) {
                    if (count == buffer.Length) overflow = true;
                    else buffer[count++] = bytes[i];
                }
            }
        }
        public void Finish() { if (count != 0 || overflow) { if (OnInvalid != null) OnInvalid(); count = 0; overflow = false; } }
    }
}
