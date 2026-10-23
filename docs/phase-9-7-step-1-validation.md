# Phase 9.7.1 — Supervision transversale des intégrations

Validation locale du 23 octobre 2026, depuis `main` / `origin/main`
`b75b53a7e389bede36ba53566b3aef2d79d329e7`. Aucun staging, commit ou push.

## Contrats

- `GET /api/integration-supervision` : workspace courant, instant UTC d'observation,
  total des opérations, groupes `(type, statusCode, state)`, compteurs des tentatives
  actives/anciennes et des reçus non traités/anciens, dates les plus anciennes, alertes.
  `n8nHealth` vaut toujours `not-assessed`.
- `GET /api/integration-supervision/operations` : `offset` (0–1 000 000, défaut 0),
  `limit` (1–100, défaut 50), filtres exacts facultatifs `type`, `status`, `state`.
  Retour : workspace, instant UTC, offset, limit, total filtré et items ordonnés par
  `createdAt DESC, id DESC`. Valeurs invalides : 400 `InvalidRequest`, sans écho.
- Les items fournissent les identifiants opération/connexion/commande/corrélation,
  le lien métier, les statuts persistés, l'état calculé, les dates, les compteurs et
  la dernière tentative. Aucun contenu de reçu, email, destinataire, snapshot,
  identifiant fournisseur, hash, clé d'idempotence, credential ou signature.
  `diagnosticCode` suit une liste fermée ; tout autre diagnostic libre devient
  `RecordedDiagnostic`, avec `hasRecordedDiagnostic=true`.
- Authentification humaine et `ICurrentWorkspaceProvider` existants. Aucune sélection
  de workspace par le client ; anonymes et identité machine refusés. Réponses non
  mises en cache. Erreurs internes génériques, sans journalisation de leur contenu
  par cette fonctionnalité.

Types : `technical.probe`, `collection.request`, `email.sync`, `email.send`,
`calendar.sync`, `calendar.event.create`, `calendar.event.update`.
Statuts techniques : `pending`, `awaiting-result`, `result-received`, `succeeded`,
`failed`. Les relations métier réutilisent RemoteCollectionIntents/SourceCollectionJobs,
EmailSyncJobs, EmailSendDeliveries, CalendarSyncJobs et CalendarEventDeliveries.

## États et alertes

Les états calculés n'écrasent jamais les statuts persistés. Priorité : conflit métier
ou diagnostic de conflit connu ; reçu non traité ; incertitude métier ; échec métier ;
fin métier ; échec/fin technique ; liaison métier manquante ; dernière tentative
incertaine/en échec ; résultat annoncé sans preuve en attente ; attente normale.

| État | Sens |
| --- | --- |
| `waiting` | Attente normale, y compris un ACK ou une tentative active ancienne |
| `uncertain` | Résultat explicitement inconnu, jamais déduit de l'âge seul |
| `evidence-pending` | Preuve reçue mais non traitée, y compris après une fin technique |
| `conflict` | Conflit persistant nécessitant diagnostic ; les reçus restent comptés |
| `failed` | Échec enregistré, distinct d'une incertitude |
| `completed` | Opération terminée, y compris annulation métier |
| `diagnostic-required` | Métadonnées incohérentes ou liaison métier absente |

| Code | Niveau | Compteur / seuil inclusif |
| --- | --- | --- |
| `IntegrationAttemptOld` | warning | Tentatives `started` depuis au moins 5 minutes |
| `IntegrationWaitingOld` | warning | Opérations `waiting` depuis au moins 30 minutes |
| `IntegrationEvidencePending` | info | Reçus non traités |
| `IntegrationEvidenceOld` | warning | Reçus non traités depuis au moins 30 minutes |
| `IntegrationResultUncertain` | warning | Opérations `uncertain` |
| `IntegrationConflict` | error | Opérations `conflict` |
| `IntegrationFailed` | error | Opérations `failed` |
| `IntegrationMetadataInconsistent` | warning | Opérations `diagnostic-required` |

Seules les alertes de compteur positif sont retournées. Les alertes d'âge/preuve et
d'incertitude portent la date la plus ancienne disponible. L'attente/incertitude
est datée du début de la dernière tentative, sinon de la création de l'opération :
ce n'est pas une mesure exacte du passage au statut inconnu. Une ancienne tentative
`unknown` ne rend pas incertaine une opération depuis acquittée ou terminée.

## Lecture seule et compatibilité

