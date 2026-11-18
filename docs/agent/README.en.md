# Local agent interface (MCP + CLI)

This page documents the first local-agent interface in the **source development branch**.
The currently published `v2.6.3` installer does **not** include it, and this branch does not
publish a new release. Do not use this document as a claim about the published installer.

The interface is local-only and current-user-only:

- The `拆包智录` / Lingtu Unpack Recorder desktop app must already be running and have finished
  settings, record-repository and Excel-queue initialization.
- MCP uses a local stdio command. No remote HTTP listener or LAN endpoint is exposed.
- CLI/MCP calls the running desktop. It never starts the desktop, a camera, or the compatibility
  sync service automatically.
- CLI and MCP share the same `RecordAutomationService`; they cannot drift into separate business rules.

## Where the binary lives

Source build:

```text
src/UnpackVision.Agent/bin/Debug/net10.0/UnpackVision.Agent.exe
```

`scripts/publish.ps1` places the self-contained Agent at:

```text
artifacts/publish/App/Agent/UnpackVision.Agent.exe
```

The publish directory also includes `THIRD_PARTY_NOTICES.md` and
`Agent/LICENSES/` with the ModelContextProtocol (Apache-2.0) and
System.CommandLine (MIT) texts.

## Connection boundary

After desktop initialization, the app listens on a random IPv4 loopback port and writes:

```text
%LOCALAPPDATA%\UnpackVision\ai\connection.json
```

That file contains the current per-start Bearer token and inherits the current user's local-app-data
permissions. Publishing and owner cleanup use the same cross-process sidecar lock for the path and
revalidate inside the lock before replacing/deleting, so an older instance cannot delete a file a
newer instance just published. The client rereads the file for every call, so a desktop restart can
rotate the port and token without restarting the Agent. Tests may override the path with
`UNPACKVISION_AGENT_CONNECTION`; normal users should not set it.

The client accepts only a literal `http://127.0.0.1:<port>/`. It rejects `localhost`, IPv6,
LAN hosts, `https`, paths, userinfo and redirects. Proxies and automatic retries are disabled;
the per-call timeout is 10 seconds. On exit the desktop cancels requests, waits for Kestrel to
shut down, and deletes the connection file only when its own token and PID still match.

## CLI

Every business command prints one JSON object on stdout. `--help` and `schema` do not read the
connection file and work while the desktop is stopped. Error example:

```json
{"ok":false,"error":{"code":"unavailable","message":"UnpackVision desktop is not running or has not finished initialization."}}
```

Success example:

```json
{"ok":true,"tool":"get_status","result":{"desktopReady":true,"excelSync":{"connectorId":"excel","queuedIsSynced":false}}}
```

Parser errors (unknown command, unknown option, non-numeric `--limit`, missing required
argument) also print one `invalid_argument` JSON object on stdout and exit with code 1;
they do not fall back to stderr-only text.

Commands:

```text
UnpackVision.Agent.exe get-status
UnpackVision.Agent.exe list-records [--tracking-no <text>] [--limit <1..200>]
UnpackVision.Agent.exe get-record --record-id <GUID>
UnpackVision.Agent.exe list-tags
UnpackVision.Agent.exe set-record-note --record-id <GUID> --note <text>
UnpackVision.Agent.exe add-record-tag --record-id <GUID> --tag-id <TAG_ID>
UnpackVision.Agent.exe remove-record-tag --record-id <GUID> --assignment-id <ASSIGNMENT_GUID>
UnpackVision.Agent.exe retry-excel-sync
UnpackVision.Agent.exe schema
UnpackVision.Agent.exe --help
```

Exit codes:

| Code | Meaning |
| ---: | --- |
| 0 | Success |
| 1 | Usage, argument or field validation failure |
| 2 | Desktop not running or not initialized |
| 3 | Stale/incorrect connection token or authentication failure |
| 4 | Record, tag or tag assignment not found |
| 5 | Record is not completed/imported, or the state does not allow the write |
| 6 | Transport failure or unexpected server response |
| 7 | Unexpected local failure |

