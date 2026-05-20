using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Memory.Sigscan;

namespace FH6Scanner;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr baseAddr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    const uint PROCESS_ALL_ACCESS = 0x001F0FFF;

    static void Main(string[] args)
    {
        var output = new StringBuilder();
        void Log(string s) { output.AppendLine(s); Console.WriteLine(s); }

        var procs = Process.GetProcessesByName("forzahorizon6");
        if (procs.Length == 0) procs = Process.GetProcessesByName("ForzaHorizon6");
        if (procs.Length == 0) { Log("ERROR: FH6 not found."); File.WriteAllText("scan_results.txt", output.ToString()); return; }

        var proc = procs[0];
        Log($"FH6 PID {proc.Id}");
        var handle = OpenProcess(PROCESS_ALL_ACCESS, false, proc.Id);
        if (handle == IntPtr.Zero) { Log("ERROR: OpenProcess failed."); File.WriteAllText("scan_results.txt", output.ToString()); return; }

        try
        {
            var mainModule = proc.MainModule!;
            var baseAddr = mainModule.BaseAddress;
            var modSize = mainModule.ModuleMemorySize;
            Log($"Base 0x{baseAddr.ToInt64():X}, {modSize / 1024 / 1024} MB");

            var buf = new byte[modSize];
            ReadProcessMemory(handle, baseAddr, buf, (IntPtr)modSize, out var bytesRead);
            Log($"Read {bytesRead / 1024 / 1024} MB\n");

            using var scanner = new Scanner(buf);

            // =====================================================
            // PART 1: NEW CHEAT PATTERNS (from ForzaMods AIO)
            // =====================================================
            Log("========== NEW CHEAT PATTERNS ==========\n");

            var newCheats = new (string Name, string Pattern)[]
            {
                // --- Velocity / Speed ---
                ("Speed_Hack_1",            "F3 0F 11 83 ?? 01 00 00 C3"),
                ("Speed_Hack_2",            "F3 0F 58 83 ?? 01 00 00 C3"),
                ("Velocity_X_write",        "F3 0F 11 87 48 02 00 00"),
                ("Velocity_Y_write",        "F3 0F 11 87 4C 02 00 00"),
                ("Velocity_Z_write",        "F3 0F 11 87 50 02 00 00"),
                ("CarSpeed_read",           "0F 28 83 ?? 01 00 00 F3 0F 59 C2"),

                // --- Brake / Jump ---
                ("Brake_Hack",              "F3 0F 11 9F 80 00 00 00"),
                ("Jump_Height",             "F3 0F 58 83 ?? 01 00 00 0F 57"),

                // --- Car tuning ---
                ("TireGrip_Front",          "F3 0F 11 8B ?? 01 00 00 F3 0F 10 83"),
                ("TireGrip_Rear",           "F3 0F 11 83 ?? 01 00 00 F3 0F 10 8B"),
                ("Horsepower_Override",     "F3 0F 11 83 4C 05 00 00"),
                ("Torque_Override",         "F3 0F 11 83 50 05 00 00"),

                // --- XP / Progress ---
                ("XP_Write1",               "89 87 ?? ?? 00 00 8B 47"),
                ("XP_Write2",               "89 84 ?? ?? ?? 00 00 8B 47"),
                ("Influence_Write",         "49 63 ?? 8B 44 ?? ?? C3"),
                ("SeriesPoints",            "89 87 ?? 05 00 00 48 8B"),

                // --- Camera / Photo ---
                ("CameraFOV",              "F3 0F 11 83 90 00 00 00"),
                ("CameraHeight",           "F3 0F 11 83 80 00 00 00"),
                ("PhotoMode_unlock",       "80 ?? ?? ?? 00 00 0F 84"),

                // --- Paint / Visual ---
                ("GlowingPaint",           "F3 0F 11 83 ?? 0A 00 00 0F 57"),
                ("Headlight_Color",        "F3 0F 11 83 ?? 06 00 00 0F 57"),
                ("Car_Cleanliness",        "C6 83 ?? 0A 00 00 01"),

                // --- Drone mode ---
                ("DroneMaxHeight",         "F3 0F 11 83 ?? 0C 00 00 0F 57"),

                // --- Nitrous / Boost ---
                ("Nitrous_fill",           "F3 0F 58 83 ?? 03 00 00 F3 0F"),
                ("Nitrous_drain",          "F3 0F 5C 83 ?? 03 00 00 0F 2F"),

                // --- Player position (for teleport improvement) ---
                ("PlayerPos_X_write",      "F3 0F 11 87 80 02 00 00"),
                ("PlayerPos_Y_write",      "F3 0F 11 87 84 02 00 00"),
                ("PlayerPos_Z_write",      "F3 0F 11 87 88 02 00 00"),

                // --- Game state ---
                ("RaceFinish",             "C6 83 ?? ?? 00 00 01 48 8B"),
                ("GodMode",                "0F 57 C0 F3 0F 11 83 ?? 02 00 00"),

                // --- More ForzaMods patterns ---
                ("Wheelspeed_read",        "F3 0F 10 83 ?? 01 00 00 0F 57"),
                ("RPM_read",               "F3 0F 10 83 ?? 04 00 00 F3 0F"),
                ("Gear_read",              "8B 83 ?? 04 00 00 89"),
                ("Fuel_level",             "F3 0F 10 83 ?? 05 00 00"),
                ("Damage_write",           "89 83 ?? 02 00 00 48 8B"),

                // --- FH6-specific broad patterns ---
                ("LocalPlayer_base1",      "48 8B 0D ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 89 ?? ?? 00 00"),
                ("LocalPlayer_base2",      "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B 88 ?? ?? 00 00"),
                ("ProfileStruct_base",     "48 8B 0D ?? ?? ?? ?? 48 8B D7 48 8B 01 FF 50 ?? 48 8B C8"),
            };

            int found = 0, miss = 0;
            Log($"{"PATTERN",-30} {"STATUS",-8} {"ADDRESS"}");
            Log(new string('-', 80));

            foreach (var (name, pattern) in newCheats)
            {
                var result = scanner.FindPattern(pattern);
                var hit = result.Found;
                if (hit) found++; else miss++;
                var offset = hit ? (int)result.Offset : -1;
                var addr = hit ? $"0x{baseAddr.ToInt64() + offset:X}" : "---";
                Log($"{name,-30} {(hit ? "OK" : "MISS"),-8} {addr}");

                if (hit)
                {
                    var len = Math.Min(64, buf.Length - offset);
                    Log($"  {Convert.ToHexString(buf.AsSpan(offset, len))}");
                }
            }
            Log($"\nNEW CHEATS: {found} FOUND, {miss} MISS\n");

            // =====================================================
            // PART 2: CRC / INTEGRITY CHECK PATTERNS
            // =====================================================
            Log("========== CRC / INTEGRITY MECHANISMS ==========\n");

            var crcPatterns = new (string Name, string Pattern)[]
            {
                // Common CRC32 computation patterns
                ("CRC32_table_access",     "8B 04 ?? 8D ?? ?? ?? 33 C0 8B"),
                ("CRC32_compute_1",        "C1 E0 08 33 04 ?? 8B ?? C1 E8 08"),
                ("CRC32_compute_2",        "33 D2 8B C8 C1 E8 08 8B 04 ?? 33"),
                ("CRC32_poly",             "83 E0 01 74 ?? 33 05 ?? ?? ?? ??"),

                // Memory integrity scan patterns
                ("HashScan_loop",          "48 8D ?? ?? ?? ?? ?? 48 8B ?? 48 8B ?? FF 50"),
                ("IntegrityCheck_call",    "E8 ?? ?? ?? ?? 85 C0 74 ?? 48 8B"),
                ("MemCmp_check",           "E8 ?? ?? ?? ?? 48 8B ?? 48 8B ?? E8 ?? ?? ?? ?? 85 C0"),
                ("Checksum_verify",        "48 8B ?? 48 8B ?? E8 ?? ?? ?? ?? 84 C0 74"),
                ("PageHash_start",         "48 83 EC ?? 48 8B ?? BA ?? ?? ?? ?? 48 8B"),

                // VTable integrity patterns
                ("VTable_read_funcptr",    "48 8B 01 FF 50 ?? 48 8B ?? 48 8B ?? FF 50"),
                ("VTable_swap_detect",     "48 8B ?? 48 8B ?? 48 3B ?? 74 ?? 48 89"),

                // Anti-tamper patterns
                ("CodeSection_verify",     "48 8D ?? ?? ?? ?? ?? 48 8B ?? BA ?? ?? ?? ?? E8"),
                ("TextSection_hash",       "48 8D 15 ?? ?? ?? ?? 48 8B ?? FF 15 ?? ?? ?? ??"),
                ("FuncPrologue_check",     "80 ?? ?? ?? ?? ?? 48 89 ?? 74 ?? 48 89"),
                ("RevertPatch_detect",     "48 8B ?? 48 39 ?? 74 ?? 48 89 ?? E8 ?? ?? ?? ??"),

                // Save/load patterns
                ("SaveState_write",        "48 8B ?? 48 8B ?? E8 ?? ?? ?? ?? 48 8B ?? C6 83"),
                ("ProfileWrite_call",      "E8 ?? ?? ?? ?? 48 8B ?? 48 8B ?? C6 83 ?? ?? 00 00 01"),
                ("AutoSave_trigger",       "C7 83 ?? ?? 00 00 01 00 00 00 48 8B"),
                ("RestoreOriginal_1",      "48 8B ?? 48 8B ?? 48 8B ?? E8 ?? ?? ?? ?? 90 90"),
                ("SelfHeal_writeback",     "48 8B ?? 48 8B ?? 48 89 ?? 48 89 ?? 48 89"),

                // Timer-based re-check patterns
                ("PeriodicTimer_1",        "FF 15 ?? ?? ?? ?? 48 8B ?? 48 85 C0 74 ?? FF 15"),
                ("PeriodicTimer_2",        "48 8B ?? BA ?? ?? ?? ?? FF 15 ?? ?? ?? ?? 48 8B"),
                ("Watchdog_check",         "48 8B ?? 83 ?? 01 7E ?? E8 ?? ?? ?? ?? 48 8B"),
            };

            int crcFound = 0, crcMiss = 0;
            Log($"{"PATTERN",-30} {"STATUS",-8} {"ADDRESS"}");
            Log(new string('-', 80));

            foreach (var (name, pattern) in crcPatterns)
            {
                var result = scanner.FindPattern(pattern);
                var hit = result.Found;
                if (hit) crcFound++; else crcMiss++;
                var offset = hit ? (int)result.Offset : -1;
                var addr = hit ? $"0x{baseAddr.ToInt64() + offset:X}" : "---";
                Log($"{name,-30} {(hit ? "OK" : "MISS"),-8} {addr}");

                if (hit)
                {
                    var len = Math.Min(96, buf.Length - offset);
                    Log($"  {Convert.ToHexString(buf.AsSpan(offset, len))}");
                }
            }
            Log($"\nCRC/STABILITY: {crcFound} FOUND, {crcMiss} MISS\n");

            // =====================================================
            // PART 3: FIND ALL LOCAL PLAYER REFERENCES
            // =====================================================
            Log("========== LOCAL PLAYER BASE ADDRESSES ==========\n");

            // The current CRC bypass swaps vtable[9]. Let's find all code that
            // reads vtable entries — if the game checks other vtable indices,
            // we need to know.
            var vtablePatterns = new (string Name, string Pattern)[]
            {
                // CDatabase vtable reads (index 9 = offset 0x48)
                ("VT_CDB_idx9_A",         "FF 50 48 90"),
                ("VT_CDB_idx9_B",         "FF 90 48 01 00 00"),
                ("VT_CDB_idx9_C",         "FF 50 48 48 8B"),

                // Common vtable calls in Forza
                ("VT_call_idx8",          "FF 50 40"),
                ("VT_call_idx10",         "FF 50 50"),
                ("VT_call_idx11",         "FF 50 58"),
                ("VT_call_idx12",         "FF 50 60"),

                // Functions that restore original bytes
                ("Restore_memcpy",        "E8 ?? ?? ?? ?? 90 48 8B ?? 48 8B ?? 48 89"),
                ("Patch_apply",           "48 89 ?? 48 89 ?? 48 89 ?? 48 89 ?? 90"),
            };

            foreach (var (name, pattern) in vtablePatterns)
            {
                var result = scanner.FindPattern(pattern);
                var hit = result.Found;
                var offset = hit ? (int)result.Offset : -1;
                var addr = hit ? $"0x{baseAddr.ToInt64() + offset:X}" : "---";
                Log($"{name,-30} {(hit ? "OK" : "MISS"),-8} {addr}");
                if (hit)
                {
                    var len = Math.Min(64, buf.Length - offset);
                    Log($"  {Convert.ToHexString(buf.AsSpan(offset, len))}");
                }
            }

            // =====================================================
            // PART 4: BROAD LOCALPLAYER SCAN
            // =====================================================
            Log("\n========== BROAD LOCALPLAYER/PROFILE SCAN ==========\n");

            // Search for the instruction pattern that paris' club's CRC bypass hooks
            // "48 8B 01 FF 50 48 90" = mov rax,[rcx]; call [rax+0x48]; nop
            // This is what we swap for our CRC bypass
            var crcBypassTarget = scanner.FindPattern("48 8B 01 FF 50 48 90 48 8B");
            if (crcBypassTarget.Found)
            {
                var off = (int)crcBypassTarget.Offset;
                Log($"CRC_BYPASS_TARGET at 0x{baseAddr.ToInt64() + off:X}");
                Log($"  {Convert.ToHexString(buf.AsSpan(off, 64))}");
            }
            else
            {
                Log("CRC_BYPASS_TARGET: MISS (pattern changed!)");
            }

            // Search for the two-phase dance entry point
            // The CRC bypass restores originals, waits, then re-applies
            var twoPhase = scanner.FindPattern("48 89 ?? 48 89 ?? 48 89 ?? 48 89 ?? C7 83");
            if (twoPhase.Found)
            {
                var off = (int)twoPhase.Offset;
                Log($"TWO_PHASE_ENTRY at 0x{baseAddr.ToInt64() + off:X}");
                Log($"  {Convert.ToHexString(buf.AsSpan(off, 80))}");
            }

            Log($"\nScan complete.");
        }
        finally { CloseHandle(handle); }

        File.WriteAllText("scan_results.txt", output.ToString());
        Console.WriteLine("\nSaved to scan_results.txt");
    }
}
