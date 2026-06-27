# Anonymisation de plans d'exécution (.sqlplan)

Spec de conception. Statut : approuvé en brainstorming, prêt pour plan d'implémentation.

## Objectif

Prendre en entrée un plan d'exécution SQL Server (showplan XML, fichier `.sqlplan`) et produire une copie anonymisée, partageable sans fuite (forum, consultant), tout en restant ouvrable dans SSMS. Les noms d'objets (bases, schémas, tables, colonnes, index, statistiques, paramètres, alias) sont remplacés par des jetons lisibles déterministes ; les littéraux et valeurs de paramètres sont neutralisés. Une table de correspondance locale (`map.json`) permet la relecture et la dé-anonymisation côté utilisateur, sans jamais être incluse dans le plan partagé.

## Périmètre

Anonymisation complète :

1. Noms d'objets : `<Object Database Schema Table Index Statistics Alias>`.
2. Noms de colonnes : `<ColumnReference Column>`, `<OutputList>`, `<DefinedValues>`, `<MissingIndexes>`.
3. Texte SQL embarqué : `<StmtSimple StatementText>` réécrit finement (identifiants remappés, littéraux passés en `?`).
4. Valeurs de paramètres : `ParameterCompiledValue`, `ParameterRuntimeValue` neutralisées.
5. Prédicats : `<ScalarOperator ScalarString>` réécrits comme du texte SQL.

Hors périmètre v1 : voir Limites connues.

## Contraintes

- Le plan anonymisé reste ouvrable dans SSMS. SSMS rend le XML graphiquement et ne recroise pas `StatementText` avec l'arbre d'opérateurs, donc un renommage cohérent suffit.
- Transformation structurelle via `XDocument`. Pas de regex sur le XML brut.
- Cœur headless sans I/O, conforme à l'architecture KISS du projet (spec §2). Pas d'interface `IXxx`, pas de DI, types `record`/POCO, services à constructeur primaire, classes utilitaires statiques.
- Microsecondes : sans objet ici (pas de durées manipulées), mais aucune conversion d'unité n'est introduite.

## Architecture

Nouveau namespace `SqlFerret.Core.Obfuscation`, trois unités.

### PlanObfuscator (classe utilitaire statique)

Orchestre la transformation, pure et sans I/O.

Signature de référence :

```csharp
public static (string anonXml, ObfuscationMap map) Obfuscate(string showplanXml, ObfuscationMap map);
```

Étapes :

1. Charge le XML en `XDocument`.
2. Collecte les noms depuis les nœuds structurés : `<Object>` (Database, Schema, Table, Index, Statistics, Alias), `<ColumnReference>` (Database, Schema, Table, Column, Alias), `<ParameterList>` / `<ColumnReference Column>` côté paramètres.
3. Pour chaque nom non whitelisté, demande son jeton à la `map` (création si absent, réutilisation sinon).
4. Réécrit les attributs des nœuds avec les jetons.
5. Délègue à `StatementTextRewriter` la réécriture de chaque `StatementText` et de chaque `ScalarString`.
6. Neutralise `ParameterCompiledValue` / `ParameterRuntimeValue` en `?`.
7. Retourne le XML sérialisé et la map enrichie.

La map passée en entrée peut être vide (mode standalone, premier plan) ou pré-remplie (mode projet, cohérence inter-plans). `Obfuscate` ne fait que la lire et l'enrichir ; la persistance est la responsabilité de l'hôte.

### ObfuscationMap (POCO sérialisable)

Tient les correspondances par nature, dans des dictionnaires distincts :

```
databases, schemas, tables, columns, indexes, statistics, parameters, aliases
```

Règles :

- Attribution du prochain jeton libre par nature, dans l'ordre de rencontre, de façon stable : `Db1`, `Schema1`, `Table1`, `Col1`, `Idx1`, `Stat1`, `Param1`, `Alias1`, puis 2, 3, etc.
- Clé de correspondance insensible à la casse pour les identifiants SQL ; les crochets `[]` sont retirés avant mappage.
- (Dé)sérialisation JSON pour `map.json`. Le format expose, par nature, `original -> token` ; la dé-anonymisation est l'inverse `token -> original`.

