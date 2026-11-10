using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The Lightning shrine's effect: the Shrines row 1 (Lightning) summons MonsterVarieties 2918
/// (Metadata/Monsters/Daemon/ShrineDaemonLightning), whose GrantedEffects 1067 <c>ShrineShockNova</c> is the Shock Nova
/// its buff promises ("Shock Novas happen around you", BuffDefinitions 248 <c>shrine_lightning</c>). While that buff is
/// up the nova is cast around the player once per the daemon's attack time (MonsterVarieties AttackSpeed 1000).
/// <para>Shock Nova's own numbers are the skill's stat set (GrantedEffectStatSets 1075):
/// <c>active_skill_area_of_effect_radius_+%_final</c> 100 (SkillArea doubles the generic 12-cell radius),
/// <c>shock_nova_places_X_ground_lightning_patches</c> 5 and <c>base_skill_effect_duration</c> 4000, and its cast time
/// (GrantedEffects CastTime 750) is how long its art plays. The shrine skill carries no damage stats of its own: a
/// monster spell's damage is its variety's level hit (Monsters.BaseDamage), which is what the daemon's nova is scaled
/// from here, taken as lightning damage, the skill's element. The nova hits hostile monsters only: the shrine's own
/// effect never damages the player who used it (its shocked ground may still be stood in, as any ground may).</para>
/// <para>The nova is drawn with the client's own Shock Nova art (<see cref="NovaArt"/>), scaled to the radius it hits in
/// like the player's Righteous Fire ring is, and leaves its ground patches (GroundEffectTypes 7 Lightning, GroundEffects'
/// lightning art) around the caster. A patch is four seconds of shocked ground (BuffDefinitions 232
/// <c>ground_lightning_shock</c>, its granted stat <c>base_damage_taken_+%_from_shock</c> 15): the player standing in one
/// shows that buff, and a monster standing in one is kept under a <see cref="ShockPct"/>% shock (the server's own
/// damage-taken path for that stat, <c>MonsterDamageTaken</c>). The patches lie on a ring around the caster, the way the
/// nova's ring does, and the player's own shocked-ground icon appears only if they step into one.</para>
/// <para>Not modelled: Shock Nova's two components (an inner ring and the larger nova, which can both hit one monster);
/// the hit is one area hit, and each monster in it takes it once per cast.</para>
/// </summary>
static class ShrineNova
{
    /// <summary>GrantedEffects row of the Lightning shrine's Shock Nova (<c>ShrineShockNova</c>).</summary>
    internal const int Effect = 1067;

    /// <summary>BuffDefinitions 248 <c>shrine_lightning</c>: the buff whose presence casts the novae.</summary>
    internal const ushort BuffRow = 248;

    /// <summary>MonsterVarieties row of the shrine's own daemon: its level hit is the nova's damage, its attack time the
    /// cast cadence.</summary>
    internal const string Daemon = "2918";

    /// <summary>GroundEffectTypes 7 Lightning's BuffDefinitions row: 232 <c>ground_lightning_shock</c> ("Shocked Ground").</summary>
    internal const ushort GroundBuffRow = 232;

    /// <summary>Stat 16700 <c>base_damage_taken_+%_from_shock</c>, the shocked ground's own value (wiki Shocked ground:
    /// 15% base): the shock a monster standing in a patch is kept under.</summary>
    internal const int ShockPct = 15;

    /// <summary>The client's Shock Nova art (the cast's nova, not its ring of ground).</summary>
    internal const string NovaArt = "Metadata/Effects/Spells/shock_nova/new/shock_nova.ao";

    /// <summary>GroundEffects' lightning art (GroundEffectTypes Lightning, its first variation).</summary>
    internal const string GroundArt = "Metadata/Effects/Spells/ground_effects/lightning/lightning_ground_01.ao";

    /// <summary>GroundEffectTypes Lightning's patch size in cells, as BurningGround reads Fire's 4 (the tables carry no
    /// radius per ground type).</summary>
    internal const int GroundRadius = 4;

    /// <summary>How far from the caster the patches land: a ring around the player, the way the nova is cast around them,
    /// so the ground is theirs to step into rather than under their feet.</summary>
    internal const int GroundRing = 6;

    /// <summary>The generic area radius SkillArea scales when the skill exports none (SkillArea.BaseRadius): Shock Nova's
    /// own stat set has only the +100% final radius.</summary>
    internal const int GenericRadius = 12;

    /// <summary>The fallback patch duration when the skill's stat set has none (GrantedEffectStatSets: 4000 ms).</summary>
    internal const double FallbackSeconds = 4;

    /// <summary>The fallback cast animation length when the effect row has no cast time (GrantedEffects: 750 ms).</summary>
    internal const double FallbackCastSeconds = .75;

    /// <summary>How long the ground's shock lasts each time it is refreshed: long enough to bridge the tick that
    /// refreshes it, so leaving the patch clears it shortly after.</summary>
    internal const double GroundShockSeconds = 1;

