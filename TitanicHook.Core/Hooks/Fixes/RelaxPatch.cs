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
/// Relax and Autopilot, as osu-somtum-patcher does for today's osu!: misses show their X (from b639;
/// the judgement sprite was only drawn for hits under them) and a broken combo plays its sound. The
/// flags those checks read (set from the mods when a play starts) read as off there.
/// </summary>
public class RelaxJudgementPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.RelaxJudgement";

    private static string? _relax, _autopilot;

    public RelaxJudgementPatch() : base(HookName)
    {
        RelaxFlags.Find(out _relax, out _autopilot, out _);
        if (_relax != null)
        {
            foreach (MethodInfo method in ShapeCode.OsuMethods)
            {
                if (ShapeCode.Shaped(method, HasGates) != null)
                    TargetMethods.Add(method);
            }
        }
        Logging.HookStep(HookName, $"Relax flag {(_relax != null ? "found" : "not found")}, Autopilot flag {(_autopilot != null ? "found" : "not found")}; " +
                                   $"{TargetMethods.Count} method(s) with Relax/Autopilot checks");
        Transpilers = [AccessTools.Method(typeof(RelaxJudgementPatch), nameof(FlagsOffTranspiler))];
    }

    #region Hook

    private static IEnumerable<CodeInstruction> FlagsOffTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        if (_relax == null || ShapeCode.Shaped(list, HasGates) is not { } code)
            return list;
        foreach (int load in OldClientShapes.RelaxGates(code, _relax, _autopilot))
        {
            // The flag's load becomes "false"; the branch after it stays (labels on it are kept).
            list[code[load].At].opcode = OpCodes.Ldc_I4_0;
            list[code[load].At].operand = null;
        }
        return list;
    }

    private static bool HasGates(List<Il> code) => _relax != null && OldClientShapes.RelaxGates(code, _relax, _autopilot).Count > 0;

    #endregion
}

/// <summary>
/// The HUD under Relax and Autopilot shows as in any play (b394a on): score, accuracy, combo, HP bar,
/// progress bar/pie, the leaderboard during the play and taiko's score popups, which osu! hid under
/// them. The flag loads those checks read (<see cref="OldClientShapes.HudGates"/>) read as off there.
/// </summary>
public class RelaxHudPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.RelaxHud";

    private static string? _relax, _autopilot;

    public RelaxHudPatch() : base(HookName)
    {
        RelaxFlags.Find(out _relax, out _autopilot, out _);
        if (_relax != null)
        {
            foreach (MethodInfo method in ShapeCode.OsuMethods)
            {
                if (ShapeCode.Shaped(method, HasGates) != null)
                    TargetMethods.Add(method);
            }
            // Score and accuracy, the HP bar: in their constructors.
            foreach (ConstructorInfo constructor in ShapeCode.OsuConstructors)
            {
                if (ShapeCode.Shaped(constructor, HasGates) != null)
                    TargetConstructors.Add(constructor);
            }
        }
        Logging.HookStep(HookName, _relax == null ? "Relax flag not found" :
            $"{TargetMethods.Count + TargetConstructors.Count} method(s) hiding the HUD under Relax/Autopilot");
        Transpilers = [AccessTools.Method(typeof(RelaxHudPatch), nameof(HudOnTranspiler))];
    }

    #region Hook

    private static IEnumerable<CodeInstruction> HudOnTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        if (_relax == null || ShapeCode.Shaped(list, HasGates) is not { } code)
            return list;
        foreach (int load in OldClientShapes.HudGates(code, _relax, _autopilot))
        {
            // The flag's load becomes "false"; the branch after it stays (labels on it are kept).
            list[code[load].At].opcode = OpCodes.Ldc_I4_0;
            list[code[load].At].operand = null;
        }
        return list;
    }

    private static bool HasGates(List<Il> code) => _relax != null && OldClientShapes.HudGates(code, _relax, _autopilot).Count > 0;

    #endregion
}

