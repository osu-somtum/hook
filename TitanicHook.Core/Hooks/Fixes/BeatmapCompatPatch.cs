// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using ClrTest.Reflection;
using Harmony;
using TitanicHook.Core.Compat;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;
using TitanicHook.Core.OsuInterop;

namespace TitanicHook.Core.Hooks.Fixes;

/// <summary>
/// Today's beatmaps in old clients: the client's beatmap loaders (the methods that read .osu/.osb
/// lines and parse their [Sections]) read each line through <see cref="BeatmapCompat.ReadLine"/>,
/// which rewrites only what this build can't read (decimal difficulty, perfect-circle sliders,
/// osu!mania, storyboard decimals and the Overlay layer, newer hit object and timing point bits).
/// </summary>
public class BeatmapCompatPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.BeatmapCompat";

    public BeatmapCompatPatch() : base(HookName)
    {
        Configure(OsuVersion.GetVersion());
        foreach (MethodInfo loader in FindLoaders())
            TargetMethods.Add(loader);
        Transpilers = [AccessTools.Method(typeof(BeatmapCompatPatch), nameof(ReadLineTranspiler))];
    }

    /// <summary>
    /// What the build can't read, by when osu! learnt it (as found in every decompiled build):
    /// whole-number difficulty before b20140616, no perfect-circle sliders before b20121115, no
    /// osu!mania before b20121003. Numbered builds (b337 to b1844) come before all of these. An
    /// unknown version gets only the rewrites that are safe everywhere.
    /// </summary>
    private static void Configure(string? version)
    {
        bool numbered = version != null && Regex.IsMatch(version, @"^b\d{1,4}(?!\d)");
        Match dated = Regex.Match(version ?? string.Empty, @"^b(\d{8})");
        int date = dated.Success ? int.Parse(dated.Groups[1].Value) : 0;
        bool before(int build) => numbered || (date != 0 && date < build);

        BeatmapCompat.RoundDifficulty = before(20140616);
        BeatmapCompat.ConvertPerfectCurves = before(20121115);
        BeatmapCompat.ManiaUnsupported = before(20121003);
        BeatmapCompat.OverlayToForeground = true;
        Logging.HookStep(HookName, $"{version ?? "unknown build"}: round difficulty {BeatmapCompat.RoundDifficulty}, " +
                                   $"perfect curves converted {BeatmapCompat.ConvertPerfectCurves}, mania unsupported {BeatmapCompat.ManiaUnsupported}");
    }

    /// <summary>Methods that read lines and know beatmap files ("osu file format", or the FileSection enum).</summary>
    private static List<MethodInfo> FindLoaders()
    {
        var loaders = new List<MethodInfo>();
        foreach (Type type in AssemblyUtils.OsuTypes)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (MethodInfo method in methods)
            {
                try
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
                        continue;
                    if (IsLoader(method))
                        loaders.Add(method);
                }
                catch (Exception)
                {
                    // Methods the IL reader can't read aren't loaders.
                }
            }
        }
        Logging.HookStep(HookName, $"Found {loaders.Count} beatmap loader(s)");
        return loaders;
    }

    private static bool IsLoader(MethodInfo method)
    {
        bool readsLines = false, knowsSections = false;
        foreach (ILInstruction instruction in new ILReader(method))
        {
            if (instruction is InlineMethodInstruction call && IsReadLine(call.Method))
                readsLines = true;
            else if (instruction is InlineTokInstruction token && token.Member is Type { Name: "FileSection" })
                knowsSections = true;
            else if (instruction is InlineTypeInstruction typeInstruction && typeInstruction.Type.Name == "FileSection")
                knowsSections = true;
            else if (instruction is InlineStringInstruction text && text.String.StartsWith("osu file format"))
                knowsSections = true;
        }
        if (!readsLines)
            return false;
        if (knowsSections)
            return true;
        // Builds with encrypted strings: the header check's text only shows decrypted.
        foreach (string text in SigScanning.GetStrings(method))
        {
            if (text.StartsWith("osu file format"))
                return true;
        }
        return false;
    }

    private static bool IsReadLine(MethodBase? method) =>
        method is { Name: "ReadLine" } && method.GetParameters().Length == 0 &&
        (method.DeclaringType == typeof(TextReader) || method.DeclaringType == typeof(StreamReader));

    #region Hook

    private static IEnumerable<CodeInstruction> ReadLineTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo compatReadLine = AccessTools.Method(typeof(BeatmapCompat), nameof(BeatmapCompat.ReadLine));
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && IsReadLine(instruction.operand as MethodBase))
            {
                // The reader is already on the stack: BeatmapCompat.ReadLine(TextReader) takes it.
                instruction.opcode = OpCodes.Call;
                instruction.operand = compatReadLine;
            }
            yield return instruction;
        }
    }

    #endregion
}
