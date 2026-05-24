using System;
using System.Collections.Generic;
using System.Threading;

namespace FH6Mod.Cheats.RuntimeHook;

/// <summary>
/// Resolves profile field struct offsets and base address, then writes values
/// directly to the profile struct in game memory. No .text modification needed
/// for the four profile fields (Credits, Wheelspins, SuperWheelspins, SkillPoints).
/// </summary>
internal sealed class FnvProfileResolver : IDisposable
{
    private readonly RuntimeHookEngine _engine;
    private readonly Dictionary<RuntimeProfileFeature, ResolvedField> _fields = new();
    private ulong _structBase;
    private bool _structBaseResolved;
    private Timer? _lockTimer;
    private int _timerRunning;
    private static readonly Random _jitter = new();
    private bool _disposed;

    internal sealed class ResolvedField
    {
        public int StructOffset;
        public int DesiredValue;
        public bool Active;
    }

    public FnvProfileResolver(RuntimeHookEngine engine)
    {
        _engine = engine;
    }

    public ulong StructBase => _structBase;
    public bool IsResolved => _structBaseResolved;

    // ===== Resolution =====

    /// <summary>
    /// Finds the profile struct base address by tracing the pointer chain
    /// from global pointers found in callers of setter functions.
    /// </summary>
    public bool ResolveStructBase(byte[] moduleBytes)
    {
        if (_structBaseResolved) return true;

        // Find the Credits setter function address
        var setterRva = FindSetterRva(moduleBytes, RuntimeProfileFeature.Credits);
        if (setterRva < 0)
        {
            _engine.LogPublic("FNV: Credits setter not found, cannot resolve struct base");
            return false;
        }

        _engine.LogPublic($"FNV: Credits setter instruction at RVA 0x{setterRva:X}");

        // Find the function entry point (callers target the function start, not the instruction)
        var funcStartRva = FindFunctionStart(moduleBytes, setterRva);
        _engine.LogPublic($"FNV: Credits setter function starts at RVA 0x{funcStartRva:X}");

        // Scan .text for E8 calls to the setter function, then look for global pointer loads
        var callerGlobals = FindCallerGlobals(moduleBytes, funcStartRva);
        if (callerGlobals.Count == 0)
        {
            _engine.LogPublic("FNV: No callers with global pointer loads found");
            return false;
        }

        // Try each global candidate at runtime
        foreach (var (globalAddr, derefOffset) in callerGlobals)
        {
            var candidate = TryDereferenceGlobal(globalAddr, derefOffset);
            if (candidate != 0 && ValidateStructBase(candidate))
            {
                _structBase = candidate;
                _structBaseResolved = true;
                _engine.LogPublic($"FNV: Profile struct base = 0x{_structBase:X} (global 0x{globalAddr:X}+0x{derefOffset:X})");
                return true;
            }
        }

        _engine.LogPublic($"FNV: Tried {callerGlobals.Count} global candidates, none validated");
        return false;
    }

    /// <summary>
    /// Resolves a profile field's struct offset by finding its setter via AOB
    /// and extracting the disp32 from the add/mov [rbx+X], eax instruction.
    /// </summary>
    public bool ResolveField(RuntimeProfileFeature feature, byte[] moduleBytes)
    {
        if (_fields.ContainsKey(feature)) return true;

        var desc = ProfileFeatureCatalog.Get(feature);
        var offset = ExtractOffsetFromSignature(moduleBytes, desc);
        if (offset < 0)
        {
            _engine.LogPublic($"FNV: Could not extract struct offset for {desc.Name}");
            return false;
        }

        _fields[feature] = new ResolvedField { StructOffset = offset };
        _engine.LogPublic($"FNV: {desc.Name} offset = 0x{offset:X}");
        return true;
    }

    /// <summary>
    /// Combines struct base + field offset resolution. Returns true if the field
    /// is ready for direct writes.
    /// </summary>
    public bool TryResolve(RuntimeProfileFeature feature, byte[] moduleBytes)
    {
        if (!_structBaseResolved && !ResolveStructBase(moduleBytes)) return false;
        return ResolveField(feature, moduleBytes);
    }

    // ===== Read/Write =====

    public int ReadValue(RuntimeProfileFeature feature)
    {
        if (!_fields.TryGetValue(feature, out var field) || !_structBaseResolved)
            throw new InvalidOperationException($"{feature} not resolved");
        var addr = _structBase + (ulong)field.StructOffset;
        return _engine.ReadInt32Public(addr);
    }

