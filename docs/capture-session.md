# Capture session: events and actions

What to put in your Extended Events session so that SQLFerret can do its job, and what happens if
you leave something out.

## Events

| Event | Role |
|---|---|
| `sqlserver.rpc_completed` | Stored procedure and `sp_executesql` executions |
| `sqlserver.sql_batch_completed` | Ad-hoc batch executions |
| `sqlserver.query_post_execution_plan_profile` | **Actual** plan with runtime counters |
| `sqlserver.blocked_process_report` | Blocking incidents (requires a server-level threshold, see [blocking.md](blocking.md)) |
| `sqlserver.xml_deadlock_report` | Deadlock graphs |

> **Do not substitute `query_post_execution_showplan` for `query_post_execution_plan_profile`.**
> They look interchangeable and are not. The showplan variant is dramatically more expensive and
> has no business running on a production instance. SQLFerret matches the profile event by exact
> name precisely so the expensive one cannot be routed here by accident.

## Actions

`query_post_execution_plan_profile` self-identifies: `QueryHash` and `QueryPlanHash` are
attributes of `<StmtSimple>` **inside the plan XML**. No action is needed on that event for
shape-level correlation.

What is missing is on the completion side:

| Action | Put it on | What it enables |
|---|---|---|
| `sqlserver.query_hash` | `rpc_completed`, `sql_batch_completed` | Join `executions.query_hash` ↔ `plan_profiles.query_hash` |
| `sqlserver.query_plan_hash` | `rpc_completed`, `sql_batch_completed` | Join on plan shape |
| `package0.attach_activity_id` | **every** event | A GUID plus a sequence number shared within one batch: correlation per *execution*, not per query shape |
| `sqlserver.session_id` | every event | Fallback, combined with event ordering |
| `sqlserver.database_name`, `sqlserver.client_app_name`, `sqlserver.client_hostname` | completion events | Slicing the workload by origin |

**Never use `<StmtSimple StatementText>` as a key.** It is truncated, and it can be nearly empty.
On our reference plans it is 1,518 characters in one case and **2 characters** in another, for a
complete `SELECT` statement in both.

If you skip `sqlserver.query_hash`, SQLFerret still ingests everything, but `import` warns you:

```text
warning: 346 plan profiles ingested, but no execution carries query_hash.
         Plans cannot be correlated with queries.
         Add ACTION(sqlserver.query_hash) to rpc_completed / sql_batch_completed.
         See docs/capture-session.md
```

## Ready-to-paste session

```sql
CREATE EVENT SESSION [sqlferret_capture] ON SERVER
  ADD EVENT sqlserver.rpc_completed (
      ACTION (sqlserver.query_hash, sqlserver.query_plan_hash,
              package0.attach_activity_id, sqlserver.session_id,
              sqlserver.database_name, sqlserver.client_app_name)
      WHERE duration > 100000),
  ADD EVENT sqlserver.sql_batch_completed (
      ACTION (sqlserver.query_hash, sqlserver.query_plan_hash,
              package0.attach_activity_id, sqlserver.session_id,
              sqlserver.database_name, sqlserver.client_app_name)
      WHERE duration > 100000),
  ADD EVENT sqlserver.query_post_execution_plan_profile (
      ACTION (package0.attach_activity_id, sqlserver.session_id,
              sqlserver.database_name))
  ADD TARGET package0.event_file (SET filename = N'sqlferret', max_file_size = 256)
  WITH (MAX_MEMORY = 8192 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
        MAX_DISPATCH_LATENCY = 30 SECONDS, STARTUP_STATE = OFF);
```

`duration > 100000` is 100 ms, because Extended Events expresses `duration` in microseconds.
Raise the threshold on a busy server; lower it when hunting a chatty ORM.

To add blocking and deadlocks:

```sql
ALTER EVENT SESSION [sqlferret_capture] ON SERVER
  ADD EVENT sqlserver.blocked_process_report,
  ADD EVENT sqlserver.xml_deadlock_report;
```

Start, run your workload, stop, then copy the `.xel` files off the instance:

```sql
ALTER EVENT SESSION [sqlferret_capture] ON SERVER STATE = START;
-- … later …
ALTER EVENT SESSION [sqlferret_capture] ON SERVER STATE = STOP;
```

