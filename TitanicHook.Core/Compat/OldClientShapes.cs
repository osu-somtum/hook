// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum
//
// osu!somtum: where an old client does what the decompiled clients' somtum patcher changes (its steps
// 6 to 9), found by the shape of the IL, not by names (obfuscation changes those). These are plain
// functions over a method's instructions, so the same code runs in the hook (on Harmony's
// instructions) and in tests on every original build.

#nullable enable

using System.Collections.Generic;
using System.Reflection.Emit;

namespace TitanicHook.Core.Compat
{
    /// <summary>One instruction as the shape checks see it.</summary>
    public sealed class Il
    {
        public OpCode Op;

        /// <summary>The instruction's own operand (a FieldInfo, a MethodBase...), for whoever built the list.</summary>
        public object? Operand;

        /// <summary>The field or method used, as "Owner::Name" (two uses of one member compare equal).</summary>
        public string? Member;

        public string? Name;
        public string? Owner;

        /// <summary>A field's type, or a method's return type (full names).</summary>
        public string? Type;

        public int Params;

        /// <summary>A static field, or a static method.</summary>
        public bool Static;

        /// <summary>A P/Invoke method's entry point.</summary>
        public string? EntryPoint;

        /// <summary>An integer constant (any ldc.i4 form).</summary>
        public int? Int;

        /// <summary>A string constant (ldstr, or an encrypted string's decrypted text).</summary>
        public string? Str;

        /// <summary>A branch's target (index into the method's instructions), or -1.</summary>
        public int Target = -1;
    }

    public static class OldClientShapes
    {
        public const int RelaxMod = 128;
        public const int AutopilotMod = 8192;

        private static bool IsCall(Il i) => i.Op == OpCodes.Call || i.Op == OpCodes.Callvirt;

        private static bool IsCondBranch(Il i) => i.Op.FlowControl == FlowControl.Cond_Branch;

        private static bool IsLoad(Il i) =>
            i.Op == OpCodes.Ldarg || i.Op == OpCodes.Ldarg_S || i.Op == OpCodes.Ldarg_0 || i.Op == OpCodes.Ldarg_1 ||
            i.Op == OpCodes.Ldarg_2 || i.Op == OpCodes.Ldarg_3 || i.Op == OpCodes.Ldloc || i.Op == OpCodes.Ldloc_S ||
            i.Op == OpCodes.Ldloc_0 || i.Op == OpCodes.Ldloc_1 || i.Op == OpCodes.Ldloc_2 || i.Op == OpCodes.Ldloc_3;

        private static bool IsStringEquality(Il i) => IsCall(i) && i.Name == "op_Equality" && i.Owner == "System.String";

        // ── 9. Relax and Autopilot ─────────────────────────────────────────────────────────────

        /// <summary>
        /// "flag = has(mods, mod)": [ldc mod][call bool][stsfld bool], where a play starts. The index of
        /// the stsfld (whose member is the flag gameplay checks), or -1.
        /// </summary>
        public static int FlagAssignment(IList<Il> code, int mod)
        {
            for (int k = 0; k + 2 < code.Count; k++)
            {
                if (code[k].Int == mod && IsCall(code[k + 1]) && code[k + 1].Type == "System.Boolean" &&
                    code[k + 2].Op == OpCodes.Stsfld && code[k + 2].Type == "System.Boolean")
                    return k + 2;
            }
            return -1;
        }

        /// <summary>
        /// The flag loads that hide misses and the combo break under Relax/Autopilot (read as false, the
        /// checks pass as without them):
        /// the judgement sprite, "if ((!relax &amp;&amp; !autopilot) || result >= Ignore)" (Ignore is 0),
        /// and the combo break sound, "if (combo > 20 &amp;&amp; !relax &amp;&amp; !autopilot)".
        /// Builds before Autopilot (b337 to b639) check Relax alone.
        /// </summary>
        public static List<int> RelaxGates(IList<Il> code, string relax, string? autopilot)
        {
            var loads = new List<int>();
            for (int k = 0; k + 1 < code.Count; k++)
            {
                if (code[k].Op != OpCodes.Ldsfld || code[k].Member != relax || !IsCondBranch(code[k + 1]))
                    continue;
                int j = k + 2, ap = -1;
                if (autopilot != null)
                {
                    if (j + 1 >= code.Count || code[j].Op != OpCodes.Ldsfld || code[j].Member != autopilot || !IsCondBranch(code[j + 1]))
                        continue;
                    ap = j;
                    j += 2;
                }
                bool miss = j + 2 < code.Count && code[j + 1].Int == 0 &&
                            (code[j + 2].Op == OpCodes.Blt || code[j + 2].Op == OpCodes.Blt_S || code[j + 2].Op == OpCodes.Bge ||
                             code[j + 2].Op == OpCodes.Bge_S || code[j + 2].Op == OpCodes.Clt);
                bool combo = k >= 2 && code[k - 2].Int == 20 &&
                             (code[k - 1].Op == OpCodes.Ble || code[k - 1].Op == OpCodes.Ble_S || code[k - 1].Op == OpCodes.Bgt ||
                              code[k - 1].Op == OpCodes.Bgt_S || code[k - 1].Op == OpCodes.Cgt);
                if (!miss && !combo)
                    continue;
                loads.Add(k);
                if (ap >= 0)
                    loads.Add(ap);
            }
            return loads;
        }

