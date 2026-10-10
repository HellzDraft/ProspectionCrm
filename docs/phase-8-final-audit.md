# Audit final de la Phase 8

## Périmètre et référence

Dépôt HellzDraft/ProspectionCrm, branche main, HEAD de départ
`505a669704ae35f4c626d5e53634b72081bc8dec`.
Working tree propre au début de la mission.
Build GitHub #53 vérifié completed/success sur ce SHA.
Build Release initial réussi avec deux avertissements CS8618 préexistants dans
Settings.razor ; aucun changement Blazor prévu par cette phase.

Ce rapport porte sur les contrats exécutables, les contraintes PostgreSQL,
les chemins d’erreur et les scénarios d’intégration. La validation finale est
réussie : **1 234 tests réussis, zéro échec et zéro ignoré**.

Référence locale avant toute modification : **1 137 tests réussis, zéro échec,
zéro ignoré, 26 min 22 s**. Docker était déjà disponible. Rapport local ignoré :
`TestResults/phase86-baseline.trx`.

## Tests ajoutés

| Classe | Cas | Couverture |
| --- | --- | --- |
| AutomationCircuitBreakerTests | 9 | Séries exactes, seuil courant, ordre par séquence, reset, horloge et isolation |
| AutomationCircuitResetTests | 18 | Audit, notes, concurrence, verrou runtime, invariance métier, API et pagination |
| AutomationCircuitSchemaTests | 6 | Contraintes PostgreSQL, unicité concurrente et FK Restrict |
| AutomationCircuitMigrationTests | 1 | Base vierge, upgrade 8.5, Down/Up peuplé et modèle EF |
| AutomationSupervisionTests | 10 | Compteurs, fenêtres, stale, recovery, quotas, absence de mutation et workspace |
| AutomationSupervisionPureTests | 39 | Douze alertes, seuils inclusifs, ordre, absence de Count zéro et options |
| AutomationSupervisionHostTests | 4 | ValidateOnStart des options, même worker désactivé |
| AutomationPhase8EndToEndTests | 8 | Événements HTTP, worker hébergé, trois modes, circuit/reset, kill switch et crash |
| AutomationAuditRegressionTests | 2 | Erreurs contrôlées sans payload d’exception dans les logs |

**97 nouveaux tests**, tous réussis dans la passe ciblée finale (28 s,
zéro échec/ignoré). Les 1 137 cas précédents restent présents.
La première passe ciblée avait une assertion incorrecte sur le SQLSTATE d’un
RESTRICT : PostgreSQL renvoie 23001, et non 23503. La protection fonctionnait ;
l’assertion a été corrigée avant la passe verte. Un lot étendu redondant a été
interrompu pour passer à la régression complète non filtrée.

## Synthèse et architecture

| Phase | Apport |
| --- | --- |
| 8.1 | Paramètres par workspace, kill switch, modes et plafonds de sécurité |
| 8.2 | Queue persistante, déduplication, claim, leases et recovery |
| 8.3 | Événements, dispatch idempotent, évaluation pure et plan typé |
| 8.4 | Worker API, historique durable, effet CRM local, quotas, circuit et retries |
| 8.5 | Demande humaine persistante, snapshot immuable, approbation/rejet atomiques |
| 8.6 | Supervision calculée, guard commun, circuit dérivé et reset explicite audité |

Migration 8.1 : `20261007124044_Phase81AutomationRuntimeSettings`.
Migration 8.2 : `20261008083956_Phase82AutomationJobs`.
La phase 8.3 ne crée pas de migration.
Migration 8.4 : `20261008124357_Phase84AutomationExecutionRuntime`.
Migration 8.5 : `20261009115938_Phase85AutomationActionRequests`.
Migration 8.6 : `20261009144211_Phase86AutomationCircuitResets`.

Flux : événement → dispatcher → AutomationJob → claim → processor → évaluation
et guard sous verrou → CrmTask + AutomationExecution → completion.
Manual/assist passent par AutomationActionRequest puis décision humaine avant
de réintégrer le worker. La décision autorise exactement le snapshot.

