using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BoneLib;
using HarmonyLib;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Data;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.Warehouse;
using LabFusion.Entities;
using LabFusion.Marrow.Pool;
using LabFusion.Network;
using LabFusion.Network.Serialization;
using LabFusion.Extensions;
using LabFusion.Player;
using LabFusion.RPC;
using LabFusion.SDK.Gamemodes;
using LabFusion.SDK.Modules;
using UnityEngine;

[assembly: MelonLoader.MelonGame("Stress Level Zero", "BONELAB")]
[assembly: MelonLoader.MelonInfo(typeof(BonelabGunGame.FusionGunGameMod), "Fusion Gun Game", "1.0.9", "Local Mod")]

namespace BonelabGunGame;

public sealed class FusionGunGameMod : MelonLoader.MelonMod
{
    private HarmonyLib.Harmony _harmony;

    public override void OnInitializeMelon()
    {
        _harmony = new HarmonyLib.Harmony("local.bonelab.fusiongungame");
        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        if (FindMelon("LabFusion", "Lakatrazz") == null)
        {
            LoggerInstance.Warning("Fusion was not found; Gun Game will stay inactive.");
            return;
        }

        ModuleManager.RegisterModule<GunGameModule>();
        GamemodeRegistration.LoadGamemodes(Assembly.GetExecutingAssembly());
        LoggerInstance.Msg("Fusion Gun Game loaded. Select it from Fusion's gamemode list.");
    }

    public override void OnDeinitializeMelon()
    {
        _harmony?.UnpatchSelf();
    }
}

public sealed class GunGameModule : LabFusion.SDK.Modules.Module
{
    public override string Name => "Fusion Gun Game";
    public override string Author => "Local Mod";
    public override Version Version => new(1, 0, 9);
    public override ConsoleColor Color => ConsoleColor.Cyan;

    protected override void OnModuleRegistered()
    {
        ModuleMessageManager.RegisterHandler<KillReportMessage>();
        ModuleMessageManager.RegisterHandler<TierUpdateMessage>();
    }
}

public sealed class FusionGunGameMode : Gamemode
{
    private static readonly string[] FallbackBarcodes =
    {
        "c1534c5a-fcfc-4f43-8fb0-d29531393131",
        "c1534c5a-bcb7-4f02-a4f5-da9550333530",
        "c1534c5a-2a4f-481f-8542-cc9545646572",
        "c1534c5a-d00c-4aa8-adfd-3495534d474d",
        "c1534c5a-8d03-42de-93c7-f595534d4755",
        "c1534c5a-4c47-428d-b5a5-b05747756e56",
        "c1534c5a-40e5-40e0-8139-194347756e55",
        "c1534c5a-a6b5-4177-beb8-04d947756e41",
        "c1534c5a-9112-49e5-b022-9c955269666c",
        "c1534c5a-c061-4c5c-a5e2-3d955269666c",
    };
    private const int MatchTiers = 10;
    private static readonly System.Random Random = new();

    private static FusionGunGameMode _instance;
    private readonly GunGameProgression _progression = new(MatchTiers);
    private byte? _lastAttacker;
    private float _lastAttackTime;
    private ushort? _currentNetworkWeaponId;
    private string _currentBarcode;
    private readonly Dictionary<byte, (int tier, string barcode)> _lastAppliedUpdate = new();
    private readonly Dictionary<byte, float> _recentVictimDeaths = new();
    private int _respawnGeneration;
    internal static float LocalInvulnerabilityUntil = float.NegativeInfinity;
    internal static bool ImmunityHitLogged;

    public override string Title => "Gun Game";
    public override string Author => "Local Mod";
    public override string Description => "Get a player kill to advance to the next weapon. First to finish the weapon ladder wins.";

    public override void OnGamemodeRegistered()
    {
        _instance = this;
        base.OnGamemodeRegistered();
    }

    public override void OnGamemodeUnregistered()
    {
        if (_instance == this)
            _instance = null;
        base.OnGamemodeUnregistered();
    }