        /// <summary>
        /// The results screen's "keep it as a local score" check: "has(mods, Relax)" and "has(mods,
        /// Autopilot)", the same method called close together. The indices of the two calls.
        /// </summary>
        public static List<int> ModPairCalls(IList<Il> code)
        {
            var calls = new List<int>();
            for (int k = 0; k + 1 < code.Count; k++)
            {
                if (code[k].Int != RelaxMod || !IsCall(code[k + 1]) || code[k + 1].Type != "System.Boolean")
                    continue;
                for (int j = k + 2; j + 1 < code.Count && j < k + 14; j++)
                {
                    if (code[j].Int == AutopilotMod && IsCall(code[j + 1]) && code[j + 1].Member == code[k + 1].Member)
                    {
                        calls.Add(k + 1);
                        calls.Add(j + 1);
                        break;
                    }
                }
            }
            return calls;
        }

        // ── 8. Song switch ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Song select keeping the playing song: "playing != null &amp;&amp; next.Audio == playing.Audio" as
        /// [ldsfld playing][brfalse][load next][ldfld audio][ldsfld playing][ldfld audio][call ==], in
        /// builds that don't compare the folder right after. The index of the == call, or -1.
        /// </summary>
        public static int SameAudioCheck(IList<Il> code)
        {
            for (int k = 0; k + 7 < code.Count; k++)
            {
                if (code[k].Op != OpCodes.Ldsfld || !(code[k + 1].Op == OpCodes.Brfalse || code[k + 1].Op == OpCodes.Brfalse_S) ||
                    !IsLoad(code[k + 2]) || code[k + 3].Op != OpCodes.Ldfld || code[k + 4].Op != OpCodes.Ldsfld ||
                    code[k + 4].Member != code[k].Member || code[k + 5].Op != OpCodes.Ldfld || code[k + 5].Member != code[k + 3].Member ||
                    !IsStringEquality(code[k + 6]))
                    continue;
                // Builds from b1553 compare the folder next: "&& next.Folder == playing.Folder".
                int n = k + 8;
                if (n + 4 < code.Count && IsLoad(code[n]) && code[n + 1].Op == OpCodes.Ldfld && code[n + 2].Op == OpCodes.Ldsfld &&
                    code[n + 2].Member == code[k].Member && code[n + 3].Op == OpCodes.Ldfld && IsStringEquality(code[n + 4]))
                    return -1;
                return k + 6;
            }
            return -1;
        }

        /// <summary>A beatmap's folder: "field = Path.GetDirectoryName(...)" in its type. The stfld's index, or -1.</summary>
        public static int FolderAssignment(IList<Il> code, string beatmapType)
        {
            for (int k = 0; k + 1 < code.Count; k++)
            {
                if (IsCall(code[k]) && code[k].Name == "GetDirectoryName" && code[k].Owner == "System.IO.Path" &&
                    code[k + 1].Op == OpCodes.Stfld && code[k + 1].Owner == beatmapType && code[k + 1].Type == "System.String")
                    return k + 1;
            }
            return -1;
        }

        // ── 7. Set ids ─────────────────────────────────────────────────────────────────────────

        /// <summary>The .osu loader's "Artist" key: where a "BeatmapSetID" key goes in front of it.</summary>
        public sealed class KeyCase
        {
            /// <summary>[load key] [ldstr "Artist"] [call ==] [brtrue case]: the load's index.</summary>
            public int LoadKey;

            /// <summary>The case: [load beatmap] [load value] [stfld artist] [br end].</summary>
            public int LoadBeatmap, LoadValue, Break;
        }

        public static KeyCase? ArtistKey(IList<Il> code)
        {
            for (int k = 1; k + 2 < code.Count; k++)
            {
                if (code[k].Op == OpCodes.Ldstr && code[k].Str == "BeatmapSetID")
                    return null; // reads it already (2012 on)
            }
            for (int k = 1; k + 2 < code.Count; k++)
            {
                if (code[k].Op != OpCodes.Ldstr || code[k].Str != "Artist" || !IsLoad(code[k - 1]) || !IsStringEquality(code[k + 1]) ||
                    !(code[k + 2].Op == OpCodes.Brtrue || code[k + 2].Op == OpCodes.Brtrue_S))
                    continue;
                int t = code[k + 2].Target;
                if (t < 0 || t + 3 >= code.Count || !IsLoad(code[t]) || !IsLoad(code[t + 1]) || code[t + 2].Op != OpCodes.Stfld ||
                    code[t + 2].Type != "System.String" || !(code[t + 3].Op == OpCodes.Br || code[t + 3].Op == OpCodes.Br_S))
                    continue;
                return new KeyCase { LoadKey = k - 1, LoadBeatmap = t, LoadValue = t + 1, Break = t + 3 };
            }
            return null;
        }

