using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The shrines of a generated map: the level generator's shrine rooms place <c>Metadata/Shrines/Shrine</c> lines, and
/// <see cref="ShrinePlacement"/> keeps the ones on walkable ground and rolls their types. Each is created as a 0x2bb
/// object with <see cref="ShrineObject"/>.
/// <para>A click (C→S 0x24 → <see cref="IModuleContext.OnObjectUsed"/>) is answered only for an object this module
/// created, only while the player stands in the shrine's walk-up range, and only once: the object leaves the map's set
/// on the successful click, so a repeated request grants nothing. The type is the placement's own row, so one shrine
/// never grants another's buff. The buff's duration is the Shrines row's (45 s, Divine 20 s, PVP/lesser rows their
/// own); it is sent as 0x1c1 with the f32 duration and removed by 0x1c2 at expiry, keyed by buff instance so a
/// replacement cannot be removed by an older timer.</para>
/// <para>A shrine whose Shrines row names guard packs (the boss and influence shrines: <see cref="ShrineCatalog.GuardPack"/>)
/// brings their monsters around it when it is created, at the area's level, through the monsters' own spawn path
/// (SpawnMonster). Each guard also takes the shrine's monster buff (<c>Monster_ShrineBuffsKey</c>): the BuffDefinitions
/// row the client shows, and the same stats on the monster's own effects (SpawnedMonster.EncounterStats), so the ones the
/// server models change its hit, crit, speed and damage taken.</para>
/// </summary>
public sealed class ShrinesModule : IModule
{
    public void Load(IModuleContext module)
    {
        module.OnAreaGenerated(async player =>
        {
            var conn = ConnOf(player);
            await ShrinesFeature.Spawn(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
        });
        module.OnObjectUsed(async use => await ShrinesFeature.Use(ConnOf(use.Player), use));
        // Dev: the basic types in the area on a ring, so each type's icon, buff and tooltip can be checked in one place.
        module.OnChatCommand("shrines", "/shrines [Id …] (dev: create one shrine of every basic type, or of the named ones, on a ring around the player)", async c =>
        {
            int made = await ShrinesFeature.SpawnEveryType(ConnOf(c.Player), c.Args);
            await c.Player.Tell($"Shrines: {made} created around you");
        });
        // The active shrine buffs join the character's own stat sum, so the hit, crit and repeat paths reading
        // CharacterPlayerStats see them; the buff's expiry takes them out again. FinalStatsBuilding is too late for those.
        module.Lifetime.Own(GameEvents.PlayerStatSources.Add("Shrines", (conn, stats) => ShrinesFeature.AddStats(conn, stats)));
        // The Lightning shrine's own effect: its Shock Novas and the shocked ground they leave, while its buff is up.
        module.OnTick((player, _) => ShrineNova.Tick(ConnOf(player)));
    }
}

static class ShrinesFeature
{
    /// <summary>The shrine type's object size (object-types.json), the walk-up range a click must be inside.</summary>
    const int Size = 7;

    sealed class ShrineState
    {
        /// <summary>The shrine objects this area created, by object id; a used shrine leaves it.</summary>
        public readonly Dictionary<uint, ShrinePlacement.Placed> Shrines = [];
        /// <summary>The granted buffs by BuffDefinitions row: its instance and the token of the timer that owns it.</summary>
        public readonly Dictionary<ushort, (ushort Instance, int Token)> Buffs = [];
        /// <summary>The stats of the buffs above, by the same BuffDefinitions row (ShrineCatalog's exact values).</summary>
        public readonly Dictionary<ushort, IReadOnlyList<(uint Key, int Value)>> Granted = [];
        public int Token;
    }

    static readonly ConditionalWeakTable<ClientState, ShrineState> States = new();
    static ShrineState Of(ClientState conn) => States.GetOrCreateValue(conn);

