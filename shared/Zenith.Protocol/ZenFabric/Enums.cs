namespace Zenith.Protocol.ZenFabric;

/// <summary>Improbable.Worker.CInterop.OperationCode (values from global-metadata.dat).</summary>
public enum OpCode
{
    None = 0,
    ProtocolSettings = 1,           // S->C  RSA Public Key
    AuthWorker = 2,                 // C->S  encrypted symmetric key + identity token
    InitWorker = 3,                 // S->C  WorkerEntityId
    FlagUpdate = 4,                 // S->C
    AddEntity = 5,                  // S->C
    RemoveEntity = 6,               // S->C
    AddComponent = 7,               // both
    ComponentUpdate = 8,            // both
    ComponentSetAuthorityChange = 9,// S->C
    CreateEntityRequest = 10,       // C->S
    CreateEntityResponse = 11,      // S->C
    DeleteEntityRequest = 12,       // C->S
    DeleteEntityResponse = 13,      // S->C
    CommandRequest = 14,            // both
    CommandResponse = 15,           // both
    EntityQueryRequest = 16,        // C->S
    EntityQueryResponse = 17,       // S->C
    SystemRequest = 18,             // S->C
    SystemResponse = 19,            // C->S
    Ping = 20,                      // C->S
    Pong = 21,                      // S->C
    ReservedBegin = 22,
    ReservedEnd = 89,
    ReserveEntityIdsRequest = 90,   // C->S
    ReserveEntityIdsResponse = 91,  // S->C
    Log = 100,                      // C->S
    Disconnect = 200,               // both
    Max = 201,
}

/// <summary>Improbable.Worker.CInterop.StatusCode (1 byte on the wire).</summary>
public enum StatusCode : byte
{
    Success = 1,
    Timeout = 2,
    NotFound = 3,
    AuthorityLost = 4,
    PermissionDenied = 5,
    ApplicationError = 6,
    InternalError = 7,
}

/// <summary>Improbable.Worker.CInterop.Authority (1 byte on the wire).</summary>
public enum Authority : byte
{
    NotAuthoritative = 0,
    Authoritative = 1,
}

/// <summary>Improbable.Worker.CInterop.LogLevel (1 byte on the wire).</summary>
public enum WorkerLogLevel : byte
{
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}
