# File persistante, Worker et planification — Phases 7.1 à 7.4

`SourceCollectionJob` est une commande persistante demandant une collecte future.
`SourceExecution` est l'historique d'une tentative réellement commencée. L'enqueue
ne crée aucune exécution, observation, opportunité ou provenance, et ne réutilise
pas `AutomationExecution`.

La Phase 7.2 ajoute un Worker .NET optionnel, le claim atomique et les leases.
La Phase 7.3 ajoute retry, backoff persistant et réconciliation.
La Phase 7.4 ajoute la [planification quotidienne UTC](source-collection-scheduling-v1.md)
et la création atomique de jobs `scheduled` par un scheduler distinct.
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
`running`, `succeeded` et `failed`. Le scheduler 7.4 produit `scheduled` ; `event` et `retry`
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

## Job logique, tentatives et exécution

Le retry réutilise **le même job**. `AttemptCount` augmente uniquement lors du
claim ; `EnqueuedAt` et `TriggerTypeCode` gardent leur origine. Un job manual reste
manual, comme ses SourceExecution ; un job scheduled reste scheduled. Le numéro de tentative distingue les reprises ;
aucun nouveau job `retry` n’est créé et aucune deuxième file n’existe.

`SourceCollectionJobAttempts` contient une ligne par claim : clé `(JobId,
AttemptNumber)`, WorkspaceId, dates, résultat, code contrôlé, statut HTTP distant
éventuel et SourceExecutionId facultatif. Cette entité conserve aussi les claims
échoués en prévalidation et ceux interrompus avant création d’une exécution.
Elle ne recopie ni entrées métier, ni compteurs, ni snapshots, ni observations.
La SourceExecution reste l’historique métier de la collecte. Les FK composées
protègent le Workspace ; un index unique empêche de réutiliser une exécution.

Le pointeur `SourceCollectionJob.SourceExecutionId` conserve son contrat actuel :
exécution courante/terminale lorsqu’elle existe, null en queued. En cas de retry,
les anciens liens restent dans les tentatives et aucune exécution n’est supprimée.
Les routes/DTO existants ne changent pas ; la relation complète est consultable en
base, et les exécutions par les routes historiques existantes. Exemple interne :

```sql
SELECT "JobId", "AttemptNumber", "SourceExecutionId", "StatusCode", "StartedAt",
       "FinishedAt", "ErrorCode", "UpstreamStatusCode"
FROM "SourceCollectionJobAttempts"
WHERE "JobId" = '<job-id>' ORDER BY "AttemptNumber";
```

## Claim, propriété et protection des écritures

Le claim reste une instruction PostgreSQL atomique : candidat queued disponible,
tri `AvailableAt ASC, EnqueuedAt ASC, Id ASC`, `FOR UPDATE SKIP LOCKED`, puis
`UPDATE ... RETURNING` et insertion de la tentative dans des CTE. Un échec de
l’insertion annule aussi le claim. L’horloge PostgreSQL fait autorité. Le claim
exclut les jobs ayant déjà atteint MaxAttempts. Aucun incrément hors claim.

Un contrôle advisory transactionnel évite de consommer une tentative si la session
de l’ancien traitement détient encore le verrou du job. Dans ce cas, aucun claim
n’est retourné et le prochain cycle peut réessayer. Les lignes verrouillées par
une transaction restent ignorées grâce à SKIP LOCKED.

Après claim, le processeur acquiert un **verrou advisory de session par JobId**,
puis revérifie état, Workspace et token avant d’appeler l’orchestration. Ce verrou
est conservé jusqu’à la fin du traitement/nettoyage. C’est une évolution nécessaire
par rapport à 7.2 : une connexion PostgreSQL dédiée, non poolée, reste ouverte
pendant le réseau, **sans transaction ouverte ni verrou de ligne pendant le fetch**.
La fermeture de cette connexion libère le verrou. Les lectures EF demeurent
matérialisées, sans transaction réseau. Une collision du hash 64 bits du verrou
ne peut que retarder du travail, jamais autoriser un traitement concurrent.

