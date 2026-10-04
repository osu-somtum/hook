// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Harmony;
using TitanicHook.Core.Compat;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;

namespace TitanicHook.Core.Hooks.Fixes;

/// <summary>
/// Song select (b699 to b1218) kept the playing song when the next map's audio file had the same
/// name, and today's maps all call theirs audio.mp3: the folder has to match too, as osu! itself
/// checked from b1553 on. "next.Audio == playing.Audio" becomes "... &amp;&amp; next.Folder == playing.Folder".
/// </summary>
public class SongSwitchPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.SongSwitch";

    private static FieldInfo? _folder;

    public SongSwitchPatch() : base(HookName)
    {
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (ShapeCode.Shaped(method, HasAudioCheck) is not { } code)
                continue;
            int check = OldClientShapes.SameAudioCheck(code);
            if (code[check - 2].Operand is not FieldInfo { FieldType: { } beatmap })
                continue;
            _folder = FolderField(beatmap);
            if (_folder != null)
                TargetMethods.Add(method);
        }
        Logging.HookStep(HookName, _folder != null ? $"Audio check found, folder is {_folder.Name}" : "No audio-only check (this build compares the folder)");
        Transpilers = [AccessTools.Method(typeof(SongSwitchPatch), nameof(SameFolderTranspiler))];
    }

    private static bool HasAudioCheck(List<Il> code) => CallsStreamCreateFile(code) && OldClientShapes.SameAudioCheck(code) >= 0;

    private static bool CallsStreamCreateFile(List<Il> code)
    {
        foreach (Il il in code)
        {
            if (il.Name == "BASS_StreamCreateFile")
                return true;
        }
        return false;
    }

    /// <summary>The beatmap's folder: the string field it sets from Path.GetDirectoryName (in a constructor).</summary>
    private static FieldInfo? FolderField(System.Type beatmap)
    {
        string type = beatmap.FullName ?? beatmap.ToString();
        var members = new List<MethodBase>();
        try
        {
            members.AddRange(beatmap.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }
        catch (System.Exception)
        {
        }
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (method.DeclaringType == beatmap)
                members.Add(method);
        }
        foreach (MethodBase member in members)
        {
            if (ShapeCode.Shaped(member, c => OldClientShapes.FolderAssignment(c, type) >= 0) is { } code)
                return code[OldClientShapes.FolderAssignment(code, type)].Operand as FieldInfo;
        }
        return null;
    }

    #region Hook

    private static IEnumerable<CodeInstruction> SameFolderTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        if (_folder == null || ShapeCode.Shaped(list, c => OldClientShapes.SameAudioCheck(c) >= 0) is not { } code)
            return list;
        int equals = OldClientShapes.SameAudioCheck(code);
        // [ldsfld playing][brfalse][load next][ldfld audio][ldsfld playing][ldfld audio][call ==]
        Il next = code[equals - 4], playing = code[equals - 6];
        list.InsertRange(code[equals].At + 1,
        [
            new CodeInstruction(next.Op, next.Operand),
            new CodeInstruction(OpCodes.Ldfld, _folder),
            new CodeInstruction(OpCodes.Ldsfld, playing.Operand),
            new CodeInstruction(OpCodes.Ldfld, _folder),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(string), "op_Equality", [typeof(string), typeof(string)])),
            new CodeInstruction(OpCodes.And)
        ]);
        return list;
    }

    #endregion
}
