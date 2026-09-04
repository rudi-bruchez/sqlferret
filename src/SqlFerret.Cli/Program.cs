// src/SqlFerret.Cli/Program.cs
using Microsoft.Data.SqlClient;
using SqlFerret.Cli;
using SqlFerret.Core.Analysis;
using SqlFerret.Core.Config;
using SqlFerret.Core.Filtering;
using SqlFerret.Core.Ingestion;
using SqlFerret.Core.Normalization;
using SqlFerret.Core.Parameters;
using SqlFerret.Core.Project;
using SqlFerret.Core.Server;
using SqlFerret.Core.Storage;

// Le meme avertissement est emis par 'top-slow' et par 'query' : une seule source de verite,
// sans quoi les deux textes divergent au premier ajustement.
const string StaleClassificationNote =
    "note: classification anterieure detectee — lancer 'reclassify' pour typer le DDL";

string Arg(string name, string? fallback = null)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? "";
}

AuditProject? OpenProject()
{
    var dir = Arg("--project");
    if (string.IsNullOrWhiteSpace(dir))
    {
        Console.Error.WriteLine("--project <dir> is required");
        return null;
    }
    AuditProject ap;
    try { ap = AuditProject.OpenOrCreate(dir, Directory.GetCurrentDirectory()); }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
    {
        // File-at-path guard, malformed sqlferret.config.json, or a permission/IO error:
        // present a clean message instead of an unhandled stack trace.
        Console.Error.WriteLine($"--project: {ex.Message}");
        return null;
    }
    // Surface corrupt-manifest recovery so the user knows provenance was reset.
    if (ap.ManifestWarning is { } warning)
        Console.Error.WriteLine(warning);
    return ap;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: import <path> --project <dir> [--redaction off|hash|masked|full] [--sanitize-sql-text raw|literals] | top-slow --project <dir> | export-blocking --project <dir> [...] | query-store-import --project <dir> [--conn <s>] [--database <db>] [--no-plans] [--from <dt> --to <dt> | --last <N>{h|d}] | export-events --project <dir> --out <dir> [--kind blocking|deadlock|both] [--from <dt> --to <dt> | --last <N>{h|d}] [--fingerprint <hash>] [--database <id>] [--limit <n>] | obfuscate-plan (--in <file> --out <file> | --in-dir <dir> --out-dir <dir> [--map <file>] | --project <dir> --plan-id <id>) | export-health --project <dir> [--format json|md|both] [--out <file>] [--limit <n>] | query --project <dir> (--sql <texte> | --file <chemin>) [--format table|csv|json|md] [--limit <n> (defaut 1000) | --no-limit] [--raw] | reclassify --project <dir> [--force]");
    return 1;
}

