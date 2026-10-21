# Phase 9.6.1 — Comptes calendaires et synchronisation entrante

**Implémentation terminée ; validation locale finale entièrement verte.**
85/85 tests dédiés, 502 scénarios distincts de régression validés, build Release final
0 erreur et 0 avertissement. La suite globale finale autorisée lors de la reprise termine
à **1 726/1 726**, **0 échec, 0 ignoré**. TRX et code de sortie 0 concordants ;
**58/58 empreintes source et 3/3 empreintes DLL inchangées** pendant la passe.
HasPendingModelChanges=false. L'historique des passes rouges et du correctif du test
Automation est conservé ci-dessous. Aucun staging, commit ou push.

## Référence et limites

Départ vérifié sur `main`, HEAD `dd9bc587e54a2a5efec987a4bd12b2830a745fda`,
arbre propre et index vide. Aucun AGENTS.md dans le dépôt ou ses parents.
Audit de CalendarEvent, configurations/DTO/services/routes, Workspace/Contact/Opportunity,
EmailAccount/SyncState/SyncJob, InboundEmailImportService, opérations/tentatives/reçus,
HMAC et authentification humaine, migrations et rapports 9.5.1/9.5.4.1.

Aucun fournisseur, OAuth, credential, workflow n8n, calendrier réel ou envoi externe.
`ICalendarSyncTransport` est enregistré exclusivement avec `UnavailableCalendarSyncTransport`.
Pas de worker, de transport HTTP calendrier ni d'option permettant son activation.
Les tests injectent un transport synthétique en mémoire. Docker et PostgreSQL Development
étaient déjà démarrés ; seuls des PostgreSQL 18 Testcontainers jetables sont utilisés
pour les tests. Aucune migration ni écriture métier Development, aucun redémarrage
API/Blazor/proxy/n8n. Aucune modification de AutomationSafetyPolicy.
Aucun staging, commit ou push. La phase 9.6.2 n'est pas commencée.

## Modèle et compatibilité

| Objet | Responsabilité |
| --- | --- |
| CalendarAccount | Identité fournisseur stable, connexion logique, statut, révision, archivage |
| Calendar | Calendrier externe identifié dans un compte, fournisseur/connexion liés, fuseau facultatif, révision et archivage |
| CalendarSyncState | Un état par calendrier, curseur opaque, révision, dernière tentative et dernier succès |
| CalendarSyncJob | Intention manuelle, révisions figées compte/calendrier/curseur, limite, opération, statut et compteurs |
| CalendarEvent | Événement historique ou événement importé avec révision, empreinte, statut et identité d'occurrence |

Un workspace peut déclarer plusieurs comptes d'un même fournisseur, chacun contenant
plusieurs calendriers. Unicité du compte `(WorkspaceId, ProviderCode, ExternalAccountId)` ;
unicité du calendrier `(CalendarAccountId, ExternalCalendarId)` y compris après archivage.
Les identités, le workspace, le fournisseur et la connexion ne sont pas modifiables via
les DTO humains. La connexion logique doit correspondre aux IntegrationOptions du workspace.
Pas de secret, refresh token ou référence de credential dans ces modèles/DTO.

Statuts compte : active, disabled, revoked ; révocation irréversible dans cette tranche.
Statuts calendrier : active, disabled. Archivage indépendant et réversible. Toute
modification humaine exige la révision courante et l'incrémente ; un changement suivi
d'une restauration invalide donc un job ancien. L'archivage ne supprime aucun historique.

CalendarEvent.CalendarId est nullable. Aucune ancienne ligne n'est attribuée à un compte
ou calendrier inventé, ni fusionnée avec un import. L'unicité historique workspace /
fournisseur / ExternalEventId reste applicable aux lignes sans calendrier ; les imports
utilisent `(CalendarId, ExternalEventId)`. Une FK composite impose workspace et fournisseur
identiques au calendrier, lui-même lié au compte par workspace/connexion/fournisseur.
Les références Contact et Opportunity restent nulles à l'import ; une mise à jour externe
ne les écrase pas. Aucun Contact, Company, Opportunity ou CrmTask n'est créé.

Les lectures `/api/calendar-events` et `/{id}`, leurs filtres opportunityId/contactId/from/to
et leur sémantique historique sont conservés. Les DTO ajoutent CalendarId, ExternalRevision,
StatusCode, SeriesId et OccurrenceId. Une annulation est lisible sous `cancelled`.

