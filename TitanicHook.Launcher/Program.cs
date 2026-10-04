// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TitanicHook.Launcher;

/// <summary>
/// osu!somtum's one exe for every build: it reads which .NET osu!.exe is for and starts the loader
/// made for it. A process runs one .NET runtime, and the loader runs osu! inside its own process, so
/// the .NET 2 builds (2008 to early 2015) and the .NET 4 ones (2015 cuttingedge and stable) each get
/// theirs. Both loaders are inside this exe; the one used is written next to osu!.exe (osu! finds its
/// Songs and Skins from where the running exe is) and started there.
/// </summary>
internal static class Program
{
    private static readonly string[] OsuExecutables =
        ["osu!.exe", "osu.exe", "osu!test.exe", "osu!public.exe", "osu!shine1.exe", "osu!cuttingedge.exe"];

    [STAThread]
    private static int Main(string[] args)
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        string? osu = null;
        foreach (string name in OsuExecutables)
        {
            if (File.Exists(Path.Combine(folder, name)))
            {
                osu = Path.Combine(folder, name);
                break;
            }
        }
        if (osu == null)
            return Fail("Couldn't find osu!.exe here. Put osu!somtum.exe in your osu! folder, next to osu!.exe.");

        string? runtime;
        try
        {
            runtime = ClrVersion(osu);
        }
        catch (Exception e)
        {
            return Fail($"Couldn't read {Path.GetFileName(osu)}: {e.Message}");
        }
        if (runtime == null)
            return Fail($"{Path.GetFileName(osu)} isn't a .NET program, so it isn't an osu! this works with.");

        // .NET 4 builds: the .NET 4 loader. .NET 2 builds: the .NET 2 loader, on .NET 2 as osu! ran
        // then, when it's installed (.NET 3.5); else the .NET 4 one, which runs them on .NET 4.
        bool forceNet40 = Array.IndexOf(args, "--net40") >= 0;
        string variant = runtime.StartsWith("v4") || forceNet40 || !Net2Installed() ? "net40" : "net20";
        string loader = Path.Combine(folder, $"osu!somtum.{variant}.exe");
        try
        {
            Write($"{variant}.exe", loader);
            Write($"{variant}.exe.config", loader + ".config");
        }
        catch (Exception e)
        {
            return Fail($"Couldn't write {Path.GetFileName(loader)} in the osu! folder: {e.Message}");
        }

        var arguments = new StringBuilder();
        foreach (string arg in args)
        {
            if (arg == "--net40")
                continue;
            if (arguments.Length > 0)
                arguments.Append(' ');
            arguments.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
        }
        try
        {
            Process.Start(new ProcessStartInfo(loader, arguments.ToString()) { WorkingDirectory = folder, UseShellExecute = false });
        }
        catch (Exception e)
        {
            return Fail($"Couldn't start {Path.GetFileName(loader)}: {e.Message}");
        }
        return 0;
    }

    /// <summary>The .NET runtime a program is for (its metadata's version, "v2.0.50727" or "v4.0.30319"), or null when it isn't .NET.</summary>
    internal static string? ClrVersion(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(file, 0x3C);
        if (pe <= 0 || pe + 24 > file.Length || BitConverter.ToUInt32(file, pe) != 0x00004550)
            return null;
        int sections = BitConverter.ToUInt16(file, pe + 6);
        int optionalSize = BitConverter.ToUInt16(file, pe + 20);
        int optional = pe + 24;
        int directories = optional + (BitConverter.ToUInt16(file, optional) == 0x20B ? 112 : 96);
        uint cliRva = BitConverter.ToUInt32(file, directories + 14 * 8);
        if (cliRva == 0)
            return null;
        int sectionTable = optional + optionalSize;

        int Offset(uint rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = sectionTable + i * 40;
                uint virtualAddress = BitConverter.ToUInt32(file, s + 12);
                uint size = Math.Max(BitConverter.ToUInt32(file, s + 8), BitConverter.ToUInt32(file, s + 16));
                if (rva >= virtualAddress && rva < virtualAddress + size)
                    return (int)(rva - virtualAddress + BitConverter.ToUInt32(file, s + 20));
            }
            return -1;
        }

        int cli = Offset(cliRva);
        if (cli < 0)
            return null;
        int metadata = Offset(BitConverter.ToUInt32(file, cli + 8));
        if (metadata < 0 || BitConverter.ToUInt32(file, metadata) != 0x424A5342) // "BSJB"
            return null;
        int length = BitConverter.ToInt32(file, metadata + 12);
        return Encoding.ASCII.GetString(file, metadata + 16, length).TrimEnd('\0');
    }

    /// <summary>Whether .NET 2 (with 3.0 and 3.5) is installed, which the .NET 2 builds were made for.</summary>
    private static bool Net2Installed()
    {
        foreach (string key in new[] { @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5", @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v2.0.50727" })
        {
            try
            {
                using RegistryKey? ndp = Registry.LocalMachine.OpenSubKey(key);
                if (ndp?.GetValue("Install") is int installed && installed == 1)
                    return true;
            }
            catch (Exception)
            {
            }
        }
        return false;
    }

    /// <summary>Writes a loader file from inside this exe, unless it's there already as it is.</summary>
    private static void Write(string resource, string path)
    {
        using Stream? source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (source == null)
            throw new FileNotFoundException($"{resource} isn't inside this osu!somtum.exe (a Debug build?)");
        var content = new byte[source.Length];
        int read = 0;
        while (read < content.Length)
            read += source.Read(content, read, content.Length - read);

        if (File.Exists(path) && SameBytes(File.ReadAllBytes(path), content))
            return;
        if (File.Exists(path))
            File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, content);
        File.SetAttributes(path, FileAttributes.Hidden);
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private static int Fail(string message)
    {
        MessageBox.Show(message, "osu!somtum", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }
}