    internal static async Task Spawn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var state = Of(conn);
        state.Shrines.Clear();
        state.Buffs.Clear();
        state.Granted.Clear();
        if (!MapDevices.Value.IsMapArea(conn.Layout.Hash)) return;
        if (conn.Layout.Walkable is not { } walk) return;
        if (ServerLevels.For(conn.Layout.Id, conn.Seed) is not { } level) return;
        foreach (var p in ShrinePlacement.Plan(level.RoomObjects, walk, conn.Seed))
        {
            // The statue is the shrine room line's own model (a tileset's Metadata/Shrines/*_shrine.ao); the shrine
            // type has no animated_object of its own, and a create without one draws nothing.
            await Create(remote, stream, ciphers, conn, p, p.Model);
            await SpawnGuards(remote, stream, ciphers, conn, p);
        }
        if (state.Shrines.Count > 0)
            Console.WriteLine($"[gateway] {remote} area {conn.Layout.Id}: {state.Shrines.Count} shrine(s) created");
    }

    /// <summary>Creates one shrine object at its cell and registers it for clicks. The Shrines row goes in the object's
    /// Shrine component (the client draws that row's buff icon over it), so the icon and the buff the click grants are
    /// one value: <see cref="ShrinePlacement.Placed.Shrine"/>.</summary>
    static async Task<uint> Create(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ShrinePlacement.Placed placed, string? model)
    {
        uint id = conn.NextObjectId++;
        var shrine = new ShrineObject(placed.X, placed.Y, placed.Rotation,
            Model: model is { Length: > 0 } ? model : null, Row: placed.Shrine.Row);
        Of(conn).Shrines[id] = placed;
        await Send(remote, stream, ciphers,
            InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(ShrinePlacement.Type), shrine.Blob()),
            $"0x02bb create shrine 0x{id:x} {placed.Shrine.Id} at ({placed.X}, {placed.Y}) model {model}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        return id;
    }

    /// <summary>Dev (/shrines): a shrine on a ring around the player, all inside the click range, so a type's icon and
    /// buff can be checked in one area instead of hunting a seed that rolls it: every basic type, or the named ones.
    /// Returns how many were created.</summary>
    internal static async Task<int> SpawnEveryType(ClientState conn, IReadOnlyList<string> ids)
    {
        if (conn.Layout.Walkable is not { } walk) return 0;
        var types = ids.Count == 0 ? ShrineCatalog.Standard : [.. ids.Select(Find)];
        if (types.Count == 0) return 0;
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        const string model = "Metadata/Shrines/generic_shrine.ao";
        int made = 0;
        for (int i = 0; i < types.Count; i++)
        {
            double angle = Math.PI * 2 * i / types.Count;
            var (x, y) = Clear(walk, px + (int)Math.Round(Math.Cos(angle) * (Size + 5)),
                py + (int)Math.Round(Math.Sin(angle) * (Size + 5)), Size + 6);
            await Create(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
                new ShrinePlacement.Placed(x, y, 0, model, types[i]), model);
            made++;
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} /shrines: {made} shrine types created around ({px}, {py})");
        return made;
    }

    /// <summary>The shrine type an /shrines argument names: the basic rows and every other Shrines row by id, case-insensitive.
    /// An unknown id is a refusal, not a silently missing shrine.</summary>
    static ShrineCatalog.Shrine Find(string id) => ShrineCatalog.Find(id) ?? ShrineCatalog.All.FirstOrDefault(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown shrine \"{id}\" (rows: {string.Join(", ", ShrineCatalog.Standard.Select(s => s.Id))}).");

    /// <summary>The nearest walkable cell to (<paramref name="x"/>, <paramref name="y"/>) within
    /// <paramref name="reach"/> rings, or the cell itself when none is (a dev placement still has to draw).</summary>
    static (int X, int Y) Clear(Func<int, int, bool> walk, int x, int y, int reach)
    {
        if (walk(x, y)) return (x, y);
        for (int r = 1; r <= reach; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && walk(x + dx, y + dy)) return (x + dx, y + dy);
        return (x, y);
    }

