using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The shrines of the client data, read the way PassiveBuffAuras reads a passive's buff: a
/// <c>Shrines</c> row's <c>Player_ShrineBuffsKey</c> is a BuffTemplates row, whose <c>BuffDefinitionsKey</c> is the
/// BuffDefinitions row the client shows, and whose <c>Buff_StatValues</c> are that definition's <c>GrantedStats</c>
/// values, in order (a Stats row + 1 is the client key). Duration, the lesser flag, shared charges and the sound are
/// the Shrines row's own. A template whose values do not line up with its definition's stats is skipped rather than
/// misapplied (PassiveBuffAuras does the same).
/// <para><c>Monster_ShrineBuffsKey</c> is read the same way: the shrine's buff on the monsters it guards, which is not
/// the player's buff (Acceleration is −60 action speed and 100 projectile speed against the player's −50 and 80;
/// Massive 60% life and 155% area against 40 and 40). <c>MonsterPacks</c> names the pack rows that guard the shrine:
/// only the boss and influence shrines have them in the data (Brine King, Lunaris, Solaris, Arakaali, the four
/// Conqueror shrines and Faridun); the standard map shrines are guarded by the area's own packs, not by a shrine row.</para>
/// </summary>
public static class ShrineCatalog
{
    /// <summary>A pack row that guards a shrine (Shrines.MonsterPacks → MonsterPacks): its row, Id, spawn count and
    /// the MonsterVarieties rows it rolls (MonsterPackEntries: MonsterPacksKey → MonsterVarietiesKey, with a weight).</summary>
    public sealed record GuardPack(int Row, string Id, int MinCount, int MaxCount,
        IReadOnlyList<(ushort Variety, int Weight)> Monsters);

    /// <summary>The buff a shrine puts on monsters: its BuffTemplates row and Id, the BuffDefinitions row, and values.</summary>
    public sealed record MonsterBuff(ushort TemplateRow, string Template, ushort BuffRow, string BuffId,
        IReadOnlyList<(uint Key, int Value)> Stats);

    /// <summary>One shrine type: its Shrines row, the buffs it grants the player and monsters, and its guard packs.</summary>
    public sealed record Shrine(int Row, string Id, int Duration, bool Lesser, bool ChargesShared, bool PvPOnly, int Sound,
        ushort BuffRow, string BuffId, IReadOnlyList<(uint Key, int Value)> Stats,
        MonsterBuff? Monster, IReadOnlyList<GuardPack> GuardPacks);

    static readonly Lazy<(Shrine[] Rows, string[] Skipped)> Data = new(() => Load("data"));

    /// <summary>Every shrine type, by Shrines row. Rows without a resolvable player buff template are left out.</summary>
    public static IReadOnlyList<Shrine> All => Data.Value.Rows;

    /// <summary>The Shrines rows left out because their template values did not match, for the log.</summary>
    public static IReadOnlyList<string> Skipped => Data.Value.Skipped;

    /// <summary>The shrine type with this Shrines row id (Acceleration, GodMode, …), or null.</summary>
    public static Shrine? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The basic shrines, by Shrines row id: the fifteen of the wiki's list. Several data ids differ from the
    /// names the wiki uses (Diamond is Criticals, Echoing Multi, Divine GodMode, Freezing Cold, Impenetrable Defence,
    /// Replenishing Regen, Shrouded Smoke, Skeletal Skeletons). The legacy rows (Fire, Arc, Thorns, Curses, Greed, Vaal,
    /// UpgradeOnKill), the PVP, CTF, Royale, blood and Evolving ones and the apparition and boss shrines are not in it.</summary>
    static readonly string[] BasicIds =
    [
        "Acceleration", "Lightning", "Cold", "Criticals", "Brutal", "Regen", "Massive", "Defence", "Resistance",
        "GodMode", "Multi", "Skeletons", "Smoke", "Resonating", "Gloom",
    ];

    /// <summary>The apparition shrines, each guarded by one of the Elderslayers' or the Pantheon's apparitions: they come
    /// with the Domination Scarab of Apparitions, which is not implemented yet, so a map does not roll them. Their rows,
    /// buffs, guard packs and the Solaris caster's burning ground stay in the catalog for that scarab.</summary>
    public static readonly string[] ApparitionIds =
        ["BrineKing", "Lunaris", "Solaris", "Arakaali", "Warlord", "Crusader", "Redeemer", "Hunter"];

    /// <summary>Every type a map may roll: the basic shrines. A lesser shrine is The Gull's Create Lesser Shrine skill
    /// and a greater one is a summoned shrine daemon (its Shrines row has Duration 0), so neither is a map shrine
    /// room's type; the apparition shrines wait for their scarab (<see cref="ApparitionIds"/>).</summary>
    public static IReadOnlyList<Shrine> Standard => [.. All.Where(s => BasicIds.Contains(s.Id))];

