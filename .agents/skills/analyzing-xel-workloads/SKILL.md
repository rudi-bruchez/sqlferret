---
name: analyzing-xel-workloads
description: >
  Analyser une trace SQL Server Extended Events avec SQLFerret sans reproduire les erreurs
  classiques : filtres validés à l'envers, motifs de texte sensibles à l'espace, préfixes de
  schéma, listes d'entités en dur, niveaux de capture oubliés, colonnes en microsecondes lues
  comme des millisecondes.
  Charger ce skill avant toute analyse d'un fichier .xel ou d'un projet SQLFerret, ou dès qu'il est
  question de "trace", "Extended Events", ".xel", "workload", "audit d'upgrade", "statement_kind",
  "blocking", "deadlock", "plan d'exécution", "Query Store", ou d'interroger un fichier .duckdb de
  projet. Couvre aussi la validation d'une conclusion d'analyse et la manière de commander une
  relecture utile.
---

# Analyser une trace .xel sans se tromper

## L'outil en deux lignes

SQLFerret transforme des captures Extended Events `.xel` et des instantanés Query Store en **un
répertoire de projet DuckDB interrogeable**. Répondre d'abord par les **exports bornés** ; ne
descendre au SQL sur le fichier DuckDB que lorsqu'ils ne suffisent pas.

Il n'y a **pas de `--help`** : lancé sans argument, l'outil imprime sa ligne d'usage.
`docs/cli-reference.md` est la référence des commandes, `docs/data-model.md` celle du schéma.

## Invocation

`sqlferret` ci-dessous est un raccourci — le dépôt n'installe aucun binaire sur le `PATH` :

```bash
dotnet run --project <sqlferret>/src/SqlFerret.Cli -- <commande> ...
```

Le binaire compilé se trouve sinon dans `src/SqlFerret.Cli/bin/Debug/net10.0/SqlFerret.Cli.exe`.

## Déroulé type

1. **Ingérer** — `sqlferret import <fichier-ou-dossier> --project <dir>`
   `--project` est un **répertoire**, créé à la première utilisation. Réimporter ajoute un run ;
   rien n'est remplacé. Lire la ligne de résumé sur stdout : `unmapped`, `cleaned`,
   `tokenizeFailures`, `*ParseFailures` disent ce qui manquera à l'analyse.
   Passer `--redaction off` quand l'analyse a besoin du texte SQL réel et des valeurs de paramètres.
2. **Survoler** — `sqlferret top-slow --project <dir> --limit 20`
3. **Blocages** — `sqlferret export-blocking --project <dir> --format md`
   Borné à dessein. `--full` (NDJSON non borné) seulement vers un fichier.
4. **Creuser** — `sqlferret query` sur le projet (voir règle 9), pour tout ce que les exports ne
   couvrent pas.

---

# Règles

Ces règles viennent d'analyses réelles. Chacune est adossée à l'erreur qui l'a produite.

Les règles 1 et 10 encadrent toutes les autres : **valider contre la source, jamais contre ce qu'on
attend ni contre un avis.**

## 1. Valider un filtre par ce qu'il exclut, jamais par ce qu'il retient

C'est la règle la plus importante. Inspecter l'ensemble retenu ne révèle rien : ce qui manque n'y
est pas. Il faut classer **toutes** les instructions, puis mesurer l'ensemble écarté.

```sql
-- Combien de temps le filtre laisse-t-il de côté, et sur quels types ?
SELECT CASE WHEN <condition du filtre> THEN 'RETENU' ELSE 'EXCLU' END AS statut,
       <categorie>, count(*) n,
       round(sum(duration_us)/1e6, 2) tot_s, round(max(duration_us)/1e6, 2) max_s
FROM executions
GROUP BY 1, 2 ORDER BY statut, tot_s DESC
```

Un filtre est acceptable quand l'ensemble exclu est négligeable **et qu'on l'a mesuré**. Sur une
analyse réelle, cette méthode a révélé trois défauts en deux passes ; l'inspection de l'ensemble
retenu n'en avait révélé aucun.

