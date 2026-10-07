# File persistante et Worker de collecte — Phases 7.1 et 7.2

`SourceCollectionJob` est une commande persistante demandant une collecte future.
`SourceExecution` est l'historique d'une tentative réellement commencée. L'enqueue
ne crée aucune exécution, observation, opportunité ou provenance, et ne réutilise
pas `AutomationExecution`.

La Phase 7.2 ajoute un Worker .NET optionnel, le claim atomique et les leases.
Le Worker est désactivé par défaut. Activé, il consomme les commandes persistées,
y compris celles en attente avant son démarrage. Aucun endpoint `/run` ou
`/process-next` n’est exposé. Les routes et les 14 champs du DTO Phase 7.1 restent
inchangés ; les deux propriétés de lease sont internes au modèle persistant.

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
fingerprint ou contenu de flux. Lors du traitement, le Worker relit
et revalide la configuration courante. Une validation réussie à l'enqueue ne
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
non négatif et les dates. Aucun trigger de transition n'est ajouté. Le Worker utilise
`running`, `succeeded` et `failed`. Les triggers `scheduled`, `event`, `retry`
restent acceptés par le schéma sans création automatique de ces commandes.

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

## Claim PostgreSQL et possession

`ISourceCollectionJobQueue` est distinct du service des routes HTTP. Le claim
exécute une seule instruction : CTE sélectionnant au maximum une ligne `queued`
dont `AvailableAt <= statement_timestamp()`, triée par `AvailableAt ASC`,
`EnqueuedAt ASC`, `Id ASC`, avec `FOR UPDATE SKIP LOCKED`, puis `UPDATE ... FROM
candidate ... RETURNING j.*`. La transition, l’incrément de `AttemptCount`,
`StartedAt` et la création du lease sont atomiques. Le résultat est la ligne
réellement modifiée, ou aucun travail. L’horloge PostgreSQL fait autorité.

Une ligne verrouillée est ignorée ; l’ordre porte sur les candidats accessibles,
pas sur un FIFO bloquant. Deux instances ne prennent pas le même job. La course
avec `/cancel` produit soit un job annulé non claimé, soit un job running dont
l’annulation retourne 409. Aucun verrou ni transaction du claim ne dure pendant
le réseau. Une instance traite un job à la fois ; plusieurs instances peuvent
consommer des jobs distincts.

`LeaseToken` est un UUID non vide propre à la possession ; `LeaseExpiresAt` vaut
`StartedAt + LeaseDurationSeconds`. La contrainte SQL impose un token et une
expiration strictement après StartedAt pour running, et leur absence dans tous
les autres états. Le rattachement d’historique et la finalisation exigent le même
JobId, WorkspaceId, état running et token. Une possession étrangère ne peut pas
finaliser le job.

Le lease est fixe, sans renouvellement ni heartbeat. Le processeur déclenche une
annulation coopérative selon la durée configurée, en déduisant le temps écoulé
depuis le début du claim (horloge monotone). Ce budget conservatif évite de supposer
les horloges applicative et PostgreSQL synchronisées ; les opérations doivent
respecter le token. Le nettoyage historique et la
finalisation peuvent dépasser l’échéance. Le détenteur du même token reste
habilité à finaliser après expiration, puisqu’aucune reprise concurrente n’existe.
Ce mécanisme n’est pas une interruption forcée ni une garantie « exactly once ».

**Un running expiré n’est jamais sélectionné, réinitialisé ou réenfilé.** Après
crash ou impossibilité de finaliser en base, il reste running avec son compteur,
son token, son échéance et son éventuelle exécution. L’index actif continue de
bloquer un enqueue identique. La réconciliation est laissée à une tranche future ;
aucun endpoint de réparation ni script de remise en file n’est fourni ici.
Diagnostic en lecture seule :

```sql
SELECT "Id", "WorkspaceId", "StartedAt", "LeaseExpiresAt", "SourceExecutionId"
FROM "SourceCollectionJobs"
WHERE "StatusCode" = 'running' AND "LeaseExpiresAt" <= statement_timestamp();
```

## Cycle du Worker et options

`SourceCollectionWorker` est un singleton `BackgroundService` qui reçoit une
factory de scopes, les options et un logger, jamais un DbContext scoped. Chaque
cycle crée et libère un scope pour `ISourceCollectionJobProcessor` et ses services.
Après un job traité, il recherche le suivant ; si la file est vide ou qu’une erreur
de cycle survient, il attend avec le token d’arrêt. Les exceptions inattendues sont
journalisées et ne tuent pas la boucle.

```json
"SourceCollectionWorker": {
  "Enabled": false,
  "IdleDelaySeconds": 5,
  "LeaseDurationSeconds": 300
}
```

Ces valeurs sont communes à tous les environnements. Les options typées sont
validées au démarrage : IdleDelaySeconds entre 1 et 300, LeaseDurationSeconds entre
30 et 3600. Les variables `SourceCollectionWorker__Enabled`,
`SourceCollectionWorker__IdleDelaySeconds`, `SourceCollectionWorker__LeaseDurationSeconds`
les surchargent au démarrage. Les tests peuvent conserver Enabled=false, remplacer
les services par DI ou activer explicitement le vrai hosted service. Les migrations
restent explicites, jamais exécutées par le Worker.