    static (Shrine[], string[]) Load(string folder)
    {
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        var skipped = new List<string>();
        if (new[] { "Shrines", "BuffTemplates", "BuffDefinitions" }.Any(t => !ClientTables.Exists(Table(t))))
            return ([], [.. skipped]);

        using var shrines = JsonDocument.Parse(ClientTables.ReadAllBytes(Table("Shrines")));
        using var templates = JsonDocument.Parse(ClientTables.ReadAllBytes(Table("BuffTemplates")));
        using var definitions = JsonDocument.Parse(ClientTables.ReadAllBytes(Table("BuffDefinitions")));
        using var packs = ClientTables.Exists(Table("MonsterPacks"))
            ? JsonDocument.Parse(ClientTables.ReadAllBytes(Table("MonsterPacks"))) : null;
        using var entries = ClientTables.Exists(Table("MonsterPackEntries"))
            ? JsonDocument.Parse(ClientTables.ReadAllBytes(Table("MonsterPackEntries"))) : null;
        var packMonsters = new Dictionary<int, List<(ushort Variety, int Weight)>>();
        if (entries is not null)
            foreach (var e in entries.RootElement.EnumerateArray())
            {
                if (e.GetProperty("MonsterPacksKey") is not { ValueKind: JsonValueKind.Number } p
                    || e.GetProperty("MonsterVarietiesKey") is not { ValueKind: JsonValueKind.Number } v) continue;
                if (!packMonsters.TryGetValue(p.GetInt32(), out var list)) packMonsters[p.GetInt32()] = list = [];
                list.Add(((ushort)v.GetInt32(), e.GetProperty("Weight").GetInt32()));
            }

        // A template row → its definition row, Id and the definition's GrantedStats with the template's values.
        (ushort Row, string Id, List<(uint Key, int Value)> Stats)? Resolve(int templateRow, string shrineId, string which)
        {
            if (templateRow < 0 || templateRow >= templates.RootElement.GetArrayLength())
            {
                skipped.Add($"{shrineId}: {which} template row {templateRow} out of range");
                return null;
            }
            var t = templates.RootElement[templateRow];
            if (!t.TryGetProperty("BuffDefinitionsKey", out var def) || def.ValueKind != JsonValueKind.Number)
            {
                skipped.Add($"{shrineId}: {which} template {templateRow} has no definition");
                return null;
            }
            int definitionRow = def.GetInt32();
            if (definitionRow < 0 || definitionRow >= definitions.RootElement.GetArrayLength())
            {
                skipped.Add($"{shrineId}: {which} definition row {definitionRow} out of range");
                return null;
            }
            var d = definitions.RootElement[definitionRow];
            var granted = d.GetProperty("GrantedStats").EnumerateArray().Select(v => v.GetInt32()).ToList();
            var values = t.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32()).ToList();
            if (granted.Count != values.Count)
            {
                skipped.Add($"{shrineId}: {which} {granted.Count} stat(s) against {values.Count} value(s)");
                return null;
            }
            return ((ushort)definitionRow, d.GetProperty("Id").GetString() ?? "",
                [.. granted.Zip(values, (stat, value) => ((uint)(stat + 1), value))]);
        }

        var rows = new List<Shrine>();
        for (int row = 0; row < shrines.RootElement.GetArrayLength(); row++)
        {
            var r = shrines.RootElement[row];
            if (r.GetProperty("Id").GetString() is not { Length: > 0 } id) continue;
            if (!r.TryGetProperty("Player_ShrineBuffsKey", out var key) || key.ValueKind != JsonValueKind.Number)
            {
                skipped.Add($"{id}: no player buff template");
                continue;
            }
            if (Resolve(key.GetInt32(), id, "player") is not { } player)
            {
                skipped.Add($"{id}: player buff does not resolve");
                continue;
            }

            MonsterBuff? monster = null;
            if (r.TryGetProperty("Monster_ShrineBuffsKey", out var monsterRow) && monsterRow.ValueKind == JsonValueKind.Number)
            {
                int mr = monsterRow.GetInt32();
                if (mr is >= 0 and <= ushort.MaxValue && Resolve(mr, id, "monster") is { } resolved)
                    monster = new MonsterBuff((ushort)mr, templates.RootElement[mr].GetProperty("Id").GetString() ?? "",
                        resolved.Row, resolved.Id, resolved.Stats);
            }

            var guards = new List<GuardPack>();
            if (packs is not null && r.TryGetProperty("MonsterPacks", out var guardRows) && guardRows.ValueKind == JsonValueKind.Array)
                foreach (var g in guardRows.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.Number) continue;
                    int gr = g.GetInt32();
                    if (gr < 0 || gr >= packs.RootElement.GetArrayLength()) continue;
                    var p = packs.RootElement[gr];
                    guards.Add(new GuardPack(gr, p.GetProperty("Id").GetString() ?? "",
                        p.GetProperty("MinCount").GetInt32(), p.GetProperty("MaxCount").GetInt32(),
                        packMonsters.GetValueOrDefault(gr) ?? []));
                }

            rows.Add(new Shrine(row, id, r.GetProperty("Duration").GetInt32(), r.GetProperty("IsLesserShrine").GetBoolean(),
                r.GetProperty("ChargesShared").GetBoolean(), r.GetProperty("IsPVPOnly").GetBoolean(),
                r.TryGetProperty("ShrineSoundsKey", out var sound) && sound.ValueKind == JsonValueKind.Number ? sound.GetInt32() : 0,
                player.Row, player.Id, player.Stats, monster, guards));
        }
        return ([.. rows], [.. skipped]);
    }
}
