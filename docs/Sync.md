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
| AlreadyApplied | The message names the receiver’s current revision. Keep the current state. |
| StaleBaseline | The expected baseline differs or the message is older. Request a fresh snapshot when the receiver missed an update. |
| SchemaMismatch | The generated schema, comparison settings, or registered value contract differs. Align the endpoint builds and settings. |
| InvalidPacket | The packet header or size is invalid. |
| InvalidOperation | The payload is malformed or an operation could not be validated or applied. Inspect the returned issue. |

Assign each distinct state a unique revision and keep the receiver’s state and revision together. `Preview` produces the same candidate as `Apply` without committing it. Both stage mutations on a separately serialized copy. Validation and failed application leave the supplied receiver model untouched. Model accessors and custom serializer converters should be side-effect free. Commit the returned value under the application’s normal concurrency control.

## Schema evolution and fallback

The generated fingerprint includes the generated member schema, comparison settings, snapshot member names, and registered value IDs. Increment `ContractVersion` when changing custom comparer rules or serializer behavior within the same types. If a receiver missed a revision, send `CreateSnapshot(desired, revision)`. A snapshot can advance a receiver across missing revisions.

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

The transport starts with the four bytes `DES1`. It then carries the unsigned 64-bit schema fingerprint, signed 64-bit baseline revision, signed 64-bit resulting revision, and a one-byte payload kind. Integers use little-endian byte order. Kind 0 carries a headerless BDC1 operation document. Kind 1 carries UTF-8 snapshot JSON and uses baseline revision −1. Defaults limit message size to 16 MiB and patch nesting to 64.

Existing BDC1 payloads retain their wire format. Content encoded with registered value types uses an opt-in tag and requires the same registry at both ends.
