using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CoreLasso
{
    public sealed class LogicalCpu
    {
        public uint   SetId;             // opaque id required by SetProcessDefaultCpuSets
        public int    CpuIndex;          // logical processor index within its group
        public ushort Group;
        public int    CoreIndex;         // physical core; SMT siblings share this
        public int    EfficiencyClass;
        public int    NumaNode;
        public int    CacheIndex;
        public bool   Parked;

        public bool   IsPerformanceCore; // EfficiencyClass == max on this system
        public bool   IsSmtSibling;      // second+ logical cpu on its physical core
        public int    SmtWidth;          // how many logical cpus share the core

        /// <summary>Bit position in a 64-bit affinity mask (valid within its group).</summary>
        public int    MaskBit { get { return CpuIndex; } }

        public string Kind { get { return IsPerformanceCore ? "P" : "E"; } }

        public string ShortLabel
        {
            get { return "CPU " + CpuIndex + (IsSmtSibling ? " HT" : ""); }
        }
    }

    public static class Topology
    {
        static List<LogicalCpu> _cpus;

        public static bool  HybridDetected   { get; private set; }
        public static bool  MultiGroup       { get; private set; }
        public static ulong SystemMaskGroup0 { get; private set; }

        public static IList<LogicalCpu> Cpus { get { if (_cpus == null) Load(); return _cpus; } }

        public static void Load()
        {
            var list = new List<LogicalCpu>();
            uint needed;
            // First call with a zero buffer tells us how much to allocate.
            Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out needed, IntPtr.Zero, 0);
            if (needed == 0)
                throw new InvalidOperationException("GetSystemCpuSetInformation reported no data.");

            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                uint written;
                if (!Native.GetSystemCpuSetInformation(buf, needed, out written, IntPtr.Zero, 0))
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(), "GetSystemCpuSetInformation failed.");

                int offset = 0;
                while (offset < (int)written)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + offset);
                    var info = (Native.SYSTEM_CPU_SET_INFORMATION)Marshal.PtrToStructure(
                        p, typeof(Native.SYSTEM_CPU_SET_INFORMATION));
                    if (info.Size == 0) break;              // malformed; bail rather than spin
                    if (info.Type == 0)                     // CpuSetInformation
                    {
                        list.Add(new LogicalCpu
                        {
                            SetId           = info.Id,
                            CpuIndex        = info.LogicalProcessorIndex,
                            Group           = info.Group,
                            CoreIndex       = info.CoreIndex,
                            EfficiencyClass = info.EfficiencyClass,
                            NumaNode        = info.NumaNodeIndex,
                            CacheIndex      = info.LastLevelCacheIndex,
                            Parked          = (info.AllFlags & 0x1) != 0,
                        });
                    }
                    offset += (int)info.Size;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            if (list.Count == 0) throw new InvalidOperationException("No CPU sets enumerated.");

            int maxEff = list.Max(c => c.EfficiencyClass);
            int minEff = list.Min(c => c.EfficiencyClass);
            HybridDetected = maxEff != minEff;

            // Group by (Group, CoreIndex) to find SMT siblings.
            foreach (var core in list.GroupBy(c => new { c.Group, c.CoreIndex }))
            {
                var ordered = core.OrderBy(c => c.CpuIndex).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    ordered[i].SmtWidth     = ordered.Count;
                    ordered[i].IsSmtSibling = i > 0;
                }
            }
            foreach (var c in list)
                c.IsPerformanceCore = !HybridDetected || c.EfficiencyClass == maxEff;

            _cpus = list.OrderBy(c => c.Group).ThenBy(c => c.CpuIndex).ToList();
            MultiGroup = _cpus.Select(c => c.Group).Distinct().Count() > 1;

            ulong sys = 0;
            foreach (var c in _cpus)
                if (c.Group == 0 && c.MaskBit < 64) sys |= 1UL << c.MaskBit;
            SystemMaskGroup0 = sys;
        }

        // ---------- mask helpers ----------

        public static ulong MaskFor(Func<LogicalCpu, bool> pred)
        {
            ulong m = 0;
            foreach (var c in Cpus)
                if (c.Group == 0 && c.MaskBit < 64 && pred(c)) m |= 1UL << c.MaskBit;
            return m;
        }

        public static uint[] SetIdsFromMask(ulong mask)
        {
            var ids = new List<uint>();
            foreach (var c in Cpus)
                if (c.Group == 0 && c.MaskBit < 64 && (mask & (1UL << c.MaskBit)) != 0) ids.Add(c.SetId);
            return ids.ToArray();
        }

        public static ulong MaskFromSetIds(IEnumerable<uint> ids)
        {
            var set = new HashSet<uint>(ids);
            ulong m = 0;
            foreach (var c in Cpus)
                if (set.Contains(c.SetId) && c.Group == 0 && c.MaskBit < 64) m |= 1UL << c.MaskBit;
            return m;
        }

        public static int PopCount(ulong v)
        {
            int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
        }

        public static string DescribeMask(ulong mask)
        {
            if (mask == 0) return "(none)";
            int p = Cpus.Count(c => c.IsPerformanceCore  && (mask & (1UL << c.MaskBit)) != 0);
            int e = Cpus.Count(c => !c.IsPerformanceCore && (mask & (1UL << c.MaskBit)) != 0);
            var parts = new List<string>();
            if (p > 0) parts.Add(p + "x P");
            if (e > 0) parts.Add(e + "x E");
            return string.Join(" + ", parts.ToArray()) + "   " + RangeString(mask);
        }

        public static string RangeString(ulong mask)
        {
            var idx = new List<int>();
            for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) idx.Add(i);
            if (idx.Count == 0) return "";
            var parts = new List<string>();
            int start = idx[0], prev = idx[0];
            for (int i = 1; i <= idx.Count; i++)
            {
                if (i < idx.Count && idx[i] == prev + 1) { prev = idx[i]; continue; }
                parts.Add(start == prev ? start.ToString() : start + "-" + prev);
                if (i < idx.Count) { start = idx[i]; prev = idx[i]; }
            }
            return "CPU " + string.Join(",", parts.ToArray());
        }

        // ---------- presets ----------

        public static ulong PresetAll()    { return SystemMaskGroup0; }
        public static ulong PresetPCores() { return MaskFor(c => c.IsPerformanceCore); }
        public static ulong PresetECores() { return MaskFor(c => !c.IsPerformanceCore); }
        public static ulong PresetPNoSmt() { return MaskFor(c => c.IsPerformanceCore && !c.IsSmtSibling); }

        /// <summary>First N physical P-cores, primary thread only - the classic legacy-game fix.</summary>
        public static ulong PresetFirstPhysicalPCores(int coreCount)
        {
            ulong m = 0;
            foreach (var c in Cpus.Where(x => x.IsPerformanceCore && !x.IsSmtSibling)
                                  .OrderBy(x => x.CpuIndex).Take(coreCount))
                m |= 1UL << c.MaskBit;
            return m;
        }

        /// <summary>First N physical P-cores including their SMT siblings.</summary>
        public static ulong PresetFirstPCoresWithSmt(int coreCount)
        {
            var coreIdx = Cpus.Where(c => c.IsPerformanceCore && !c.IsSmtSibling)
                              .OrderBy(c => c.CpuIndex).Take(coreCount)
                              .Select(c => c.CoreIndex).ToList();
            ulong m = 0;
            foreach (var c in Cpus)
                if (c.IsPerformanceCore && coreIdx.Contains(c.CoreIndex)) m |= 1UL << c.MaskBit;
            return m;
        }
    }
}
