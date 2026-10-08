# File persistante d’automatisation métier — Phase 8.2

`AutomationJob` conserve un travail à traiter. `AutomationExecution` reste
l’historique d’une évaluation/exécution métier réelle : l’enqueue et les transitions
de queue ne créent jamais cet historique. Aucune règle n’est évaluée, aucun
`ConditionJson` n’est interprété, aucune action n’est préparée ou exécutée.

La queue est indépendante de `SourceCollectionJob`, de son worker et de son
scheduler. Elle reprend le principe PostgreSQL de claim atomique de Phase 7,
sans ses tentatives métier, ses retries, son backoff ou ses verrous d’ingestion.
Il n’existe aucun consumer ni BackgroundService d’automatisation en Phase 8.2.

## Schéma

Migration : `20261008083956_Phase82AutomationJobs`.

| Champ | Type PostgreSQL | Contrat |
|---|---|---|
| Id | uuid | PK |
| WorkspaceId | uuid | FK obligatoire vers Workspaces, Restrict |
| AutomationRuleId | uuid nullable | FK composite avec WorkspaceId vers AutomationRules, Restrict |
| TriggerTypeCode | varchar(50) | `manual` uniquement en V1 |
| TriggerKey | varchar(200) nullable | clé d’idempotence, sensible à la casse |
| ActionCategoryCode | varchar(50) | catalogue Phase 8.1 : general, email, application |
| StatusCode | varchar(20) | pending par défaut, cinq codes autorisés |
| Priority | integer | 0 à 100, défaut 50 ; plus élevé = prioritaire |
| AvailableAt | timestamptz | UTC, maintenant par défaut ; passé autorisé |
| LeaseOwner | varchar(100) nullable | jeton opaque généré à chaque claim, interne |
| LeaseExpiresAt | timestamptz nullable | échéance UTC du lease |
| AttemptCount | integer | >= 0, défaut 0, incrémenté au claim |
| ContextJson | jsonb nullable | objet JSON, 16 Kio UTF-8 maximum à l’entrée |
| LastError | varchar(2000) nullable | code public contrôlé, jamais une exception brute |
| CreatedAt | timestamptz | UTC courant à l’insertion |
| UpdatedAt | timestamptz nullable | UTC à chaque transition effective |
| CompletedAt | timestamptz nullable | présent exactement pour les états terminaux |

PostgreSQL impose les statuts, catégories, triggers, bornes de priorité et de
tentatives, la cohérence du lease et de `CompletedAt`, le contexte objet JSON et
les codes d’erreur. Le contexte normalisé par jsonb est borné à 64 Kio en SQL
(marge pour la représentation canonique et ses espaces). Le service refuse les
clés vides, uniquement blanches, avec caractères de contrôle ou de plus de
200 caractères. Une clé n’est ni tronquée ni trimée. Le JSON fourni vide ou
malformé est refusé, ainsi que les valeurs incompatibles avec jsonb, dont U+0000.

Tous les horodatages sont des `DateTimeOffset` / `timestamptz`. L’API normalise
les offsets reçus vers UTC. L’horloge PostgreSQL gouverne l’enqueue par défaut,
le claim, les transitions et la récupération ; aucun test temporel du lease ne
dépend de l’horloge d’un futur worker.

## Idempotence

L’index unique partiel `UX_AutomationJobs_Workspace_Trigger_Key` porte sur
`(WorkspaceId, TriggerTypeCode, TriggerKey) WHERE TriggerKey IS NOT NULL`.
Une clé nulle permet plusieurs travaux indépendants. La même clé peut être
utilisée dans deux workspaces. Les codes et clés sont sensibles à la casse.

Cette identité désigne **un événement logique pour toute sa durée de conservation**,
y compris après completed, failed ou cancelled. Le premier enqueue gagne : un
nouvel appel valide avec la même identité retourne le job existant sans remplacer
sa règle, sa catégorie, sa priorité ou son contexte. Une nouvelle intention exige
une nouvelle clé. Il n’existe pas d’API de suppression ou de réutilisation des clés.

`INSERT ... ON CONFLICT ... DO NOTHING` confie l’arbitrage à PostgreSQL, même
en concurrence. Une lecture distincte en READ COMMITTED voit ensuite le gagnant
commité ; on évite le piège d’une lecture dans le snapshot de l’INSERT concurrent.
Les méthodes de queue sont des unités d’opération courtes, prévues pour un contexte
sans transaction englobante ni modifications EF en attente.

## Claim, lease et recovery

