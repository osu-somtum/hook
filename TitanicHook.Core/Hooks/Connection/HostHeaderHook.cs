// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2025 Oreeeee

#if NET40
using System.Net;
using System.Reflection;
using Harmony;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;

namespace TitanicHook.Core.Hooks.Connection;

/// <summary>
/// Hook for Host header in clients using pWebRequest.
/// This works by making a prefix in setter of HttpWebRequest.Host and changing the passed in domain.
/// </summary>
public class HostHeaderHook : TitanicPatch
{
    public const string HookName = "sh.Titanic.Hook.HostHeader";

    public HostHeaderHook() : base(HookName)
    {
        TargetMethods = [typeof(HttpWebRequest).GetMethod("set_Host", BindingFlags.Instance | BindingFlags.Public)];
        Prefixes = [AccessTools.Method(typeof(HostHeaderHook), nameof(SetHostPrefix))];
    }
    
    #region Hook
    
    private static void SetHostPrefix(ref string __0)
    {
        Logging.HookTrigger(HookName);
        string moved = ServerHosts.MoveHostHeader(__0);
        if (moved != __0)
        {
            Logging.HookOutput(HookName, $"Moving {__0} to {moved} in set_Host");
            __0 = moved;
        }
    }
    
    #endregion
}
#endif // NET40
