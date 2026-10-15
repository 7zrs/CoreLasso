using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreLasso
{
    public enum ApplyMode
    {
        CpuSets = 0,       // SetProcessDefaultCpuSets - the modern, scheduler-friendly knob
        HardAffinity = 1,  // SetProcessAffinityMask    - changes what the process SEES
        Both = 2,
    }

    public enum PriorityChoice
    {
        Unchanged = 0, Idle, BelowNormal, Normal, AboveNormal, High,
    }

    public sealed class ProcSnapshot
    {
        public int    Pid;
        public string Name = "";
        public string Path = "";
        public ulong  AffinityMask;
        public ulong  CpuSetMask;
        public bool   HasCpuSetRestriction;
        public int    Threads;
        public string PriorityText = "";
        public bool   Accessible;
        public string Note = "";
    }

    public sealed class ApplyResult
    {
        public bool   CpuSetsOk, AffinityOk, PriorityOk;
        public bool   ElevationRequired;   // caller should offer to restart elevated
        public string Error;
        public bool   Success { get { return Error == null; } }
        public override string ToString()
        {
            if (Error != null) return "FAILED: " + Error;
            var p = new List<string>();
            if (CpuSetsOk)  p.Add("cpu-sets");
            if (AffinityOk) p.Add("affinity");
            if (PriorityOk) p.Add("priority");
            return "ok (" + string.Join(", ", p.ToArray()) + ")";
        }
    }

    public static class ProcessOps
    {
        const int ERROR_ELEVATION_REQUIRED = 740;

        /// <summary>True when this instance of CoreLasso is running elevated.</summary>
        public static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(
                        System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static uint PriorityFlag(PriorityChoice p)
        {
            switch (p)
            {
                case PriorityChoice.Idle:        return 0x00000040;
                case PriorityChoice.BelowNormal: return 0x00004000;
                case PriorityChoice.Normal:      return 0x00000020;
                case PriorityChoice.AboveNormal: return 0x00008000;
                case PriorityChoice.High:        return 0x00000080;
                default: return 0;
            }
        }

        public static string PriorityToText(uint flag)
        {
            switch (flag)
            {
                case 0x00000040: return "Idle";
                case 0x00004000: return "Below Normal";
                case 0x00000020: return "Normal";
                case 0x00008000: return "Above Normal";
                case 0x00000080: return "High";
                case 0x00000100: return "Realtime";
                default: return "?";
            }
        }

        public static string TryGetImagePath(IntPtr hProc)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint cap = (uint)sb.Capacity;
                if (Native.QueryFullProcessImageName(hProc, 0, sb, ref cap))
                    return sb.ToString();
            }
            catch { }
            return "";
        }

        /// <summary>Reads current state without modifying anything. Never throws.</summary>
        public static ProcSnapshot Read(Process p)
        {
            var s = new ProcSnapshot { Pid = p.Id };
            try { s.Name = p.ProcessName; } catch { }

            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero)
            {
                s.Accessible = false;
                s.Note = "no access (elevated or protected)";
                try { s.Threads = p.Threads.Count; } catch { }
                return s;
            }
            try
            {
                s.Accessible = true;
                s.Path = TryGetImagePath(h);

                UIntPtr pm, sm;
                if (Native.GetProcessAffinityMask(h, out pm, out sm))
                    s.AffinityMask = (ulong)pm.ToUInt64();

                uint required;
                Native.GetProcessDefaultCpuSets(h, null, 0, out required);
                if (required > 0)
                {
                    var ids = new uint[required];
                    uint req2;
                    if (Native.GetProcessDefaultCpuSets(h, ids, required, out req2))
                    {
                        s.CpuSetMask = Topology.MaskFromSetIds(ids);
                        s.HasCpuSetRestriction = true;
                    }
                }
                else
                {
                    // Empty default set == unrestricted (all CPUs allowed).
                    s.CpuSetMask = Topology.SystemMaskGroup0;
                    s.HasCpuSetRestriction = false;
                }

                uint pc = Native.GetPriorityClass(h);
                if (pc != 0) s.PriorityText = PriorityToText(pc);

                try { s.Threads = p.Threads.Count; } catch { }
            }
            catch (Exception ex) { s.Note = ex.Message; }
            finally { Native.CloseHandle(h); }
            return s;
        }

        /// <summary>
        /// Applies a rule to an already-running process. Validates via Safety first.
        /// Returns a result object - never throws.
        /// </summary>
        public static ApplyResult Apply(int pid, string nameForSafety, ulong mask,
                                        ApplyMode mode, PriorityChoice prio)
        {
            var r = new ApplyResult();

            string reason;
            if (!Safety.Validate(nameForSafety, mask, out reason))
            {
                r.Error = reason;
                return r;
            }

            uint access = Native.PROCESS_SET_LIMITED_INFORMATION |
                          Native.PROCESS_SET_INFORMATION |
                          Native.PROCESS_QUERY_LIMITED_INFORMATION;

            IntPtr h = Native.OpenProcess(access, false, pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                // Retry with only what cpu-sets needs; some processes deny SET_INFORMATION.
                h = Native.OpenProcess(Native.PROCESS_SET_LIMITED_INFORMATION |
                                       Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    r.ElevationRequired = (err == 5) && !IsElevated();
                    r.Error = "Cannot open PID " + pid + " (Win32 error " + err + "). " +
                              (err == 5 ? "Access denied - the process runs elevated or as another " +
                              "user, or it is protected by anti-cheat."
                                        : "The process may have exited.");
                    return r;
                }
            }

            try
            {
                if (mode == ApplyMode.CpuSets || mode == ApplyMode.Both)
                {
                    uint[] ids = Topology.SetIdsFromMask(mask);
                    if (ids.Length == 0) { r.Error = "Mask resolved to no CPU set ids."; return r; }
                    if (!Native.SetProcessDefaultCpuSets(h, ids, (uint)ids.Length))
                    {
                        r.Error = "SetProcessDefaultCpuSets failed: " +
                                  new Win32Exception(Marshal.GetLastWin32Error()).Message;
                        return r;
                    }
                    r.CpuSetsOk = true;
                }

                if (mode == ApplyMode.HardAffinity || mode == ApplyMode.Both)
                {
                    if (!Native.SetProcessAffinityMask(h, new UIntPtr(mask)))
                    {
                        r.Error = "SetProcessAffinityMask failed: " +
                                  new Win32Exception(Marshal.GetLastWin32Error()).Message +
                                  (r.CpuSetsOk ? " (CPU sets were applied successfully.)" : "");
                        return r;
                    }
                    r.AffinityOk = true;
                }

                if (prio != PriorityChoice.Unchanged)
                    r.PriorityOk = Native.SetPriorityClass(h, PriorityFlag(prio));
            }
            catch (Exception ex) { r.Error = ex.Message; }
            finally { Native.CloseHandle(h); }

            return r;
        }

        /// <summary>
        /// Clears every restriction: all CPUs for both cpu-sets and hard affinity.
        /// This is the undo button and it always works on an accessible process.
        /// </summary>
        public static ApplyResult Reset(int pid)
        {
            var r = new ApplyResult();
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_LIMITED_INFORMATION |
                                          Native.PROCESS_SET_INFORMATION |
                                          Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                r.Error = "Cannot open PID " + pid + " (error " +
                          Marshal.GetLastWin32Error() + ").";
                return r;
            }
            try
            {
                // Passing a null id list removes the default-cpu-set restriction entirely.
                r.CpuSetsOk  = Native.SetProcessDefaultCpuSets(h, null, 0);
                r.AffinityOk = Native.SetProcessAffinityMask(h, new UIntPtr(Topology.SystemMaskGroup0));
                if (!r.CpuSetsOk && !r.AffinityOk)
                    r.Error = "Both reset calls failed: " +
                              new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            finally { Native.CloseHandle(h); }
            return r;
        }

        /// <summary>
        /// The real fix for legacy games: create the process SUSPENDED, apply the rule,
        /// then resume. The game's very first instruction already sees the restricted
        /// topology, so its CPU-detection code cannot trip over 20 logical processors.
        /// </summary>
        public static ApplyResult LaunchWithRule(string exePath, string arguments, string workingDir,
                                                 ulong mask, ApplyMode mode, PriorityChoice prio,
                                                 out int pid)
        {
            pid = 0;
            var r = new ApplyResult();

            string reason;
            if (!Safety.Validate(exePath, mask, out reason)) { r.Error = reason; return r; }
            if (!System.IO.File.Exists(exePath)) { r.Error = "Executable not found:\n" + exePath; return r; }

            var si = new Native.STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(Native.STARTUPINFO));
            Native.PROCESS_INFORMATION pi;

            string cmdLine = "\"" + exePath + "\"" +
                             (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
            if (string.IsNullOrEmpty(workingDir))
                workingDir = System.IO.Path.GetDirectoryName(exePath);

            if (!Native.CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                                      Native.CREATE_SUSPENDED, IntPtr.Zero, workingDir, ref si, out pi))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ELEVATION_REQUIRED)
                {
                    // CreateProcess cannot elevate. ShellExecute("runas") could, but it cannot
                    // create the process suspended, which would defeat the whole point - the
                    // mask has to be in place before the game's first instruction. So the only
                    // correct answer is for CoreLasso itself to be elevated.
                    r.ElevationRequired = true;
                    r.Error = "\"" + System.IO.Path.GetFileName(exePath) + "\" requires " +
                    "administrator rights to start.\r\n\r\n" +
                    "CoreLasso must itself be running as administrator to launch it with " +
                    "a CPU rule, because a non-elevated process cannot create an elevated one.";
                }
                else
                {
                    r.Error = "CreateProcess failed: " + new Win32Exception(err).Message;
                }
                return r;
            }

            try
            {
                pid = pi.dwProcessId;

                if (mode == ApplyMode.CpuSets || mode == ApplyMode.Both)
                {
                    uint[] ids = Topology.SetIdsFromMask(mask);
                    r.CpuSetsOk = Native.SetProcessDefaultCpuSets(pi.hProcess, ids, (uint)ids.Length);
                }
                if (mode == ApplyMode.HardAffinity || mode == ApplyMode.Both)
                    r.AffinityOk = Native.SetProcessAffinityMask(pi.hProcess, new UIntPtr(mask));
                if (prio != PriorityChoice.Unchanged)
                    r.PriorityOk = Native.SetPriorityClass(pi.hProcess, PriorityFlag(prio));

                // Resume no matter what - never leave a suspended orphan behind.
                if (Native.ResumeThread(pi.hThread) == 0xFFFFFFFF)
                    r.Error = "Rule applied but ResumeThread failed; the process is suspended. " +
                    "Kill PID " + pid + " in Task Manager.";
            }
            catch (Exception ex)
            {
                try { Native.ResumeThread(pi.hThread); } catch { }
                r.Error = ex.Message;
            }
            finally
            {
                Native.CloseHandle(pi.hThread);
                Native.CloseHandle(pi.hProcess);
            }
            return r;
        }
    }
}
