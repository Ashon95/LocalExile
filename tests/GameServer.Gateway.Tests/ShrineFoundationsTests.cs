using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class ShrineFoundationsTests
{
    [Fact]
    public void Every_shrine_resolves_its_own_buff_values_and_duration()
    {
        Assert.Empty(ShrineCatalog.Skipped);
        Assert.Equal(68, ShrineCatalog.All.Count);

        var acceleration = ShrineCatalog.Find("Acceleration")!;
        Assert.Equal(45, acceleration.Duration);
        Assert.Equal(241, acceleration.BuffRow);
        Assert.Equal("shrine_acceleration", acceleration.BuffId);
        // BuffDefinitions 241's GrantedStats are action_speed_-%, base_projectile_speed_+% and has_shrine_effect;
        // BuffTemplates 383 gives -50, 80 and 1. The client key is the Stats row + 1.
        Assert.Equal([(348u, -50), (178u, 80), (5819u, 1)], acceleration.Stats);

        var brutal = ShrineCatalog.Find("Brutal")!;
        Assert.Equal(45, brutal.Duration);
        Assert.Equal([(25u, 50), (111u, 1), (343u, 30), (1065u, 1), (5819u, 1)], brutal.Stats);

        var divine = ShrineCatalog.Find("GodMode")!;
        Assert.Equal(20, divine.Duration);
        Assert.Equal(261, divine.BuffRow);

        var resistance = ShrineCatalog.Find("Resistance")!;
        Assert.Equal([50, 10, 1], resistance.Stats.Select(s => s.Value));
        Assert.Equal(4, ShrineCatalog.Find("Massive")!.Stats.Count);
        Assert.Equal(4, ShrineCatalog.Find("Multi")!.Stats.Count);

        // The monster buffs are their own templates and values: Acceleration's monsters take -60 action speed and
        // 100 projectile speed where the player takes -50 and 80.
        // The monster template (384) grants the same BuffDefinitions row as the player's (241), with its own values.
        Assert.NotNull(acceleration.Monster);
        Assert.Equal((ushort)384, acceleration.Monster!.TemplateRow);
        Assert.Equal("acceleration_monster", acceleration.Monster.Template);
        Assert.Equal((ushort)241, acceleration.Monster.BuffRow);
        Assert.Equal([(348u, -60), (178u, 100), (5819u, 1)], acceleration.Monster.Stats);
        Assert.Empty(acceleration.GuardPacks);
        Assert.Equal([20, 155, 60, 1], ShrineCatalog.Find("Massive")!.Monster!.Stats.Select(s => s.Value));
        Assert.DoesNotContain(ShrineCatalog.Skipped, s => s.Contains("monster"));

        // Guard packs are data: only the boss and influence shrines have them (Crusader: four packs of 3-4, 3-4,
        // 3-4 and 3-7).
        var crusader = ShrineCatalog.Find("Crusader")!;
        Assert.Equal(4, crusader.GuardPacks.Count);
        // A guarded shrine's monster buff (Monster_ShrineBuffsKey) is a real stat set on the guards it spawns: Solaris
        // gives them always_crit (Stats row 349, client key 350) on top of its fire damage.
        var solaris = ShrineCatalog.Find("Solaris")!;
        Assert.Equal(4, solaris.GuardPacks.Count);
        Assert.Contains((350u, 1), solaris.Monster!.Stats);

        // Those stats are real modifiers, not a display: a guard's elemental damage is a gain of its physical hit, and
        // the shrine's fire_damage_+% with elemental_damage_+% raises it (100 physical, gained as fire, × 1.5 × 1.2).
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var burning = MonsterModEffects.AddedTo(physical, new Dictionary<string, int>
        {
            ["physical_damage_%_to_add_as_fire"] = 100, ["fire_damage_+%"] = 50, ["elemental_damage_+%"] = 20,
        });
        Assert.Equal(100, burning[DamageType.Physical].Max);
        Assert.Equal(170, burning[DamageType.Fire].Max);
        // Arakaali's guards gain chaos from their physical hit (physical_damage_%_to_add_as_chaos) and its shrine's
        // chaos_damage_+% raises that: 10% of 100, × 1.5.
        var poisoned = MonsterModEffects.AddedTo(physical, new Dictionary<string, int>
        {
            ["physical_damage_%_to_add_as_chaos"] = 10, ["chaos_damage_+%"] = 50,
        });
        Assert.Equal(15, poisoned[DamageType.Chaos].Max);

        // And the guards really deal an element, from their own granted effects: MeleeCold converts 75% of their physical
        // damage to cold, and the Solaris caster's buff adds 50% of its physical damage as fire. The shrine's increases are
        // applied to that damage by MonsterModEffects.Hit, so BrineKing's cold buff and Solaris's fire buff change the
        // numbers the player takes.
        var skills = new SkillCatalog();
        var cold = skills.MonsterElemental(skills.EffectRow("MeleeCold"), [], 66);
        Assert.Equal(75, cold.ConversionPercent(DamageType.Physical, DamageType.Cold));
        var coldHit = new MonsterModEffects(new Dictionary<string, int> { ["cold_damage_+%"] = 100 })
            .Hit(physical.Convert(cold));
        Assert.Equal(25, coldHit[DamageType.Physical].Max);
        Assert.Equal(150, coldHit[DamageType.Cold].Max);
        var fire = skills.MonsterElemental(skills.EffectRow("MeleeAtAnimationSpeed"), [6824], 66);
        Assert.Equal(50, fire.GainPercent(DamageType.Physical, DamageType.Fire));
        var fireHit = new MonsterModEffects(new Dictionary<string, int> { ["fire_damage_+%"] = 50 })
            .Hit(physical.Convert(fire));
        Assert.Equal(100, fireHit[DamageType.Physical].Max);
        Assert.Equal(75, fireHit[DamageType.Fire].Max);

        Assert.Equal("atlasexileinfluence-crusader-shrine1", crusader.GuardPacks[0].Id);
        // The pack's members come from MonsterPackEntries (variety 8238 once, 8239 at weight 80, 8242 at 20).
        Assert.Equal([(8238, 1), (8239, 80), (8242, 20)], crusader.GuardPacks[0].Monsters.Select(m => ((int)m.Variety, m.Weight)));

        // The Solaris caster's nova (GrantedEffects 6432) is a real burning ground: four seconds (its stat set's
        // base_skill_effect_duration 4000), four cells (GroundEffectTypes Fire __col1), drawn with GroundEffects' fire art
        // and burning at the game's own monster burning-ground rate (Mods 6528: 3750% of its maximum hit a minute).
        Assert.Contains(1325, solaris.GuardPacks.SelectMany(p => p.Monsters).Select(m => (int)m.Variety));
        Assert.Equal("Metadata/Effects/Spells/ground_effects/fire/fire1.ao", BurningGround.Art);
        Assert.Equal(4, BurningGround.Radius);
        Assert.Equal(4.0, BurningGround.Seconds(skills), 3);
        Assert.Equal(62.5, BurningGround.DamagePerSecond(100), 3);
        // The player's icon is BuffDefinitions 166 ground_fire_burn (BuffVisuals 181: "Burning Ground").
        Assert.Equal((ushort)166, BurningGround.BuffRow);
    }

    [Fact]
    public void The_shrine_blob_writes_the_ten_components_the_client_reads()
    {
        // data/object-types.json lists Positioned, BaseEvents, Stats, Animated, Buffs, Life, ObjectMagicProperties,
        // Shrine, MinimapIcon and InteractionAction as the shrine's deserialized components (read = 10).
        Assert.Equal(10, ShrineObject.Components.Length);
        Assert.Equal(["Positioned", "BaseEvents", "Stats", "Animated", "Buffs", "Life", "ObjectMagicProperties",
            "Shrine", "MinimapIcon", "InteractionAction"], ShrineObject.Components);

        // Two header bytes, 18 Positioned, 10 Stats, 1 Animated, 2 Buffs, 55 Life, 4 magic properties, 2 Shrine,
        // 1 MinimapIcon, 1 InteractionAction, 1 trailing task count.
        var blob = new ShrineObject(120, 45).Blob();
        Assert.Equal(98, blob.Length);
        Assert.Equal(0, blob[0]);
        Assert.Equal(0, blob[^1]);
        // The Shrine component starts with the Shrines row the client looks up (0 = Acceleration, which is why every
        // shrine drew Acceleration's icon before), then the u8 stored at +0x50 and its list's trailing byte.
        Assert.Equal(0, blob[92]);
        Assert.Equal(0, blob[93]);
        Assert.Equal(0, blob[94]);
        Assert.Equal(11, new ShrineObject(120, 45, Row: 11).Blob()[92]);

        // A shrine room line is a shrine only on walkable ground, and the same layout and seed roll the same types.
        bool Walkable(int x, int y) => x == 5 && y == 5;
        const string Statue = "Metadata/Shrines/generic_shrine.ao";
        PlacedTileObject Line(string type, int x, int y) => new(type, Statue, x, y, 0.5f, 0f);
        var lines = new[] { Line(ShrinePlacement.Type, 5, 5), Line(ShrinePlacement.Type, 5, 5), Line(ShrinePlacement.Type, 0, 0) };
        var plan = ShrinePlacement.Plan(lines, Walkable, seed: 7);
        Assert.Single(plan);
        Assert.Equal((5, 5), (plan[0].X, plan[0].Y));
        Assert.Equal(0.5f, plan[0].Rotation);
        // The statue is the room line's model: the shrine type has no animated_object of its own.
        Assert.Equal(Statue, plan[0].Model);
        // A map's shrine rooms roll only the basic shrines: the apparition shrines (Warlord's, Crusader's, ...) wait
        // for the Domination Scarab of Apparitions, and the lesser (The Gull's skill) and greater (shrine daemons)
        // rows are not a map shrine room's type.
        Assert.Equal(15, ShrineCatalog.Standard.Count);
        Assert.Equal(["Acceleration", "Lightning", "Cold", "Criticals", "Brutal", "Regen", "Massive", "Defence",
            "Resistance", "GodMode", "Multi", "Skeletons", "Smoke", "Resonating", "Gloom"],
            ShrineCatalog.Standard.Select(s => s.Id));
        Assert.Equal(8, ShrineCatalog.ApparitionIds.Length);
        Assert.DoesNotContain(ShrineCatalog.Standard, s => ShrineCatalog.ApparitionIds.Contains(s.Id));
        Assert.DoesNotContain(ShrineCatalog.Standard, s => s.Lesser || s.GuardPacks.Count > 0 || s.Duration == 0);
        Assert.Contains(plan[0].Shrine, ShrineCatalog.Standard);
        Assert.Equal(plan, ShrinePlacement.Plan(lines, Walkable, seed: 7));
        Assert.Empty(ShrinePlacement.Plan(lines, Walkable, seed: 7, limit: 0));
    }
}