## 2. Le T-SQL est indifférent à la nature de l'espace

`LIKE 'ALTER TABLE% ADD %'` exige un espace littéral et rate `…MA_TABLE<TAB>ADD MACOLONNE`. Sur une
analyse réelle, ce motif a retenu trois exécutions d'une instruction et raté la quatrième — la plus
coûteuse.

Utiliser `regexp_matches(u, '\sADD\s')`, jamais un `LIKE` avec espace.

## 3. Les noms qualifiés ont un préfixe optionnel

`ALTER\s+TABLE\s+\[?[A-Z]*\]?\.?\[?([A-Z0-9_]+)\]?` extrait `_TABLE` de `[MA_TABLE]` : faute
de point, `[A-Z]*` consomme `MA`. Le point doit faire partie du groupe optionnel :

```
(?:\[?ident\]?\.)?\[?(ident)\]?
```

## 4. Jamais de liste d'entités codée en dur

Énumérer les tables intéressantes rend invisibles celles qu'on n'a pas prévues, quel que soit leur
coût. Extraire le nom depuis l'instruction.

## 5. Trois niveaux de capture

| Niveau | Événement | Ce qu'on y voit |
|---|---|---|
| Instruction du script | `sql_statement_completed` | ce qui est écrit dans le script |
| Via `sp_executesql` | `sp_statement_completed` | le DDL exécuté dynamiquement, **invisible au niveau 1** |
| Généré par le moteur | `sp_statement_completed` | `insert [T] select * from [T] option (maxdop 1)` pour une construction d'index, `UPDATE [T] SET [c] = DEFAULT` pour un `ADD NOT NULL DEFAULT` |

Filtrer sur le seul `sql_statement_completed` perd les deux autres. Le troisième niveau est
précieux : le `maxdop 1` d'une construction d'index prouve qu'elle est mono-thread, et
`SET [c] = DEFAULT` prouve qu'un ajout de colonne réécrit bien toutes les lignes.

## 6. Une instruction à 0,00 s est une preuve, pas du bruit

Elle établit qu'une opération est purement métadonnées, ou qu'elle n'a rien fait. Filtrer sur la
durée détruit exactement la démonstration qu'on cherche.

## 7. `row_count` sur du DDL compte des structures, pas des lignes

Un `DROP CONSTRAINT` sur une table de 41 416 569 lignes rapportant 165 666 276 lignes — soit
exactement 4× — démontre que le tas **et trois index non-clusters** ont été réécrits. La
divisibilité exacte par la taille de la table est une technique de démonstration.

## 8. Préférer la classification de l'outil aux regex

Depuis le normaliseur v2, `normalized_queries` porte `statement_kind`, `primary_table` et
`target_object`, y compris pour le DDL. Les regex ne servent plus que pour les marqueurs métier —
par exemple un commentaire de tête identifiant le fichier source d'un batch.

Si un projet a été importé avant : `sqlferret reclassify --project <dir>`.

**Piège d'agrégation** : `primary_table` conserve le nom **tel qu'écrit**. Une même table écrite
`dbo.T` puis `T` produit deux valeurs distinctes, et un `GROUP BY primary_table` l'éclate en deux
lignes. Vérifier avant d'agréger.

## 9. Interroger le projet avec `sqlferret query`

```bash
sqlferret query --project <dir> --sql "SELECT ..." --format md
```

Ne pas fabriquer de harnais chargeant `DuckDB.NET.Data.dll` à la main.

Trois détails qui piègent :

1. Le formatage des durées suit le **nom de la colonne de sortie** : un alias sans suffixe `_us`
   restitue des microsecondes brutes (`AS total_us` pour le garder), `--raw` ne l'a jamais. Le
   suffixe convertit toute valeur numérique, quel que soit son type SQL — `sum()` rend un HUGEINT
   et `avg()` un DOUBLE — donc deux colonnes `_us` d'une même ligne sont dans la même unité.
2. Une **limite de 1000 lignes s'applique par défaut** ; `--limit <n>` la change, `--no-limit` la
   lève. La troncature s'annonce sur `stderr`, qu'il ne faut donc pas jeter dans un tube.
