// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2025 Oreeeee

#if NET40
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Harmony;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;

namespace TitanicHook.Core.Hooks.Connection;

/// <summary>
/// Hook for create request method in WebRequest class
/// </summary>
public class CreateRequestHook : TitanicPatch
{
    public const string HookName = "sh.Titanic.Hook.CreateRequest";

    public CreateRequestHook() : base(HookName)
    {
        TargetMethods = [GetTargetMethod()];
        Prefixes = [AccessTools.Method(typeof(CreateRequestHook), nameof(CreateRequestPrefix))];
    }

    private static MethodInfo? GetTargetMethod()
    {
        // Look for the Create(string) overload
        return typeof(WebRequest).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "Create" &&
                                 m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType.FullName == "System.String");
    }
    
    #region Hook
    
    private static void CreateRequestPrefix(ref string __0)
    {
        Logging.HookTrigger(HookName);
        string moved = ServerHosts.MoveUrl(__0);
        if (moved != __0)
        {
            Logging.HookOutput(HookName, $"Moving {__0} to {moved} in WebRequest.Create(string)");
            __0 = moved;
        }
    }
    
    #endregion
}
#endif // NET40