## Contrat calendar.sync v1

Enveloppe fermée : version 1, type calendar.sync, messageId, operationId, correlationId,
payload ; un résultat ajoute commandMessageId. GUID non vides au format D.
Commande : calendarRef, jobRef, previousCursor, maxItems (1 à 100).
Résultat : calendarRef, jobRef, outcome (`succeeded` ou `failed`), nextCursor, events.
Un résultat failed doit avoir une liste vide et nextCursor null ; un succès vide peut
avancer le curseur. Les curseurs sont opaques, non secrets, limités à 2 048 caractères.
Le résultat n'a aucun WorkspaceId ou identifiant Contact/Opportunity libre.

Limites : commande 16 Kio, résultat canonique et brut 512 Kio, profondeur JSON 6,
100 événements maximum et au plus la limite du job. Champs inconnus, propriétés
répétées, types erronés, versions inconnues, identifiants dupliqués sont refusés.
Le parseur fermé `ClosedIntegrationJson` est partagé avec email.sync. L'authentification
machine HMAC existante reste obligatoire sur les octets reçus ; seul le type calendrier
rejoint les contrats autorisés à dépasser la petite enveloppe probe. TLS, audience,
horodatage, signature et comparaison constante ne sont pas affaiblis.

Chaque événement porte externalEventId (500), revision entière monotone >= 0, title (500),
description facultative (10 000), location facultative (500), startsAt, endsAt, isAllDay,
timeZoneId IANA facultatif (100), status confirmed/cancelled et éventuellement seriesId /
occurrenceId (500 chacun). Les chaînes NUL, identités blanches et titres blancs sont refusés.
Les listes sont triées par identité avant comparaison canonique.

## Dates, annulations et récurrences

Les instants doivent porter Z ou un décalage explicite ; jamais de date-heure locale naïve.
UTC est accepté comme instant absolu avec un fuseau IANA d'affichage. Un décalage local
non nul doit correspondre au fuseau fourni. Les identifiants Windows et fuseaux inconnus
sont refusés. Normalisation UTC à la microseconde PostgreSQL, puis contrôle d'une durée
strictement positive. Une durée disparaissant à cette précision est rejetée avant SQL.

La fin est exclusive. Pour all-day, un fuseau IANA est obligatoire et les deux instants
convertis dans ce fuseau doivent correspondre à des minuits de dates distinctes croissantes.
Cela autorise les journées de 23 ou 25 heures sans leur imposer 24 heures UTC. Les tests
couvrent les transitions Europe/Paris de mars et octobre, l'heure locale inexistante et
l'heure répétée désambiguïsée par son offset. Les bornes locales restent reconstructibles
à partir des instants UTC et du fuseau ; les filtres historiques ne sont pas réinterprétés.

Une annulation est un événement complet avec status cancelled et une révision ; il peut
être enregistré même sans occurrence antérieure. Pas de suppression silencieuse ni
d'interprétation de l'absence d'un événement comme une annulation.

Aucune RRULE acceptée ni expansion. Une occurrence récurrente requiert simultanément
seriesId et occurrenceId stables, en plus d'externalEventId stable dans le calendrier.
Le couple série/occurrence est unique et immuable, même si l'occurrence est déplacée.
Un changement d'identité ou un alias sous une autre identité externe est un conflit.
Les représentations non prises en charge sont refusées en bloc, sans curseur avancé.

## Import, concurrence et cycle durable

```text
intention + IntegrationOperation pending (transaction unique)
 -> tentative started + job running (commit avant transport)
 -> ACK : awaiting-result ; réseau ambigu/crash : dispatch-unknown
 -> résultat HMAC : receipt durable + result-received
 -> traitement humain : import atomique + curseur + job/opération terminaux
```

Le cycle suit email.sync ; les primitives de verrouillage du reçu/horodatage PostgreSQL
et la frontière de dispatch `IntegrationSyncDispatch` sont partagées. Pas de transaction
maintenue pendant le transport. Le dispatcher ne réessaie jamais : délai indépendant
30 secondes et finalisation 5 secondes après le commit du début de dispatch.

Ordre de verrouillage : workspace SHARE pour les mutations métier, compte, calendrier,
job, opération, reçu, état, événements. Le verrou compte sérialise brièvement les calendriers
d'un même compte ; il est libéré pendant le transport. Un index unique partiel limite à
un job actif par calendrier. Un FK SQL supplémentaire lie job/opération par workspace,
connexion et type, sans changer les anciennes opérations à connexion nullable.

