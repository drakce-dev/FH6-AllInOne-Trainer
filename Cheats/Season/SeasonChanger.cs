using System;
using FH6Mod.Cheats.RuntimeHook;

namespace FH6Mod.Cheats.Season;

/// <summary>
/// Changes the current visual season by writing to the season controller entity.
/// Uses AOB scan to find the entity global pointer, then writes the season value.
///
/// Season enum: 0=Spring, 1=Summer, 2=Autumn, 3=Winter
/// Entity layout (from reverse engineering):
///   +0x278 = Season value (int32: 0-3)
///   +0x2D8 = Visual update flag 1
///   +0x2D9 = Visual update flag 2
///   +0x2DA = Visual update flag 3
/// </summary>
public sealed class SeasonChanger
{
    private readonly RuntimeHookEngine _engine;
    private ulong _entityPtrAddr;
    private bool _resolved;

    public enum FHSeason
    {
        Spring = 0,
        Summer = 1,
        Autumn = 2,
        Winter = 3,
    }

    // AOB: MOV RCX,[rip+disp] / CALL / TEST AL,AL / JNZ +0F / LEA RDX,["SeasonSettings Loaded"] / MOV RCX,RDI
    // This is the season entity pointer load near the "SeasonSettings Loaded" log string.
    private const string SeasonEntitySig =
        "48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 0F 48 8D 15 ?? ?? ?? ?? 48 8B CF";

    private const int DispOffset = 3;

    // Alternative patterns for different builds
    private static readonly string[] AltSigs =
    [
        // Variant with JNZ +0x0F but different surrounding code
        "48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 ?? ?? 48 8D 15 ?? ?? ?? ?? 48 8B CF E8",
        // Direct: look for the season value read pattern (entity+0x278) near loading messages
        "41 8B 9E 78 02 00 00 33 D2 49 8B 8E 80 02 00 00 E8",
    ];

    private const int SeasonValueOffset = 0x278;

    public SeasonChanger(RuntimeHookEngine engine) => _engine = engine;

    public bool IsResolved => _resolved;

    public bool Resolve(out string? error)
    {
        error = null;
        if (_resolved) return true;
        if (!_engine.IsAttached) { error = "Not attached."; return false; }

        var mainBase = _engine.MainBase;
        var mainSize = _engine.MainSize;
        if (mainBase == 0 || mainSize <= 0) { error = "Main module not captured."; return false; }

        var moduleBytes = _engine.ReadBytesPublic(mainBase, mainSize);
        if (moduleBytes.Length == 0) { error = "Could not read main module."; return false; }

        // Try primary AOB
        var found = TryResolveWithSig(moduleBytes, mainBase, SeasonEntitySig, DispOffset, ref error);

        // Try alternatives
        if (!found)
        {
            foreach (var altSig in AltSigs)
            {
                found = TryResolveWithSig(moduleBytes, mainBase, altSig, DispOffset, ref error);
                if (found) break;
            }
        }

        if (found)
        {
            _resolved = true;
            var entityPtr = _engine.ReadUInt64Public(_entityPtrAddr);
            var season = entityPtr != 0 ? _engine.ReadInt32Public(entityPtr + SeasonValueOffset) : -1;
            _engine.LogPublic($"Season entity resolved: global=0x{_entityPtrAddr:X}, entity=0x{entityPtr:X}, currentSeason={SeasonName(season)} ({season})");
        }

        return found;
    }

    private bool TryResolveWithSig(byte[] moduleBytes, ulong mainBase, string sig, int dispOffset, ref string? error)
    {
        var pattern = Pattern.Parse(sig);
        foreach (var off in Pattern.FindAll(moduleBytes, pattern, 4))
        {
            var matchAddr = mainBase + (ulong)off;

            // Read the displacement from the MOV RCX,[rip+disp32] instruction
            var dispBytes = new byte[4];
            Buffer.BlockCopy(moduleBytes, off + dispOffset, dispBytes, 0, 4);
            var disp = BitConverter.ToInt32(dispBytes, 0);

            // Resolve: global_addr = matchAddr + 7 (instruction length) + disp
            var globalAddr = (ulong)((long)(matchAddr + 7) + disp);

            // Validate: read the entity pointer from the global
            var entityPtr = _engine.ReadUInt64Public(globalAddr);
            if (entityPtr == 0)
            {
                _engine.LogPublic($"Season entity: global at 0x{globalAddr:X} has null pointer (entity not yet created?)");
                // Still accept it — entity might be created later
            }

            // If entity exists, validate by reading the season value
            if (entityPtr != 0)
            {
                var season = _engine.ReadInt32Public(entityPtr + SeasonValueOffset);
                if (season < 0 || season > 3)
                {
                    _engine.LogPublic($"Season entity: value at entity+0x278 = {season} (out of range 0-3), skipping");
                    continue;
                }
            }

            _entityPtrAddr = globalAddr;
            return true;
        }

        error = $"Season entity AOB not found (tried primary + {AltSigs.Length} alts).";
        return false;
    }

    /// <summary>
    /// Get the current season value (0-3) or -1 if unavailable.
    /// </summary>
    public int GetCurrentSeason()
    {
        if (!_resolved || _entityPtrAddr == 0) return -1;
        var entityPtr = _engine.ReadUInt64Public(_entityPtrAddr);
        if (entityPtr == 0) return -1;
        return _engine.ReadInt32Public(entityPtr + SeasonValueOffset);
    }

    /// <summary>
    /// Set the visual season. After setting, fast-travel or load a race
    /// to force the game to refresh the season visuals.
    /// </summary>
    public bool SetSeason(FHSeason season, out string? error)
    {
        error = null;
        if (!_resolved && !Resolve(out error)) return false;

        var entityPtr = _engine.ReadUInt64Public(_entityPtrAddr);
        if (entityPtr == 0)
        {
            error = "Season entity pointer is null — the game may not have loaded yet.";
            return false;
        }

        var seasonAddr = entityPtr + SeasonValueOffset;
        var current = _engine.ReadInt32Public(seasonAddr);

        if (current == (int)season)
        {
            _engine.LogPublic($"Season already set to {season}.");
            return true;
        }

        _engine.WriteInt32Public(seasonAddr, (int)season);
        _engine.LogPublic($"Season changed: {SeasonName(current)} ({current}) -> {season} ({(int)season})");

        // Try to nudge visual update by writing to the flag offsets near 0x2D8
        // These flags are checked in the SeasonSettings initialization path
        NudgeVisualUpdate(entityPtr);

        return true;
    }

    private void NudgeVisualUpdate(ulong entityPtr)
    {
        // Write 1 to flags at 0x2D8, 0x2D9, 0x2DA to try to trigger visual update
        // These are checked in the SeasonSettings loaded function
        for (ulong off = 0x2D8; off <= 0x2DA; off++)
        {
            try
            {
                var current = _engine.ReadBytesPublic(entityPtr + off, 1);
                if (current.Length > 0 && current[0] == 0)
                {
                    _engine.WriteBytesPublic(entityPtr + off, [1]);
                    _engine.LogPublic($"Season: set visual flag at entity+0x{off:X}");
                }
            }
            catch { /* flag offsets may vary between builds */ }
        }
    }

    public static string SeasonName(int season) => season switch
    {
        0 => "Spring",
        1 => "Summer",
        2 => "Autumn",
        3 => "Winter",
        _ => $"Unknown({season})",
    };

    public void Reset()
    {
        _entityPtrAddr = 0;
        _resolved = false;
    }
}
