# Checked model sync

Use the same generated model schema and options at both endpoints. Use source-generated `JsonTypeInfo<T>` for snapshots. Registered binary types use explicit numeric IDs and serializer metadata on both ends.

```csharp
var sync = current.CreateDeepSync(InventoryJson.Default.Inventory, new DeepSyncOptions
{
    ContractVersion = 1,
    Profile = inventoryProfile,
    ValueTypes = new(new BinaryDeltaType(1, InventoryJson.Default.InventoryLine))
});

var message = sync.CreateUpdate(current, desired, baselineRevision: 17, revision: 18);
var result = sync.Apply(current, currentRevision: 17, message.Bytes);
if (result.Status == DeepSyncStatus.Applied)
{
    current = result.Value!;
    revision = result.Revision;
}
```

## Receiver behavior

| Status | Meaning |
| --- | --- |
| Applied | Commit the returned model and revision together. |
| AlreadyApplied | The message names the receiver’s current revision and its expected result matches the current state. |
| StaleBaseline | The baseline revision differs or the message is older. Request a fresh snapshot when the receiver missed an update. |
| SchemaMismatch | The generated schema, comparison settings, or registered value contract differs. Align the endpoint builds and settings. |
| InvalidPacket | The packet header or size is invalid. |
| InvalidOperation | The payload is malformed or an operation could not be validated or applied. Inspect the returned issue. |
| StateMismatch | The receiver has the wrong state at the expected revision, a replay conflicts with the current state, or staging produced an unexpected result. Keep the current model and revision and request a newer snapshot. |

Assign each distinct state a unique revision and keep the receiver’s state and revision together. A patch checks the current state before staging operations and checks the staged result before returning it. Snapshots verify their result too. `Preview` and `Apply` both stage mutations on an isolated copy. Commit the returned model and revision together under the application’s normal concurrency control. Keep inputs stable for the duration of each call and use side-effect-free accessors and converters that create independent copies.

State identity covers the snapshot serializer’s JSON representation. The digest sorts object property names using ordinal comparison, so dictionary insertion order can differ. Array order and scalar representations remain significant. A comparison profile can treat two values as equal while their serialized states differ. The sender may use a full snapshot in that case to deliver the exact desired state. The snapshot serializer must also round-trip the values used in comparisons.

## Schema evolution and fallback

The generated fingerprint includes the generated member schema, comparison settings, snapshot member names, and registered value IDs. Increment `ContractVersion` when changing custom comparer rules or serializer behavior within the same types. If a receiver missed a revision, send `CreateSnapshot(desired, revision)`. A snapshot can advance a receiver across missing revisions.

Upgrade both endpoints from DES1 to DES2 and start again with a verified snapshot. DES1 packets are rejected because they cannot prove the baseline or result. To repair drift at a revision the receiver already holds, issue the replacement snapshot with a new revision.

`CheckBinaryPatch` gives the property or collection path of an unsupported value. For model instances, register their generated `JsonTypeInfo` with a stable ID. A typed collection replacement can be registered as a whole. When sync cannot validate or round-trip a patch, it returns an explicit full-snapshot message with a reason. Snapshot serializers must preserve the compared state.

Native AOT supports scalar operations, explicitly registered typed values, and source-generated snapshots. Register typed containers and enums that would otherwise use runtime type construction or assembly lookup.

## Inspect a capture

The inventory sample can write and inspect its own sync packets using the generated schema.

```sh
dotnet run --project samples/DeltaRoundTrip -c Release -- write inventory.patch
dotnet run --project samples/DeltaRoundTrip -c Release -- inspect inventory.patch
dotnet run --project samples/DeltaRoundTrip -c Release -- apply inventory.patch
```

Applications can call `Inspect` and format the resulting operations as text or JSON. The JSON describes operations as received and omits prior values that are absent from the patch.

## Wire envelope

DES2 uses the following envelope. Integers use little-endian byte order.

| Offset | Field |
| ---: | --- |
| 0 | Four ASCII bytes `DES2` |
| 4 | Unsigned 64-bit schema fingerprint |
| 12 | Signed 64-bit baseline revision |
| 20 | Signed 64-bit resulting revision |
| 28 | One-byte payload kind |
| 29 | 32-byte SHA-256 baseline digest |
| 61 | 32-byte SHA-256 resulting-state digest |
| 93 | Payload |

Kind 0 carries a headerless BDC1 operation document. Kind 1 carries UTF-8 snapshot JSON, uses baseline revision −1, and sets the baseline digest to all zeros. State digests cover compact JSON generated from the configured snapshot serializer, with recursively sorted, unique object property names and preserved array order. Defaults limit the complete message to 16 MiB and patch nesting to 64.

Existing BDC1 payloads retain their wire format. Content encoded with registered value types uses an opt-in tag and requires the same registry at both ends.
