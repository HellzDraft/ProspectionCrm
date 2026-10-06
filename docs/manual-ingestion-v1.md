# Ingestion manuelle de résultats — Phases 6.1 à 6.2.2

Cette route reçoit des résultats déjà fournis par le client. Elle n'exécute aucune
recherche réseau, collecte planifiée ou automatisation. La configuration et la
recherche sauvegardée doivent déjà exister ; aucun catalogue métier n'est créé.

## Contrat HTTP

`POST /api/saved-searches/{savedSearchId}/ingestions`

```json
{
  "pipelineStageId": "<GUID de l'étape cible>",
  "items": [
    {
      "title": "Développeur Unity",
      "externalId": "job-123",
      "sourceUrl": "https://example.org/jobs/123",
      "companyName": "Studio exemple",
      "location": "France",
      "description": "Description fournie par le client"
    }
  ]
}
```

L'étape est explicitement fournie, jamais déduite de son nom, de son ordre ou du
pipeline par défaut. Elle doit être non archivée et appartenir exactement au
pipeline non archivé de la recherche. Recherche et configuration doivent être
non archivées et `Enabled = true`, dans le workspace courant.

| Champ | Règle |
| --- | --- |
| `pipelineStageId` | GUID obligatoire et non vide |
| `items` | Tableau obligatoire de 1 à 100 éléments non null |
| `title` | Obligatoire, non blanc, maximum 200 caractères |
| `externalId` | Facultatif, non blanc si fourni, maximum 500 ; opaque et sensible à la casse |
| `sourceUrl` | Facultative, maximum 2048 ; URL HTTP(S) absolue sans identifiants utilisateur |
| `companyName` | Facultatif, non blanc si fourni, maximum 200 ; comparaison métier et historique |
| `location` | Facultative, maximum 200 |
| `description` | Facultative, maximum 10000 ; stockée dans `Opportunity.Notes` à la création seulement |

Chaque élément doit fournir au moins `externalId`, `sourceUrl` ou `companyName`.
Un couple titre/société sans URL ni identifiant peut retrouver une opportunité
existante ; s'il ne la retrouve pas, le lot échoue en 409 `MissingPersistentIdentity`.
Une création exige toujours une URL ou un identifiant externe. Le titre/société reçu
est conservé dans SourceExecutionItem et peut désormais retrouver cette opportunité
lors d'un élément ultérieur ou d'un prochain lot, même sans Company liée.

Aucune Company n'est créée ni affectée à partir de son nom. Pour la déduplication,
`CompanyName` compare le nom de la Company liée et les observations historiques réussies. Plusieurs
sociétés peuvent avoir le même nom ; plusieurs opportunités candidates produisent
un conflit. Le titre seul, ou titre + société absente, n'est jamais une identité.

Les propriétés inconnues sont refusées, y compris `publishedAt`, `metadata`,
`priorityCode` ou des timestamps de provenance. Les dates de publication et les
métadonnées ne sont pas conservées. Le corps HTTP est limité à 2 000 000 octets
(un dépassement de transport peut produire 413).

## Réponses

Un succès renvoie **201**, y compris pour un rejeu : une nouvelle SourceExecution
est créée pour chaque lot admis. `Location` pointe vers
`GET /api/source-executions/{executionId}`.

```json
{
  "execution": {
    "id": "<GUID>",
    "sourceConfigurationId": "<GUID>",
    "savedSearchId": "<GUID>",
    "triggerTypeCode": "manual",
    "statusCode": "succeeded",
    "startedAt": "<date UTC>",
    "finishedAt": "<date UTC>",
    "itemsFound": 1,
    "itemsCreated": 1,
    "itemsUpdated": 0,
    "itemsIgnored": 0,
    "errorMessage": null,
    "historyAvailable": true,
    "historyVersion": 1,
    "contractVersion": 1,
    "normalizationVersion": 1,
    "targetPipelineId": "<GUID>",
    "targetPipelineStageId": "<GUID>",
    "itemsRejected": 0,
    "itemsRolledBack": 0,
    "itemsNotProcessed": 0,
    "itemsCancelled": 0
  },
  "items": [
    { "index": 0, "opportunityId": "<GUID>", "outcome": "created" }
  ]
}
```

