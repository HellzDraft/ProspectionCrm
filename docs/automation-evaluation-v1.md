# Évaluation déterministe des automatisations — Phase 8.3

## Architecture et portée

`événement manuel → dispatcher → AutomationJob ciblé → preview de l’évaluateur → plan`

Le dispatch persiste des travaux ; l’évaluateur produit un diagnostic ; l’exécution
métier reste absente. `AutomationJob` est le travail durable, `AutomationExecution`
est réservé à l’historique d’un véritable traitement futur. Ni dispatch ni preview
ne créent cet historique, de CrmTask, d’ActivityEntry ou d’autre objet métier.
Aucun appel externe, worker, BackgroundService ou consommation périodique n’est ajouté.
La queue et ses API d’administration Phase 8.2 restent indépendantes de l’évaluateur.

`IAutomationEventDispatcher` reçoit un workspace interne de confiance et utilise
`IAutomationJobQueue`. `IAutomationRuleEvaluator` est pur : ses seuls inputs sont
le job, la règle courante et les paramètres courants. Il n’accède ni à EF ni à
l’horloge. `AutomationEvaluationService` charge les données du preview sans suivi EF.

## Catalogues et validation des règles

Le catalogue existant `AutomationJobTriggers` est partagé : seul `manual` est connu,
avec comparaison exacte et sensible à la casse. Aucun alias implicite.

`AutomationActionCatalog` décrit uniquement `create-crm-task`, sa catégorie
`general`, son parseur de configuration et son type `CreateCrmTaskPlan`.
Les catégories `email` et `application` de la policy restent inchangées ; elles
n’ont aucune action V1. La catégorie ne peut pas être choisie par la règle.

Create et Update de `/api/automation-rules` vérifient les deux catalogues, les
conditions, la configuration et l’appartenance du pipeline au workspace courant.
Les propriétés de DTO inconnues sont refusées, dont `workspaceId`. Le contrôle
existant des pipelines archivés est conservé. Les erreurs 400 sont des
ProblemDetails avec un `code` stable. Les définitions JSON sont stockées sous forme
canonique et restent des objets. Limite de parsing : 64 Kio UTF-8 par définition.

Les règles historiques inconnues restent lisibles, sans réécriture automatique.
Leur preview retourne un diagnostic invalide explicite. Leur modification doit
fournir une définition V1 valide. Par cohérence, Restore valide aussi la définition :
une règle historique archivée doit être corrigée par Update avant réactivation.
Archive et la lecture ne demandent pas de définition valide.

## Configuration et plan `create-crm-task`

```json
{"title":"Relancer le contact","description":"Texte fixe","dueInDays":3}
```

- `title` : chaîne obligatoire non blanche, maximum 200 caractères, aligné sur CrmTask.
- `description` : chaîne ou null, optionnelle, maximum 10 000 caractères, aligné sur CrmTask.
- `dueInDays` : entier JSON de 0 à 365 ; absence = aucune échéance. Null, chaîne et fraction refusés.
- Propriétés inconnues, doublons et marqueurs de template `{{` / `}}` refusés.

Les chaînes sont des textes littéraux. Aucun moteur de template, interpolation,
expression ou script n’existe. Le plan contient `actionTypeCode`,
`actionCategoryCode`, `opportunityId`, `title`, `description`, `dueInDays`.
Il ne contient pas de `DueAt` dépendant de l’instant du preview.
`payload.opportunityId` doit être un GUID non vide. L’évaluateur ne consulte pas
Opportunity ; le futur executor devra vérifier existence et workspace avant toute mutation.

## Événement et enveloppe

`POST /api/automation-events` :

```json
{
  "triggerTypeCode": "manual",
  "eventKey": "manual-test-001",
  "pipelineId": null,
  "payload": {
    "opportunityId": "00000000-0000-0000-0000-000000000001",
    "source": "manual"
  }
}
```

Workspace exclusivement résolu par `ICurrentWorkspaceProvider` ; aucun workspace
client n’est accepté. `eventKey` est requis, non blanc, au plus 100 caractères,
sans caractères de contrôle. Il n’est ni tronqué ni normalisé en casse.
Le pipeline facultatif doit appartenir au workspace ; absent ou étranger retourne
le même 404 `pipeline-not-found`. Un GUID vide est invalide. Le dispatch peut
persister pour un pipeline archivé appartenant au workspace ; il ne réactive rien.

