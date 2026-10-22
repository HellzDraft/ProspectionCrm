# Phase 9.6.4.1 — Exécution calendrier persistante et réconciliation simulée

**Validation locale terminée : 86/86 dédiés, 1198 scénarios distincts de
régression validés, globale finale unique 1880/1880, 0 échec et 0 ignoré.
Build Release final 0 erreur/0 avertissement ; HasPendingModelChanges=false ;
697/697 fichiers source/projet et 3/3 DLL inchangés pendant la globale.**

## Référence et périmètre

Audit initial : `main`, HEAD, référence locale `origin/main` et branche distante
(`git ls-remote`) identiques à `3c021f5f51bf8571fe9aec92338e9ed0f0b7bd3c`.
Index et working tree vides. Rapport 9.6.3, préparation calendrier, contrats,
imports, exécution email, sécurité Automation et configurations/migrations relus.

Cette tranche ajoute uniquement l'exécution humaine des plans déjà approuvés,
avec `calendar.event.create` et `calendar.event.update`. La catégorie calendrier
reste plafonnée à `manual` ; `AutomationSafetyPolicy` n'est pas modifiée.
Aucun worker d'exécution calendrier ni intégration n8n réelle n'est ajouté.

## Intention et autorisation

`CalendarEventDelivery` conserve le workspace, le plan, la demande humaine,
le compte, le calendrier, la connexion, le type d'opération, le hash approuvé,
la clé d'idempotence, l'identité fournisseur du calendrier et la cible update.
Les identités, empreintes et préconditions deviennent immuables en PostgreSQL.
Les index uniques garantissent une intention par plan/demande, une opération
par intention et une clé d'idempotence unique (`calendar-event-{DeliveryId}`).
Les FK composites et le trigger de liaison vérifient les correspondances avec
le plan, la demande, le calendrier et l'opération d'intégration.

Une création répétée retourne l'intention existante, y compris après un update
réussi. Elle ne réarme jamais l'exécution. `CalendarEventPlan` reste `approved`.

La création initiale et le démarrage du dispatch exigent une identité humaine
propriétaire, une demande et un plan exactement approuvés, une décision datée
valide, les bons type/catégorie et un snapshot canonique identique à l'approbation.
Le service réutilise `CalendarEventPlanService.Revalidate` pour les références
CRM, les comptes/calendriers actifs et non archivés, la connexion et la cible.
Il contrôle aussi la configuration d'intégration, les settings persistants,
le kill switch, le circuit et les quotas minute/jour sous le verrou Automation.
Le décompte calendrier inclut ses tentatives, les envois email et les tentatives
automatiques du workspace ; il est conservateur après une issue inconnue.

Pour update, toute variation locale de révision, contenu, annulation,
identité externe ou couple série/occurrence interdit le dispatch. L'empreinte
de contenu entrant de la cible est également vérifiée avant création d'intention.

## Contrat et protection côté fournisseur

`CalendarEventContracts` définit un contrat fermé version 1, limité à 65536 octets,
avec identifiants opération/message/corrélation, DeliveryId, IdempotencyKey,
ContentHash, snapshot approuvé, fournisseur, calendrier externe et précondition.
Les champs inconnus, doublons imbriqués, champs requis absents, types invalides,
identifiants vides, dates invalides et contenus hors limites sont rejetés.
Les règles de dates, offsets, fuseaux IANA, microsecondes, all-day et fin exclusive
réutilisent `CalendarSyncContracts`.

Update porte explicitement CalendarId, ExternalCalendarId, ExternalEventId,
ExpectedRevision et TargetContentHash. Le simulateur compare ces valeurs sous
le même verrou mémoire que l'effet et son registre d'idempotence. Une version
différente produit `version-conflict` avec la version observée ; aucune écriture
n'est appliquée. À révision égale, un contenu différent ou une cible annulée
produit une preuve de non-application. Une cible absente ne peut pas être créée
implicitement par update.

Un futur transport réel devra démontrer une protection atomique équivalente,
par exemple ETag/If-Match. Ce travail ne suppose pas cette garantie acquise et
ne traduit jamais un simple statut HTTP en conflit de version ou non-application.

## États et transactions

États de l'intention : `pending`, `dispatching`, `awaiting-result`,
`operation-unknown`, `result-received`, `succeeded`, `failed`, `conflict`.

1. Créer intention et IntegrationOperation dans une transaction, puis commit.
2. Revalider l'autorisation et enregistrer IntegrationAttempt numéro 1 et
   `dispatching`, puis commit.
