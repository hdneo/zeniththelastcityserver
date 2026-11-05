using Zenith.Protocol.Schema;

namespace Zenith.Protocol.ZenFabric;

// =====================================================================================
//  ZenFabric operations. Every message (after the LiteNetLib frame [int32 len]) is:
//      [int32 OpCode] [Body]
//  The body layouts below are reconstructed 1:1 from Serialize()/Deserialize() of the classes in
//  Improbable.Worker.CInterop (GameAssembly.dll).
//  "bytes" = [int32 len][data], "string" = [int32 len][utf8], "u8" = 1 byte.
// =====================================================================================

public abstract class ZfOp
{
    public abstract OpCode Code { get; }
    public abstract void WriteBody(SchemaWriter w);

    public byte[] Serialize()
    {
        var w = new SchemaWriter();
        w.WriteInt32((int)Code);
        WriteBody(w);
        return w.ToArray();
    }
}

/// <summary>Component data: [uint32 ComponentId][bytes SchemaData]</summary>
public sealed class ComponentData
{
    public uint ComponentId { get; set; }
    public byte[] Data { get; set; } = [];

    public ComponentData() { }
    public ComponentData(uint id, byte[] data) { ComponentId = id; Data = data; }

    public void Write(SchemaWriter w) { w.WriteUInt32(ComponentId); w.WriteBytes(Data); }
    public static ComponentData Read(ref SchemaReader r) => new(r.ReadUInt32(), r.ReadBytes());
}

/// <summary>Entity: [int64 EntityId][int32 n][n x ComponentData]</summary>
public sealed class EntityData
{
    public long EntityId { get; set; }
    public List<ComponentData> Components { get; set; } = [];

    public void Write(SchemaWriter w)
    {
        w.WriteInt64(EntityId);
        w.WriteInt32(Components.Count);
        foreach (var c in Components) c.Write(w);
    }

    public static EntityData Read(ref SchemaReader r)
    {
        var e = new EntityData { EntityId = r.ReadInt64() };
        var n = r.ReadInt32();
        for (var i = 0; i < n; i++) e.Components.Add(ComponentData.Read(ref r));
        return e;
    }
}

/// <summary>1 | S->C | [bytes RSA-Modulus (big endian)][bytes RSA-Exponent]</summary>
public sealed class ProtocolSettingsOp : ZfOp
{
    public override OpCode Code => OpCode.ProtocolSettings;
    public byte[] Modulus { get; set; } = [];
    public byte[] Exponent { get; set; } = [];
    public override void WriteBody(SchemaWriter w) { w.WriteBytes(Modulus); w.WriteBytes(Exponent); }
    public static ProtocolSettingsOp Read(ref SchemaReader r) => new() { Modulus = r.ReadBytes(), Exponent = r.ReadBytes() };
}

/// <summary>2 | C->S | [string IdentityToken][int32 n][n x bytes RSA-PKCS1-encrypted key parts]</summary>
public sealed class AuthWorkerOp : ZfOp
{
    public override OpCode Code => OpCode.AuthWorker;
    public string IdentityToken { get; set; } = "";
    public List<byte[]> SymmetricKeyParts { get; set; } = [];

    public override void WriteBody(SchemaWriter w)
    {
        w.WriteString(IdentityToken);
        w.WriteInt32(SymmetricKeyParts.Count);
        foreach (var p in SymmetricKeyParts) w.WriteBytes(p);
    }

    public static AuthWorkerOp Read(ref SchemaReader r)
    {
        var op = new AuthWorkerOp { IdentityToken = r.ReadString() };
        var n = r.ReadInt32();
        for (var i = 0; i < n; i++) op.SymmetricKeyParts.Add(r.ReadBytes());
        return op;
    }
}

/// <summary>3 | S->C | [int64 WorkerEntityId]</summary>
public sealed class InitWorkerOp : ZfOp
{
    public override OpCode Code => OpCode.InitWorker;
    public long WorkerEntityId { get; set; }
    public override void WriteBody(SchemaWriter w) => w.WriteInt64(WorkerEntityId);
    public static InitWorkerOp Read(ref SchemaReader r) => new() { WorkerEntityId = r.ReadInt64() };
}

/// <summary>4 | S->C | [string Name][string Value]  (worker flags)</summary>
public sealed class FlagUpdateOp : ZfOp
{
    public override OpCode Code => OpCode.FlagUpdate;
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public override void WriteBody(SchemaWriter w) { w.WriteString(Name); w.WriteString(Value); }
    public static FlagUpdateOp Read(ref SchemaReader r) => new() { Name = r.ReadString(), Value = r.ReadString() };
}