AutomationRuntimeGuard partage quotas et circuit entre worker et supervision.
AutomationCircuitBreakerService calcule le circuit à partir des outcomes,
du dernier reset et du seuil courant. Le reset utilise le même verrou PostgreSQL.
La supervision agrège queue, demandes et executions sans aucune mutation.

## Machines à états et contraintes

| Entité | Transitions réelles |
| --- | --- |
| AutomationJob | pending → leased ; leased → completed/failed/pending/awaiting-approval ; pending → cancelled ; awaiting-approval → pending après approve ou cancelled après reject |
| AutomationActionRequest | création pending ; pending → approved/rejected ; cancelled réservé aux états système ; aucune transition après décision via l’API |
| AutomationExecution | running → succeeded/failed/skipped ; skipped deferred autorise un nouveau claim ; échec transitoire autorise une tentative suivante ; résultats terminaux conservés |
| AutomationCircuitReset | insertion seulement si open ; aucune modification/suppression par les services |

Le schéma impose statut/lease cohérents, CompletedAt uniquement pour les jobs
terminaux, champs de décision cohérents, une demande par job, une tentative par
job/numéro et une preuve d’effet par job. Les contraintes séparent origines humaine
et automatique ; un outcome séquencé ne peut être qu’automatique.
Le reset a une séquence positive et une unicité workspace/séquence. Son historique
est append-only au niveau des services ; aucune route d’édition/suppression.

## Sécurité et isolation

- IsEnabled est contrôlé avant préparation et immédiatement avant nouvel effet.
- Modes manual et assist exigent une décision persistante explicite ; un changement
  global ne décide jamais une demande déjà pending.
- Les plafonds application=manual et email<=assist restent dans AutomationSafetyPolicy.
- create-crm-task est la seule action exécutée. Aucun réseau ni effet externe.
- Les API déduisent le workspace côté serveur. Les workers n’utilisent pas
  ICurrentWorkspaceProvider et traitent uniquement leurs jobs internes.
- Les FK composites protègent les liens job/règle/workspace et demande/execution.
  Les UserAccount sont globaux ; les acteurs V1 sont résolus depuis OwnerUserId.
- Les autorisations d’Opportunity sont revérifiées sous verrou avant mutation.
- Les nouveaux DTO de supervision ne contiennent aucun contexte, plan ou configuration
  de règle brute. Les erreurs utilisent des codes stables et des logs sans exception complète.

Limite existante : ce modèle V1 n’est pas un système d’authentification
multi-utilisateur. Les invariants entre certaines entités héritées (par exemple
Opportunity et CrmTask) restent contrôlés par les services et les verrous ; cela ne
constitue pas une autorisation d’écriture directe en base. L’API d’execution
héritée conserve ses DTO historiques, tandis que le runtime ne persiste que des
identifiants et codes diagnostiques dans son ContextJson.

## Concurrence et crash recovery

| Garantie | Mécanisme vérifié |
| --- | --- |
| Dispatch idempotent | Index unique partiel workspace/trigger/key, ON CONFLICT et vérification du gagnant |
| Claim concurrent | FOR UPDATE SKIP LOCKED, ordre déterministe, capacité owner renouvelée |
| Lease fencing | Owner + tentative + workspace + expiration PostgreSQL revérifiés après les attentes et avant commit |
| Effet unique | CrmTask.AutomationJobId unique et checkpoint EffectApplied durable |
| Demande unique | Index PostgreSQL AutomationJobId ; insertion et attente dans la même transaction |
| Décisions concurrentes | Verrou workspace puis demande/job/règle, décision conditionnelle, idempotence sans changement de note/date |
| Quotas et circuit | Guard partagé, contrôle et mutation sérialisés par workspace, revalidation avant effet |
| Reset concurrent | Même advisory lock que les outcomes + unicité PostgreSQL ; second reset observe closed |

