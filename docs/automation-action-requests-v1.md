# Demandes d’action et décisions humaines — Phase 8.5

> Extension Phase 8.6 : la [supervision](automation-supervision-v1.md) compte les
> demandes et partage leur helper IsStale avec l’API. Un snapshot invalide est
> désormais signalé stale, en plus des changements de définition. Les effets
> humains continuent d’ignorer les quotas et le circuit automatiques.

Migration : `20261009115938_Phase85AutomationActionRequests`.
Le worker reste désactivé par défaut. Les migrations ne sont jamais appliquées
au démarrage ; aucune migration automatique de la base Development.

## Flux et modèle

Une évaluation valide, matched et dotée d’un plan, sous décision `manual` ou
`approval-required`, crée une `AutomationActionRequest`. Le code de décision
est conservé : une action détectée en mode manual reste distinguée d’une
proposition préparée en mode assist. Les deux exigent une décision explicite.

La demande et le passage du job de `leased` à `awaiting-approval` partagent
une transaction courte, le verrou d’automatisation du workspace et la vérification
du lease après les attentes. L’INSERT utilise `ON CONFLICT DO NOTHING` et un
index PostgreSQL unique sur AutomationJobId. Il n’existe qu’une demande par job.
L’attente ne crée ni tâche ni AutomationExecution et ne consomme aucun quota.

Le job en attente n’a ni lease ni CompletedAt ni LastError. Claim sélectionne
toujours uniquement les jobs pending disponibles. Recovery ignore l’attente.
Cancel reste limité aux jobs pending et ne peut pas remplacer un rejet humain.
Les transitions dédiées sont dans AutomationActionRequestStore ; l’interface
de queue Phase 8.2 reste compatible.

Les demandes ont les statuts `pending`, `approved`, `rejected`, `cancelled`.
Elles représentent une décision, jamais un résultat d’exécution. Les FK composites
protègent les liens workspace/job et workspace/règle. La FK des executions vers
les demandes porte aussi sur le job et la règle : un historique ne peut pas être
rattaché à la demande d’un autre job. Les suppressions sont Restrict.

## Snapshot et obsolescence

ActionPlanJson est un objet jsonb borné à 64 Kio contenant opportunityId, title,
description et dueInDays. Le parseur refuse les propriétés inconnues, doublons,
GUID vide, textes invalides, templates et délais hors 0–365. Une échéance null
signifie aucune échéance. Le plan utilise les limites et la validation du catalogue
create-crm-task. Il ne recopie ni l’enveloppe du job ni la configuration complète.
Aucune API ne permet de modifier ce snapshot.

RuleFingerprint est un SHA-256 hexadécimal minuscule de 64 caractères. Il porte
sur TriggerTypeCode, PipelineId, ConditionJson canonique, ActionTypeCode et
ActionConfigurationJson canonique. La canonicalisation JSON existante trie les
propriétés et normalise les nombres sans arrondi. Name, Description, Enabled,
ArchivedAt et les dates ne participent pas au hash. Enabled et ArchivedAt sont
contrôlés séparément.

GET calcule IsStale par comparaison avec la définition courante. Une définition
illisible ou absente est stale. Le plan retourné est structuré et validé ; un
snapshot corrompu est représenté par un plan null, jamais par son JSON brut.

Approve refuse une définition changée avec 409 ActionRequestStale, sans mutation.
Reject reste possible si la règle est stale, désactivée ou archivée.
Après approbation, une modification métier de la règle produit un historique
humain skipped, raison `action-request-stale`, puis un job completed. La demande
reste approved. Une nouvelle intention exige un nouvel événement et un nouveau job.

## API

Toutes les routes utilisent exclusivement ICurrentWorkspaceProvider.

| Route | Contrat |
| --- | --- |
| GET /api/automation-action-requests | Liste paginée, sans mutation |
| GET /api/automation-action-requests/{id} | Détail, 404 si absent ou étranger |
| POST /api/automation-action-requests/{id}/approve | Décision approved + job pending disponible immédiatement |
| POST /api/automation-action-requests/{id}/reject | Décision rejected + job cancelled |

Filtres : status, decisionRequirement, automationRuleId, automationJobId,
actionType, offset (défaut 0), limit (défaut 50, entre 1 et 200). Ordre :
RequestedAt décroissant puis Id décroissant. La réponse de liste contient
offset, limit, totalCount, hasMore, items.

Body des décisions :

```json
{"decisionNote":"Validé après vérification."}
```

decisionNote est facultative, limitée à 2 000 caractères ; une chaîne blanche
devient null. Les autres propriétés sont refusées, notamment workspaceId,
decidedByUserId, statusCode, actionPlanJson et les identifiants de job/règle.
Ne pas y inscrire de secrets. En V1 sans authentification multi-utilisateur,
**l’acteur enregistré côté serveur est Workspace.OwnerUserId**. Le champ nullable
et sa FK UserAccount permettent une future identité authentifiée.

