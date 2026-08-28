# Capture session — événements et actions requis

## Événements

| Événement | Rôle |
|---|---|
| `sqlserver.rpc_completed` | Exécutions de procédures et de `sp_executesql` |
| `sqlserver.sql_batch_completed` | Exécutions de batches ad hoc |
| `sqlserver.query_post_execution_plan_profile` | Plan **réel** avec compteurs d'exécution |

## Actions

`query_post_execution_plan_profile` s'auto-identifie : `QueryHash` et `QueryPlanHash` sont des
attributs de `<StmtSimple>` **dans le XML du plan**. Aucune action n'est requise sur cet événement
pour une corrélation au niveau de la forme de requête.

Ce qui manque est du côté des événements de complétion :

| Action | À poser sur | Ce qu'elle permet |
|---|---|---|
| `sqlserver.query_hash` | `rpc_completed`, `sql_batch_completed` | Jointure `executions.query_hash` ↔ `plan_profiles.query_hash` |
| `sqlserver.query_plan_hash` | `rpc_completed`, `sql_batch_completed` | Jointure sur la forme de plan |
| `package0.attach_activity_id` | **tous** les événements | GUID + n° de séquence partagés au sein d'un même batch : corrélation par exécution, et non par forme de requête |
| `sqlserver.session_id` | tous | Filet de secours, combiné à l'ordre des événements |

**N'utilisez jamais `<StmtSimple StatementText>` comme clé.** Il est tronqué, et peut être vide :
sur nos plans de référence il vaut 1 518 caractères dans un cas et **2 caractères** dans l'autre
pour une instruction `SELECT` complète.

## Session prête à coller

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

## Corrélation manuelle, à défaut d'actions

Les digests et `index.json` publient `captured_at_utc` en ISO 8601 UTC, précision microseconde.

**SSMS affiche les événements étendus en heure locale par défaut.** Cocher « Display values in UTC »
dans la visionneuse, sinon la corrélation manuelle échoue d'un décalage horaire entier.

## Observé sur la trace de référence (trace_0.xel, 2026-08-04)

Sonde XELite jetable (`tools/probe/`, supprimée après usage, jamais committée) exécutée sur
`trace_0.xel` (39,4 Mo) avec `Microsoft.SqlServer.XEvent.XELite` 2024.2.5.1 / .NET 10.

**1. Champ portant le XML du plan.** `showplan_xml` est confirmé — présent tel quel dans
`IXeEventData.Fields` de `query_post_execution_plan_profile`. L'hypothèse de la spec
(`ev.Fields["showplan_xml"]`) est donc correcte, aucun changement requis pour
`EventMapper.ExtractShowplanXml` (tâche 1).

Liste complète des champs (`Fields.Keys`) de `query_post_execution_plan_profile` :

    cpu_time, database_name, dop, duration, estimated_cost, estimated_rows,
    granted_memory_kb, ideal_memory_kb, nest_level, object_id, object_name,
    object_type, requested_memory_kb, serial_ideal_memory_kb, showplan_xml,
    source_database_id, used_memory_kb

**2. `duration` et `cpu_time`.** Les deux sont **présents** sur `query_post_execution_plan_profile`,
confirmant le relevé de métadonnées qui fondait la spec. Le typage `long?` du writer reste
néanmoins la bonne décision : ce constat vaut pour `trace_0.xel`, pas comme garantie du moteur pour
toute capture.

**3. Action d'activité.** **Ni `activity_id` ni `attach_activity_id` ne sont présents** sur les
`Actions` réellement observées, sur aucun des trois types d'événements de cette trace. Les actions
effectivement capturées sont :

- `query_post_execution_plan_profile` : `client_app_name`, `client_hostname`, `sql_text`,
  `username` (l'action `sql_text` sur cet événement n'était pas anticipée par la spec — bonus,
  non requis par le contrat V1) ;
- `rpc_completed` / `sql_batch_completed` : `client_app_name`, `client_hostname`, `username`.