Après expiration, recovery remet le job pending puis réconcilie les running
abandonnés. La supervision utilise la même définition sans déclencher cette recovery.
Un effet committé avant completion reste un succès : la reprise termine la queue
sans recréer tâche ou historique. La suppression ultérieure d’une tâche ne détruit
pas le checkpoint. La création de demande et la décision humaine sont atomiques ;
un crash ne laisse pas de décision sans transition de job.

Le budget de retries techniques est distinct du nombre de claims. Les reports
de kill switch, quota ou circuit ne fabriquent pas de tentatives répétitives.
Les erreurs humaines conservent leur demande approved et n’affectent pas le circuit
automatique. Le reset conserve AvailableAt, les jobs failed et les quotas.

## Circuit et observabilité

Le statut open/closed n’est pas persisté. Les séquences, le reset et le seuil courant
sont la source de vérité. Les horloges ne déterminent pas l’ordre des outcomes.
OpenedAt est recalculé selon le seuil courant. Aucun reset automatique ni half-open.

Les logs applicatifs du runtime portent workspace/job/règle/tentative/execution et
codes de raison. La supervision ne prétend pas mesurer la vie du worker :
ConfiguredEnabled n’est ni IsRunning ni Healthy. Les alertes sont déterministes,
triées et calculées à chaque GET. Elles ne sont ni stockées ni acquittées.

Les détails des métriques, fenêtres, options, erreurs et routes figurent dans
[automation-supervision-v1.md](automation-supervision-v1.md).

## Défauts et adaptations ciblées

1. Le helper IsStale 8.5 ne signalait pas un snapshot invalide, bien que son parseur
   retourne déjà un plan null. Le helper commun couvre désormais cette invalidité
   pour GET, approbation et supervision. Le worker conserve la raison diagnostique
   spécifique action-request-invalid-plan pour une demande déjà approuvée.
2. Le filtre d’exception des settings journalisait l’objet exception complet.
   Il ne journalise plus que son type ; un test de non-régression injecte une
   exception contenant un marqueur sensible.
3. La définition de running abandonné est extraite dans une requête partagée.
   Elle reconnaît directement un lease expiré, y compris avant le passage de la
   recovery sur ce job. La reprise conserve ses limites de lot et ses verrous.
4. Les assertions historiques de dernière migration et de snapshots de tables
   sont ajustées à l’ajout 8.6 ; aucune migration historique n’est éditée.

## Migrations et limites

Up ajoute une table vide et ses contraintes/index, sans backfill ni mutation des
données 8.5. Down supprime seulement AutomationCircuitResets ; les échecs masqués
par les marqueurs redeviennent visibles pour le runtime 8.5. Aucun succès fabriqué.
Les workers doivent être arrêtés avant migration/downgrade.
Les validations utilisent exclusivement PostgreSQL Testcontainers jetables.
La base Development permanente n’est jamais migrée par cette mission.

Le snapshot de supervision est non bloquant et non transactionnel globalement :
les compteurs peuvent évoluer entre requêtes. L’analyse des demandes pending
parcourt leurs plans en flux ; son coût croît avec le backlog. Les historiques
peuvent croître sans mécanisme de purge dans cette phase.
Les compteurs de lease utilisent PostgreSQL ; les autres fenêtres TimeProvider UTC.

## Décision sur le worker

Conserver AutomationWorker dans ProspectionCrm.Api. L’infrastructure locale reste
simple, la configuration est disabled par défaut, les services et la base sont
communs et aucun déploiement/scaling indépendant n’est requis. Les tests de
concurrence et de plusieurs instances reposent déjà sur PostgreSQL.

Une extraction future serait justifiée par un scaling indépendant, une disponibilité
différente de l’API, une isolation des pannes, des cadences de déploiement séparées,
une charge de fond importante ou un processus devant rester actif sans trafic API.
Aucun projet ProspectionCrm.Worker n’est créé ici.

## Critères de clôture

La clôture exige : référence initiale verte, build Release final réussi, suite
complète sans échec/ignoré, migrations base vierge/upgrade 8.5/Down-Up validées,
HasPendingModelChanges=false, git diff --check propre, respect de toutes les
garanties et écarts documentés. Tous ces critères sont satisfaits : **la Phase 8
peut être clôturée définitivement dans le périmètre défini**. Les changements
restent locaux, sans staging, commit ni push.

