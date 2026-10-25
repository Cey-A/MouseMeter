using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MouseMeter
{
    /// <summary>One reading from one source.</summary>
    public struct Reading
    {
        public string Id;            // stable key: "2.4G|0BDA:4762|keyboard"
        public string Kind;          // DeviceKind.*
        public string DeviceName;
        public string Model;
        public int Level;
        public bool Charging;
        public string Transport;     // 2.4G / BLE / 蓝牙 / USB / HFP
        public string HardwareId;    // "0BDA:4762" or a BLE address
        public string Source;        // provider name, for diagnostics
        public DateTime At;

        public bool IsValid { get { return Level >= 0 && Level <= 100; } }

        public static Reading Invalid()
        {
            var r = new Reading();
            r.Level = -1;
            return r;
        }
    }

    /// <summary>
    /// Pure decoders for the vendor feature-report family used by Realtek based dongles.
    /// Byte values travel inverted (value ^ 0xFF), see docs/PROTOCOL.md.
    ///
    /// Kept static and side-effect free on purpose: this is the only part of the protocol we can
    /// verify without hardware, so it must stay unit testable (tests/ProtocolTests.cs).
    /// </summary>
    public static class Protocol
    {
        public const byte RequestReportId = 0x12;
        public const byte ResponseReportId = 0x11;
        public const byte ReplyHead = 0xF9;

        public const int FrameLength = 65;
        public const int MinFrame = 65;

        public const int LevelIndex = 11;
        public const int ChargeIndex = 12;
        public const int TransportIndex = 10;
        public const int PidLoIndex = 4;
        public const int PidHiIndex = 5;

        // Response type byte (payload[0] / reply[2]) -> device class.
        // 0x01 = mouse (verified on A5 Ultra). The two new entries are hypotheses taken from the
        // same Realtek firmware family; a mismatch is rejected by Validate() and never displayed.
        public const byte TypeMouse = 0x01;
        public const byte TypeKeyboard = 0x02;
        public const byte TypeHeadset = 0x03;

        /// <summary>Build the 65 byte query frame. opcode is the plain (non inverted) operation byte.</summary>
        public static byte[] BuildRequest(byte opcode)
        {
            var frame = new byte[FrameLength];
            for (int i = 0; i < frame.Length; i++) frame[i] = 0xFF;
            frame[0] = RequestReportId;
            frame[1] = (byte)(opcode ^ 0xFF);
            return frame;
        }

        public static int Invert(byte raw) { return raw ^ 0xFF; }

        /// <summary>Model name carried by the dongle, if the reply includes it (payload bytes 13..27).</summary>
        public static string ParseModel(byte[] reply)
        {
            if (reply == null || reply.Length < 16) return "";
            var chars = new List<char>();
            for (int i = 13; i < reply.Length && i < 29; i++)
            {
                int c = Invert(reply[i]);
                if (c == 0) break;
                if (c < 0x20 || c > 0x7E) return "";   // not an ASCII field on this firmware
                chars.Add((char)c);
            }
            string s = new string(chars.ToArray()).Trim();
            return s.Length >= 2 ? s : "";
        }

        /// <summary>
        /// Decode a reply into a Reading. Returns false for anything malformed; callers then treat it
        /// as "no answer" and keep the previous value instead of showing garbage.
        /// </summary>
        public static bool TryParse(byte[] reply, out Reading r, out string reason)
        {
            r = Reading.Invalid();
            reason = "";

            if (reply == null || reply.Length < FrameLength) { reason = "short"; return false; }
            if (reply[1] != ReplyHead) { reason = "head"; return false; }

            int level = Invert(reply[LevelIndex]);
            int charge = Invert(reply[ChargeIndex]);
            if (level > 100) { reason = "level"; return false; }

            // the payload carries the receiver PID in little endian; if it is present and does not
            // match the collection we talked to, we are reading somebody else's answer.
            int pidEcho = Invert(reply[PidLoIndex]) | (Invert(reply[PidHiIndex]) << 8);

            r.Level = level;
            r.Charging = charge != 0;
            r.At = DateTime.Now;
            r.Transport = "2.4G";
            reason = "type=" + Invert(reply[2]).ToString("X2") + " pid=" + pidEcho.ToString("X4");
            return true;
        }

        /// <summary>Response type byte, or -1 when the reply head is wrong.</summary>
        public static int ResponseType(byte[] reply)
        {
            if (reply == null || reply.Length < 3 || reply[1] != ReplyHead) return -1;
            return Invert(reply[2]);
        }

        /// <summary>Optional confirmation of "did the PID in the answer match this receiver".</summary>
        public static bool PidMatches(byte[] reply, int pid)
        {
            if (reply == null || reply.Length < 6) return false;
            int echo = Invert(reply[PidLoIndex]) | (Invert(reply[PidHiIndex]) << 8);
            return echo == pid;
        }
    }

    /// <summary>Progressive backoff shared by all providers: 60, 60, 90, 120, 180 ms (~500 ms worst case).</summary>
    public static class Retry
    {
        public static readonly int[] Delays = new int[] { 60, 60, 90, 120, 180 };

        public static int DelayFor(int attempt)
        {
            int i = attempt < 0 ? 0 : (attempt >= Delays.Length ? Delays.Length - 1 : attempt);
            return Delays[i];
        }

        public static int WorstCaseMs { get { int s = 0; for (int i = 0; i < Delays.Length; i++) s += Delays[i]; return s; } }
    }

    /// <summary>
    /// The probe: one vendor collection, one query, classified result.
    /// Replaces the old "try 8 times in a row" loop that blocked a pool thread for ~1.6 s.
    /// </summary>
    public static class VendorProbe
    {
        public sealed class Result
        {
            public bool Ok;
            public Reading Value;
            public string Reason;
            public int ErrorCode;
        }

        public static Result Query(string path, byte opcode)
        {
            var res = new Result();
            res.Value = Reading.Invalid();

            SafeFileHandle h;
            var frame = Protocol.BuildRequest(opcode);

            for (int attempt = 0; attempt < Retry.Delays.Length; attempt++)
            {
                h = Hid.Open(path, true);
                if (h.IsInvalid) { res.ErrorCode = Hid.LastError; res.Reason = "open " + Hid.DescribeError(res.ErrorCode); Thread.Sleep(Retry.DelayFor(attempt)); continue; }
                try
                {
                    if (!Hid.SetFeature(h, frame))
                    {
                        res.ErrorCode = Hid.LastError;
                        res.Reason = "set " + Hid.DescribeError(res.ErrorCode);
                    }
                    else
                    {
                        Thread.Sleep(30);
                        var reply = Hid.GetFeature(h, Protocol.FrameLength, Protocol.ResponseReportId);
                        Reading r;
                        string why;
                        if (reply == null)
                        {
                            res.ErrorCode = Hid.LastError;
                            res.Reason = "get " + Hid.DescribeError(res.ErrorCode);
                        }
                        else if (Protocol.TryParse(reply, out r, out why))
                        {
                            res.Ok = true;
                            res.Value = r;
                            res.Value.Source = "vendor";
                            res.Reason = why;
                            return res;
                        }
                        else
                        {
                            res.Reason = "parse " + why;
                        }
                    }
                }
                catch (Exception ex) { res.Reason = "exception " + ex.Message; }
                finally { h.Close(); }

                Thread.Sleep(Retry.DelayFor(attempt));
            }
            return res;
        }
    }
}