    public override void OnGamemodeStarted()
    {
        SdkGunCatalog.Load();
        _progression.ResetMatch();
        _lastAppliedUpdate.Clear();
        _recentVictimDeaths.Clear();
        _lastAttacker = null;
        LocalHealth.OnAttackedByPlayer += OnAttackedByPlayer;
        LocalHealth.OnRespawn += OnLocalRespawn;
        SpawnLocalTier(0, PickGun());
        base.OnGamemodeStarted();
    }

    public override void OnGamemodeStopped()
    {
        LocalHealth.OnAttackedByPlayer -= OnAttackedByPlayer;
        LocalHealth.OnRespawn -= OnLocalRespawn;
        LocalInvulnerabilityUntil = float.NegativeInfinity;
        ImmunityHitLogged = false;
        _lastAttacker = null;
        _respawnGeneration++;
        DespawnCurrentWeapon();
        _progression.ResetMatch();
        _lastAppliedUpdate.Clear();
        _recentVictimDeaths.Clear();
        base.OnGamemodeStopped();
    }

    protected override void OnPlayerLeft(PlayerID player)
    {
        _progression.ResetPlayer(player.SmallID);
        base.OnPlayerLeft(player);
    }

    private void OnAttackedByPlayer(Il2CppSLZ.Marrow.Combat.Attack attack, PlayerDamageReceiver.BodyPart bodyPart, PlayerID attacker)
    {
        if (attacker == null || !attacker.IsValid)
            return;

        _lastAttacker = attacker.SmallID;
        _lastAttackTime = Time.time;
    }

    private void OnLocalRespawn()
    {
        _lastAttacker = null;
        LocalInvulnerabilityUntil = Time.time + 3f;
        ImmunityHitLogged = false;
        MelonLoader.MelonLogger.Msg("Gun Game: 3-second respawn immunity started.");
        var generation = ++_respawnGeneration;
        MelonLoader.MelonCoroutines.Start(RespawnRoutine(generation));
    }

    private IEnumerator RespawnRoutine(int generation)
    {
        // Fusion's respawn event can run before the local rig has finished rebuilding.
        yield return new WaitForSeconds(0.8f);
        if (generation != _respawnGeneration || !IsStarted || PlayerIDManager.LocalID == null)
            yield break;

        MoveToRandomSpawn();
        SpawnLocalTier(_progression.GetTier(PlayerIDManager.LocalID.SmallID), _currentBarcode ?? PickGun());
    }

    internal static void OnLocalPlayerDeath(Player_Health health)
    {
        var mode = _instance;
        if (mode == null || !mode.IsStarted || health == null)
            return;

        var localRig = Player.RigManager;
        if (localRig == null || health.transform == null || health.transform.root != localRig.transform.root)
            return;

        if (!mode._lastAttacker.HasValue || Time.time - mode._lastAttackTime > 10f)
        {
            mode._lastAttacker = null;
            return;
        }

        var victimId = PlayerIDManager.LocalID;
        var killerId = mode._lastAttacker.Value;
        mode._lastAttacker = null;
        if (victimId == null || killerId == victimId.SmallID)
            return;

        var report = new KillReportData { KillerId = killerId, VictimId = victimId.SmallID };
        if (NetworkInfo.IsHost)
            ProcessKillReport(report);
        else
            MessageRelay.RelayModule<KillReportMessage, KillReportData>(
                report,
                new MessageRoute(RelayType.ToServer, NetworkChannel.Reliable));

        MelonLoader.MelonLogger.Msg($"Gun Game: reported player kill {killerId} -> {victimId.SmallID}.");
    }

    internal static void HandleKillReport(KillReportData data, ReceivedMessage received)
    {
        var mode = _instance;
        if (mode == null || !mode.IsStarted || !NetworkInfo.IsHost)
            return;

        // A client can only report a death belonging to its own player ID.
        if (received.Sender.HasValue && received.Sender.Value != data.VictimId)
            return;

        ProcessKillReport(data);
    }

