# Phase 9.6.3 — Préparation et approbation humaine calendrier

**Validation locale complète : 52/52 dédiés, 903 scénarios de régression validés,
globale finale unique 1794/1794, 0 échec et 0 ignoré. Build Release final 0/0,
HasPendingModelChanges=false ; 49/49 sources et 3/3 DLL inchangées.**

## Référence et périmètre

Base `main` : `65bd6f4cf29a11a3d458ddb12e915a8b0e637f65`, identique à
`origin/main` après fetch. Arbre et index vides au départ. CI 9.6.2 #71 confirmée
`completed/success` sur cette base (1742 tests dans le rapport précédent).

Audit des rapports 9.5.3, 9.6.1 et 9.6.2, du cycle EmailSendPlan, des demandes
AutomationActionRequest, du catalogue, de la policy et des contrats/entités EF calendrier.

Cette tranche prépare uniquement une création ou une modification d'occurrence.
Aucun exécuteur, appel fournisseur, invitation, notification, retry, job d'automation
ou écriture de CalendarEvent n'est produit par la préparation ou la décision.
Les transports et leurs configurations restent inchangés et désactivés par défaut.

## Modèle et snapshot

`CalendarEventPlan` conserve le workspace, le compte, le calendrier et la connexion
exacts, l'opération `create`/`update`, le contenu JSON, les références CRM explicites,
la révision optimiste, la révision soumise et la demande de décision.
Pour update : identifiant CalendarEvent, identité/révision externes et snapshot de
la cible sont persistés. Celui-ci conserve aussi le contenu, le statut, le hash
entrant, les références historiques et le couple série/occurrence.

Le snapshot fermé version 1 contient l'identité du plan et du workspace, la connexion,
le compte, la révision soumise, le contenu proposé et la cible éventuelle. Une
sérialisation déterministe produit le SHA-256 hexadécimal `ContentHash`.
`AutomationActionRequest.ContentFingerprint` et, pour cette origine manuelle,
`RuleFingerprint` doivent être identiques à cette empreinte. Les objets inconnus,
versions inconnues et propriétés JSON dupliquées sont refusés par le lecteur de snapshot.

La soumission fige le contenu : `SubmittedRevision` permet de conserver le même hash
lorsque la révision de cycle augmente. L'approbation compare le snapshot canonique
complet, les identités, les empreintes, les références CRM et la cible actuelle.
Un changement de version, de contenu, d'identité d'occurrence ou une annulation de
la cible provoque un conflit sans décision partielle ni actualisation silencieuse.
Un nouveau contenu exige un nouveau plan après soumission.

## États, API et sécurité humaine

Transitions : draft → pending-approval → approved/rejected ; draft ou pending → cancelled.
PUT est limité à draft et exige la révision courante. Soumission répétée et révision
obsolète : conflit. Une décision identique répétée est idempotente ; une décision
opposée est refusée. Plans approuvés, rejetés et annulés conservés et immuables.

Routes humaines bornées à 65536 octets, DTO fermés :

- POST et GET `/api/calendar-event-plans` ;
- GET et PUT `/api/calendar-event-plans/{id}` ;
- POST `/api/calendar-event-plans/{id}/submit` et `/cancel`, avec Revision ;
- décisions uniquement par les routes approve/reject existantes d'AutomationActionRequest.

L'authentification cookie, l'antiforgery et le contrôle du propriétaire du workspace
sont conservés. Les signatures machine n'autorisent aucune préparation ou décision.
Les lectures sont filtrées par workspace ; les FK composites protègent compte,
calendrier, connexion, références CRM et cible. La correspondance cible/calendrier
est revalidée sous verrou, sans imposer CalendarId aux événements historiques.
Aucun Contact, Company ou Opportunity n'est inféré.

Actions `calendar.event.create` et `calendar.event.update`, catégorie distincte `calendar`.
La policy plafonne explicitement cette catégorie à `manual` dans les trois modes.
Le kill switch bloque la soumission ; comme pour email, une décision humaine déjà
pendante reste possible si les préconditions métier sont encore satisfaites.
Validation des règles, dispatcher et évaluateur refusent les actions calendrier.
Il n'existe aucun chemin de préparation automatique ni exécuteur calendrier.
Les comportements task/email, quotas, circuits et workers existants sont conservés.

## Dates, fuseaux et récurrences