Le token de traitement combine arrêt de l’application et délai du lease. En cas
d’arrêt coopératif, l’historique conserve l’annulation lorsqu’une tentative a
commencé ; le job devient failed avec `CollectionWorkerStopping`. Une échéance
atteinte donne `CollectionLeaseExpired`. Le nettoyage utilise un scope neuf et un
délai indépendant de dix secondes pour finaliser, après le nettoyage historique
lui-même borné à dix secondes. Si PostgreSQL n’est plus accessible ou si le
processus est tué, la lease persistante reste disponible pour diagnostic.

## Revalidation, historique et transitions

`SourceCollectionService` conserve la façade HTTP synchrone et la sélection du
Workspace courant. `ISourceCollectionOrchestrator` porte l’orchestration partagée
avec le Worker. Le Worker utilise exclusivement le WorkspaceId du job, y compris
si plusieurs workspaces sont actifs ; il ne consulte pas ICurrentWorkspaceProvider.
Le resolver recharge Workspace, SavedSearch, SourceConfiguration, Pipeline,
PipelineStage et adaptateur. Les modifications valides d’URL/critères sont prises
en compte ; les ressources devenues invalides échouent avant le réseau, sans
SourceExecution artificielle.

Après lecture, l’orchestration calcule le fingerprint existant. L’ingestion garde
la revalidation sous verrou, la normalisation, les transactions/savepoints, la
déduplication, les observations et les provenances de Phase 6. Un changement de
configuration pendant le réseau est rejeté avant toute écriture métier.

L’ID d’exécution est alloué par tentative ; sa persistance et son rattachement au
job se font **dans la même transaction**, avant le savepoint métier de l’ingestion.
L’échec réseau utilise également une transaction pour l’historique et le lien.
Ainsi une SourceExecution persistée reste liée même si le processus s’arrête entre
son commit et la finalisation du job. Aucun contrat d’ingestion ni de lecture
historique n’est changé. Le trigger de l’exécution reprend celui du job.

| Transition | Effets |
| --- | --- |
| queued → running | Claim, AttemptCount +1, StartedAt, token et expiration |
| queued → cancelled | Route existante, FinishedAt, aucun traitement |
| running → succeeded | Exécution persistée succeeded, lien conservé, FinishedAt, erreur et lease nulles |
| running → failed | Code stable, FinishedAt, exécution liée lorsqu’elle existe, lease supprimée |
| running expiré → running | Aucun traitement automatique ni incrément |

La finalisation verrouille le job, vérifie token et Workspace et consulte son
exécution persistée. Un succès déjà commité prime sur une annulation ou erreur
observée juste après le commit. Les erreurs de validation/collecte conservent leur
code (`InactiveResource`, `WorkspaceUnavailable`, `UpstreamTimeout`, etc.). Les
erreurs inattendues de l’orchestration donnent `CollectionInternalError`, celles
du processeur `CollectionWorkerError`. Les exceptions détaillées sont réservées aux
logs structurés ; ErrorCode ne contient jamais l’exception brute. Une impossibilité
de finaliser est journalisée et laisse running pour réconciliation, sans relance.

## Migration Phase 7.2 et tests

`20261007074738_Phase72CollectionWorkerLeases` ajoute uniquement les deux colonnes
nullables et `CK_SourceCollectionJobs_Lease`. Les migrations antérieures sont
inchangées. Les lignes existantes conservent tous leurs champs Phase 7.1. Pour les
éventuels running legacy, la migration génère un token et fixe l’échéance à
StartedAt + une microseconde : normalement déjà expirée, sauf date de départ future
anormale. Aucun job n’est exécuté ni créé. Down enlève seulement la contrainte et
les deux colonnes ; un nouvel Up recrée les tokens legacy sans modifier les autres
champs. Ne pas effectuer le downgrade pendant que des Workers tournent.

Les tests `SourceCollectionClaimTests`, `SourceCollectionWorkerTests` et
`SourceCollectionWorkerMigrationTests` utilisent PostgreSQL 18 réel : claims
concurrents, verrou ignoré, priorité, filtres d’éligibilité, token/Workspace,
expiration sans reprise, course annulation, vrai hosted service, redémarrage,
déduplication, revalidation, changement pendant réseau, arrêt et échec réseau suivi
d’un succès. Ils vérifient les contraintes de lease, l’upgrade depuis Phase 7.1,
la conservation des jobs, Down/Up, l’absence de migrations en attente et de divergence
EF. `SourceCollectionWorkerLifecycleTests` vérifie les options invalides au
démarrage, la désactivation, la libération des scopes et la survie de la boucle
après une exception inattendue journalisée. Les tests existants couvrent aussi
la chaîne vierge complète, `/collect`,
`/ingestions`, l’historique, OpportunitySource et les reconstructions Phases 4 et 6.

## Limites et prochaines tranches

Pas de retry/backoff automatique, pas de création automatique de jobs `retry`, pas
de planification quotidienne, pas de scheduler/cron, pas de n8n opérationnel, pas
d’événements métier, pas d’interface Blazor, scoring, IA ou email. La récupération
des leases expirées, la réconciliation après crash et toute politique de nouvelle
tentative restent à concevoir en Phase 7.3 ou ultérieurement. Un échec ne redevient
jamais queued dans cette version.
