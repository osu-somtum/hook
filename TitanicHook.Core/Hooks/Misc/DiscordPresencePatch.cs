// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using ClrTest.Reflection;
using Harmony;
using TitanicHook.Core.Compat;
using TitanicHook.Core.Framework;
using TitanicHook.Core.Helpers;
using TitanicHook.Core.OsuInterop;

namespace TitanicHook.Core.Hooks.Misc;

/// <summary>
/// Discord Rich Presence: what the player is doing, as bancho is told. The status packet's constructor
/// (bStatusUpdate: the status, then the beatmap's text and checksum, mods and, from b476, the mode and,
/// from b497, the beatmap id) is found by its parameters, and a postfix reads what it was given.
/// Builds the somtum patcher made already have their own presence (Somtum.Discord): nothing is done there.
/// </summary>
public class DiscordPresencePatch : TitanicPatch
{
    public const string HookName = "sh.Somtum.Hook.DiscordPresence";

    /// <summary>osu!somtum's Discord application ("osu!somtum").</summary>
    public const string ApplicationId = "1509236899632517321";

    private static FieldInfo? _status, _text, _checksum, _mods, _mode, _beatmapId;
    private static bool _firstEra;
    private static bool _taikoMod; // no mode yet (before b476): the Taiko mod (512) is the mode
    private static FieldInfo? _username;

    private static readonly object Lock = new();
    private static int _beatmapIdValue;
    private static int _modeValue;
    private static long _since;

    /// <summary>The build was patched by the somtum patcher, which has its own presence.</summary>
    public static bool BuildHasOwnPresence { get; private set; }

