// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Harmony;
using TitanicHook.Core.Compat;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;

namespace TitanicHook.Core.Hooks.Fixes;

/// <summary>
/// Chat links (b337 to b20121223): the chat found links by "http://" only, so an https:// one wasn't
/// clickable; it now finds whichever comes first. The in-game beatmap links (osu!direct for
/// supporters) match https:// too. Opening "http://https://..." is fixed in <see cref="Misc.StartProcessHook"/>.
/// </summary>
public class ChatLinksPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.ChatLinks";

    public ChatLinksPatch() : base(HookName)
    {
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (ShapeCode.Shaped(method, HasLinks) != null)
                TargetMethods.Add(method);
        }
        Logging.HookStep(HookName, TargetMethods.Count == 0 ? "This build finds https:// links already" : $"{TargetMethods.Count} method(s) with chat links");
        Transpilers = [AccessTools.Method(typeof(ChatLinksPatch), nameof(LinksTranspiler))];
    }

    /// <summary>Where the first link in <paramref name="text"/> starts (http:// or https://), or -1.</summary>
    public static int IndexOfUrl(string text, string http) => IndexOfUrl(text, http, 0);

    /// <summary>Where the first link in <paramref name="text"/> from <paramref name="start"/> starts (http:// or https://), or -1.</summary>
    public static int IndexOfUrl(string text, string http, int start)
    {
        int plain = text.IndexOf("http://", start, StringComparison.Ordinal);
        int secure = text.IndexOf("https://", start, StringComparison.Ordinal);
        if (plain < 0)
            return secure;
        if (secure < 0)
            return plain;
        return Math.Min(plain, secure);
    }

    /// <summary>A beatmap link's pattern ("http\://osu...") that matches https:// too.</summary>
    public static string AnyScheme(string pattern) =>
        pattern != null && pattern.StartsWith("http\\://", StringComparison.Ordinal) ? "https?" + pattern.Substring(4) : pattern!;

    /// <summary>An escaped "http://osu..." that matches https:// too, as osu! did from b20130303.</summary>
    public static string AnyEscapedScheme(string escaped) => escaped == null ? null! : escaped.Replace("http", "http[s]?");

    private static bool HasLinks(List<Il> code) =>
        OldClientShapes.UrlSearches(code).Count > 0 || OldClientShapes.HttpLinkPatterns(code).Count > 0 || OldClientShapes.EscapedHttpLinks(code).Count > 0;

    #region Hook

    private static IEnumerable<CodeInstruction> LinksTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        if (ShapeCode.Shaped(list, HasLinks) is not { } code)
            return list;
        // text.IndexOf("http://"[, start]) -> IndexOfUrl(text, "http://"[, start]): the same stack.
        foreach (int call in OldClientShapes.UrlSearches(code))
        {
            var types = code[call].Params == 1 ? new[] { typeof(string), typeof(string) } : new[] { typeof(string), typeof(string), typeof(int) };
            list[code[call].At].opcode = OpCodes.Call;
            list[code[call].At].operand = AccessTools.Method(typeof(ChatLinksPatch), nameof(IndexOfUrl), types);
        }
        // "http\://osu..." -> AnyScheme("http\://osu..."), after the string is loaded (or decrypted);
        // Regex.Escape("http://osu...") -> AnyEscapedScheme(Regex.Escape("http://osu...")).
        var at = new SortedDictionary<int, string>();
        foreach (int text in OldClientShapes.HttpLinkPatterns(code))
            at[code[text].At] = nameof(AnyScheme);
        foreach (int escape in OldClientShapes.EscapedHttpLinks(code))
            at[code[escape].At] = nameof(AnyEscapedScheme);
        var places = new List<int>(at.Keys);
        for (int i = places.Count - 1; i >= 0; i--)
            list.Insert(places[i] + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ChatLinksPatch), at[places[i]])));
        return list;
    }

    #endregion
}
