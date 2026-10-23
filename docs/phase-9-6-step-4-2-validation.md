# Phase 9.6.4.2 — Transport HTTP calendrier et simulateur SQLite durable

**Validation terminée : 137/137 JavaScript, 115 scénarios .NET dédiés distincts,
335/335 régressions ciblées et globale finale unique 1909/1909. Build Release
sans erreur ni avertissement ; HasPendingModelChanges=false ; 711/711 sources,
3/3 DLL et 61/61 tables Development inchangées pendant les tests finaux.**

Validation locale reprise le **23 octobre 2026**, sur l'implémentation laissée
le 22 octobre. Référence : `main`,
`1a6e1a8984de92d7c038631186e95aaf37200333`.
Aucun staging, commit, push, reset de base ou de volume. Aucune phase suivante.

## Périmètre et reprise

L'audit initial retrouve six fichiers suivis modifiés et neuf nouveaux fichiers.
Le transport, le simulateur, les fixtures et les tests sont conservés sans
réimplémentation. Aucun fichier produit ni test du dépôt n'a été modifié durant
la reprise ; seul ce rapport est ajouté.

Les preuves et scripts de validation restent hors Git dans :
`%LOCALAPPDATA%/ProspectionCrmOperations/phase-9-6-4-2-20261022/`.
Ce dossier contient des artefacts privés : ne pas le publier en bloc.

La sauvegarde PostgreSQL initiale, 318570 octets, a de nouveau été vérifiée :
SHA-256 `8743AF3C29063F4655AE68E5C8CCDC206F269D907C4D001B8C8F8D82FE286E07`.
Le journal initial atteste la lecture du TOC (548 entrées) et le décodage complet
sans restauration. Les migrations `20261021144859_Phase963CalendarEventPlans`
et `20261022120733_Phase9641CalendarEventDeliveries` étaient déjà appliquées.
Elles n'ont pas été réappliquées ; aucune nouvelle migration n'est ajoutée.

Les 58 tables historiques, hors historique EF, étaient strictement identiques
avant/après ces migrations. La base passe de 59 à 61 tables, historique EF inclus.
À la reprise, les 61 empreintes correspondent à l'état après migration, sauf
les paramètres Automation temporairement activés lors de la session précédente.
PostgreSQL et n8n étaient arrêtés ; seuls leurs conteneurs existants ont été
redémarrés. L'opérateur s'est reconnecté dans le navigateur pour les routes
humaines ; aucun contournement de l'authentification ou de l'antiforgery.

## Transport et contrat

`HttpCalendarEventTransport` utilise le transport HTTPS/HMAC commun et exige
les deux activations `Integrations.Enabled` et `Integrations.CalendarEvent.Enabled`.
Le défaut reste désactivé, avec endpoint vide. Endpoint administrateur HTTPS
sans userinfo, query ni fragment ; aucune URL fournie par la commande.

Après l'enregistrement durable de la tentative, une seconde transaction valide
le workspace, la connexion, le plan approuvé, la demande humaine, les settings,
les comptes/calendriers, les quotas et l'égalité de la commande complète avec
la commande persistée. Elle se termine avant tout accès réseau. Le contrôle
de quota exclut sa propre tentative autorisée. Le dispatch conserve le gate
commun sans acquisition imbriquée.

Le corps UTF-8 exact est signé. Un 202 complet vaut ACK, jamais preuve d'effet.
Timeout, réponse tronquée, compression inattendue, corps d'ACK trop grand ou
erreur HTTP restent inconnus ; même un 409 réseau n'est pas un conflit fournisseur.
Une intention conserve au maximum une tentative, sans retry automatique.

Le contrat fermé version 1 réutilise les snapshots et hashes C# ; les fixtures
golden vérifient UTF-8, échappements, microsecondes et hashes octet pour octet.
Limite commande/preuve : 65536 octets. La constante `notifications: "disabled"`
est obligatoire. Participants, attendees et commandes de notification sont absents
et refusés comme propriétés inconnues.

## Simulateur et installation locale