Restent hors périmètre : frontend, dashboard, alertes persistantes, notification,
e-mail/webhook, Prometheus, instrumentation supplémentaire, heartbeat, reset
automatique, réessai de failed après reset, nouvelles actions, IA/n8n, authentification
complète et extraction du worker.

## Rapport de livraison — points du cahier des charges

| Point | Résultat |
| --- | --- |
| 1 — Départ Git | HellzDraft/ProspectionCrm, main, 505a669704ae35f4c626d5e53634b72081bc8dec |
| 2 — Working tree initial | Propre, avant toute modification |
| 3 — Référence | Build Release réussi, deux avertissements Blazor préexistants ; 1 137 tests réussis, zéro échec/ignoré |
| 4 — Circuit | Statut dérivé, sans booléen persistant open/closed |
| 5 — Source commune | AutomationRuntimeGuard et AutomationCircuitBreakerService utilisés par runtime et supervision |
| 6 — Fichiers | 48 fichiers, inventaire détaillé ci-dessous |
| 7 — Migration | 20261009144211_Phase86AutomationCircuitResets |
| 8 — Schéma | Id, WorkspaceId, séquence positive, date UTC, acteur obligatoire, note nullable 2 000 caractères ; FK Restrict et unicité workspace/séquence |
| 9 — Calcul | Compte exact des échecs après max(dernier succès, dernier reset), ordre OutcomeSequence |
| 10 — Seuil | Seuil courant ; OpenedAt recalculé, sans réécrire l’historique |
| 11 — Reset | Insertion uniquement si open ; statut fermé et wasReset ; aucune mutation métier |
| 12 — Concurrence | Verrou advisory commun, unicité PostgreSQL, deuxième reset closed sans nouvelle ligne |
| 13 — API circuit | GET /api/automation-circuit-breaker, GET /resets, POST /reset sous ce préfixe |
| 14 — API supervision | GET /api/automation-supervision, workspace exclusivement serveur |
| 15 — Queue | Huit compteurs d’états et quatre dates ; leases mesurés avec PostgreSQL |
| 16 — Demandes | Pending/stale, décisions 24 h, oldest/last ; même helper IsStale que l’API 8.5 |
| 17 — Executions | Running/abandoned, résultats et effets 24 h, échecs techniques configurables et dernières dates |
| 18 — Quotas | Effets automatiques seulement ; 60 s glissantes et journée UTC, mêmes dates de reprise que le worker |
| 19 — Alertes | Douze codes centralisés ; niveaux critical/warning/info ; ordre stable et Count positif |
| 20 — Options | Seuils 30 minutes, 24 heures, 100 éléments, fenêtre d’échecs 24 heures ; bornes et ValidateOnStart |
| 21 — Reprise | AvailableAt conservé après reset ; aucun job failed réessayé |
| 22 — Humain | Circuit et quotas automatiques ignorés ; kill switch obligatoire |
| 23 — Tests circuit | Séries, seuils, succès intermédiaire, reset, ordre, horloge et isolation |
| 24 — Tests reset | Audit, invariance des données, idempotence, concurrence, API, notes et pagination |
| 25 — Tests supervision | Tous les compteurs, alertes, bornes temporelles, lecture sans mutation et erreurs contrôlées |
| 26 — Bout en bout | Événement HTTP + worker hébergé ; automatic, manual approuvé, assist rejeté, circuit/reset et crash |
| 27 — Audit | Snapshot invalide non signalé stale et exception complète dans les logs des settings identifiés |
| 28 — Correctifs | Helper stale partagé complété, logs limités au type d’exception, tests de non-régression ; requête d’abandon partagée |
| 29 — Worker | Conservé dans l’API ; extraction future seulement selon les critères documentés |
| 30 — Documentation | Deux documents nouveaux ; README, worker et demandes humaines mis à jour |
| 31 — Total final | 1 234 tests réussis, zéro échec et zéro ignoré ; 97 nouveaux tests ; durée annoncée 26,6878 minutes |
| 32 — Build Release final | Réussi, zéro avertissement et zéro erreur |
| 33 — Migrations | Base vierge, upgrade 8.5, Down/Up validés sans migration en attente dans les bases de test ; HasPendingModelChanges=false confirmé après la suite complète |
| 34 — Diff | git diff --check réussi après rédaction finale |
| 35 — Git final | main, HEAD de départ conservé ; 19 fichiers suivis modifiés et 29 nouveaux, sans staging |
| 36 — Reset automatique | Aucun reset automatique ajouté |
| 37 — Historique | Le reset ne modifie aucune AutomationExecution ni aucune décision précédente |
| 38 — Alertes | Calculées à la lecture, jamais persistées |
| 39 — GET | Aucune mutation, réparation, recovery ou claim |
| 40 — Effets humains | Exclus des quotas et du circuit automatiques |
| 41 — Activation | AutomationWorker.Enabled reste false par défaut |
| 42 — Development | Base permanente non migrée ; seules les bases Testcontainers sont utilisées pour les migrations |
| 43 — Périmètre | Aucun frontend ni effet externe ajouté |
| 44 — Projet Worker | Aucun projet séparé créé |
| 45 — Publication | Aucun commit, aucun push pendant la Phase 8.6 |
| 46 — Clôture | La Phase 8 peut être clôturée définitivement dans le périmètre défini ; changements locaux non committés |

