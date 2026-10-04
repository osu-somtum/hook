// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Harmony;
using TitanicHook.Core.Compat;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;

namespace TitanicHook.Core.Hooks.Fixes;

/// <summary>
/// Screen mode (b476 to 2015): when windowed, the OpenGL renderer put back the desktop size the build
/// saw at startup, which with today's display scaling isn't a mode the screen has, and every refused
/// mode showed two message boxes ("Unable to process your request"). Windowed now asks Windows for
/// the desktop's own mode, and a refused mode is skipped quietly. (b20121223 to b20130716 resize
/// another way: only their message boxes are quiet.)
/// </summary>
public class ScreenModePatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.ScreenMode";

    private static string? _changer;

    public ScreenModePatch() : base(HookName)
    {
        // The mode changer: static (int width, int height), calls ChangeDisplaySettings.
        var changers = new List<MethodInfo>();
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (ShapeCode.Of(method) is not { } code || !OldClientShapes.ChangesDisplaySettings(code))
                continue;
            changers.Add(method);
            ParameterInfo[] parameters = method.GetParameters();
            if (_changer == null && method.IsStatic && method.ReturnType == typeof(void) && parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(int))
                _changer = ShapeCode.MemberOf(method);
        }
        int boxes = 0, windowed = 0;
        foreach (MethodInfo changer in changers)
        {
            if (OldClientShapes.ModeMessageBoxes(ShapeCode.Of(changer)!).Count > 0)
            {
                TargetMethods.Add(changer);
                boxes++;
            }
        }
        if (_changer != null)
        {
            foreach (MethodInfo method in ShapeCode.OsuMethods)
            {
                if (ShapeCode.Of(method) is { } code && OldClientShapes.WindowedModeCall(code, _changer) >= 0 && !TargetMethods.Contains(method))
                {
                    TargetMethods.Add(method);
                    windowed++;
                }
            }
        }
        Logging.HookStep(HookName, $"{changers.Count} mode changer(s), {boxes} with message boxes, {windowed} windowed resize(s)");
        Transpilers = [AccessTools.Method(typeof(ScreenModePatch), nameof(ScreenModeTranspiler))];
    }

    [DllImport("user32.dll")]
    private static extern int ChangeDisplaySettings(IntPtr devMode, int flags);

    /// <summary>
    /// In place of the windowed resize's mode change: the desktop's own mode (Windows' registry one),
    /// which always exists; asking for it when it's set already changes nothing.
    /// </summary>
    public static void RestoreDesktopMode(int width, int height)
    {
        try
        {
            ChangeDisplaySettings(IntPtr.Zero, 0);
        }
        catch (Exception)
        {
        }
    }

    #region Hook

    private static IEnumerable<CodeInstruction> ScreenModeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        List<Il> code = ShapeCode.Of(list);

        if (_changer != null && OldClientShapes.WindowedModeCall(code, _changer) is var windowed and >= 0)
            list[windowed].operand = AccessTools.Method(typeof(ScreenModePatch), nameof(RestoreDesktopMode));

        // MessageBox.Show(args) -> the args dropped, DialogResult.OK (1) in place of its answer.
        List<int> boxes = OldClientShapes.ModeMessageBoxes(code);
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            int call = boxes[i];
            int args = code[call].Params;
            list[call].opcode = OpCodes.Pop;
            list[call].operand = null;
            var rest = new List<CodeInstruction>();
            for (int a = 1; a < args; a++)
                rest.Add(new CodeInstruction(OpCodes.Pop));
            rest.Add(new CodeInstruction(OpCodes.Ldc_I4_1));
            list.InsertRange(call + 1, rest);
        }
        return list;
    }

    #endregion
}