    /// <summary>The monsters a guarded shrine brings: its Shrines row's MonsterPacks, each pack at its own MinCount..MaxCount
    /// (MonsterPackEntries rolls the variety by weight), standing around the shrine. The monsters' own spawn path places
    /// them on walkable ground, so a ring cell inside a wall moves to the nearest clear one.</summary>
    static async Task SpawnGuards(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ShrinePlacement.Placed shrine)
    {
        if (shrine.Shrine.GuardPacks.Count == 0) return;
        byte level = AreaLevel(conn);
        var created = new List<string>(shrine.Shrine.GuardPacks.Count);
        int buffed = 0;
        foreach (var pack in shrine.Shrine.GuardPacks)
        {
            int count = Random.Shared.Next(Math.Max(1, pack.MinCount), Math.Max(pack.MinCount, pack.MaxCount) + 1);
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                if (Roll(pack.Monsters) is not { Supported: true } variety) break;
                var (x, y) = Around(shrine.X, shrine.Y, i, count);
                uint guard = await SpawnMonster(remote, stream, ciphers, conn, variety, level,
                    GameServer.Core.Protocol.Instance.MonsterRarity.Normal, x, y, alone: false, log: false);
                if (guard == 0) break;
                made++;
                if (shrine.Shrine.Monster is { } monsterBuff && await GuardBuff(remote, stream, ciphers, conn, guard, monsterBuff))
                    buffed++;
            }
            created.Add($"{pack.Id} × {made}");
        }
        Console.WriteLine($"[gateway] {remote} shrine {shrine.Shrine.Id} at ({shrine.X}, {shrine.Y}) guards, level {level}: " +
            string.Join(", ", created) +
            (shrine.Shrine.Monster is { } buff ? $"; shrine buff {buff.Template} (row {buff.BuffRow}) on {buffed}" : ""));
    }

    /// <summary>The shrine's monster buff on one of its guards: the BuffDefinitions row the Shrines row names, and its
    /// BuffTemplates values as the monster's effects. <see cref="SpawnedMonster.EncounterStats"/> is where a monster's
    /// buff stats are read (<see cref="SpawnedMonster.Effects"/>), keyed by stat name: the client key is a Stats row + 1,
    /// so the name comes back from <see cref="Stats"/>.</summary>
    static async Task<bool> GuardBuff(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint guard, ShrineCatalog.MonsterBuff buff)
    {
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in buff.Stats)
            if (key >= 1 && key <= Stats.Names.Count)
            {
                string name = Stats.Name(key);
                stats[name] = stats.GetValueOrDefault(name) + value;
            }
        ushort instance;
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(guard, out var monster)) return false;
            instance = conn.NextBuff++;
            if (stats.Count > 0) monster.EncounterStats = stats;
        }
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(guard, instance, buff.BuffRow, guard),
            $"0x01c1 guard 0x{guard:x} has shrine buff {buff.Template} (BuffDefinitions row {buff.BuffRow})");
        return true;
    }

    /// <summary>One of a pack's varieties: its MonsterPackEntries rows by their weight (PackCatalog.PickVariety's roll).</summary>
    static MonsterCatalog.Variety? Roll(IReadOnlyList<(ushort Variety, int Weight)> monsters)
    {
        int total = monsters.Sum(m => Math.Max(0, m.Weight));
        if (monsters.Count == 0 || total <= 0) return null;
        int roll = Random.Shared.Next(total);
        foreach (var (variety, weight) in monsters)
            if ((roll -= Math.Max(0, weight)) < 0) return Monsters.Find(variety.ToString());
        return Monsters.Find(monsters[^1].Variety.ToString());
    }

    /// <summary>Where guard <paramref name="index"/> of a pack stands: evenly around the shrine, on a ring just outside
    /// its object size, alternating radii so the guards do not stack.</summary>
    static (int X, int Y) Around(int x, int y, int index, int count)
    {
        double angle = Math.PI * 2 * index / Math.Max(1, count);
        int radius = Size + 1 + index % 3;
        return (x + (int)Math.Round(Math.Cos(angle) * radius), y + (int)Math.Round(Math.Sin(angle) * radius));
    }

    /// <summary>Whether this connection holds the shrine buff of BuffDefinitions row <paramref name="row"/>: the buffs
    /// are granted and expired in Grant/Expire, and read by the shrine's own effect (ShrineNova).</summary>
    internal static bool Active(ClientState conn, ushort row)
    {
        var state = Of(conn);
        lock (conn.Monsters) return state.Buffs.ContainsKey(row);
    }

    /// <summary>The area's monster level: an open map's own level for this area, else the WorldAreas level, as
    /// the area's population decides it (Monsters.PopulateArea).</summary>
    internal static byte AreaLevel(ClientState conn)
    {
        int level = ServerLevels.AreaLevel(conn.Layout.Id);
        if (MapDevice.OpenOf(CharacterName(conn)) is { } openMap && openMap.Area.Hash16 == conn.Layout.Hash && openMap.Area.Level > 0)
            level = openMap.Area.Level;
        return (byte)Math.Clamp(level, 1, 100);
    }

    /// <summary>Whether the click was a shrine's. The buff is granted only for a shrine of this area, within reach.</summary>
    internal static async Task<bool> Use(ClientState conn, ObjectUse use)
    {
        var state = Of(conn);
        if (!state.Shrines.TryGetValue(use.ObjectId, out var shrine)) return false;
        if (!PlayerWalk.InWalkUpRange(use.Player.Position, (shrine.X, shrine.Y), Size)) return false;
        state.Shrines.Remove(use.ObjectId);
        Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine 0x{use.ObjectId:x} {shrine.Shrine.Id} used at " +
            $"({use.Player.Position.X}, {use.Player.Position.Y})");
        await Grant(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, shrine);
        // The shrine is spent: the object leaves the area just after the client's own use animation, so it is not left
        // standing inactive and nothing can click it again (the object was already out of the click set). The tables carry
        // no activated statue state for the standard shrines: ShrineVisualArtVariations only maps the six boss/influence
        // rows (24-29), so the tileset statue has no "used" clip to advance to.
        _ = Consume(conn, use.ObjectId, shrine.Shrine.Id);
        return true;
    }

    /// <summary>The used shrine's object leaving the area (0x02bd) a second after the click: the buff it granted stays
    /// with the player for the Shrines row's duration.</summary>
    static async Task Consume(ClientState conn, uint id, string type)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (conn.Closed || conn.LeavingArea) return;
        await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, InstanceProtocol.RemoveObject(id),
            $"0x02bd shrine 0x{id:x} {type} consumed");
    }

    static async Task Grant(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ShrinePlacement.Placed shrine)
    {
        var state = Of(conn);
        ushort row = shrine.Shrine.BuffRow;
        ushort instance;
        int token;
        byte[]? replaced = null;
        lock (conn.Monsters)
        {
            token = ++state.Token;
            if (state.Buffs.Remove(row, out var old)) replaced = InstanceEntities.RemoveBuff(PlayerId, old.Instance);
            instance = conn.NextBuff++;
            state.Buffs[row] = (instance, token);
            state.Granted[row] = shrine.Shrine.Stats;
            Console.WriteLine($"[gateway] shrine {shrine.Shrine.Id} grants " +
                string.Join(", ", shrine.Shrine.Stats.Select(s => $"0x{s.Key:x}={s.Value}")));
        }
        if (replaced is not null)
            await Send(remote, stream, ciphers, replaced, $"0x01c2 shrine buff {row} replaced by a new shrine");
        await Send(remote, stream, ciphers, [], $"0x01c1 shrine {shrine.Shrine.Id} buff {row} for {shrine.Shrine.Duration} s",
            atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.LeavingArea) return null;
                    var bytes = new List<byte>(InstanceEntities.AddBuff(PlayerId, instance, row, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits(shrine.Shrine.Duration)));
                    // The buff's stats are in the character's sum now, so pools and the panel must follow.
                    RecomputePoolsAndSpeed(conn);
                    bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                    return [.. bytes];
                }
            });
        _ = Expire(remote, stream, ciphers, conn, row, instance, token, shrine.Shrine.Duration);
    }

    /// <summary>The shrine buffs' stats added to the character's own sum (GameEvents.PlayerStatSources). Each value
    /// is the BuffTemplates row's value for its definition's stat; the client key is the Stats row + 1.</summary>
    internal static Dictionary<uint, int> AddStats(ClientState conn, Dictionary<uint, int> stats)
    {
        var state = Of(conn);
        lock (conn.Monsters)
            foreach (var granted in state.Granted.Values)
                foreach (var (key, value) in granted)
                    stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    static async Task Expire(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort row,
        ushort instance, int token, int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await Send(remote, stream, ciphers, [], $"0x01c2 shrine buff {row} ends", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea) return null;
                var state = Of(conn);
                if (!state.Buffs.TryGetValue(row, out var buff) || buff.Instance != instance || buff.Token != token) return null;
                state.Buffs.Remove(row);
                state.Granted.Remove(row);
                var bytes = new List<byte>(InstanceEntities.RemoveBuff(PlayerId, instance));
                RecomputePoolsAndSpeed(conn);
                bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                return [.. bytes];
            }
        });
    }
}