3. Appeler le transport hors transaction SQL et sans verrou PostgreSQL.
4. Enregistrer ACK (`awaiting-result`) ou ambiguïté (`operation-unknown`).
5. Recevoir une preuve durable, puis réconcilier explicitement.

Un ACK 202 ne prouve aucun effet. Exception, timeout, déconnexion ou erreur
générique restent inconnus. Une seule tentative est permise par intention,
y compris après redémarrage. Les triggers interdisent son effacement et le
retour en pending. Un checkpoint dispatching/awaiting-result sans preuve peut
être classé unknown par réconciliation explicite après deux minutes ; cela
n'appelle jamais le fournisseur. L'arrivée d'un callback avant l'ACK ne peut
pas faire régresser l'état réconcilié.

Ordre des verrous : workspace SHARE, advisory transactionnel Automation,
demande, plan, compte, calendrier, puis intention/opération et événement.
Le callback ne verrouille que workspace/Automation/intention/opération.
Le dispatch réutilise la validation de cible sous le verrou calendrier ;
la réconciliation et calendar.sync respectent compte → calendrier → événement.
Ce verrou calendrier sérialise aussi les créations concurrentes d'identité externe.

## Preuves et réconciliation

Le callback existant `/api/integrations/results` authentifie HTTPS/HMAC et
fournit les claims workspace/connexion. Aucun workspace de résultat, secret,
credential, token OAuth ou URL libre de callback n'est accepté. Les reçus
`IntegrationReceipt` conservent contenu canonique, SHA-256, heure de réception
et numéro de preuve (1 à 100). Le contenu et l'identité des reçus calendrier
sont immuables ; ProcessedAt ne peut pas être effacé ou modifié après traitement.

Les résultats distinguent `applied`, `not-applied`, `version-conflict` et
`unknown`. Une preuve décisive comporte un instant UTC ; une preuve applied
comporte l'identité externe réellement générée par le simulateur, sa révision
et l'événement confirmé. La réconciliation vérifie tous les identifiants, la
connexion persistée, le hash approuvé, le snapshot, les dates de preuve et le
contenu effectivement autorisé. Update exige la même identité et une nouvelle
révision supérieure à la révision approuvée.

Tous les reçus en attente sont validés avant mutation métier. Une empreinte
de l'effet prouvé exclut uniquement le MessageId et l'heure d'observation : des
observations identiques sont idempotentes ; des effets contradictoires sont
signalés par `CalendarEvidenceContradiction`. Un reçu incohérent reste non traité
et conservé. Le diagnostic `conflict` est persistant et n'est pas effacé par un
nouveau callback ou une réconciliation répétée. Les preuves inconnues peuvent
être suivies d'une preuve décisive tardive.

Les réceptions répétées avec le même MessageId et un contenu différent sont
refusées sans remplacer le reçu original. Après perte de la réponse au commit,
les GET et une réconciliation répétée retrouvent l'état enregistré sans effet
fournisseur supplémentaire.

## Mutation CalendarEvent

Aucune mutation sur approbation, dispatch ou ACK. Après preuve applied, le service
réutilise `InboundCalendarImportService`, son hash et ses règles de révision.
Create emploie uniquement ExternalEventId retourné par le simulateur, conserve
le fournisseur/calendrier exact et rattache les références CRM explicites du plan.
CalendarEvent n'a pas de champ CompanyId : cette référence reste dans le plan.

Update conserve CalendarEvent.Id, OpportunityId, ContactId et les identités de
série/occurrence. Les références historiques sont préservées même si le contenu
proposé du plan porte d'autres références. Toute version ou mutation CRM
incompatible produit un conflit de réconciliation, conserve la preuve fournisseur
et n'écrase rien. Si calendar.sync a déjà importé exactement la même identité,
révision et contenu, l'événement est réutilisé. Une divergence de références
locales sur la cible update reste un conflit.

Traitement de la preuve, état technique et mutation métier sont atomiques dans
PostgreSQL. Un échec au commit laisse le reçu non traité et l'événement inchangé ;
une réconciliation ultérieure peut terminer sans nouveau dispatch.

## Transport, invitations et limites

`UnavailableCalendarEventTransport` est l'enregistrement par défaut, sans option
d'activation HTTP. `InMemoryCalendarEventTransport` est injecté explicitement dans
les tests. Il ne possède aucune capacité réseau ou d'envoi de notification.
La commande exige la constante `notifications: "disabled"`. Aucun participant,
attendee, sendUpdates ou notifyAttendees n'est ajouté aux snapshots ou DTO.