Chaque lecture de supervision utilise une transaction PostgreSQL `REPEATABLE READ`
avec `SET TRANSACTION READ ONLY`. Agrégats, compte et page sont cohérents au sein
de leur requête HTTP. Les projections SQL paramétrées ne chargent que les métadonnées ;
les liaisons métier vérifient workspace, opération, connexion et type. Les tentatives
et reçus sont rattachés par leur clé étrangère d'opération.

Aucun dispatcher, processeur, client réseau ou service de circuit n'est injecté.
Aucun acquittement, suppression, retry, réconciliation, nettoyage ou réarmement.
La supervision fonctionne sans créer de paramètres Automation et avec les
intégrations désactivées. Le contrat humain/workspace de la supervision Automation
8.6 est réutilisé ; ses circuits et routes restent inchangés. Schéma et migrations
inchangés, preuves des phases 9.5/9.6 conservées.

## Validation

| Vérification finale | Résultat |
| --- | --- |
| Build solution Release, `--no-restore` | 0 erreur, 0 avertissement |
| Nouveaux scénarios `IntegrationSupervision*` | 27 réussis |
| Régressions ciblées existantes | 232 réussies |
| Total `targeted-final.trx` | 259 réussis, 0 échec, 0 ignoré |
| `git diff --check` et vérification des fichiers nouveaux | Réussies |

Exécution via `dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj
--configuration Release --no-build --no-restore`, avec filtre `FullyQualifiedName~`
sur les classes suivantes (union) : `IntegrationSupervision`, `AutomationSupervision`,
`IntegrationPersistenceTests`, `IntegrationSecurityTests`, `OperatorSecurityTests`,
`CalendarEventDeliveryTests`, `EmailSendDeliveryTests`, `CalendarSyncLifecycleTests`,
`EmailSyncLifecycleTests`, `RemoteCollectionLifecycleTests`.

Les tests couvrent les frontières d'âge, la priorité des états, les reçus tardifs,
la pagination stable et filtrée, l'isolation workspace, les refus humains/machine,
les diagnostics libres et contenus sentinelles, et les sondes historiques sans connexion.
Un intercepteur vérifie `transaction_read_only=on`, `RepeatableRead` et l'absence
de suivi EF. Des empreintes de toutes les tables mappées sont comparées avant/après
les GET ; les compteurs des faux transports restent inchangés.

Parcours métier observés : collecte distante ACK/inconnue ; email ACK/inconnu/non
envoyé ; synchronisations email/calendrier et conflits historiques ; CREATE/UPDATE
calendrier en attente, inconnu, preuve reçue, terminé puis conflit tardif.
Les mutations de préparation/réconciliation sont explicites dans les seuls tests,
séparées des GET. PostgreSQL Testcontainers jetable uniquement, aucun service ou
migration Development, fournisseur réel ou invitation. Aucune suite globale locale.

Les journaux et TRX sont conservés hors Git sous
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-7-1-20261023/`.
Une traduction LINQ d'ordre après projection d'agrégats a échoué lors du premier
passage ; l'ordre est désormais appliqué avant projection. Les échecs intermédiaires
restent dans les journaux, sans modification des preuves précédentes.

## Limites

La vue décrit les opérations persistées, pas la disponibilité de n8n ni celle d'un
fournisseur. Les jobs sans IntegrationOperation et les erreurs rejetées sans trace
persistante (notamment certains conflits de preuve email) ne sont pas inventés.
Les circuits Automation restent consultables par leur supervision existante.
La liste expose la dernière tentative et les compteurs, pas tous les reçus/tentatives.
Les seuils sont fixes et informatifs, sans action automatique. La pagination par
offset peut se décaler entre deux requêtes concurrentes. Les agrégats parcourent
l'historique du workspace ; aucune qualification de charge de production n'est
revendiquée. La suite globale est réservée à la CI après un push ultérieur autorisé.

## Inventaire Git

9 fichiers : 1 modifié, 8 nouveaux ; index vide.

```text
 M src/ProspectionCrm.Api/Program.cs
?? docs/phase-9-7-step-1-validation.md
?? src/ProspectionCrm.Api/Controllers/IntegrationSupervisionController.cs
?? src/ProspectionCrm.Api/Dtos/Integrations/IntegrationSupervisionDtos.cs
?? src/ProspectionCrm.Api/Services/Integrations/IntegrationSupervisionQuery.cs
?? src/ProspectionCrm.Api/Services/Integrations/IntegrationSupervisionService.cs
?? tests/ProspectionCrm.Api.Tests/IntegrationSupervisionCalendarTests.cs
?? tests/ProspectionCrm.Api.Tests/IntegrationSupervisionHistoryTests.cs
?? tests/ProspectionCrm.Api.Tests/IntegrationSupervisionTests.cs
```