L'import compare le lot entier avant toute insertion ou mise à jour :

- identité absente : insertion ;
- révision égale et contenu canonique identique : rejeu sans écriture métier ;
- révision supérieure et identité d'occurrence inchangée : mise à jour, ID CRM conservé ;
- révision inférieure, contenu contradictoire à révision égale, empreinte stockée incohérente
  ou conflit d'occurrence : échec contrôlé, compteurs zéro, aucun import partiel.

Le job expose CreatedCount, ExistingCount, UpdatedCount et ConflictingExternalEventId.
Le curseur, sa révision et LastSuccessfulSyncAt changent seulement dans le commit complet
d'un succès. Un conflit métier termine le job et marque son reçu traité, sans changer
le curseur ; un nouveau job explicite est nécessaire après diagnostic/correction.
Une erreur SQL, annulation du token ou crash avant commit annule toutes les écritures.
Une réponse HTTP perdue après commit autorise un second process sans doublon.

Réception : contrôle de la liaison HMAC workspace/connexion, des identifiants du job/opération,
de la limite et de la phase attendue. Un même messageId et contenu canonique est un rejeu ;
un contenu différent est un conflit. Le callback peut précéder l'ACK ; cet ACK ne remplace
pas le résultat métier. Un résultat tardif après dispatch-unknown peut être traité.
Après annulation ou échec terminal, il ne laisse qu'une trace traitée et ne rouvre pas le job.

Recovery explicite `resolve-unknown` : après deux minutes, une tentative started peut être
classée unknown sans retransmission. Une tentative encore en vol peut terminer ensuite
sans écraser cette décision. Une intention inconnue reste active jusqu'à réception tardive
ou annulation humaine ; il n'existe aucun retry automatique ni récupération par GET.

## API humaine

| Route | Usage |
| --- | --- |
| GET/POST /api/calendar-accounts | Liste / création |
| GET/PUT /api/calendar-accounts/{id} | Lecture / modification avec révision |
| POST /api/calendar-accounts/{id}/calendars | Déclarer un calendrier |
| GET /api/calendars et GET/PUT /api/calendars/{id} | Liste, lecture, modification/archivage |
| POST /api/calendars/{id}/sync-jobs | Intention manuelle |
| GET /api/calendar-sync-jobs/{id} | Lecture du job |
| POST /api/calendar-sync-jobs/{id}/dispatch | Refus par défaut : transport indisponible |
| POST /api/calendar-sync-jobs/{id}/process-result | Import d'un résultat durable |
| POST /api/calendar-sync-jobs/{id}/resolve-unknown | Recovery explicite sans renvoi |
| POST /api/calendar-sync-jobs/{id}/cancel | Annulation sans suppression |

Cookie humain/workspace et antiforgery existants pour les mutations. Le schéma machine
ne donne pas accès aux routes humaines ; un cookie seul ne permet pas un callback.
DTO humains fermés, révision et décision d'archivage explicitement requises pour les PUT.
GET sans effet secondaire. Les corps bruts des reçus ne sont pas exposés par les routes.

## Migration

`20261021110428_Phase961InboundCalendarSync` ajoute quatre tables, les colonnes nullable
CalendarEvent et leurs contraintes/index/FK ; élargit le type des IntegrationOperations.
Aucune migration historique modifiée. Aucun backfill ni mise à jour de donnée historique.
Down est autorisé seulement en absence de données calendrier nouvelles ou d'opérations
calendar.sync ; sinon il refuse transactionnellement plutôt que perdre identités et curseurs.
Les anciennes contraintes sont restaurées au Down vide. Les tests comparent toutes les
lignes des tables historiques en JSON avant/après upgrade et Down/Up, y compris événement
legacy rattaché à une opportunité, message outbound fictif, demande humaine et reçu email.send.
Les seules colonnes nouvelles CalendarEvent sont exclues de cette comparaison historique.
Tests également sur base vierge, isolation FK workspace/fournisseur, unicité et refus du Down peuplé.

## Validation locale et historique

Journaux/TRX hors Git : `%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-6-1-20261021`.
Les deux premiers journaux build/migration sont dans le dossier parent (préfixe phase961).

- Premier build applicatif : 0 erreur, 2 avertissements historiques CS8618 dans Settings.razor.
- Première compilation des tests : 5 erreurs d'adaptation des fixtures (anciens noms email et
  accès WorkspaceId de CrmTask corrigé via Opportunity). Aucun changement métier historique.
