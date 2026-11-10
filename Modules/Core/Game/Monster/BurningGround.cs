using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The burning ground a monster's nova leaves: the Solarsworn Infernomage's
/// <c>NovaProjectileSpellBurningGround</c> (GrantedEffects 6432) is four seconds of fire ground where the player
/// stands. The patch is drawn with GroundEffects' fire art (GroundEffectTypes Fire, variation 0) and burns for the
/// game's own monster burning-ground rate: Mods <c>MonsterGroundFireOnDeath1</c> deals
/// <c>monster_ground_fire_on_death_%_max_damage_to_deal_per_minute</c> 3750 of the monster's maximum hit a minute.
/// The tick goes through <see cref="ClientState.TrapDegenPerSecond"/>, the path the Labyrinth's traps and the
/// Oversoul's cold ground use (Program.Player's recovery loop deals it), so the player loses life continuously while
/// standing in it; each connection's patches and their share of that rate live here.
/// </summary>
static class BurningGround
{
    /// <summary>GrantedEffects row of the nova the Solaris casters carry.</summary>
    internal const int Skill = 6432;
    internal const string EffectId = "NovaProjectileSpellBurningGround";

    /// <summary>GroundEffectTypes Fire __col1: the patch's radius in cells, read as FlaskGrounds reads Profane's.</summary>
    internal const int Radius = 4;

    /// <summary>Mods 6528 MonsterGroundFireOnDeath1: the stat's 3750% of maximum damage a minute.</summary>
    internal const int MaxDamagePctPerMinute = 3750;

    /// <summary>BuffDefinitions 166 <c>ground_fire_burn</c>, the player's "Burning Ground" debuff (BuffVisuals 181,
    /// its StatsKeys base_fire_damage_taken_per_minute and is_on_ground_fire_burn).</summary>
    internal const ushort BuffRow = 166;

    /// <summary>GroundEffects' fire art (GroundEffectTypes Fire, variation 0, __col1 0 / __col2 1).</summary>
    internal const string Art = "Metadata/Effects/Spells/ground_effects/fire/fire1.ao";

    /// <summary>The fallback duration when the skill's stat set has none (GrantedEffectStatSets: 4000).</summary>
    internal const double FallbackSeconds = 4;

    /// <summary>One patch: where it burns, when it ends (connection clock), the monster's burning rate and its art object.</summary>
    internal sealed class Patch
    {
        public (int X, int Y) At;
        public double Until;
        public double Dps;
        public uint Object;
    }

    /// <summary>A connection's patches and the rate this system last added to TrapDegenPerSecond.</summary>
    sealed class State
    {
        public readonly List<Patch> Patches = [];
        public double Dps;
        /// <summary>The player's burning-ground buff instance, and the patch end it was sent for.</summary>
        public ushort Buff;
        public double BuffUntil;
    }

    static readonly ConditionalWeakTable<ClientState, State> States = new();
    static readonly ConditionalWeakTable<SkillCatalog, ConcurrentDictionary<int, int>> Durations = new();

    /// <summary>How much a monster's burning ground deals a second: 3750% of its maximum hit a minute.</summary>
    internal static double DamagePerSecond(double maxHit) => Math.Max(0, maxHit) * MaxDamagePctPerMinute / 100.0 / 60.0;

    /// <summary>Whether the monster casts the burning-ground nova (its granted effects include 6432).</summary>
    internal static bool Casts(ClientState conn, SpawnedMonster m)
    {
        if (m.Variety?.GrantedEffects is not { Count: > 0 } effects) return false;
        var catalog = SkillsFor(conn);
        return effects.Any(effect => catalog.EffectId(effect) == EffectId);
    }

    /// <summary>The nova's duration: its stat set's base_skill_effect_duration (4000 ms).</summary>
    internal static double Seconds(SkillCatalog catalog)
    {
        var durations = Durations.GetValue(catalog, _ => new());
        return durations.GetOrAdd(Skill, effect =>
        {
            if (Stats.Key("base_skill_effect_duration") is not uint key) return (int)(FallbackSeconds * 1000);
            foreach (var (stat, value) in catalog.EffectStats(effect, 1)) if (stat == key && value > 0) return value;
            return (int)(FallbackSeconds * 1000);
        }) / 1000.0;
    }

