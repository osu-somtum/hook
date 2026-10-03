// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System.Net;
using System.Text.RegularExpressions;

namespace TitanicHook.Core.Helpers;

/// <summary>
/// The hosts osu! clients talk to, moved to the configured server (<see cref="Configuration.ServerName"/>):
/// osu!'s own (ppy.sh, and 2008's peppy.chigau.com), and those of the private servers old builds are
/// handed out patched for (titanic.sh, and lekuru.xyz before it). Subdomains are kept (osu., c1.,
/// server., ...); a two-level one (d.osu.) becomes its last label. HTTP bancho's fallbacks, raw
/// addresses and TCP bancho's port (:13381), become c.&lt;server&gt;.
/// </summary>
public static class ServerHosts
{
    private static readonly Regex Host = new(
        @"(?<![A-Za-z0-9.-])(?<host>(?:[A-Za-z0-9-]+\.)*(?:ppy\.sh|titanic\.sh|lekuru\.xyz)|peppy\.chigau\.com)(?![A-Za-z0-9-])",
        RegexOptions.IgnoreCase);

    private static string Server => EntryPoint.Config.ServerName;

    /// <summary>Whether <paramref name="text"/> names one of the hosts to move.</summary>
    public static bool Contains(string? text) => !string.IsNullOrEmpty(text) && Host.IsMatch(text);

    /// <summary>A host name, moved to the server (other names as they are).</summary>
    public static string MoveHost(string host)
    {
        if (!Host.IsMatch(host) || Host.Match(host).Value.Length != host.Length)
            return host;
        string name = host.ToLowerInvariant();
        if (name == "peppy.chigau.com")
            return $"osu.{Server}"; // 2008's beatmap downloads
        int domainStart = name.EndsWith("lekuru.xyz") ? name.Length - "lekuru.xyz".Length
            : name.EndsWith("titanic.sh") ? name.Length - "titanic.sh".Length
            : name.Length - "ppy.sh".Length;
        if (domainStart == 0)
            return Server;
        string[] labels = name.Substring(0, domainStart - 1).Split('.');
        return $"{labels[labels.Length - 1]}.{Server}";
    }

    /// <summary>Every host named in <paramref name="text"/> (links, chat, strings), moved to the server.</summary>
    public static string Move(string text) =>
        string.IsNullOrEmpty(text) ? text : Host.Replace(text, m => MoveHost(m.Groups["host"].Value));

    /// <summary>
    /// A URL's host moved to the server; also HTTP bancho's fallbacks (http://&lt;public IPv4&gt;,
    /// http://c2.&lt;host&gt;:13381) to c.&lt;server&gt; on port 80. The path and query stay as they are.
    /// </summary>
    public static string MoveUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;
        int schemeEnd = url.IndexOf("://", System.StringComparison.Ordinal);
        if (schemeEnd < 0)
            return Move(url);
        int hostStart = schemeEnd + 3;
        int hostEnd = url.IndexOfAny(['/', '?', '#'], hostStart);
        if (hostEnd < 0)
            hostEnd = url.Length;
        string hostPort = url.Substring(hostStart, hostEnd - hostStart);
        string moved = MoveHostHeader(hostPort, url.StartsWith("http:", System.StringComparison.OrdinalIgnoreCase));
        return moved == hostPort ? url : url.Substring(0, hostStart) + moved + url.Substring(hostEnd);
    }

    /// <summary>A Host header value or URL authority (host[:port]), moved to the server.</summary>
    public static string MoveHostHeader(string hostPort, bool http = true)
    {
        if (string.IsNullOrEmpty(hostPort))
            return hostPort;
        int colon = hostPort.LastIndexOf(':');
        string host = colon > 0 ? hostPort.Substring(0, colon) : hostPort;
        string port = colon > 0 ? hostPort.Substring(colon) : string.Empty;

        if (http && IsPublicIPv4(host))
            return $"c.{Server}"; // a fallback bancho address
        if (!Contains(host))
            return hostPort;

        string moved = MoveHost(host);
        // TCP bancho's port on an HTTP bancho host: HTTP bancho is on port 80.
        if (http && port.StartsWith(":1338") && (moved.StartsWith("c") && moved.IndexOf('.') <= 3))
            port = string.Empty;
        return moved + port;
    }

    private static bool IsPublicIPv4(string host)
    {
        if (!Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$") || !IPAddress.TryParse(host, out var address))
            return false;
        byte[] b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }
}