Les chaînes de dates conservent leur offset jusqu'à la validation via le contrat
CalendarSync de 9.6.1 : offset explicite, UTC à la microseconde, durée strictement
positive, fin exclusive, fuseau IANA connu. Un offset local non nul doit correspondre
au fuseau. All-day exige des minuits locaux et une date de fin ultérieure ; les
journées DST de 23 et 25 heures sont acceptées.

Create ne crée aucun événement. Update exige un événement importé, confirmé,
identifié et révisé dans le même calendrier/workspace. Une occurrence conserve
ses identités série/occurrence ; RRULE, modification d'une série entière et
participants ne font pas partie des DTO acceptés.

## Transactions et concurrence

Ordre : workspace SHARE, verrou transactionnel d'automation du workspace,
demande existante avant plan, puis compte/calendrier et cible. L'ordre relatif
compte → calendrier → événement est compatible avec CalendarSync. Les références
CRM sont lues sous SHARE. Aucun verrou n'est conservé pendant un transport.

Plan et demande sont décidés dans la même transaction. Les tests couvrent double
soumission, update/submit, approve/approve, approve/reject, cancel/approve, calendrier
désactivé pendant l'attente du verrou, révision obsolète et cible modifiée/annulée.
Le trigger PostgreSQL interdit la mutation du contenu soumis, les transitions
inverses, toute mutation terminale et la suppression de l'historique des plans.

## Migration et préservation

Migration additive : `20261021144859_Phase963CalendarEventPlans`.
Nouvelle table, index/FK/contraintes, clés alternatives workspace sur événements et
liaison compte/calendrier ; extension des contraintes d'origine/fingerprint des
requêtes. Les demandes calendrier sont uniquement manuelles (job et règle NULL).
Aucune migration historique modifiée, aucun backfill et aucune donnée métier réécrite.

Tests : base vierge, upgrade depuis 9.6.1, comparaison JSON intégrale de toutes les
tables historiques, Down/Up vide et refus de downgrade en présence de plans ou de
demandes calendrier. Les données de préservation comprennent événements legacy et
importés, curseur, demande email, EmailSendPlan, EmailSendDelivery, opération,
tentative et reçu. Les attentes historiques de dernière migration ont été adaptées
par lot ; les bornes des downgrades refusés restent celles de chaque ancienne phase.

Development vérifié en lecture seule : dernière migration toujours
`20261021110428_Phase961InboundCalendarSync`. Aucune migration 9.6.3 appliquée.
Aucun service applicatif ni n8n démarré ; seules les bases jetables des tests sont utilisées.

## Validation et historique

