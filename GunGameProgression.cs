using System;
using System.Collections.Generic;

namespace BonelabGunGame;

/// <summary>
/// Server-authoritative progression rules for a Fusion Gun Game match.
/// The Fusion adapter should call RecordKill only after validating a real
/// player-vs-player kill, then give the returned tier's weapon to that player.
/// </summary>
public sealed class GunGameProgression
{
    private readonly Dictionary<int, int> _tiers = new();
    private readonly int _tierCount;

    public GunGameProgression(int tierCount)
    {
        if (tierCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tierCount));
        _tierCount = tierCount;
    }

    public int TierCount => _tierCount;

    public int GetTier(int playerId) => _tiers.TryGetValue(playerId, out var tier) ? tier : 0;

    /// <summary>Advances one tier; returns false once the player has won.</summary>
    public bool RecordKill(int killerId, int victimId, out int newTier)
    {
        newTier = GetTier(killerId);
        if (killerId == victimId || newTier >= _tierCount)
            return false;

        newTier++;
        _tiers[killerId] = newTier;
        return true;
    }

    /// <summary>Applies a tier received from the match host.</summary>
    public void ApplyAuthoritativeTier(int playerId, int tier)
    {
        if (tier < 0 || tier > _tierCount)
            throw new ArgumentOutOfRangeException(nameof(tier));

        if (tier == 0)
            _tiers.Remove(playerId);
        else
            _tiers[playerId] = tier;
    }

    public bool HasWon(int playerId) => GetTier(playerId) >= _tierCount;

    public void ResetPlayer(int playerId) => _tiers.Remove(playerId);

    public void ResetMatch() => _tiers.Clear();
}
