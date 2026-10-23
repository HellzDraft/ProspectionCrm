# Phase 9.7.2.1 — Diagnostic détaillé des intégrations

23 octobre 2026. Base `main` / `origin/main` :
`b0e6d836895e8a5d75e84e2f0ab0e718d3538ba9`, dépôt initial propre.

## Contrat

`GET /api/integration-supervision/operations/{id:guid}/diagnostic`

Session humaine et workspace existants ; un identifiant absent ou étranger retourne
404 avant toute lecture de ses tentatives/reçus. Aucune sélection de workspace par
paramètre. Même protection des erreurs et du cache que la supervision 9.7.1.

Retour : instant d'observation, projection d'opération 9.7.1 (type, statut technique,
statut métier, identifiants, compteurs), `diagnosticCode`, `riskLevel`,
`evidenceAssessment`, `anomalyCodes`, `recommendationCodes`, `reconciliation`,
et pages `attempts` / `receipts`.

- Tentatives : identifiant, numéro, statut, début et fin ; ordre par numéro.
- Reçus : identifiant de message, numéro de preuve, réception et traitement ; ordre
  par numéro puis identifiant. Une date de traitement ne signifie pas succès.
- `attemptOffset` et `receiptOffset` indépendants, 0–1 000 000, défaut 0 ;
  `limit` 1–100, défaut 50. Chaque page fournit offset, limit, total, items.
  La classification porte sur toute l'opération, indépendamment de la page.
- Aucune projection de payload, message brut, email, signature, secret, hash,
  clé d'idempotence, identité fournisseur ou snapshot. Les diagnostics persistés
  libres restent masqués par la liste fermée 9.7.1.

## Diagnostics et recommandations

| Diagnostic | Risque de base | Conduite |
| --- | --- | --- |
| `NormalWaiting` | low | `WaitForEvidence` |
| `UncertainWithoutEvidence` / `UncertainAfterEvidence` | medium | `WaitForEvidence`, `ManualInvestigationRequired` |
| `EvidencePending` | medium | `ExamineExistingEvidence` |
| `EvidenceInconsistent` / `EvidenceContradictory` | high | `ManualInvestigationRequired` |
| `BusinessConflict` / `MetadataInconsistent` | high | `ManualInvestigationRequired` |
| `OperationFailed` | medium | `ManualInvestigationRequired` |
| `OperationCompleted` | none, sans anomalie | `NoInterventionRequired` |

Un reçu non traité ajoute toujours `ExamineExistingEvidence`. Les alertes d'âge
9.7.1 sont conservées (5 minutes pour une tentative active, 30 pour attente/reçu),
avec risque au moins medium et investigation pour tentative/attente ancienne.
`EvidencePendingAfterCompletion` signale les preuves en attente après clôture.
Une attente ancienne seule ne devient jamais une incertitude ou une panne n8n.

La contradiction/incohérence de contenu n'est affirmée que sur un diagnostic
calendrier déjà persisté par la réconciliation existante. Les nouvelles preuves,
y compris tardives, restent `not-assessed` ; aucune lecture/parsing de contenu
ni reproduction des algorithmes de réconciliation. Sans reçu : `no-evidence`.

`reconciliation.statusCode` distingue :

- `available-at-observation` avec `HumanReconciliationAvailable` : uniquement
  l'envoi email sans aucun reçu, livraison `dispatching`/`awaiting-result`, début
  non nul depuis au moins deux minutes selon PostgreSQL, liaison opération /
  workspace / connexion et existence du compte dans ce workspace vérifiées.
  Route existante : `POST /api/email-send-deliveries/{id}/reconcile`.
  Cette branche peut constater l'incertitude ; elle ne renvoie aucun email.