/// <summary>5 | S->C | [int64 EntityId]</summary>
public sealed class AddEntityOp : ZfOp
{
    public override OpCode Code => OpCode.AddEntity;
    public long EntityId { get; set; }
    public override void WriteBody(SchemaWriter w) => w.WriteInt64(EntityId);
    public static AddEntityOp Read(ref SchemaReader r) => new() { EntityId = r.ReadInt64() };
}

/// <summary>6 | S->C | [int64 EntityId]</summary>
public sealed class RemoveEntityOp : ZfOp
{
    public override OpCode Code => OpCode.RemoveEntity;
    public long EntityId { get; set; }
    public override void WriteBody(SchemaWriter w) => w.WriteInt64(EntityId);
    public static RemoveEntityOp Read(ref SchemaReader r) => new() { EntityId = r.ReadInt64() };
}

/// <summary>7 | both | [int64 EntityId][uint32 ComponentId][bytes Data]</summary>
public sealed class AddComponentOp : ZfOp
{
    public override OpCode Code => OpCode.AddComponent;
    public long EntityId { get; set; }
    public ComponentData Data { get; set; } = new();
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(EntityId); Data.Write(w); }
    public static AddComponentOp Read(ref SchemaReader r) => new() { EntityId = r.ReadInt64(), Data = ComponentData.Read(ref r) };
}

/// <summary>8 | both | [int64 EntityId][uint32 ComponentId][bytes UpdateData]</summary>
public sealed class ComponentUpdateOp : ZfOp
{
    public override OpCode Code => OpCode.ComponentUpdate;
    public long EntityId { get; set; }
    public uint ComponentId { get; set; }
    public byte[] Data { get; set; } = [];
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(EntityId); w.WriteUInt32(ComponentId); w.WriteBytes(Data); }
    public static ComponentUpdateOp Read(ref SchemaReader r) => new() { EntityId = r.ReadInt64(), ComponentId = r.ReadUInt32(), Data = r.ReadBytes() };
}

/// <summary>9 | S->C | [int64 EntityId][uint32 ComponentSetId][u8 Authority][int32 n][n x ComponentData]</summary>
public sealed class ComponentSetAuthorityChangeOp : ZfOp
{
    public override OpCode Code => OpCode.ComponentSetAuthorityChange;
    public long EntityId { get; set; }
    public uint ComponentSetId { get; set; }
    public Authority Authority { get; set; }
    public List<ComponentData> CanonicalData { get; set; } = [];

    public override void WriteBody(SchemaWriter w)
    {
        w.WriteInt64(EntityId);
        w.WriteUInt32(ComponentSetId);
        w.WriteByte((byte)Authority);
        w.WriteInt32(CanonicalData.Count);
        foreach (var c in CanonicalData) c.Write(w);
    }

    public static ComponentSetAuthorityChangeOp Read(ref SchemaReader r)
    {
        var op = new ComponentSetAuthorityChangeOp { EntityId = r.ReadInt64(), ComponentSetId = r.ReadUInt32(), Authority = (Authority)r.ReadByte() };
        var n = r.ReadInt32();
        for (var i = 0; i < n; i++) op.CanonicalData.Add(ComponentData.Read(ref r));
        return op;
    }
}

/// <summary>10 | C->S | [int64 RequestId][int64 EntityId (0 = server chooses)][int32 n][n x ComponentData]</summary>
public sealed class CreateEntityRequestOp : ZfOp
{
    public override OpCode Code => OpCode.CreateEntityRequest;
    public long RequestId { get; set; }
    public long EntityId { get; set; }
    public List<ComponentData> Components { get; set; } = [];

    public override void WriteBody(SchemaWriter w)
    {
        w.WriteInt64(RequestId);
        w.WriteInt64(EntityId);
        w.WriteInt32(Components.Count);
        foreach (var c in Components) c.Write(w);
    }

    public static CreateEntityRequestOp Read(ref SchemaReader r)
    {
        var op = new CreateEntityRequestOp { RequestId = r.ReadInt64(), EntityId = r.ReadInt64() };
        var n = r.ReadInt32();
        for (var i = 0; i < n; i++) op.Components.Add(ComponentData.Read(ref r));
        return op;
    }
}

