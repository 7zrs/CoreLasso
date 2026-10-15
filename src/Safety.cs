using System;
using System.Collections.Generic;

namespace CoreLasso
{
    /// <summary>
    /// Hard guardrails. Nothing in this app touches a process until these pass.
    ///
    /// Why this is safe by construction:
    ///  * CPU affinity and CPU sets are per-process-instance state held in the kernel
    ///    process object. They die with the process. A reboot (or just restarting the
    ///    game) wipes everything. No registry keys, no drivers, no services.
    ///  * We never write to disk outside our own config folder.
    ///  * We refuse to touch OS-critical processes, where starving a thread could
    ///    genuinely hang or bugcheck the machine.
    ///  * We refuse an empty mask. A process with zero runnable CPUs never schedules
    ///    again - that is the one way to truly wedge something with this API.
    /// </summary>
    public static class Safety
    {
        /// <summary>Processes we flatly refuse to modify. Starving these can hang or bugcheck Windows.</summary>
        static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memory compression", "secure system",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "ntoskrnl", "system interrupts", "wudfhost", "fontdrvhost",
            "svchost", "dwm", "audiodg", "wmiprvse", "trustedinstaller",
            "msmpeng", "mpdefendercoreservice", "securityhealthservice",
            "csrss.exe", "smss.exe",
        };

        /// <summary>Allowed, but we warn first - restricting these is legal yet usually a mistake.</summary>
        static readonly HashSet<string> Sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost",
            "ctfmon", "sihost", "taskhostw", "runtimebroker", "conhost",
            "corelasso",
        };

        public static string Normalize(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return "";
            string n = nameOrPath;
            int slash = n.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) n = n.Substring(slash + 1);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            return n.Trim();
        }

        public static bool IsCritical(string nameOrPath)
        {
            return Critical.Contains(Normalize(nameOrPath));
        }

        public static bool IsSensitive(string nameOrPath)
        {
            return Sensitive.Contains(Normalize(nameOrPath));
        }

        /// <summary>
        /// Validates a target before any kernel call. Returns false plus a reason if blocked.
        /// </summary>
        public static bool Validate(string procName, ulong mask, out string reason)
        {
            reason = null;
            string n = Normalize(procName);

            if (IsCritical(n))
            {
                reason = "\"" + n + "\" is an operating-system critical process.\n\n" +
                         "CoreLasso refuses to restrict it, because starving a system thread " +
                         "of CPU time can hang or crash Windows. This is a hard block.";
                return false;
            }

            if (mask == 0)
            {
                reason = "No CPUs selected.\n\n" +
                         "A process with an empty affinity mask can never be scheduled again - " +
                         "it would freeze permanently. Select at least one CPU.";
                return false;
            }

            ulong sys = Topology.SystemMaskGroup0;
            if ((mask & ~sys) != 0)
            {
                reason = "The selection includes CPUs that do not exist on this system " +
                         "(system mask 0x" + sys.ToString("X") + ").";
                return false;
            }

            if ((mask & sys) == 0)
            {
                reason = "The selection resolves to zero usable CPUs on this system.";
                return false;
            }

            return true;
        }

        /// <summary>Soft warnings shown to the user; they may proceed.</summary>
        public static List<string> Warnings(string procName, ulong mask, ApplyMode mode)
        {
            var w = new List<string>();
            string n = Normalize(procName);

            if (IsSensitive(n))
                w.Add("\"" + n + "\" is a Windows shell / system-adjacent process. " +
                      "Restricting it can make the desktop feel sluggish. It is reversible - " +
                      "just reset to All CPUs or restart the process.");

            if (Topology.PopCount(mask) == 1)
                w.Add("Only one CPU is selected. Most software runs extremely slowly on a single " +
                      "logical core, and a few multithreaded apps can deadlock waiting on each other. " +
                      "Use this only if you specifically need it.");

            bool anyP = (mask & Topology.PresetPCores()) != 0;
            if (Topology.HybridDetected && !anyP)
                w.Add("Only E-cores (efficiency cores) are selected. Performance will be " +
                      "substantially lower than on P-cores. This is usually the wrong choice for a game.");

            if (mode == ApplyMode.HardAffinity || mode == ApplyMode.Both)
                w.Add("Hard affinity changes what the process SEES (GetProcessAffinityMask / " +
                      "GetSystemInfo report only the selected CPUs). That is exactly what fixes " +
                      "legacy games, but it also prevents Windows from ever moving work elsewhere, " +
                      "even when your selected cores are saturated.");

            return w;
        }
    }
}