La session n’est pas une preuve absolue de vie : elle peut être perdue pendant que
le processus poursuit une requête RSS. La protection finale reste le token :
**avant toute écriture métier**, dans la transaction d’ingestion, le rattachement
vérifie JobId, Workspace, état running et token ; il verrouille le job jusqu’au
commit. L’exécution et les deux liens job/tentative sont persistés atomiquement,
avant le savepoint métier. Un ancien propriétaire dont le token a été révoqué ne
peut ni ingérer, ni rattacher une exécution, ni finaliser/requeue. Une transaction
active ayant déjà verrouillé le job est ignorée par la réconciliation.

En cas de perte de session, des lectures HTTP anciennes peuvent encore se terminer
après reprise ; aucune garantie « exactly once » sur les appels réseau n’est
revendiquée. Le transport actuel effectue des lectures RSS. Les écritures métier
restent protégées par le token et la déduplication existante. Un futur adaptateur
ayant des effets externes devra fournir sa propre idempotence.

## Options et backoff persistant

```json
"SourceCollectionWorker": {
  "Enabled": false,
  "IdleDelaySeconds": 5,
  "LeaseDurationSeconds": 300,
  "MaxAttempts": 3,
  "InitialRetryDelaySeconds": 60,
  "MaxRetryDelaySeconds": 900
}
```

Validation au démarrage : IdleDelaySeconds 1–300, LeaseDurationSeconds 30–3600,
MaxAttempts 1–10, InitialRetryDelaySeconds 1–3600, MaxRetryDelaySeconds compris
entre le délai initial et 86400. Les variables d’environnement utilisent le
préfixe `SourceCollectionWorker__`. Les options sont lues au démarrage et doivent
être cohérentes entre instances. Worker désactivé par défaut dans tous les environnements.

Après l’échec de la tentative n, le délai vaut :

`min(MaxRetryDelaySeconds, InitialRetryDelaySeconds × 2^(n−1))`.

Valeurs par défaut : 60 s après le premier échec, 120 s après le deuxième ; le
troisième est terminal. Avec une limite supérieure, les délais suivants sont
240, 480, puis 900 secondes au maximum. Pas de jitter ni de Retry-After externe.
Le délai est calculé une fois lors de la décision, sous verrou, et la disponibilité
est persistée : `AvailableAt = max(heure PostgreSQL, AvailableAt précédent) + délai`.
Aucun timer de plusieurs minutes ni état de retry ne vit en mémoire.

Un échec retryable requeue seulement si `AttemptCount < MaxAttempts`. Sinon le
job termine failed en conservant la cause. Si MaxAttempts est abaissé pendant un
backoff, un job désormais épuisé termine, lorsqu’il devient disponible, avec
`CollectionAttemptsExhausted`, sans claim, réseau ni tentative supplémentaire.
Dans ce cas, StartedAt et FinishedAt du job datent cette clôture administrative ;
les dates de la dernière tentative restent dans son historique.

## Classification centralisée des erreurs

`SourceCollectionRetryPolicy` est l’unique politique de décision. Le statut HTTP
exposé par l’API ne décide jamais seul d’un retry.

| Cause | Politique |
| --- | --- |
| UpstreamTimeout, UpstreamTransportError, DnsResolutionFailed | Retry possible |
| UpstreamHttpError avec statut distant 408, 429, 500, 502, 503 ou 504 | Retry possible |
| CollectionWorkerStopping, CollectionLeaseExpired, CollectionCancelled, RequestCancelled | Interruption d’un traitement lié à un job : retry possible |
| CollectionAbandoned | Aucun résultat durable après expiration : retry possible |
| UpstreamHttpError avec tout autre statut ou statut absent | Terminal |
| UpstreamTlsFailure, UnsafeFeedUrl, UnsafeRedirect, UnsafeResolvedAddress | Terminal |
| InvalidRequest, InvalidBatch, WorkspaceUnavailable, ResourceNotFound, InactiveResource, WrongPipeline | Terminal |
| UnsupportedSourceType, MissingFeedUrl, InvalidRssCriteria, InvalidFeed, NoUsableFeedEntries | Terminal |
| TooManyRedirects, ResponseTooLarge, UnsupportedContentType | Terminal |
| AmbiguousIdentity, MissingPersistentIdentity, ConcurrentIdentityChange, PersistenceFailure, CollectionConfigurationChanged | Terminal |
| CollectionInternalError, CollectionWorkerError, CollectionRecoveryAmbiguous, CollectionAttemptsExhausted | Terminal |
| Toute erreur inconnue ou cause absente sur une exécution échouée | Terminal, normalisée en CollectionWorkerError |