Journaux/TRX et manifestes hors Git :
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-6-3-20261021/`.

- Build-01 : 0 erreur, 2 avertissements CS8618 préexistants de Settings.razor.
- Build-02 : 1 erreur de compilation dans le nouveau test SQL (accolades littérales
  dans une chaîne interpolée), corrigée avec jsonb_build_object ; aucun défaut produit.
- Build-03 et build-04 : Release **0 erreur, 0 avertissement**.
- Dedicated-01 : **50/50**, 0 échec, 0 ignoré.
- Dedicated-02, après compléments de couverture : **52/52**, 0 échec, 0 ignoré.
- Regressions-01 : **901/903**, 2 échecs, 0 ignoré (6 min 7 s).
  Les deux comparaisons historiques Phase 7 (`SourceCollectionJobMigrationTests`
  et `SourceCollectionSchedulingMigrationTests`) incluaient la nouvelle table vide
  CalendarEventPlans dans leur inventaire avant/après. Correction de leur liste
  de tables postérieures à exclure, sans changement produit ni assouplissement
  des comparaisons des données historiques. Revalidation ciblée `regressions-fix` : **30/30**, 0 échec, 0 ignoré (2 min 38 s).
  Bilan : **903 scénarios distincts de régression validés**.
- Build-final après ces corrections de tests : **0 erreur, 0 avertissement**.
- Globale finale unique `global-final` : **1794/1794**, **0 échec, 0 ignoré**,
  durée console **20 min 7 s**, code de sortie **0**. TRX `Completed`, 1794 résultats
  individuels `Passed` ; aucun error, timeout, aborted ou notExecuted.
  Début TRX : `2026-10-21T17:01:57.5060966+02:00` ; fin :
  `2026-10-21T17:22:06.3286253+02:00`. Aucune seconde suite globale lancée.
- EF : `HasPendingModelChanges=false`, vérifié par les tests et la commande EF Release.
  L'outil local 10.0.10 signale sa différence de version avec le runtime 10.0.12,
  sans changement du modèle ; aucun outil mis à jour.
- Empreintes enregistrées : 49 fichiers source du périmètre et 3 DLL Release.
  Comparaison finale : **49/49 sources et 3/3 DLL identiques** au lancement de la
  globale. Les manifestes `sources-global-before/after.json` et
  `dll-global-before/after.json` sont conservés hors Git ; seul ce rapport est
  complété après le lancement. Le manifeste antérieur aux corrections Phase 7
  reste également conservé, sans être confondu avec celui de la globale.
- Contrôles finaux : `git diff --check` réussi ; inventaire du rapport exactement
  identique à `git status --short` ; index vide ; aucun secret réel, credential,
  clé privée, dump, journal, TRX ni artefact local identifié dans le périmètre.
- EF reconfirmé après la globale : `HasPendingModelChanges=false`.
  Development reste sur `20261021110428_Phase961InboundCalendarSync` (lecture seule).
  Aucun listener API/Blazor/proxy/n8n ; aucun service applicatif redémarré.
- `main`, HEAD et origin/main restent sur
  `65bd6f4cf29a11a3d458ddb12e915a8b0e637f65`.

### Couverture dédiée finale

| Classe | Scénarios |
| --- | ---: |
| CalendarEventPlanContractTests | 19 |
| CalendarEventPlanSecurityTests | 5 |
| CalendarEventPlanMigrationTests | 2 |
| CalendarEventPlanTests | 8 |
| CalendarEventPlanTargetTests | 12 |
| CalendarEventPlanConcurrencyTests | 6 |
| Total | **52** |

Les TRX confirment les compteurs ci-dessus ; les deux noms de scénarios en échec
sur regressions-01 sont présents et Passed dans regressions-fix.

## Limites et travail restant

Une approbation est une autorisation de contenu, jamais une preuve d'exécution.
Toute future exécution devra revalider autorisation, compte/calendrier, références
et version exacte de cible. Aucun effet fournisseur n'est implémenté dans cette
tranche. Pas d'interface Blazor nouvelle : les API humaines exposent les plans et
la demande expose son snapshot calendrier. Aucune phase 9.6.4 commencée.

Aucun staging, commit ou push. Validation locale 9.6.3 terminée, sans migration
Development, fournisseur externe, événement externe ni invitation.

## Inventaire Git

34 fichiers modifiés et 16 nouveaux, soit 50 fichiers ; index vide.

```text
 M src/ProspectionCrm.Api/Controllers/AutomationActionRequestsController.cs
 M src/ProspectionCrm.Api/Data/Configurations/AutomationActionRequestConfiguration.cs
 M src/ProspectionCrm.Api/Data/Configurations/CalendarEventConfiguration.cs
 M src/ProspectionCrm.Api/Data/Configurations/CalendarSyncConfiguration.cs
 M src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
 M src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
 M src/ProspectionCrm.Api/Dtos/AutomationActionRequests/AutomationActionRequestDto.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationActionCatalog.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationActionRequestService.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationActionSnapshot.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationCodes.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationEventDispatcher.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationRuleEvaluator.cs
 M src/ProspectionCrm.Api/Services/Automation/AutomationSafetyPolicy.cs
 M tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/CalendarSyncMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/EmailSendDeliveryMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/EmailSendPlanMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/EmailSyncMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/IngestionHistoryReadTests.cs
 M tests/ProspectionCrm.Api.Tests/IngestionHistoryTests.cs
 M tests/ProspectionCrm.Api.Tests/IntegrationPersistenceMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/IntegrationSecurityMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/Phase6ReconstructionTests.cs
 M tests/ProspectionCrm.Api.Tests/RemoteCollectionMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/RemoteCollectionResultProcessingTests.cs
 M tests/ProspectionCrm.Api.Tests/RssCollectionTests.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingMigrationTests.cs
?? docs/phase-9-6-step-3-validation.md
?? src/ProspectionCrm.Api/Controllers/CalendarEventPlansController.cs
?? src/ProspectionCrm.Api/Data/Configurations/CalendarEventPlanConfiguration.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261021144859_Phase963CalendarEventPlans.Designer.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261021144859_Phase963CalendarEventPlans.cs
?? src/ProspectionCrm.Api/Dtos/CalendarEvents/CalendarEventPlanDtos.cs
?? src/ProspectionCrm.Api/Entities/CalendarEventPlan.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarEventPlanService.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarEventSnapshots.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventPlanConcurrencyTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventPlanContractTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventPlanFixture.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventPlanMigrationTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventPlanSecurityTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventPlanTargetTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventPlanTests.cs
```