Les résultats restent dans l'ordre du lot, avec index à partir de zéro. Les issues
sont `created`, `updated` ou `ignored`. `updated` signifie **redécouverte**, avec
observation de la provenance, jamais remplacement des champs métier Opportunity.

| Statut | Cas |
| --- | --- |
| 400 | JSON/DTO/lot invalide, URL invalide, longueurs dépassées, absence d'identité utilisable |
| 404 | Recherche, configuration, pipeline ou étape inexistante ou hors workspace |
| 409 | Workspace V1 incompatible, état inactif, étape d'un autre pipeline, identité ambiguë ou non persistable, collision concurrente reconnue |
| 500 | Échec technique pendant le traitement, avec rollback métier |

Les erreurs métier renvoient ProblemDetails avec `code`, `itemIndex` nullable et
`executionId` nullable. Les validations automatiques ASP.NET renvoient leur
ValidationProblemDetails habituel. Aucun classement ne dépend du texte d'une
exception. Les détails SQL ne sont pas exposés au client.

## Normalisation et identités

URL : suppression des blancs périphériques, passage du schéma et de l'autorité
(hôte, avec port éventuel) en minuscules invariantes. Les blancs internes, caractères
de contrôle, antislashs, URL relatives et identifiants utilisateur sont refusés.
Le chemin, la query et le fragment restent inchangés : casse, ordre des paramètres,
paramètres de suivi, encodage pourcent, slash final et port explicite sont conservés.
Pas de décodage, résolution de redirection, ajout de slash ou suppression de fragment.
OpportunitySource conserve désormais la clé NormalizedSourceUrl calculée avec cette
fonction. L'ingestion compare directement cette clé persistée. Une URL legacy invalide
bloque la migration ; elle n'est ni ignorée ni réécrite.

Titre/société : `Trim()`, remplacement de chaque suite d'espaces reconnus par
`\s` par un espace ASCII, puis `ToUpperInvariant()`. Ni suppression d'accents,
ni translittération, ni fuzzy matching. Ces clés servent uniquement à comparer :
le titre, la localisation et la description fournis restent inchangés au stockage.
ExternalId est comparé exactement, sans trim ni changement de casse.

La recherche couvre **tout le workspace**, toutes les étapes/pipelines et les
opportunités archivées. Ordre de priorité :

1. `(WorkspaceId, SourceConfigurationId, ExternalId)` exact ;
2. `(WorkspaceId, NormalizedSourceUrl)` ;
3. titre/société normalisés issus de la Company actuelle, de l’historique réussi
   avec OpportunityId vivant, et du registre du lot.

Toutes les identités disponibles sont examinées. Si leur union désigne plusieurs
opportunités, même avec un ExternalId valide, le résultat est un 409
`AmbiguousIdentity`. Une URL déjà portée par plusieurs opportunités est également
ambiguë. Aucune fusion ou sélection arbitraire n'est effectuée.

Une création utilise l'étape demandée et `PriorityCode = normal`, sans société,
contact, score, archive ou modification du défaut. Une redécouverte ne change
**aucun champ Opportunity**, y compris `UpdatedAt`.

## Provenance

Une nouvelle provenance reçoit configuration/recherche/exécution d'origine,
`SourceLabel = SourceConfiguration.Name`, les identités disponibles et les dates
serveur. Une provenance existante conserve ses identités, son libellé et ses
références d'origine. `LastSeenAt` devient le maximum de sa valeur précédente,
de `FirstSeenAt` et du début de l'exécution ; il ne recule jamais.

Une nouvelle URL pour un ExternalId connu crée un alias URL distinct, sans effacer
l'ancienne URL. Une URL déjà attachée à l'opportunité ne peut pas être réinsérée
sous une autre configuration : l'index SQL existant l'interdit. Si un nouvel
ExternalId accompagne cette URL, une provenance portant cet ExternalId et une URL
null est ajoutée ; la provenance URL existante est conservée et observée.
Sans nouvel ExternalId, seule la provenance URL existante est observée.

