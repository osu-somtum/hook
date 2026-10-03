// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 osu!somtum
//
// osu!somtum: today's beatmaps for an old osu! client. BeatmapCompatPatch makes the client's beatmap
// loaders read their .osu/.osb lines through ReadLine below, which rewrites what the client can't
// read and leaves everything else as it is. (The same code is patched into the decompiled clients.)
//
// - Difficulty: decimal HP/CS/OD/AR (2014 on) rounded to whole numbers (byte.Parse failed on them).
// - Hit objects: the combo colour skip bits (16-64, 2014 on) removed from the type; perfect-circle
//   sliders ("P", 2013 on) given as Bezier pieces along their arc, joined by red anchors.
// - Timing points: only the kiai bit of the effects (8, omit first barline, read as "not kiai").
// - Storyboards: decimal positions rounded; the Overlay layer (2015 on) drawn as Foreground.
// - osu!mania (2012 on) in builds without it: the difficulty loads as an empty osu! map named
//   "... (mania)", rather than failing to load.

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TitanicHook.Core.Compat
{
    public static class BeatmapCompat
    {
        /// <summary>This build reads HP/CS/OD/AR as whole numbers.</summary>
        public static bool RoundDifficulty;

        /// <summary>This build has no perfect-circle sliders.</summary>
        public static bool ConvertPerfectCurves;

        /// <summary>This build has no Overlay storyboard layer.</summary>
        public static bool OverlayToForeground = true;

        /// <summary>This build has no osu!mania.</summary>
        public static bool ManiaUnsupported;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // The file being read and the [Section] it's in. Per thread: each loader reads a file top to bottom
        // on one thread; another reader is another file.
        [ThreadStatic]
        private static TextReader current;

        [ThreadStatic]
        private static string section;

        // The current file is an osu!mania difficulty this build can't play.
        [ThreadStatic]
        private static bool mania;

        public static string ReadLine(TextReader reader)
        {
            if (!ReferenceEquals(reader, current))
            {
                current = reader;
                section = null;
                mania = false;
            }
            if (mania && section == "[HitObjects]")
            {
                return null; // an osu!mania difficulty: no hit objects
            }
            string line = reader.ReadLine();
            if (line == null)
            {
                return null;
            }
            try
            {
                return Line(line);
            }
            catch (Exception)
            {
                // Never break loading over a rewrite: the line as it was.
                return line;
            }
        }

        public static string Line(string line)
        {
            if (line.Length == 0 || line.StartsWith("//"))
            {
                return line;
            }
            if (line.StartsWith("osu file format"))
            {
                section = null;
                mania = false;
                return line;
            }
            if (line[0] == '[')
            {
                section = line.Trim();
                return line;
            }
            switch (section)
            {
                case "[General]":
                    if (ManiaUnsupported && line.StartsWith("Mode") && line.Substring(line.IndexOf(':') + 1).Trim() == "3")
                    {
                        mania = true;
                        return line.Substring(0, line.IndexOf(':') + 1) + " 0";
                    }
                    return line;

                case "[Metadata]":
                    return mania && line.StartsWith("Version:") ? line + " (mania)" : line;

                case "[Difficulty]":
                    return RoundDifficulty && IsDifficulty(line) ? Difficulty(line) : line;

                case "[Events]":
                    if (line[0] == ' ' || line[0] == '_')
                    {
                        return StoryboardCommand(line);
                    }
                    if (line.StartsWith("Sprite,") || line.StartsWith("Animation,"))
                    {
                        return StoryboardObject(line);
                    }
                    return line;

                case "[TimingPoints]":
                    return TimingPoint(line);

                case "[HitObjects]":
                    return HitObject(line);

                default:
                    return line;
            }
        }

        private static bool IsDifficulty(string line)
        {
            return line.StartsWith("HPDrainRate") || line.StartsWith("CircleSize")
                || line.StartsWith("OverallDifficulty") || line.StartsWith("ApproachRate");
        }

        private static string Difficulty(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                return line;
            }
            string value = line.Substring(colon + 1).Trim();
            double number;
            if (value.IndexOf('.') < 0 || !double.TryParse(value, NumberStyles.Float, Invariant, out number))
            {
                return line;
            }
            int rounded = (int)Math.Round(Math.Max(0.0, Math.Min(10.0, number)), MidpointRounding.AwayFromZero);
            return line.Substring(0, colon + 1) + rounded.ToString(Invariant);
        }

        // " M,0,1000,2000,320,124.2182" and nested ("__M,..."): positions rounded.
        private static string StoryboardCommand(string line)
        {
            int start = 0;
            while (start < line.Length && (line[start] == ' ' || line[start] == '_'))
            {
                start++;
            }
            string[] fields = line.Substring(start).Split(',');
            string command = fields[0];
            if (command != "M" && command != "MX" && command != "MY")
            {
                return line;
            }
            bool changed = false;
            for (int i = 4; i < fields.Length; i++)
            {
                string rounded = RoundNumber(fields[i]);
                if (rounded != fields[i])
                {
                    fields[i] = rounded;
                    changed = true;
                }
            }
            return changed ? line.Substring(0, start) + string.Join(",", fields) : line;
        }

        // Sprite,Layer,Origin,"file",x,y and Animation,Layer,Origin,"file",x,y,frames,delay[,loop]
        private static string StoryboardObject(string line)
        {
            string[] fields = line.Split(',');
            if (fields.Length < 6)
            {
                return line;
            }
            bool changed = false;
            if (OverlayToForeground && fields[1] == "Overlay")
            {
                fields[1] = "Foreground";
                changed = true;
            }
            for (int i = 4; i <= 5; i++)
            {
                string rounded = RoundNumber(fields[i]);
                if (rounded != fields[i])
                {
                    fields[i] = rounded;
                    changed = true;
                }
            }
            return changed ? string.Join(",", fields) : line;
        }

        // time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects
        private static string TimingPoint(string line)
        {
            string[] fields = line.Split(',');
            int effects;
            if (fields.Length == 8 && int.TryParse(fields[7], NumberStyles.Integer, Invariant, out effects) && effects > 1)
            {
                fields[7] = (effects & 1).ToString(Invariant);
                return string.Join(",", fields);
            }
            return line;
        }

        // x,y,time,type,hitSound,objectParams...,hitSample
        private static string HitObject(string line)
        {
            string[] fields = line.Split(',');
            int type;
            if (fields.Length < 5 || !int.TryParse(fields[3], NumberStyles.Integer, Invariant, out type) || !IsNumber(fields[0]) || !IsNumber(fields[1]) || !IsNumber(fields[2]))
            {
                return line;
            }
            bool changed = false;
            if ((type & 0x70) != 0)
            {
                fields[3] = (type & ~0x70).ToString(Invariant);
                changed = true;
            }
            if (ConvertPerfectCurves && (type & 2) != 0 && fields.Length > 5 && fields[5].StartsWith("P|"))
            {
                fields[5] = PerfectCurve(fields[0], fields[1], fields[5]);
                changed = true;
            }
            return changed ? string.Join(",", fields) : line;
        }

        private static bool IsNumber(string text)
        {
            double number;
            return double.TryParse(text, NumberStyles.Float, Invariant, out number);
        }

        private static string RoundNumber(string text)
        {
            double number;
            if (text.IndexOf('.') < 0 || !double.TryParse(text, NumberStyles.Float, Invariant, out number))
            {
                return text;
            }
            return ((long)Math.Round(number, MidpointRounding.AwayFromZero)).ToString(Invariant);
        }

        // "P|x1:y1|x2:y2" from (x0,y0): the arc through the three points, as cubic Bezier pieces of at
        // most a quarter turn joined by a repeated point. Not a circle (in a line): the points as lines.
        private static string PerfectCurve(string startX, string startY, string curve)
        {
            string[] parts = curve.Split('|');
            if (parts.Length != 3)
            {
                return "B" + curve.Substring(1);
            }
            double ax = double.Parse(startX, Invariant), ay = double.Parse(startY, Invariant);
            string[] middle = parts[1].Split(':');
            string[] end = parts[2].Split(':');
            double bx = double.Parse(middle[0], Invariant), by = double.Parse(middle[1], Invariant);
            double cx = double.Parse(end[0], Invariant), cy = double.Parse(end[1], Invariant);

            double d = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 0.001)
            {
                return "L|" + parts[1] + "|" + parts[2];
            }
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ox = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            double oy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
            double radius = Math.Sqrt((ax - ox) * (ax - ox) + (ay - oy) * (ay - oy));
            double startAngle = Math.Atan2(ay - oy, ax - ox);
            double endAngle = Math.Atan2(cy - oy, cx - ox);
            // The way that passes the middle point: by which side of start-end it's on.
            bool clockwise = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax) < 0;
            double sweep = endAngle - startAngle;
            if (clockwise)
            {
                while (sweep > 0)
                {
                    sweep -= 2.0 * Math.PI;
                }
            }
            else
            {
                while (sweep < 0)
                {
                    sweep += 2.0 * Math.PI;
                }
            }

            int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2.0)));
            double step = sweep / pieces;
            // How far the control points sit from the ends for a cubic Bezier on a circle.
            double k = 4.0 / 3.0 * Math.Tan(step / 4.0) * radius;
            StringBuilder result = new StringBuilder("B");
            for (int i = 0; i < pieces; i++)
            {
                double a0 = startAngle + step * i;
                double a1 = a0 + step;
                double x0 = ox + radius * Math.Cos(a0), y0 = oy + radius * Math.Sin(a0);
                double x1 = ox + radius * Math.Cos(a1), y1 = oy + radius * Math.Sin(a1);
                if (i > 0)
                {
                    Point(result, x0, y0); // the red anchor: where the previous piece ended
                }
                Point(result, x0 - k * Math.Sin(a0), y0 + k * Math.Cos(a0));
                Point(result, x1 + k * Math.Sin(a1), y1 - k * Math.Cos(a1));
                if (i == pieces - 1)
                {
                    result.Append('|').Append(parts[2]); // ends exactly where the map says
                }
                else
                {
                    Point(result, x1, y1);
                }
            }
            return result.ToString();
        }

        private static void Point(StringBuilder result, double x, double y)
        {
            result.Append('|')
                .Append(((int)Math.Round(x, MidpointRounding.AwayFromZero)).ToString(Invariant))
                .Append(':')
                .Append(((int)Math.Round(y, MidpointRounding.AwayFromZero)).ToString(Invariant));
        }
    }
}