- `requires-validation` avec `ReviewHumanReconciliationPreconditions` : une route
  existante est référencée pour examen humain, sans annoncer son exécutabilité.
  Les validations de preuve, approbation et contexte restent à faire par la route.
  Cela concerne les livraisons email/calendrier avec preuve en attente ou résultat
  incertain ; pour la collecte, opération et job doivent être `result-received`.
  Routes : `/api/calendar-event-deliveries/{id}/reconcile` et
  `/api/source-collection-jobs/{id}/process-result`, ainsi que l'email ci-dessus.
- `not-recommended` : aucune route proposée, notamment conflit persistant,
  opération terminée, liaison absente ou préconditions connues non remplies.

Les références portent le template de route et l'identifiant métier, jamais une
commande exécutée. Chaque POST revérifie ses conditions ; le GET ne verrouille
pas un droit à une action ultérieure. Les synchronisations email/calendrier restent
observables mais aucune nouvelle capacité de traitement ne leur est annoncée.

## Garanties et validation

Transaction `REPEATABLE READ`, `SET TRANSACTION READ ONLY`, projections sans suivi
EF ; réutilisation de la requête 9.7.1. Aucun service de réconciliation, dispatch,
retry, recovery, fournisseur ou réseau appelé par le GET. Aucune mutation, aucun
acquittement/suppression de reçu, aucune migration ou modification des historiques.

| Vérification finale | Résultat |
| --- | --- |
| Build solution Release `--no-restore` | 0 erreur, 0 avertissement |
| Tests ciblés `--configuration Release --no-build --no-restore` | 125 réussis, 0 échec, 0 ignoré |
| Scénarios | 21 nouveaux, 104 existants (dont couverture 9.7.1 étendue) |
| `git diff --check`, y compris nouveaux fichiers | Réussi |

Filtre `FullyQualifiedName~` (union) : `IntegrationDiagnostic`,
`IntegrationSupervision`, `EmailSendDeliveryTests`, `CalendarEventDeliveryTests`,
`RemoteCollectionLifecycleTests`. Preuves finales : `build-final.log`,
`targeted-final.log`, `targeted-final.trx`. Le premier build complet signalait les
deux avertissements CS8618 préexistants de `Settings.razor` ; aucune correction
hors périmètre n'a été apportée.

Tests : classification, métadonnées et non-divulgation, 401 humain/machine,
404 inter-workspace, bornes/pagination, preuves tardives, inconnues traitées,
invalides et contradictoires, conflit de version, collecte et email, conditions
de recommandation email et succès du POST explicite dans le cas annoncé disponible.
Empreintes de toutes les tables avant/après GET, compteurs de faux transports,
vérification de la transaction PostgreSQL en lecture seule sur le détail complet.

Tests uniquement sur PostgreSQL jetable Testcontainers. Aucun service Development,
fournisseur réel, invitation ou suite globale locale. Journaux/TRX hors Git :
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-7-2-1-20261023/`.
Les échecs intermédiaires de fixtures (dates immuables, format d'identité email,
enveloppe unknown calendrier) sont conservés dans ces journaux ; leurs corrections
ne changent aucun mécanisme métier.

Limites : aucun diagnostic de disponibilité n8n ; contradictions email rejetées
sans diagnostic persistant non déduites des métadonnées ; état observé susceptible
de changer avant l'action humaine ; pagination par offset susceptible de décalage
entre requêtes ; aucune qualification de charge de production.

## Inventaire Git

8 fichiers : 4 modifiés, 4 nouveaux ; index vide, aucun commit/push.

```text
 M src/ProspectionCrm.Api/Controllers/IntegrationSupervisionController.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSupervisionService.cs
 M tests/ProspectionCrm.Api.Tests/IntegrationSupervisionHistoryTests.cs
 M tests/ProspectionCrm.Api.Tests/IntegrationSupervisionTests.cs
?? docs/phase-9-7-step-2-1-validation.md
?? src/ProspectionCrm.Api/Dtos/Integrations/IntegrationDiagnosticDtos.cs
?? src/ProspectionCrm.Api/Services/Integrations/IntegrationOperationDiagnostic.cs
?? tests/ProspectionCrm.Api.Tests/IntegrationDiagnosticTests.cs
```