Une redécouverte par titre/société seul ne crée ni ne réobserve de provenance vide.
SourceExecutionItem porte cette décision ; aucun lien SourceExecutionItemSource
n’est requis. Les anciens marqueurs null/null restent inchangés en base.

Sur OpportunitySource, `SourceExecutionId` et `SavedSearchId` restent les références
de première création de la provenance, pas celles de sa dernière observation.
Depuis la Phase 6.2.1, les observations sont conservées séparément dans
SourceExecutionItem et SourceExecutionItemSource.

## Transaction et concurrence

`IngestionService` possède une transaction PostgreSQL **ReadCommitted** couvrant
le lot ; il ne chaîne pas les services CRUD existants.

Un verrou transactionnel consultatif `pg_advisory_xact_lock` sérialise les ingestions
et les écritures coopératives d'identité d'un même workspace. Sa clé est constituée des huit premiers octets
SHA-256, lus en Int64 big-endian, de
`ProspectionCrm/manual-ingestion/{workspaceId:D}`. Ce périmètre correspond à celui
de la déduplication, y compris entre recherches et configurations différentes.
Il n'y a aucun verrou global ni verrou de table ; un autre workspace peut avancer.

Ordre : verrou consultatif, workspace `FOR KEY SHARE`, recherche `FOR SHARE`,
configuration `FOR SHARE`, pipeline `FOR SHARE`, relecture de l'étape. Les valeurs
sont relues après attente. Le verrou parent protège la sélection d'étape face aux
services d'archivage/réordonnancement de Phase 4. Aucune ligne Opportunity existante
n'est modifiée et aucun verrou workspace `FOR NO KEY UPDATE` n'est ajouté.

WorkspaceIdentityLock est partagé par l'ingestion, Create/Update/Delete OpportunitySource,
Create/Update/Delete physique Opportunity et Update/Delete Company. Il exige une transaction
active et conserve exactement la clé V1. Le verrou d'identité est acquis avant les
verrous Pipeline, puis les données sont relues. Company.Create reste hors verrou :
une Company non liée n'est pas candidate ; son rattachement passe par Opportunity.Update.
Archive/Restore Opportunity reste hors de ce verrou, car les archives sont candidates.
Le SQL externe ne coopère pas forcément : les nouvelles contraintes et unicités
PostgreSQL restent la protection des références et clés persistées. L'ingestion
convertit les violations 23505 de ses deux index en 409 ConcurrentIdentityChange, sans retry.

## SourceExecution et atomicité

La validation du lot et des ressources précède toute création d'exécution. Les
refus à cette étape n'ajoutent aucune donnée.

Un lot admis crée une exécution `manual/running` avec son snapshot de contexte et
une SourceExecutionItem `pending` par entrée, puis sauvegarde ces lignes avant
le savepoint. Les écritures métier, décisions et liens vers les provenances suivent
ce savepoint. En succès, le statut devient `succeeded` et l'ensemble
est committé. Le POST retourne les timestamps relus dans PostgreSQL, avec la même
précision que le GET. Le statut `running` reste interne à la transaction et n'est
pas publié comme suivi temps réel.

En conflit ou erreur technique, rollback au savepoint, abandon de l'état EF suivi,
puis finalisation `failed` et commit du seul historique (exécution et éléments).
Les décisions provisoires sont conservées en mémoire pour cette finalisation.
Une annulation reçue
pendant le traitement tente de conserver `cancelled` avec la même procédure.
La finalisation dispose d'un délai indépendant de 10 secondes, sans retry.

| Compteur | Signification pour HistoryVersion = 1 |
| --- | --- |
| `ItemsFound` | Nombre d'éléments reçus, doublons compris |
| `ItemsCreated` | Éléments ayant créé une nouvelle Opportunity |
| `ItemsUpdated` | Éléments non répétés ayant retrouvé une Opportunity et observé sa provenance |
| `ItemsIgnored` | Répétitions dans le lot de la même signature ExternalId/URL normalisée/titre normalisé/société normalisée |
| `ItemsRejected` | Élément fautif, conflit métier ou erreur technique identifiable |
| `ItemsRolledBack` | Éléments déjà traités dont la décision provisoire a été annulée avec le lot |
| `ItemsNotProcessed` | Éléments non traités après l'élément bloquant |
| `ItemsCancelled` | Élément en cours lors d'une annulation (au plus un) |