## Manual correlation, when the actions are missing

Digests and `index.json` publish `captured_at_utc` in ISO 8601 UTC with microsecond precision.
That timestamp is the fallback correlation path when the plans and the executions cannot be
joined on a hash.

**SSMS shows extended events in local time by default.** Tick "Display values in UTC" in the
viewer, or manual correlation will be off by a whole time zone.

---

# Field notes from the reference trace

The rest of this page records what was actually measured against a real production capture
(`trace_0.xel`, 39.4 MB, 2026-08-04) rather than what the documentation predicted. It is kept
because several of the predictions were wrong in ways worth knowing about.

## First pass: a throwaway XELite probe

Run with `Microsoft.SqlServer.XEvent.XELite` 2024.2.5.1 on .NET 10, before the real parser
existed.

**1. Which field carries the plan XML.** `showplan_xml`, confirmed. It is present as-is in
`IXeEventData.Fields` for `query_post_execution_plan_profile`. The full field list:

```text
cpu_time, database_name, dop, duration, estimated_cost, estimated_rows,
granted_memory_kb, ideal_memory_kb, nest_level, object_id, object_name,
object_type, requested_memory_kb, serial_ideal_memory_kb, showplan_xml,
source_database_id, used_memory_kb
```

**2. `duration` and `cpu_time`.** Both are present on `query_post_execution_plan_profile`. The
writer still types them as `long?`, because that is true of *this* trace, not a guarantee the
engine makes for every capture.

**3. Activity actions.** **Neither `activity_id` nor `attach_activity_id` was present** on the
actions actually observed, on any of the three event types in this trace. What was there:

- `query_post_execution_plan_profile`: `client_app_name`, `client_hostname`, `sql_text`,
  `username`. The `sql_text` action on this event was not anticipated, and is a bonus rather than
  a contract.
- `rpc_completed` / `sql_batch_completed`: `client_app_name`, `client_hostname`, `username`.

This corrects an earlier reading that listed `activity_id` and `database_name` as present, which
came from extracting UTF-16 strings out of the metadata block. That method finds names *declared*
somewhere in the binary, not actions actually attached to an event. A live XELite read
(`IXEvent.Actions`) is authoritative, and on this trace both are absent. No per-activity
correlation is possible on this capture.

**4. `xe.Timestamp.Kind`.** `IXEvent.Timestamp` is a `DateTimeOffset`, not a `DateTime`, so
reading `.Kind` directly does not compile. On this trace all three event types report
`Offset=00:00:00` and `UtcDateTime.Kind=Utc`, so XELite hands back timestamps already expressed in
UTC:

```text
TIMESTAMP: 2026-08-04T14:20:11.9144860+00:00  Offset=00:00:00
UtcDateTime=2026-08-04T14:20:11.9144860Z  Kind=Utc
```

The manual-correlation-in-UTC contract holds. `XelReader` already calls `.UtcDateTime`, so
`IXeEventData.Timestamp` is a plain UTC `DateTime` by the time Core sees it.

**5. Volume.** 346 plan events, **62 distinct plans**. Far below the point where the artifact
writer would need any cap.

## Second pass: the real `import` command

Everything above came from a probe. The following came from running the shipped V1 pipeline
end to end on the same file, twice.

- **897 events read** (`rpc_completed` + `sql_batch_completed` + `query_post_execution_plan_profile`).
- **346 plan events ingested, 62 distinct plans**, matching the probe exactly, this time through
  the real `XDocument` parser rather than a probe regex.
- **85 `.sqlplan` files written**: 62 first occurrences (`p_*`, `m_*` or `c_*` depending on how the
  plan identity was derived) plus 23 `.worst.sqlplan` files, for the plans where a later execution
  was strictly slower than the first. 85 exceeds 62 by exactly those 23 plans having two files.
- **62 `*.digest.json`** (one per distinct plan) and **1 `index.json`**.
- **The correlation warning fired**, because the trace carries no `sqlserver.query_hash` action on
  its completion events, so no `executions` row in the run has a non-null `query_hash`.

Which is the documented scenario at the top of this page: without capturing
`sqlserver.query_hash`, timestamp-based manual correlation is the only path left.