/// <summary>11 | S->C | [int64 RequestId][u8 Status][string Message][int64 EntityId]</summary>
public sealed class CreateEntityResponseOp : ZfOp
{
    public override OpCode Code => OpCode.CreateEntityResponse;
    public long RequestId { get; set; }
    public StatusCode Status { get; set; } = StatusCode.Success;
    public string Message { get; set; } = "";
    public long EntityId { get; set; }
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteByte((byte)Status); w.WriteString(Message); w.WriteInt64(EntityId); }
    public static CreateEntityResponseOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), Status = (StatusCode)r.ReadByte(), Message = r.ReadString(), EntityId = r.ReadInt64() };
}

/// <summary>12 | C->S | [int64 RequestId][int64 EntityId]</summary>
public sealed class DeleteEntityRequestOp : ZfOp
{
    public override OpCode Code => OpCode.DeleteEntityRequest;
    public long RequestId { get; set; }
    public long EntityId { get; set; }
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteInt64(EntityId); }
    public static DeleteEntityRequestOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), EntityId = r.ReadInt64() };
}

/// <summary>13 | S->C | [int64 RequestId][int64 EntityId][u8 Status][string Message]</summary>
public sealed class DeleteEntityResponseOp : ZfOp
{
    public override OpCode Code => OpCode.DeleteEntityResponse;
    public long RequestId { get; set; }
    public long EntityId { get; set; }
    public StatusCode Status { get; set; } = StatusCode.Success;
    public string Message { get; set; } = "";
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteInt64(EntityId); w.WriteByte((byte)Status); w.WriteString(Message); }
    public static DeleteEntityResponseOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), EntityId = r.ReadInt64(), Status = (StatusCode)r.ReadByte(), Message = r.ReadString() };
}

/// <summary>14 | both | [int64 RequestId][int64 EntityId][int64 CallerWorkerEntityId][uint32 ComponentId][uint32 CommandIndex][bytes Data]</summary>
public sealed class CommandRequestOp : ZfOp
{
    public override OpCode Code => OpCode.CommandRequest;
    public long RequestId { get; set; }
    public long EntityId { get; set; }
    public long CallerWorkerEntityId { get; set; }
    public uint ComponentId { get; set; }
    public uint CommandIndex { get; set; }
    public byte[] Data { get; set; } = [];

    public override void WriteBody(SchemaWriter w)
    {
        w.WriteInt64(RequestId); w.WriteInt64(EntityId); w.WriteInt64(CallerWorkerEntityId);
        w.WriteUInt32(ComponentId); w.WriteUInt32(CommandIndex); w.WriteBytes(Data);
    }

    public static CommandRequestOp Read(ref SchemaReader r) => new()
    {
        RequestId = r.ReadInt64(), EntityId = r.ReadInt64(), CallerWorkerEntityId = r.ReadInt64(),
        ComponentId = r.ReadUInt32(), CommandIndex = r.ReadUInt32(), Data = r.ReadBytes(),
    };
}

/// <summary>15 | both | [int64 RequestId][int64 EntityId][u8 Status][string Message][uint32 ComponentId][uint32 CommandIndex][bytes Data]</summary>
public sealed class CommandResponseOp : ZfOp
{
    public override OpCode Code => OpCode.CommandResponse;
    public long RequestId { get; set; }
    public long EntityId { get; set; }
    public StatusCode Status { get; set; } = StatusCode.Success;
    public string Message { get; set; } = "";
    public uint ComponentId { get; set; }
    public uint CommandIndex { get; set; }
    public byte[] Data { get; set; } = [];

    public override void WriteBody(SchemaWriter w)
    {
        w.WriteInt64(RequestId); w.WriteInt64(EntityId); w.WriteByte((byte)Status); w.WriteString(Message);
        w.WriteUInt32(ComponentId); w.WriteUInt32(CommandIndex); w.WriteBytes(Data);
    }

    public static CommandResponseOp Read(ref SchemaReader r) => new()
    {
        RequestId = r.ReadInt64(), EntityId = r.ReadInt64(), Status = (StatusCode)r.ReadByte(), Message = r.ReadString(),
        ComponentId = r.ReadUInt32(), CommandIndex = r.ReadUInt32(), Data = r.ReadBytes(),
    };
}