Une répétition n'écrase pas les données même si sa localisation/description diffère.
Un rejeu dans un nouvel appel observe de nouveau la provenance et compte `updated`.
Sur succès, les quatre nouveaux compteurs valent zéro. Sur échec ou annulation,
`ItemsCreated = ItemsUpdated = ItemsIgnored = 0`. Une exécution failed ne compte
aucun cancelled ; une exécution cancelled ne compte aucun rejected.
Pour chaque exécution terminale de version 1 :

```text
ItemsFound = ItemsCreated + ItemsUpdated + ItemsIgnored
           + ItemsRejected + ItemsRolledBack + ItemsNotProcessed + ItemsCancelled
```

Exemple d'échec au milieu d'un lot de trois : `rolled-back / rejected / not-processed`
et compteurs `1 / 1 / 1`. Une annulation au même point remplace rejected par
cancelled. Si la finalisation échoue après le traitement de tous les éléments,
ils deviennent tous rolled-back, y compris les doublons provisoirement ignored.
Une annulation après le traitement complet peut donc avoir ItemsCancelled = 0.
Aucun élément pending ne subsiste dans une exécution terminale produite par le service.

Les timestamps sont UTC et `FinishedAt >= StartedAt`. `ErrorMessage` contient un
code contrôlé, sans données sensibles du lot. Les logs structurés portent workspace,
recherche et exécution ; les contenus de résultats ne sont pas journalisés par le service.

Une panne de connexion, un arrêt du processus ou l'impossibilité d'écrire l'exécution
elle-même peut empêcher la conservation du journal d'échec. La transaction protège
toujours contre les écritures métier partielles ; un commit dont l'accusé de réception
est perdu peut avoir abouti. Un rejeu permet de retrouver les identités persistées.

## Historique structuré — Phase 6.2.1

La migration `20261006091016_Phase621IngestionHistoryFoundation` ajoute dix colonnes
à SourceExecution et deux tables. Son Up ne supprime aucune donnée et ne fabrique
aucun historique pour les anciennes exécutions. Son Down retire uniquement ces
ajouts (et donc le nouvel historique), en préservant les colonnes antérieures.

Les anciennes exécutions ont HistoryVersion, ContractVersion, NormalizationVersion,
TargetPipelineId, TargetPipelineStageId et ContextSnapshotJson à null, et les quatre
nouveaux compteurs à zéro. Leur convention antérieure de compteurs reste inchangée.
Le DTO expose `HistoryAvailable = HistoryVersion.HasValue` : false pour ce legacy.
Les nouvelles exécutions manuelles utilisent les trois versions à 1, les deux IDs
de destination et le snapshot obligatoire. Les IDs de destination sont historiques,
sans FK vivante qui empêcherait leur conservation.

`SourceExecutionItem` conserve exactement une entrée du lot, avec :

- Id, WorkspaceId, SourceExecutionId et ItemIndex zéro-based ;
- OpportunityId vivant nullable et OpportunityIdSnapshot nullable ;
- OutcomeCode (50 caractères), DecisionCode contrôlé (100), ReceivedAt et ProcessedAt ;
- Title brut (200) et NormalizedTitle (400) ;
- CompanyName brut nullable (200) et NormalizedCompanyName nullable (400) ;
- ExternalId exact nullable (500), SourceUrl brute et NormalizedSourceUrl nullables (2048) ;
- PayloadSnapshotJson et DecisionDetailsJson, objets jsonb nullables.

PayloadSnapshotJson ne contient que `location` et `description`, ou reste null si
les deux sont null. Les valeurs brutes ne sont jamais réécrites. ReceivedAt est le
StartedAt de l'exécution ; ProcessedAt est renseigné pour chaque issue terminale.
La paire (SourceExecutionId, ItemIndex) est unique. La FK composée
(WorkspaceId, SourceExecutionId) garantit le workspace du parent.
Les paires brut/normalisé sont présentes ou absentes ensemble.