    /// <summary>The nova's ground appears where the player stood when it landed. Called under the connection's
    /// monsters lock from the hit path, so it only queues the art packets (Program.Minions sends them).</summary>
    internal static void Place(ClientState conn, SpawnedMonster m, (int X, int Y) at, double now)
    {
        if (!Casts(conn, m) || m.Attack is not { } attack) return;
        double dps = DamagePerSecond(attack.Damage.MaxTotal);
        double until = now + Seconds(SkillsFor(conn));
        var state = States.GetValue(conn, _ => new());
        // The connection's monsters lock first, then the state: Tick takes the same order, so a hit placing a patch can
        // never deadlock against it. The id is taken before the state lock and may go unused when a patch is refreshed.
        uint id; lock (conn.Monsters) id = conn.NextObjectId++;
        lock (state)
        {
            // A cast on the same cell refreshes the patch instead of stacking a second one (burning ground does not stack).
            if (state.Patches.FirstOrDefault(p => p.Until > now && p.At == at) is { } kept)
            {
                kept.Until = Math.Max(kept.Until, until);
                kept.Dps = Math.Max(kept.Dps, dps);
                return;
            }
            state.Patches.Add(new() { At = at, Until = until, Dps = dps, Object = id });
            var art = new EffectObject(at.X, at.Y, Art) { Flags0 = EffectObject.FixedRotation };
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                art.Blob()), null));
            Console.WriteLine($"[gateway] burning ground at ({at.X}, {at.Y}) for {until - now:0.#} s, {dps:0.#}/s");
        }
    }

    /// <summary>The patch the player stands in, and its rate after the player's fire resistance (the trap-degen path
    /// does not apply one). Null when they are not in burning ground or are protected on entry.</summary>
    internal static Patch? Standing(ClientState conn, double now)
    {
        if (conn.PlayerLife == 0 || InGracePeriod(conn)) return null;
        if (!States.TryGetValue(conn, out var state)) return null;
        var here = conn.Walk.Position(now);
        lock (state) return state.Patches.Where(p => p.Until > now && Dist(p.At, here) <= Radius).MaxBy(p => p.Dps);
    }

    /// <summary>One tick: expire finished patches (removing their art), and keep the player's degeneration at the
    /// standing patch's rate (less fire resistance), as a trap runner keeps its share.</summary>
    internal static async Task Tick(ClientState conn)
    {
        if (conn.Closed) return;
        if (!States.TryGetValue(conn, out var state)) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        // The monsters lock always comes first (the hit path places patches under it), and the state lock inside it.
        lock (conn.Monsters)
        {
            // A connection that has left the area keeps none of it: the patches' objects are gone with the area, and its
            // share of the degeneration must not follow the player into the next one (VaalOversoul.Leaving does the same).
            if (conn.LeavingArea)
            {
                lock (state)
                {
                    state.Patches.Clear();
                    state.Buff = 0; state.BuffUntil = 0;
                    conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - state.Dps); state.Dps = 0;
                }
                return;
            }
            lock (state)
            {
                foreach (var ended in state.Patches.Where(p => p.Until <= now).ToArray())
                {
                    state.Patches.Remove(ended);
                    conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ended.Object), null));
                }
                var patch = Standing(conn, now);
                double dps = patch is null ? 0 : patch.Dps * (1 - Math.Min(MaxResistance(conn, DamageType.Fire),
                    Resistance(conn, DamageType.Fire)) / 100.0);
                conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - state.Dps + dps);
                if ((state.Dps > 0) != (dps > 0))
                    Console.WriteLine(dps > 0
                        ? $"[gateway] burning ground: the player stands in it, {dps:0.#}/s"
                        : "[gateway] burning ground: the player left it");
                state.Dps = dps;
                // The player's Burning Ground icon (BuffDefinitions 166): shown while they stand in a patch, with the
                // time left of it. A refreshed patch is a new instance, as the shrine buffs replace theirs.
                if (patch is not null && (state.Buff == 0 || Math.Abs(patch.Until - state.BuffUntil) > .25))
                {
                    if (state.Buff != 0) conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, state.Buff), null));
                    state.Buff = (ushort)conn.NextBuff++; state.BuffUntil = patch.Until;
                    conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, state.Buff, BuffRow, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, patch.Until - now))),
                        $"0x01c1 player burning ground buff {state.Buff} ({patch.Until - now:0.#} s)"));
                }
                else if (patch is null && state.Buff != 0)
                {
                    conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, state.Buff),
                        $"0x01c2 player burning ground buff {state.Buff} ends"));
                    state.Buff = 0; state.BuffUntil = 0;
                }
            }
        }
        await Task.CompletedTask;
    }
}