Les fichiers de `docs/integrations/calendar-event-demo/` constituent le node
local n8n, le contrat, le registre SQLite, les tests et le workflow modèle inactif.
Le workflow ne contient ni secret ni identifiant de credential. Il requiert
une configuration administrateur des cinq identités de scope et le credential
local `crmProbe`, qui contient deux clés directionnelles distinctes et la CA.
Le module partagé `CrmLocalHmac.js` ajoute le protocole calendrier sans changer
les limites des protocoles précédents.

Registre installé : `/home/node/.n8n/calendar-event-demo-state/provider.sqlite`,
dans le volume existant. Initialisation explicite, fichier créé exclusivement,
aucun reset d'un registre existant ou incomplet. Les deux calendriers et quatre
fixtures initiales proviennent des définitions fournisseur indépendantes 9.6.2 ;
leurs hashes ont été comparés aux lignes importées. Update ne crée jamais sa
cible à partir du snapshot reçu.

SQLite utilise `journal_mode=DELETE`, `synchronous=EXTRA`, clés étrangères,
tables STRICT, `BEGIN IMMEDIATE`, contrôle d'intégrité et attente bornée du verrou.
L'événement, le registre d'idempotence, la preuve et le journal d'effet sont
commis dans une transaction. La comparaison de révision/contenu et l'effet sont
atomiques. Une même clé et une autre commande sont refusées ; une commande
identique retrouve sa preuve sans effet supplémentaire. Lookup est en lecture
seule. Les politiques synthétiques privées ne sont pas accessibles au payload.

L'exécution locale utilise l'API Development HTTPS 7096, le proxy TLS 7443 et
n8n 2.40.7 sur loopback 5678. Le proxy conserve les octets signés. Le callback
valide réellement TLS avec la CA locale, sans désactivation de vérification.
Aucun credential Google/Microsoft, fournisseur distant ou invitation réelle.

## Parcours réseau validés

Chaque ligne possède un plan approuvé, une intention, une tentative et un reçu
durable traité. Les observations avant dispatch, avant/après réconciliation et
après replay sont enregistrées dans les fichiers `*-evidence.json`.

| Parcours | Résultat vérifié |
| --- | --- |
| CREATE | Identité externe générée par SQLite, preuve applied, `succeeded`, nouvel événement seulement après réconciliation |
| UPDATE | Fixture 9.6.2 révision 1 → 2, `succeeded`, même CalendarEvent.Id et ExternalEventId ; références CRM et série/occurrence conservées |
| Version-conflict | Révision fournisseur avancée indépendamment 2 → 3 après approbation ; preuve version-conflict, état `conflict`, cible CRM inchangée à 2, aucun effet de la commande |
| Unknown explicite | Preuve unknown, `operation-unknown`, aucun effet et aucune mutation CalendarEvent |
| Non-application | Preuve not-applied, `failed`, aucun effet ni mutation CalendarEvent |
| Preuve tardive | Effet commis et callback retenu ; HTTP 503, `operation-unknown`, aucun reçu ni mutation CRM ; après redémarrage API+n8n, lecture de preuve SQLite puis callback HMAC et réconciliation → `succeeded` |

Les événements restent identiques entre dispatch/réception et réconciliation
pour CREATE/UPDATE. Le redémarrage conserve exactement commandes, preuves et
journal d'effets SQLite. Le checkpoint SQL unknown est également identique
avant/après redémarrage. La reprise tardive conserve une seule tentative.

Pour les six intentions : deuxième dispatch refusé (409), création d'intention
répétée retournant le même ID, réconciliation répétée sans nouvelle mutation,
commande rejouée dans le registre avec la même preuve et les mêmes comptes.
Le replay réseau signé d'une commande CREATE est aussi accepté par n8n sans
nouvel effet ; son callback répond `alreadyRecorded=true`. Le replay du callback
seul produit le même résultat. Un MessageId existant avec contenu différent
est refusé (409), sans remplacer le reçu.

