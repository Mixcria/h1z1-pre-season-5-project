# Login character lifecycle, 2026-09-26

Scope: August 2017 C# restoration, account admission, character create/select/delete,
and failed local persistence. These changes enforce the restoration's own lifecycle
invariants; they do not establish original retail account policy. No client, packet
layout, failure status, live account, or endpoint configuration changes.

## Source findings and changes

`LoginService.OnMessage` already resolves an account before roster operations.
Character selection checks ownership, GUID/server membership, and available status;
deletion checks ownership. `LocalAccountDirectory` hashes provisioned credentials,
requires a matching account for registered launcher tunnels, and rejects unknown
or expired `cb1.` tickets without falling back to loopback development admission.
Existing account-directory updates save a candidate before exposing it in memory.
These paths did not need an ownership redesign.

Two reachable inconsistencies were found:

1. **Deleted-character handoffs stayed usable.** `SendCharacterLoginSuccess` issues
   a five-minute gateway ticket carrying identity and appearance. The registry's
   validator deliberately permits retries and does not reread the roster. Deleting
   the character did not revoke those tickets, so a subsequent validation could
   accept a character no longer in the select roster. `RevokeCharacter` now removes
   every ticket for that GUID after a successful durable delete, before its reply.
   Foreign, missing, or failed deletes do not revoke tickets; other characters and
   normal reusable handoffs are unchanged.
2. **Failed roster saves changed only memory.** `CharacterRosterStore` previously
   inserted/removed rows and advanced the high-water mark before its write and
   rename. An I/O failure could leave a name occupied or a character missing until
   restart while disk still held the previous roster. Create/delete now save a
   candidate snapshot first and publish it under the existing lock only after
   rename succeeds. `LoginService` returns the existing create/delete failure
   status on `IOException` or `UnauthorizedAccessException`, allowing a subsequent
   request instead of throwing into the transport's service-error disconnect path.

The persisted GUID high-water policy remains unchanged: committed characters'
GUIDs are never reused, even after deletion. A create that never commits does not
reserve a GUID. `Load` documentation now reflects its existing behavior: missing
files start empty; read/parse failures propagate instead of silently resetting.

## Ordering and remaining limits

- The host uses one `SoeListener` for `LoginService`; its UDP and queued relay
  dispatch execute on the listener loop. Selection/ticket issuance and deletion
  therefore run serially there. `GatewayTicketRegistry` protects validation and
  revocation with the same lock on the separate gateway listener. After a successful
  delete reply, later validations of those tickets fail. A validation that already
  completed before revocation may still finish admission; revocation does not evict
  an already connected character. Defining deletion while that character is in a
  live zone requires a separate cross-listener lifecycle decision.
- `HandleCharacterCreate` first saves the roster, then binds its account. If the
  account write fails, its existing rollback durably removes the new roster row.
  A successful rollback allows the same name on retry while retiring the committed
  GUID. These are two files, not an atomic cross-file transaction: a process exit
  between saves or failure of both membership save and rollback can leave an
  unowned roster row. A journal/recovery design is still required to close that gap;
  this patch does not claim to do so.
- Deleted GUID ownership entries are retained in the separate directory and do
  not authorize selection without an available roster row. They also support
  account/history mappings and cannot collide with later allocated GUIDs. No
  ownership-history purge was added without a retention policy.
- Appearance/profile IDs, broader create-screen policy, and original server
  deletion/reconnect rules remain outside this source-only assessment.

## Validation status

**Implemented; locally passing** in the final Release suite. The
[batch validation record](inventory-persistence-batch-20260926.md#validation-record)
owns exact totals and artifacts. The checks below use synthetic accounts and
isolated stores. Native gameplay repetitions for this batch: zero.

`CharacterLifecycleTests` contributes nine passing cases using fresh temporary stores and
direct `LoginService.OnMessage` dispatch, without sockets. Directory blockers
deterministically fail the write or rename rather than relying on timing:

- Failed create/delete preserves live memory and saved state at both failure points,
  then succeeds on retry without GUID reuse or a stuck name (four cases).
- Successful delete revokes all its handoffs, rejects reselection, and keeps another
  character's handoff; a foreign delete cannot revoke the owner's ticket (two cases).
- Login create/delete reports failure and accepts the subsequent valid request;
  failed deletion retains its ticket until successful retry (two cases).
- Failed account binding rolls back the new row; retry preserves ownership after
  both stores are reloaded (one case).

Reproduction filter:

```text
FullyQualifiedName~CharacterLifecycleTests|FullyQualifiedName~AccountAdmissionTests|FullyQualifiedName~RosterPersistenceTests|FullyQualifiedName~LocalAccountDirectoryTests|FullyQualifiedName~CharacterDeleteTests|FullyQualifiedName~LoginPacketTests
```