### StatementTextRewriter (classe utilitaire statique)

Réécrit un fragment T-SQL en réutilisant ScriptDom (`TSql160Parser`, token stream), dans l'esprit de `TokenNormalizer` mais en variante non destructive : on conserve espaces et casse pour la lisibilité.

Signature de référence :

```csharp
public static string Rewrite(string sqlFragment, ObfuscationMap map);
```

Règles :

- Pour chaque token `Identifier` / `QuotedIdentifier` dont la valeur (crochets retirés, casse ignorée) figure dans la map, substituer le jeton.
- Pour chaque token littéral (`Integer`, `Numeric`, `Money`, `Real`, `HexLiteral`, `AsciiStringLiteral`, `UnicodeStringLiteral`), substituer `?`.
- Mots-clés et fonctions intégrées : laissés tels quels (absents de la map, non littéraux).
- Fallback de sûreté : si `Parse` renvoie des erreurs ou si une exception survient, remplacement par chaîne des noms connus (du plus long au plus court pour éviter les sous-chaînes) plus suppression des littéraux par motif. Garantit qu'aucun nom mappé ni littéral d'origine ne survit, au prix d'une réécriture moins fine.

## Hôtes CLI

Cœur unique, deux points d'entrée.

### Mode projet

```
obfuscate-plan --project wl.duckdb --plan-id abc
```