`SourceExecutionItemSource` conserve Id, WorkspaceId, SourceExecutionItemId,
OpportunitySourceId vivant nullable, OpportunitySourceIdSnapshot obligatoire et
RoleCode (50, external-id ou source-url). Une même provenance portant les deux
identités produit deux liens de rôles distincts, sans doublon exact.
La FK parent est composée avec WorkspaceId. Aucun lien n'est requis pour ignored
ou un rapprochement par titre/société seul ; le marqueur OpportunitySource sans
identité est conservé pour le legacy uniquement ; aucune nouvelle ligne vide n’est créée.

Les FK vivantes vers Opportunity et OpportunitySource utilisent SET NULL : une
suppression physique conserve les IDs snapshot. Les suppressions administratives
d'exécutions suppriment leurs éléments et liens par cascade, sous réserve des
restrictions de provenance déjà existantes. Les FK vivantes vers les objets métier
ne sont pas encore composées avec WorkspaceId.

L'index (WorkspaceId, NormalizedTitle, NormalizedCompanyName), filtré sur une
société et un OpportunityIdSnapshot présents et une issue created/updated/ignored,
est **non unique**. Il sert au fallback historique durable depuis la Phase 6.2.2. Les index chronologiques, par issue et par provenance
complètent ces accès.

Les décisions sont centralisées dans `IngestionHistoryCodes` :
Pending, CreatedNewOpportunity, MatchedExternalId, MatchedSourceUrl,
MatchedTitleCompany, MatchedConsistentIdentities, DuplicateInBatch,
AmbiguousIdentity, MissingPersistentIdentity, ConcurrentIdentityChange,
PersistenceFailure, RequestCancelled, RolledBackAfterFailure et NotProcessedAfterFailure.
DecisionCode n'a pas de liste SQL exhaustive pour permettre son évolution.

DecisionDetailsJson contient uniquement les détails contrôlés appropriés :
`providedIdentities` pour created, `matchedBy` pour updated,
`duplicateOfItemIndex` pour ignored, `candidateOpportunityIds` pour une ambiguïté,
`provisionalOutcomeCode` et `provisionalOpportunityId` pour rolled-back,
`blockedByItemIndex` pour not-processed et `cancelledAtItemIndex` pour cancelled.
Les issues d'échec n'ont ni OpportunityId vivant ni OpportunityIdSnapshot :
un ID provisoire reste uniquement dans les détails rolled-back.
Aucun lien SourceExecutionItemSource ajouté après le savepoint ne survit à l'échec.

Le snapshot de contexte a exactement cette structure :

```json
{
  "schemaVersion": 1,
  "sourceConfiguration": {
    "id": "<guid>", "name": "<nom>", "sourceTypeCode": "<type>",
    "baseUrl": "<valeur ou null>",
    "configurationSha256": "<SHA-256 hexadécimal minuscule ou null>"
  },
  "savedSearch": {
    "id": "<guid>", "name": "<nom>", "searchUrl": "<valeur ou null>",
    "criteria": {}
  },
  "pipeline": { "id": "<guid>", "name": "<nom>", "typeCode": "<type>" },
  "targetStage": { "id": "<guid>", "name": "<nom>", "categoryCode": "<catégorie>" }
}
```

Il est généré avec JsonSerializer avant le savepoint. criteria est l'objet issu de
CriteriaJson. ConfigurationJson n'est jamais copié : seul son SHA-256 UTF-8,
calculé sur la chaîne relue de PostgreSQL telle que stockée, est conservé, ou null.
Aucun header, cookie, token ou secret externe n'est ajouté. Les snapshots restent
inchangés après renommage ou modification des ressources. L'application ne propose
aucune modification des observations terminales ; ce journal n'est pas une protection
contre un administrateur écrivant directement en SQL.

Le POST et le GET SourceExecution exposent les mêmes versions, destinations et
compteurs. **ContextSnapshotJson et les observations détaillées ne sont pas exposés
publiquement** ; leurs endpoints de lecture appartiennent à la Phase 6.2.4.