Le statut HTTP distant est sauvegardé avec le lien historique dans la même
transaction. Une ancienne exécution `UpstreamHttpError` sans statut distant est
conservativement terminale lors de la récupération. Une erreur interne HTTP 500
n’est donc jamais automatiquement retryable. Aucune stack trace, URL, SQL ou
réponse brute n’est copiée dans les nouveaux ErrorCode ; les détails techniques
restent dans les logs structurés. Les données historiques legacy restent intactes.

## Finalisation et réconciliation

`SourceCollectionJobFinalizer` partage la décision entre fin normale et recovery.
Le propriétaire est contrôlé avec un verrou de ligne et son token. La tentative
est terminée, puis le job finalisé ou remis en attente dans la même transaction.
Un succès durable déjà commité prime sur une annulation observée ensuite.

| Transition | Effets |
| --- | --- |
| queued → running | AttemptCount +1, StartedAt, token/expiration, nouvelle tentative |
| running → queued | Backoff persisté ; dates de traitement, pointeur d’exécution, erreur et lease remis à null ; tentative et exécution précédentes conservées |
| running → succeeded | Exécution réussie liée, FinishedAt, erreur et lease nulles |
| running → failed | Cause stable, FinishedAt, lease supprimée, lien d’exécution conservé s’il existe |
| queued → cancelled | Route d’annulation existante, y compris pendant le backoff |

`ISourceCollectionJobRecovery` s’exécute avant le claim dans chaque cycle du
processeur. Il traite au maximum huit jobs queued devenus épuisés, puis examine
au maximum huit running expirés, triés par expiration puis Id. La sélection et
les transitions sont dans une transaction courte avec `FOR UPDATE SKIP LOCKED`.
Chaque running nécessite aussi un `pg_try_advisory_xact_lock` sur la même clé que
le propriétaire. Aucun réseau n’est effectué dans la réconciliation.

| État durable retrouvé | Décision |
| --- | --- |
| Lease non expiré | Aucun changement |
| Session de traitement encore verrouillée | Aucun changement, même si le lease a expiré |
| A — SourceExecution cohérente succeeded | Finaliser succeeded, sans nouveau traitement |
| B — SourceExecution failed/cancelled | Classifier sa cause durable et le statut HTTP enregistré ; retry/backoff ou failed selon MaxAttempts |
| C — Aucune SourceExecution, tentative cohérente | CollectionAbandoned, retry/backoff borné ou failed ; ancien token révoqué atomiquement |
| D — Exécution running/partial, historique incohérent ou manquant, références incompatibles | Journaliser et terminer failed avec CollectionRecoveryAmbiguous ; conserver les données pour diagnostic, sans retry |

La révocation du token, le résultat de tentative et le requeue sont atomiques.
Deux réconciliateurs ne décident pas deux fois pour le même état running. Une
session réellement bloquée reste protégée jusqu’à sa fermeture/détection de perte
par PostgreSQL ; l’expiration seule n’autorise jamais à ignorer cette protection.
Le batch est volontairement petit ; des sessions bloquées parmi les huit premiers
expirés peuvent retarder la réconciliation des suivants.

## Cycle, arrêt, redémarrage et annulation

Le BackgroundService reste singleton avec un scope DI par cycle. Le processeur
réconcilie, claim puis traite ; un cycle sans progression attend IdleDelaySeconds.
Une erreur inattendue est journalisée et ne tue pas la boucle. Aucune route publique
`/retry`, `/recover`, `/reconcile` ou `/process-next` n’est ajoutée.