- Lit `plans/abc.sqlplan`.
- Charge la map projet depuis la table DuckDB `obfuscation_map`.
- Appelle `PlanObfuscator.Obfuscate`.
- Écrit `plans/abc.anon.sqlplan` et exporte `plans/abc.map.json`.
- `INSERT` les seules nouvelles entrées dans `obfuscation_map` (les existantes sont réutilisées telles quelles, d'où la cohérence inter-plans).
- Validation `planId` : composant de nom de fichier nu, pas de séparateur, pas de `..` (même garde que `EstimatedPlanService.Save`).

### Mode standalone

```
obfuscate-plan --in foo.sqlplan --out foo.anon.sqlplan
```

- Aucun projet requis, aucune dépendance DuckDB.
- Map initialisée vide en mémoire.
- Écrit le `.anon.sqlplan` et un `foo.map.json` à côté de `--out`.

## Stockage de la map projet

Table DuckDB dans le fichier projet :

```sql
CREATE TABLE IF NOT EXISTS obfuscation_map (
    kind          VARCHAR NOT NULL,   -- database|schema|table|column|index|statistics|parameter|alias
    original_name VARCHAR NOT NULL,
    token         VARCHAR NOT NULL,
    PRIMARY KEY (kind, original_name)
);
```

- Migration idempotente (`CREATE TABLE IF NOT EXISTS`), sur le modèle de la migration `blocking_reports.raw_xml`.
- Source de vérité canonique au niveau projet. Le `map.json` est un export volontaire par-run pour partage et relecture, jamais le stock.
- Le chargement reconstruit un `ObfuscationMap` ; l'écriture n'insère que les paires `(kind, original_name)` absentes.

## Données neutralisées, détail

Couverture élargie à l'implémentation (la revue finale a montré que l'énumération par élément était fragile selon la version du schéma showplan ; `RenameNode` est désormais appliqué à chaque élément, le mappage est piloté par nom d'attribut, et la whitelist reste évaluée par élément).

- Noms (mappés en jetons) : `Database`, `Schema`, `Table`, `Server`, `Index`, `Statistics`, `Alias`, `Column` (préfixe `@` -> paramètre), `Name` (uniquement sur `<Column>` des MissingIndexes), `RemoteObject`, `RemoteSource`, `CursorName`, `PlanGuideName`, `PlanGuideDB`, `TemplatePlanGuideName`, `TemplatePlanGuideDB`, `Assembly`, `Method`, `UDXName`, et les noms multi-parties `ProcName` / `FunctionName` (découpés par segment, jetons partagés avec les références d'objets).
- Texte T-SQL réécrit via `StatementTextRewriter` : `StatementText`, `ScalarString`, `RemoteQuery`, `ParameterizedText`, `Expression`.
- Valeurs littérales -> `?` : `ConstValue`, `ParameterCompiledValue`, `ParameterRuntimeValue`.
- Préservés (non sensibles) : coûts, compteurs, opérateurs (`PhysicalOp`/`LogicalOp`), `DataType`, `StatementType`, hachages (`QueryHash`/`QueryPlanHash`), `Build`/`Version`, énumérations.

## Whitelist objets système

Non mappés, laissés intacts pour garder le plan lisible et fidèle :

- Schémas `sys` et `INFORMATION_SCHEMA`.
- Base `tempdb`.
- Objets internes : `Worktable`, `Workfile` (et leurs colonnes).
- Fonctions intégrées et mots-clés : naturellement préservés, car absents des nœuds structurés et non littéraux.

Quand un `<Object>` appartient à la whitelist (schéma système, base `tempdb`, table interne), ni l'objet ni ses colonnes ne sont ajoutés à la map.

## Invariant de sûreté

Aucune chaîne d'origine sensible (nom mappé ou valeur littérale) ne doit apparaître dans la sortie. C'est l'invariant central, vérifié par assertion d'absence dans les tests. Le fallback du `StatementTextRewriter` existe précisément pour ne jamais relâcher l'original en cas d'échec de parse.

## Tests (TDD, fixtures golden)

- Fixtures `.sqlplan` minimales écrites à la main, sortie attendue figée.
- Absence : aucun nom d'origine ni littéral d'origine dans la sortie.
- Cohérence inter-plans : deux plans partageant `Customers` reçoivent le même jeton via la map projet.
- Idempotence : ré-obfusquer une sortie déjà anonymisée est stable.
- Round-trip `map.json` : (dé)sérialisation fidèle, dé-anonymisation correcte.
- Whitelist : `sys.indexes` et un `Worktable` restent intacts.
- Fallback : un `StatementText` volontairement non parsable ne relâche aucun nom ni littéral d'origine.
- `obfuscation_map` : migration idempotente, `INSERT` des seules nouvelles entrées (test du style `QdsStorageTests`).
- Ouvrabilité : la sortie est un XML showplan bien formé (validation `XDocument.Parse` ; idéalement contrôle de structure du namespace showplan).

## Limites connues v1 (documentées)

- Un identifiant présent seulement dans le texte SQL et jamais dans l'arbre d'opérateurs (alias local défini en SQL, `@variable`) n'est pas mappé : il est laissé verbatim. Risque faible (pas de PII), réécriture fine déférée.
- Pas de validation contre le XSD showplan officiel ; on se limite à « bien formé » plus contrôle de structure léger.
- Un nom multi-parties à quatre composants (`serveur.base.schéma.objet` dans `ProcName`/`FunctionName`) voit son segment serveur retiré (jamais émis, donc pas de fuite) plutôt que mappé : la fidélité de round-trip sur ce segment n'est pas garantie.
- Une colonne portée par une référence en base `tempdb` (table temporaire `#temp`) est whitelistée avec l'élément, donc non mappée (comportement hérité de la règle whitelist par élément).

## Fichiers touchés (indicatif)

- `src/SqlFerret.Core/Obfuscation/PlanObfuscator.cs` (nouveau)
- `src/SqlFerret.Core/Obfuscation/ObfuscationMap.cs` (nouveau)
- `src/SqlFerret.Core/Obfuscation/StatementTextRewriter.cs` (nouveau)
- `src/SqlFerret.Core/Storage/DuckDbProject.*.cs` : migration + chargement/écriture de `obfuscation_map`
- `src/SqlFerret.Cli/` : commande `obfuscate-plan` (modes projet et standalone)
- `tests/SqlFerret.Core.Tests/` : fixtures golden + suites ci-dessus