## Identités persistantes — Phase 6.2.2

La migration `20261006101923_Phase622PersistentSourceIdentities` ajoute à OpportunitySource
`WorkspaceId` obligatoire et `NormalizedSourceUrl` nullable, limitée à 2048 caractères.
Le CRUD conserve SourceUrl brute ; l'ingestion conserve sa valeur métier V1 déjà
normalisée. Dans les deux flux, NormalizedSourceUrl est la clé V1. NormalizationVersion
reste à 1 ; aucune règle de comparaison URL ou texte n'a changé.

Les clés alternatives (WorkspaceId, Id) existent sur Opportunity, SourceConfiguration,
SavedSearch, SourceExecution et OpportunitySource. Les FK d'OpportunitySource vers
Opportunity, SourceConfiguration, SavedSearch et SourceExecution incluent WorkspaceId.
La première conserve CASCADE, les autres RESTRICT ; Workspace possède sa propre FK RESTRICT.
Les références facultatives restent facultatives. Les liens vivants d'historique
conservent leurs FK simples SET NULL.

Les nouvelles unicités partielles sont :

- `UX_OpportunitySources_Workspace_NormalizedSourceUrl` sur
  (WorkspaceId, NormalizedSourceUrl), si la clé URL n'est pas null ;
- `UX_OpportunitySources_Workspace_SourceConfiguration_ExternalId` sur
  (WorkspaceId, SourceConfigurationId, ExternalId), si les deux identifiants sont présents.

Un ExternalId exige une SourceConfiguration. SourceUrl et NormalizedSourceUrl doivent
être présents ou absents ensemble. Les anciens index uniques sont remplacés ;
l'index non unique SourceUrl reste disponible. ExternalId reste opaque et sensible à la casse.
La même URL peut exister dans deux workspaces ; le même ExternalId dans deux configurations.

Le backfill ajoute d'abord les colonnes nullables et remplit WorkspaceId depuis
Opportunity. Il vérifie ensuite les références, calcule les clés URL et vérifie les
collisions URL/ExternalId avant d'imposer NOT NULL et les nouvelles contraintes.
La fonction SQL temporaire est figée avec cette migration, y compris la casse Unicode
invariante .NET, et testée contre UrlKey. Elle n'est pas utilisée par les services.
SourceUrl brute n'est jamais réécrite. Une anomalie fait échouer toute la migration
dans sa transaction : diagnostics Phase622 contrôlés, avec compte et IDs, sans URL
ni ExternalId en clair. Aucun rattachement, fusion ou nettoyage automatique n'est effectué.
Les données doivent être corrigées explicitement avant de réessayer.

Les provenances legacy null/null sont conservées telles quelles. Les services refusent
les nouvelles provenances vides ; la base ne pose pas un check global qui invaliderait
ces lignes historiques. Down restaure les FK/index précédents et retire les deux nouvelles
colonnes (leurs valeurs sont perdues), sans supprimer les lignes ni modifier les anciennes
colonnes. La réapplication reconstruit les clés sur un jeu compatible.

Le fallback réunit trois ensembles d'OpportunityId distincts : Company actuellement
liée, SourceExecutionItem historique, registre du lot. Le registre utilise
(NormalizedTitle, NormalizedCompanyName) vers un ensemble d'IDs et est enrichi après
chaque décision réussie. L'historique exige le même workspace, les deux clés identiques,
une issue created/updated/ignored et un OpportunityId vivant dans le workspace.
Un ID snapshot sans référence vivante n'est jamais candidat. Un renommage ultérieur
n'efface pas l'identité historique ; une suppression physique exclut l'opportunité.
Les trois ensembles et les identités ExternalId/URL sont examinés ensemble :
plusieurs opportunités distinctes donnent AmbiguousIdentity, sans priorité masquant
une contradiction. L'index titre/société reste non unique.

Le CRUD conserve `/api/opportunities/{opportunityId}/sources` :

