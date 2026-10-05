// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using ClrTest.Reflection;
using Harmony;
using TitanicHook.Core.Compat;

namespace TitanicHook.Core.Helpers;

/// <summary>
/// osu!somtum: a method's instructions as <see cref="OldClientShapes"/> reads them, from the method
/// itself (to find what to patch) or from Harmony's instructions (to patch it).
/// </summary>
public static class ShapeCode
{
    private static List<MethodInfo>? _osuMethods;
    private static List<ConstructorInfo>? _osuConstructors;
    private static readonly Dictionary<MethodBase, List<Il>?> Raw = new();
    private static readonly Dictionary<MethodBase, List<Il>?> Readable = new();
    private static readonly Dictionary<MethodBase, List<Il>?> FlowedCache = new();

    /// <summary>Every method of osu!'s own types that has a body.</summary>
    public static List<MethodInfo> OsuMethods
    {
        get
        {
            if (_osuMethods != null)
                return _osuMethods;
            _osuMethods = [];
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
                        if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null)
                            _osuMethods.Add(method);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return _osuMethods;
        }
    }

    /// <summary>Every instance constructor of osu!'s own types (the HUD parts set themselves up there).</summary>
    public static List<ConstructorInfo> OsuConstructors
    {
        get
        {
            if (_osuConstructors != null)
                return _osuConstructors;
            _osuConstructors = [];
            foreach (Type type in AssemblyUtils.OsuTypes)
            {
                ConstructorInfo[] constructors;
                try
                {
                    constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (ConstructorInfo constructor in constructors)
                {
                    try
                    {
                        if (!constructor.ContainsGenericParameters && constructor.GetMethodBody() != null)
                            _osuConstructors.Add(constructor);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return _osuConstructors;
        }
    }

    /// <summary>Lets go of the methods and instructions read (once every patch has found its targets).</summary>
    public static void Forget()
    {
        _osuMethods = null;
        _osuConstructors = null;
        Raw.Clear();
        Readable.Clear();
        FlowedCache.Clear();
    }

    /// <summary>
    /// The method's instructions as found, with encrypted strings read as the text they are, or null
    /// when they can't be read.
    /// </summary>
    public static List<Il>? Of(MethodBase method)
    {
        if (!Readable.TryGetValue(method, out List<Il>? code))
        {
            code = RawOf(method) is { } raw ? Decrypted(raw) : null;
            Readable[method] = code;
        }
        return code;
    }

    /// <summary>The same in the order they run (for control flow obfuscation, <see cref="OldClientShapes.Flow"/>).</summary>
    public static List<Il>? Flowed(MethodBase method)
    {
        if (!FlowedCache.TryGetValue(method, out List<Il>? code))
        {
            code = RawOf(method) is { } raw ? Decrypted(OldClientShapes.Flow(raw)) : null;
            FlowedCache[method] = code;
        }
        return code;
    }

    /// <summary>
    /// The method's instructions in the form <paramref name="found"/> finds its shape in: as they
    /// are, else in the order they run (exes with control flow obfuscation); null when neither.
    /// </summary>
    public static List<Il>? Shaped(MethodBase method, Func<List<Il>, bool> found)
    {
        if (Of(method) is not { } code)
            return null;
        if (found(code))
            return code;
        return Flowed(method) is { } flowed && found(flowed) ? flowed : null;
    }

    /// <summary>Harmony's instructions (in a transpiler) the same way: each <see cref="Il.At"/> is its index there.</summary>
    public static List<Il>? Shaped(List<CodeInstruction> instructions, Func<List<Il>, bool> found)
    {
        List<Il> raw = RawOf(instructions);
        List<Il> code = Decrypted(raw);
        if (found(code))
            return code;
        List<Il> flowed = Decrypted(OldClientShapes.Flow(raw));
        return found(flowed) ? flowed : null;
    }

    private static List<Il>? RawOf(MethodBase method)
    {
        if (Raw.TryGetValue(method, out List<Il>? cached))
            return cached;
        List<Il>? code;
        try
        {
            ILInstruction[] instructions = new ILReader(method).ToArray();
            var index = new Dictionary<int, int>();
            for (int i = 0; i < instructions.Length; i++)
                index[instructions[i].Offset] = i;
            code = [];
            foreach (ILInstruction instruction in instructions)
            {
                object? operand = instruction switch
                {
                    InlineIInstruction i => i.Int32,
                    ShortInlineIInstruction i => (int)(sbyte)i.Byte,
                    InlineFieldInstruction f => f.Field,
                    InlineMethodInstruction m => m.Method,
                    InlineStringInstruction s => s.String,
                    _ => null
                };
                Il il = Describe(instruction.OpCode, operand);
                il.At = code.Count;
                if (instruction is InlineBrTargetInstruction br && index.TryGetValue(br.TargetOffset, out int target))
                    il.Target = target;
                else if (instruction is ShortInlineBrTargetInstruction sbr && index.TryGetValue(sbr.TargetOffset, out int shortTarget))
                    il.Target = shortTarget;
                code.Add(il);
            }
        }
        catch (Exception)
        {
            code = null;
        }
        Raw[method] = code;
        return code;
    }

    private static List<Il> RawOf(List<CodeInstruction> instructions)
    {
        var labels = new Dictionary<Label, int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            foreach (Label label in instructions[i].labels)
                labels[label] = i;
        }
        var code = new List<Il>(instructions.Count);
        foreach (CodeInstruction instruction in instructions)
        {
            Il il = Describe(instruction.opcode, instruction.operand);
            il.At = code.Count;
            if (instruction.operand is Label label && labels.TryGetValue(label, out int target))
                il.Target = target;
            code.Add(il);
        }
        return code;
    }

    /// <summary>
    /// Encrypted strings ([ldc id][call decrypt]) as one string load with the text, so the shapes read
    /// them like ldstr (the load's <see cref="Il.At"/> is the decrypt call's).
    /// </summary>
    private static List<Il> Decrypted(List<Il> code)
    {
        int token;
        try
        {
            if (!ObfHelper.HasStringDecrypt)
                return code;
            token = ObfHelper.StringObfToken;
        }
        catch (Exception)
        {
            return code;
        }
        bool any = false;
        for (int i = 0; i + 1 < code.Count && !any; i++)
            any = code[i].Int != null && IsDecrypt(code[i + 1], token);
        if (!any)
            return code;

        var index = new int[code.Count];
        var kept = new List<Il>(code.Count);
        for (int i = 0; i < code.Count; i++)
        {
            if (i + 1 < code.Count && code[i].Int is { } id && IsDecrypt(code[i + 1], token))
            {
                Il text = code[i + 1].Copy();
                text.Op = OpCodes.Ldstr;
                text.Str = SafeDecrypt(id);
                index[i] = index[i + 1] = kept.Count;
                kept.Add(text);
                i++;
                continue;
            }
            index[i] = kept.Count;
            kept.Add(code[i].Copy());
        }
        foreach (Il il in kept)
        {
            if (il.Target >= 0 && il.Target < index.Length)
                il.Target = index[il.Target];
        }
        return kept;
    }

    private static bool IsDecrypt(Il il, int token) =>
        (il.Op == OpCodes.Call || il.Op == OpCodes.Callvirt) && il.Operand is MethodBase method && method.MetadataToken == token &&
        il.Params == 1 && il.Type == "System.String";

    private static string? SafeDecrypt(int id)
    {
        try
        {
            return ObfHelper.DecString(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A method's identity, as its calls' <see cref="Il.Member"/>.</summary>
    public static string MemberOf(MethodBase method) => Name(method.DeclaringType) + "::" + method.Name + "#" + method.MetadataToken;

    private static Il Describe(OpCode op, object? operand)
    {
        var il = new Il { Op = op, Operand = operand, Int = Constant(op, operand) };
        switch (operand)
        {
            case FieldInfo field:
                il.Member = Name(field.DeclaringType) + "::" + field.Name;
                il.Name = field.Name;
                il.Owner = Name(field.DeclaringType);
                il.Type = Name(field.FieldType);
                il.Static = field.IsStatic;
                break;
            case MethodBase method:
                il.Member = MemberOf(method);
                il.Name = method.Name;
                il.Owner = Name(method.DeclaringType);
                il.Type = method is MethodInfo info ? Name(info.ReturnType) : "System.Void";
                il.Params = method.GetParameters().Length;
                il.Static = method.IsStatic;
                il.EntryPoint = EntryPoint(method);
                break;
            case string text:
                il.Str = text;
                break;
        }
        return il;
    }

    private static int? Constant(OpCode op, object? operand)
    {
        if (op == OpCodes.Ldc_I4_M1) return -1;
        if (op == OpCodes.Ldc_I4_0) return 0;
        if (op == OpCodes.Ldc_I4_1) return 1;
        if (op == OpCodes.Ldc_I4_2) return 2;
        if (op == OpCodes.Ldc_I4_3) return 3;
        if (op == OpCodes.Ldc_I4_4) return 4;
        if (op == OpCodes.Ldc_I4_5) return 5;
        if (op == OpCodes.Ldc_I4_6) return 6;
        if (op == OpCodes.Ldc_I4_7) return 7;
        if (op == OpCodes.Ldc_I4_8) return 8;
        if (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S)
        {
            return operand switch
            {
                int i => i,
                sbyte s => s,
                byte b => (sbyte)b,
                _ => null
            };
        }
        return null;
    }

    private static string Name(Type? type) => type == null ? "" : type.FullName ?? type.ToString();

    private static string? EntryPoint(MethodBase method)
    {
        if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
            return null;
        try
        {
            object[] attributes = method.GetCustomAttributes(typeof(DllImportAttribute), false);
            if (attributes.Length > 0 && attributes[0] is DllImportAttribute import && !string.IsNullOrEmpty(import.EntryPoint))
                return import.EntryPoint;
        }
        catch (Exception)
        {
        }
        return method.Name;
    }
}