/// <summary>16 | C->S | [int64 RequestId][bytes SerializedConstraint (= Improbable.QueryConstraint SchemaObject)]</summary>
public sealed class EntityQueryRequestOp : ZfOp
{
    public override OpCode Code => OpCode.EntityQueryRequest;
    public long RequestId { get; set; }
    public byte[] SerializedConstraint { get; set; } = [];
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteBytes(SerializedConstraint); }
    public static EntityQueryRequestOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), SerializedConstraint = r.ReadBytes() };
}

/// <summary>17 | S->C | [int64 RequestId][u8 Status][string Message][int32 n][n x Entity]</summary>
public sealed class EntityQueryResponseOp : ZfOp
{
    public override OpCode Code => OpCode.EntityQueryResponse;
    public long RequestId { get; set; }
    public StatusCode Status { get; set; } = StatusCode.Success;
    public string Message { get; set; } = "";
    public List<EntityData> Results { get; set; } = [];

    public override void WriteBody(SchemaWriter w)
    {
        w.WriteInt64(RequestId); w.WriteByte((byte)Status); w.WriteString(Message);
        w.WriteInt32(Results.Count);
        foreach (var e in Results) e.Write(w);
    }

    public static EntityQueryResponseOp Read(ref SchemaReader r)
    {
        var op = new EntityQueryResponseOp { RequestId = r.ReadInt64(), Status = (StatusCode)r.ReadByte(), Message = r.ReadString() };
        var n = r.ReadInt32();
        for (var i = 0; i < n; i++) op.Results.Add(EntityData.Read(ref r));
        return op;
    }
}

/// <summary>18 | S->C | [int64 RequestId][string Command][string Args]</summary>
public sealed class SystemRequestOp : ZfOp
{
    public override OpCode Code => OpCode.SystemRequest;
    public long RequestId { get; set; }
    public string Command { get; set; } = "";
    public string Args { get; set; } = "";
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteString(Command); w.WriteString(Args); }
    public static SystemRequestOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), Command = r.ReadString(), Args = r.ReadString() };
}

/// <summary>19 | C->S | [int64 RequestId][string Data]</summary>
public sealed class SystemResponseOp : ZfOp
{
    public override OpCode Code => OpCode.SystemResponse;
    public long RequestId { get; set; }
    public string Data { get; set; } = "";
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteString(Data); }
    public static SystemResponseOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), Data = r.ReadString() };
}

/// <summary>20 | C->S | [int64 RequestId]</summary>
public sealed class PingOp : ZfOp
{
    public override OpCode Code => OpCode.Ping;
    public long RequestId { get; set; }
    public override void WriteBody(SchemaWriter w) => w.WriteInt64(RequestId);
    public static PingOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64() };
}

/// <summary>21 | S->C | [int64 RequestId]</summary>
public sealed class PongOp : ZfOp
{
    public override OpCode Code => OpCode.Pong;
    public long RequestId { get; set; }
    public override void WriteBody(SchemaWriter w) => w.WriteInt64(RequestId);
    public static PongOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64() };
}

/// <summary>90 | C->S | [int64 RequestId][uint32 NumberOfEntityIds]</summary>
public sealed class ReserveEntityIdsRequestOp : ZfOp
{
    public override OpCode Code => OpCode.ReserveEntityIdsRequest;
    public long RequestId { get; set; }
    public uint NumberOfEntityIds { get; set; }
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteUInt32(NumberOfEntityIds); }
    public static ReserveEntityIdsRequestOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), NumberOfEntityIds = r.ReadUInt32() };
}

/// <summary>91 | S->C | [int64 RequestId][u8 Status][string Message][int64 FirstEntityId][int32 NumberOfEntityIds]</summary>
public sealed class ReserveEntityIdsResponseOp : ZfOp
{
    public override OpCode Code => OpCode.ReserveEntityIdsResponse;
    public long RequestId { get; set; }
    public StatusCode Status { get; set; } = StatusCode.Success;
    public string Message { get; set; } = "";
    public long FirstEntityId { get; set; }
    public int NumberOfEntityIds { get; set; }
    public override void WriteBody(SchemaWriter w) { w.WriteInt64(RequestId); w.WriteByte((byte)Status); w.WriteString(Message); w.WriteInt64(FirstEntityId); w.WriteInt32(NumberOfEntityIds); }
    public static ReserveEntityIdsResponseOp Read(ref SchemaReader r) => new() { RequestId = r.ReadInt64(), Status = (StatusCode)r.ReadByte(), Message = r.ReadString(), FirstEntityId = r.ReadInt64(), NumberOfEntityIds = r.ReadInt32() };
}