| Opération | Comportement |
| --- | --- |
| POST | 201 ; WorkspaceId serveur, références résolues, URL validée et normalisée, collisions vérifiées |
| PUT non utilisé | 204 après validations ; WorkspaceId et OpportunityId ne changent jamais |
| PUT utilisé identique | 204 sans écriture |
| PUT utilisé modifié | 409 ImmutableSourceIdentity |
| DELETE non utilisé | 204, suppression physique |
| DELETE utilisé | 409 SourceIdentityInUse |

Une provenance est utilisée si SourceExecutionId est renseigné **ou** si une
SourceExecutionItemSource la référence. La protection porte sur configuration,
recherche, exécution, libellé, URL, ExternalId et LastSeenAt. L'ingestion peut toujours
observer LastSeenAt dans sa transaction ; cette opération n'est pas un PUT utilisateur.

Les résultats de service sont typés : Succeeded, NotFound, InvalidInput et Conflict.
ProblemDetails expose code et detail contrôlé : InvalidSourceUrl/MissingSourceIdentity/
InvalidReference/InvalidInput/InvalidTimestamps en 400, ressource absente en 404,
DuplicateExternalId/DuplicateSourceUrl/ImmutableSourceIdentity/SourceIdentityInUse/
ConcurrentIdentityChange en 409. Les violations 23505 des deux index connus sont
mappées précisément ; aucun texte d'exception n'est analysé et aucun détail SQL n'est exposé.

PostgreSQL garantit les relations de workspace, les paires URL/clé, le contexte
ExternalId et les unicités des clés stockées. Les services garantissent la normalisation,
l'absence de nouvelles provenances vides, la protection des identités observées et la
sérialisation des écritures coopératives. Un SQL externe peut encore fournir une
**fausse clé normalisée**, modifier l'historique ou contourner les règles de service :
aucun trigger de normalisation ou dispositif contre un administrateur n'est ajouté.
Le fallback titre/société n'est pas une unicité SQL et ne déclenche aucune fusion.

## Vérification et limites

`IngestionTests` teste les routes HTTP sur PostgreSQL 18 jetable : mappage, rejeu,
archives et champs utilisateur, toutes les identités et conflits, normalisation,
références et isolation, lots invalides, échec de provenance ou de finalisation,
compteurs, concurrence HTTP et revalidation après attente. Les contrôles EF restent
actifs. `IngestionHistoryTests` vérifie également les valeurs historiques, les liens,
le snapshot et son hash, les contraintes SQL, la suppression physique, les échecs,
l'annulation avec gate, la reconstruction et l'upgrade réel depuis la migration
Phase42PipelineLifecycleAndDefault (insertion SQL dans l'ancien schéma).

`PersistentSourceIdentityMigrationTests` couvre le backfill depuis 6.2.1, les refus
atomiques, la parité SQL/.NET et Down/Up. `PersistentSourceIdentityTests` couvre
les identités persistées, les fallbacks et les contraintes PostgreSQL directes.
`OpportunitySourceTests` vérifie le CRUD HTTP et les collisions avec un écrivain SQL.
`PersistentSourceIdentityConcurrencyTests` utilise des gates et pg_stat_activity
pour vérifier la coopération des écritures Opportunity/Company/provenance.

Le premier parcours privilégie une décision déterministe sur des lots bornés. Il
charge les opportunités/provenances du workspace en mémoire ; les clés URL sont
persistées et indexées, mais les recherches SQL ciblées et l'optimisation pour des
volumes importants restent à étudier. Les noms de société
libres conservés dans le journal participent au fallback durable depuis 6.2.2.
Les endpoints publics d'historique restent à réaliser en 6.2.4. Aucun Worker, queue, planification, réseau, n8n, retry
automatique, scoring, IA, email ou automatisation métier n'est ajouté.

```powershell
dotnet test ProspectionCrm.slnx --configuration Release --filter "FullyQualifiedName~IngestionTests|FullyQualifiedName~IngestionHistoryTests|FullyQualifiedName~PersistentSourceIdentity|FullyQualifiedName~OpportunitySource|FullyQualifiedName~Phase4ReconstructionTests"
dotnet build ProspectionCrm.slnx --configuration Release
dotnet test ProspectionCrm.slnx --configuration Release
```