Les signatures absentes, invalides et périmées sont rejetées (401) en entrée
n8n et sur le callback CRM. Une signature machine sur la route humaine de dispatch
ne donne pas accès (401). Les journaux n8n attestent `signatureValidated=true`,
`callbackTlsValidated=true`, callback 202 et la détection des replays.
Les utilisateurs, MFA, credentials chiffrés et workflow technique historique
n8n ont les mêmes empreintes avant/après.

État fournisseur final : **6 commandes mémorisées, 3 effets, 6 événements**
(4 fixtures initiales, 2 CREATE, 1 UPDATE). `PRAGMA integrity_check=ok`.
Aucune invitation : seules des lignes SQLite sont écrites ; la seule sortie
réseau du node est le callback CRM local, aucun SDK fournisseur ni canal d'envoi.
Les quatre modules réellement installés (contrat, store, node et HMAC partagé)
ont les mêmes SHA-256 que les sources du dépôt : `installed-runtime-hashes.json`.

## Préservation et nettoyage

Le vérificateur hors Git `verify-evidence.cjs` contrôle les assertions des six
parcours, l'intégrité de la sauvegarde, les snapshots après redémarrage,
l'absence de champs d'invitation et les empreintes historiques.

| Table | Après migrations | Après parcours |
| --- | ---: | ---: |
| CalendarEventPlans / CalendarEventDeliveries | 0 / 0 | 6 / 6 |
| AutomationActionRequests | 6 | 12 |
| IntegrationOperations / Attempts / Receipts | 22 / 21 / 22 | 28 / 27 / 28 |
| CalendarEvents | 5 | 7 |

Aucune ligne historique supprimée. Une seule ligne historique est modifiée :
`CalendarEvents/43acf18c-f81e-425f-a810-221b381e4f65`, l'UPDATE autorisé.
Les autres lignes historiques sont identiques, y compris contacts, entreprises,
opportunités, emails, état de synchronisation et paramètres Automation restaurés.
La référence fournisseur du second calendrier reste indépendante et inchangée.

Les paramètres temporaires sont restaurés exactement, y compris UpdatedAt :
Automation désactivé, mode manual, limites 10/minute, 100/jour, 3 échecs.
Les activations d'intégration étaient uniquement dans l'environnement des services
de validation ; elles disparaissent à leur arrêt. Tous les workflows n8n sont
inactifs, dont le nouveau workflow. API, Blazor, proxy, navigateur et n8n arrêtés.
PostgreSQL conservé actif et healthy. Preuves PostgreSQL, registre SQLite,
sauvegardes et journaux conservés ; export temporaire des clés de callback supprimé.

## Tests et vérifications finales

Les résultats terminés avant interruption sont réutilisés :

- `js-01` : **137/137**, 0 échec/ignoré. Inclut contrats fermés, HMAC, limites,
  dates/DST, scopes, concurrence interprocessus, perte de callback et crash d'un
  processus aux checkpoints avant/après commit SQLite ; les protocoles historiques
  utilisant le module partagé sont également couverts.
- `calendar-targeted-01` : 102/115 réussis, 13 erreurs du harnais HTTP.
  Les **86 tests historiques de livraison sont tous réussis**.
- Après correction du harnais, `transport-02` : **29/29**, 0 échec/ignoré,
  23 secondes. Le TRX était déjà terminé ; pas de relance dédiée à la reprise.
  Bilan dédié : **115 scénarios distincts validés**.
- `build-final` : **0 erreur, 0 avertissement**, Release, 5,53 secondes.
- `ef-final` : **HasPendingModelChanges=false**, sans build, environnement Testing
  et connexion de conception factice ; aucune connexion Development. L'outil
  EF 10.0.10 signale sa différence avec le runtime 10.0.12, sans mise à jour.
- `regressions-resume` : **335/335**, 0 échec/ignoré, 4 min 25 s, code 0.
- `global-final` : **1909/1909**, 0 échec/ignoré, **20 min 54 s**, code 0.
  TRX `Completed`, 1909 résultats individuels `Passed`, aucun résultat non exécuté.
  Début : `2026-10-23T10:34:11.6639402+02:00` ; fin :
  `2026-10-23T10:55:07.9388689+02:00`.

