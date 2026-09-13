using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Cranberry.Zone.World;

/// <summary>
/// The space and everything in it. Owned by exactly one <see cref="Match"/> and touched only inside
/// <see cref="Match.Tick"/>.
/// <para>
/// There is no entity handle type: an object reference is the internal handle, <c>Removed</c> is the
/// staleness check, and the dictionaries are cold-path lookups for the guids and slots the client
/// names back. Players and entities both live in stable slot tables — a slot is an interest-grid key
/// and must not move under a live entity, which is exactly what a compacting list would do.
/// </para>
/// </summary>
public sealed class World
{
    private readonly MatchPlayer?[] _players;
    private readonly Dictionary<EntityId, MatchPlayer> _playersById = [];
    private readonly WorldEntity?[] _entities;
    private readonly Dictionary<EntityId, WorldEntity> _entitiesById = [];
    private readonly Stack<int> _freeEntitySlots = new();
    private int _entityHighWater;

    public World(ushort matchId, MatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        MatchId = matchId;
        Settings = settings;
        Ids = new EntityAllocator(matchId);
        _players = new MatchPlayer?[settings.MaxPlayers];
        _entities = new WorldEntity?[settings.EntityCapacity];
        Grid = new InterestGrid(settings.MaxPlayers + settings.EntityCapacity);
    }

    public ushort MatchId { get; }

    public MatchSettings Settings { get; }

    public EntityAllocator Ids { get; }

    public InterestGrid Grid { get; }

    /// <summary>Stable slots; a null slot is free. Systems scan this and skip nulls.</summary>
    public ReadOnlySpan<MatchPlayer?> Players => _players;

    public int PlayerCount { get; private set; }

    /// <summary>Scanned, not cached: at 50 slots the scan is cheaper than an invariant to maintain.</summary>
    public int AliveCount
    {
        get
        {
            int alive = 0;
            foreach (MatchPlayer? player in _players)
            {
                if (player is { Life: LifeState.Alive })
                {
                    alive++;
                }
            }

            return alive;
        }
    }

    /// <summary>Stable slots up to the high-water mark; a null slot is free.</summary>
    public ReadOnlySpan<WorldEntity?> Entities => _entities.AsSpan(0, _entityHighWater);

    public int EntityCount { get; private set; }

    // --- players ---

    /// <summary>
    /// Adds a body to the match. The sink may be null: a disconnected player keeps its slot, and a
    /// synthetic test player never has one.
    /// </summary>
    public MatchPlayer AddPlayer(ulong accountGuid, string name, IPlayerSink? sink)
    {
        int slot = -1;
        for (int i = 0; i < _players.Length; i++)
        {
            if (_players[i] is null)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            throw new InvalidOperationException($"Match {MatchId} is full ({_players.Length} slots).");
        }

        var player = new MatchPlayer(slot, Ids.Next(EntityKind.Character), accountGuid, name)
        {
            Sink = sink,
            Position = new Vector3(Settings.StagingSpawn.X, Settings.StagingSpawn.Y, Settings.StagingSpawn.Z),
            Health = Settings.StartingHealth,
        };

        player.GridCell = InterestGrid.CellOf(player.Position);
        _players[slot] = player;
        _playersById[player.Id] = player;
        PlayerCount++;
        Grid.Add(PlayerKey(player), player.GridCell);
        return player;
    }

    public void RemovePlayer(MatchPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (_players[player.Slot] != player)
        {
            return;
        }

        Grid.Remove(PlayerKey(player), player.GridCell);
        _players[player.Slot] = null;
        _playersById.Remove(player.Id);
        player.Sink = null;
        player.View.Clear();
        PlayerCount--;
    }

    public bool TryGetPlayer(EntityId id, [NotNullWhen(true)] out MatchPlayer? player) =>
        _playersById.TryGetValue(id, out player);

    public MatchPlayer? PlayerAt(int slot) =>
        (uint)slot < (uint)_players.Length ? _players[slot] : null;

    // --- entities ---

