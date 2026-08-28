# SQLFerret documentation

Start at the [project README](../README.md) if you have not read it yet.

## By task

**"I have a `.xel` file and no idea what to do with it."**
→ [Getting started](getting-started.md)

**"I need to set up a capture session."**
→ [Capture session](capture-session.md)

**"What does this command do again?"**
→ [CLI reference](cli-reference.md) · [Terminal UI](tui.md)

**"I want to write my own query against the data."**
→ [Data model](data-model.md)

**"Two statements I expected to group together did not."**
→ [Query normalization](normalization.md)

**"Something is wrong with a plan."**
→ [Execution plans](execution-plans.md)

**"Queries are blocking each other."**
→ [Blocking and deadlocks](blocking.md)

**"I need coverage beyond my trace window."**
→ [Query Store](query-store.md)

**"I need to share this outside my organization."**
→ [Privacy and redaction](privacy.md)

**"Where do I put my connection string?"**
→ [Configuration](configuration.md)

**"I want to change the code."**
→ [Architecture](architecture.md) · [Development](development.md)

## Full index

| Document | Covers |
|---|---|
| [getting-started.md](getting-started.md) | Capture, build, import, analyze, export — the whole first run |
| [capture-session.md](capture-session.md) | Events, actions, a ready-to-paste session, and field notes from a real trace |
| [cli-reference.md](cli-reference.md) | `import`, `top-slow`, `export-blocking`, `export-events`, `query-store-import`, `obfuscate-plan` |
| [tui.md](tui.md) | Views, key map, drill-down, replay copy, persisted state |
| [configuration.md](configuration.md) | `sqlferret.config.json`, `.env`, resolution order, `project.json` |
| [data-model.md](data-model.md) | Every DuckDB table and column, plus a library of useful queries |
| [normalization.md](normalization.md) | Tokenization, classification, fingerprinting, versioning, guarantees |
| [execution-plans.md](execution-plans.md) | Plan identity, deduplication, the eight findings, digest and index formats |
| [blocking.md](blocking.md) | Blocked-process capture, the relational model, the digest, chains, XML export |
| [query-store.md](query-store.md) | Snapshot import, time windows, version differences, units |
| [privacy.md](privacy.md) | What lands on disk, redaction policies, plan obfuscation, threat model |
| [architecture.md](architecture.md) | Layering, design rules, invariants, data flow, how to extend it |
| [development.md](development.md) | Build, test, conventions, known gaps, dependencies |
