# Phase 9.5.2 — Validation réelle n8n de la synchronisation email entrante

## Résultat et référence

Validation Development du **16 octobre 2026**, sur `main` au commit
`a6785da77e5a8550d949d13b47d4c3a41e7f5ac0` —
`Add persistent inbound email synchronization`. HEAD, origin/main et la référence
distante concordaient ; arbre propre, index vide. La
[CI Build #65](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38888305212)
est `completed / success` sur ce SHA. Aucun staging, commit ou push pendant cette phase.

La chaîne **CRM → n8n → CRM → InboundEmailImportService → EmailMessage / EmailSyncState**
a été exercée réellement, sur des comptes et contenus exclusivement fictifs.
Six exécutions n8n ont reçu les commandes HTTPS/HMAC et effectué les callbacks.
Quatre messages inbound ont été importés sur deux comptes. Deux résultats métier
ont été refusés volontairement, sans mutation des messages ni avance du curseur.
Aucun fournisseur externe, OAuth, envoi d'email ou début de 9.5.3.

Les rapports 9.5.1 et 9.4.3, les modèles et services email, les contrats et
persistances d'intégration, le helper HMAC et le workflow de collecte ont été lus.
`AutomationSafetyPolicy` et toutes les migrations restent inchangées.

## Sauvegarde et migration Development

PostgreSQL et n8n étaient arrêtés initialement. PostgreSQL a été démarré et
contrôlé `healthy`. API, Blazor et proxy étaient arrêtés ; intégrations désactivées
et aucun transport email configuré. Les 1 542 tests n'ont pas été relancés avant
la validation réelle.

Une sauvegarde PostgreSQL privée a précédé la migration : **237 365 octets**,
**440 entrées** de catalogue, empreinte SHA-256 vérifiée, catalogue lisible et
archive entièrement décodée avec `pg_restore --file=/dev/null`, sans restauration.
Elle reste hors dépôt dans un dossier dont les droits sont limités à l'utilisateur
Windows courant et SYSTEM. Aucun dump ni journal n'est proposé à Git.

Seule la migration existante **20261016093720_Phase951InboundEmailSync** a été
appliquée à Development, depuis `20261015104738_Phase941RemoteCollectionLifecycle`.
Aucune migration nouvelle ou historique modifiée. Le premier contrôle du script
privé a refusé une référence de départ mal remplacée ; la base n'avait pas été
touchée. Après correction de ce contrôle, l'application a réussi.

Les comptes et empreintes des **49 tables applicatives préexistantes** sont
identiques immédiatement avant/après migration, en ignorant uniquement la nouvelle
colonne nullable dans l'empreinte EmailMessages. Les trois tables email sont alors
vides. L'unique ancien EmailMessage, outbound, conserve `EmailAccountId=null` et
toutes ses valeurs ; les trois collectes distantes 9.4 sont préservées.

## Transport minimal et déclenchement humain

Le nouveau `HttpEmailSyncTransport` réutilise `IntegrationTransport`, son client
HTTP injecté, la validation TLS native, HMAC v1, les limites de réponse, les
résultats de transport et la limite partagée de concurrence. Aucun protocole de
signature n'est réimplémenté dans le CRM. L'endpoint email est une configuration
d'administrateur ; aucune URL n'est acceptée depuis une requête humaine.

Deux activations explicites sont nécessaires : `Integrations:Enabled` et
`Integrations:EmailSync:Enabled`. L'endpoint email doit être HTTPS, sans userinfo,
query ni fragment. La configuration versionnée désactive les deux fonctions.
`UnavailableEmailSyncTransport` reste l'implémentation DI par défaut. L'endpoint
générique des sondes et collectes n'est pas remplacé.

La route **POST /api/email-sync-jobs/{id}/dispatch**, sans corps nécessaire, utilise
le cookie opérateur, le workspace et l'antiforgery existants. Elle appelle le
dispatcher 9.5.1 ; ni worker, ni scheduler, ni retry automatique n'est ajouté.
Le job et sa tentative sont durables avant HTTP. L'adaptateur revérifie la liaison
compte/opération/workspace/connexion avant tout envoi de commande.
Un refus local connu, notamment une limite de concurrence atteinte, retourne un
échec de transport ; une nouvelle intention humaine est nécessaire. Une panne
incertaine conserve la sémantique `dispatch-unknown` de 9.5.1. Le délai effectif
reste borné par les 30 secondes du dispatcher et par le timeout configuré du
transport. Un 204 de dispatch signifie que l'action est terminée : le résultat
métier reste lisible dans le job et exige toujours `process-result`.

L'activation Development a utilisé uniquement les variables des processus
temporaires. **Aucun User Secret n'a été changé**, vérifié par empreinte avant/après.
Les workers de collecte, son scheduler et le worker d'automatisation sont restés
désactivés dans l'API de validation.

## Comptes fictifs

Création via POST humain `/api/email-accounts`, réponse 201, avec la connexion
logique existante `2500eecc-06eb-4568-92e2-00e9038610ac` et le workspace
`20000000-0000-0000-0000-000000000001`.

| Champ | Compte 1 | Compte 2 |
| --- | --- | --- |
| Id | `6c21e658-c2fb-42da-baa4-85bf51e739ba` | `513fb0a0-9b8f-4ba4-83ff-18639d1d95f5` |
| ProviderCode | `local-demo` | `local-demo` |
| ExternalAccountId | `phase-9-5-2-account-001` | `phase-9-5-2-account-002` |
| EmailAddress | `demo-inbox@example.test` | `demo-inbox-2@example.test` |
| DisplayName | Phase 9.5.2 deterministic inbox 1 | Phase 9.5.2 deterministic inbox 2 |
| État initial | active, non archivé | active, non archivé |
| État de sync initial | Cursor=null, Revision=0 | Cursor=null, Revision=0 |

Aucun mot de passe, token, credential fournisseur ou client OAuth n'est créé.
Les deux comptes et leurs preuves sont conservés dans Development.

## Workflow et HTTPS/HMAC

Workflow distinct : **Phase 9.5.2 - deterministic inbound email sync**.
Il comporte `Receive raw command`, `Verify and callback`, `Return transport status`.
Le nœud `CrmEmailSync` délègue au **CrmLocalHmac.js partagé inchangé** ; les nœuds et
workflows 9.3.3/9.4.3 ne sont pas modifiés. Le credential natif local est réutilisé.

```text
CRM Windows
  → HTTPS localhost:7443/webhook/crm-email-sync-demo
  → proxy privé Windows, loopback seulement
  → HTTP 127.0.0.1:5678, publication Docker loopback
  → n8n : validation HMAC, contrat et comptes configurés
  → HTTPS localhost:7096/api/integrations/results
  → ACK 202 seulement après callback CRM accepté 202
```

Identifiants publics réutilisés : `crm-probe-out-20261015` pour CRM → n8n et
`n8n-probe-out-20261015` pour n8n → CRM. Les clés restent hors Git et ne sont
jamais affichées. Le certificat localhost déjà approuvé est réutilisé, sans
création/export de clé privée, modification du magasin ou exception TLS.
Le callback conserve `rejectUnauthorized=true`, TLS 1.2 minimum et le nom TLS
localhost, avec résolution Docker via `host.docker.internal`.

Les écoutes des services de validation sont uniquement `127.0.0.1` et `::1` :
5432, 5678, 7075, 7096, 7443. Aucune écoute applicative sur `0.0.0.0`, aucun
changement du pare-feu. Readiness HTTPS n8n : 200. Les six callbacks attestent
`signatureValidated=true`, `callbackTlsValidated=true`, `callbackStatus=202`.
Pour chacun, le hash des octets bruts observés par n8n correspond exactement à
la commande reconstruite depuis les identifiants et snapshots durables CRM.

L'API et Blazor ont d'abord été lancés sans intégrations pour permettre la
connexion opérateur et créer les comptes. Pour les synchronisations, n8n a été
démarré puis le proxy, puis l'API redémarrée avec les activations temporaires.
Le navigateur intégré n'a pas pu se connecter ; le profil Edge privé existant
a été utilisé. L'utilisateur a renouvelé lui-même sa session expirée.

## Contenus déterministes

| Message | Expéditeur | Thread | Date UTC fixe |
| --- | --- | --- | --- |
| `mail-demo-001` | `alice@example.test` | `thread-demo-001` | `2026-10-16T08:01:00Z` |
| `mail-demo-002` | `bob@example.test` | `thread-demo-002` | `2026-10-16T08:02:00Z` |
| `mail-demo-003` | `carol@example.test` | `thread-demo-003` | `2026-10-16T08:03:00Z` |

Pour le compte 1, To vaut `["demo-inbox@example.test"]`. Cc vaut
`["team@example.test"]` pour le message 2 et `[]` pour les autres.
Les sujets sont `Phase 9.5.2 first deterministic message`,
`Phase 9.5.2 second deterministic message`, `Phase 9.5.2 third deterministic message`.
Les corps suivent `Inbound deterministic email message N`, avec N égal à 1, 2 ou 3.
Aucun HTML ni pièce jointe. Ces valeurs, dates et destinataires normalisés ont été
comparés intégralement avec les lignes persistées, ainsi que workspace, compte,
provider, direction inbound et références Contact/Company/Opportunity toutes nulles.

Le compte 2 réutilise `mail-demo-001` et sa date, avec To
`["demo-inbox-2@example.test"]`, le sujet `Phase 9.5.2 second account message`
et le corps `Inbound deterministic email for account 2`.

Le workflow est une fixture locale stricte : deux identifiants de comptes à
configurer, cursors connus seulement, maxItems entier 1–100 et suffisamment grand
pour le lot. Les essais ont utilisé **10**. Le premier lot et le deuxième exigent
au moins 2 ; les succès vides restent valides avec une limite de 1.
Le helper partagé conserve sa limite locale de commande de 4 Kio ; les commandes
de cette fixture mesurent 292 ou 300 octets, sous cette limite et celle de 16 Kio
du contrat CRM. Il ne s'agit pas d'un adaptateur fournisseur généraliste.

## Parcours observés

Les POST d'enqueue rendent 202/queued. Les six dispatches humains rendent 204.
Dans ce workflow, **le callback précède l'ACK** : l'opération atteint awaiting-result,
la réception fait passer le job running à result-received, puis la tentative
termine acknowledged sans rouvrir le job. Le job ne séjourne pas nécessairement
dans awaiting-result.

Pour le premier job : début à **11:13:24.949988Z**, réception durable à
**11:13:25.202150Z**, ACK à **11:13:25.301701Z**, traitement à **11:13:59.094930Z**.
Avant traitement : aucun nouveau message, Cursor=null/Revision=0, receipt pending.
Après POST process-result 204 : deux messages, job/opération succeeded, receipt
traité, CreatedCount=2/ExistingCount=0, cursor-001/Revision=1.

| Parcours | n8n | Résultat process-result | Créés / existants | Curseur courant après / révision |
| --- | ---: | --- | --- | --- |
| Premier lot compte 1 | 7 | 204, succeeded | 2 / 0 | cursor-001 / 1 |
| Deuxième lot compte 1 | 8 | 204, succeeded | 1 / 1 | cursor-002 / 2 |
| Succès vide après redémarrage API | 9 | 204, succeeded | 0 / 0 | cursor-003 / 3 |
| Divergence compte 1 | 10 | 409, EmailMessageContentConflict | 0 / 0 | cursor-003 / 3 |
| Premier lot compte 2 | 11 | 204, succeeded | 1 / 0 | cursor-001 / 1 |
| Compte 2 désactivé après réception | 12 | 409, EmailAccountUnavailable | 0 / 0 | cursor-001 / 1 |

Les six exécutions n8n sont **success**, y compris les deux résultats ensuite
refusés par le traitement métier CRM. Chaque job possède une seule opération,
une seule tentative acknowledged et une seule réception finalement traitée.

**Replay :** le corps exact du callback n8n 7 est renvoyé avec le même messageId
et un nouveau timestamp/signature HMAC valides. Réponse **202**, `alreadyRecorded=true`.
Le process-result répété retourne **204**. La comparaison complète des lignes
dédiées avant/après est identique : aucun job, message, curseur ou révision changé,
aucune nouvelle exécution n8n.

**Deuxième lot :** previousCursor=cursor-001 ; mail-demo-001 est strictement
identique et mail-demo-003 est nouveau. Avant traitement : toujours deux messages.
Après : trois messages du compte 1, sans doublon, cursor-002/Revision=2.

**Redémarrage :** le troisième résultat est durable avec zéro message et nextCursor=cursor-003.
L'API seule est arrêtée puis redémarrée. Toutes les lignes dédiées sont identiques,
le receipt reste pending, cursor-002/Revision=2, et les exécutions n8n sont inchangées.
Le process-result ultérieur avance une seule fois le curseur à cursor-003, sans
nouveau message ni nouvel appel n8n.

**Conflit :** mail-demo-001 revient avec le corps `Deliberately divergent content`
et nextCursor=cursor-004. Le CRM conserve le message d'origine ; job/opération failed,
`ConflictingExternalMessageId=mail-demo-001`, receipt traité, aucune ingestion.
Le nextCursor reçu demeure un diagnostic du job, jamais le curseur courant.

**Multi-compte :** le message mail-demo-001 propre au compte 2 est créé sans
collision. Deux EmailAccountId distincts portent donc ce même ExternalMessageId,
avec des contenus et curseurs indépendants.

**Compte indisponible :** après la sixième réception, le compte 2 est passé à
disabled via PUT humain avec sa révision attendue. Le traitement refuse avant
ingestion et finalise le job/opération failed et le receipt traité. Ce lot était
un succès vide proposant cursor-002 : le curseur courant reste cursor-001/Revision=1.
Le statut active initial a ensuite été restauré par PUT avec la nouvelle révision.
Le compte 2 termine à Revision=2 ; son état de synchronisation reste à Revision=1.
Aucun passage à revoked.

## Identifiants de preuve

| Parcours | Job | Opération | Result messageId |
| --- | --- | --- | --- |
| Premier | `7c6ff574-deb9-4711-93b9-2712e42d3195` | `4ee5da7c-df6e-4970-bb24-79d09f47db81` | `24495cc3-e47d-4ee3-a14b-6a027ae77c4b` |
| Deuxième | `a564082c-d7a9-4290-bea7-8e287f7fdd38` | `c9708646-8ae5-4480-a431-1906046f33b0` | `97cc5e3e-2293-4458-8914-57b097509320` |
| Reprise | `65693eab-4b47-4012-9782-d2f3df24f991` | `36c54ce0-6cdf-4a7a-9c7f-463337de2aed` | `7b2f0dde-6ac1-4df2-a38a-6f7296e26fb1` |
| Conflit | `91580a20-e6ea-4fe8-8cff-82d2ce21ee1b` | `bde26141-7167-4fef-8aa1-9e5567dcb6ae` | `97821a5e-5974-4df1-8c27-4a13c4560165` |
| Multi-compte | `8fe37049-83ba-42ad-bc47-6d071d188b6d` | `369752d9-4674-433a-a891-b1fa27997396` | `696faba4-3874-436f-a055-9edf8bfc8520` |
| Indisponible | `fb41e7af-ba52-4892-8e8d-4c7380d3e2c3` | `5890fde3-b897-40f4-987a-e19d264a8f76` | `5bcc92f6-5cc4-4062-9010-ab5a660883ae` |

## Préservation et absence d'effets externes

Les **98 lignes préexistantes des 50 tables historiques** sont retrouvées par
empreinte à l'identique après les scénarios. Les **46 tables autres que les sept
tables attendues** ont des comptes et empreintes complets inchangés.

| Table / catégorie | Avant scénarios | Après |
| --- | ---: | ---: |
| EmailAccounts | 0 | 2 |
| EmailSyncStates | 0 | 2 |
| EmailSyncJobs | 0 | 6 |
| IntegrationOperations | 4 | 10 |
| IntegrationAttempts | 4 | 10 |
| IntegrationReceipts | 4 | 10 |
| EmailMessages inbound | 0 | 4 |
| EmailMessages outbound | 1 | 1 |
| Contacts | 1 | 1 |
| Companies | 1 | 1 |
| Opportunities | 3 | 3 |
| CalendarEvents | 1 | 1 |
| AutomationJobs | 0 | 0 |
| AutomationExecutions | 1 | 1 |
| AutomationActionRequests | 0 | 0 |
| RemoteCollectionIntents | 3 | 3 |

Le périmètre de comparaison inclut toute la base ; les deux comptes ont été créés
entre le relevé après migration et le premier dispatch. Aucun effet fournisseur,
calendrier, automatisation ou création métier implicite n'est constaté.

## Sécurité, export et tests

Contrôles réels : callback sans HMAC **401** ; cookie opérateur seul sur callback
**401** ; HMAC seul sur route humaine **401** ; session opérateur + antiforgery sur
création/enqueue/dispatch/traitement/mise à jour **réussis**. Les requêtes HTTPS
conservent la validation TLS normale dans les deux sens.

L'export réel après désactivation correspond exactement au workflow Git, après
retrait des métadonnées d'installation, références de credentials et valeurs des
deux accountRefs locaux. L'export versionné a `active=false`, aucun credential ID,
workflow ID d'installation, pinData ou donnée d'exécution. Pour réinstaller,
conserver les dossiers frères `email-sync-demo` et `shared`, installer le schéma
`technical-probe-local/CrmProbe.credentials.js`, sélectionner le credential natif
local et renseigner explicitement les deux EmailAccount IDs. L'export seul n'active rien.

| Contrôle | Résultat |
| --- | --- |
| JavaScript hors réseau, Node 24.19.0 | **40/40**, dont 21 nouveaux et 19 contrôles partagés |
| Build Release final | **0 erreur, 0 avertissement** |
| Email / intégration ciblés | **222/222**, 0 échec, 0 ignoré |
| Régressions collecte distante / sécurité opérateur / policy | **121/121**, 0 échec, 0 ignoré |
| Suite globale finale | **1 551/1 551**, 0 échec, 0 ignoré |

Les nouveaux tests .NET vérifient le dispatch humain et sa séparation HMAC/CSRF,
le défaut désactivé sans tentative, HTTPS/HMAC partagé sur l'endpoint email, le
callback avant ACK, le traitement explicite, le refus de redispatch, les endpoints
invalides, les deux activations et le refus d'un compte différent avant HTTP.
Les tests utilisent des bases jetables, jamais Development. La suite globale est
lancée seulement après la validation réelle et l'arrêt des services temporaires.
Elle réussit en 28 min 14 s. Les comptes et empreintes de toutes les tables
Development sont identiques avant/après les tests automatisés ; les conteneurs
de tests ont ensuite été supprimés automatiquement.

Le premier build a signalé les deux avertissements CS8618 préexistants de
Settings.razor. Le premier build des nouveaux tests a signalé une capture redondante
du paramètre du fixture (CS9107), corrigée avant les tests. Le build final est sans
avertissement. Les logs et TRX sont conservés hors Git, sans effacer ces premiers passages.

## État final et périmètre Git

- Workflow 9.5.2 désactivé ; workflows 9.3.3 et 9.4.3 toujours désactivés.
- n8n arrêté ; compte, MFA, credential et workflow technical.probe inchangés par empreinte.
- API, Blazor, proxy et navigateur dédié arrêtés ; ports 5678/7075/7096/7443 libérés.
- Transport email désactivé par défaut ; les activations temporaires ont disparu
  avec les processus. User Secrets intégralement inchangés.
- PostgreSQL Development seul reste healthy après validation ; comptes, messages,
  jobs et résultats conservés. Les bases de tests sont jetables.
- Profil Edge privé conservé hors Git : **il n'a pas été supprimé**.
- Aucun secret réel, cookie/session, credential réel, certificat privé, dump,
  log, TRX, bin/obj ou profil navigateur dans le périmètre Git proposé.
- HEAD et origin/main restent au SHA de départ, index vide ; aucun staging,
  commit ou push, aucune phase 9.5.3.

Périmètre exact : **6 fichiers modifiés, 6 nouveaux**, soit **12 fichiers** :

```text
 M src/ProspectionCrm.Api/Controllers/EmailAccountsController.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/Email/EmailSyncDispatcher.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationTransport.cs
 M src/ProspectionCrm.Api/appsettings.json
?? docs/integrations/email-sync-demo/CrmEmailSync.node.js
?? docs/integrations/email-sync-demo/CrmEmailSync.test.js
?? docs/integrations/email-sync-demo/workflow.json
?? docs/phase-9-5-step-2-validation.md
?? src/ProspectionCrm.Api/Services/Email/HttpEmailSyncTransport.cs
?? tests/ProspectionCrm.Api.Tests/EmailSyncTransportTests.cs
```