    public void WriteValue(RuntimeProfileFeature feature, int value)
    {
        if (!_fields.TryGetValue(feature, out var field) || !_structBaseResolved)
            throw new InvalidOperationException($"{feature} not resolved");
        var addr = _structBase + (ulong)field.StructOffset;
        _engine.WriteInt32Public(addr, value);
    }

    // ===== Value Lock =====

    public void StartLock(RuntimeProfileFeature feature, int value, int periodMs = 5000)
    {
        if (!_fields.TryGetValue(feature, out var field)) return;
        field.DesiredValue = value;
        field.Active = true;

        // Write immediately
        try { WriteValue(feature, value); }
        catch { /* will retry on timer tick */ }

        EnsureTimerStarted(periodMs);
    }

    public void StopLock(RuntimeProfileFeature feature)
    {
        if (_fields.TryGetValue(feature, out var field))
            field.Active = false;

        // If no fields are active, stop timer
        foreach (var f in _fields.Values)
            if (f.Active) return;

        StopTimer();
    }

    public bool IsFieldActive(RuntimeProfileFeature feature)
        => _fields.TryGetValue(feature, out var f) && f.Active;

    // ===== Timer =====

    private void EnsureTimerStarted(int periodMs)
    {
        if (_lockTimer != null) return;
        _lockTimer = new Timer(LockTimerTick, null, 1000, Timeout.Infinite);
    }

    private void StopTimer()
    {
        var t = _lockTimer;
        _lockTimer = null;
        try { t?.Dispose(); } catch { }
    }

    private void LockTimerTick(object? _)
    {
        if (Interlocked.Exchange(ref _timerRunning, 1) == 1) return;
        try
        {
            if (!_engine.IsAttached || !_structBaseResolved) return;

            // Validate struct base is still good
            if (!ValidateStructBase(_structBase))
            {
                _engine.LogPublic("FNV: Struct base invalid, attempting re-resolution");
                _structBaseResolved = false;
                var moduleBytes = _engine.ReadBytesPublic(_engine.MainBase, _engine.MainSize);
                if (moduleBytes.Length > 0 && ResolveStructBase(moduleBytes))
                    _engine.LogPublic($"FNV: Re-resolved struct base = 0x{_structBase:X}");
                else
                {
                    _engine.LogPublic("FNV: Re-resolution failed");
                    return;
                }
            }

            foreach (var kvp in _fields)
            {
                if (!kvp.Value.Active) continue;
                try
                {
                    var addr = _structBase + (ulong)kvp.Value.StructOffset;
                    var current = _engine.ReadInt32Public(addr);
                    if (current != kvp.Value.DesiredValue)
                        _engine.WriteInt32Public(addr, kvp.Value.DesiredValue);
                }
                catch { /* struct may have moved, will re-resolve next tick */ }
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _timerRunning, 0);
            if (!_disposed)
            {
                try
                {
                    var nextMs = 5000 + _jitter.Next(-1000, 1001);
                    _lockTimer?.Change(nextMs, Timeout.Infinite);
                }
                catch { }
            }
        }
    }

    // ===== Resolution helpers =====

    /// <summary>
    /// Finds the RVA (relative virtual address) of a setter function by AOB scanning.
    /// Returns -1 if not found.
    /// </summary>
    private int FindSetterRva(byte[] moduleBytes, RuntimeProfileFeature feature)
    {
        var desc = ProfileFeatureCatalog.Get(feature);
        var sigs = new List<(string Sig, string Label)> { (desc.Signature, "primary") };
        foreach (var alt in desc.AltSignatures)
            sigs.Add((alt, "alt"));

        foreach (var (sig, label) in sigs)
        {
            var pattern = Pattern.Parse(sig);
            foreach (var off in Pattern.FindAll(moduleBytes, pattern, 128))
            {
                // Validate context pattern if set
                if (!string.IsNullOrEmpty(desc.ContextPattern) && !HasContextPattern(moduleBytes, off, desc.ContextPattern))
                    continue;

                // Read the bytes at the match to validate they're a real setter
                var hookAddr = (ulong)((long)_engine.MainBase + off + desc.MatchOffset);
                var original = _engine.ReadBytesPublic(hookAddr, desc.HookSize);
                if (original.Length < desc.HookSize) continue;

                // Skip already-patched targets
                if (original.Length > 0 && original[0] == 0xE9) continue;

                // Skip if already hooked by another cheat
                if (_engine.IsAddressHooked(hookAddr)) continue;

                return off + desc.MatchOffset;
            }
        }
        return -1;
    }