3. `csv` et `json` sont **fidèles** (sauts de ligne et types conservés) ; `table` et `md` sont des
   formats de **présentation** où les sauts de ligne d'un `sql_text_raw` sortent en `\n`, pour ne
   pas rompre la grille ni le document. Du SQL destiné à être relu ou rejoué se sort en `csv` ou
   en `json`, pas en `md`.

## 10. Valider une conclusion contre la source, jamais contre un avis

C'est la règle 1 étendue au-delà des filtres. Une conclusion d'analyse — « c'est cette étape qui
coûte », « cet index est reconstruit deux fois » — se valide en retournant à la trace, pas en
demandant à quelqu'un si le raisonnement paraît juste.

Sur une analyse réelle, les trouvailles les plus lourdes ne sont venues ni d'une relecture par
l'auteur, ni d'une relecture externe, mais de deux confrontations aux faits : **lire le code
réellement exécuté** plutôt que le supposer, et **appliquer l'audit inverse** de la règle 1. Une
relecture, quelle qu'en soit la source, ne voit que ce que le document affirme.

**Corollaire, quand on fait relire par un tiers — humain ou modèle.**

- Demander des **défauts**, pas un verdict. « Trouve trois choses qui casseront à l'exécution »,
  « vérifie chaque nom de type contre la section précédente », plutôt que « relis ceci ». À qui l'on
  demande un jugement rend un jugement ; à qui l'on demande des défauts cherche des défauts.
- **Une revue sans trouvaille n'est pas une validation, c'est une absence de donnée.** Le plus
  souvent elle signale que l'artefact dépassait ce que le relecteur pouvait réellement vérifier —
  l'éloge est une réponse au volume, pas à la qualité. La bonne réaction est de redemander avec une
  consigne plus étroite, sur une partie plus petite, et non de considérer le point comme acquis.
- **Découper ce qu'on soumet.** Un document de deux mille lignes ne se relit pas ; trois sections de
  deux cents lignes, si.
- Vérifier les affirmations de la revue elle-même. Une revue peut halluciner un détail — une
  technologie absente du projet, une garantie de sécurité surestimée. Ce qu'elle avance se vérifie
  comme le reste.

---

# Pièges de lecture des données

Ceux-ci ne relèvent pas de la méthode mais du schéma : ils produisent des réponses fausses sans
rien signaler.

| Piège | Quoi faire |
|---|---|
| Les durées paraissent absurdement grandes | Toute colonne `*_us` est en **microsecondes**. `/1e3` pour des ms, `/1e6` pour des s. Ne jamais supposer des ms. |
| `export-events` annonce `skipped` | Le XML de blocage/deadlock n'est conservé que pour les runs importés avec `--redaction off`. Avec toute autre politique il n'a jamais été écrit. Réimporter la capture. |
| Une jointure sur `query_hash` ne rend rien | Trois formats texte différents : `executions.query_hash` en décimal, `plan_profiles.query_hash` en hexa nu, `qds_queries.query_hash` en hexa préfixé `0x`. Normaliser en hexa majuscule nu avant de comparer. |
| Les plans ne se corrèlent pas aux exécutions | La capture a omis l'ACTION `sqlserver.query_hash`. Aucun code ne corrige cela — il faut une nouvelle capture. `import` avertit sur stderr le cas échéant. |
| Grouper les plans par texte d'instruction | `plan_profiles.statement_text` est **tronqué par le moteur** et peut être quasi vide. Jamais comme clé : utiliser `plan_hash`. |
| `top-slow` ne trie pas comme voulu | Son tri (durée totale) n'est pas configurable en ligne de commande. Passer par `sqlferret query` pour un classement p95/max/avg. |
| Comparer des empreintes entre projets | Valable seulement si `normalizer_version` concorde (`ingestion_runs`, `normalized_queries`). |
| `database_name` / `login_name` / `client_app_name` vides | Ces ACTIONs n'étaient pas dans la session. Absence de donnée, pas absence d'activité. |

---

# Recettes