Approve/reject verrouillent workspace, demande et job dans une transaction.
Approve verrouille aussi la règle en lecture partagée jusqu’au commit.
Une réponse 200 retourne la demande. Répéter la même décision retourne 200 sans
changer note/dates ni remettre le job en file. Une décision opposée ou une demande
cancelled donne 409 ActionRequestAlreadyDecided. Deux décisions concurrentes
opposées ont un seul gagnant. Aucun de ces endpoints n’appelle l’executor ni ne
crée une AutomationExecution ou un CrmTask.

Erreurs ProblemDetails contrôlées : InvalidRequest, InvalidDecisionNote,
InvalidPagination, InvalidStatusCode, InvalidDecisionRequirement,
InvalidActionTypeCode, ResourceNotFound, WorkspaceUnavailable,
ActionRequestAlreadyDecided, ActionRequestStale, ActionRequestUnavailable,
ActionRequestJobStateConflict, ConcurrentActionRequestChange et
AutomationActionRequestInternalError. Aucune exception brute, SQL ou valeur
sensible n’est recopiée dans les réponses ou logs.

## Exécution, quotas et retries

Le worker reconnaît la demande approved avant de considérer le mode courant.
Il utilise exclusivement son plan snapshot. Il revalide sous les verrous Phase 8.4
le workspace, job, règle, fingerprint, codes et plan ; l’executor verrouille et
vérifie l’Opportunity active du workspace. Les modes manual, assist et automatic
n’imposent pas de seconde approbation. Une demande pending n’est jamais approuvée
implicitement lors d’un changement de mode.

Le kill switch bloque préparation et effets, mais pas la décision humaine.
Une approbation pendant IsEnabled=false est enregistrée ; le job pending est
ensuite différé par le worker, sans accumulation d’historiques si le blocage
existe dès le contrôle initial. Un changement tardif après le running produit
le diagnostic deferred existant, lié à la demande humaine.

Les executions ajoutent AutomationActionRequestId et IsHumanApprovedAttempt.
Les DTO exposent aussi IsAutomaticAttempt ; la liste accepte le filtre
automationActionRequestId. Les deux origines sont exclusives. Les effets humains
succeeded portent EffectApplied=true et OutcomeSequence=null. Les erreurs métier
sont skipped + completed, les erreurs techniques suivent MaxAttempts, la
classification transitoire et le backoff Phase 8.4. Chaque tentative référence
la même demande, qui reste approved même après échec terminal.

Les quotas comptent explicitement IsAutomaticAttempt ET EffectApplied. Le circuit
parcourt seulement les outcomes automatiques. Un effet humain contourne un quota
atteint ou un circuit ouvert, ne les consomme pas et ne remet pas le circuit à
zéro. Un échec humain ne l’ouvre pas. Le kill switch reste obligatoire.

L’unicité CrmTask.AutomationJobId et la preuve d’effet unique par job demeurent.
Après crash entre effet committé et completion, le checkpoint succeeded termine
le job au retry, même si la tâche a été supprimée. Une tâche déjà liée sans
historique est adoptée avec effect-already-applied et la bonne origine humaine.
Les abandons running sont réconciliés avec leur lien et leur origine humaine.

## Adaptations ciblées et migrations

Le runtime 8.4 persiste un running avant sa transaction finale. Si le mode devient
manual/assist entre ces deux transactions, aucun executor n’a encore été appelé :
ce seul checkpoint provisoire est retiré atomiquement avec la création de la
demande et le passage en attente. Aucun ancien skipped n’est supprimé ou converti.
Les demandes créées directement depuis un mode humain ne créent aucun running.

Up crée la table vide, étend les contraintes et ajoute des métadonnées nullable/
false à l’historique. Aucun backfill des skipped ou des jobs completed. Les jobs
pending existants sont traités normalement à leur prochain claim.

Arrêter les workers avant migration ou downgrade et ne pas mélanger des versions
runtime incompatibles. Down remet les jobs awaiting-approval en pending, supprime
les demandes et leurs métadonnées d’execution, puis rétablit les contraintes 8.4.
Il ne supprime aucun job, règle, historique, tâche ou workspace.

**Écart nécessaire pour Down :** la contrainte 8.4 exige qu’un effet appliqué soit
automatique avec OutcomeSequence. Les succès humains ne peuvent satisfaire ce
contrat. Down efface donc leur seul indicateur EffectApplied, conserve leur statut
succeeded et ne les requalifie pas en automatiques. Le checkpoint succeeded et
l’unicité des tâches préservent la reprise sans duplication. Les données 8.4
restent inchangées ; les décisions et métadonnées humaines supprimées ne sont
pas reconstituées par Up. Un rollback peut remettre des intentions en file :
réexaminer paramètres et jobs avant de réactiver un ancien worker.