Les régressions ciblées couvrent préparation calendrier, calendar.sync, comptes,
transport/sécurité/contrat d'intégration, transports email et policy/settings
Automation. Les 115 dédiés déjà validés ne sont pas relancés dans cette sélection.
La suite globale finale utilise les binaires Release figés, sans filtre,
`--no-build --no-restore`. Les manifestes `sources-global-before.json` et
`dll-global-before.json` couvrent **711 fichiers source/configuration et 3 DLL** ;
leurs hashes sont revérifiés avant lancement. Une garde du script refuse un
second lancement si `global-start.txt` existe. **Une seule suite globale lancée.**
Après exécution, les manifestes `sources-global-after.json` et
`dll-global-after.json` confirment **711/711 sources et 3/3 DLL identiques**,
avec inventaire source identique. Le rapport seul est complété après le gel.

Les empreintes des **61 tables Development** sont identiques avant les
régressions et après la globale. `final-summary.json` conserve les résultats,
horodatages, comparaisons et références Git. `git diff --check` réussit ; les
dix fichiers nouveaux sont également contrôlés en reconnaissant les fins de
ligne CRLF, sans défaut d'espacement. L'inventaire du rapport est comparé à
`git status --short --untracked-files=all` : **16 fichiers, index vide**.
`main`, HEAD et la référence locale `origin/main` restent au SHA initial.
Aucun journal, TRX, dump, registre SQLite, credential, cookie, certificat privé
ou artefact de build n'entre dans cet inventaire.

Incidents du pilote de reprise, sans modification produit : les routes dispatch
et reconcile retournent 204 (le premier pilote attendait 200) ; une signature
machine seule sur une route humaine donne 401 (le pilote attendait 403).
Les réponses originales sont conservées. Le contrôle hors ligne a été corrigé
pour comparer l'état immédiatement après chaque parcours, avant les parcours
suivants, et pour ne pas traiter WorkspaceId comme clé unique des tables composites.
Les assertions corrigées passent sans modification des données observées.

## Limites

La validation prouve un échange réseau réel avec un fournisseur **simulé local**.
Elle ne certifie ni API calendrier réelle, ni garanties d'invitations implicites
Google/Microsoft, ni ETag/If-Match d'un fournisseur distant. Aucun exactly-once
distribué n'est revendiqué. Le registre SQLite doit être conservé ; un fichier
absent/corrompu est refusé, jamais reconstruit depuis une commande.

Le scénario ambigu réel retient volontairement le callback et répond 503 après
commit ; il ne simule pas une panne électrique physique. Les crashes interprocessus
et l'atomicité sont couverts par les tests JavaScript déjà validés. La récupération
tardive est une action explicite de validation, sans retry ni worker ajouté.
Le parcours unknown explicite reste volontairement unknown, preuve conservée.

## Inventaire Git

16 fichiers : 6 modifiés et 10 nouveaux ; index vide.

```text
 M docs/integrations/shared/CrmLocalHmac.js
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Calendar/CalendarEventDeliveryService.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
 M src/ProspectionCrm.Api/appsettings.json
 M tests/ProspectionCrm.Api.Tests/CalendarEventPlanFixture.cs
?? docs/integrations/calendar-event-demo/CalendarEventContract.js
?? docs/integrations/calendar-event-demo/CalendarEventStore.js
?? docs/integrations/calendar-event-demo/CalendarEventStore.test.js
?? docs/integrations/calendar-event-demo/CrmCalendarEvent.node.js
?? docs/integrations/calendar-event-demo/CrmCalendarEvent.test.js
?? docs/integrations/calendar-event-demo/contract-golden.json
?? docs/integrations/calendar-event-demo/workflow.json
?? docs/phase-9-6-step-4-2-validation.md
?? src/ProspectionCrm.Api/Services/Calendar/HttpCalendarEventTransport.cs
?? tests/ProspectionCrm.Api.Tests/CalendarEventTransportTests.cs
```
