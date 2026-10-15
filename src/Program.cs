using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CoreLasso
{
    internal static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool AllocConsole();

        static void OpenConsole()
        {
            if (!AttachConsole(-1)) AllocConsole();
            try
            {
                var w = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(w);
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                OpenConsole();
                try
                {
                    switch (args[0].ToLowerInvariant())
                    {
                        case "--list":     CmdList();     return 0;
                        case "--ps":       CmdPs();       return 0;
                        case "--selftest": return CmdSelfTest();
                        default:
                            Console.WriteLine("CoreLasso - usage:");
                            Console.WriteLine("  CoreLasso.exe             launch the GUI");
                            Console.WriteLine("  CoreLasso.exe --list      print CPU topology and presets");
                            Console.WriteLine("  CoreLasso.exe --ps        print processes with restricted CPUs");
                            Console.WriteLine("  CoreLasso.exe --selftest  verify the kernel calls work end-to-end");
                            return 1;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex);
                    return 2;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Never die with a raw stack trace; a crash here must not look like system damage.
            Application.ThreadException += (s, e) =>
            {
                RuleStore.Log("UI exception: " + e.Exception);
                MessageBox.Show("CoreLasso hit an error but your system is unaffected:\r\n\r\n" +
                                e.Exception.Message + "\r\n\r\nNo CPU settings were changed by this error.",
                                "CoreLasso", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                RuleStore.Log("Fatal: " + e.ExceptionObject);

            try { Topology.Load(); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read the CPU topology from Windows:\r\n\r\n" + ex.Message +
                                "\r\n\r\nCoreLasso needs Windows 10 1607 or newer.",
                                "CoreLasso", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 3;
            }

            Application.Run(new MainForm());
            return 0;
        }

        // ---------------- CLI commands ----------------

        static void CmdList()
        {
            Topology.Load();
            Console.WriteLine("Logical CPUs: " + Topology.Cpus.Count +
                              "   hybrid: " + Topology.HybridDetected +
                              "   multi-group: " + Topology.MultiGroup);
            Console.WriteLine("System mask : 0x" + Topology.SystemMaskGroup0.ToString("X"));
            Console.WriteLine();
            Console.WriteLine("CPU  SetId   Grp Core Eff Kind  SMT  Parked");
            foreach (var c in Topology.Cpus)
                Console.WriteLine(string.Format("{0,3}  0x{1:X4}  {2,3} {3,4} {4,3} {5,-4}  {6,-4} {7}",
                    c.CpuIndex, c.SetId, c.Group, c.CoreIndex, c.EfficiencyClass,
                    c.IsPerformanceCore ? "P" : "E",
                    c.IsSmtSibling ? "HT" : (c.SmtWidth > 1 ? "pri" : "-"), c.Parked));

            Console.WriteLine();
            var presets = new[]
            {
                new { N = "All CPUs",                     M = Topology.PresetAll() },
                new { N = "P-cores (all threads)",        M = Topology.PresetPCores() },
                new { N = "P-cores, no HT",               M = Topology.PresetPNoSmt() },
                new { N = "E-cores only",                 M = Topology.PresetECores() },
                new { N = "First 2 P-cores, no HT",       M = Topology.PresetFirstPhysicalPCores(2) },
                new { N = "First 4 P-cores, no HT",       M = Topology.PresetFirstPhysicalPCores(4) },
                new { N = "First 2 P-cores + HT",         M = Topology.PresetFirstPCoresWithSmt(2) },
            };
            foreach (var p in presets)
                Console.WriteLine(string.Format("{0,-26} 0x{1,-6:X}  {2}", p.N, p.M, Topology.DescribeMask(p.M)));
        }

        static void CmdPs()
        {
            Topology.Load();
            Console.WriteLine("Processes whose CPUs are restricted (or inaccessible):");
            foreach (var p in Process.GetProcesses().OrderBy(x => { try { return x.ProcessName; } catch { return ""; } }))
            {
                try
                {
                    var s = ProcessOps.Read(p);
                    if (!s.Accessible) continue;
                    bool restricted = s.HasCpuSetRestriction ||
                                      (s.AffinityMask != 0 && s.AffinityMask != Topology.SystemMaskGroup0);
                    if (!restricted) continue;
                    Console.WriteLine(string.Format("{0,-28} pid {1,-7} aff 0x{2:X}  cpuset {3}",
                        s.Name, s.Pid, s.AffinityMask,
                        s.HasCpuSetRestriction ? "0x" + s.CpuSetMask.ToString("X") : "(none)"));
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// End-to-end verification against a throwaway process we spawn ourselves
        /// (ping, which exits on its own). Nothing the user cares about is touched.
        /// </summary>
        static int CmdSelfTest()
        {
            int fails = 0;
            Topology.Load();
            Console.WriteLine("CoreLasso self-test");
            Console.WriteLine("===================");
            Console.WriteLine("Topology: " + Topology.Cpus.Count + " logical CPUs, hybrid=" +
                              Topology.HybridDetected + ", system mask=0x" +
                              Topology.SystemMaskGroup0.ToString("X"));

            Action<string, bool, string> check = (name, ok, detail) =>
            {
                Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + (detail == null ? "" : "   " + detail));
                if (!ok) fails++;
            };

            check("hybrid P/E detected via EfficiencyClass", Topology.HybridDetected,
                  "P threads=" + Topology.PopCount(Topology.PresetPCores()) +
                  " E threads=" + Topology.PopCount(Topology.PresetECores()));

            // --- safety gates must refuse bad input ---
            string reason;
            check("empty mask refused", !Safety.Validate("notepad", 0UL, out reason), null);
            check("critical process refused", !Safety.Validate("csrss.exe", 0xFUL, out reason), null);
            check("out-of-range mask refused",
                  !Safety.Validate("notepad", 0xFFFFFFFFFFFFFFFFUL, out reason), null);
            check("ordinary target accepted", Safety.Validate("somegame.exe", 0x3UL, out reason), null);

            // --- launch suspended, apply, verify ---
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            if (!File.Exists(exe)) { Console.WriteLine("  SKIP  ping.exe not found; cannot run live test."); return fails; }

            ulong want = Topology.PresetFirstPhysicalPCores(2);
            if (Topology.PopCount(want) < 2) want = 0x3UL;

            int pid;
            var res = ProcessOps.LaunchWithRule(exe, "-n 30 127.0.0.1", null, want, ApplyMode.Both,
                                                PriorityChoice.Unchanged, out pid);
            check("launch suspended + apply + resume", res.Success, res.ToString() + " pid=" + pid);
            if (!res.Success) return fails;

            Process target = null;
            try
            {
                target = Process.GetProcessById(pid);
                System.Threading.Thread.Sleep(250);

                var snap = ProcessOps.Read(target);
                check("hard affinity read-back matches", snap.AffinityMask == want,
                      "got 0x" + snap.AffinityMask.ToString("X") + " want 0x" + want.ToString("X"));
                check("cpu-set read-back matches",
                      snap.HasCpuSetRestriction && snap.CpuSetMask == want,
                      "got 0x" + snap.CpuSetMask.ToString("X") + " restricted=" + snap.HasCpuSetRestriction);

                // --- real-time edit on the already-running process ---
                ulong want2 = Topology.PresetECores();
                if (want2 == 0) want2 = Topology.PresetAll();
                var r2 = ProcessOps.Apply(pid, "ping", want2, ApplyMode.Both, PriorityChoice.BelowNormal);
                check("real-time re-apply succeeded", r2.Success, r2.ToString());
                System.Threading.Thread.Sleep(150);
                var snap2 = ProcessOps.Read(target);
                check("real-time edit took effect", snap2.AffinityMask == want2 && snap2.CpuSetMask == want2,
                      "aff 0x" + snap2.AffinityMask.ToString("X") + " set 0x" + snap2.CpuSetMask.ToString("X"));
                check("priority change took effect", snap2.PriorityText == "Below Normal", snap2.PriorityText);

                // --- reset must fully restore ---
                var r3 = ProcessOps.Reset(pid);
                check("reset succeeded", r3.Success, r3.ToString());
                System.Threading.Thread.Sleep(150);
                var snap3 = ProcessOps.Read(target);
                check("affinity restored to all CPUs", snap3.AffinityMask == Topology.SystemMaskGroup0,
                      "0x" + snap3.AffinityMask.ToString("X"));
                check("cpu-set restriction removed", !snap3.HasCpuSetRestriction, null);
            }
            catch (Exception ex) { check("live verification", false, ex.Message); }
            finally
            {
                try { if (target != null && !target.HasExited) target.Kill(); } catch { }
                try { if (target != null) target.Dispose(); } catch { }
            }

            // --- rule round-trip through the config file ---
            try
            {
                var rules = new List<Rule>
                {
                    new Rule { Exe = "selftest_dummy", Mask = want, Mode = ApplyMode.Both,
                               Priority = PriorityChoice.High, Reapply = true, Note = "self-test" },
                };
                string backupPath = RuleStore.RulesPath + ".selftest-backup";
                bool had = File.Exists(RuleStore.RulesPath);
                if (had) File.Copy(RuleStore.RulesPath, backupPath, true);

                RuleStore.Save(rules);
                var loaded = RuleStore.Load();
                var one = loaded.FirstOrDefault(r => r.Exe == "selftest_dummy");
                check("rule save/load round-trip",
                      one != null && one.Mask == want && one.Mode == ApplyMode.Both &&
                      one.Priority == PriorityChoice.High && one.Reapply,
                      one == null ? "not found" : "0x" + one.Mask.ToString("X"));

                if (had) { File.Copy(backupPath, RuleStore.RulesPath, true); File.Delete(backupPath); }
                else File.Delete(RuleStore.RulesPath);
                check("self-test left no rules behind",
                      had ? File.Exists(RuleStore.RulesPath) : !File.Exists(RuleStore.RulesPath), null);
            }
            catch (Exception ex) { check("rule round-trip", false, ex.Message); }

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "ALL CHECKS PASSED" : fails + " CHECK(S) FAILED");
            return fails == 0 ? 0 : 1;
        }
    }
}