/// <summary>100 | C->S | [u8 Level][string LoggerName][string Message][int64 EntityId]</summary>
public sealed class LogOp : ZfOp
{
    public override OpCode Code => OpCode.Log;
    public WorkerLogLevel Level { get; set; } = WorkerLogLevel.Info;
    public string LoggerName { get; set; } = "";
    public string Message { get; set; } = "";
    public long EntityId { get; set; }
    public override void WriteBody(SchemaWriter w) { w.WriteByte((byte)Level); w.WriteString(LoggerName); w.WriteString(Message); w.WriteInt64(EntityId); }
    public static LogOp Read(ref SchemaReader r) => new() { Level = (WorkerLogLevel)r.ReadByte(), LoggerName = r.ReadString(), Message = r.ReadString(), EntityId = r.ReadInt64() };
}

/// <summary>200 | both | [u8 ConnectionStatusCode][string Reason]</summary>
public sealed class DisconnectOp : ZfOp
{
    public override OpCode Code => OpCode.Disconnect;
    public byte ConnectionStatusCode { get; set; }
    public string Reason { get; set; } = "";
    public override void WriteBody(SchemaWriter w) { w.WriteByte(ConnectionStatusCode); w.WriteString(Reason); }
    public static DisconnectOp Read(ref SchemaReader r) => new() { ConnectionStatusCode = r.ReadByte(), Reason = r.ReadString() };
}

/// <summary>Fallback for unknown opcodes.</summary>
public sealed class UnknownOp(int code, byte[] body) : ZfOp
{
    public int RawCode { get; } = code;
    public byte[] Body { get; } = body;
    public override OpCode Code => (OpCode)RawCode;
    public override void WriteBody(SchemaWriter w) => w.WriteRaw(Body);
}

public static class ZfOps
{
    /// <summary>Parses an (already decrypted) payload [int32 OpCode][Body].</summary>
    public static ZfOp Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SchemaReader(payload);
        var code = r.ReadInt32();
        ZfOp op = (OpCode)code switch
        {
            OpCode.ProtocolSettings => ProtocolSettingsOp.Read(ref r),
            OpCode.AuthWorker => AuthWorkerOp.Read(ref r),
            OpCode.InitWorker => InitWorkerOp.Read(ref r),
            OpCode.FlagUpdate => FlagUpdateOp.Read(ref r),
            OpCode.AddEntity => AddEntityOp.Read(ref r),
            OpCode.RemoveEntity => RemoveEntityOp.Read(ref r),
            OpCode.AddComponent => AddComponentOp.Read(ref r),
            OpCode.ComponentUpdate => ComponentUpdateOp.Read(ref r),
            OpCode.ComponentSetAuthorityChange => ComponentSetAuthorityChangeOp.Read(ref r),
            OpCode.CreateEntityRequest => CreateEntityRequestOp.Read(ref r),
            OpCode.CreateEntityResponse => CreateEntityResponseOp.Read(ref r),
            OpCode.DeleteEntityRequest => DeleteEntityRequestOp.Read(ref r),
            OpCode.DeleteEntityResponse => DeleteEntityResponseOp.Read(ref r),
            OpCode.CommandRequest => CommandRequestOp.Read(ref r),
            OpCode.CommandResponse => CommandResponseOp.Read(ref r),
            OpCode.EntityQueryRequest => EntityQueryRequestOp.Read(ref r),
            OpCode.EntityQueryResponse => EntityQueryResponseOp.Read(ref r),
            OpCode.SystemRequest => SystemRequestOp.Read(ref r),
            OpCode.SystemResponse => SystemResponseOp.Read(ref r),
            OpCode.Ping => PingOp.Read(ref r),
            OpCode.Pong => PongOp.Read(ref r),
            OpCode.ReserveEntityIdsRequest => ReserveEntityIdsRequestOp.Read(ref r),
            OpCode.ReserveEntityIdsResponse => ReserveEntityIdsResponseOp.Read(ref r),
            OpCode.Log => LogOp.Read(ref r),
            OpCode.Disconnect => DisconnectOp.Read(ref r),
            _ => new UnknownOp(code, r.RemainingSpan.ToArray()),
        };
        return op;
    }

    /// <summary>Like Parse, but without exceptions (for the sniffer).</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out ZfOp? op, out string? error)
    {
        try
        {
            op = Parse(payload);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            op = null;
            error = ex.Message;
            return false;
        }
    }
}