- Passe calendar-01 : 33/82, 49 échecs ; liaison de type email.sync résiduelle, DTO humains
  insuffisamment fermés/champs obligatoires, effets secondaires de ces refus sur les scénarios.
- Passe calendar-02 : 74/82, 8 échecs ; 7 contrôles DTO humains et 1 fixture de migration
  (ContentFingerprint requis pour une AutomationActionRequest email.send).
- Passe calendar-03 : 83/84, 1 échec ; fixture du reçu email.send exigeant EvidenceNumber=1.
  Les contraintes historiques ont été respectées, pas affaiblies.
- Revue : durée submicroseconde désormais refusée après normalisation ; cas dédié ajouté.
- Les assertions historiques « dernière migration » et la liste ordonnée Phase 7 sont
  adaptées à 9.6.1 ; les assertions de cibles de downgrade restent celles des phases concernées.

- Tests dédiés finaux : **85/85**, 0 échec, 0 ignoré, 31 secondes (calendar-final.trx).
- Build Release final après stabilisation du test historique : **0 erreur, 0 avertissement**
  (build-after-clock-test.log). Le build précédant la globale était également 0/0 (build-final.log).
- EF : **HasPendingModelChanges=false**, confirmé par les tests et la commande CLI sans build.
  Avis outil EF 10.0.10/runtime 10.0.12, sans incidence sur le modèle.
- Development vérifié en lecture seule : dernière migration `20261021075256_Phase9541EmailSendDeliveries`.
- Empreintes avant régressions : **56 fichiers source**, rapport exclu ; aucune variation au contrôle initial.
- Régressions initiales : **500/502**, 2 échecs, 0 ignoré, 6 min 31 s (regressions.trx).
  Les deux échecs sont les snapshots Phase 7 comparant les noms des nouvelles tables vides.
  Corrections limitées aux tests : exclusion des quatre tables calendrier inexistantes avant upgrade.
  Aucun changement applicatif après les 85 tests dédiés réussis.
- Revalidation des deux classes Phase 7 : **30/30**, 0 échec, 0 ignoré, 2 min 54 s
  (regressions-corrected.trx). Les **502 scénarios distincts de régression** sont validés
  par la passe initiale et cette revalidation ciblée ; aucune deuxième passe large.
- Gel final : **57 empreintes source et 3 empreintes DLL**, rapport exclu, après le build final.
- Première suite globale, seule passe autorisée dans la tranche initiale : `dotnet test ProspectionCrm.slnx
  --configuration Release --no-build --no-restore` (logger TRX et results-directory hors Git).
  Résultat : **1 724/1 726**, **2 échecs**, **0 ignoré**, 22 min 39 s ; code de sortie 1.
  TRX : 1 726 exécutés, 0 erreur technique, timeout ou abandon. Début
  `2026-10-21T13:20:20.0508101+02:00`, fin `2026-10-21T13:43:01.4148462+02:00`.
  Les 85 tests calendrier passent aussi dans cette globale.
- Reproduction ciblée sans rebuild : **2/3**, mode automatic encore en échec
  (automation-diagnostic.trx). Instrumentation de l'assertion : **3/3**
  (automation-timestamps.trx), confirmant l'intermittence.
- Après stabilisation de l'horloge du test : **3/3**, 0 échec, 0 ignoré, 3 secondes
  (automation-corrected.trx). Aucun autre changement après la première globale ; aucune seconde
  globale dans cette tranche initiale.
- Les **57/57 fichiers source du manifeste initial** restent inchangés ; un fichier de test
  historique supplémentaire a été modifié après cette passe. Les DLL API/Blazor restent
  identiques ; seule la DLL de tests a été reconstruite. Le périmètre final contient
  **58 fichiers source**, plus ce rapport, et possède un manifeste final distinct :
  **58/58 empreintes inchangées** au contrôle de clôture.
- `git diff --check` réussi ; inventaire vérifié, index vide. Aucun secret réel ou artefact privé
  identifié dans le périmètre. Aucune migration historique, configuration fournisseur ou policy modifiée.
- Dernier contrôle des services : seul PostgreSQL Development reste démarré (déjà actif au départ) ;
  aucun listener 5678/7075/7096/7443. Son historique EF reste sur Phase9541EmailSendDeliveries.

## Diagnostic de la première suite globale

