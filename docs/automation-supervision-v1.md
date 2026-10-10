# Supervision des automatisations — Phase 8.6

Le worker reste hébergé dans l’API et désactivé par défaut. Aucune migration
n’est exécutée au démarrage. Aucun frontend, effet externe, heartbeat, notification
ou alerte persistante n’est ajouté.

## Routes et isolation

| Route | Contrat |
| --- | --- |
| GET /api/automation-supervision | Snapshot opérationnel du workspace courant |
| GET /api/automation-circuit-breaker | État dérivé du circuit automatique |
| GET /api/automation-circuit-breaker/resets | Historique paginé des resets |
| POST /api/automation-circuit-breaker/reset | Réarmement explicite et audité |

ICurrentWorkspaceProvider est l’unique source du workspace HTTP. Les services
internes du worker reçoivent les IDs des jobs et ne résolvent pas le workspace HTTP.
La convention V1 sélectionne exactement un workspace actif ; elle ne constitue
pas une authentification multi-utilisateur.

Les GET n’effectuent ni claim, ni recovery, ni SaveChanges, ni création de settings.
Des settings absents donnent 409 AutomationSettingsMissing. Le snapshot n’est
pas une photographie globale transactionnelle bloquante : les compteurs peuvent
évoluer entre lectures. GeneratedAt est capturé une fois avec TimeProvider UTC.
Les lectures ne prennent aucun verrou métier ni advisory lock.

## Mesures

Worker expose ConfiguredEnabled, IdleDelaySeconds, RecoveryIntervalSeconds et
MaxAttempts. ConfiguredEnabled décrit uniquement la configuration de ce processus.
Il n’existe aucun IsRunning ou Healthy : sans heartbeat persistant, l’API ne
peut pas attester la disponibilité d’un autre worker.

RuntimeSettings expose IsEnabled, OperatingModeCode, MaxExecutionsPerMinute,
MaxExecutionsPerDay et MaxConsecutiveFailures.

| Groupe | Mesures et définition |
| --- | --- |
| Queue | PendingAvailableCount : pending et AvailableAt <= GeneratedAt ; PendingScheduledCount : pending futur |
| Leases | LeasedActiveCount / LeasedExpiredCount : comparaison stricte > / <= à statement_timestamp() PostgreSQL |
| États | AwaitingApprovalCount, FailedCount, CancelledCount ; CompletedLast24HoursCount selon CompletedAt |
| Dates queue | OldestOpenJobAt : min CreatedAt des pending/leased/awaiting-approval ; OldestPendingAvailableAt : min AvailableAt des pending disponibles ; NextAvailableAt : min AvailableAt des pending futurs ; LastJobUpdatedAt : max UpdatedAt ou CreatedAt |
| Demandes | PendingCount, PendingStaleCount, ApprovedLast24HoursCount, RejectedLast24HoursCount, OldestPendingAt, LastDecisionAt |
| Executions | RunningCount, AbandonedRunningCount, SucceededLast24HoursCount, FailedLast24HoursCount, SkippedLast24HoursCount |
| Effets | AutomaticEffectsLast24HoursCount, HumanEffectsLast24HoursCount, LastEffectAt |
| Diagnostics | RecentTechnicalFailuresCount : failed terminés dans la fenêtre configurée, abandons inclus ; LastExecutionAt : max TriggeredAt |

Les fenêtres 24 h sont inclusives aux deux extrémités, [GeneratedAt - 24 h,
GeneratedAt]. Les décisions utilisent DecidedAt, les executions FinishedAt.
Les timestamps oldest/last sont null en l’absence de ligne.

PendingStaleCount appelle le même helper que l’API 8.5 : règle absente ou étrangère,
fingerprint différent/ininterprétable, ou snapshot invalide. Un changement de mode
global ou de métadonnées d’affichage ne rend pas une demande stale. Enabled et
ArchivedAt restent des contrôles distincts d’autorisation.