/// <summary>
/// Relax and Autopilot plays are local scores (best, grade) like any other, as osu-somtum-patcher
/// does for today's osu! (from b699): the results screen's "has(mods, Relax)" and "has(mods,
/// Autopilot)" read as false.
/// </summary>
public class RelaxLocalScorePatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.RelaxLocalScore";

    public RelaxLocalScorePatch() : base(HookName)
    {
        // The results screen: the types that load "ranking-" sprites, but not the player (whose own
        // flag setup checks both mods too).
        RelaxFlags.Find(out _, out _, out string? player);
        var rankingTypes = new List<Type>();
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (method.DeclaringType == null || rankingTypes.Contains(method.DeclaringType) || method.DeclaringType.FullName == player)
                continue;
            try
            {
                foreach (string text in SigScanning.GetStrings(method))
                {
                    if (text.StartsWith("ranking-"))
                    {
                        rankingTypes.Add(method.DeclaringType);
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (method.DeclaringType != null && rankingTypes.Contains(method.DeclaringType) && ShapeCode.Shaped(method, HasPair) != null)
                TargetMethods.Add(method);
        }
        Logging.HookStep(HookName, $"{TargetMethods.Count} results screen check(s)");
        Transpilers = [AccessTools.Method(typeof(RelaxLocalScorePatch), nameof(ModsOffTranspiler))];
    }

    #region Hook

    private static IEnumerable<CodeInstruction> ModsOffTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        if (ShapeCode.Shaped(list, HasPair) is { } code)
            RelaxFlags.AnswerFalse(list, code, OldClientShapes.ModPairCalls(code));
        return list;
    }

    private static bool HasPair(List<Il> code) => OldClientShapes.ModPairCalls(code).Count > 0;

    #endregion
}

/// <summary>
/// Relax and Autopilot plays are submitted (b452 to b1844, whose ranked mods check refused them; later
/// builds submit them already), for the server's Relax and Autopilot leaderboards. SpunOut and
/// Autoplay stay unranked.
/// </summary>
public class RelaxSubmitPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.RelaxSubmit";

    public RelaxSubmitPatch() : base(HookName)
    {
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (method.ReturnType == typeof(bool) && ShapeCode.Shaped(method, HasRankedChecks) != null)
                TargetMethods.Add(method);
        }
        Logging.HookStep(HookName, TargetMethods.Count == 0 ? "This build submits Relax/Autopilot plays already" : $"{TargetMethods.Count} ranked mods check(s)");
        Transpilers = [AccessTools.Method(typeof(RelaxSubmitPatch), nameof(RankedTranspiler))];
    }

    #region Hook

    private static IEnumerable<CodeInstruction> RankedTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        if (ShapeCode.Shaped(list, HasRankedChecks) is { } code)
            RelaxFlags.AnswerFalse(list, code, OldClientShapes.RankedModsChecks(code));
        return list;
    }

    private static bool HasRankedChecks(List<Il> code) => OldClientShapes.RankedModsChecks(code).Count > 0;

    #endregion
}

