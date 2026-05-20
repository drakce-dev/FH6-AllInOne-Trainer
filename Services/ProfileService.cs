using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FH6Mod.Cheats.RuntimeHook;
using FH6Mod.Cheats.Sql;

namespace FH6Mod.Services;

public sealed class ProfileService
{
    public static readonly string ProfilesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FH6AllInOneTrainer", "profiles");

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FH6AllInOneTrainer", "last_profile.json");

    public List<string> ListProfiles()
    {
        var list = new List<string>();
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            foreach (var f in Directory.GetFiles(ProfilesDir, "*.json"))
                list.Add(Path.GetFileNameWithoutExtension(f));
        }
        catch { }
        list.Sort();
        return list;
    }

    public void Save(string name, CheatProfile profile)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            var node = new JsonObject { ["name"] = name };
            var cheats = new JsonObject();
            foreach (var kv in profile.Features)
            {
                var entry = new JsonObject { ["enabled"] = kv.Value.Enabled };
                if (kv.Value.Value.HasValue)
                    entry["value"] = kv.Value.Value.Value;
                cheats[kv.Key.ToString()] = entry;
            }
            node["features"] = cheats;
            var sql = new JsonObject();
            foreach (var kv in profile.SqlLocks)
                sql[kv.Key.ToString()] = kv.Value;
            node["sqlLocks"] = sql;
            File.WriteAllText(Path.Combine(ProfilesDir, name + ".json"),
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public CheatProfile? Load(string name)
    {
        try
        {
            var path = Path.Combine(ProfilesDir, name + ".json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var profile = new CheatProfile();
            if (root.TryGetProperty("features", out var feats))
            {
                foreach (var prop in feats.EnumerateObject())
                {
                    if (!Enum.TryParse<RuntimeProfileFeature>(prop.Name, out var f)) continue;
                    var enabled = prop.Value.TryGetProperty("enabled", out var e) && e.GetBoolean();
                    int? val = null;
                    if (prop.Value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                        val = v.GetInt32();
                    profile.Features[f] = new CheatState { Enabled = enabled, Value = val };
                }
            }
            if (root.TryGetProperty("sqlLocks", out var locks_))
            {
                foreach (var prop in locks_.EnumerateObject())
                {
                    if (!Enum.TryParse<SqlFeature>(prop.Name, out var f)) continue;
                    profile.SqlLocks[f] = prop.Value.GetBoolean();
                }
            }
            return profile;
        }
        catch { return null; }
    }

    public void Delete(string name)
    {
        try
        {
            var path = Path.Combine(ProfilesDir, name + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}

public sealed class CheatProfile
{
    public Dictionary<RuntimeProfileFeature, CheatState> Features = new();
    public Dictionary<SqlFeature, bool> SqlLocks = new();
}

public sealed class CheatState
{
    public bool Enabled;
    public int? Value;
}