    public DiscordPresencePatch() : base(HookName)
    {
        BuildHasOwnPresence = HasOwnPresence();
        if (BuildHasOwnPresence)
        {
            Logging.HookStep(HookName, "The build has its own Discord presence (Somtum.Discord)");
            return;
        }
        foreach (Type type in StatusTypes())
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
                if (TargetConstructors.Count == 0 && IsStatusConstructor(constructor) && ReadFields(constructor))
                    TargetConstructors.Add(constructor);
            }
        }
        _username = UsernameField();
        Logging.HookStep(HookName, TargetConstructors.Count == 0 ? "No status packet found" :
            $"Status packet {TargetConstructors[0].DeclaringType?.FullName}, username {(_username == null ? "from osu!.cfg" : _username.Name)}");
        Postfixes = [AccessTools.Method(typeof(DiscordPresencePatch), nameof(StatusPostfix))];
    }

    /// <summary>Starts the presence (the game is idle until it says otherwise).</summary>
    public static void Start()
    {
        _since = Now();
        DiscordIpc.Start(ApplicationId, Activity);
    }

    #region Find

    /// <summary>The somtum patcher's own presence (Somtum.Discord, or a Somtum.Discord* type).</summary>
    private static bool HasOwnPresence()
    {
        foreach (Type type in AssemblyUtils.OsuTypes)
        {
            try
            {
                string name = type.FullName ?? "";
                if (name == "Somtum.Discord" || name.StartsWith("Somtum.Discord"))
                    return true;
            }
            catch (Exception)
            {
            }
        }
        return false;
    }

    /// <summary>osu!'s types, and osu!common's (b337 to b504, b1652, b1700, b20130303, b20130606.1).</summary>
    private static IEnumerable<Type> StatusTypes()
    {
        foreach (Type type in AssemblyUtils.OsuTypes)
            yield return type;
        Type[] common = [];
        try
        {
            foreach (AssemblyName reference in AssemblyUtils.OsuAssembly.GetReferencedAssemblies())
            {
                if (reference.Name == "osu!common")
                {
                    Assembly assembly = Assembly.Load(reference);
                    try
                    {
                        common = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        common = Array.FindAll(e.Types, t => t != null);
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        foreach (Type type in common)
            yield return type;
    }

    /// <summary>
    /// (bStatus status, [bool beatmapUpdate,] string text, string checksum, [int beatmapId,] Mods mods[, PlayModes mode]):
    /// the status first, two strings, at least one more enum, nothing else but a bool and an int.
    /// </summary>
    private static bool IsStatusConstructor(ConstructorInfo constructor)
    {
        try
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            if (parameters.Length < 4 || parameters.Length > 7 || !parameters[0].ParameterType.IsEnum)
                return false;
            int strings = 0, enums = 0;
            foreach (ParameterInfo parameter in parameters)
            {
                Type type = parameter.ParameterType;
                if (type == typeof(string))
                    strings++;
                else if (type.IsEnum)
                    enums++;
                else if (type != typeof(bool) && type != typeof(int))
                    return false;
            }
            return strings == 2 && enums >= 2 && constructor.GetMethodBody() != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Which field each parameter is kept in ([ldarg.0][ldarg n][stfld]), by the constructor's IL.</summary>
    private static bool ReadFields(ConstructorInfo constructor)
    {
        ParameterInfo[] parameters = constructor.GetParameters();
        var fields = new FieldInfo?[parameters.Length];
        try
        {
            int arg = -1;
            foreach (ILInstruction instruction in new ILReader(constructor))
            {
                OpCode op = instruction.OpCode;
                if (op == OpCodes.Nop || op == OpCodes.Br || op == OpCodes.Br_S)
                    continue; // control flow obfuscation puts a br after every instruction
                int? loaded = op == OpCodes.Ldarg_1 ? 1 : op == OpCodes.Ldarg_2 ? 2 : op == OpCodes.Ldarg_3 ? 3
                    : instruction is ShortInlineVarInstruction s && op == OpCodes.Ldarg_S ? s.Ordinal
                    : instruction is InlineVarInstruction v && op == OpCodes.Ldarg ? v.Ordinal : null;
                if (loaded != null)
                {
                    arg = loaded.Value;
                    continue;
                }
                if (op == OpCodes.Stfld && instruction is InlineFieldInstruction store && arg >= 1 && arg <= parameters.Length &&
                    store.Field.DeclaringType == constructor.DeclaringType && store.Field.FieldType == parameters[arg - 1].ParameterType)
                    fields[arg - 1] = store.Field;
                arg = -1;
            }
        }
        catch (Exception)
        {
            return false;
        }

        FieldInfo? status = fields[0], text = null, checksum = null, mods = null, mode = null, beatmapId = null;
        bool hasBool = false;
        for (int i = 1; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (type == typeof(bool))
                hasBool = true;
            else if (type == typeof(string))
            {
                if (text == null && checksum == null && i == FirstString(parameters))
                    text = fields[i];
                else
                    checksum = fields[i];
            }
            else if (type == typeof(int))
                beatmapId = fields[i];
            else if (type.IsEnum)
            {
                // The mode is 0-3 (osu!, taiko, catch, mania); the mods are flags up to 512 and more.
                if (MaxValue(type) <= 3)
                    mode = fields[i];
                else
                    mods = fields[i];
            }
        }
        if (status == null || text == null)
            return false;
        _status = status;
        _text = text;
        _checksum = checksum;
        _mods = mods;
        _mode = mode;
        _beatmapId = beatmapId;
        _firstEra = !hasBool; // b337: no beatmapUpdate flag, and its own status numbers
        _taikoMod = mode == null;
        return true;
    }

    private static int FirstString(ParameterInfo[] parameters)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(string))
                return i;
        }
        return -1;
    }

    private static long MaxValue(Type enumType)
    {
        long max = 0;
        foreach (object value in Enum.GetValues(enumType))
            max = Math.Max(max, Convert.ToInt64(value));
        return max;
    }

    /// <summary>
    /// The config's username: the static field set right after the config reader's "Username" string
    /// (a string, or in 2015 a bindable of one).
    /// </summary>
    private static FieldInfo? UsernameField()
    {
        foreach (MethodInfo method in ShapeCode.OsuMethods)
        {
            if (!method.IsStatic || ShapeCode.Of(method) is not { } plain || !plain.Exists(il => il.Str == "Username") ||
                ShapeCode.Shaped(method, c => UsernameStore(c) != null) is not { } code ||
                UsernameStore(code) is not { } field)
                continue;
            return field;
        }
        return null;
    }

    private static FieldInfo? UsernameStore(List<Il> code)
    {
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Str != "Username")
                continue;
            for (int j = i + 1; j < code.Count && j <= i + 8; j++)
            {
                if (!string.IsNullOrEmpty(code[j].Str))
                    break; // another key: the "" default doesn't count
                if (code[j].Op == OpCodes.Stsfld && code[j].Operand is FieldInfo field &&
                    (field.FieldType == typeof(string) || IsBindableString(field.FieldType)))
                    return field;
            }
        }
        return null;
    }

    private static bool IsBindableString(Type type)
    {
        try
        {
            return type.IsGenericType && type.GetGenericArguments().Length == 1 && type.GetGenericArguments()[0] == typeof(string) &&
                   StringValue(type) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static PropertyInfo? StringValue(Type type)
    {
        PropertyInfo? found = null;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.PropertyType != typeof(string) || property.GetIndexParameters().Length > 0 || property.GetGetMethod(true) == null)
                continue;
            if (property.Name == "Value")
                return property;
            found ??= property;
        }
        return found;
    }

    #endregion

    #region Hook

    // Today's bStatus numbers (b337's are converted).
    private const int Idle = 0, Afk = 1, Playing = 2, Editing = 3, Modding = 4, Multiplayer = 5, Watching = 6, Unknown = 7,
        Testing = 8, Submitting = 9, Paused = 10, Lobby = 11, Multiplaying = 12, OsuDirect = 13;

    /// <summary>The game's thread: only reads the packet and works out the two lines (no I/O).</summary>
    private static void StatusPostfix(object __instance)
    {
        try
        {
            int status = Convert.ToInt32(_status!.GetValue(__instance));
            if (_firstEra)
            {
                if (status == 10)
                    return; // b337's "stats changed", not a status
                if (status == 11 || status == 12)
                    status--; // Paused, Lobby
            }
            if (status == Unknown || status < 0 || status > OsuDirect)
                return;
            string text = (_text?.GetValue(__instance) as string ?? "").Trim();
            string checksum = _checksum?.GetValue(__instance) as string ?? "";
            // The two strings the other way round in some build: the checksum is 32 hex digits.
            if (IsChecksum(text) && checksum.Length > 0 && !IsChecksum(checksum))
                text = checksum.Trim();
            int mods = _mods == null ? 0 : Convert.ToInt32(_mods.GetValue(__instance));
            int mode = _mode == null ? (_taikoMod && (mods & 512) != 0 ? 1 : 0) : Convert.ToInt32(_mode.GetValue(__instance));
            int beatmapId = _beatmapId == null ? 0 : Convert.ToInt32(_beatmapId.GetValue(__instance));
            if (_taikoMod)
                mods &= ~512;

            lock (Lock)
            {
                // The same lines as the somtum patcher's presence (Somtum/Discord.cs), without its screen names.
                bool mapStatus = status is Playing or Multiplaying or Paused or Testing or Editing or Modding or Submitting;
                // A status set directly (the editor's, the one back from AFK) comes without the map: keep the last one.
                if (text.Length == 0 && mapStatus)
                {
                    text = _lastMap;
                    if (beatmapId == 0)
                        beatmapId = _lastMapId;
                }
                else if (text.Length > 0 && mapStatus)
                {
                    _lastMap = text;
                    _lastMapId = beatmapId;
                }

                string details;
                string state = "";
                bool showMap = true;
                string modText = ModText(mods);
                switch (status)
                {
                    case Playing:
                        details = Join("Playing", text);
                        state = modText;
                        break;
                    case Multiplaying:
                        details = Join("Playing", text);
                        state = Join("Multiplayer", modText);
                        break;
                    case Paused:
                        details = Join("Playing", text);
                        state = "Paused";
                        break;
                    case Testing:
                        details = Join("Testing", text);
                        state = "In the editor";
                        break;
                    case Editing:
                        details = Join("Editing", text);
                        break;
                    case Modding:
                        details = Join("Modding", text);
                        break;
                    case Submitting:
                        details = Join("Submitting", text);
                        break;
                    case Watching:
                        // The game says "Player play Artist - Title [Diff]"; "osu!" is its autoplay.
                        int play = text.IndexOf(" play ", StringComparison.Ordinal);
                        if (play > 0)
                        {
                            string player = text.Substring(0, play).Trim();
                            details = player == "osu!" ? "Watching autoplay" : "Watching " + player + "'s replay";
                            state = text.Substring(play + 6).Trim();
                        }
                        else
                        {
                            details = "Watching a replay";
                            state = text;
                        }
                        break;
                    case Multiplayer when text.Length > 0:
                        // b337 says Multiplayer during a multiplayer play too, with the map.
                        details = Join("Playing", text);
                        state = Join("Multiplayer", modText);
                        break;
                    case Multiplayer:
                        details = "Multiplayer";
                        state = "In a lobby";
                        showMap = false;
                        break;
                    case Lobby:
                        details = "Multiplayer";
                        state = "In the lobby";
                        showMap = false;
                        break;
                    case OsuDirect:
                        details = "Browsing osu!direct";
                        showMap = false;
                        break;
                    case Afk:
                        details = "AFK";
                        showMap = false;
                        break;
                    default:
                        details = "Idle";
                        showMap = false;
                        break;
                }

                // The timer starts again when the activity changes (not when only the mods do).
                if (details != _details)
                    _since = Now();
                _details = details;
                _stateText = state;
                _modeValue = mode is >= 0 and <= 3 ? mode : 0;
                _beatmapIdValue = showMap ? beatmapId : 0;
            }
            DiscordIpc.Update();
        }
        catch (Exception e)
        {
            Logging.HookError(HookName, e.Message, false);
        }
    }

    private static string Join(string first, string second) => second.Length == 0 ? first : first + " " + second;

    #endregion

    #region Presence

    private static string _details = "Idle";
    private static string _stateText = "";
    private static string _lastMap = "";
    private static int _lastMapId;
    private static string? _build;

    /// <summary>What Discord shows now (asked on the presence thread).</summary>
    private static DiscordActivity Activity()
    {
        string details, state;
        int mode, beatmapId;
        long since;
        lock (Lock)
        {
            details = _details;
            state = _stateText;
            beatmapId = _beatmapIdValue;
            mode = _modeValue;
            since = _since;
        }

        if (_build == null)
        {
            try
            {
                _build = OsuVersion.GetVersion() ?? "";
            }
            catch (Exception)
            {
                _build = "";
            }
        }

        string website = "https://osu." + EntryPoint.Config.ServerName;
        var activity = new DiscordActivity
        {
            Details = details,
            // The build goes on the state line too (not only in the logo's tooltip), so it shows at a glance.
            State = _build.Length == 0 ? state : state.Trim().Length > 0 ? state.Trim() + " \u00b7 " + _build : _build,
            StartTimestamp = since,
            LargeImage = "osu_logo_stable",
            LargeText = _build.Length > 0 ? "osu!somtum (" + _build + ")" : "osu!somtum",
            SmallImage = "mode_" + mode,
            SmallText = ModeName(mode),
        };

        var labels = new List<string>();
        var urls = new List<string>();
        string username = Username();
        if (username.Length > 0)
        {
            // By name ("@": a name made of digits isn't taken for an id); the website redirects to /users/<id>.
            labels.Add("Profile");
            urls.Add(website + "/users/@" + Uri.EscapeDataString(username));
        }
        if (beatmapId > 0)
        {
            labels.Add("Beatmap");
            urls.Add(website + "/b/" + beatmapId);
        }
        activity.ButtonLabels = labels.ToArray();
        activity.ButtonUrls = urls.ToArray();
        return activity;
    }

    private static string ModeName(int mode) => mode switch
    {
        1 => "osu!taiko",
        2 => "osu!catch",
        3 => "osu!mania",
        _ => "osu!",
    };

    // The somtum patcher's mod names, in bit order, then mania's keys.
    private static readonly int[] ModBits = [1, 2, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 1048576, 2097152, 33554432];
    private static readonly string[] ModNames = ["NF", "EZ", "HD", "HR", "SD", "DT", "RX", "HT", "NC", "FL", "Auto", "SO", "AP", "PF", "FI", "RD", "Coop"];
    private static readonly int[] KeyBits = [67108864, 268435456, 134217728, 32768, 65536, 131072, 262144, 524288, 16777216];

    private static string ModText(int mods)
    {
        if (mods <= 0)
            return "";
        if ((mods & 512) != 0)
            mods &= ~64; // Nightcore is shown instead of Double Time
        if ((mods & 16384) != 0)
            mods &= ~32; // Perfect instead of Sudden Death
        var text = new StringBuilder();
        for (int i = 0; i < ModBits.Length; i++)
        {
            if ((mods & ModBits[i]) != 0)
                text.Append(ModNames[i]);
        }
        for (int i = 0; i < KeyBits.Length; i++)
        {
            if ((mods & KeyBits[i]) != 0)
                text.Append(i + 1).Append('K');
        }
        return text.Length == 0 ? "" : "+" + text;
    }

    private static string Username()
    {
        try
        {
            if (_username != null)
            {
                object? value = _username.GetValue(null);
                string? name = value as string;
                if (name == null && value != null && StringValue(value.GetType()) is { } property)
                    name = property.GetValue(value, null) as string;
                if (name != null)
                    return name.Trim();
            }
        }
        catch (Exception)
        {
        }
        return ConfigUsername();
    }

    private static string _configUsername = "";
    private static DateTime _configRead = DateTime.MinValue;

    /// <summary>"Username = ..." in osu!.&lt;Windows user&gt;.cfg (2010 on) or osu!.cfg.</summary>
    private static string ConfigUsername()
    {
        // Read again now and then until found (osu! writes it on exit, after the first login).
        if (_configUsername.Length > 0 || DateTime.UtcNow < _configRead.AddMinutes(1))
            return _configUsername;
        _configRead = DateTime.UtcNow;
        try
        {
            string folder = Path.GetDirectoryName(AssemblyUtils.OsuAssembly.Location) ?? "";
            foreach (string file in new[] { "osu!." + Environment.UserName + ".cfg", "osu!.cfg" })
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path))
                    continue;
                foreach (string line in File.ReadAllLines(path))
                {
                    int equals = line.IndexOf('=');
                    if (equals > 0 && line.Substring(0, equals).Trim() == "Username")
                        return _configUsername = line.Substring(equals + 1).Trim();
                }
            }
        }
        catch (Exception)
        {
        }
        return _configUsername;
    }

    private static bool IsChecksum(string value)
    {
        if (value.Length != 32)
            return false;
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    private static long Now() => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    #endregion
}