AbandonedRunningCount partage sa requête avec la recovery : tentative running liée
à un job dont le statut n’est plus leased, dont AttemptCount ne correspond plus,
ou dont le lease PostgreSQL a expiré. Les anciens historiques sans AutomationJobId
ne sont pas des tentatives du runtime. Le GET compte ; seule la recovery réconcilie.

## Quotas et source commune

AutomationRuntimeGuard est utilisé par AutomationRuntimeStore.DeferredAsync et
la supervision. AutomationCircuitBreakerService fournit l’unique calcul du circuit.
Les quotas comptent exclusivement IsAutomaticAttempt ET EffectApplied.

| Quota | Used / Limit / IsReached / AvailableAgainAt |
| --- | --- |
| Minute | Fenêtre glissante, FinishedAt > GeneratedAt - 60 s ; date de sortie du nombre nécessaire d’effets, null si non atteint |
| Jour | Journée UTC, minuit inclus jusqu’au minuit suivant exclu ; AvailableAgainAt est toujours le prochain minuit UTC |

Comme en 8.4, un effet dont FinishedAt semble futur après un recul d’horloge reste
compté de façon conservatrice dans la fenêtre minute. Le calcul du report du
worker est strictement le même que celui exposé.

Les effets humains approuvés ignorent circuit et quotas automatiques. Ils ne les
consomment pas et ne les réarment pas ; le kill switch reste obligatoire pour
tout nouvel effet. Les reports initiaux ne créent pas d’historiques répétitifs.

## Circuit dérivé

Aucun booléen open/closed n’est persisté. Les entrées sont les outcomes automatiques
avec OutcomeSequence, le seuil courant et le dernier reset. Skipped, deferred,
previews, demandes et résultats humains n’interviennent pas.

Le dernier reset est celui dont ResetAfterOutcomeSequence est maximal, indépendamment
d’un recul d’horloge. La série d’échecs commence après le plus grand de :
la séquence du dernier reset et la séquence du dernier succès automatique.
ConsecutiveFailureCount compte toute cette série, sans plafonnement au seuil.
StatusCode est open lorsque le compte atteint MaxConsecutiveFailures, sinon closed ;
CanReset est vrai uniquement pour open.

OpenedAt est FinishedAt de l’échec qui atteint le seuil courant, dans l’ordre des
séquences. Il est null si closed. Augmenter ou diminuer le seuil peut fermer ou
ouvrir le circuit et recalculer OpenedAt, sans réécriture d’historique.
LastAutomaticOutcomeSequence/At, LastAutomaticSuccessAt et LastAutomaticFailureAt
décrivent les derniers outcomes dans l’ordre des séquences, même avant un reset.
Les dates ne servent jamais à trier le circuit.

## Reset explicite

Body : `{"note":"Incident corrigé."}`. La note est facultative, limitée à
2 000 caractères ; une chaîne blanche devient null. Ne pas y inscrire de secret.
Les propriétés inconnues sont refusées. WorkspaceId et RequestedByUserId ne sont
jamais acceptés depuis le client.

L’acteur V1 enregistré est Workspace.OwnerUserId, relu sous verrou partagé.
UserAccount est global dans ce modèle, sans WorkspaceId : les deux FK Restrict
pointent vers le workspace et l’utilisateur. Le service choisit l’acteur depuis
le workspace ; aucune appartenance multi-utilisateur fictive n’est introduite.

La transaction prend le même advisory lock automation-runtime du workspace que
le worker, relit les settings sous verrou partagé, calcule le circuit et insère
un AutomationCircuitReset seulement s’il est ouvert. Le marqueur contient Id,
WorkspaceId, ResetAfterOutcomeSequence > 0, RequestedAt UTC, RequestedByUserId
et Note. L’unicité PostgreSQL porte sur workspace + séquence. L’historique est
append-only dans les services ; aucune route ne le modifie ou supprime.