        /// <summary>
        /// osu!direct's "already downloaded" in its result parser (the method reading Convert.ToDateTime):
        /// "= Lookup(setId) != null". The index of the Lookup call, or -1.
        /// </summary>
        public static int DownloadedLookup(IList<Il> code)
        {
            bool parser = false;
            foreach (Il i in code)
            {
                if (IsCall(i) && i.Name == "ToDateTime" && i.Owner == "System.Convert")
                    parser = true;
            }
            if (!parser)
                return -1;
            for (int k = 0; k + 2 < code.Count; k++)
            {
                if (IsCall(code[k]) && code[k].Static && code[k].Params == 1 && code[k + 1].Op == OpCodes.Ldnull &&
                    (code[k + 2].Op == OpCodes.Cgt_Un || code[k + 2].Op == OpCodes.Ceq))
                    return k;
            }
            return -1;
        }

        /// <summary>The Lookup's predicate (its ldftn), or -1.</summary>
        public static int Predicate(IList<Il> code)
        {
            for (int k = 0; k < code.Count; k++)
            {
                if (code[k].Op == OpCodes.Ldftn)
                    return k;
            }
            return -1;
        }

        /// <summary>
        /// The predicate "beatmap.SetId == setId" ([ldarg.1][ldfld setid][ldarg.0][ldfld captured][ceq], or
        /// the other way round): the index of the beatmap's ldfld, or -1.
        /// </summary>
        public static int ComparedField(IList<Il> code)
        {
            for (int k = 0; k + 4 < code.Count; k++)
            {
                if (code[k + 4].Op != OpCodes.Ceq && code[k + 4].Op != OpCodes.Bne_Un && code[k + 4].Op != OpCodes.Bne_Un_S)
                    continue;
                if (code[k].Op == OpCodes.Ldarg_1 && code[k + 1].Op == OpCodes.Ldfld && code[k + 2].Op == OpCodes.Ldarg_0 && code[k + 3].Op == OpCodes.Ldfld)
                    return k + 1;
                if (code[k].Op == OpCodes.Ldarg_0 && code[k + 1].Op == OpCodes.Ldfld && code[k + 2].Op == OpCodes.Ldarg_1 && code[k + 3].Op == OpCodes.Ldfld)
                    return k + 3;
            }
            return -1;
        }

        // ── 6. Screen mode ─────────────────────────────────────────────────────────────────────

        /// <summary>Calls ChangeDisplaySettings (by its P/Invoke entry point; obfuscation renames the method).</summary>
        public static bool ChangesDisplaySettings(IList<Il> code)
        {
            foreach (Il i in code)
            {
                if (IsCall(i) && (i.EntryPoint ?? i.Name) is { } name && name.StartsWith("ChangeDisplaySettings"))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The renderer's resize handler, "if (fullscreen) { Change(w, h); } else { Change(desktop w, h); }":
        /// [call change][br end] ... [call change], the second being the windowed one. Its index, or -1.
        /// (b20121223 to b20130716 resize another way, without it.)
        /// </summary>
        public static int WindowedModeCall(IList<Il> code, string changer)
        {
            int first = -1, calls = 0, second = -1;
            for (int k = 0; k < code.Count; k++)
            {
                if (!IsCall(code[k]) || code[k].Member != changer)
                    continue;
                calls++;
                if (first < 0)
                    first = k;
                else
                    second = k;
            }
            if (calls != 2 || first + 1 >= code.Count || !(code[first + 1].Op == OpCodes.Br || code[first + 1].Op == OpCodes.Br_S))
                return -1;
            return second;
        }

        /// <summary>
        /// The message boxes after a refused screen mode: every MessageBox.Show in the mode changer but
        /// the "reboot" one. The calls' indices (a text that can't be read leaves its box).
        /// </summary>
        public static List<int> ModeMessageBoxes(IList<Il> code)
        {
            var boxes = new List<int>();
            for (int k = 0; k < code.Count; k++)
            {
                if (!IsCall(code[k]) || code[k].Name != "Show" || code[k].Owner != "System.Windows.Forms.MessageBox")
                    continue;
                int text = k - code[k].Params; // each argument is one load
                if (text < 0 || code[text].Str is not { } message || message.IndexOf("Reboot", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                boxes.Add(k);
            }
            return boxes;
        }
    }
}
