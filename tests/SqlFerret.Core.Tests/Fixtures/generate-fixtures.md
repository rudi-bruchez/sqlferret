# Generating .xel fixtures

## Real-life testing (primary approach)

Integration tests that exercise `XelReader` against real SQL Server Extended Events captures
use the local `sample/` folder at the repository root. This folder is **gitignored** — it
contains production query text and is never committed. On a developer machine that has the
traces available, the `Reads_real_workload_events` SkippableFact will run against the smallest
`performances_*.xel` file it finds there (by file length). On CI, or any machine without the
folder, the test is automatically skipped.

## Optional: producing a committed synthetic fixture

If you want a small, safe, committed `.xel` fixture (e.g. for CI without a real capture),
run once on a machine with Docker or Podman. The resulting `sample_basic.xel` should be placed
in this `Fixtures/` directory and committed.

```bash
podman run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Strong!Passw0rd' \
  -p 1433:1433 -d --name sfedge mcr.microsoft.com/azure-sql-edge:latest

# Create an event session writing to /var/opt/mssql/log, run a few queries, stop it:
sqlcmd -S localhost -U sa -P 'Strong!Passw0rd' -Q "
CREATE EVENT SESSION sf ON SERVER
  ADD EVENT sqlserver.sql_batch_completed,
  ADD EVENT sqlserver.rpc_completed
  ADD TARGET package0.event_file(SET filename='/var/opt/mssql/log/sample_basic.xel', max_rollover_files=1)
  WITH (MAX_DISPATCH_LATENCY=1 SECONDS);
ALTER EVENT SESSION sf ON SERVER STATE = START;
SELECT * FROM sys.databases WHERE database_id = 1;
EXEC sp_who;
WAITFOR DELAY '00:00:02';
ALTER EVENT SESSION sf ON SERVER STATE = STOP;"

podman cp sfedge:/var/opt/mssql/log/sample_basic.xel ./sample_basic.xel
# Trim/rename the rollover suffix if present so the committed file is `sample_basic.xel`.
```

The committed fixture must contain at least one `sql_batch_completed` and one `rpc_completed`
event with non-empty SQL text.

The `SqlFerret.Core.Tests.csproj` already includes:

```xml
<ItemGroup>
  <None Include="Fixtures/**/*.xel" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

so any `.xel` placed here will be copied to the test output directory automatically.
