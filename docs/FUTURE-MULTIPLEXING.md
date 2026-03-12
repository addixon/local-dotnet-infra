# Future: Full AMQP Connection Multiplexing

**Status**: Not implemented  
**Priority**: Medium  
**Prerequisite**: Current proxy working with one-connection-per-entity model

---

## Background

The current ServiceBus Emulator Proxy assumes **one AMQP connection per entity**. This means each `ServiceBusClient` connection targets a single topic or queue. The Azure ServiceBus SDK can multiplex multiple links (sender/receiver) over a single AMQP connection, where each link targets a different entity.

In the current implementation, if a client opens a connection and creates links to two different topics that live on different backend emulators, the second link will fail because the proxy routes the entire connection to the backend determined by the first link's entity.

## Current Model

```
Client ──conn──▶ Proxy ──conn──▶ Backend Emulator #0
                    │                (topic.1, topic.2, ..., topic.50)
                    │
                    └── All links on this connection route to emulator #0
```

## Target Model

```
Client ──conn──▶ Proxy ──conn──▶ Backend Emulator #0 (for topic.1)
                    │
                    ├────conn──▶ Backend Emulator #1 (for topic.51)
                    │
                    └────conn──▶ Backend Emulator #2 (for topic.101)
```

In the target model, a single client AMQP connection can have links to entities on different backend emulators. The proxy maintains multiple backend connections and routes each link independently.

## Implementation Approach

### 1. Link-Level Routing (Recommended)

Instead of routing at the connection level, route at the **link level**:

**ProxyLinkProcessor changes:**
- When processing an `Attach` frame, determine the backend emulator from the entity address
- Create a backend session+link on the correct emulator's connection
- Maintain a `Dictionary<uint, (EmulatorInstance, Session, Link)>` mapping client link handles to backend links
- Each backend emulator connection can host multiple links from different client connections

**Key data structures:**
```csharp
// Per client connection
class ClientConnectionState
{
    // Client link handle → backend routing info
    Dictionary<uint, BackendLinkState> Links { get; }
}

class BackendLinkState
{
    EmulatorInstance Instance { get; }
    Connection BackendConnection { get; }
    Session BackendSession { get; }
    // For sender: SenderLink, for receiver: ReceiverLink
    Link BackendLink { get; }
}
```

### 2. Session Multiplexing

AMQP sessions can carry multiple links. The proxy needs to:

1. Accept client sessions with the `ContainerHost`
2. For each link Attach on a session, independently determine the backend
3. Create backend sessions per-emulator (one backend session can carry multiple links to that emulator)
4. Forward Transfer/Disposition/Flow frames to the correct backend session based on link handle

### 3. Credit Management

With multiplexing, credit management becomes more complex:

- Client grants credit on a per-link basis (Flow frames with link handle)
- Proxy must forward credit to the correct backend link
- Backend sends transfers on its links; proxy must forward to the correct client link

### 4. Disposition Routing

Dispositions (Accept/Reject/Release) must be routed correctly:

- Client sends Disposition with delivery IDs → proxy maps to backend delivery IDs
- Backend sends Disposition → proxy maps to client delivery IDs
- Delivery ID spaces are independent per connection, so proxy must maintain delivery ID translation tables

## Testing Strategy

1. Create a test that opens a single `ServiceBusClient` connection
2. Create senders to entities on different shards (e.g., `topic.1` and `topic.51`)
3. Send messages through both senders
4. Verify messages arrive at the correct backend emulators
5. Create receivers and verify message delivery
6. Test concurrent send/receive across shards on a single connection

## Risks and Considerations

- **AMQPNetLite ContainerHost limitations**: The `ContainerHost` may not expose enough low-level control for link-level routing. May need to use `ConnectionListener` directly.
- **Performance overhead**: Per-frame routing adds latency. For most dev/test scenarios this is acceptable.
- **Session window management**: AMQP sessions have incoming/outgoing window sizes that control transfer flow. The proxy needs to manage these independently per backend.
- **Error propagation**: If one backend link fails, it should not affect other links on the same client connection.

## Files to Modify

- `servicebus/proxy/Services/AmqpProxy.cs` — Major refactor of link routing
- `servicebus/proxy/Services/AmqpProxy.cs` — New `ClientConnectionState` and `BackendLinkState` classes
- `servicebus/proxy/Services/EntityRouter.cs` — No changes expected (already routes by entity name)

## Estimated Effort

Medium-high. The core routing logic change is conceptually simple but requires careful handling of AMQP session and link lifecycle, credit management, and delivery ID translation. Estimate 2-3 days of implementation + testing.