    /// <summary>
    /// Extracts the struct offset from a setter instruction at the AOB match.
    /// </summary>
    private int ExtractOffsetFromSignature(byte[] moduleBytes, RuntimeProfileHookDescriptor desc)
    {
        var sigs = new List<(string Sig, string Label)> { (desc.Signature, "primary") };
        foreach (var alt in desc.AltSignatures)
            sigs.Add((alt, "alt"));

        foreach (var (sig, label) in sigs)
        {
            var pattern = Pattern.Parse(sig);
            foreach (var off in Pattern.FindAll(moduleBytes, pattern, 128))
            {
                if (!string.IsNullOrEmpty(desc.ContextPattern) && !HasContextPattern(moduleBytes, off, desc.ContextPattern))
                    continue;

                var hookAddr = (ulong)((long)_engine.MainBase + off + desc.MatchOffset);
                var original = _engine.ReadBytesPublic(hookAddr, 6);
                if (original.Length < 6) continue;

                // 89 83 XX XX XX XX = MOV [rbx+disp32], eax
                // 01 83 XX XX XX XX = ADD [rbx+disp32], eax
                if ((original[0] == 0x89 || original[0] == 0x01) && original[1] == 0x83)
                    return BitConverter.ToInt32(original, 2);
            }
        }
        return -1;
    }

    /// <summary>
    /// Scans .text for E8 calls to a target RVA, then searches backwards in
    /// the caller for RIP-relative global pointer loads.
    /// Returns (globalAddress, derefOffset) candidates.
    /// </summary>
    private List<(ulong GlobalAddr, int DerefOffset)> FindCallerGlobals(byte[] moduleBytes, int targetRva)
    {
        var results = new List<(ulong, int)>();
        var targetVa = _engine.MainBase + (ulong)targetRva;
        var callers = new HashSet<ulong>();

        // Scan for E8 XX XX XX XX that calls our target
        for (var i = 0; i < moduleBytes.Length - 5; i++)
        {
            if (moduleBytes[i] != 0xE8) continue;
            var rel = BitConverter.ToInt32(moduleBytes, i + 1);
            var callAddr = _engine.MainBase + (ulong)i;
            var callTarget = callAddr + 5 + (ulong)rel;
            if (callTarget != targetVa) continue;

            // Found a caller at offset i. Find the function start.
            var callerFuncStart = FindFunctionStart(moduleBytes, i);
            if (callerFuncStart < 0) continue;
            var callerVa = _engine.MainBase + (ulong)callerFuncStart;

            if (callers.Contains(callerVa)) continue;
            callers.Add(callerVa);

            // Search backwards from the E8 call for global pointer loads
            var searchStart = Math.Max(callerFuncStart, i - 128);
            for (var j = i - 7; j >= searchStart; j--)
            {
                if (j + 7 > moduleBytes.Length) continue;

                // mov rax, [rip+disp32]: 48 8B 05 XX XX XX XX
                if (moduleBytes[j] == 0x48 && moduleBytes[j + 1] == 0x8B && moduleBytes[j + 2] == 0x05)
                {
                    var disp = BitConverter.ToInt32(moduleBytes, j + 3);
                    var instrVa = _engine.MainBase + (ulong)j;
                    var globalAddr = instrVa + 7 + (ulong)disp;

                    // Check if there's a mov rcx, [rax+X] or mov rcx, rax after this
                    int derefOff = 0;
                    for (var k = j + 7; k < i; k++)
                    {
                        // mov rcx, [rax+XX]: 48 8B 48 XX (4 bytes, signed byte offset)
                        if (k + 4 <= moduleBytes.Length && moduleBytes[k] == 0x48 && moduleBytes[k + 1] == 0x8B && moduleBytes[k + 2] == 0x48)
                        {
                            derefOff = (sbyte)moduleBytes[k + 3];
                            break;
                        }
                        // mov rcx, [rax+disp32]: 48 8B 88 XX XX XX XX (7 bytes)
                        if (k + 7 <= moduleBytes.Length && moduleBytes[k] == 0x48 && moduleBytes[k + 1] == 0x8B && moduleBytes[k + 2] == 0x88)
                        {
                            derefOff = BitConverter.ToInt32(moduleBytes, k + 3);
                            break;
                        }
                        // mov rcx, rax: 48 8B C8 (3 bytes, no dereference)
                        if (k + 3 <= moduleBytes.Length && moduleBytes[k] == 0x48 && moduleBytes[k + 1] == 0x8B && moduleBytes[k + 2] == 0xC8)
                        {
                            derefOff = 0;
                            break;
                        }
                    }

                    results.Add((globalAddr, derefOff));
                }

                // mov rcx, [rip+disp32]: 48 8B 0D XX XX XX XX
                if (moduleBytes[j] == 0x48 && moduleBytes[j + 1] == 0x8B && moduleBytes[j + 2] == 0x0D)
                {
                    var disp = BitConverter.ToInt32(moduleBytes, j + 3);
                    var instrVa = _engine.MainBase + (ulong)j;
                    var globalAddr = instrVa + 7 + (ulong)disp;
                    results.Add((globalAddr, 0));
                }
            }
        }

        _engine.LogPublic($"FNV: Found {callers.Count} callers, {results.Count} global candidates");
        return results;
    }