    private static void ProcessKillReport(KillReportData data)
    {
        var mode = _instance;
        if (mode == null || !mode.IsStarted || !NetworkInfo.IsHost)
            return;

        if (data.KillerId == data.VictimId)
            return;

        var now = Time.time;
        if (mode._recentVictimDeaths.TryGetValue(data.VictimId, out var previousDeath) && now - previousDeath < 3f)
            return;
        mode._recentVictimDeaths[data.VictimId] = now;

        if (!mode._progression.RecordKill(data.KillerId, data.VictimId, out var tier))
            return;

        var winner = mode._progression.HasWon(data.KillerId);
        MelonLoader.MelonLogger.Msg($"Gun Game: player {data.KillerId} advanced to weapon tier {tier + 1}.");
        var barcode = mode.PickGun();
        var update = new TierUpdateData { PlayerId = data.KillerId, Tier = tier, IsWinner = winner, Barcode = barcode };
        // Apply locally because host broadcasts may exclude their sender. If the
        // host does receive its own relay, ApplyTier's update key makes it a no-op.
        mode.ApplyTier(update.PlayerId, update.Tier, update.IsWinner, update.Barcode);
        MessageRelay.RelayModule<TierUpdateMessage, TierUpdateData>(
            update,
            new MessageRoute(RelayType.ToClients, NetworkChannel.Reliable));
    }

    internal static void HandleTierUpdate(TierUpdateData data)
    {
        _instance?.ApplyTier(data.PlayerId, data.Tier, data.IsWinner, data.Barcode);
    }

    private void ApplyTier(byte playerId, int tier, bool winner, string barcode)
    {
        if (_lastAppliedUpdate.TryGetValue(playerId, out var previous) &&
            previous.tier == tier && string.Equals(previous.barcode, barcode, StringComparison.Ordinal))
            return;
        _lastAppliedUpdate[playerId] = (tier, barcode);
        _progression.ApplyAuthoritativeTier(playerId, tier);

        if (playerId == PlayerIDManager.LocalID.SmallID)
        {
            if (winner)
                MelonLoader.MelonLogger.Msg("Gun Game: you won the match!");
            else
                SpawnLocalTier(tier, barcode);
        }

        if (winner)
        {
            MelonLoader.MelonLogger.Msg($"Gun Game: player {playerId} won the match.");
            if (NetworkInfo.IsHost)
                GamemodeManager.StopGamemode();
        }
    }

    private string PickGun()
    {
        var pool = SdkGunCatalog.Barcodes;
        return pool.Count > 0 ? pool[Random.Next(pool.Count)] : FallbackBarcodes[Random.Next(FallbackBarcodes.Length)];
    }

    private void SpawnLocalTier(int tier, string barcode)
    {
        if (tier < 0 || tier >= _progression.TierCount)
            return;

        try
        {
            DespawnCurrentWeapon();
            var rig = Player.RigManager;
            if (rig == null || rig.physicsRig == null || rig.physicsRig.m_head == null)
                return;

            var head = rig.physicsRig.m_head;
            var spawnPosition = head.position + head.forward * 0.8f;
            if (string.IsNullOrWhiteSpace(barcode))
                barcode = PickGun();
            _currentBarcode = barcode;

            if (NetworkInfo.HasServer)
            {
                var spawnable = new Spawnable
                {
                    crateRef = new SpawnableCrateReference(barcode)
                };

                NetworkAssetSpawner.Spawn(new NetworkAssetSpawner.SpawnRequestInfo
                {
                    Spawnable = spawnable,
                    Position = spawnPosition,
                    Rotation = head.rotation,
                    SpawnEffect = false,
                    SpawnSource = (EntitySource)0,
                    SpawnCallback = (NetworkAssetSpawner.SpawnCallbackInfo info) =>
                    {
                        if (info.Entity != null)
                        {
                            _currentNetworkWeaponId = info.Entity.ID;
                            MelonLoader.MelonLogger.Msg($"Gun Game: spawned synchronized weapon tier {tier + 1} (entity {info.Entity.ID}).");
                        }
                        else
                            MelonLoader.MelonLogger.Warning("Gun Game: Fusion returned a weapon spawn without a network entity.");
                    }
                });
                MelonLoader.MelonLogger.Msg($"Gun Game: requesting synchronized weapon tier {tier + 1}.");
                return;
            }

            HelperMethods.SpawnCrate(
                barcode,
                spawnPosition,
                head.rotation,
                Vector3.one,
                false,
                null,
                null);
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Error($"Gun Game could not spawn tier {tier + 1}: {ex}");
        }
    }

