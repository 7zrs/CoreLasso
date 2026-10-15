using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CoreLasso
{
    /// <summary>
    /// Polls for newly started processes and applies matching rules.
    ///
    /// Poll-based on purpose: a WMI __InstanceCreationEvent subscription needs a
    /// permanent WMI consumer or a managed event sink, both of which are heavier and
    /// can fail silently on hardened systems. A 500 ms scan of the process table
    /// costs a fraction of one core and cannot leave anything behind if we crash.
    ///
    /// For a game that must be constrained from its very first instruction, use
    /// "Launch with rule" instead - the watcher is inherently a few hundred ms late.
    /// </summary>
    public sealed class Watcher : IDisposable
    {
        readonly Func<List<Rule>> _rulesProvider;
        Thread _thread;
        volatile bool _run;

        readonly Dictionary<int, string> _seen = new Dictionary<int, string>();
        int _tick;

        public int  IntervalMs   = 500;
        public int  ReapplyEvery = 10;   // ticks between re-assert passes for reapply rules
        public bool IsRunning    { get { return _run; } }

        public event Action<string> Logged;

        public Watcher(Func<List<Rule>> rulesProvider)
        {
            _rulesProvider = rulesProvider;
        }

        void Emit(string msg)
        {
            RuleStore.Log(msg);
            var h = Logged;
            if (h != null) { try { h(msg); } catch { } }
        }

        public void Start()
        {
            if (_run) return;
            _run = true;

            // Prime the seen-set so enabling the watcher does not retroactively
            // clamp every process that happens to already be running.
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try { _seen[p.Id] = p.ProcessName; } catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }

            _thread = new Thread(Loop);
            _thread.IsBackground = true;             // never blocks app exit
            _thread.Priority = ThreadPriority.BelowNormal;
            _thread.Start();
            Emit("Watcher started (poll " + IntervalMs + " ms). Already-running processes are not touched.");
        }

        public void Stop()
        {
            if (!_run) return;
            _run = false;
            try { if (_thread != null) _thread.Join(2000); } catch { }
            _thread = null;
            Emit("Watcher stopped. Rules already applied to running processes stay in effect.");
        }

        void Loop()
        {
            while (_run)
            {
                try { Scan(); }
                catch (Exception ex) { Emit("Watcher scan error (continuing): " + ex.Message); }

                // Sleep in slices so Stop() is responsive.
                int slept = 0;
                while (_run && slept < IntervalMs) { Thread.Sleep(50); slept += 50; }
            }
        }

        void Scan()
        {
            var rules = _rulesProvider();
            _tick++;
            bool reapplyPass = ReapplyEvery > 0 && (_tick % ReapplyEvery == 0);

            if (rules.Count == 0)
            {
                // Still refresh the seen-set so rules added later only affect future launches.
                RefreshSeenOnly();
                return;
            }

            var live = new HashSet<int>();
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return; }

            foreach (var p in procs)
            {
                int pid;
                string name;
                try { pid = p.Id; name = p.ProcessName; }
                catch { try { p.Dispose(); } catch { } continue; }

                live.Add(pid);
                bool isNew = !_seen.ContainsKey(pid);

                try
                {
                    if (isNew) _seen[pid] = name;

                    if (Safety.IsCritical(name)) continue;

                    // Only act on brand-new processes, or on a periodic re-assert pass.
                    if (!isNew && !reapplyPass) continue;

                    var candidates = rules.Where(r => r.Matches(name, null)).ToList();
                    if (candidates.Count == 0) continue;

                    // Resolve the full path only when a name already matched - this keeps
                    // the common case (no match) from opening a handle to every process.
                    string path = "";
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try { path = ProcessOps.TryGetImagePath(h); } finally { Native.CloseHandle(h); }
                    }

                    var rule = candidates.FirstOrDefault(r => r.Matches(name, path));
                    if (rule == null) continue;
                    if (!isNew && !rule.Reapply) continue;

                    var res = ProcessOps.Apply(pid, name, rule.Mask, rule.Mode, rule.Priority);
                    if (res.Success)
                        Emit((isNew ? "Applied" : "Re-applied") + " rule to " + name + " (PID " + pid + "): " +
                             Topology.DescribeMask(rule.Mask) + " via " + rule.Mode);
                    else if (isNew)
                        Emit("Could not apply rule to " + name + " (PID " + pid + "): " +
                             res.Error.Replace(Environment.NewLine, " "));
                }
                catch (Exception ex) { Emit("Rule error on PID " + pid + ": " + ex.Message); }
                finally { try { p.Dispose(); } catch { } }
            }

            // Forget dead PIDs so Windows reusing a PID does not look like an old process.
            if (_seen.Count > live.Count)
                foreach (var dead in _seen.Keys.Where(k => !live.Contains(k)).ToList())
                    _seen.Remove(dead);
        }

        void RefreshSeenOnly()
        {
            try
            {
                var live = new HashSet<int>();
                foreach (var p in Process.GetProcesses())
                {
                    try { live.Add(p.Id); _seen[p.Id] = p.ProcessName; } catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                foreach (var dead in _seen.Keys.Where(k => !live.Contains(k)).ToList())
                    _seen.Remove(dead);
            }
            catch { }
        }

        public void Dispose() { Stop(); }
    }
}