    /// <summary>
    /// Dereferences a global pointer at runtime and returns the candidate struct base.
    /// Returns 0 on failure.
    /// </summary>
    private ulong TryDereferenceGlobal(ulong globalAddr, int derefOffset)
    {
        try
        {
            var ptr = _engine.ReadUInt64Public(globalAddr);
            if (ptr == 0) return 0;
            var candidate = ptr + (ulong)derefOffset;
            return candidate;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Validates that a candidate struct base looks like a real profile struct
    /// by checking that known offsets have reasonable values.
    /// </summary>
    private bool ValidateStructBase(ulong candidate)
    {
        try
        {
            // Check a few known offsets for reasonable int32 values
            // Profile values should be non-negative and not absurdly large
            for (var i = 0; i < 0x300; i += 4)
            {
                var val = _engine.ReadInt32Public(candidate + (ulong)i);
                if (val < 0 || val > 2_000_000_000) continue;
                // Found at least one reasonable value
            }

            // Try to read the Credits field specifically
            if (_fields.TryGetValue(RuntimeProfileFeature.Credits, out var creditsField))
            {
                var creditsVal = _engine.ReadInt32Public(candidate + (ulong)creditsField.StructOffset);
                if (creditsVal >= 0 && creditsVal <= 2_000_000_000)
                    return true;
            }

            // If Credits offset not resolved yet, accept any candidate with
            // at least 3 reasonable int32 values in the first 0x200 bytes
            var reasonableCount = 0;
            for (var i = 0; i < 0x200; i += 4)
            {
                var val = _engine.ReadInt32Public(candidate + (ulong)i);
                if (val >= 0 && val <= 2_000_000_000)
                    reasonableCount++;
            }
            return reasonableCount >= 3;
        }
        catch { return false; }
    }

    /// <summary>
    /// Finds function start by scanning backwards for CC padding.
    /// </summary>
    private static int FindFunctionStart(byte[] data, int offset)
    {
        var start = Math.Max(0, offset - 0x400);
        for (var pos = offset; pos > start; pos--)
        {
            if (data[pos] == 0xCC && pos + 1 < data.Length && data[pos + 1] == 0xCC)
            {
                while (pos < offset && data[pos] == 0xCC)
                    pos++;
                return pos;
            }
        }
        return start;
    }

    /// <summary>
    /// In-place context pattern validation. Same logic as RuntimeHookEngine.HasContextPattern.
    /// </summary>
    private static bool HasContextPattern(byte[] data, int matchOffset, string contextPattern)
    {
        var ctx = Pattern.Parse(contextPattern);
        var searchStart = Math.Max(0, matchOffset - 256);
        var searchEnd = matchOffset - ctx.Length;
        if (searchEnd < searchStart) return false;
        for (var i = searchStart; i <= searchEnd; i++)
        {
            var match = true;
            for (var j = 0; j < ctx.Length; j++)
            {
                if (ctx[j] != -1 && data[i + j] != ctx[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    // ===== Cleanup =====

    public void Dispose()
    {
        _disposed = true;
        StopTimer();
    }
}
