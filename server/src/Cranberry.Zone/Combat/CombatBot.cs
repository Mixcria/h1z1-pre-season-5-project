using System.Numerics;

namespace Cranberry.Zone.Combat;

public readonly record struct BotOpponent(ulong Guid, Vector3 Position);
public readonly record struct BotShot(ulong VictimGuid, Vector3 AimPoint, bool Hit);

/// <summary>Small, deterministic outdoor combat AI. Policy values are server test settings.</summary>
public sealed class CombatBot
{
    public const int MaximumPerMatch = 50;
    public const int TickMs = 100;
    public const int SpawnGraceMs = 5000;
    public const float DetectionMetres = 150;
    private readonly long _spawnReadyMs;
    private uint _random;
    private long _lastTickMs;
    private long _nextShotMs;
    private ulong _opponent;
    private int _burst;
    private int _rounds = 30;

    public CombatBot(PracticeTarget body, string difficulty, long nowMs)
    {
        Body = body;
        Difficulty = difficulty;
        _lastTickMs = nowMs;
        // Adjacent small seeds give xorshift strongly correlated opening rolls, and OR 1
        // aliases adjacent even/odd IDs. Mix the whole identity before generating any rolls.
        _random = SeedFor(body.WorldGuid);
        _spawnReadyMs = nowMs + SpawnGraceMs;
        _nextShotMs = _spawnReadyMs;
    }

    public PracticeTarget Body { get; }
    public string Difficulty { get; }
    public float Heading { get; private set; }
    public float Speed { get; private set; }
    public int ShotsFired { get; private set; }
    public int Hits { get; private set; }
    public int Magazine => _rounds;
    public bool Reloading { get; private set; }
    public ulong Opponent => _opponent;

    public BotShot? Tick(long nowMs, IReadOnlyList<BotOpponent> opponents,
        Func<float, float, float?> ground, bool frozen)
    {
        float dt = Math.Clamp((nowMs - _lastTickMs) / 1000f, 0, 0.2f);
        _lastTickMs = nowMs;
        Speed = 0;
        if (!Body.IsAlive || frozen)
        {
            _nextShotMs = Math.Max(_nextShotMs, nowMs + 1000);
            return null;
        }
        BotOpponent? nearest = null;
        float distance = DetectionMetres;
        foreach (var candidate in opponents)
        {
            float range = Vector3.Distance(Body.Position, candidate.Position);
            if (range < distance) { nearest = candidate; distance = range; }
        }
        if (nearest is not { } target)
        {
            _opponent = 0;
            return null;
        }
        if (_opponent != target.Guid)
        {
            _opponent = target.Guid;
            int reactionMs = Difficulty == "hard" ? 450 : Difficulty == "easy" ? 1400 : 900;
            int staggerMs = (int)(RandomUnit() * 600);
            // Keep spawn/reload deadlines even if a target changes. Add the reaction and
            // stagger after the preparation period, so the whole group cannot fire at expiry.
            _nextShotMs = Math.Max(_nextShotMs, Math.Max(nowMs, _spawnReadyMs) + reactionMs + staggerMs);
        }
        Vector3 delta = target.Position - Body.Position;
        Heading = MathF.Atan2(delta.X, delta.Z);
        Vector3 forward = new(MathF.Sin(Heading), 0, MathF.Cos(Heading));
        // Approach, then strafe with a changing direction. Leave a comfortable firing distance.
        float side = ((nowMs / 2400 + (long)(Body.WorldGuid & 7)) & 1) == 0 ? 1 : -1;
        Vector3 strafe = new(forward.Z * side, 0, -forward.X * side);
        Vector3 direction = distance > 28 ? forward : distance < 10 ? -forward : strafe;
        float speed = distance > 50 ? 5.5f : 3f;
        Vector3 next = Body.Position + direction * (speed * dt);
        if (!TryGroundedStep(Body.Position, ref next, ground))
        {
            next = Body.Position + strafe * (speed * dt);
            if (!TryGroundedStep(Body.Position, ref next, ground)) next = Body.Position;
        }
        Speed = dt > 0 ? Vector3.Distance(next, Body.Position) / dt : 0;
        Body.Move(next, Heading);
        if (distance > 65 || nowMs < _spawnReadyMs || nowMs < _nextShotMs
            || !HasTerrainSight(Body.Position + Vector3.UnitY * 1.5f, target.Position + Vector3.UnitY, ground))
            return null;
        if (Reloading) { Reloading = false; _rounds = 30; }
        _rounds--;
        ShotsFired++;
        if (_rounds == 0) { Reloading = true; _nextShotMs = nowMs + 2500; }
        else _nextShotMs = nowMs + (++_burst % 3 == 0 ? 900 : 250);
        float chance = Difficulty == "easy" ? 0.18f : Difficulty == "hard" ? 0.65f : 0.35f;
        chance *= Math.Clamp(35f / Math.Max(1, distance), 0.4f, 1f);
        bool hit = RandomUnit() < chance;
        if (hit) Hits++;
        Vector3 aim = target.Position + Vector3.UnitY;
        if (!hit) aim += new Vector3(forward.Z * 2.5f, 0.5f, -forward.X * 2.5f);
        return new(target.Guid, aim, hit);
    }

    public static bool HasTerrainSight(Vector3 from, Vector3 to, Func<float, float, float?> ground)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / 2));
        for (int i = 1; i < steps; i++)
        {
            Vector3 at = Vector3.Lerp(from, to, i / (float)steps);
            if (ground(at.X, at.Z) is not float height || height > at.Y) return false;
        }
        return true;
    }

    private static bool TryGroundedStep(Vector3 origin, ref Vector3 next, Func<float, float, float?> ground)
    {
        if (ground(next.X, next.Z) is not float height || !float.IsFinite(height)
            || MathF.Abs(height - origin.Y) > Math.Max(0.6f, Vector3.Distance(origin, next) * 1.5f)) return false;
        next.Y = height;
        return true;
    }

    private float RandomUnit()
    {
        _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5;
        return (_random & 0xffffff) / 16777216f;
    }

    private static uint SeedFor(ulong identity)
    {
        unchecked
        {
            ulong mixed = identity + 0x9e3779b97f4a7c15UL;
            mixed = (mixed ^ (mixed >> 30)) * 0xbf58476d1ce4e5b9UL;
            mixed = (mixed ^ (mixed >> 27)) * 0x94d049bb133111ebUL;
            mixed ^= mixed >> 31;
            uint seed = (uint)(mixed ^ (mixed >> 32));
            return seed == 0 ? 0xa341316cU : seed;
        }
    }
}