`IAutomationJobQueue` propose Enqueue, Claim, Complete, Fail, Release, Cancel et
Recover. Chaque opération interne reçoit un WorkspaceId de confiance ; aucune
ne traite les jobs d’un autre workspace. Les entités retournées ne sont pas suivies
par EF. Les écritures SQL sont paramétrées.

Le claim sélectionne un seul `pending` avec `AvailableAt <= statement_timestamp()` :

1. Priority décroissante ;
2. AvailableAt croissant ;
3. CreatedAt croissant ;
4. Id croissant.

Un CTE `SELECT ... FOR UPDATE SKIP LOCKED`, suivi d’un UPDATE RETURNING dans
la même instruction, réserve la ligne, passe à `leased`, incrémente AttemptCount,
et renseigne LeaseOwner, LeaseExpiresAt et UpdatedAt. Les candidats déjà verrouillés
sont ignorés : la priorité s’applique parmi les candidats disponibles non verrouillés.

La durée par défaut est **5 minutes**, centralisée dans `AutomationJobQueueOptions`.
Elle est configurable dans le code via IOptions, strictement positive et au plus
d’une heure. Elle n’est ni exposée par l’API ni ajoutée à AutomationRuntimeSettings.
LeaseOwner est une capacité aléatoire renouvelée à chaque claim, pas un nom de
machine réutilisable. Le consumer doit conserver cette valeur et la fournir pour
finaliser ou libérer le job. L’API ne l’expose jamais. Une ancienne tentative ne
peut pas finaliser un job récupéré, même si le même worker le réclame à nouveau.

Complete, Fail et Release verrouillent la ligne correspondante et vérifient
l’expiration après acquisition du verrou, avec `clock_timestamp()` ; un lease
expiré ne suffit plus, même avant recovery. Aucun verrou n’est conservé entre
deux appels de queue, et aucun traitement métier ne se déroule dans une transaction.

Recover sélectionne les leases expirés (`LeaseExpiresAt <= maintenant`) par lots
de 100, avec `FOR UPDATE SKIP LOCKED`, pour le workspace demandé. Il les remet
en pending, efface les deux champs de lease, met UpdatedAt à jour et conserve
AttemptCount, ContextJson et AvailableAt. Il retourne le nombre de lignes modifiées.
Répéter l’appel ne modifie pas les lignes déjà récupérées. Des lignes verrouillées
peuvent être reprises à un appel ultérieur ; zéro ne signifie donc pas qu’aucun
autre traitement concurrent ne détient de lease expiré. Aucun worker n’appelle
automatiquement cette méthode en Phase 8.2.

## Transitions

| Opération | État requis | Résultat |
|---|---|---|
| Enqueue nouveau | aucun | pending |
| Claim | pending, échéance atteinte | leased, nouvelle capacité, tentative +1 |
| Complete | leased, propriétaire attendu, non expiré | completed, CompletedAt, lease et LastError effacés |
| Fail | leased, propriétaire attendu, non expiré | failed, CompletedAt, code LastError, lease effacé |
| Release | leased, propriétaire attendu, non expiré | pending, AvailableAt fourni ou maintenant, lease effacé |
| Recover | leased expiré | pending, lease effacé |
| Cancel | pending | cancelled, CompletedAt |
| Cancel répété | cancelled | succès, aucune date modifiée |

Cancel refuse leased, completed et failed. Pour annuler un lease abandonné,
récupérer d’abord le job puis tenter Cancel ; si un autre consumer gagne le claim,
Cancel échoue proprement. Les autres transitions invalides, mauvais propriétaires,
workspaces étrangers et jobs absents retournent false. Les états terminaux ne sont
jamais réouverts. Il n’y a pas de statut running distinct.

Fail représente un échec définitif de queue. `AutomationJobErrors` n’autorise
que `AutomationJobFailed` et `AutomationProcessingRejected` ; tout texte inconnu,
même long, devient `AutomationJobFailed`. Aucune stack trace, chaîne de connexion,
valeur de contexte ou requête SQL n’est persistée dans LastError. Le DTO applique
également cette normalisation défensive. Aucun retry/backoff automatique n’existe.

## API

Trois routes uniquement :

- `POST /api/automation-jobs` : 201 avec Location si créé ; 200 avec le job existant si dédupliqué.
- `GET /api/automation-jobs` : page `{ offset, limit, total, hasMore, items }`.
- `GET /api/automation-jobs/{id}` : détail, ou 404 si absent/hors workspace.

Exemple de corps POST :

