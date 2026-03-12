# Future: Asynchronous Emulator Spin-Up

**Status**: Not implemented  
**Priority**: Low  
**Prerequisite**: Current proxy working with synchronous (blocking) spin-up

---

## Background

The current ServiceBus Emulator Proxy **blocks** management API calls (e.g., `PUT /new-topic`) when a new emulator needs to be spun up. This means the caller waits 30-60 seconds while the new SQL Server + emulator containers start and become healthy.

This document describes the design for an **asynchronous spin-up** model where the management API returns immediately and the entity becomes available once the emulator is ready.

## Current Model (Synchronous)

```
Client ──PUT /topic.new──▶ Proxy ──creates SQL container──▶ waits for healthy (30s)
                                  ──creates emulator──▶ waits for healthy (30s)
                                  ──returns 201──▶ Client
                                  Total: ~60s blocking
```

## Target Model (Asynchronous)

```
Client ──PUT /topic.new──▶ Proxy ──queues spin-up──▶ returns 202 Accepted
                                  Location: /_proxy/entities/topic.new/status

Background:
  Proxy ──creates SQL container──▶ healthy
         ──creates emulator──▶ healthy
         ──registers routes──▶ entity available
```

## Implementation Approach

### 1. Entity Status Model

Add entity lifecycle states:

```csharp
enum EntityStatus
{
    /// <summary>Entity is available and routed to a healthy emulator.</summary>
    Active,

    /// <summary>Emulator is being provisioned. Entity will be available soon.</summary>
    Provisioning,

    /// <summary>Emulator provisioning failed. Entity is not available.</summary>
    Failed
}

record EntityState
{
    string Name { get; }
    EntityStatus Status { get; }
    string? ErrorMessage { get; }
    DateTime? EstimatedReadyTime { get; }
}
```

### 2. Management Proxy Changes

**`ManagementProxy.cs`:**

When a `PUT` request creates a new entity and a new emulator is needed:

1. Register the entity in the router with `EntityStatus.Provisioning`
2. Queue a background spin-up task
3. Return `202 Accepted` with a `Location` header pointing to a status endpoint
4. The status endpoint (`/_proxy/entities/{name}/status`) returns the current entity state

When the emulator is ready:
1. Update entity status to `Active`
2. Register the AMQP route

If provisioning fails:
1. Update entity status to `Failed`
2. Return error details on the status endpoint

### 3. AMQP Proxy Changes

**`AmqpProxy.cs`:**

When a client tries to attach a link to a `Provisioning` entity:

Option A: **Retry with backoff** — The proxy holds the Attach request and periodically checks if the entity is ready. Once ready, complete the Attach. If timeout, reject with a retryable error.

Option B: **Immediate reject with retryable error** — Return an AMQP error with condition `amqp:not-found` and a description indicating the entity is provisioning. The ServiceBus SDK's retry policy will retry the connection.

**Recommended: Option A** — This provides the best client experience. The client's `CreateSender`/`CreateReceiver` call blocks until the entity is available, rather than failing and requiring application-level retry.

```csharp
async Task HandleEntityLinkAsync(AttachContext context, Attach attach, string address)
{
    var entityName = ParseTopLevelEntity(address);
    var instance = router.GetBackend(entityName);

    if (instance is null)
    {
        // Check if entity is provisioning
        var status = router.GetEntityStatus(entityName);
        if (status == EntityStatus.Provisioning)
        {
            // Wait for provisioning to complete (with timeout)
            instance = await router.WaitForEntityAsync(entityName, timeout: TimeSpan.FromSeconds(120));
            if (instance is null)
            {
                context.Complete(new Error(ErrorCode.InternalError)
                    { Description = $"Entity '{entityName}' provisioning timed out" });
                return;
            }
        }
        else
        {
            context.Complete(new Error(ErrorCode.NotFound)
                { Description = $"Entity '{entityName}' does not exist" });
            return;
        }
    }

    // Proceed with normal link setup...
}
```

### 4. Status Endpoint

Add a new HTTP endpoint:

```
GET /_proxy/entities/{name}/status

Response:
{
    "name": "topic.new",
    "status": "provisioning",
    "estimatedReadyTime": "2025-01-15T10:32:00Z",
    "shardIndex": 3
}
```

### 5. Background Spin-Up Queue

Use a `Channel<SpinUpRequest>` for the background queue:

```csharp
class AsyncSpinUpService : BackgroundService
{
    Channel<SpinUpRequest> _queue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                var instance = await orchestrator.ProvisionShardAsync(request.ShardIndex, request.Config);
                router.Register(request.EntityName, instance);
                router.SetEntityStatus(request.EntityName, EntityStatus.Active);
            }
            catch (Exception ex)
            {
                router.SetEntityStatus(request.EntityName, EntityStatus.Failed, ex.Message);
            }
        }
    }
}
```

### 6. Client SDK Compatibility

The `ServiceBusAdministrationClient` expects synchronous entity creation (the `PUT` returns the created entity). With async spin-up:

- **Option A**: Return `202 Accepted` — The admin client may not handle this gracefully. It expects a `201 Created` with the entity body.
- **Option B**: Return `201 Created` immediately with the entity definition, but mark the entity as not-yet-routable internally. The admin client is happy, but AMQP connections will block until the emulator is ready.

**Recommended: Option B** — Return `201 Created` to the admin client immediately. The entity is "created" from the management perspective but not yet available for messaging. AMQP connections will wait (via Option A from section 3) until the backend is ready.

## Testing Strategy

1. Create a topic that triggers a new emulator spin-up
2. Immediately try to send a message to the topic
3. Verify the send blocks (not fails) until the emulator is ready
4. Verify the message is delivered after the emulator starts
5. Test concurrent entity creation during spin-up
6. Test timeout behavior when emulator fails to start

## Risks and Considerations

- **Timeout management**: Need configurable timeouts for AMQP wait and overall provisioning
- **Resource contention**: Multiple concurrent spin-up requests could strain Docker/system resources
- **State consistency**: Entity status transitions must be thread-safe
- **Client retry storms**: If AMQP rejects with retryable error, multiple clients might retry simultaneously

## Files to Modify

- `servicebus/proxy/Services/AmqpProxy.cs` — Add wait-for-provisioning logic
- `servicebus/proxy/Services/ManagementProxy.cs` — Return 201 immediately, queue background work
- `servicebus/proxy/Services/EmulatorOrchestrator.cs` — Extract `ProvisionShardAsync` for background use
- `servicebus/proxy/Services/EntityRouter.cs` — Add entity status tracking and wait mechanism
- `servicebus/proxy/Program.cs` — Register `AsyncSpinUpService` as hosted service

## Estimated Effort

Low-medium. The core changes are well-isolated. Main risk is testing the async flow reliably. Estimate 1-2 days of implementation + testing.
