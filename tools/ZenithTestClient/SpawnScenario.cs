using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

internal sealed class SpawnScenario(string accountId, string ticket, string characterId, string name, long playerId,
    bool challenges, bool shop)
{
    private const uint VfxComponent = 1013;
    private const uint PublicActorStats = 4021;
    private const uint GodstoneComponent = 2035;
    private const uint BowComponent = 4031;
    private const uint ArrowComponent = 4032;
    private const uint ProjectileComponent = 2030;
    private readonly Dictionary<long, string> _entities = new();
    private readonly Dictionary<long, HashSet<uint>> _authority = new();
    private int _responses;
    private long _characterId;
    private long _avatarId;
    private long _characterWithInterest;
    private bool _bodyPartsLinked;
    private bool _rejectedWrongHands;
    private bool _handsRegistered;
    private bool _mainWorld;
    private bool _worldLoaded;
    private bool _positionAccepted;
    private int _teleportsCompleted;
    private uint _lifeState = 3;
    private int _startingHealth;
    private int _health;
    private int _maxHealth;
    private float _totalHealth;
    private bool _recoveryAcknowledged;
    private readonly ChallengeScenario? _challenges = challenges ? new(playerId) : null;
    private readonly ShopScenario? _shop = shop ? new() : null;
    private long _shoulderBow;
    private bool _starterBowEquipped;
    private bool _starterAbilities;
    private bool _starterStatusEffects;
    private bool _bowDrawUpdated;
    private Guid _arrowCastId;
    private long _arrow;
    private bool _arrowAttached;
    private bool _arrowShot;
    private int _bowAmmo = -1;
    private int _combatAmmo = -1;
    private int _shotResponses;
    private int _cooldownUpdates;
    private int _airChopsEnded;
    private Guid _castId;
    private long _projectile;
    private bool _projectileInitialized;
    public bool Completed { get; private set; }

    public CommandRequestOp Start(long requestId) => Request(requestId, characterId, "invalid-ticket");

    public ZfOp? Handle(ZfOp op, long requestId)
    {
        if (_shop is not null)
        {
            var next = _shop.Handle(op, requestId, _characterId);
            Completed = _shop.Completed;
            if (next is not null || Completed) return next;
        }
        switch (op)
        {
            case CommandResponseOp { ComponentId: Ids.CharacterSpawnerComponent, CommandIndex: 3 } response:
                _responses++;
                switch (_responses)
                {
                    case 1:
                        Require(response.Status == StatusCode.PermissionDenied, "invalid account ticket");
                        return Request(requestId, "unknown-character", ticket);
                    case 2:
                        Require(response.Status == StatusCode.NotFound, "unknown character");
                        return Request(requestId, characterId, ticket);
                    case 3:
                        Require(response.Status == StatusCode.Success && response.Data.Length == 0, "empty spawn acknowledgment");
                        break;
                    case 4:
                        Require(response.Status == StatusCode.Success && response.Data.Length == 0, "idempotent spawn retry");
                        Require(_mainWorld, "main world assigned after spawn");
                        Require(_bodyPartsLinked, "avatar linked to replicated body parts");
                        return RegisterHands(requestId, Find("RightHand"), Find("LeftHand"));
                }
                break;
            case AddComponentOp { Data.ComponentId: Ids.Metadata } metadata:
                Require(_responses >= 3, "spawn acknowledgment before entity replication");
                _entities.Add(metadata.EntityId, StandardComponents.ReadMetadata(metadata.Data.Data)!);
                break;
            case RemoveEntityOp removed:
                _challenges?.CheckRemoval(removed.EntityId);
                _entities.Remove(removed.EntityId);
                _authority.Remove(removed.EntityId);
                break;
            case AddComponentOp { Data.ComponentId: Ids.InstancedAreaTriggerPrefabInfoComponent } prefab:
                _challenges?.RecordPrefab(prefab);
                break;
            case AddComponentOp { Data.ComponentId: 1016 } equipment when challenges && _characterId == 0:
                CheckStarterEquipment(equipment.Data.Data);
                break;
            case AddComponentOp { Data.ComponentId: 1009 } effects when _characterId == 0:
                var status = new SchemaReader(effects.Data.Data);
                _starterStatusEffects = status.ReadFieldId() == 1 && status.ReadListCount() == 0 && !status.HasMore;
                break;
            case AddComponentOp { Data.ComponentId: GodstoneComponent } abilities when challenges && _characterId == 0:
                CheckStarterAbilities(abilities.Data.Data);
                break;
            case AddComponentOp { Data.ComponentId: Ids.ZSyncComponent } bow
                when challenges && _entities.GetValueOrDefault(bow.EntityId) == "Item-CN-Bow-Sky1":
                if (ReadShoulderParent(bow.Data.Data) == _avatarId) _shoulderBow = bow.EntityId;
                break;
            case AddComponentOp { Data.ComponentId: BowComponent } bow when bow.EntityId == _shoulderBow:
                var initialBow = new SchemaReader(bow.Data.Data);
                Require(initialBow.ReadFieldId() == 2, "initial bow draw field");
                initialBow.ReadFloat();
                Require(initialBow.ReadFieldId() == 3, "initial bow nock field");
                initialBow.ReadFloat();
                Require(initialBow.ReadFieldId() == 4 && initialBow.ReadInt32() == 12, "bow starts with 12 arrows");
                break;
            case AddComponentOp { Data.ComponentId: Ids.ZSyncComponent } arrow when arrow.EntityId == _arrow:
                _arrowAttached = ReadShoulderParent(arrow.Data.Data) == _shoulderBow;
                break;
            case AddComponentOp { Data.ComponentId: 4022 } cast when _castId != Guid.Empty || _arrowCastId != Guid.Empty:
                var prewarm = new SchemaReader(cast.Data.Data);
                Require(prewarm.ReadFieldId() == 1, "prewarm ID field");
                var prewarmId = prewarm.ReadOptionalString();
                if (prewarmId == _castId.ToString()) _projectile = cast.EntityId;
                if (prewarmId == _arrowCastId.ToString()) _arrow = cast.EntityId;
                break;
            case AddComponentOp { Data.ComponentId: ProjectileComponent } projectile when projectile.EntityId == _projectile:
                _projectileInitialized = projectile.Data.Data.Length == 144;
                break;
            case AddComponentOp { Data.ComponentId: Ids.PlayerAvatarComponent } avatar when _characterId == 0:
                var initial = new SchemaReader(avatar.Data.Data);
                Require(ReadEntityId(ref initial, 1) == playerId, "avatar player link");
                CheckBodyParts(ref initial, 0, 0, 0);
                break;
            case AddComponentOp { Data.ComponentId: Ids.Interest } interest
                when _entities.GetValueOrDefault(interest.EntityId) == "PlayerCharacter":
                var interests = new SchemaReader(interest.Data.Data);
                Require(interests.ReadFieldId() == 1 && interests.ReadListCount() == 0 && !interests.HasMore,
                    "valid initial character interest map");
                _characterWithInterest = interest.EntityId;
                break;
            case AddComponentOp { Data.ComponentId: Ids.ActorStatsComponent } stats
                when _entities.GetValueOrDefault(stats.EntityId) == "PlayerCharacter":
                CheckActorStats(stats.Data.Data, initialized: false);
                break;
            case AddComponentOp { Data.ComponentId: PublicActorStats } stats
                when _characterId == 0 && _entities.GetValueOrDefault(stats.EntityId) == "PlayerCharacter":
                var initialStats = new SchemaReader(stats.Data.Data);
                Require(initialStats.ReadFieldId() == 7 && initialStats.ReadInt32() == 1, "starting actor level");
                Require(initialStats.ReadFieldId() == 8 && initialStats.ReadInt32() == 0, "starting actor XP");
                Require(initialStats.ReadFieldId() == 9, "current health field");
                _startingHealth = _health = initialStats.ReadInt32();
                Require(_health > 0, "positive starting health");
                Require(ReadInitialMaxHealth(ref initialStats) == 0, "effective max health starts unset");
                break;
            case ComponentSetAuthorityChangeOp { Authority: Authority.Authoritative } change:
                if (!_authority.TryGetValue(change.EntityId, out var sets))
                    _authority.Add(change.EntityId, sets = []);
                sets.Add(change.ComponentSetId);
                break;
            case ComponentUpdateOp { ComponentId: Ids.PlayerCharacterComponent } links when links.EntityId == playerId:
                CheckGraph(links.Data);
                return Request(requestId, characterId, ticket);
            case ComponentUpdateOp { ComponentId: Ids.PlayerAvatarComponent } links when links.EntityId == _avatarId:
                var parts = new SchemaReader(links.Data);
                CheckBodyParts(ref parts, Find("LeftHand"), Find("RightHand"), Find("Head"));
                _bodyPartsLinked = true;
                break;
            case ComponentUpdateOp { ComponentId: Ids.PlayerWorldComponent } world when world.EntityId == playerId:
                if (_challenges is not null)
                {
                    _challenges.RecordWorld(world.Data, initialEntry: !_mainWorld);
                    if (_mainWorld) return _challenges.AreaChanged(requestId, _avatarId);
                    _mainWorld = true;
                }
                else _mainWorld = world.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0, 0, 0 });
                break;
            case ComponentUpdateOp { ComponentId: Ids.NetworkedObjectInteractionComponent } hands when hands.EntityId == _characterId:
                var handLinks = new SchemaReader(hands.Data);
                Require(handLinks.ReadFieldId() == 2 && handLinks.ReadInt64() == Find("LeftHand"), "left interaction hand");
                Require(handLinks.ReadFieldId() == 3 && handLinks.ReadInt64() == Find("RightHand") && !handLinks.HasMore,
                    "right interaction hand");
                _handsRegistered = true;
                break;
            case CommandResponseOp { ComponentId: Ids.NetworkedObjectInteractionComponent, CommandIndex: 2 } hands:
                if (!_rejectedWrongHands)
                {
                    Require(hands.Status == StatusCode.PermissionDenied && !_handsRegistered, "swapped hands rejected");
                    _rejectedWrongHands = true;
                    return RegisterHands(requestId, Find("LeftHand"), Find("RightHand"));
                }
                Require(hands.Status == StatusCode.Success && hands.Data.Length == 0 && _handsRegistered,
                    "hand registration acknowledgment and component update");
                var ready = new SchemaWriter();
                ready.AddEmptyOption(1);
                ready.AddBool(2, false);
                return WorldCommand(requestId, _avatarId, Ids.TeleportComponent, 3, ready.ToArray());
            case CommandResponseOp { ComponentId: Ids.TeleportComponent, CommandIndex: 3 } readyResponse:
                Require(readyResponse.Status == StatusCode.Success && readyResponse.Data.Length == 0, "avatar ready response");
                return WorldCommand(requestId, playerId, Ids.PlayerWorldComponent, 2, []);
            case CommandResponseOp { ComponentId: Ids.PlayerWorldComponent, CommandIndex: 2 } loaded:
                Require(loaded.Status == StatusCode.Success && loaded.Data.Length == 0, "world loaded response");
                _worldLoaded = true;
                break;
            case CommandResponseOp { ComponentId: Ids.PlayerWorldComponent, CommandIndex: 4 } areas when _challenges is not null:
                return _challenges.EnterFirstArea(areas, _avatarId);
            case CommandResponseOp { ComponentId: Ids.PlayerWorldComponent, CommandIndex: 10 } returned when _challenges is not null:
                Require(returned.Status == StatusCode.Success && returned.Data.Length == 0, "return to Neo City acknowledged");
                break;
            case ComponentUpdateOp { ComponentId: BowComponent } draw when draw.EntityId == _shoulderBow:
                var bowUpdate = new SchemaReader(draw.Data);
                var bowField = bowUpdate.ReadFieldId();
                if (bowField == 2)
                {
                    _bowDrawUpdated = bowUpdate.ReadFloat() == 0.5f &&
                        bowUpdate.ReadFieldId() == 3 && bowUpdate.ReadFloat() == -0.25f && !bowUpdate.HasMore;
                    break;
                }
                Require(bowField == 4, "ammo count update");
                _bowAmmo = bowUpdate.ReadInt32();
                Require(bowUpdate.ReadFieldId() == 6 && bowUpdate.ReadFloat() >= 0 && !bowUpdate.HasMore &&
                    _bowAmmo == _combatAmmo, "ammo count and recharge are replicated");
                if (_bowAmmo == 12 && _airChopsEnded == 1) return StartAirChopsCooldown(requestId);
                break;
            case ComponentUpdateOp { ComponentId: 1010 } combat when combat.EntityId == _characterId && challenges:
                var ammo = new SchemaReader(combat.Data);
                Require(ammo.ReadFieldId() == 13 && ammo.ReadOptionHasValue(), "character combat ammo");
                _combatAmmo = ammo.ReadInt32();
                Require(!ammo.HasMore, "only combat ammo changed");
                break;
            case ComponentUpdateOp { ComponentId: ArrowComponent } shot when shot.EntityId == _arrow:
                var arrowUpdate = new SchemaReader(shot.Data);
                _arrowShot = arrowUpdate.ReadFieldId() == 2 && arrowUpdate.ReadBool() && !arrowUpdate.HasMore;
                break;
            case CommandResponseOp { ComponentId: BowComponent, CommandIndex: 1 } draw when draw.EntityId == _shoulderBow:
                Require(draw.Status == StatusCode.Success && _bowDrawUpdated, "starter bow draw replicated");
                return SpawnArrow(requestId);
            case CommandResponseOp { ComponentId: BowComponent, CommandIndex: 2 } nock when nock.EntityId == _shoulderBow:
                Require(nock.Status == StatusCode.Success && _arrow != 0 && _arrowAttached &&
                    _entities.GetValueOrDefault(_arrow) == "Projectile-BoStaffArrow-Reimagined-Base", "arrow attached and prewarmed");
                return WorldCommand(requestId, _arrow, ArrowComponent, 2, []);
            case CommandResponseOp { ComponentId: ArrowComponent, CommandIndex: 2 } released when released.EntityId == _arrow:
                Require(released.Status == StatusCode.Success && _arrowShot, "arrow released");
                return ShootArrow(requestId);
            case CommandResponseOp { ComponentId: BowComponent, CommandIndex: 4 } shoot when shoot.EntityId == _shoulderBow:
                Require(shoot.Status == StatusCode.Success && _bowAmmo == 11, "one arrow spent");
                return ++_shotResponses == 1 ? ShootArrow(requestId) : StartAirChopsCooldown(requestId);
            case CommandResponseOp { ComponentId: GodstoneComponent, CommandIndex: 9 } cooldown when cooldown.EntityId == _characterId:
                Require(cooldown.Status == StatusCode.Success && cooldown.Data.Length == 0, "cooldown start acknowledged");
                break;
            case ComponentUpdateOp { ComponentId: GodstoneComponent } cooldown when cooldown.EntityId == _characterId:
                var cooldownReader = new SchemaReader(cooldown.Data);
                Require(cooldownReader.ReadFieldId() == 3 && cooldownReader.ReadObjectList().Count == 2 &&
                    !cooldownReader.HasMore, "starter cooldown state replicated");
                _cooldownUpdates++;
                return CastAirChop(requestId);
            case CommandResponseOp { ComponentId: Ids.ActorStatsComponent, CommandIndex: 15 } cast when cast.EntityId == _characterId:
                Require(cast.Status == StatusCode.Success && cast.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 1 }) &&
                    _projectile != 0 && _projectileInitialized, "Air Chops projectile matched the cast ID");
                return WorldCommand(requestId, _projectile, ProjectileComponent, 4, []);
            case CommandResponseOp { ComponentId: ProjectileComponent, CommandIndex: 4 } ended when _challenges is not null:
                Require(ended.Status == StatusCode.Success && ended.EntityId == _projectile, "Air Chops ended");
                _airChopsEnded++;
                if (_airChopsEnded == 1) return _bowAmmo == 12 ? StartAirChopsCooldown(requestId) : null;
                Require(_cooldownUpdates == 2 && _bowAmmo == 12, "two Air Chops and one arrow recharge");
                Console.WriteLine("   [OK] Bow draw, nock, shot, ammo recharge and repeated Air Chops");
                return _challenges.RequestAreas(requestId);
            case ComponentUpdateOp { ComponentId: Ids.PlayerLifecycleComponent } life when life.EntityId == _characterId:
                return CheckLifeUpdate(life.Data, requestId);
            case ComponentUpdateOp { ComponentId: PublicActorStats } stats when stats.EntityId == _characterId:
                var health = new SchemaReader(stats.Data);
                Require(health.ReadFieldId() == 9, "current health update");
                _health = health.ReadInt32();
                if (_maxHealth == 0)
                {
                    Require(health.ReadFieldId() == 29, "max health published with current health");
                    _maxHealth = health.ReadInt32();
                    Require(_maxHealth == _startingHealth && _maxHealth == _totalHealth, "HUD and total health agree");
                }
                Require(!health.HasMore && _health == (_lifeState == 1 ? 0 : _startingHealth), "health follows lifecycle state");
                break;
            case ComponentUpdateOp { ComponentId: Ids.ActorStatsComponent } stats when stats.EntityId == _characterId:
                CheckActorStats(stats.Data, initialized: true);
                break;
            case CommandResponseOp { ComponentId: Ids.PlayerLifecycleComponent, CommandIndex: 1 } response:
                Require(response.Status == StatusCode.Success && response.Data.Length == 0 && _lifeState == 1,
                    "recovery acknowledged before lifecycle changes");
                _recoveryAcknowledged = true;
                break;
            case ComponentUpdateOp { ComponentId: Ids.TeleportComponent } teleport when teleport.EntityId == _avatarId:
                var reader = new SchemaReader(teleport.Data);
                if (reader.ReadFieldId() == 1001)
                {
                    Require(_worldLoaded && teleport.Data.Length == 48, "teleport event after world loaded");
                    if (_teleportsCompleted is 1 or 2)
                    {
                        Require(_lifeState == 2 && _health == 0, "teleport while recovering");
                        var payload = new SchemaReader(reader.ReadBytesSpan());
                        Require(payload.ReadFieldId() == 1, "recovery teleport destination");
                        var destination = new SchemaReader(payload.ReadBytesSpan());
                        Require(destination.ReadFieldId() == 1, "destination X");
                        destination.ReadDouble();
                        Require(destination.ReadFieldId() == 2 && destination.ReadDouble() > -150,
                            "server chose an in-bounds destination");
                    }
                    if (_teleportsCompleted >= 3) _challenges?.CheckTeleport(reader.ReadBytesSpan(), recovering: _lifeState == 2);
                    return WorldCommand(requestId, _avatarId, Ids.TeleportComponent, 1, [1, 0, 1]);
                }
                Require(_positionAccepted && teleport.Data.Length == 24, "confirmed teleport position update");
                return WorldCommand(requestId, _avatarId, Ids.TeleportComponent, 4, [1, 0, 1]);
            case CommandResponseOp { ComponentId: Ids.TeleportComponent, CommandIndex: 1 } position:
                Require(position.Status == StatusCode.Success && position.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 1 }),
                    "position confirmation response");
                _positionAccepted = true;
                break;
            case CommandResponseOp { ComponentId: Ids.TeleportComponent, CommandIndex: 4 } finished:
                Require(finished.Status == StatusCode.Success && finished.Data.Length == 0, "teleport finished response");
                _teleportsCompleted++;
                if (_teleportsCompleted == 1)
                    return _shop is not null ? _shop.Start(requestId, _characterId) : GlideEffect(requestId, 1);
                Require(_lifeState == 3 && _health == _maxHealth && _maxHealth == _startingHealth,
                    "alive and fully healed after recovery");
                if (_teleportsCompleted == 2) return FallBelowWorld(useZSync: false);
                if (_challenges is not null)
                {
                    _positionAccepted = false;
                    if (_teleportsCompleted == 3) return DrawBow(requestId);
                    var next = _challenges.FinishTeleport(_avatarId);
                    Completed = next is null;
                    return next;
                }
                Completed = true;
                break;
            case CommandResponseOp { ComponentId: VfxComponent, CommandIndex: 1 or 2 } effect:
                Require(effect.Status == StatusCode.Success && effect.Data.Length == 0, "glide effect acknowledgment");
                break;
            case ComponentUpdateOp { ComponentId: VfxComponent } effect when effect.EntityId == _characterId:
                var eventReader = new SchemaReader(effect.Data);
                var eventId = eventReader.ReadFieldId();
                Require(eventId is 1001 or 1002, "glide effect event ID");
                Require(eventReader.ReadBytesSpan().SequenceEqual(GlideEffect(requestId, eventId - 1000).Data) &&
                    !eventReader.HasMore, "glide effect event matches the command payload");
                return eventId == 1001 ? GlideEffect(requestId, 2) : FallBelowWorld(useZSync: true);
        }
        return null;
    }

    private void CheckStarterEquipment(byte[] data)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1, "equipment entries field");
        foreach (var raw in reader.ReadObjectList())
        {
            var row = new SchemaReader(raw);
            Require(row.ReadFieldId() == 1, "equipment slot field");
            if (row.ReadInt32() != 18) continue;
            Require(row.ReadFieldId() == 2, "shoulder item field");
            var item = new SchemaReader(row.ReadBytesSpan());
            Require(item.ReadFieldId() == 1 && item.ReadOptionalString() is not null, "bow instance ID");
            _starterBowEquipped = item.ReadFieldId() == 2 && item.ReadOptionalString() == "weapon_bow_reimagined_starter";
        }
    }

    private void CheckStarterAbilities(byte[] data)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1 && reader.ReadListCount() == 0, "starter etherdisks");
        Require(reader.ReadFieldId() == 2, "active etherdisk field");
        reader.ReadBytesSpan();
        Require(reader.ReadFieldId() == 3 && reader.ReadObjectList().Count == 2, "starter cooldowns");
        Require(reader.ReadFieldId() == 4 && reader.ReadListCount() == 2, "starter ability list");
        _starterAbilities = reader.ReadString() == "godstone_airChops" && reader.ReadString() == "godstone_arrow_reimagined_base";
    }

    private static long ReadShoulderParent(byte[] data)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1, "bow location field");
        reader.ReadBytesSpan();
        Require(reader.ReadFieldId() == 2, "bow rotation field");
        reader.ReadUInt32();
        Require(reader.ReadFieldId() == 3, "bow scale field");
        reader.ReadBytesSpan();
        Require(reader.ReadFieldId() == 4 && reader.ReadOptionHasValue(), "bow shoulder parent");
        var parent = new SchemaReader(reader.ReadBytesSpan());
        Require(parent.ReadFieldId() == 1, "bow avatar parent field");
        return parent.ReadInt64();
    }

    private CommandRequestOp DrawBow(long requestId)
    {
        Require(_shoulderBow != 0 && _starterBowEquipped && _starterAbilities && _starterStatusEffects && _maxHealth > 0,
            "starter bow, abilities and status effects initialized");
        var draw = new SchemaWriter();
        draw.AddFloat(1, 0.5f);
        draw.AddFloat(2, -0.25f);
        return WorldCommand(requestId, _shoulderBow, BowComponent, 1, draw.ToArray());
    }

    private CommandRequestOp CastAirChop(long requestId)
    {
        _castId = Guid.NewGuid();
        _projectile = 0;
        _projectileInitialized = false;
        var request = new SchemaWriter();
        request.AddString(1, "godstone_airChops");
        request.AddObject(2, info =>
        {
            info.AddEmptyList(1);
            info.AddFloat(2, 0.5f);
            info.AddBytes(3, []);
            info.AddString(4, null);
            info.AddString(5, null);
            info.AddBool(6, false);
            info.AddString(7, _castId.ToString());
            info.AddInt32(8, 0);
            info.AddString(0, null);
            info.AddBytes(9, []);
            info.AddObject(10, hand => hand.AddObject(1, position =>
            {
                position.AddInt32(1, 2_653_800);
                position.AddInt32(2, 799_700);
                position.AddInt32(3, 2_649_300);
            }));
            info.AddEmptyList(11);
            info.AddEmptyList(12);
        });
        return WorldCommand(requestId, _characterId, Ids.ActorStatsComponent, 15, request.ToArray());
    }

    private CommandRequestOp StartAirChopsCooldown(long requestId)
    {
        var cooldown = new SchemaWriter();
        cooldown.AddUInt32(1, 1);
        return WorldCommand(requestId, _characterId, GodstoneComponent, 9, cooldown.ToArray());
    }

    private CommandRequestOp SpawnArrow(long requestId)
    {
        _arrowCastId = Guid.NewGuid();
        var arrow = new SchemaWriter();
        arrow.AddString(3, _arrowCastId.ToString());
        arrow.AddBool(4, true);
        arrow.AddString(5, "Projectile-BoStaffArrow-Reimagined-Base");
        return WorldCommand(requestId, _shoulderBow, BowComponent, 2, arrow.ToArray());
    }

    private CommandRequestOp ShootArrow(long requestId)
    {
        var shot = new SchemaWriter();
        shot.AddInt32(1, 1);
        shot.AddBool(2, true);
        shot.AddBool(3, true);
        shot.AddFloat(4, 5);
        shot.AddInt32(5, 3);
        shot.AddInt32(6, 2);
        return WorldCommand(requestId, _shoulderBow, BowComponent, 4, shot.ToArray());
    }

    private CommandRequestOp? CheckLifeUpdate(byte[] data, long requestId)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1, "lifecycle state field");
        var state = reader.ReadUInt32();
        Require(state == (_lifeState % 3) + 1, "downed, recovering, then alive");
        _lifeState = state;
        if (state == 2)
        {
            Require(!reader.HasMore && (_teleportsCompleted != 1 || _recoveryAcknowledged), "recovering state update");
            return null;
        }

        Require(reader.ReadFieldId() == (state == 1 ? 1001 : 1002), "captured lifecycle event ID");
        var evt = new SchemaReader(reader.ReadBytesSpan());
        Require(evt.ReadFieldId() == (state == 1 ? 1 : 2) && evt.ReadBool() == (state == 1) &&
            !evt.HasMore && !reader.HasMore, "captured lifecycle event payload");
        if (state != 1 || _teleportsCompleted != 1) return null;

        var request = new SchemaWriter();
        request.AddObject(1, proposed => proposed.AddObject(1, destination =>
        {
            destination.AddDouble(1, 0);
            destination.AddDouble(2, -151);
            destination.AddDouble(3, 0);
        }), isOption: true);
        return WorldCommand(requestId, _characterId, Ids.PlayerLifecycleComponent, 1, request.ToArray());
    }

    private CommandRequestOp GlideEffect(long requestId, uint index)
    {
        const string leftGlide = "glide-left-hand";
        var writer = new SchemaWriter();
        if (index == 1)
        {
            writer.AddObject(1, location =>
            {
                location.AddObject(1, coordinates =>
                {
                    for (uint field = 1; field <= 3; field++) coordinates.AddInt32(field, 0);
                });
                location.AddEmptyOption(2);
                location.AddEmptyOption(3);
                location.AddEntityId(4, Find("LeftHand"), isOption: true);
                for (uint field = 5; field <= 9; field++) location.AddEmptyOption(field);
            });
            writer.AddInt32(2, 99);
            writer.AddString(3, "gliding");
            writer.AddBool(4, false);
            writer.AddString(5, leftGlide);
            writer.AddEntityId(6, _characterId);
            writer.AddFloat(7, 1);
            writer.AddInt64(8, -1);
        }
        else
        {
            writer.AddInt32(1, 99);
            writer.AddString(2, leftGlide);
            writer.AddEntityId(3, _characterId);
        }
        return WorldCommand(requestId, _characterId, VfxComponent, index, writer.ToArray());
    }

    private ComponentUpdateOp FallBelowWorld(bool useZSync)
    {
        _positionAccepted = false;
        const float height = -151;
        var zsync = new SchemaWriter();
        zsync.AddObject(1, location =>
        {
            location.AddFloat(1, 0);
            location.AddFloat(2, height);
            location.AddFloat(3, 0);
        });
        return new ComponentUpdateOp
        {
            EntityId = _avatarId,
            ComponentId = useZSync ? Ids.ZSyncComponent : Ids.Position,
            Data = useZSync ? zsync.ToArray() : StandardComponents.Position(0, height, 0),
        };
    }

    private static int ReadInitialMaxHealth(ref SchemaReader reader)
    {
        foreach (var field in new[] { 18, 19, 21, 22 })
        {
            Require(reader.ReadFieldId() == field, "initial public combat stats");
            reader.ReadInt32(); // Integers and floats both occupy four bytes.
        }
        Require(reader.ReadFieldId() == 25 && reader.ReadListCount() == 0, "initial status effects");
        Require(reader.ReadFieldId() == 26, "new-spawn flag");
        reader.ReadBool();
        Require(reader.ReadFieldId() == 28, "max stagger duration");
        reader.ReadFloat();
        Require(reader.ReadFieldId() == 29, "initial max health field");
        return reader.ReadInt32();
    }

    private void CheckActorStats(byte[] data, bool initialized)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 10, "current rage");
        var rage = reader.ReadInt32();
        Require(reader.ReadFieldId() == 12, "current essence");
        reader.ReadInt32();
        if (!initialized)
        {
            Require(reader.ReadFieldId() == 13, "base stats");
            var baseCount = reader.ReadListCount();
            for (var i = 0; i < baseCount; i++) reader.ReadBytesSpan();
        }

        Require(reader.ReadFieldId() == 15, "total stats");
        var totalCount = reader.ReadListCount();
        if (!initialized)
        {
            Require(rage == 0 && totalCount == 0, "effective resources and stats start unset");
            return;
        }

        float? moveSpeed = null, stamina = null, maxRage = null;
        for (var i = 0; i < totalCount; i++)
        {
            var stat = new SchemaReader(reader.ReadBytesSpan());
            Require(stat.ReadFieldId() == 1, "stat key");
            var key = stat.ReadInt32();
            Require(stat.ReadFieldId() == 2, "stat value");
            var value = stat.ReadFloat();
            if (key == 4) _totalHealth = value;
            if (key == 5) maxRage = value;
            if (key == 9) moveSpeed = value;
            if (key == 11) stamina = value;
        }
        Require(moveSpeed == 10, "neutral movement multiplier");
        Require(stamina > 0, "positive maximum stamina");
        Require(_totalHealth == _startingHealth && !reader.HasMore, "effective stats published at world entry");
        Require(challenges ? rage > 0 && rage == maxRage : rage == 0, "starting rage matches the class");
    }

    private void CheckGraph(byte[] data)
    {
        Require(_entities.Count == 5 + (challenges ? ChallengeScenario.MapCount : 0), "spawned character rig and its world");
        _challenges?.CheckPreloadedMaps();
        var reader = new SchemaReader(data);
        var character = ReadEntityId(ref reader, 1);
        var avatar = ReadEntityId(ref reader, 2);
        _characterId = character;
        _avatarId = avatar;
        Require(ReadEntityId(ref reader, 3) == playerId && !reader.HasMore, "player link");
        Require(_entities.GetValueOrDefault(character) == "PlayerCharacter", "character link");
        Require(_characterWithInterest == character, "character has the Interest component before avatar setup");
        Require(_entities.GetValueOrDefault(avatar) == "PlayerAvatar", "avatar link");
        Require(_authority[character].SetEquals([Ids.PlayerCharacterClientSet]), "character authority");
        Require(_authority[avatar].SetEquals([Ids.PlayerAvatarClientSet, Ids.PositionSet, Ids.ZSyncSet]), "avatar authority");
        Require(_authority[Find("Head")].SetEquals([Ids.PositionSet, Ids.ZSyncSet]), "head authority");
        Require(_authority[Find("LeftHand")].SetEquals([Ids.PlayerHandClientSet, Ids.PositionSet, Ids.ZSyncSet]), "left hand authority");
        Require(_authority[Find("RightHand")].SetEquals([Ids.PlayerHandClientSet, Ids.PositionSet, Ids.ZSyncSet]), "right hand authority");
    }

    private static void CheckBodyParts(ref SchemaReader data, long left, long right, long head)
    {
        Require(data.ReadFieldId() == 2, "avatar body parts field");
        var parts = new SchemaReader(data.ReadBytesSpan());
        Require(parts.ReadFieldId() == 1 && parts.ReadInt64() == left, "avatar left hand");
        Require(parts.ReadFieldId() == 2 && parts.ReadInt64() == right, "avatar right hand");
        Require(parts.ReadFieldId() == 3 && parts.ReadInt64() == head && !parts.HasMore && !data.HasMore, "avatar head");
    }

    private CommandRequestOp RegisterHands(long requestId, long left, long right)
    {
        var writer = new SchemaWriter();
        writer.AddEntityId(1, left, isOption: true);
        writer.AddEntityId(2, right, isOption: true);
        return WorldCommand(requestId, _characterId, Ids.NetworkedObjectInteractionComponent, 2, writer.ToArray());
    }

    private long Find(string type) => _entities.Single(entry => entry.Value == type &&
        entry.Key >= _characterId && entry.Key < _characterId + 5).Key;

    private static CommandRequestOp WorldCommand(long requestId, long entity, uint component, uint command, byte[] data) =>
        new() { RequestId = requestId, EntityId = entity, ComponentId = component, CommandIndex = command, Data = data };

    private CommandRequestOp Request(long requestId, string selectedId, string sessionTicket)
    {
        var writer = new SchemaWriter();
        writer.AddString(1, accountId);
        writer.AddString(2, selectedId);
        writer.AddUInt32(6, challenges ? 4u : 3u);
        writer.AddString(9, "DPS");
        writer.AddString(3, name);
        writer.AddUInt32(4, 1); // Windows Steam
        writer.AddString(5, null);
        writer.AddUInt64(7, 0);
        writer.AddString(8, null);
        writer.AddString(10, null);
        writer.AddString(11, null);
        writer.AddEmptyList(12);
        writer.AddEmptyList(13);
        writer.AddString(14, sessionTicket);
        writer.AddEmptyOption(15);
        return new()
        {
            RequestId = requestId, EntityId = 4, ComponentId = Ids.CharacterSpawnerComponent,
            CommandIndex = 3, Data = writer.ToArray(),
        };
    }

    private static long ReadEntityId(ref SchemaReader reader, uint field)
    {
        Require(reader.ReadFieldId() == field, $"entity link field {field}");
        return long.Parse(reader.ReadOptionalString()!);
    }

    private static void Require(bool valid, string detail)
    {
        if (!valid) throw new InvalidDataException($"Unexpected spawn data: {detail}");
    }
}