    public WorldEntity SpawnVehicle(
        uint vehicleId,
        uint modelId,
        in Vector3 position,
        in Quaternion rotation,
        EntityId owner)
    {
        WorldEntity entity = Spawn(EntityKind.Vehicle, position);
        entity.Rotation = rotation;
        entity.VehicleId = vehicleId;
        entity.VehicleModelId = modelId;
        entity.Owner = owner;
        return entity;
    }

    public WorldEntity SpawnGroundItem(
        uint itemDefinitionId,
        uint groundModelId,
        uint nameId,
        uint count,
        in Vector3 position)
    {
        WorldEntity entity = Spawn(EntityKind.GroundItem, position);
        entity.ItemDefinitionId = itemDefinitionId;
        entity.GroundModelId = groundModelId;
        entity.NameId = nameId;
        entity.Count = count;
        return entity;
    }

    public bool TryGetEntity(EntityId id, [NotNullWhen(true)] out WorldEntity? entity) =>
        _entitiesById.TryGetValue(id, out entity);

    /// <summary>Flags the entity for the end-of-tick sweep. Idempotent.</summary>
    public void Despawn(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity.Removed = true;
    }

    /// <summary>Drops entities flagged <c>Removed</c> and frees their grid cells. End of tick only.</summary>
    public int SweepRemoved()
    {
        int swept = 0;
        for (int slot = 0; slot < _entityHighWater; slot++)
        {
            if (_entities[slot] is not { Removed: true } entity)
            {
                continue;
            }

            Grid.Remove(EntityKey(entity), entity.GridCell);
            _entitiesById.Remove(entity.Id);
            _entities[slot] = null;
            entity.Slot = -1;
            _freeEntitySlots.Push(slot);
            EntityCount--;
            swept++;
        }

        return swept;
    }

    // --- grid ---

    /// <summary>Interest-grid key for a player: its slot.</summary>
    public int PlayerKey(MatchPlayer player) => player.Slot;

    /// <summary>Interest-grid key for an entity: its slot, above the player key space.</summary>
    public int EntityKey(WorldEntity entity) => _players.Length + entity.Slot;

    /// <summary>Resolves a key returned by <see cref="InterestGrid.Query"/> back to a player.</summary>
    public MatchPlayer? PlayerFromKey(int key) =>
        key >= 0 && key < _players.Length ? _players[key] : null;

    /// <summary>Resolves a key returned by <see cref="InterestGrid.Query"/> back to an entity.</summary>
    public WorldEntity? EntityFromKey(int key)
    {
        int slot = key - _players.Length;
        return (uint)slot < (uint)_entities.Length ? _entities[slot] : null;
    }

    /// <summary>Re-buckets a player whose position changed. No-op while it stays in its cell.</summary>
    public void UpdateCell(MatchPlayer player)
    {
        ushort cell = InterestGrid.CellOf(player.Position);
        if (cell == player.GridCell)
        {
            return;
        }

        Grid.Move(PlayerKey(player), player.GridCell, cell);
        player.GridCell = cell;
    }

    public void UpdateCell(WorldEntity entity)
    {
        ushort cell = InterestGrid.CellOf(entity.Position);
        if (cell == entity.GridCell)
        {
            return;
        }

        Grid.Move(EntityKey(entity), entity.GridCell, cell);
        entity.GridCell = cell;
    }

    private WorldEntity Spawn(EntityKind kind, in Vector3 position)
    {
        int slot;
        if (_freeEntitySlots.Count > 0)
        {
            slot = _freeEntitySlots.Pop();
        }
        else if (_entityHighWater < _entities.Length)
        {
            slot = _entityHighWater++;
        }
        else
        {
            throw new InvalidOperationException(
                $"Match {MatchId} exhausted its {_entities.Length} entity slots.");
        }

        var entity = new WorldEntity
        {
            Slot = slot,
            Id = Ids.Next(kind),
            Kind = kind,
            Position = position,
            GridCell = InterestGrid.CellOf(position),
        };

        _entities[slot] = entity;
        _entitiesById[entity.Id] = entity;
        EntityCount++;
        Grid.Add(EntityKey(entity), entity.GridCell);
        return entity;
    }
}