Deux échecs signalés dans `AutomationPhase8EndToEndTests.EventToDispatchWorkerDecisionEffectAndSupervision`,
variantes `manual` et `automatic`, à la ligne 72 : `CompletedLast24HoursCount` attendu 1,
obtenu 0. Au lancement de cette première passe, le test et les services Automation
concernés étaient inchangés par cette phase.
Bilan confirmé dans le TRX : deux échecs sur 1 726, puis reproduction ciblée en mode automatic.

Piste examinée : `AutomationJobQueue` écrit CompletedAt avec `clock_timestamp()` PostgreSQL,
alors que le test fige `Clock.Now = DateTimeOffset.UtcNow` avant la lecture de supervision.
`AutomationSupervisionService` filtre `CompletedAt <= now`. Un léger décalage entre horloges
peut donc exclure temporairement un job terminé. Une mesure directe en lecture seule sur
un PostgreSQL jetable montre une avance maximale observée de 0,081 ms par rapport à l'instant
hôte après réception (5 échantillons, hors coût de première connexion). Cela étaye la piste,
mais ne prouve pas la valeur exacte des horloges au moment des assertions échouées.
Les mesures privées sont dans `clock-direct-diagnostic.json` ; aucun secret n'y figure.
Aucune mutation de donnée métier, correction Automation ou modification de binaire pendant
la suite globale. Après sa fin, seul le test historique est stabilisé : il vérifie le statut
terminal et la présence de CompletedAt, puis place sa borne d'observation au maximum de
l'heure hôte et de la fin persistée avant de lire la supervision. Aucun délai arbitraire,
aucun compteur attendu assoupli, aucune modification du service ou de AutomationSafetyPolicy.
L'assertion conserve un diagnostic détaillé (mode, statut, CompletedAt, borne, compteur).
Le passage 3/3 après cette modification valide le scénario corrigé ; il ne remplace pas
le bilan rouge de la première suite globale. La validation globale supplémentaire a été
autorisée séparément lors de la reprise ; son bilan est consigné dans la section suivante.

## Reprise : validation globale finale du 21 octobre 2026

Une seule suite globale exécutée lors de cette reprise, sans relance des tests dédiés
ou des régressions séparément, sans changement de source et sans nouvelle reconstruction.
Les 58 empreintes du manifeste de clôture précédent correspondent aux sources présentes.
Le build Release final 0/0 et le contrôle ciblé 3/3 sont conservés. Le PDB Release contient
pour AutomationPhase8EndToEndTests.cs la même empreinte SHA-256 que la source corrigée :
`34131ED19AC4EC13B0BCACF9FD1AFFF5008ED1F6F6EDB9C78BE079BDAA5A4397`.
Le correctif est donc cohérent avec les derniers binaires Release utilisés.

Commande unique : `dotnet test ProspectionCrm.slnx --configuration Release --no-build
--no-restore`, complétée par le logger TRX et le répertoire de résultats hors Git.

- Résultat console : **1 726 réussites / 1 726 tests**, **0 échec, 0 ignoré**,
  durée affichée **20 min 29 s**, code de sortie **0**.
- TRX : résultat **Completed**, **1 726 résultats individuels Passed**, total/exécutés/réussis
  **1 726/1 726/1 726** ; failed, notExecuted, error, timeout et aborted tous à **0**.
- Début TRX : `2026-10-21T14:18:46.1368649+02:00` ;
  fin : `2026-10-21T14:39:17.2174233+02:00`.
- Comparaison SHA-256 avant/après : **58/58 fichiers C# inchangés**, rapport exclu ;
  **3/3 DLL Release inchangées** (API, Blazor et tests).
- EF reconfirmé après la passe, avec `migrations has-pending-model-changes` en Release
  `--no-build` : **HasPendingModelChanges=false**, code de sortie 0. Aucun update de base.
- Development contrôlé en lecture seule : dernière migration toujours
  `20261021075256_Phase9541EmailSendDeliveries`. Aucune migration Development appliquée.
- Inventaire exact conservé : **59 fichiers, 32 modifiés et 27 nouveaux** ; index vide.
  `git diff --check` réussi. Aucun secret réel ou artefact privé identifié ; seuls
  58 fichiers C# et ce rapport sont présents dans le périmètre. Aucun fichier historique
  de migration, AutomationSafetyPolicy ou configuration fournisseur modifié.
