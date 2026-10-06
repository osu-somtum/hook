// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TitanicHook.Core.Helpers;

/// <summary>What Discord shows for the game (an activity). Empty texts are left out.</summary>
public sealed class DiscordActivity
{
    public string Details = "";
    public string State = "";
    public long StartTimestamp;
    public string LargeImage = "";
    public string LargeText = "";
    public string SmallImage = "";
    public string SmallText = "";
    public string[] ButtonLabels = [];
    public string[] ButtonUrls = [];
}

/// <summary>
/// osu!somtum: Discord Rich Presence over Discord's local IPC (the named pipe \\.\pipe\discord-ipc-N),
/// for .NET 2.0 as well (no System.IO.Pipes there: the pipe is opened with CreateFile and read and written
/// with ReadFile/WriteFile). Everything runs on one background thread, and nothing on it waits for Discord
/// without a timeout: Discord's answers are only read once PeekNamedPipe says they are there. When Discord
/// isn't running it tries again every 20 seconds. It never throws.
/// </summary>
public static class DiscordIpc
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    /// <summary>
    /// Discord takes about five activity updates in 20 seconds and drops the rest: at most one is sent
    /// every 5 seconds, and changes in between are sent as one (the latest).
    /// </summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);

    private static readonly object Lock = new();
    private static readonly AutoResetEvent Changed = new(false);
    private static string _clientId = "";
    private static Func<DiscordActivity?>? _activity;
    private static Thread? _thread;
    private static SafeFileHandle? _pipe;
    private static int _nonce;

    /// <summary>Starts the presence thread; <paramref name="activity"/> is asked for what to show (on that thread).</summary>
    public static void Start(string clientId, Func<DiscordActivity?> activity)
    {
        try
        {
            lock (Lock)
            {
                if (_thread != null)
                    return;
                _clientId = clientId;
                _activity = activity;
                _thread = new Thread(Run) { IsBackground = true, Name = "osu!somtum Discord presence", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Asks the thread to send the activity again (it changed). Cheap: safe on the game's thread.</summary>
    public static void Update()
    {
        try
        {
            Changed.Set();
        }
        catch (Exception)
        {
        }
    }

    private static void Run()
    {
        string sent = "";
        DateTime nextTry = DateTime.MinValue, lastSend = DateTime.MinValue;
        while (true)
        {
            try
            {
                if (_pipe == null && DateTime.UtcNow >= nextTry)
                {
                    sent = ""; // a new connection shows nothing yet
                    if (!Connect())
                        nextTry = DateTime.UtcNow.AddSeconds(20);
                }
                if (_pipe != null)
                {
                    Drain();
                    TimeSpan since = DateTime.UtcNow - lastSend;
                    if (_pipe != null && since < MinGap && since >= TimeSpan.Zero)
                        Thread.Sleep(MinGap - since); // coalesce: what is shown is read after the wait
                    if (_pipe != null)
                    {
                        string json = ActivityJson(_activity?.Invoke());
                        if (json != sent)
                        {
                            _nonce++;
                            if (!Send(OpFrame, "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + ProcessId() + ",\"activity\":" + json +
                                               "},\"nonce\":\"" + _nonce + "\"}"))
                                throw new IOException("Discord closed the pipe");
                            lastSend = DateTime.UtcNow;
                            sent = json;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
                Disconnect();
                nextTry = DateTime.UtcNow.AddSeconds(20);
            }
            try
            {
                // Once in a while anyway: to reconnect, answer pings and send what changed while sending.
                Changed.WaitOne(_pipe == null ? 20000 : 5000, false);
            }
            catch (Exception)
            {
                Thread.Sleep(5000);
            }
        }
    }

    private static bool Connect()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                SafeFileHandle handle = CreateFile(@"\\.\pipe\discord-ipc-" + i, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle == null || handle.IsInvalid)
                {
                    handle?.Close();
                    continue;
                }
                _pipe = handle;
                // The handshake comes first; Discord answers READY (or CLOSE for a wrong client id).
                if (Send(OpHandshake, "{\"v\":1,\"client_id\":\"" + _clientId + "\"}") && WaitForData(3000))
                {
                    Drain();
                    if (_pipe != null)
                    {
                        Log($"connected (discord-ipc-{i})");
                        return true;
                    }
                }
                Disconnect();
            }
            catch (Exception)
            {
                Disconnect();
            }
        }
        return false;
    }

    private static void Disconnect()
    {
        try
        {
            _pipe?.Close();
        }
        catch (Exception)
        {
        }
        _pipe = null;
    }

    /// <summary>One frame: the opcode and the length (little-endian), then the UTF-8 JSON.</summary>
    private static bool Send(int op, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[8 + body.Length];
        WriteInt(frame, 0, op);
        WriteInt(frame, 4, body.Length);
        Buffer.BlockCopy(body, 0, frame, 8, body.Length);
        int done = 0;
        while (done < frame.Length)
        {
            byte[] rest = frame;
            if (done > 0)
            {
                rest = new byte[frame.Length - done];
                Buffer.BlockCopy(frame, done, rest, 0, rest.Length);
            }
            if (_pipe == null || !WriteFile(_pipe, rest, rest.Length, out int written, IntPtr.Zero) || written <= 0)
                return false;
            done += written;
        }
        return true;
    }

    /// <summary>Waits up to <paramref name="ms"/> for Discord to send something.</summary>
    private static bool WaitForData(int ms)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
        while (_pipe != null)
        {
            if (!PeekNamedPipe(_pipe, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                return false;
            if (available > 0)
                return true;
            if (DateTime.UtcNow >= until)
                return false;
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>
    /// Reads the frames Discord has sent (answers, errors, pings) without waiting for more: a ping is
    /// answered, a close drops the pipe. A frame is only read once all of it is there.
    /// </summary>
    private static void Drain()
    {
        while (_pipe != null)
        {
            if (!PeekNamedPipe(_pipe, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                throw new IOException("Discord closed the pipe");
            if (available < 8)
                return;
            byte[] header = new byte[8];
            if (!PeekNamedPipe(_pipe, header, 8, out uint peeked, out available, IntPtr.Zero) || peeked < 8)
                return;
            int op = BitConverter.ToInt32(header, 0);
            int length = BitConverter.ToInt32(header, 4);
            if (length < 0 || length > 1 << 20)
                throw new IOException("bad frame from Discord");
            if (available < 8 + (uint)length)
                return; // the rest isn't there yet
            ReadExactly(8);
            string body = Encoding.UTF8.GetString(ReadExactly(length));
            switch (op)
            {
                case OpPing:
                    Send(OpPong, body);
                    break;
                case OpClose:
                    throw new IOException("Discord closed the connection: " + body);
                case OpFrame:
                    if (body.Contains("\"evt\":\"ERROR\""))
                        Log(body);
                    break;
            }
        }
    }

    private static byte[] ReadExactly(int count)
    {
        byte[] buffer = new byte[count];
        int done = 0;
        while (done < count)
        {
            byte[] part = done == 0 ? buffer : new byte[count - done];
            if (_pipe == null || !ReadFile(_pipe, part, part.Length, out int read, IntPtr.Zero) || read <= 0)
                throw new IOException("Discord closed the pipe");
            if (done > 0)
                Buffer.BlockCopy(part, 0, buffer, done, read);
            done += read;
        }
        return buffer;
    }

    private static void WriteInt(byte[] buffer, int at, int value)
    {
        buffer[at] = (byte)value;
        buffer[at + 1] = (byte)(value >> 8);
        buffer[at + 2] = (byte)(value >> 16);
        buffer[at + 3] = (byte)(value >> 24);
    }

    private static void Log(string message)
    {
        try
        {
            Logging.Info("Discord presence: " + message);
        }
        catch (Exception)
        {
        }
    }

    private static int? _pid;

    private static int ProcessId()
    {
        if (_pid == null)
        {
            try
            {
                _pid = Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
                _pid = 0;
            }
        }
        return _pid.Value;
    }

    /// <summary>The activity as Discord's JSON ("null" clears it).</summary>
    public static string ActivityJson(DiscordActivity? a)
    {
        if (a == null)
            return "null";
        var json = new StringBuilder("{");
        bool first = true;
        void Text(StringBuilder sb, string key, string value, int max)
        {
            value = Clip(value, max);
            if (value.Length == 0)
                return;
            if (value.Length < 2)
                value += "\u3000"; // Discord refuses texts shorter than 2 (an ideographic space: not trimmed away)
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append('"').Append(key).Append("\":").Append(Quote(value));
        }
        Text(json, "details", a.Details, 128);
        Text(json, "state", a.State, 128);
        if (a.StartTimestamp > 0)
        {
            if (!first)
                json.Append(',');
            first = false;
            json.Append("\"timestamps\":{\"start\":").Append(a.StartTimestamp).Append('}');
        }

        var assets = new StringBuilder("{");
        bool firstAsset = true;
        void Asset(string key, string value)
        {
            value = Clip(value, 128);
            if (value.Length < 2)
                return;
            if (!firstAsset)
                assets.Append(',');
            firstAsset = false;
            assets.Append('"').Append(key).Append("\":").Append(Quote(value));
        }
        Asset("large_image", a.LargeImage);
        Asset("large_text", a.LargeText);
        Asset("small_image", a.SmallImage);
        Asset("small_text", a.SmallText);
        if (!firstAsset)
        {
            if (!first)
                json.Append(',');
            first = false;
            json.Append("\"assets\":").Append(assets).Append('}');
        }

        var buttons = new StringBuilder();
        int count = 0;
        for (int i = 0; i < a.ButtonLabels.Length && i < a.ButtonUrls.Length && count < 2; i++)
        {
            string label = Clip(a.ButtonLabels[i], 32), url = a.ButtonUrls[i] ?? "";
            if (label.Length == 0 || url.Length == 0 || url.Length > 512)
                continue;
            buttons.Append(count++ == 0 ? "" : ",").Append("{\"label\":").Append(Quote(label)).Append(",\"url\":").Append(Quote(url)).Append('}');
        }
        if (count > 0)
        {
            if (!first)
                json.Append(',');
            first = false;
            json.Append("\"buttons\":[").Append(buttons).Append(']');
        }
        if (!first)
            json.Append(',');
        return json.Append("\"instance\":false}").ToString();
    }

    /// <summary>Trimmed, and cut to <paramref name="max"/> characters with "..." (never through a surrogate pair).</summary>
    private static string Clip(string? value, int max)
    {
        value = (value ?? "").Trim();
        if (value.Length <= max)
            return value;
        int cut = max - 3;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
            cut--;
        return value.Substring(0, cut).TrimEnd() + "...";
    }

    private static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    bool lone = char.IsHighSurrogate(c) ? i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])
                        : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(value[i - 1]));
                    if (c < 0x20 || c == '\u2028' || c == '\u2029' || lone)
                        sb.Append("\\u").Append(((int)(lone ? '\uFFFD' : c)).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(SafeFileHandle pipe, byte[] buffer, uint size, out uint read, out uint available, IntPtr left);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);
}