    /// <summary>One patch: where it lies, when it ends (connection clock) and its art object.</summary>
    sealed class Patch
    {
        public (int X, int Y) At;
        public double Until;
        public uint Object;
    }

    /// <summary>One cast's nova art: the object drawn and when it is removed (its clip is the skill's cast time).</summary>
    sealed class Visual
    {
        public uint Object;
        public double Until;
    }

    /// <summary>A connection's patches, its live nova arts, the next cast (connection clock) and its ground buff instance.</summary>
    sealed class State
    {
        public readonly List<Patch> Patches = [];
        public readonly List<Visual> Novas = [];
        public double NextCast;
        public ushort Buff;
        public double BuffUntil;
    }

    static readonly ConditionalWeakTable<ClientState, State> States = new();
    static State Of(ClientState conn) => States.GetOrCreateValue(conn);

    /// <summary>One tick: expire finished patches and nova arts (removing their objects), keep each monster's shocked
    /// ground in step with where it stands, show the player's Shocked Ground buff, and cast the nova while the shrine buff
    /// is up. The monsters lock always comes first, then the state (BurningGround's order); the hits await outside both.</summary>
    internal static async Task Tick(ClientState conn)
    {
        if (conn.Closed) return;
        var state = Of(conn);
        double now = conn.Clock.Elapsed.TotalSeconds;
        var targets = new List<(uint Id, SpawnedMonster M)>();
        uint nova = 0;
        double radius = 0;
        bool cast = false;
        lock (conn.Monsters)
        lock (state)
        {
            // A connection that has left the area keeps none of it: the patches' and novae's objects are gone with the area.
            if (conn.LeavingArea)
            {
                state.Patches.Clear();
                state.Novas.Clear();
                state.NextCast = 0;
                state.Buff = 0;
                state.BuffUntil = 0;
                return;
            }
            foreach (var ended in state.Patches.Where(p => p.Until <= now).ToArray())
            {
                state.Patches.Remove(ended);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ended.Object), null));
            }
            foreach (var ended in state.Novas.Where(v => v.Until <= now).ToArray())
            {
                state.Novas.Remove(ended);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ended.Object), null));
            }
            // Shocked ground: a monster standing in a patch is kept under a 15% shock (base_damage_taken_+%_from_shock),
            // refreshed only when it has run low, so its buff is not re-sent every tick.
            foreach (var (_, m) in conn.Monsters)
                if (m is { Hostile: true, Dead: false, Life: > 0 }
                    && state.Patches.Any(p => p.Until > now && Dist(p.At, m.Walk.Position(now)) <= GroundRadius)
                    && (m.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, now) < ShockPct
                        || m.Ailments.Remaining(MonsterElementalAilments.Kind.Shock, now) < .4))
                    m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, GroundShockSeconds, now,
                        n => 0, n => m.Effects[n], 1, ShockPct);
            MaintainPlayerBuff(conn, state, now);
            if (!ShrinesFeature.Active(conn, BuffRow) || now < state.NextCast) return;
            cast = true;
            state.NextCast = now + CastSeconds(conn);
            var at = conn.Walk.Position(now);
            double seconds = Seconds(conn);
            var own = Own(conn);
            radius = SkillArea.Radius(GenericRadius, n => Stat(own, n), spell: true);
            nova = Nova(conn, state, at, radius, now);
            Place(conn, at, now, seconds);
            foreach (var (id, m) in conn.Monsters)
                if (m is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                    && Dist(m.Walk.Position(now), at) <= radius) targets.Add((id, m));
            Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Shock Nova 0x{nova:x} at ({at.X}, {at.Y}): " +
                $"radius {radius}, {targets.Count} monster(s) in it");
        }
        if (!cast) return;
        var packet = NovaDamage(conn);
        if (packet.MaxTotal > 0)
            foreach (var (id, m) in targets)
            {
                int damage;
                lock (conn.Monsters)
                {
                    if (m.Dead || m.Life == 0) continue;
                    var mitigated = DamageModel.Mitigate(packet.Roll(Random.Shared), type => MonsterHitResistance(m, type, default));
                    damage = MonsterTakesMitigated(m, mitigated, spell: true);
                }
                if (damage <= 0) continue;
                if (!await HitMonster(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, id, m, damage,
                        "Shock Nova", log: false)) continue;
                // The shock lands after the hit that lands it, so the hit does not gain from its own shock.
                lock (conn.Monsters)
                    if (!m.Dead && m.Life > 0)
                        m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, Seconds(conn), now,
                            n => 0, n => m.Effects[n], 1, ShockPct);
            }
    }

    /// <summary>The nova's art object at the caster, scaled to the radius it hits in (as the Righteous Fire ring is:
    /// the art's own native radius against the game's world units). Its clip is the skill's cast time, then it is removed.
    /// Called under the connection's locks.</summary>
    static uint Nova(ClientState conn, State state, (int X, int Y) at, double radius, double now)
    {
        uint id = conn.NextObjectId++;
        var art = new EffectObject(at.X, at.Y, NovaArt)
        {
            Flags0 = EffectObject.FixedRotation,
            Scale = Art.ArtRadius(NovaArt) is int native and > 0 ? (float)(radius * (250d / 23) / native) : 1,
        };
        conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            art.Blob()), null));
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null));
        state.Novas.Add(new() { Object = id, Until = now + CastSeconds(conn) });
        return id;
    }

    /// <summary>The five patches around the caster (the skill's <c>shock_nova_places_X_ground_lightning_patches</c>); a
    /// cast on a cell that already has a live patch refreshes it instead of stacking a second one, as BurningGround's
    /// does. Like BurningGround, the patch is not kept off cells the walk estimate calls unwalkable: it is spell art, and
    /// the nova's own cast put it there. Called under the connection's locks.</summary>
    static void Place(ClientState conn, (int X, int Y) at, double now, double seconds)
    {
        var state = Of(conn);
        int count = Math.Max(0, Stat(Own(conn), "shock_nova_places_X_ground_lightning_patches"));
        double until = now + seconds;
        int made = 0;
        for (int i = 0; i < count; i++)
        {
            double angle = Math.PI * 2 * i / count;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * GroundRing);
            int y = at.Y + (int)Math.Round(Math.Sin(angle) * GroundRing);
            if (state.Patches.FirstOrDefault(p => p.Until > now && p.At == (x, y)) is { } kept)
            {
                kept.Until = Math.Max(kept.Until, until);
                continue;
            }
            uint id = conn.NextObjectId++;
            state.Patches.Add(new() { At = (x, y), Until = until, Object = id });
            var art = new EffectObject(x, y, GroundArt) { Flags0 = EffectObject.FixedRotation };
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                art.Blob()), null));
            made++;
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Shock Nova at ({at.X}, {at.Y}): " +
            $"{made}/{count} shocked ground patches for {seconds:0.#} s");
    }

    /// <summary>The player's Shocked Ground icon (BuffDefinitions 232) while they stand in a patch, with the time left of
    /// it; a refreshed patch is a new instance, as the shrine buffs replace theirs. Under the locks of <see cref="Tick"/>.</summary>
    static void MaintainPlayerBuff(ClientState conn, State state, double now)
    {
        var here = conn.Walk.Position(now);
        var patch = state.Patches.Where(p => p.Until > now && Dist(p.At, here) <= GroundRadius).MaxBy(p => p.Until);
        if (patch is not null && (state.Buff == 0 || Math.Abs(patch.Until - state.BuffUntil) > .25))
        {
            if (state.Buff != 0)
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, state.Buff), null));
            state.Buff = (ushort)conn.NextBuff++;
            state.BuffUntil = patch.Until;
            conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, state.Buff, GroundBuffRow, PlayerId,
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, patch.Until - now))),
                $"0x01c1 player shocked ground buff {state.Buff} ({patch.Until - now:0.#} s)"));
        }
        else if (patch is null && state.Buff != 0)
        {
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, state.Buff),
                $"0x01c2 player shocked ground buff {state.Buff} ends"));
            state.Buff = 0;
            state.BuffUntil = 0;
        }
    }

    /// <summary>How long a patch lasts: the skill's <c>base_skill_effect_duration</c> (4000 ms).</summary>
    static double Seconds(ClientState conn)
    {
        int ms = Stat(Own(conn), "base_skill_effect_duration");
        return ms > 0 ? ms / 1000.0 : FallbackSeconds;
    }

    /// <summary>How often the daemon casts, and how long its art plays: its attack time (MonsterVarieties AttackSpeed
    /// 1000 → 1 s), or the effect's own cast time when that is longer.</summary>
    static double CastSeconds(ClientState conn)
    {
        double attack = Monsters.Find(Daemon) is { AttackMs: > 0 } daemon ? daemon.AttackMs / 1000.0 : 1;
        int cast = SkillsFor(conn).CastTimeMs(Effect);
        return Math.Max(attack, cast > 0 ? cast / 1000.0 : FallbackCastSeconds);
    }

    /// <summary>The nova's hit: the daemon's level hit as lightning damage (see the summary: the shrine skill carries no
    /// damage stats).</summary>
    static DamagePacket NovaDamage(ClientState conn)
    {
        if (Monsters.Find(Daemon) is not { } daemon) return DamagePacket.Empty;
        if (Monsters.BaseDamage(daemon, ShrinesFeature.AreaLevel(conn)) is not { } range) return DamagePacket.Empty;
        return DamagePacket.Of(DamageType.Lightning, range.Item1, range.Item2);
    }

    static Dictionary<uint, int> Own(ClientState conn) =>
        SkillsFor(conn).EffectStats(Effect, 1).ToDictionary(s => s.Item1, s => s.Item2);

    static int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
}
