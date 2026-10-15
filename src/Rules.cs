using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CoreLasso
{
    public sealed class Rule
    {
        public bool           Enabled = true;
        public string         Exe = "";            // match on image name, no extension, case-insensitive
        public string         PathContains = "";   // optional extra filter on the full image path
        public ulong          Mask;
        public ApplyMode      Mode = ApplyMode.Both;
        public PriorityChoice Priority = PriorityChoice.Unchanged;
        public bool           Reapply;             // re-assert periodically (for apps that reset themselves)
        public string         Note = "";

        public Rule Clone()
        {
            return (Rule)MemberwiseClone();
        }

        public bool Matches(string procName, string fullPath)
        {
            if (!Enabled) return false;
            if (!string.Equals(Safety.Normalize(procName), Safety.Normalize(Exe),
                               StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(PathContains))
            {
                if (string.IsNullOrEmpty(fullPath)) return false;
                if (fullPath.IndexOf(PathContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Flat, human-editable config. Deliberately not JSON/XML so a corrupt or
    /// hand-edited line degrades to "skip that line" instead of losing the file.
    /// </summary>
    public static class RuleStore
    {
        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreLasso");
            }
        }
        public static string RulesPath { get { return Path.Combine(Dir, "rules.txt"); } }
        public static string LogPath   { get { return Path.Combine(Dir, "corelasso.log"); } }

        public static List<Rule> Load()
        {
            var list = new List<Rule>();
            try
            {
                if (!File.Exists(RulesPath)) return list;
                foreach (var raw in File.ReadAllLines(RulesPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var r = Parse(line);
                    if (r != null) list.Add(r);
                }
            }
            catch { /* unreadable config must never prevent startup */ }
            return list;
        }

        static Rule Parse(string line)
        {
            try
            {
                var r = new Rule();
                foreach (var field in line.Split('|'))
                {
                    int eq = field.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = field.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = field.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "exe":      r.Exe = v; break;
                        case "path":     r.PathContains = v; break;
                        case "mask":
                            v = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v.Substring(2) : v;
                            r.Mask = ulong.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            break;
                        case "mode":
                            r.Mode = (ApplyMode)Enum.Parse(typeof(ApplyMode), v, true); break;
                        case "priority":
                            r.Priority = (PriorityChoice)Enum.Parse(typeof(PriorityChoice), v, true); break;
                        case "enabled": r.Enabled = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "reapply": r.Reapply = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "note":    r.Note = v.Replace("\\n", " "); break;
                    }
                }
                return string.IsNullOrEmpty(r.Exe) ? null : r;
            }
            catch { return null; }
        }

        public static void Save(IEnumerable<Rule> rules)
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>
            {
                "# CoreLasso rules. One rule per line; edit by hand if you like.",
                "# mask is a hex CPU bitmask (bit 0 = CPU 0). mode = CpuSets | HardAffinity | Both",
                "# priority = Unchanged | Idle | BelowNormal | Normal | AboveNormal | High",
                "",
            };
            foreach (var r in rules)
            {
                lines.Add(string.Format(
                    "exe={0} | mask=0x{1:X} | mode={2} | priority={3} | enabled={4} | reapply={5} | path={6} | note={7}",
                    r.Exe, r.Mask, r.Mode, r.Priority, r.Enabled ? 1 : 0, r.Reapply ? 1 : 0,
                    r.PathContains, (r.Note ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ")));
            }

            // Write to a temp file then move, so a crash mid-write cannot corrupt the config.
            string tmp = RulesPath + ".tmp";
            File.WriteAllLines(tmp, lines.ToArray());
            if (File.Exists(RulesPath)) File.Delete(RulesPath);
            File.Move(tmp, RulesPath);
        }

        static readonly object _logLock = new object();

        public static void Log(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(Dir);
                    // Keep the log from growing without bound.
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    {
                        var keep = File.ReadAllLines(LogPath);
                        File.WriteAllLines(LogPath, keep.Skip(Math.Max(0, keep.Length - 500)).ToArray());
                    }
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