Le registre du simulateur permet de relire une preuve par clé sans réexécuter
l'effet et bloque la réutilisation d'une clé avec une autre commande. Ce registre
est volatile : le redémarrage de l'application CRM conserve l'intention,
la tentative et les reçus SQL, mais un redémarrage du simulateur peut perdre
une preuve non encore reçue. Dans ce cas l'issue reste inconnue. La garantie
est une tentative CRM au maximum ; aucun exactly-once distribué n'est affirmé.

L'absence de participants dans le CRM n'est pas une preuve d'absence de
notifications implicites chez un fournisseur réel. Une future validation n8n
devra documenter ces effets séparément, démontrer la suppression effective des
invitations, la concurrence fournisseur et la récupération durable des résultats.
Elle devra conserver les contrats fermés, HTTPS/HMAC, provenance persistée,
idempotence et absence de retry automatique. La phase 9.6.4.2 n'est pas commencée.

## API humaine

- POST `/api/calendar-event-deliveries/from-plan/{planId}` ;
- GET `/api/calendar-event-deliveries/{id}` ;
- POST `/api/calendar-event-deliveries/{id}/dispatch` ;
- POST `/api/calendar-event-deliveries/{id}/reconcile`.

Les mutations exigent cookie humain, antiforgery et workspace autorisé. Les routes
n'exposent aucun DTO d'événement arbitraire à exécuter. HMAC reste réservé au
callback machine. Aucun nouveau parcours Blazor n'est ajouté.

## Migration et validation

Migration additive : `20261022120733_Phase9641CalendarEventDeliveries`.
Nouvelle table, clé alternative du plan, extension du catalogue technique des
opérations et des slots de reçus, triggers de liaison/immutabilité et tentative
unique. Les anciennes migrations sont inchangées. Aucun backfill métier.
Down refuse toute intention ou opération calendrier conservée, même sans
intention liée lorsqu'une preuve existe. Down/Up vide conserve les données
historiques et restaure les règles des reçus email.