    private void MoveToRandomSpawn()
    {
        try
        {
            var rig = Player.RigManager;
            if (rig == null)
                return;

            // BONELAB's IL2CPP build does not implement Object.FindObjectsOfType(Type).
            // Sample ground positions around the game's valid respawn point instead.
            var respawnOrigin = rig.transform.position;
            var candidates = new List<Vector3>();
            const float radius = 45f;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var angle = (float)Random.NextDouble() * 6.28318530718f;
                var distance = 12f + (float)Random.NextDouble() * (radius - 12f);
                var x = respawnOrigin.x + Mathf.Cos(angle) * distance;
                var z = respawnOrigin.z + Mathf.Sin(angle) * distance;
                var rayOrigin = new Vector3(x, respawnOrigin.y + 100f, z);
                if (!Physics.Raycast(rayOrigin, Vector3.down, out var hit, 250f) || hit.collider == null || hit.normal.y < 0.65f)
                    continue;

                var point = hit.point + Vector3.up * 0.2f;
                if (Vector3.Distance(point, respawnOrigin) >= 10f)
                    candidates.Add(point);
            }

            if (candidates.Count == 0)
            {
                MelonLoader.MelonLogger.Warning("Gun Game: no walkable ground found near the map's respawn point; keeping the normal location.");
                return;
            }

            var position = candidates[Random.Next(candidates.Count)];
            rig.TeleportToPosition(position, true);
            MelonLoader.MelonLogger.Msg($"Gun Game: teleported from respawn point {respawnOrigin} to random point {position}.");
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"Gun Game: random respawn failed; using normal respawn location. {ex.Message}");
        }
    }

    private void DespawnCurrentWeapon()
    {
        var networkId = _currentNetworkWeaponId;
        _currentNetworkWeaponId = null;

        try
        {
            if (NetworkInfo.HasServer)
            {
                if (networkId.HasValue)
                {
                    NetworkAssetSpawner.Despawn(new NetworkAssetSpawner.DespawnRequestInfo
                    {
                        EntityID = networkId.Value,
                        DespawnEffect = false
                    });
                }
                return;
            }
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"Gun Game could not remove the previous weapon: {ex.Message}");
        }
    }
}

public sealed class KillReportData : INetSerializable
{
    public byte KillerId;
    public byte VictimId;
    public int? GetSize() => 2;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref KillerId);
        serializer.SerializeValue(ref VictimId);
    }
}

public sealed class KillReportMessage : ModuleMessageHandler
{
    protected override void OnHandleMessage(ReceivedMessage received)
    {
        FusionGunGameMode.HandleKillReport(received.ReadData<KillReportData>(), received);
    }
}

internal static class SdkGunCatalog
{
    private static readonly List<string> _barcodes = new();
    public static IReadOnlyList<string> Barcodes => _barcodes;

