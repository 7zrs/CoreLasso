# CoreLasso

A Process-Lasso-style CPU affinity / CPU-Set manager for hybrid Intel CPUs.
Built for this machine: **i7-13650HX — 6 P-cores (12 threads, CPU 0–11) + 8 E-cores (CPU 12–19)**.

Run `bin\CoreLasso.exe`. Rebuild with `build.cmd`.

---

## The thing that actually matters

Old games break on modern CPUs for two different reasons, and they need two
different fixes. This is the part most affinity tools get wrong.

| Mode | What it does | Fixes a game that *miscounts cores*? |
|---|---|---|
| **CPU Sets** (`SetProcessDefaultCpuSets`) | Scheduler-level restriction. Friendly, instantly reversible. | **No.** `GetSystemInfo` / `GetProcessAffinityMask` still report all 20 CPUs, so the game's broken detection code still runs. |
| **Hard affinity** (`SetProcessAffinityMask`) | Restricts execution **and** changes what the process *sees*. | **Yes.** The game is told it has 4 CPUs, so its startup math works. |

So:

- Game **crashes / hangs / won't start** → **hard affinity**, applied **before the
  game's first instruction**. Use **"Launch .exe with rule"**.
- Game **runs but stutters** on E-cores → **CPU Sets** (or Both) is fine, and the
  watcher's timing doesn't matter.

The watcher is inherently a few hundred milliseconds late — fine for performance
tuning, too late for a game that crashes in its CPU-detection code during init.
That's exactly why `LaunchWithRule` creates the process `CREATE_SUSPENDED`,
applies the mask, and only then calls `ResumeThread`.

---

## Three ways to apply a rule

1. **Launch .exe with rule** — most reliable. Process is created suspended, the
   mask is applied, then it resumes. The game never sees 20 cores, not even once.
2. **Watcher** (Rules tab) — polls every 500 ms and clamps matching processes as
   they start. Use this for games launched by Steam/Epic/a launcher, where you
   can't start the exe directly.
3. **Apply to selected process** — live edit on anything already running. Takes
   effect immediately; this is the real-time editing you asked for.

Rules persist to `%APPDATA%\CoreLasso\rules.txt` — a plain text file you can
hand-edit. Activity goes to `%APPDATA%\CoreLasso\corelasso.log`.

---

## Presets for this CPU

| Preset | Mask | CPUs |
|---|---|---|
| All CPUs | `0xFFFFF` | 0–19 |
| P-cores only | `0xFFF` | 0–11 |
| P-cores, no HT | `0x555` | 0,2,4,6,8,10 |
| E-cores only | `0xFF000` | 12–19 |
| Legacy: first 2 P-cores, no HT | `0x5` | 0,2 |
| Legacy: first 4 P-cores, no HT | `0x55` | 0,2,4,6 |
| Legacy: first 2 P-cores + HT | `0xF` | 0–3 |

**For an old game, start with "P-cores, no HT" (`0x555`).** That removes the
hybrid topology *and* hyperthreading — the two things that confuse pre-2015
engines — while still giving you 6 real cores. If it still won't start, step down
to `0x55` (4 cores), then `0xF`.

Avoid E-cores-only for games. The app warns you if you try.

---

## Why this is safe

Affinity and CPU sets are **per-process kernel state**. They are stored in the
process object and die with the process.

- **Nothing persists.** Restart the game, or reboot, and defaults are back. There
  is no cleanup step and no way to leave the system in a modified state.
- **No driver, no service, no registry writes, no system-wide settings.** The only
  thing written to disk is `%APPDATA%\CoreLasso\`.
- **OS-critical processes are hard-blocked** (`csrss`, `lsass`, `services`,
  `svchost`, `smss`, `wininit`, `winlogon`, `dwm`, `audiodg`, …). Starving a system
  thread of CPU time is the one way affinity can genuinely hang Windows, so the
  app refuses — not a warning, a refusal.
- **Empty masks are refused.** A process with zero allowed CPUs can never be
  scheduled again. This is the only truly destructive thing this API can do, and
  it's blocked in `Safety.Validate`, which every code path goes through.
- **The watcher only touches processes started after you enable it**, and only ones
  matching a rule you wrote. On startup it primes its seen-set so it won't
  retroactively clamp everything already running.
- **Reset to all CPUs** is always available, and the app never needs elevation to
  manage your own non-elevated processes.

Press **"Is this safe?"** in the app for the same explanation in-place.

### Verify it yourself

```
bin\CoreLasso.exe --selftest
```

Spawns a throwaway `ping`, applies a mask to it suspended, reads it back, live-edits
it, resets it, kills it, and round-trips a rule through the config file. 16 checks.
Current result on this machine: **ALL CHECKS PASSED**.

```
bin\CoreLasso.exe --list    # dump topology, EfficiencyClass, SMT siblings, presets
bin\CoreLasso.exe --ps      # list processes that currently have restricted CPUs
```

---

## Limits, honestly

- **Anti-cheat games** (EAC, BattlEye, Vanguard) may block external modification or
  revert it. "Launch .exe with rule" has the best chance since the mask is set
  before the anti-cheat initializes — but a protected process can still refuse.
- **Elevated games** need `Run as Admin.cmd`. Two distinct symptoms:
  - `CreateProcess failed: The requested operation requires elevation` (error 740)
    when using *Launch .exe with rule* — the game's own manifest demands admin, and a
    non-elevated process cannot create an elevated child.
  - `Access denied` (error 5) when applying to an already-running elevated process.

  Either way CoreLasso now offers a one-click elevated restart. Note that elevating is
  the **only** way to keep the suspended-launch guarantee: `ShellExecute("runas")` can
  elevate but cannot create a process suspended, so the mask could not be applied before
  the game's first instruction. The title bar and header show whether you are elevated.
- **Hard affinity means Windows can't help you.** If you pin a game to 4 CPUs and it
  saturates them, the scheduler will not spill work onto the idle 16. That's the
  trade you're making deliberately.
- Hard affinity applies to **processor group 0**. Irrelevant here (20 CPUs, one
  group); it would matter only on a >64-thread machine. The app says so in its
  header if it ever detects multiple groups.
- Some games spawn a **launcher** that then starts the real exe. Write the rule
  against the real game exe and use the watcher, or point "Launch with rule" at
  the real exe directly.

---

## Source layout

| File | Purpose |
|---|---|
| `src/Native.cs` | P/Invoke declarations. Read-only or process-scoped calls only. |
| `src/Topology.cs` | `GetSystemCpuSetInformation` → P/E classification via `EfficiencyClass`, SMT siblings via shared `CoreIndex`, mask helpers, presets. |
| `src/Safety.cs` | The guardrails. Critical-process blocklist, empty-mask refusal, range check, soft warnings. |
| `src/ProcessOps.cs` | Read / Apply / Reset / LaunchWithRule. Never throws; returns result objects. |
| `src/Rules.cs` | Line-based config. A corrupt line is skipped, not fatal. Atomic temp-file save. |
| `src/Watcher.cs` | Background poll loop for new processes. Background thread, below-normal priority. |
| `src/MainForm.cs` | WinForms UI. |
| `src/Program.cs` | Entry point + `--list` / `--ps` / `--selftest`. |

No dependencies beyond what Windows already ships. ~74 KB exe.
