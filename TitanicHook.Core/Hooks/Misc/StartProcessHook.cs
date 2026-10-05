// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2025 Oreeeee

using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Harmony;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;

namespace TitanicHook.Core.Hooks.Misc;

public class StartProcessHook : TitanicPatch
{
    public const string HookName = "sh.Titanic.Hook.StartProcess";
    
    /// <summary>
    /// Regex for the copyright link
    /// </summary>
    private static readonly Regex CopyrightRegex = new Regex(@"^https?:\/\/osu\.ppy\.sh\/?$");
    
    public StartProcessHook() : base(HookName)
    {
        TargetMethods = [GetTargetMethod()];
        Prefixes = [AccessTools.Method(typeof(StartProcessHook), nameof(ProcessStartPrefix))];
    }

    private static MethodInfo? GetTargetMethod()
    {
        return typeof(Process)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Start" &
                                 m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType.FullName == "System.Diagnostics.ProcessStartInfo");
    }

    #region Hook

    private static void ProcessStartPrefix(ref ProcessStartInfo __0)
    {
        Logging.HookTrigger(HookName);
        
        // osu!somtum: b639 to b1844 put "http://" in front of a chat link that doesn't start with it,
        // https:// ones included ("http://https://...").
        if (__0.FileName.StartsWith("http://https://") || __0.FileName.StartsWith("http://http://"))
            __0.FileName = __0.FileName.Substring("http://".Length);

        // Do not replace the link for copyright button
        if (CopyrightRegex.IsMatch(__0.FileName)) return;
        
        if (ServerHosts.Contains(__0.FileName))
            __0.FileName = ServerHosts.Move(__0.FileName);
    }

    #endregion
}