/// <summary>
/// The leaderboard request of builds that don't send their mods (b337 to b20130319) gets
/// "&amp;mods=" with the mods chosen now, so the server shows the Relax or Autopilot board in song
/// select. (Their bancho status has the mods only while playing.)
/// </summary>
public class LeaderboardModsPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.LeaderboardMods";

    private static FieldInfo? _mods;
    private static MethodInfo? _unwrap;

    public LeaderboardModsPatch() : base(HookName)
    {
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (method.IsStatic && method.ReturnType == typeof(bool) && method.GetParameters().Length == 1 &&
                ShapeCode.Shaped(method, c => OldClientShapes.CurrentModsRead(c) >= 0) is { } code &&
                code[OldClientShapes.CurrentModsRead(code)].Operand is FieldInfo { IsStatic: true } mods)
            {
                _mods = mods;
                // Obfuscated<Mods> (b20130303, b20130319): read through its conversion.
                _unwrap = code.Count == 5 && code[1].Operand is MethodInfo { IsStatic: true } unwrap ? unwrap : null;
                break;
            }
        }
        if (_mods != null)
        {
            foreach (MethodInfo method in ShapeCode.OsuMethods)
            {
                if (ShapeCode.Shaped(method, HasRequest) is { } code && code[OldClientShapes.LeaderboardRequest(code)].Operand is ConstructorInfo ctor &&
                    ctor.GetParameters()[0].ParameterType == typeof(string))
                    TargetMethods.Add(method);
            }
        }
        Logging.HookStep(HookName, _mods == null ? "Current mods not found" :
            TargetMethods.Count == 0 ? "This build sends its mods with the leaderboard already" : $"Mods ({_mods.Name}) added to {TargetMethods.Count} leaderboard request(s)");
        Transpilers = [AccessTools.Method(typeof(LeaderboardModsPatch), nameof(RequestTranspiler))];
    }

    /// <summary>The leaderboard URL with "&amp;mods=" (the mods chosen now) when it has none.</summary>
    public static string WithMods(string url)
    {
        try
        {
            if (_mods != null && url != null && OldClientShapes.IsLeaderboardUrl(url) &&
                url.IndexOf("mods=", StringComparison.Ordinal) < 0)
            {
                object? mods = _mods.GetValue(null);
                if (_unwrap != null)
                    mods = _unwrap.Invoke(null, [mods]);
                return url + "&mods=" + Convert.ToInt32(mods).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
        }
        return url!;
    }

    private static bool HasRequest(List<Il> code) => OldClientShapes.LeaderboardRequest(code) >= 0;

    #region Hook

    private static IEnumerable<CodeInstruction> RequestTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        if (ShapeCode.Shaped(list, HasRequest) is not { } code)
            return list;
        // new Request(url) -> new Request(WithMods(url)), the URL being on the stack.
        list.Insert(code[OldClientShapes.LeaderboardRequest(code)].At, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LeaderboardModsPatch), nameof(WithMods))));
        return list;
    }

    #endregion
}

/// <summary>The Relax and Autopilot flags: "flag = has(mods, Relax)" where a play starts.</summary>
internal static class RelaxFlags
{
    private static bool _searched;
    private static string? _relax, _autopilot, _owner;

    public static void Find(out string? relax, out string? autopilot, out string? owner)
    {
        if (!_searched)
        {
            _searched = true;
            foreach (MethodInfo method in ShapeCode.OsuMethods)
            {
                if (ShapeCode.Shaped(method, SetsFlag) is not { } code)
                    continue;
                int r = OldClientShapes.FlagAssignment(code, OldClientShapes.RelaxMod);
                if (r >= 0 && _relax == null)
                {
                    _relax = code[r].Member;
                    _owner = code[r].Owner;
                }
                int a = OldClientShapes.FlagAssignment(code, OldClientShapes.AutopilotMod);
                if (a >= 0 && _autopilot == null)
                    _autopilot = code[a].Member;
                if (_relax != null && _autopilot != null)
                    break;
            }
        }
        relax = _relax;
        autopilot = _autopilot;
        owner = _owner;
    }

    private static bool SetsFlag(List<Il> code) =>
        OldClientShapes.FlagAssignment(code, OldClientShapes.RelaxMod) >= 0 || OldClientShapes.FlagAssignment(code, OldClientShapes.AutopilotMod) >= 0;

    /// <summary>"has(mods, mod)" calls (indices into <paramref name="code"/>) answer false: their answer dropped, false instead.</summary>
    public static void AnswerFalse(List<CodeInstruction> list, List<Il> code, List<int> calls)
    {
        var at = new List<int>();
        foreach (int call in calls)
            at.Add(code[call].At);
        at.Sort();
        for (int i = at.Count - 1; i >= 0; i--)
        {
            list.Insert(at[i] + 1, new CodeInstruction(OpCodes.Pop));
            list.Insert(at[i] + 2, new CodeInstruction(OpCodes.Ldc_I4_0));
        }
    }
}