## Premier coup d'œil : formes les plus coûteuses, avec percentiles

```sql
SELECT n.statement_kind, n.primary_table, count(*) AS execs,
       sum(e.duration_us) / 1e6                 AS total_s,
       quantile_cont(e.duration_us, 0.95) / 1e3 AS p95_ms,
       n.normalized_sql
FROM executions e JOIN normalized_queries n USING (normalized_hash)
GROUP BY ALL ORDER BY total_s DESC LIMIT 20;
```

`docs/data-model.md#useful-queries` contient des requêtes prêtes à l'emploi pour le parameter
sniffing, l'attribution des blocages, les findings de plan et les contrôles de qualité
d'ingestion. Les lire avant d'inventer du SQL.

## Répartition CPU / attente

Quand `cpu_time_us` ≈ `duration_us`, l'instruction calcule : un disque plus rapide n'y changera
rien. Quand `cpu_time_us` ≪ `duration_us`, elle attend.

```sql
SELECT CASE WHEN cpu_time_us * 1.0 / nullif(duration_us, 0) >= 0.90 THEN 'CPU-bound'
            WHEN cpu_time_us * 1.0 / nullif(duration_us, 0) >= 0.60 THEN 'mixte'
            ELSE 'en attente' END AS profil,
       count(*) n,
       round(sum(duration_us) / 6e7, 1) dur_min,
       round(sum(duration_us - cpu_time_us) / 6e7, 1) attente_min
FROM executions
WHERE event_name = 'sql_statement_completed' AND duration_us > 5e6
GROUP BY 1 ORDER BY dur_min DESC
```

`cpu_time_us > duration_us` signifie exécution parallèle.

## Écoulé contre actif : segmenter sur les silences

La durée annoncée d'un traitement est rarement la durée de la fenêtre d'arrêt.

```sql
WITH b AS (
  SELECT captured_at AS t_end,
         captured_at - INTERVAL (duration_us) MICROSECOND AS t_start
  FROM executions WHERE event_name = 'sql_batch_completed'
),
g AS (SELECT t_start, t_end, lag(t_end) OVER (ORDER BY t_start) AS prev_end FROM b)
SELECT prev_end AS silence_de, t_start AS silence_a,
       date_diff('second', prev_end, t_start) AS silence_s
FROM g WHERE date_diff('second', prev_end, t_start) > 60
ORDER BY t_start
```

## Travail redondant

```sql
SELECT target_object, primary_table, count(*) constructions,
       round(sum(duration_us) / 1e6, 1) total_s,
       round((sum(duration_us) - max(duration_us)) / 1e6, 1) redondant_s
FROM executions e
JOIN normalized_queries n USING (normalized_hash)
WHERE n.statement_kind LIKE 'CREATE%INDEX' AND e.row_count > 1000000
GROUP BY 1, 2 HAVING count(*) > 1
ORDER BY redondant_s DESC
```

## Registre des réécritures d'une table

```sql
SELECT captured_at, n.statement_kind, n.target_object,
       e.row_count,
       round(e.row_count * 1.0 / <lignes_de_la_table>, 1) AS multiplicateur,
       round(e.duration_us / 1e6, 1) dur_s
FROM executions e
JOIN normalized_queries n USING (normalized_hash)
WHERE n.primary_table = '<table>' AND e.row_count > 0
ORDER BY captured_at
```

Un multiplicateur entier supérieur à 1 révèle des structures reconstruites implicitement.

---

# Avant de faire sortir quoi que ce soit du projet

`executions.sql_text_raw` n'est **jamais expurgé**, et les fichiers `.sqlplan` portent le schéma et
parfois des valeurs littérales. Avant qu'un plan quitte le projet, passer
`sqlferret obfuscate-plan --project <dir> --plan-id <id>`, et garder le `*.map.json` / la table
`obfuscation_map` en arrière : cette carte **est** la clé de désanonymisation.

Ne pas coller de SQL capturé brut, d'input buffer ni de plan non obfusqué dans quoi que ce soit
d'externe sans que l'utilisateur l'ait explicitement demandé.