## Résultats de validation finale

- Build Release : réussi, zéro avertissement et zéro erreur lors du build final.
- Suite complète non filtrée : 1 234 exécutés et réussis, zéro échec, zéro ignoré,
  zéro abandon. Durée annoncée par dotnet : 26,6878 minutes (environ 26 min 41 s).
  Rapport local ignoré : `TestResults/phase86-full-final.trx`.
- Passe ciblée : 97 réussis, zéro échec/ignoré ;
  `TestResults/phase86-targeted-final.trx`.
- Contrôle EF après les tests : aucune différence entre modèle et dernière
  migration, soit HasPendingModelChanges=false. L’outil EF 10.0.10 signale sa
  version antérieure au runtime 10.0.12 ; le contrôle réussit avec un code de sortie 0.
- Les tests vérifient l’absence de migrations en attente dans les bases jetables
  après migration. La base Development permanente reste volontairement non migrée.
- `git diff --check` : réussi. Branche main et HEAD de départ conservés ; index vide.
- Worker désactivé par défaut ; aucun frontend, effet externe, projet Worker
  séparé, commit ni push ajouté pendant cette mission.

## Inventaire des fichiers

48 fichiers : 19 fichiers suivis modifiés et 29 nouveaux, sans staging.

```text
docs/automation-action-requests-v1.md
docs/automation-supervision-v1.md
docs/automation-worker-v1.md
docs/phase-8-final-audit.md
README.md
src/ProspectionCrm.Api/appsettings.json
src/ProspectionCrm.Api/Controllers/AutomationCircuitBreakerController.cs
src/ProspectionCrm.Api/Controllers/AutomationSettingsController.cs
src/ProspectionCrm.Api/Controllers/AutomationSupervisionController.cs
src/ProspectionCrm.Api/Controllers/AutomationSupervisionErrorsAttribute.cs
src/ProspectionCrm.Api/Data/Configurations/AutomationCircuitResetConfiguration.cs
src/ProspectionCrm.Api/Data/Migrations/20261009144211_Phase86AutomationCircuitResets.cs
src/ProspectionCrm.Api/Data/Migrations/20261009144211_Phase86AutomationCircuitResets.Designer.cs
src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
src/ProspectionCrm.Api/Dtos/AutomationSupervision/AutomationCircuitDtos.cs
src/ProspectionCrm.Api/Dtos/AutomationSupervision/AutomationSupervisionDto.cs
src/ProspectionCrm.Api/Entities/AutomationCircuitReset.cs
src/ProspectionCrm.Api/Program.cs
src/ProspectionCrm.Api/Services/Automation/AutomationActionSnapshot.cs
src/ProspectionCrm.Api/Services/Automation/AutomationAlerts.cs
src/ProspectionCrm.Api/Services/Automation/AutomationCircuitBreakerService.cs
src/ProspectionCrm.Api/Services/Automation/AutomationExecutionQueries.cs
src/ProspectionCrm.Api/Services/Automation/AutomationRuntimeGuard.cs
src/ProspectionCrm.Api/Services/Automation/AutomationRuntimeLock.cs
src/ProspectionCrm.Api/Services/Automation/AutomationRuntimeStore.cs
src/ProspectionCrm.Api/Services/Automation/AutomationSupervisionOptions.cs
src/ProspectionCrm.Api/Services/Automation/AutomationSupervisionService.cs
src/ProspectionCrm.Api/Services/Automation/AutomationSupervisionWorkspace.cs
tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationAuditRegressionTests.cs
tests/ProspectionCrm.Api.Tests/AutomationCircuitBreakerTests.cs
tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationCircuitResetTests.cs
tests/ProspectionCrm.Api.Tests/AutomationCircuitSchemaTests.cs
tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
tests/ProspectionCrm.Api.Tests/AutomationPhase8EndToEndTests.cs
tests/ProspectionCrm.Api.Tests/AutomationRuntimeFixture.cs
tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationSupervisionFixture.cs
tests/ProspectionCrm.Api.Tests/AutomationSupervisionHostTests.cs
tests/ProspectionCrm.Api.Tests/AutomationSupervisionPureTests.cs
tests/ProspectionCrm.Api.Tests/AutomationSupervisionTests.cs
tests/ProspectionCrm.Api.Tests/IngestionHistoryReadTests.cs
tests/ProspectionCrm.Api.Tests/Phase6ReconstructionTests.cs
tests/ProspectionCrm.Api.Tests/RssCollectionTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingMigrationTests.cs
```

