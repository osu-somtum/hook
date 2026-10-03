// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Harmony;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;

namespace TitanicHook.Core.Hooks.Misc;

/// <summary>
/// The 2014-15 updater (check-updates.php): its check answers "no update" (0, the first UpdateStates
/// value) without asking. A private server's list would replace the build with today's osu!, and a
/// failing check makes the game repair itself after a few tries.
/// </summary>
public class DisableUpdaterPatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.DisableUpdater";

    public DisableUpdaterPatch() : base(HookName)
    {
        foreach (Type type in AssemblyUtils.CommonOrOsuTypes)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (MethodInfo method in methods)
            {
                try
                {
                    if (method.ReturnType.IsEnum && method.GetParameters().Length == 0 && method.GetMethodBody() != null &&
                        Array.Exists(SigScanning.GetStrings(method), s => s.Contains("action=check&stream=")))
                        TargetMethods.Add(method);
                }
                catch (Exception)
                {
                }
            }
        }
        Transpilers = [AccessTools.Method(typeof(DisableUpdaterPatch), nameof(NoUpdateTranspiler))];
    }

    #region Hook

    private static IEnumerable<CodeInstruction> NoUpdateTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Ldc_I4_0);
        yield return new CodeInstruction(OpCodes.Ret);
        foreach (CodeInstruction instruction in instructions)
            yield return instruction;
    }

    #endregion
}