```json
{
  "automationRuleId": null,
  "triggerTypeCode": "manual",
  "triggerKey": "manual:test-001",
  "actionCategoryCode": "general",
  "priority": 50,
  "availableAt": "2026-10-08T12:00:00Z",
  "contextJson": "{\"reason\":\"queue-test\"}"
}
```

Seuls triggerTypeCode et actionCategoryCode sont requis. Les cinq autres champs
sont optionnels. Les propriétés inconnues, dont WorkspaceId, sont refusées.
Le provider `ICurrentWorkspaceProvider` détermine exclusivement le workspace de
tous les endpoints ; un paramètre de query WorkspaceId ne change pas sa sélection.
La V1 exige exactement un workspace actif, sinon 409 `WorkspaceUnavailable`.

Une règle optionnelle doit exister dans ce workspace, sinon 404
`AutomationRuleNotFound` (même réponse pour absente et étrangère). Sa FK composite
empêche également le contournement par SQL. Référencer une règle désactivée ou
archivée conserve seulement une référence ; son éventuelle éligibilité appartient
au futur moteur. Les suppressions des ressources référencées sont Restrict.

La liste accepte `status`, `automationRuleId`, `triggerType`, `actionCategory`,
`offset` (>= 0, défaut 0), `limit` (1..200, défaut 50). Son ordre est CreatedAt
décroissant puis Id décroissant. Comme pour la collecte, count et page sont des
lectures distinctes : une insertion concurrente peut faire évoluer la pagination.

Erreurs `application/problem+json` avec extension `code` : 400 `InvalidRequest`,
`InvalidTriggerTypeCode`, `InvalidActionCategoryCode`, `InvalidPriority`,
`InvalidTriggerKey`, `InvalidAvailableAt`, `InvalidContextJson`, `InvalidPagination`
ou `InvalidStatusCode` ; 404 `ResourceNotFound`/`AutomationRuleNotFound` ;
409 `WorkspaceUnavailable`/`ConcurrentAutomationJobChange` ; 500
`AutomationJobInternalError` pour une défaillance inattendue. Les erreurs ne
recopient ni les valeurs invalides ni les diagnostics SQL. Le contexte n’est
pas ajouté aux logs applicatifs ; ne pas activer les logs EF de données sensibles.

Ne pas mettre de secrets dans ContextJson : il est conservé et lisible dans
l’API d’administration. Les opérations internes de lease/finalisation/récupération
ne sont pas exposées par HTTP. L’API expose LeaseExpiresAt mais pas LeaseOwner.

## Index et migrations

Outre PK et déduplication, les index servent au claim (workspace, priorité
décroissante, disponibilité, création, id ; pending uniquement), à la recovery
(workspace, expiration ; leased uniquement), au lookup de règle composite et à
la liste chronologique. Aucun index général sur chaque filtre n’est ajouté.

L’Up crée AutomationJobs et la clé alternative `(WorkspaceId, Id)` des règles.
Il ne crée aucun job pour les données existantes. Le Down supprime la queue et
cette clé alternative ; règles, paramètres et historiques restent intacts.
Le contenu de la queue supprimée n’est pas récupéré par un nouvel Up. Les tests
couvrent une base vierge, l’upgrade depuis 8.1, Down/Up, les migrations appliquées
et `HasPendingModelChanges() == false`. Aucune migration historique n’est modifiée.

## Sécurité Phase 8.1 et suite

**Persister un événement ne vaut pas autorisation d’action.** Enqueue, claim et
les transitions de queue n’évaluent pas AutomationRuntimeSettings et restent
possibles avec IsEnabled=false : on ne perd pas les événements pendant l’arrêt.
Le lease n’est jamais une autorisation de préparer ou d’exécuter une action.

Tout futur consumer devra consulter les paramètres courants et
`AutomationSafetyPolicy` au moment du traitement : aucune préparation/exécution
avec kill switch désactivé. La politique Phase 8.1 reste la source de vérité.
Les catégories inconnues sont refusées, jamais rabattues sur general.

La Phase 8.3 pourra définir la production des événements, étendre les triggers et
introduire un traitement contrôlé des règles avec application effective de cette
politique. Son contrat devra préciser l’évaluation, les historiques réels et les
éventuels retries ; ces fonctions ne sont pas annoncées opérationnelles ici.
En Phase 8.2 : ni e-mail, candidature, proposition, IA, n8n, approbation humaine,
quota appliqué, disjoncteur métier, nouveau frontend ni effet externe.