switch (args[0])
{
    case "import":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("import: missing <path> argument");
                return 1;
            }
            var path = args[1];
            var project = OpenProject();
            if (project is null) return 1;

            var redactionStr = Arg("--redaction", project.Config.RedactionPolicy);
            if (!Enum.TryParse<RedactionMode>(redactionStr, ignoreCase: true, out var redaction))
            {
                Console.Error.WriteLine($"import: invalid --redaction value '{redactionStr}'. Valid: off, hash, masked, full");
                return 1;
            }

            var sanitizeIndex = Array.IndexOf(args, "--sanitize-sql-text");
            if (sanitizeIndex >= 0 && sanitizeIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("import: --sanitize-sql-text requires a value. Valid: raw, literals");
                return 1;
            }
            var sanitizeStr = Arg("--sanitize-sql-text", project.Config.SqlTextPolicy);
            // Validate the INPUT STRING against the defined enum names, not the parsed value:
            // Enum.TryParse("0", ...) succeeds and yields Raw, which IS a defined value, so
            // checking Enum.IsDefined on the parsed result alone lets numeric typos through.
            if (!Enum.GetNames<SqlTextSanitization>().Any(n => n.Equals(sanitizeStr, StringComparison.OrdinalIgnoreCase))
                || !Enum.TryParse<SqlTextSanitization>(sanitizeStr, ignoreCase: true, out var sqlText))
            {
                Console.Error.WriteLine($"import: invalid --sanitize-sql-text value '{sanitizeStr}'. Valid: raw, literals");
                return 1;
            }

            using var db = project.OpenDb();
            var options = new IngestionOptions(redaction, Array.Empty<FilterRule>(),
                PlanProfileDir: project.PlanProfileRunFolder(db.PeekNextRunId()),
                SqlText: sqlText);

            // Live in-place gauge on stderr (kept off stdout so the summary stays clean and
            // pipe-friendly). Synchronous IProgress so carriage-return updates stay ordered.
            var showGauge = !Console.IsErrorRedirected;
            var progress = new SyncProgress<ImportProgress>(p =>
            {
                if (showGauge)
                    Console.Error.Write("\r" + ImportProgressText.Render(p).PadRight(100));
            });

            IngestionResult result;
            try { result = ImportRunner.Run(db, options, path, progress); }
            catch (FileNotFoundException) { Console.Error.WriteLine($"import: path not found: {path}"); return 1; }

            if (showGauge) Console.Error.WriteLine();   // terminate the in-place line

            db.FinalizePlanRun(result.RunId);
            if (result.PlanProfiles > 0)
            {
                var digestRows = db.ReadPlanDigestRows(result.RunId);
                var planDir = project.PlanProfileRunFolder(result.RunId);
                var planWriter = new SqlFerret.Core.Plans.PlanArtifactWriter(planDir);
                planWriter.WriteDigests(digestRows);
                planWriter.WriteIndex(digestRows);
                Console.WriteLine($"plans: {result.PlanProfiles} profiles, {digestRows.Count} distinct -> {planDir}");
                if (sqlText != SqlTextSanitization.Raw)
                    Console.Error.WriteLine(
                        $"warning: statement-text sanitization policy '{sanitizeStr}' does not sanitize plan " +
                        $"artifacts; plan_profiles.statement_text in sqlferret.duckdb, and .sqlplan and " +
                        $".digest.json files under {planDir}, still carry unsanitized statement text. Use " +
                        $"obfuscate-plan before sharing them.");
            }

            Console.WriteLine(
                $"run {result.RunId}: read={result.Read} mapped={result.Mapped} " +
                $"unmapped={result.Unmapped} cleaned={result.Cleaned} tokenizeFailures={result.TokenizeFailures} " +
                $"blocking={result.Blocking} deadlocks={result.Deadlocks} blockingParseFailures={result.BlockingParseFailures} " +
                $"planProfiles={result.PlanProfiles} planParseFailures={result.PlanParseFailures} " +
                $"planWriteFailures={result.PlanWriteFailures} " +
                $"sqlTextSanitizeFailures={result.SqlTextSanitizeFailures} " +
                $"serverDiagnostics={result.ServerDiagnostics} " +
                $"serverDiagnosticsUnhandled={result.ServerDiagnosticsUnhandled} " +
                $"serverDiagnosticsParseFailures={result.ServerDiagnosticsParseFailures} " +
                $"embeddedBlocking={result.EmbeddedBlocking} " +
                $"embeddedBlockingFailures={result.EmbeddedBlockingFailures}");

            if (db.PlanCorrelationWarning(result.RunId) is { } planWarning)
                Console.Error.WriteLine(planWarning);

            return 0;
        }
    case "top-slow":
        {
            var project = OpenProject();
            if (project is null) return 1;
            var limit = int.TryParse(Arg("--limit", "20"), out var l) ? l : 20;
            using var db = project.OpenDb();
            var q = new WorkloadQueries(db.Connection);
            var rows = q.TopSlow(limit, "total_duration_us", Array.Empty<FilterRule>());
            foreach (var s in rows)
                Console.WriteLine(
                    $"{s.StatementKind,-7} {s.Count,8}  total={DisplayFormat.Duration(s.TotalDurationUs, project.Config.DurationUnit),-12}  {Trim(s.NormalizedSql)}");
            if (db.HasStaleClassification())
                Console.Error.WriteLine(StaleClassificationNote);
            return 0;
        }
    case "export-health":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var format = Arg("--format", "md");
            if (format is not ("json" or "md" or "both"))
            { Console.Error.WriteLine("export-health: --format must be json, md or both"); return 1; }

            var outPath = Arg("--out", "");
            if (outPath.Length > 0 && SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outPath))
            { Console.Error.WriteLine("export-health: invalid --out path"); return 1; }

            var limit = int.TryParse(Arg("--limit", "10"), out var lv) && lv > 0 ? lv : 10;

            using var db = project.OpenDb();
            var envelope = new SqlFerret.Core.Analysis.HealthDigest(db.Connection).Build(limit);

            var json = System.Text.Json.JsonSerializer.Serialize(envelope,
                           new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var md = SqlFerret.Cli.HealthDigestMarkdown.Render(envelope);

            // `both` ecrit deux fichiers quand --out est donne, et concatene sur stdout sinon —
            // meme comportement qu'export-blocking, dont cette commande copie la forme.
            if (outPath.Length == 0)
            {
                Console.WriteLine(format switch
                {
                    "json" => json,
                    "md" => md,
                    _ => md + Environment.NewLine + json,
                });
            }
            else if (format == "both")
            {
                var stem = Path.Combine(Path.GetDirectoryName(outPath) ?? "",
                                        Path.GetFileNameWithoutExtension(outPath));
                File.WriteAllText(stem + ".md", md);
                File.WriteAllText(stem + ".json", json);
                Console.WriteLine($"written: {stem}.md, {stem}.json");
            }
            else
            {
                File.WriteAllText(outPath, format == "json" ? json : md);
                Console.WriteLine($"written: {outPath}");
            }
            return 0;
        }
    case "export-blocking":
        {
            var project = OpenProject();
            if (project is null) return 1;
            var format = Arg("--format", "both");           // json | md | both
            var samples = int.TryParse(Arg("--samples", "5"), out var sv) ? sv : 5;
            var full = Array.IndexOf(args, "--full") >= 0;
            var outPath = Arg("--out", "");
            if (outPath.Length > 0 && SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outPath))
            { Console.Error.WriteLine("export-blocking: invalid --out path"); return 1; }

            using var db = project.OpenDb();

            if (full)
            {
                // NDJSON dump of every report (bounded by file, not context)
                var q = new BlockingQueries(db.Connection);
                using var w = outPath.Length > 0 ? new StreamWriter(outPath) : null;
                TextWriter o = w ?? Console.Out;
                foreach (var b in q.TopBlockers(int.MaxValue))
                    foreach (var rep in q.SampleReports(b.Fingerprint, int.MaxValue))
                        o.WriteLine(System.Text.Json.JsonSerializer.Serialize(rep));
                return 0;
            }

            var digest = new BlockingDigest(db.Connection).Build(samplesPerPattern: samples);
            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

            string json = System.Text.Json.JsonSerializer.Serialize(
                new BlockingDigestEnvelope(BlockingDigest.SchemaVersion, digest), jsonOpts);
            string md = SqlFerret.Cli.BlockingDigestMarkdown.Render(digest);

            string payload = format switch
            {
                "json" => json,
                "md" => md,
                _ => md + "\n\n```json\n" + json + "\n```\n"
            };
            if (outPath.Length > 0) File.WriteAllText(outPath, payload); else Console.WriteLine(payload);
            return 0;
        }
    case "query-store-import":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var connOverride = Arg("--conn", "");
            var connStr = connOverride.Length > 0 ? connOverride : project.Config.ConnectionString;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                Console.Error.WriteLine("query-store-import: no connection string (set server.connectionString in config/.env, or pass --conn)");
                return 1;
            }

            var database = Arg("--database", "");
            var noPlans = Array.IndexOf(args, "--no-plans") >= 0;

            QueryStoreWindow window;
            try
            {
                window = QueryStoreWindow.Parse(
                    NullIfEmpty(Arg("--from", "")), NullIfEmpty(Arg("--to", "")), NullIfEmpty(Arg("--last", "")),
                    DateTime.UtcNow);
            }
            catch (ArgumentException ex) { Console.Error.WriteLine($"query-store-import: {ex.Message}"); return 1; }

            if (!noPlans && !string.Equals(project.Config.RedactionPolicy, "off", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine(
                    $"warning: --plans writes raw showplan XML; .sqlplan files may contain literal values not " +
                    $"covered by redaction (policy={project.Config.RedactionPolicy}). Use --no-plans to skip.");

            using var db = project.OpenDb();
            var svc = new QueryStoreImportService(connStr!, db, project.PlansFolder);
            var opts = new QueryStoreImportOptions(NullIfEmpty(database), WritePlans: !noPlans, Window: window);

            var showGauge = !Console.IsErrorRedirected;
            var progress = new SyncProgress<string>(s => { if (showGauge) Console.Error.Write("\r" + s.PadRight(60)); });

            QdsImportResult result;
            try { result = svc.Import(opts, progress); }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException)
            {
                if (showGauge) Console.Error.WriteLine();
                Console.Error.WriteLine($"query-store-import: {ex.Message}");
                return 1;
            }
            if (showGauge) Console.Error.WriteLine();

            Console.WriteLine(
                $"qds run {result.RunId}: queries={result.QueriesCount} queryText={result.QueryTextCount} " +
                $"plans={result.PlansCount} runtimeRows={result.RuntimeStatRows} waitRows={result.WaitStatRows} " +
                $"plansWritten={result.PlanFilesWritten} planFailures={result.PlanWriteFailures}");
            return 0;
        }
    case "export-events":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var outDir = Arg("--out");
            if (string.IsNullOrWhiteSpace(outDir))
            { Console.Error.WriteLine("export-events: --out <dir> is required"); return 1; }
            if (SqlFerret.Cli.BlockingDigestMarkdown.HasTraversal(outDir))
            { Console.Error.WriteLine("export-events: invalid --out path"); return 1; }

            var kindStr = Arg("--kind", "both");
            if (!Enum.TryParse<EventKind>(kindStr, ignoreCase: true, out var kind))
            { Console.Error.WriteLine($"export-events: invalid --kind '{kindStr}'. Valid: blocking, deadlock, both"); return 1; }

            QueryStoreWindow window;
            try
            {
                window = QueryStoreWindow.Parse(
                    NullIfEmpty(Arg("--from", "")), NullIfEmpty(Arg("--to", "")), NullIfEmpty(Arg("--last", "")),
                    DateTime.UtcNow);
            }
            catch (ArgumentException ex) { Console.Error.WriteLine($"export-events: {ex.Message}"); return 1; }

            int? dbId = int.TryParse(Arg("--database", ""), out var dv) ? dv : null;
            var fingerprint = NullIfEmpty(Arg("--fingerprint", ""));
            var limit = int.TryParse(Arg("--limit", "100"), out var lv) ? lv : 100;
            if (limit <= 0)
            { Console.Error.WriteLine("export-events: --limit must be a positive integer"); return 1; }

            // --fingerprint/--database apply to blocking only; warn whenever the deadlock half is in scope.
            if (kind is EventKind.Deadlock or EventKind.Both && (dbId is not null || fingerprint is not null))
                Console.Error.WriteLine("export-events: --fingerprint/--database apply to blocking only; ignored for deadlock events");

            using var db = project.OpenDb();
            var svc = new EventExportService(db.Connection);
            var opts = new EventExportOptions(outDir, kind, window, fingerprint, dbId, limit);

            EventExportResult result;
            try { result = svc.Export(opts); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or DuckDB.NET.Data.DuckDBException)
            { Console.Error.WriteLine($"export-events: {ex.Message}"); return 1; }

            // Per-kind redaction hint: a requested kind that produced no files but skipped some was
            // imported with redaction != off. Reported per kind so an all-redacted half is not masked
            // by the other half having written files.
            if (result.BlockingWritten == 0 && result.BlockingSkipped > 0)
                Console.Error.WriteLine("export-events: no blocking XML written; those runs were imported with redaction != off. Re-import with --redaction off.");
            if (result.DeadlockWritten == 0 && result.DeadlockSkipped > 0)
                Console.Error.WriteLine("export-events: no deadlock XML written; those runs were imported with redaction != off. Re-import with --redaction off.");

            // Truncation hint: more events matched than were written because --limit capped the output.
            if (result.BlockingWritten < result.BlockingMatched)
                Console.Error.WriteLine($"export-events: {result.BlockingMatched - result.BlockingWritten} more blocking events matched but were capped by --limit {limit}");
            if (result.DeadlockWritten < result.DeadlockMatched)
                Console.Error.WriteLine($"export-events: {result.DeadlockMatched - result.DeadlockWritten} more deadlock events matched but were capped by --limit {limit}");

            var summary = new
            {
                outDir = result.OutDir,
                indexPath = result.IndexPath,
                blocking = new { written = result.BlockingWritten, skipped = result.BlockingSkipped, matched = result.BlockingMatched },
                deadlock = new { written = result.DeadlockWritten, skipped = result.DeadlockSkipped, matched = result.DeadlockMatched },
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary));
            return 0;
        }
    case "obfuscate-plan":
        {
            var inDir = Arg("--in-dir");
            if (!string.IsNullOrWhiteSpace(inDir))
            {
                var outDir = Arg("--out-dir");
                if (string.IsNullOrWhiteSpace(outDir))
                {
                    Console.Error.WriteLine("obfuscate-plan: --out-dir <dir> is required with --in-dir");
                    return 1;
                }
                if (!Directory.Exists(inDir))
                {
                    Console.Error.WriteLine($"obfuscate-plan: input folder not found: {inDir}");
                    return 1;
                }
                var mapArg = Arg("--map");
                var fr = SqlFerret.Core.Obfuscation.ObfuscationRunner.RunFolder(inDir, outDir,
                    string.IsNullOrWhiteSpace(mapArg) ? null : mapArg);
                foreach (var fail in fr.Failures) Console.Error.WriteLine($"  skipped {fail}");
                if (fr.FilesFound == 0)
                {
                    Console.Error.WriteLine($"obfuscate-plan: no .sqlplan files found under {inDir}");
                    return 1;
                }
                Console.WriteLine($"obfuscated {fr.FilesProcessed} plan(s) -> {outDir} (shared map: {fr.NamesMapped} names, {fr.MapPath}; {fr.FilesFailed} skipped)");
                return fr.FilesFailed > 0 ? 1 : 0;
            }

            var inPath = Arg("--in");
            if (!string.IsNullOrWhiteSpace(inPath))
            {
                var outPath = Arg("--out");
                if (string.IsNullOrWhiteSpace(outPath))
                {
                    Console.Error.WriteLine("obfuscate-plan: --out <file> is required with --in");
                    return 1;
                }
                if (!File.Exists(inPath))
                {
                    Console.Error.WriteLine($"obfuscate-plan: input not found: {inPath}");
                    return 1;
                }
                try
                {
                    var r = SqlFerret.Core.Obfuscation.ObfuscationRunner.RunStandalone(inPath, outPath);
                    Console.WriteLine($"obfuscated -> {r.AnonPath} ({r.NamesMapped} names, map: {r.MapPath})");
                    return 0;
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
                {
                    Console.Error.WriteLine($"obfuscate-plan: {ex.Message}");
                    return 1;
                }
            }

            var planId = Arg("--plan-id");
            if (string.IsNullOrWhiteSpace(planId)
                || planId.Contains('/') || planId.Contains('\\') || planId.Contains("..")
                || Path.GetFileName(planId) != planId)
            {
                Console.Error.WriteLine("obfuscate-plan: provide --in <file> --out <file>, or --project <dir> --plan-id <bare-id>");
                return 1;
            }
            var project = OpenProject();
            if (project is null) return 1;

            var srcPlan = Path.Combine(project.PlansFolder, $"{planId}.sqlplan");
            if (!File.Exists(srcPlan))
            {
                Console.Error.WriteLine($"obfuscate-plan: plan not found: {srcPlan}");
                return 1;
            }

            try
            {
                using var db = project.OpenDb();
                var r = SqlFerret.Core.Obfuscation.ObfuscationRunner.RunProject(db, project.PlansFolder, planId);
                Console.WriteLine($"obfuscated -> {r.AnonPath} (project map: {r.NamesMapped} names total, map: {r.MapPath})");
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
            {
                Console.Error.WriteLine($"obfuscate-plan: {ex.Message}");
                return 1;
            }
            return 0;
        }
    case "query":
        {
            var project = OpenProject();
            if (project is null) return 1;

            var inline = Arg("--sql");
            var file = Arg("--file");
            if (string.IsNullOrWhiteSpace(inline) == string.IsNullOrWhiteSpace(file))
            {
                Console.Error.WriteLine("query: fournir exactement l'un de --sql ou --file");
                return 1;
            }
            string sql;
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!File.Exists(file)) { Console.Error.WriteLine($"query: fichier introuvable: {file}"); return 1; }
                sql = File.ReadAllText(file);
            }
            else sql = inline;

            var format = Arg("--format", "table");
            if (format is not ("table" or "csv" or "json" or "md"))
            {
                Console.Error.WriteLine($"query: --format invalide '{format}'. Valeurs: table, csv, json, md");
                return 1;
            }
            // --limit non numerique valait "aucune limite", silencieusement : la meme faute de
            // frappe donnait tout le jeu de resultats avec un code retour 0. Le fichier valide
            // strictement partout ailleurs (--format ci-dessus, export-events --limit) ; meme
            // idiome ici.
            // On raisonne sur l'INDICE et non sur Arg(), qui rend "" aussi bien pour un drapeau
            // absent que pour un drapeau sans valeur : `query ... --limit` en fin de ligne
            // retombait donc sur la limite par defaut, sans un mot — le residu de la faute meme
            // que cette validation corrige.
            var noLimit = Array.IndexOf(args, "--no-limit") >= 0;
            var limitAt = Array.IndexOf(args, "--limit");
            int? limit;
            if (noLimit && limitAt >= 0)
            {
                Console.Error.WriteLine("query: --limit et --no-limit sont exclusifs");
                return 1;
            }
            if (noLimit) limit = null;
            else if (limitAt < 0) limit = AdHocQuery.DefaultLimit;
            else if (limitAt + 1 >= args.Length)
            {
                Console.Error.WriteLine("query: --limit attend une valeur : entier strictement positif (ou --no-limit)");
                return 1;
            }
            else if (!int.TryParse(args[limitAt + 1], out var lq) || lq <= 0)
            {
                Console.Error.WriteLine($"query: --limit invalide '{args[limitAt + 1]}' : entier strictement positif attendu (ou --no-limit)");
                return 1;
            }
            else limit = lq;
            var raw = Array.IndexOf(args, "--raw") >= 0;

            // Une seule formulation de la cause de troncature, pour le pied de page du tableau
            // comme pour stderr : le pied de page annoncait "--limit" alors que la troncature vient
            // desormais de la limite par defaut dans la plupart des cas.
            var truncationCause = limitAt < 0
                ? $"limite par defaut de {AdHocQuery.DefaultLimit} lignes ; --limit <n> ou --no-limit"
                : "--limit ; --no-limit pour tout rendre";

            // Tout est DANS le try, ouverture comprise : une base absente ou verrouillee doit
            // produire un message, pas une trace de pile — et le rendu aussi, qui manipule les
            // types rendus par DuckDB et peut donc echouer sur une valeur inattendue. Une seule
            // connexion sert la requete et le controle de version : la seconde, ouverte apres coup
            // pour HasStaleClassification, etait hors de cette protection.
            try
            {
                using var db = project.OpenDbReadOnly();
                var table = new AdHocQuery(db.Connection).Run(sql, limit);

                Console.WriteLine(ResultFormatter.Render(table, format, project.Config.DurationUnit, raw, truncationCause));
                // json seul indexe par nom de colonne (voir ResultFormatter.DuplicateColumnNames) :
                // un JOIN sans alias sur une colonne partagee ecraserait une valeur sans
                // avertissement.
                if (format == "json")
                {
                    var dupes = ResultFormatter.DuplicateColumnNames(table);
                    if (dupes.Count > 0)
                        Console.Error.WriteLine($"warning: colonnes dupliquees dans la sortie json, valeurs ecrasees: {string.Join(", ", dupes)}");
                }
                if (table.Truncated)
                    Console.Error.WriteLine($"warning: sortie tronquee a {table.Rows.Count} lignes ({truncationCause})");

                if (db.HasStaleClassification())
                    Console.Error.WriteLine(StaleClassificationNote);
            }
            catch (Exception ex) { Console.Error.WriteLine($"query: {ex.Message}"); return 1; }
            return 0;
        }
    case "reclassify":
        {
            var project = OpenProject();
            if (project is null) return 1;
            var force = Array.IndexOf(args, "--force") >= 0;

            using var db = project.OpenDb();
            var r = new SqlFerret.Core.Storage.Reclassifier(db).Run(force);

            Console.WriteLine(r.RowsExamined == 0
                ? $"reclassify: rien a faire (deja en v{r.ToVersion})"
                : $"reclassify: v{r.FromVersion} -> v{r.ToVersion} | examined={r.RowsExamined} changed={r.RowsChanged} unchanged={r.RowsUnchanged} unclassified={r.Unclassified} withoutSample={r.RowsWithoutSample} unusableSample={r.RowsUnusableSample}");

            if (r.RowsChanged > 0)
                Console.WriteLine("note: des lignes autrefois 'OTHER' portent desormais un statement_kind DDL");
            // Les deux cas ne se traitent pas pareil, donc ils ne se disent pas ensemble : --force
            // rejoue utilement les instructions non typees apres une evolution du classifieur,
            // mais ne peut rien pour les signatures dont aucun texte source n'est conserve.
            if (r.Unclassified > 0)
                Console.Error.WriteLine($"warning: {r.Unclassified} instruction(s) non typee(s) — texte non analysable ou construction sans visiteur; --force les rejouera apres une evolution du classifieur");
            if (r.RowsWithoutSample > 0)
                Console.Error.WriteLine($"warning: {r.RowsWithoutSample} signature(s) sans texte source conserve (ni execution ni inputbuf de blocage) — leur classification est laissee telle quelle; --force n'y changera rien, seul un reimport le pourrait");
            if (r.RowsUnusableSample > 0)
                Console.Error.WriteLine($"warning: {r.RowsUnusableSample} signature(s) dont le seul texte conserve est deja normalise (import en --sanitize-sql-text literals, ou inputbuf de blocage hors --redaction off) — non reclassees pour ne pas les degrader en OTHER; seul un reimport depuis la capture le permettrait");
            return 0;
        }
    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static string Trim(string s) => s.Length <= 80 ? s : s[..77] + "...";

static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

file sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