    public static void Load()
    {
        if (_barcodes.Count > 0)
            return;

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Stress Level Zero", "BONELAB", "Mods");
        if (!Directory.Exists(root))
        {
            MelonLoader.MelonLogger.Warning($"Gun Game: SDK mods folder was not found at {root}; using fallback firearms.");
            return;
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedPallets = 0;
        var excludedWeapons = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.pallet.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (!document.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
                    continue;

                if (IsExcludedPallet(file, objects))
                {
                    excludedPallets++;
                    continue;
                }

                foreach (var item in objects.EnumerateObject())
                {
                    var crate = item.Value;
                    if (!crate.TryGetProperty("isa", out var isa) ||
                        !isa.TryGetProperty("type", out var type) ||
                        !(type.GetString() ?? string.Empty).StartsWith("crate-spawnable", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!crate.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array ||
                        !tags.EnumerateArray().Any(tag => string.Equals(tag.GetString(), "Gun", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (HasExcludedGunName(crate))
                    {
                        excludedWeapons++;
                        continue;
                    }

                    if (crate.TryGetProperty("barcode", out var barcode) && barcode.ValueKind == JsonValueKind.String)
                    {
                        var value = barcode.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            unique.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"Gun Game: couldn't read SDK pallet {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        _barcodes.AddRange(unique);
        MelonLoader.MelonLogger.Msg($"Gun Game: loaded {_barcodes.Count} SDK guns; skipped {excludedPallets} excluded pallets and {excludedWeapons} excluded guns.");
        if (_barcodes.Count == 0)
            MelonLoader.MelonLogger.Warning("Gun Game: no Gun-tagged SDK crates found; using fallback firearms.");
    }

    private static bool IsExcludedPallet(string file, JsonElement objects)
    {
        var path = file.Replace('\\', '/');
        if (ContainsExcludedPack(path))
            return true;

        foreach (var item in objects.EnumerateObject())
        {
            var crate = item.Value;
            if (!crate.TryGetProperty("isa", out var isa) ||
                !isa.TryGetProperty("type", out var type) ||
                !(type.GetString() ?? string.Empty).StartsWith("pallet", StringComparison.OrdinalIgnoreCase))
                continue;

            return ContainsExcludedPack(GetString(crate, "title")) ||
                   ContainsExcludedPack(GetString(crate, "barcode"));
        }

        return false;
    }

    private static bool HasExcludedGunName(JsonElement crate)
    {
        var title = GetString(crate, "title");
        var barcode = GetString(crate, "barcode");
        return ContainsTranqName(title) || ContainsTranqName(barcode) ||
               barcode.StartsWith("CyberBlue.LowPolyWeaponPack.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsExcludedPack(string value)
    {
        return value.Contains("deadpool", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("lowpoly", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("low poly", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsTranqName(string value)
    {
        return value.Contains("tranq", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("tranquil", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}

public sealed class TierUpdateData : INetSerializable
{
    public byte PlayerId;
    public int Tier;
    public bool IsWinner;
    public string Barcode;
    public int? GetSize() => null;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref PlayerId);
        serializer.SerializeValue(ref Tier);
        serializer.SerializeValue(ref IsWinner);
        serializer.SerializeValue(ref Barcode);
    }
}

public sealed class TierUpdateMessage : ModuleMessageHandler
{
    protected override void OnHandleMessage(ReceivedMessage received)
    {
        FusionGunGameMode.HandleTierUpdate(received.ReadData<TierUpdateData>());
    }
}

[HarmonyPatch(typeof(Player_Health), nameof(Player_Health.Death))]
internal static class PlayerDeathPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player_Health __instance)
    {
        FusionGunGameMode.OnLocalPlayerDeath(__instance);
    }
}

[HarmonyPatch(typeof(Player_Health), nameof(Player_Health.TAKEDAMAGE))]
internal static class RespawnImmunityPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player_Health __instance)
    {
        return !ShouldBlock(__instance);
    }

    internal static bool ShouldBlock(Component health)
    {
        if (Time.time >= FusionGunGameMode.LocalInvulnerabilityUntil || health == null || health.transform == null)
            return false;

        var rig = Player.RigManager;
        if (rig == null || health.transform.root != rig.transform.root)
            return false;

        if (!FusionGunGameMode.ImmunityHitLogged)
        {
            FusionGunGameMode.ImmunityHitLogged = true;
            MelonLoader.MelonLogger.Msg("Gun Game: respawn immunity blocked incoming damage.");
        }

        return true;
    }
}

[HarmonyPatch(typeof(Health), nameof(Health.TAKEDAMAGE))]
internal static class BaseHealthRespawnImmunityPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Health __instance)
    {
        return !RespawnImmunityPatch.ShouldBlock(__instance);
    }
}

[HarmonyPatch(typeof(Player_Health), nameof(Player_Health.OnReceivedDamage))]
internal static class PlayerDamageRespawnImmunityPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Player_Health __instance)
    {
        return !RespawnImmunityPatch.ShouldBlock(__instance);
    }
}
