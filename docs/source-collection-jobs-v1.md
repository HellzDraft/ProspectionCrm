# File persistante des collectes — Phase 7.1

`SourceCollectionJob` est une commande persistante demandant une collecte future.
`SourceExecution` est l'historique d'une tentative réellement commencée. L'enqueue
ne crée aucune exécution, observation, opportunité ou provenance, et ne réutilise
pas `AutomationExecution`.

**Cette version ne traite aucun job.** Un job `queued` reste en attente tant qu'il
n'est pas annulé. Aucun Worker, BackgroundService, IHostedService de collecte,
polling, lease, heartbeat, retry, ordonnanceur ou traitement automatique n'est
enregistré. Il n'existe pas d'endpoint `/run` ou `/process-next`, ni de reprise après
crash. La commande survit au redémarrage grâce à PostgreSQL, mais n'est pas exécutée.

## Enqueue

```http
POST /api/saved-searches/{savedSearchId}/collection-jobs
Content-Type: application/json

{"pipelineStageId":"<guid non vide>"}
```

Le body est strict : étape obligatoire, `Guid.Empty` et propriétés inconnues
refusés. Le client ne fournit ni date, ni priorité, ni type de déclenchement.
Les erreurs de binding retournent 400 `InvalidRequest`, sans écho des valeurs.

Succès : **202 Accepted**, `Location: /api/source-collection-jobs/{id}` et
`SourceCollectionJobDto` comprenant exactement `id`, `workspaceId`, `savedSearchId`,
`pipelineId`, `pipelineStageId`, `triggerTypeCode`, `statusCode`, `enqueuedAt`,
`availableAt`, `startedAt`, `finishedAt`, `attemptCount`, `sourceExecutionId`,
`errorCode`. Le travail reste à exécuter : la réponse n'est pas 201.

L'API résout le Workspace actif unique, ouvre une transaction PostgreSQL courte,
valide le contexte puis insère le job et commit. Valeurs initiales : `manual`,
`queued`, `attemptCount = 0`, dates UTC `availableAt = enqueuedAt`, autres dates,
exécution et erreur nulles.

`ISourceCollectionContextResolver` reçoit explicitement les IDs du Workspace, de la
recherche et de l'étape. Il charge sans tracking les ressources, vérifie leur
appartenance et leur état actif/non archivé, résout l'adaptateur et appelle seulement
`Validate`. Il ne dépend pas du Workspace courant et n'appelle ni réseau, ni DNS,
ni `CollectAsync`, ni ingestion. `/collect` utilise ce même resolver avant son
traitement synchrone habituel ; son contrat 201 et celui de `/ingestions` restent
inchangés.

Le job ne contient aucun SearchUrl, CriteriaJson, ConfigurationJson, snapshot,
fingerprint ou contenu de flux. Lors du futur traitement, le Worker devra relire
et revalider la configuration courante. Une validation réussie à l'enqueue ne
garantit donc pas la validité de la configuration au moment du traitement.

## Concurrence et intégrité

L'index unique partiel `UX_SourceCollectionJobs_Workspace_Search_Stage_Active`
porte sur `(WorkspaceId, SavedSearchId, PipelineStageId)` pour les états `queued`
et `running`. Deux enqueues concurrents identiques donnent un 202 et un 409
`CollectionJobAlreadyPending`. Après la violation PostgreSQL 23505 de cet index,
le service rollback, vide le ChangeTracker et relit le job actif. Le ProblemDetails
inclut `existingJobId` lorsqu'il est encore disponible ; aucun nom de contrainte,
SQL ou stack trace n'est exposé. Les jobs terminaux identiques sont autorisés.

Les FK composées garantissent le Workspace et le pipeline de la recherche, le
pipeline de l'étape et le Workspace de l'exécution facultative. Toutes les
relations utilisent `Restrict`. Une exécution peut être associée au maximum à un
job grâce à un index unique partiel. Ces références empêchent notamment de changer
le pipeline d'une recherche référencée par un job, y compris historique.

Les contraintes SQL contrôlent les codes, les formes des cinq états, le compteur
non négatif et les dates. Aucun trigger de transition n'est ajouté. Les états
`running`, `succeeded` et `failed`, et les triggers `scheduled`, `event`, `retry`
sont acceptés par le schéma pour les phases futures, sans route qui les crée ici.

## Lecture et pagination

```http
GET /api/source-collection-jobs/{id}
GET /api/source-collection-jobs?offset=0&limit=50&statusCode=queued&savedSearchId=<guid>&triggerTypeCode=manual
```

Le détail retourne 200 ou 404 si absent/hors Workspace. La liste renvoie
`{ offset, limit, totalCount, hasMore, items }`. Offset : au moins 0 ; limit : 1–200,
50 par défaut. Filtres facultatifs : `statusCode` dans `queued`, `running`,
`succeeded`, `failed`, `cancelled` ; `triggerTypeCode` dans `manual`, `scheduled`,
`event`, `retry` ; `savedSearchId`. Une recherche étrangère retourne une page vide.
Tri SQL `EnqueuedAt DESC, Id DESC`, comptage et pagination en base.

Les erreurs de paramètres retournent 400 `InvalidPagination`, `InvalidStatusCode`
ou `InvalidTriggerTypeCode` ; un type non bindable donne `InvalidRequest`.
Les lectures de comptage et de page ne constituent pas un snapshot commun : des
changements concurrents peuvent décaler une pagination par offset.

## Annulation

```http
POST /api/source-collection-jobs/{id}/cancel
```

Sans body. Un UPDATE conditionnel atomique passe `queued` à `cancelled`, renseigne
`finishedAt` et retourne 204. Répéter sur `cancelled` retourne 204 sans changer la
date. `running`, `succeeded` et `failed` retournent 409
`CollectionJobNotCancellable`. Absent/hors Workspace : 404. Aucune suppression
physique. Un changement concurrent de références à l'enqueue peut retourner 409
`ConcurrentCollectionJobChange`.

Les erreurs CRM contrôlées reprennent les codes de `/collect` : 404
`ResourceNotFound`, 409 `WorkspaceUnavailable`, `InactiveResource`, `WrongPipeline`,
`UnsupportedSourceType`, `MissingFeedUrl`, `InvalidRssCriteria`, `UnsafeFeedUrl`.

## Migration et validation

Migration `20261007064337_Phase71PersistentCollectionJobs`, après
`20261006101923_Phase622PersistentSourceIdentities`. Elle ajoute uniquement la
table, les clés alternatives `(WorkspaceId, Id, PipelineId)` de SavedSearch et
`(PipelineId, Id)` de PipelineStage, les FK, contraintes et index. Aucune donnée
existante modifiée, aucun job rétroactif. `Down` supprime la table et ces deux clés.
Appliquer explicitement avec :

```powershell
dotnet ef database update --project src/ProspectionCrm.Api --startup-project src/ProspectionCrm.Api -- --environment Development
```

Les tests dédiés `SourceCollectionJobTests` et `SourceCollectionJobMigrationTests`
utilisent PostgreSQL réel isolé et un transport RSS qui échoue s'il est appelé.
Ils couvrent concurrence déterministe, isolation, pagination, annulation, contraintes
réelles, upgrade préservant le contenu des tables existantes, reconstruction vierge,
Down/Up et absence de divergence EF. Les suites RSS et ingestion restent exécutées.

## Étapes futures

Les prochaines tranches devront ajouter la prise de travail atomique (claim/lease),
le Worker et sa revalidation, puis les retries/backoff et la planification. Aucun
de ces comportements n'est disponible dans la Phase 7.1.
