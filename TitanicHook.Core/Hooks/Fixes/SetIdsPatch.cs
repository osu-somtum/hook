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
/// Builds before BeatmapSetID existed (b452 to b20120916) only learn a map's set from osu!direct, so
/// today's maps from elsewhere showed as not downloaded there. The .osu loader reads BeatmapSetID
/// (a key checked before "Artist") into the field osu!direct's "already downloaded" lookup compares.
/// </summary>
public class SetIdsPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.SetIds";

    private static FieldInfo? _setId;

    public SetIdsPatch() : base(HookName)
    {
        _setId = SetIdField();
        if (_setId != null)
        {
            foreach (MethodInfo loader in BeatmapCompatPatch.FindLoaders())
            {
                if (ShapeCode.Shaped(loader, HasArtistKey) != null)
                    TargetMethods.Add(loader);
            }
        }
        Logging.HookStep(HookName, _setId == null ? "No osu!direct set lookup found" :
            TargetMethods.Count == 0 ? "The loader reads BeatmapSetID already" : $"Set id field {_setId.Name}, {TargetMethods.Count} loader(s)");
        Transpilers = [AccessTools.Method(typeof(SetIdsPatch), nameof(SetIdTranspiler))];
    }

    /// <summary>
    /// osu!direct's "already downloaded": the result parser's "Lookup(setId) != null", and the field
    /// the Lookup's predicate compares.
    /// </summary>
    private static FieldInfo? SetIdField()
    {
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (ShapeCode.Shaped(method, c => OldClientShapes.DownloadedLookup(c) >= 0) is not { } code)
                continue;
            int call = OldClientShapes.DownloadedLookup(code);
            if (code[call].Operand is not MethodBase lookup || ShapeCode.Shaped(lookup, c => OldClientShapes.Predicate(c) >= 0) is not { } lookupCode)
                continue;
            int predicate = OldClientShapes.Predicate(lookupCode);
            if (lookupCode[predicate].Operand is not MethodBase compare ||
                ShapeCode.Shaped(compare, c => OldClientShapes.ComparedField(c) >= 0) is not { } compareCode)
                continue;
            int field = OldClientShapes.ComparedField(compareCode);
            if (field >= 0 && compareCode[field].Operand is FieldInfo { FieldType: var type } setId && type == typeof(int))
                return setId;
        }
        return null;
    }

    /// <summary>"BeatmapSetID: 123" -> the beatmap's set id (called from the loader).</summary>
    public static void Read(object beatmap, string value)
    {
        try
        {
            if (_setId != null && beatmap != null && _setId.DeclaringType!.IsInstanceOfType(beatmap) &&
                int.TryParse(value?.Trim(), out int setId) && setId > 0)
                _setId.SetValue(beatmap, setId);
        }
        catch (Exception)
        {
        }
    }

    #region Hook

    private static IEnumerable<CodeInstruction> SetIdTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var list = new List<CodeInstruction>(instructions);
        if (ShapeCode.Shaped(list, HasArtistKey) is not { } code || OldClientShapes.ArtistKey(code) is not { } artist)
            return list;
        // The case's way out: its br (after the stfld in the method itself, when the br was followed).
        CodeInstruction exit = artist.Break >= 0 ? list[code[artist.Break].At] : list[code[artist.Store].At + 1];
        if (exit.opcode != OpCodes.Br && exit.opcode != OpCodes.Br_S)
            return list;
        // In front of [load key][ldstr "Artist"][call ==][brtrue case]:
        //   if (key == "BeatmapSetID") { SetIdsPatch.Read(beatmap, value); break; }
        // where the beatmap, the value and the break are the "Artist" case's own.
        int at = code[artist.LoadKey].At;
        CodeInstruction key = list[at];
        Label artistKey = generator.DefineLabel();
        var check = new CodeInstruction(key.opcode, key.operand) { labels = key.labels };
        key.labels = [artistKey];
        list.InsertRange(at,
        [
            check,
            new CodeInstruction(OpCodes.Ldstr, "BeatmapSetID"),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(string), "op_Equality", [typeof(string), typeof(string)])),
            new CodeInstruction(OpCodes.Brfalse, artistKey),
            new CodeInstruction(code[artist.LoadBeatmap].Op, code[artist.LoadBeatmap].Operand),
            new CodeInstruction(code[artist.LoadValue].Op, code[artist.LoadValue].Operand),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SetIdsPatch), nameof(Read))),
            new CodeInstruction(OpCodes.Br, exit.operand)
        ]);
        return list;
    }

    private static bool HasArtistKey(List<Il> code) => OldClientShapes.ArtistKey(code) != null;

    #endregion
}