Journaux, TRX et manifestes hors Git :
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-6-4-1-20261022/`.

Toutes les migrations de validation utilisent des PostgreSQL 18 jetables en
Testcontainers. La génération EF utilise Testing et une connexion de conception
factice, sans connexion à Development.

Couverture dédiée :

| Classe | Scénarios |
| --- | ---: |
| CalendarEventDeliveryTests | 15 |
| CalendarEventDeliveryConcurrencyTests | 13 |
| CalendarEventDeliverySecurityTests | 28 |
| CalendarEventDeliveryContractTests | 27 |
| CalendarEventDeliveryMigrationTests | 3 |
| Total | **86** |

Historique conservé :

- `dedicated-01` : 0/27. La liaison à l'opération utilisait son Guid avant
  attribution par EF ; attribution explicite avant construction de l'intention.
- `dedicated-02` : 51/53. Deux assertions du harnais corrigées : la corruption
  de catégorie est déjà refusée par PostgreSQL et l'échec de commit remonte
  comme exception dans TestServer. Les garanties produit sont conservées.
- `dedicated-03` : 82/82 après ajout des contrats et migrations.
- `dedicated-04` : 83/84. Fixture du circuit corrigée pour employer une catégorie
  Automation valide et une règle persistée. `dedicated-circuit-fix` : 1/1.
- `dedicated-final` : **86/86**, 0 échec, 0 ignoré. Ajout des décisions futures,
  de la non-application prouvée, des références CRM non nulles et de la reprise
  dans un nouvel hôte. Le seul avertissement du nouveau fixture (capture du
  paramètre constructeur) est ensuite supprimé via l'accès au fixture de base.
- Build initial : 0 erreur, deux avertissements CS8618 historiques dans
  Settings.razor. Une compilation intermédiaire a attendu la libération d'une
  DLL de test encore utilisée, avec sept avertissements MSB3026 ; elle est
  remplacée par le build final après terminaison des tests.
- `build-final` : **0 erreur, 0 avertissement**.
- `ef-final` : **HasPendingModelChanges=false**. L'outil local EF 10.0.10 signale
  sa différence avec le runtime 10.0.12 ; aucun outil n'est mis à jour.
- `regressions-01` : **1197/1198**, 0 ignoré, durée console 7 min 31 s.
  Phase 8, email.send/email.sync, calendar.sync/9.6.3, intégrations, migrations
  historiques concernées et nouveaux tests sont inclus. Le seul échec est
  l'index relatif de la chaîne des migrations dans
  `SourceCollectionJobMigrationTests.UpgradePreservesEveryExistingTableAndDownUpIsCoherent`.
  Les positions sont décalées d'une unité et la vérification explicite de 9.6.3
  est conservée avant la nouvelle dernière migration. Aucun code produit modifié.
- `build-frozen` après cette correction : **0 erreur, 0 avertissement**.
- `regressions-fix` : **28/28**, 0 échec, 0 ignoré, durée console 2 min 40 s.
  Le scénario en échec dans regressions-01 est explicitement présent et Passed
  dans ce TRX. Bilan : **1198 scénarios distincts de régression validés**.
  Les **86/86 dédiés** sont aussi Passed dans regressions-01, après la correction
  de l'avertissement du fixture.
- `ef-frozen` reconfirme **HasPendingModelChanges=false** sur les binaires figés.

Les attentes historiques de dernière migration ont été adaptées en lot.
Les bornes des downgrades historiquement refusés restent inchangées. Les deux
inventaires Phase 7 excluent la nouvelle table postérieure vide ; les comparaisons
des anciennes lignes ne sont pas assouplies.

La suite globale unique est lancée avec `--no-build --no-restore`, sans filtre.
Les manifestes `sources-global-before.json` et `dll-global-before.json` portent
sur **697 fichiers source/projet et 3 DLL Release**, recontrôlés avant lancement.

- `global-final` : **1880/1880**, **0 échec, 0 ignoré**, code de sortie **0**,
  durée console **19 min 30 s**. TRX `Completed`, 1880 résultats individuels
  `Passed`, aucun error, timeout, aborted ou notExecuted.
  Début : `2026-10-22T14:31:38.4674178+02:00` ; fin :
  `2026-10-22T14:51:09.9556395+02:00`. Une seule suite globale lancée.
- Comparaison après terminaison : **697/697 sources et 3/3 DLL identiques**.
  Les manifestes `sources-global-after.json` et `dll-global-after.json` sont
  conservés hors Git. Seul le présent rapport est complété après le gel.
- `git diff --check` réussi ; inventaire ci-dessous exactement identique à
  `git status --short` ; **index vide**. Aucun secret réel identifié dans le
  périmètre, aucun journal, TRX, dump, clé privée ni artefact de build dans Git.
- `main`, HEAD, `origin/main` et la branche distante reconfirmée restent à
  `3c021f5f51bf8571fe9aec92338e9ed0f0b7bd3c`.
- Les conteneurs PostgreSQL de test sont arrêtés à la fin. Aucun service
  Development démarré, aucune migration Development invoquée, aucun n8n réel
  ni fournisseur externe utilisé, aucune invitation ou notification envoyée.

Clôture locale 9.6.4.1 terminée. Aucun staging, commit ou push.
La phase 9.6.4.2 n'est pas commencée.

## Inventaire Git

30 fichiers modifiés et 17 nouveaux, soit 47 fichiers ; index vide.

```text
 M src/ProspectionCrm.Api/Data/Configurations/IntegrationConfiguration.cs
 M src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
 M src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Calendar/CalendarEventPlanService.cs
 M src/ProspectionCrm.Api/Services/Calendar/InboundCalendarImportService.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationStore.cs
 M tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/CalendarEventPlanFixture.cs
 M tests/ProspectionCrm.Api.Tests/CalendarEventPlanMigrationTests.cs
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
 M tests/ProspectionCrm.Api.Tests/SecurityTestHost.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingMigrationTests.cs
?? docs/phase-9-6-step-4-1-validation.md
?? src/ProspectionCrm.Api/Controllers/CalendarEventDeliveriesController.cs
?? src/ProspectionCrm.Api/Data/Configurations/CalendarEventDeliveryConfiguration.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261022120733_Phase9641CalendarEventDeliveries.Designer.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261022120733_Phase9641CalendarEventDeliveries.cs
?? src/ProspectionCrm.Api/Dtos/Integrations/CalendarEventContracts.cs
?? src/ProspectionCrm.Api/Entities/CalendarEventDelivery.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarEventDeliveryService.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarEventDispatcher.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarEventReconciliation.cs
?? src/ProspectionCrm.Api/Services/Calendar/InMemoryCalendarEventTransport.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventDeliveryConcurrencyTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventDeliveryContractTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventDeliveryFixture.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventDeliveryMigrationTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventDeliverySecurityTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventDeliveryTests.cs
```