**Cette liste corrige la section « Contexte mesuré » de la spec**, qui listait `activity_id` et
`database_name` comme actions « présentes » sur la foi d'une extraction de chaînes UTF-16 dans le
bloc de métadonnées — une méthode qui trouve des noms **déclarés** quelque part dans le binaire, pas
des actions effectivement posées sur un événement. La lecture XELite en direct (`IXEvent.Actions`)
fait foi : `activity_id` et `database_name` sont absents en pratique sur `trace_0.xel`. Ceci renforce
la conclusion déjà actée : **aucune corrélation par activité n'est possible sur cette trace**, V1
reste donc la seule voie exploitable ici.

**4. `xe.Timestamp.Kind`.** Le squelette de sonde du brief suppose `IXEvent.Timestamp` de type
`DateTime` et lit `.Kind` — **ça ne compile pas** : `IXEvent.Timestamp` est en réalité un
`DateTimeOffset` (confirmé aussi par `XelReader.cs`, qui appelle déjà `.UtcDateTime`). La sonde a été
adaptée pour lire `Offset` et `UtcDateTime.Kind` à la place. Sur `trace_0.xel`, les trois types
d'événements affichent `Offset=00:00:00` et `UtcDateTime.Kind=Utc` — XELite restitue donc des
horodatages déjà exprimés en UTC (offset nul), pas en heure locale. Exemple mesuré :

    TIMESTAMP: 2026-08-04T14:20:11.9144860+00:00  Offset=00:00:00
    UtcDateTime=2026-08-04T14:20:11.9144860Z  Kind=Utc

Le contrat de corrélation manuelle en UTC tient. Le chemin `IXeEventData.Timestamp` (déjà `DateTime`
via `.UtcDateTime` dans `XelReader.cs`) reste la bonne API côté Core ; le repli de conversion
mentionné en tâche 3 pour un `Kind=Local` reste du code mort sur cette trace, mais reste une
précaution raisonnable pour d'autres captures.

**5. Volumétrie.** 346 événements de plan, **62 plans distincts** (`QueryPlanHash` extrait du XML
par regex, à des fins de sonde uniquement — le parseur définitif utilisera `XDocument`). Très en
deçà du seuil de ~2 000 : aucun plafonnement de `PlanArtifactWriter` n'est nécessaire pour cette
trace.

## Bout en bout : run réel sur `trace_0.xel` (tâche 12)

Ce qui précède vient d'une sonde XELite jetable, avant écriture du parseur définitif. Ce qui suit
vient de la commande `import` réelle, exécutée deux fois de bout en bout sur `trace_0.xel` avec
l'implémentation V1 complète (parseur, writer, schéma DuckDB, passe finale, digests, `index.json`).
Les deux mesures sont complémentaires : la première valide les hypothèses de conception, celle-ci
valide le pipeline livré.

- **897 événements lus** dans `trace_0.xel` (`rpc_completed` + `sql_batch_completed` +
  `query_post_execution_plan_profile`).
- **346 événements de plan ingérés, 62 plans distincts** — confirme exactement la volumétrie
  mesurée par la sonde ci-dessus, cette fois via le parseur `XDocument` définitif plutôt qu'un
  regex de sonde.
- **85 fichiers `.sqlplan` écrits** : 62 premières occurrences (`p_*.sqlplan`, `m_*.sqlplan` ou
  `c_*.sqlplan` selon le cas) plus 23 fichiers `.worst.sqlplan` — les plans dont une exécution
  ultérieure a dépassé strictement la durée de la première occurrence. 85 dépasse 62 précisément
  parce que ces 23 plans ont deux fichiers chacun.
- **62 `*.digest.json`** (un par plan distinct) et **1 `index.json`**, listant les 62 plans.
- L'**avertissement de corrélation s'est déclenché** : la trace ne porte aucune action
  `sqlserver.query_hash` sur les événements de complétion, donc aucune ligne `executions` du run
  n'a de `query_hash` non nul. Message observé sur `stderr` :

  ```
  warning: 346 plan profiles ingested, but no execution carries query_hash.
           Plans cannot be correlated with queries.
           Add ACTION(sqlserver.query_hash) to rpc_completed / sql_batch_completed.
           See docs/capture-session.md
  ```

  Confirme le scénario documenté plus haut : sans capturer `sqlserver.query_hash` (voir la session
  prête à coller ci-dessus), la seule voie de corrélation qui reste est l'horodatage manuel.