## État Git final — git status --short

```text
 M README.md
 M docs/automation-action-requests-v1.md
 M docs/automation-worker-v1.md
 M src/ProspectionCrm.Api/Controllers/AutomationSettingsController.cs
 M src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
 M src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationActionSnapshot.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationRuntimeStore.cs
 M src/ProspectionCrm.Api/appsettings.json
 M tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeFixture.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/IngestionHistoryReadTests.cs
 M tests/ProspectionCrm.Api.Tests/Phase6ReconstructionTests.cs
 M tests/ProspectionCrm.Api.Tests/RssCollectionTests.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingMigrationTests.cs
?? docs/automation-supervision-v1.md
?? docs/phase-8-final-audit.md
?? src/ProspectionCrm.Api/Controllers/AutomationCircuitBreakerController.cs
?? src/ProspectionCrm.Api/Controllers/AutomationSupervisionController.cs
?? src/ProspectionCrm.Api/Controllers/AutomationSupervisionErrorsAttribute.cs
?? src/ProspectionCrm.Api/Data/Configurations/AutomationCircuitResetConfiguration.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261009144211_Phase86AutomationCircuitResets.Designer.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261009144211_Phase86AutomationCircuitResets.cs
?? src/ProspectionCrm.Api/Dtos/AutomationSupervision/
?? src/ProspectionCrm.Api/Entities/AutomationCircuitReset.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationAlerts.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationCircuitBreakerService.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationExecutionQueries.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationRuntimeGuard.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationRuntimeLock.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationSupervisionOptions.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationSupervisionService.cs
?? src/ProspectionCrm.Api/Services/Automation/AutomationSupervisionWorkspace.cs
?? tests/ProspectionCrm.Api.Tests/AutomationAuditRegressionTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationCircuitBreakerTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationCircuitResetTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationCircuitSchemaTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationPhase8EndToEndTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationSupervisionFixture.cs
?? tests/ProspectionCrm.Api.Tests/AutomationSupervisionHostTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationSupervisionPureTests.cs
?? tests/ProspectionCrm.Api.Tests/AutomationSupervisionTests.cs
```