Payload omis = `{}` ; s’il est fourni, il doit être un objet JSON, jamais null.
Le ContextJson produit est :

```json
{"eventKey":"manual-test-001","pipelineId":null,"payload":{"opportunityId":"00000000-0000-0000-0000-000000000001","source":"manual"}}
```

Les métadonnées réservées sont distinctes du payload. Une propriété
`payload.pipelineId` ne remplace jamais le scope de l’enveloppe.
L’enveloppe complète canonique est limitée à 16 Kio UTF-8 à l’entrée de la queue,
et son lecteur accepte jusqu’à 64 Kio, limite de stockage Phase 8.2 après jsonb.
L’objet payload est trié par noms de propriétés pour une sérialisation stable ; les
tableaux éventuels conservent leur ordre. Les doublons, caractères NUL, surrogates
invalides et nombres dont l’expansion décimale dépasse 16 Kio sont rejetés avant
PostgreSQL. Un nombre à exposant compact ne peut pas contourner la limite du contexte.
Les propriétés de l’enveloppe sont strictes. Ne jamais fournir de secrets dans le
payload ; celui-ci est persistant et accessible via l’administration des jobs.
Les erreurs API ne renvoient ni payload ni valeur fautive ; les filtres ne les journalisent pas.

## Conditions V1

Null, `{}` ou `{"type":"always"}` correspondent toujours. L’autre forme autorisée :

```json
{"type":"context-equals","property":"source","value":"manual"}
```

`property` nomme directement une propriété du **payload**, au plus 100 caractères,
non blanc, sans contrôle, point ou crochets. Aucun chemin, index, regex, script,
conversion ou comparaison insensible à la casse. `value` est un scalaire JSON :
string, number, bool ou null. Une propriété absente ne correspond pas, même à null.
`1` diffère de `"1"` ; `1`, `1.0` et `1e0` représentent la même valeur numérique.
La comparaison décimale exacte est indépendante de la culture, sans arrondi double.
Les objets/tableaux en tant que valeur attendue, types inconnus, champs supplémentaires
et doublons rendent la définition invalide. Aucun fallback vers `always`.

## Dispatch, scope et idempotence

Les candidats appartiennent au workspace, sont Enabled et non archivés, et ont
le même trigger. Une règle sans PipelineId est globale ; une règle avec PipelineId
exige exactement celui de l’événement. Sans pipeline événement, aucune règle scoped
n’est sélectionnée. Les conditions et configurations ne sont pas évaluées au dispatch.

Un job est produit pour chaque candidat, avec AutomationRuleId obligatoire,
trigger de l’événement, catégorie du catalogue, priorité 50 et disponibilité immédiate.
Une action historique inconnue empêche de dériver une catégorie : le dispatch entier
retourne 409 `unsupported-action` avant toute écriture. Les définitions de conditions
ou configurations historiques invalides seront diagnostiquées au preview.

`TriggerKey = "event:" + EventKey + ":" + AutomationRuleId (format N)`.
La clé est bornée à 139 caractères. L’index PostgreSQL 8.2 sur
`(WorkspaceId, TriggerTypeCode, TriggerKey)` arbitre les doublons, y compris en
concurrence. Même événement/règle = même job ; deux règles = deux jobs ; deux
workspaces sont indépendants. Le premier contexte persistant gagne, même si un
appel ultérieur change payload/pipeline ou si le job est terminal. Une intention
différente exige un nouvel EventKey. Un nouveau candidat peut produire un nouveau job
lors d’un nouveau dispatch du même événement : les candidats sont les règles courantes.

Les règles sont parcourues dans un ordre stable et les enqueues partagent une
transaction courte READ COMMITTED pour éviter un résultat partiellement écrit.
Une clé préoccupée via l’administration par un job non conforme retourne 409
`dispatch-key-conflict` et annule les nouvelles insertions du dispatch. Les deadlocks
ou conflits de sérialisation retournent 409 `concurrent-dispatch-change`, sans retry automatique.

**Le kill switch ne bloque jamais la persistance d’un événement.** Les paramètres
d’automatisation ne sont pas consultés par le dispatcher, même s’ils manquent.
La policy s’applique à l’évaluation ultérieure. Aucun événement n’est consommé automatiquement.