Réponse : status courant, wasReset, reset créé ou null. Un circuit déjà fermé
retourne 200 / false / null ; dates et notes antérieures restent intactes.
Deux requêtes concurrentes ont au plus un reset appliqué : la seconde observe
le circuit fermé. Le verrou sérialise aussi les resets avec les outcomes du worker.

Le reset ne modifie aucun job, historique, quota, mode ou kill switch. Il ne crée
aucune tâche, ne fabrique pas de succès et ne réessaie aucun job failed.
AvailableAt reste inchangé : la reprise attend cette date. Un reset est possible
avec le kill switch coupé, mais les effets restent bloqués.
De nouveaux échecs après le marqueur peuvent rouvrir le circuit.
Aucun reset automatique, délai de fermeture ou probe half-open n’existe.

Historique : offset >= 0, limit 1–200, défaut 50, ordre RequestedAt DESC puis Id DESC.
La réponse contient offset, limit, totalCount, hasMore et items ; les entrées
exposent Id, ResetAfterOutcomeSequence, RequestedAt, RequestedByUserId et Note.

## Alertes calculées

Codes et niveaux sont centralisés. Chaque entrée contient Code, SeverityCode,
Count strictement positif, Since nullable et RelatedStateCode nullable.
L’ordre est critical, warning, info, puis Code ordinal croissant.
Since est renseigné quand la métrique fournit la date pertinente ; il reste null
lorsque le snapshot ne permet pas de dater précisément l’anomalie.

| Code | Niveau | Condition |
| --- | --- | --- |
| circuit-open | critical | Circuit open ; Since = OpenedAt |
| automatic-failures-accumulating | warning | Circuit closed et échecs consécutifs > 0 |
| expired-leases | critical | Leases expirés > 0 |
| abandoned-running-executions | critical | Tentatives running abandonnées > 0 |
| worker-disabled-with-open-work | warning | Worker configuré disabled avec pending ou leased |
| runtime-disabled-with-open-work | warning | Kill switch coupé avec pending ou leased |
| failed-jobs | warning | Jobs failed > 0 |
| pending-job-backlog | warning | Nombre de pending au seuil ou âge du plus ancien disponible au seuil |
| approval-backlog | warning | Nombre de demandes pending au seuil ou âge de la plus ancienne au seuil |
| stale-approval-requests | warning | Demandes pending stale > 0 |
| minute-quota-reached | warning | Quota minute atteint |
| daily-quota-reached | warning | Quota journalier atteint |

Les seules attentes humaines ne déclenchent pas les alertes de worker/runtime
désactivés. Aucune alerte, notification ou acquittement n’est persisté.

Options techniques AutomationSupervision, validées au démarrage même worker désactivé :

| Option | Défaut | Bornes |
| --- | --- | --- |
| PendingJobWarningAgeMinutes | 30 | 1–10080 |
| ApprovalWarningAgeHours | 24 | 1–8760 |
| BacklogWarningCount | 100 | 1–1000000 |
| RecentFailureWindowHours | 24 | 1–8760 |

Ces options ne changent aucune autorisation métier et ne sont pas des RuntimeSettings.

## Erreurs et migration

ProblemDetails utilise notamment InvalidRequest, InvalidPagination, InvalidResetNote,
WorkspaceUnavailable, AutomationSettingsMissing, ConcurrentCircuitReset,
AutomationSupervisionInternalError et AutomationCircuitBreakerInternalError.
Aucun SQL, payload, JSON brut de règle/plan, stack trace ou exception complète
n’est retourné ou ajouté par les nouveaux logs applicatifs.

La migration `20261009144211_Phase86AutomationCircuitResets` crée uniquement la table, ses FK,
contrainte positive, index unique et index d’historique ; aucun backfill.
Le snapshot EF est synchronisé. Up préserve toutes les données antérieures.
Down supprime uniquement cette table : les anciens échecs redeviennent visibles
au runtime 8.5 puisque les marqueurs de reset ont disparu. Aucun succès artificiel.
Arrêter les workers avant migration ou downgrade. La base Development permanente
n’est pas migrée par cette mission.