- Aucun fournisseur réel ni n8n sollicité, aucun redémarrage de service, aucun staging,
  commit ou push. La phase 9.6.2 n'est pas commencée.

Preuves conservées hors Git :
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-6-1-final-20261021-141832/` :
`global-final.log`, `global-final.trx`, `start.txt`, `finish.txt`, `exit-code.txt`,
`summary.json`, `source-before.json`, `source-after.json`, `binary-before.json`,
`binary-after.json`, `corrected-test-pdb-proof.json` et `ef-model-check.log`.
Seul ce rapport est complété après la passe ; aucune source n'a été retouchée.

## Inventaire Git

32 fichiers suivis modifiés, 27 nouveaux, **59 fichiers au total** (rapport inclus).
Index vide ; HEAD et origin/main restent `dd9bc587e54a2a5efec987a4bd12b2830a745fda`.
18 fichiers de tests historiques adaptent les attentes de version/liste de migrations et
les deux comparaisons Phase 7 aux seules tables présentes avant upgrade (exclusion des quatre
nouvelles tables vides ; les lignes historiques restent comparées à l'identique).
Un 19e fichier historique, AutomationPhase8EndToEndTests.cs, stabilise la borne d'observation
après les échecs intermittents de la globale, comme décrit ci-dessus.
Les deux fichiers de migration 9.6.1 et le snapshot sont les seuls fichiers de migration concernés.
Aucun log, TRX, dump, credential ou artefact local inclus.

```text
 M src/ProspectionCrm.Api/Data/Configurations/CalendarEventConfiguration.cs
 M src/ProspectionCrm.Api/Data/Configurations/IntegrationConfiguration.cs
 M src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
 M src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
 M src/ProspectionCrm.Api/Dtos/CalendarEvents/CalendarEventDto.cs
 M src/ProspectionCrm.Api/Dtos/Integrations/EmailSyncContracts.cs
 M src/ProspectionCrm.Api/Entities/CalendarEvent.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/CalendarEventService.cs
 M src/ProspectionCrm.Api/Services/Email/EmailSyncDispatcher.cs
 M src/ProspectionCrm.Api/Services/Email/EmailSyncLocks.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationStore.cs
 M tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationPhase8EndToEndTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
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
?? docs/phase-9-6-step-1-validation.md
?? src/ProspectionCrm.Api/Controllers/CalendarAccountsController.cs
?? src/ProspectionCrm.Api/Data/Configurations/CalendarSyncConfiguration.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261021110428_Phase961InboundCalendarSync.Designer.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261021110428_Phase961InboundCalendarSync.cs
?? src/ProspectionCrm.Api/Dtos/CalendarEvents/CalendarAccountDtos.cs
?? src/ProspectionCrm.Api/Dtos/Integrations/CalendarSyncContracts.cs
?? src/ProspectionCrm.Api/Dtos/Integrations/ClosedIntegrationJson.cs
?? src/ProspectionCrm.Api/Entities/Calendar.cs
?? src/ProspectionCrm.Api/Entities/CalendarAccount.cs
?? src/ProspectionCrm.Api/Entities/CalendarSyncJob.cs
?? src/ProspectionCrm.Api/Entities/CalendarSyncState.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarAccountService.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarCatalogService.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarSyncDispatcher.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarSyncLocks.cs
?? src/ProspectionCrm.Api/Services/Calendar/CalendarSyncService.cs
?? src/ProspectionCrm.Api/Services/Calendar/InboundCalendarImportService.cs
?? src/ProspectionCrm.Api/Services/Integrations/IntegrationSyncDispatch.cs
?? src/ProspectionCrm.Api/Services/Integrations/IntegrationSyncLocks.cs
?? tests/ProspectionCrm.Api.Tests/CalendarAccountSecurityTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarSyncConcurrencyTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarSyncContractTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarSyncFixture.cs
?? tests/ProspectionCrm.Api.Tests/CalendarSyncImportTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarSyncLifecycleTests.cs
?? tests/ProspectionCrm.Api.Tests/CalendarSyncMigrationTests.cs
```

## Conditions pour 9.6.2 (hors périmètre)

Un futur adaptateur devra résoudre la cible calendrier autorisée, fournir les offsets/fuseaux et
révisions monotones selon ce contrat, conserver les identités de série/occurrence, signer les
callbacks avec le HMAC existant et respecter le dispatch sans retry aveugle. Aucun workflow,
credential ou fournisseur n'est préparé ni activé ici.