La réponse est toujours 200 pour un dispatch réussi, y compris zéro candidat :
`triggerTypeCode`, `eventKey`, `candidateRuleCount`, `createdCount`, `existingCount`,
`jobs` contenant `automationRuleId`, `jobId`, `isCreated`. Aucun timestamp de réponse.

## Évaluateur et policy

La règle est lue **dans son état courant au traitement**, jamais un snapshot au
moment de l’enqueue. Son état, scope, condition, configuration et les paramètres
courants peuvent changer le résultat d’un preview ultérieur.

L’évaluateur contrôle workspace du job/règle/settings, référence de règle présente
et cohérente, trigger connu/identique, action connue, catégorie du catalogue,
enveloppe, configuration et définition de condition. Il refuse ensuite les règles
archivées/désactivées ou le scope incompatible avant de comparer la condition.
Les définitions historiques invalides restent invalides même si la règle est désactivée.
Après une condition vraie et un opportunityId valide, il appelle strictement
`IAutomationSafetyPolicy` Phase 8.1, avec la catégorie dérivée.

| Paramètres general | Décision | Plan diagnostic |
| --- | --- | --- |
| IsEnabled=false | blocked | absent (la policy interdit aussi la préparation) |
| manual | manual | présent |
| assist | approval-required | présent |
| automatic | automatic | présent |

Un mode invalide reste bloqué par la policy existante. `automatic` n’exécute rien ici.
Quotas et circuit breaker persistés en Phase 8.1 ne sont pas appliqués.

Résultat : `isValid`, `isMatched`, `conditionMatched` nullable, `reasonCode`,
`triggerTypeCode`, `actionTypeCode`, `actionCategoryCode`, `safetyDecision`, `actionPlan`.
La décision imbriquée contient notamment les modes demandé/effectif. Une condition
non évaluée vaut null ; une condition fausse est un non-match valide, sans policy/plan.
Un résultat bloqué peut être valide et matched mais ne contient aucun plan.

Codes stables : `workspace-mismatch`, `automation-rule-required`, `rule-mismatch`,
`unsupported-trigger`, `trigger-mismatch`, `unsupported-action`, `action-category-mismatch`,
`invalid-context`, `invalid-action-configuration`, `invalid-condition`, `rule-archived`,
`rule-disabled`, `pipeline-mismatch`, `condition-not-matched`, `invalid-opportunity-id`,
`eligible-manual`, `eligible-approval-required`, `eligible-automatic` et raisons de
blocage de la policy 8.1, dont `automation-disabled`.

## Preview HTTP et absence de mutation

`GET /api/automation-jobs/{id}/evaluation-preview` charge le job, sa règle et les
settings du workspace courant, puis retourne 200 et le diagnostic (même invalide).
Job absent/étranger ou règle inaccessible : 404 `ResourceNotFound` ; job sans règle :
409 `automation-rule-required` ; settings absents : 409 `AutomationSettingsMissing`
comme Phase 8.1 ; workspace indisponible : 409 `WorkspaceUnavailable`.

Le preview ne claim pas, ne touche ni statut, AttemptCount, UpdatedAt, lease,
CompletedAt ni historique. Il reste consultable pour un job leased ou terminal :
c’est un diagnostic, sans acquisition d’un droit d’exécution. Deux appels sur des
données identiques produisent le même résultat fonctionnel. Le chargement ne verrouille
pas la règle ; la Phase 8.4 devra coordonner l’état courant et la mutation finale.

## Schéma, tests et Phase 8.4

Aucune entité, configuration EF ou migration modifiée. Dernière migration :
`20261008083956_Phase82AutomationJobs`. Les tests PostgreSQL vérifient l’absence de
migration en attente et `HasPendingModelChanges() == false`, ainsi que les limites CrmTask.
Les tests purs couvrent contrats et matrice d’évaluation ; les tests HTTP/PostgreSQL
couvrent validation, isolation, concurrence, atomicité, idempotence et absence d’effets.
La suite existante Phase 7/8.1/8.2 est conservée.

Phase 8.4 pourra ajouter un consumer, recovery, claim, traitement, historique,
validation finale d’Opportunity, exécution interne autorisée et gestion quotas/retries.
Rien de cette boucle, ni approbation persistante, email, IA, calendrier, webhook,
n8n ou frontend, n’est implémenté en 8.3.