Hors périmètre : frontend, authentification, nouvelles actions, effets externes,
email, candidatures, calendrier, IA, n8n et webhook.

## Audit et validation du 9 octobre 2026

Départ : dépôt HellzDraft/ProspectionCrm, branche main, HEAD
`4df7d247e52199a60913f5c544f1932b9b00f7ad`, commit
« Add persistent automation worker and controlled CRM task execution ».
Working tree propre avant modification. [GitHub Actions Build #52](https://github.com/HellzDraft/ProspectionCrm/actions/runs/37822691996)
vérifié completed/success pour ce commit.

Le build Release initial a réussi avec deux avertissements CS8618 préexistants
dans Settings.razor (UserName et Email). La première suite a découvert exactement
1 050 tests : 339 réussites et 711 erreurs d’infrastructure, Docker étant arrêté.
Après démarrage de Docker Desktop, la référence complète non filtrée a réussi :
**1 050 réussites, 0 échec, 0 ignoré, 28 min 52 s**, avant toute modification source.
Rapport : `TestResults/phase85-baseline-docker.trx`.

L’audit a couvert settings, policy, règle, évaluateur et plan, queue et statuts,
processor/store/worker/options, historique et DTO, CrmTask et configuration,
Workspace/UserAccount, DbContext, Program, migration 8.4, documentation et tests
de concurrence, crash, quotas et idempotence. Les incompatibilités ciblées sont
décrites plus haut : contraintes d’origine des effets, downgrade et changement
tardif du mode après le checkpoint running.

### Tests Phase 8.5

| Classe nouvelle | Cas |
| --- | ---: |
| AutomationActionSnapshotTests | 18 |
| AutomationActionRequestTests | 21 |
| AutomationActionRequestApiTests | 16 |
| AutomationActionRequestResilienceTests | 12 |
| AutomationActionRequestRevalidationTests | 5 |
| AutomationActionRequestSchemaTests | 16 |
| AutomationActionRequestMigrationTests | 1 |
| Total nouveau | 89 |

Les deux anciens cas exigeant un skipped définitif en manual/assist sont remplacés
par les nouveaux contrats d’attente ; les tests du changement tardif de mode sont
adaptés. Le reste des modifications historiques concerne le nom de dernière
migration, l’exclusion des nouvelles métadonnées dans les snapshots antérieurs,
la table nouvelle vide et l’ordre de la chaîne de migrations. Aucun code métier
de collecte n’est modifié.

Les scénarios comprennent décisions opposées concurrentes, ancien owner/lease
expiré, absence de tâche/historique avant décision, rejet sans historique,
idempotence des décisions, snapshot inchangé, fingerprint stale, plan corrompu,
kill switch initial/tardif, mode global, retries humains, quotas et circuit
automatiques, effet committé avant crash, suppression de tâche, adoption,
rollback avant/après commit de demande et d’approbation, FK multi-workspace/job,
contraintes directes et downgrade avec demandes et effets humains persistés.

Les validations ciblées ont donné 49 puis 147 réussites. La passe élargie aux
migrations historiques a identifié les assertions de structure/ordre à actualiser.
Après correction, les 28 cas de SourceCollectionJobMigrationTests réussissent
(`TestResults/phase85-migration-regression.trx`). La première passe complète après
implémentation a été interrompue après l’assertion historique de table nouvelle,
puis remplacée par une nouvelle passe complète sur les fichiers corrigés.

Commandes de validation finale :

```powershell
dotnet build ProspectionCrm.slnx -c Release --no-restore
dotnet test ProspectionCrm.slnx -c Release --no-build --logger 'trx;LogFileName=phase85-full-final.trx' --results-directory TestResults -- xUnit.MaxParallelThreads=4
dotnet ef migrations has-pending-model-changes --project src/ProspectionCrm.Api --startup-project src/ProspectionCrm.Api --configuration Release --no-build
git diff --check
git status --short
```

EF CLI 10.0.10 signale sa version antérieure au runtime 10.0.12, mais le contrôle
du modèle réussit. Les bases vierges et cycles Up/Down/Up sont exclusivement des
PostgreSQL 18 jetables Testcontainers. Aucun accès de migration à Development.

### Points de livraison demandés

| Point | Résultat |
| --- | --- |
| 27 — AutomationExecution | AutomationActionRequestId, IsHumanApprovedAttempt, FK composite workspace/job/règle/demande, origines exclusives ; DTO enrichis et filtre request |
| 28 — Quotas | IsAutomaticAttempt ET EffectApplied ; effets humains exclus, y compris adoption et replay |
| 29 — Circuit | Outcomes automatiques uniquement ; succès humain sans reset, échec humain sans ouverture |
| 30 — Retries humains | MaxAttempts/backoff/classification 8.4, même snapshot et demande approved, plusieurs historiques liés |
| 31 — Crash recovery | Atomicité demande/job et décision/job ; lease fencing, rollback de mutation, reprise du checkpoint sans duplication |
| 32 — Tests ajoutés | 89 cas ; deux anciens cas transitoires remplacés, régressions de migrations adaptées |
| 33 — Total final | 1 137 réussites, 0 échec, 0 ignoré ; suite complète sans filtre, 29 min 03 s |
| 34 — Build Release | Réussi, 0 avertissement, 0 erreur sur les fichiers finaux |
| 35 — Migrations / modèle | Migration 8.5 dédiée, historiques inchangées ; bases vierges, upgrade 8.4 et Down/Up peuplé testés ; HasPendingModelChanges=false |
| 36 — git diff --check | Réussi |
| 37 — git status --short | Modifications locales uniquement ; aucun fichier staged ; inventaire ci-dessous |
| 38 — Avant approbation | Aucun CrmTask créé par le chemin humain |
| 39 — API approve | Enregistre la décision et remet en file ; n’exécute pas l’action |
| 40 — API reject | Aucune AutomationExecution ; demande rejected et job cancelled atomiquement |
| 41 — Effets humains | Aucun quota automatique consommé |
| 42 — Chemin automatic | Conservé, avec ses tests de concurrence, quotas, circuit, retries et reprise |
| 43 — Worker | Désactivé par défaut ; appsettings inchangé |
| 44 — Development | Base non migrée |
| 45 — Périmètre | Aucun frontend et aucun effet externe ajoutés |
| 46 — Git | Aucun commit, aucun push |

### Inventaire des changements

La passe finale complète est verte : `TestResults/phase85-full-final.trx`.
Le total passe de 1 050 à 1 137 : 89 nouveaux cas et deux cas transitoires remplacés.
Le build Release final réussit avec 0 avertissement et 0 erreur. Le contrôle EF
ne détecte aucune modification du modèle depuis la migration ; git diff --check
réussit. HEAD reste `4df7d247e52199a60913f5c544f1932b9b00f7ad` sur main.
Le journal console final est dans `%TEMP%/prospection-phase85-full-final.log`.

46 fichiers : 27 suivis modifiés, 19 nouveaux, aucun fichier staged.

```text
 M README.md
 M docs/automation-jobs-v1.md
 M docs/automation-worker-v1.md
 M src/ProspectionCrm.Api/Controllers/AutomationExecutionsController.cs
 M src/ProspectionCrm.Api/Data/Configurations/AutomationExecutionConfiguration.cs
 M src/ProspectionCrm.Api/Data/Configurations/AutomationJobConfiguration.cs
 M src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
 M src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
 M src/ProspectionCrm.Api/Dtos/AutomationExecutions/AutomationExecutionDto.cs
 M src/ProspectionCrm.Api/Entities/AutomationExecution.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationJobCodes.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationJobProcessor.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationRuntimeStore.cs
 M src/ProspectionCrm.Api/Services/AutomationExecutionService.cs
 M src/ProspectionCrm.Api/Services/IAutomationExecutionService.cs
 M tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeBehaviorTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeFixture.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeRevalidationTests.cs
 M tests/ProspectionCrm.Api.Tests/IngestionHistoryReadTests.cs
 M tests/ProspectionCrm.Api.Tests/Phase6ReconstructionTests.cs
 M tests/ProspectionCrm.Api.Tests/RssCollectionTests.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingMigrationTests.cs
?? docs/automation-action-requests-v1.md
?? src/ProspectionCrm.Api/Controllers/AutomationActionRequestsController.cs
?? src/ProspectionCrm.Api/Data/Configurations/AutomationActionRequestConfiguration.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261009115938_Phase85AutomationActionRequests.Designer.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261009115938_Phase85AutomationActionRequests.cs
?? src/ProspectionCrm.Api/Dtos/AutomationActionRequests/AutomationActionRequestDto.cs
?? src/ProspectionCrm.Api/Entities/AutomationActionRequest.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationActionRequestCodes.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationActionRequestService.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationActionRequestStore.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationActionSnapshot.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionRequestApiTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionRequestFixture.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionRequestResilienceTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionRequestRevalidationTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionRequestSchemaTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionRequestTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationActionSnapshotTests.cs
```