Bounds: `recordId`/`assignmentId` must be a non-empty GUID; `limit` is 1..200 (default 50);
`trackingNo` is at most 64 characters; `note` must be provided explicitly and is at most 2000
characters; an explicit empty string clears the note, while an omitted CLI `--note` or HTTP/MCP
`null` is rejected; `tagId` is at most 64 characters.

## MCP

Start the stdio server:

```text
UnpackVision.Agent.exe mcp
```

MCP stdout carries protocol frames only. Tool failures are returned as a MCP
`CallToolResult` with `isError=true` and a structured `structuredContent` error object.
`initialize`, `tools/list`, help and schema do not read the connection file, so tools can be
discovered while the desktop is closed. A real tool call reports that the desktop is not ready.

The local Codex `mcp add` syntax was checked with `codex mcp add --help` (no real registration
was performed):

```text
codex mcp add unpackvision -- "C:\path\App\Agent\UnpackVision.Agent.exe" mcp
```

This is an integration example only. Replace the path with the actual publish directory. Do not
use `--url`; this version does not expose a remote MCP HTTP endpoint. Registering the command in
Codex changes the user's own configuration; repository scripts do not do that automatically.

## The eight tools

| Tool | CLI | Purpose |
| --- | --- | --- |
| `get_status` | `get-status` | Returns `desktopReady`, recording state, `excelSync` semantics and scope |
| `list_records` | `list-records` | Returns a bounded record list; `hasMore=true` means matches were not returned, never a claim of complete history |
| `get_record` | `get-record` | Returns one record, tags, sync state and the media paths owned by that record |
| `list_tags` | `list-tags` | Returns configured issue tags; no credentials |
| `set_record_note` | `set-record-note` | Updates a completed/imported record note and enqueues Excel sync |
| `add_record_tag` | `add-record-tag` | Adds an enabled tag to a completed/imported record and enqueues Excel sync |
| `remove_record_tag` | `remove-record-tag` | Removes an active tag assignment and enqueues Excel sync |
| `retry_excel_sync` | `retry-excel-sync` | Requeues all non-succeeded Excel deliveries; does not write the workbook directly |

## Write and sync semantics

- Only `Completed` or `Imported` records are writable. Active recording states are rejected to
  avoid overwriting in-memory coordinator state across threads.
- A successful note/tag write uses the existing `EnqueueDeliveryAsync("excel", ...)` path.
  **Queued does not mean synced**: Excel may not be open, may be locked, or may retry later.
  The `excelSync` result field reflects the queue state explicitly.
- `retry_excel_sync` uses the existing `RetryConnectorAsync("excel")` path. It requeues all
  `Pending/Processing/Failed` Excel deliveries, does not touch other connectors, and does not
  force an immediate workbook write.
- The desktop uses one shared record-mutation gate to serialize agent note/tag writes, the Excel
  delivery claim → latest-record read → push → completion sequence, history-window edits and
  recording renames. An older push therefore cannot flip a newly queued Pending row back to
  Succeeded, and an old record snapshot cannot overwrite a new agent note. Agent writes may wait
  while an Excel push is in progress. The gate coordinates one desktop process; it does not claim
  to coordinate another process that writes the same SQLite database directly.
- If the connection drops after a write, the caller may not know whether it completed. The first
  version does not automatically retry writes; re-query the record/tag before retrying. Setting
  the same note, adding the same tag again, or retrying the same already-removed assignment
  requeues the current record even when an older delivery says Succeeded. That status may refer
  to the version before a partially committed mutation; a retry must not silently skip the new data.
- After a successful durable write the desktop refreshes the recent list and any open all-records
  window. It does not rewrite the note input for a different in-progress recording. Runtime state
  and the tag catalog are published as immutable snapshots by the WPF thread; background requests
  do not read UI controls field by field.

## Explicitly out of scope

The first version does not provide start/stop recording, record deletion, device pairing,
arbitrary shell commands, arbitrary file reads or direct Excel workbook writes. It does not read
recording content or return camera credentials, phone pairing tokens, or raw sync-connector errors.
It does not change the mobile protocol, StationHost scan routing, legacy Service API, or the
published installer.