Le lease est fixe, sans heartbeat. Un budget monotone déduit la durée du claim de
LeaseDurationSeconds et annule coopérativement le traitement. Le nettoyage
historique et la finalisation ont chacun un délai indépendant de dix secondes.
Le même propriétaire peut finaliser après expiration si son token est toujours
valide ; une perte de propriété empêche toute finalisation tardive.

L’arrêt pendant une tentative produit CollectionWorkerStopping (ou
CollectionLeaseExpired pour le budget dépassé) et peut remettre le job en backoff.
En cas de crash avant finalisation, le prochain processus inspecte l’état durable.
Un job déjà en backoff conserve exactement AvailableAt au redémarrage, sans
recalcul ni nouvelle attente en mémoire.

L’utilisateur ne peut annuler que queued. Si `/cancel` arrive avant le requeue,
running retourne 409 ; après le requeue, l’annulation retourne 204. Une répétition
sur cancelled reste idempotente. Aucun ancien token ne peut ressusciter cancelled.

## Migration et validation Phase 7.3

`20261007083908_Phase73CollectionRetries` ajoute la table des tentatives et la clé
alternative `(WorkspaceId, Id)` du job. Les migrations 7.1 et 7.2 sont inchangées.
Les jobs et SourceExecution existants restent inchangés. L’upgrade reconstitue
uniquement la tentative connue d’un job ayant commencé : numéro AttemptCount,
exécution liée et dates disponibles. Le numéro 0 identifie un numéro legacy inconnu ;
les nouveaux claims commencent à 1. Aucun passé non connu n’est inventé. Les codes
legacy recopiés sont contrôlés, sinon CollectionWorkerError ; les originaux restent
dans les anciennes lignes.

Down supprime les métadonnées de tentative et la clé ajoutée, jamais les jobs ou
SourceExecution. Un nouvel Up ne peut reconstruire que le dernier lien connu du
job : il ne restaure pas l’association des anciens retries perdue par le downgrade.
Effectuer les migrations avec les workers arrêtés, sans mélanger versions 7.2 et
7.3 actives. L’application n’applique toujours aucune migration au démarrage.

Les suites retry, politique et migration couvrent PostgreSQL réel, backoff/restart,
limite de tentatives, classification HTTP, prévalidation, tous les cas de recovery,
réconciliateurs concurrents, ancien propriétaire, session encore active, perte de
session et verrou avant reprise, annulation, upgrade préservant les données et
Down/Up. Les tests de claim et Worker 7.2 restent exécutés ; les scénarios à une
seule tentative configurent explicitement MaxAttempts=1. Reconstruction vierge,
GetPendingMigrations vide et HasPendingModelChanges=false sont vérifiés avec les
suites historiques complètes.

## Planification — Phase 7.4

Le scheduler transforme uniquement une échéance persistée en job `scheduled`.
Il ne résout aucun adaptateur et ne fait aucun appel réseau. Le Worker conserve
la validation complète, la collecte, l'ingestion et la gestion des tentatives.
`AvailableAt` représente la disponibilité d'un job/retry ; `NextCollectionAt`
représente la prochaine occurrence quotidienne de la recherche. Ce sont deux
horloges persistées indépendantes. Un échec Worker ne remet jamais l'échéance
précédente à disposition du scheduler.

La transaction du scheduler verrouille la SavedSearch avec `FOR NO KEY UPDATE SKIP LOCKED`,
revérifie sa configuration et crée le job avec `ON CONFLICT ... DO NOTHING` ciblant
l'index actif existant. Elle avance la date même si un job manuel ou planifié
identique est déjà queued/running. Aucun jour manqué supplémentaire n'est accumulé.
L'API manuelle conserve son 202/409 et tous les DTO/routes de jobs restent inchangés.

Voir [Planification des collectes V1](source-collection-scheduling-v1.md) pour les
contrats, la suspension, les migrations et la stratégie multi-instance.

## Hors Phase 7.4

Pas de cron libre, de fuseau horaire utilisateur, de calendrier ouvré, de n8n
opérationnel, d'événements métier automatiques, de Blazor, IA, scoring ou email.
Pas de heartbeat, d'API de réparation des états ambigus ni de garantie d'unicité
des effets externes.
