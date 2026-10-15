# Phase 9.4.3 — Validation réelle de la collecte distante n8n

## Résultat et référence

Validation réalisée le **15 octobre 2026** sur Development : la chaîne réelle
CRM → n8n → CRM → `IngestionService` a créé exactement **deux opportunités
fictives**, puis les a redécouvertes sans doublon. Le replay est idempotent,
la réception survit au redémarrage de l'API et un changement de contexte
empêche l'ingestion. Aucun défaut applicatif C# n'a été révélé.

Départ sur `main`, HEAD et `origin/main` synchronisés à
`b3b095437067df0610fd0c90ce0726ffbb12fa15`
(`Process remote collection results atomically`), working tree propre et index
vide. La [CI Build #63](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38750640115)
de 9.4.2 a été vérifiée `completed / success` ; cette phase avait validé
1 461 tests sur 1 461. Aucun nouveau commit ni nouvelle CI pendant 9.4.3.

PostgreSQL était sain ; n8n, API et Blazor étaient arrêtés ;
`Integrations:Enabled=false`. Les rapports 9.3.3, 9.4.1, 9.4.2, le workflow
technical.probe et les contrats/services de collecte et d'ingestion ont été lus.

## Prérequis Development et préservation

Development avait encore 15 migrations appliquées. La migration **existante et
additive** `20261015104738_Phase941RemoteCollectionLifecycle` a été appliquée
pour permettre ce parcours réel : 16 migrations après intervention.
**Aucune migration nouvelle n'a été créée et aucun fichier de migration n'a
été modifié.** Cette application à Development distingue cette intervention
des validations locales précédentes, qui n'avaient pas migré Development.

Une sauvegarde PostgreSQL privée a précédé l'application : 225 234 octets,
429 entrées de catalogue, empreinte vérifiée, catalogue lisible et archive
entièrement décodable avec `pg_restore`, sans restauration de base. Le dump
reste hors Git. Les comptes et empreintes des **48 tables applicatives
préexistantes** sont identiques immédiatement avant/après migration ; seule
l'histoire EF s'enrichit et `RemoteCollectionIntents` est créée vide.

Avant la collecte, les empreintes de toutes les lignes des 50 tables publiques
ont été conservées localement. Le contrôle final retrouve **les 63 lignes
préexistantes à l'identique**, y compris l'opportunité, les deux provenances
existantes et la sonde 9.3.3. Les données de test sont conservées comme preuves.

## Source et recherche dédiées

| Élément | Valeur |
| --- | --- |
| SourceConfiguration | `ee9c6c15-0987-4f23-b1bf-c05ff9dd143d` |
| Nom de source | `Phase 9.4.3 - n8n deterministic demo` |
| SourceTypeCode | `remote-collection` |
| SavedSearch | `f28ddbb2-27b6-4d09-8cb9-b808c63b362d` |
| Nom de recherche | `Phase 9.4.3 - deterministic remote collection` |
| Pipeline existant | `30000000-0000-0000-0000-000000000001` — Emploi .NET |
| Stage existant | `40000000-0000-0000-0000-000000000001` — À candidater |
| Workspace | `20000000-0000-0000-0000-000000000001` |

La source et la recherche ont été créées via l'API avec la session humaine.
Elles sont activées, sans planification. Le pipeline/stage existant n'a pas
été modifié. `BaseUrl`, `ConfigurationJson` et `SearchUrl` sont absents.
Les critères sont strictement, au sens JSON :

```json
{"query":"phase-9-4-3-demo","maxItems":2}
```

Aucune Company inventée, aucun fournisseur, secret ou credential dans les
critères/configurations. Les deux candidats sont exactement :

| ExternalId | Title | Location | Description |
| --- | --- | --- | --- |
| `remote-demo-001` | Remote Demo .NET Backend | Bordeaux | Phase 9.4.3 deterministic remote collection candidate 1 |
| `remote-demo-002` | Remote Demo Unity Tools | Bordeaux | Phase 9.4.3 deterministic remote collection candidate 2 |

`CompanyName` et `SourceUrl` sont absents des callbacks. Les opportunités
persistées ont `CompanyId=null`, `ContactId=null`, la description dans `Notes`
et le stage choisi. Aucune URL fournisseur n'est appelée.

## HTTPS, HMAC et workflow

La connexion et le credential natif de 9.3.3 ont été réutilisés :

| Élément public | Identifiant |
| --- | --- |
| ConnectionId | `2500eecc-06eb-4568-92e2-00e9038610ac` |
| Clé CRM → n8n | `crm-probe-out-20261015` |
| Clé n8n → CRM | `n8n-probe-out-20261015` |

Les deux valeurs HMAC sont distinctes, de 32 octets après décodage Base64,
liées au workspace et à la connexion attendus, contrôlées sans affichage.
Elles restent dans les User Secrets et le credential natif n8n.

```text
CRM Windows
  → HTTPS localhost:7443/webhook/crm-remote-collection-demo
  → proxy Windows lié au loopback
  → HTTP 127.0.0.1:5678 (publication Docker loopback)
  → workflow n8n
  → HTTPS localhost:7096/api/integrations/results
     connexion Docker via host.docker.internal, identité TLS localhost conservée
```

Le certificat localhost existant est valide/approuvé ; aucun certificat recréé,
aucune clé privée exportée, aucun changement pare-feu. Le proxy Windows et
l'API écoutent seulement sur `127.0.0.1`/`::1`, le port n8n publié sur
`127.0.0.1`. Aucune nouvelle écoute Windows sur `0.0.0.0`.
Le callback utilise le certificat public existant, `rejectUnauthorized=true`
et TLS 1.2 minimum ; aucune désactivation de vérification TLS. Le contrôle
n8n → API anonyme a répondu 401 avec TLS autorisé, puis les trois callbacks
signés ont répondu 202 avec TLS autorisé.

Le nouveau workflow **Phase 9.4.3 - deterministic remote collection**, sur
n8n 2.40.7, comporte uniquement les trois nœuds `Receive raw command`,
`Verify and callback`, `Return transport status`. Son chemin est dédié.
Les octets bruts de la commande sont vérifiés avec HMAC-SHA256 v1 : audience,
identifiant de clé, timestamp, méthode, chemin et hash du corps. Le contrat
valide version/type/UUID/query/maxItems, rejette les propriétés dupliquées et
refuse `maxItems < 2`. Le résultat conserve les identifiants de corrélation,
commande et requête ; son `messageId` est nouveau.

Le callback est signé sur les octets exacts envoyés. Le nœud attend le
**202 du CRM avant de rendre l'ACK 202**. Les trois exécutions n8n **4, 5, 6**
sont `success` ; chacune a vérifié 303 octets bruts. Le SHA-256 de ces octets
correspond à la commande durable CRM. Aucune exécution supplémentaire pendant
le replay ou le redémarrage de l'API.

Le workflow technical.probe reste désactivé et sa définition persistée est
inchangée, vérifiée par empreinte ; ses trois anciennes exécutions sont
inchangées. Le code du nœud `CrmProbe` délègue désormais au module HMAC commun,
afin de partager les mêmes contrôles avec `CrmCollection`. Le schéma natif
`crmProbe` est réutilisé sans modification ni copie de valeurs.

## Identifiants des trois collectes

| Élément | Première collecte | Deuxième collecte + reprise | Contexte modifié |
| --- | --- | --- | --- |
| Job | `df67cfa3-c09f-4e63-8639-921c98136c52` | `7bcc86b1-5e4c-4930-bcdb-534b5df8a1c7` | `84fa4ec1-af66-47b0-8f58-6de3e33f6087` |
| Operation | `893ee6ae-dde9-4262-8cf2-d656c266ffee` | `231de972-bd64-4ed2-8311-e9007c29cfae` | `7d3db758-edbd-47f3-8eb9-cd3fedb5d4ed` |
| RequestId | `2161f40f-28b7-411c-ac7a-1c301863d817` | `35f0bcd3-9317-4ccf-9f19-2163eeaf64d3` | `396ead72-6aa8-4526-8998-97b767e3a5af` |
| CommandMessageId | `49b4627b-b4f6-4d82-b313-9e05573cbd2e` | `3105b7f0-ac39-4a2c-a2ac-dc17a8d3493c` | `b6078996-e204-4000-b665-f536b7734d41` |
| Result MessageId | `7222bb5f-e32f-48d4-8d6d-a7e07dbec45c` | `3bc9f264-340e-41f2-a8c7-0dde85f4eb75` | `a74121b4-4434-4482-96b8-cc40213eab9f` |
| SourceExecution | `3f4e04e1-8b4d-48fb-b19f-708079e546ba` | `88e76fd1-7c47-4fcf-b315-46c261a5119a` | aucune |
| Exécution n8n | 4 | 5 | 6 |

Chaque job possède une seule tentative de collecte et une seule tentative de
transport. Les trois tentatives de transport terminent `acknowledged`.
Leurs états sont distincts de ceux des tentatives de collecte, qui terminent
respectivement `succeeded`, `succeeded`, `failed`.

## Première collecte et callback avant ACK

L'enqueue humain retourne 202 et `queued`. Une observation PostgreSQL toutes
les 50 ms a relevé la séquence suivante (heures Europe/Paris, UTC+02:00) :

| Heure | Job | Opération | Transport | Lease |
| --- | --- | --- | --- | --- |
| 17:53:30.764 | queued | absente | absent | absent |
| 17:53:31.413 | running | absente | absent | présent |
| 17:53:33.563 | running | pending | absent | présent |
| 17:53:33.613 | running | awaiting-result | started | présent |
| 17:53:33.863 | result-received | result-received | started | absent |
| 17:53:33.913 | result-received | result-received | acknowledged | absent |

**Le job n'a pas séjourné dans `awaiting-result`** : le callback doit précéder
l'ACK dans ce workflow. C'est l'opération qui atteint cet état ; la réception
durable parque directement le job `running` en `result-received`. Cette branche
réelle prévue par `RemoteCollectionState` est observée, sans simulation d'un
état supplémentaire ni modification applicative.

Réception à **17:53:33.817630**, `ProcessedAt=null`, aucune SourceExecution,
aucune opportunité de test et aucun lease. L'opportunité préexistante demeure.
L'ACK termine le transport à **17:53:33.904329** sans rouvrir le job.

Le POST humain explicite `process-result`, **sans corps et avec antiforgery**,
retourne **204**. À **17:53:54.553846**, job/tentative/opération sont
`succeeded`, la réception est traitée et le SourceExecutionId est renseigné.
La SourceExecution `manual / succeeded` compte **Found=2, Created=2,
Updated=0, Ignored=0**.

| ExternalId | OpportunityId | OpportunitySourceId |
| --- | --- | --- |
| `remote-demo-001` | `16b98694-0824-4eb3-8561-8b135db06fc3` | `46ee954c-426f-473d-9360-ca47cb344374` |
| `remote-demo-002` | `58b0ed18-766d-4de2-ab9e-838eacda2c9a` | `d52bfea7-1d9b-4f8e-aed8-ee2a1016662a` |

Les provenances portent les bons SourceConfigurationId/SavedSearchId et le
SourceExecutionId d'origine. `FirstSeenAt=LastSeenAt=17:53:31.403568` pour les
deux, selon la sémantique de temps de début de collecte existante.
Les deux SourceExecutionItems ont `OutcomeCode=created`,
`DecisionCode=CreatedNewOpportunity`, les bons OpportunityIds et snapshots.

## Replay et seconde collecte avec reprise

Le **corps exact du callback de l'exécution n8n 4** a été repris, avec le même
messageId et une signature HMAC recalculée à timestamp valide :
**202**, `status=received`, `alreadyRecorded=true`. Un nouveau POST humain
`process-result` sur le premier job retourne **409 / RemoteResultNotReady**.
La comparaison de toutes les lignes dédiées avant/après ces deux appels est
identique : aucune exécution, opportunité, provenance, histoire ou LastSeenAt
supplémentaire/modifié.

La deuxième collecte est un **nouveau job**, avec nouveaux operationId,
requestId, commandMessageId, result messageId et correlationId. Elle produit
les mêmes ExternalIds. Réception à **17:54:24.915334**, encore non traitée,
job `result-received`, aucun lease et aucune nouvelle ingestion.

L'API seule a ensuite été arrêtée (port libéré) et redémarrée. La comparaison
avant/après redémarrage retrouve **exactement les mêmes lignes durables**, la
même réception pending et les mêmes deux exécutions n8n (4 et 5). Aucun appel
n8n additionnel, aucune ingestion automatique ni nouvelle tentative.

Le POST humain après redémarrage retourne **204**. La réception est traitée
à **17:54:53.752403** et le second job/tentative/opération termine `succeeded`.
Nouvelle SourceExecution `manual / succeeded` : **Found=2, Created=0,
Updated=2, Ignored=0**. Toujours **deux opportunités de test**, donc trois
opportunités au total avec celle qui préexistait.

Les identités de provenance, `FirstSeenAt` et le SourceExecutionId **d'origine**
restent identiques. `LastSeenAt` avance à **17:54:22.743210** pour les deux.
Les deux nouveaux items d'histoire ont `OutcomeCode=updated` et
`DecisionCode=MatchedExternalId`, vers les mêmes OpportunityIds. Les quatre
liens SourceExecutionItemSources portent le rôle `external-id` et les bonnes
provenances/snapshots.

Le replay conserve la même identité technique et **ne réingère rien** ; une
nouvelle collecte possède une nouvelle identité technique et produit une
nouvelle SourceExecution, tout en retrouvant les mêmes identités métier.

## Refus après changement de configuration

Le troisième job est réservé à ce scénario ; aucune quatrième collecte n'a
été nécessaire pour la reprise. Après sa réception durable à
**17:55:14.109099**, seule la SavedSearch dédiée a été modifiée via l'API :
`query=phase-9-4-3-demo-changed`, `maxItems=2`. Aucun pipeline, stage ou donnée
existante n'a été modifié.

Le POST humain `process-result` retourne **409 / RemoteContextChanged**.
Job/tentative/opération sont `failed`, la réception est traitée à
**17:55:39.392970**, `SourceExecutionId=null` et aucun lease ne subsiste.
Les SourceExecutions, opportunités, provenances, LastSeenAt et historiques
sont strictement identiques au résultat de la deuxième collecte.

Les critères d'origine ont ensuite été restaurés volontairement par PUT 204
et relus sémantiquement. La recherche reste disponible sans planification.

## Comptes avant/après et absence d'effets externes

Comptes de la base complète, avant collecte (après la migration existante)
et après les trois scénarios :

| Table | Avant | Après |
| --- | ---: | ---: |
| SourceConfigurations | 1 | 2 |
| SavedSearches | 1 | 2 |
| SourceCollectionJobs | 0 | 3 |
| SourceCollectionJobAttempts | 0 | 3 |
| RemoteCollectionIntents | 0 | 3 |
| IntegrationOperations | 1 | 4 |
| IntegrationAttempts | 1 | 4 |
| IntegrationReceipts | 1 | 4 |
| Opportunities | 1 | 3 |
| OpportunitySources | 2 | 4 |
| SourceExecutions | 1 | 3 |
| SourceExecutionItems | 0 | 4 |
| SourceExecutionItemSources | 0 | 4 |
| Companies | 1 | 1 |
| EmailMessages | 1 | 1 |
| CalendarEvents | 1 | 1 |
| AutomationExecutions | 1 | 1 |
| AutomationJobs | 0 | 0 |
| AutomationActionRequests | 0 | 0 |

Seules les **13 premières tables** diffèrent, par les ajouts attendus. Les
**37 autres tables** ont exactement les mêmes comptes et empreintes de lignes.
Les 63 lignes préexistantes des 50 tables sont toutes préservées.
Le scheduler et le worker d'automatisation étaient désactivés dans les
processus ; seul le worker de collecte était actif pendant les essais, avec
une tentative maximum. Aucun email, calendrier, automatisation externe,
Company ou fournisseur externe appelé/créé. Les exécutions n8n n'ont traversé
que les trois nœuds du workflow dédié.

## Exports et contrôles de code

L'export du workflow réellement installé, obtenu après désactivation, a été
comparé à la définition documentaire : correspondance exacte après retrait
des métadonnées d'installation et références de credentials. L'export Git est
`active=false`, sans credentialId, credentials, pinData ou identifiant de
workflow propre à l'installation.

Pour réimporter, conserver l'arborescence relative des nœuds
`technical-probe-local`, `remote-collection-demo` et `shared`. Le module commun
doit être installé à côté des dossiers des nœuds. Utiliser le schéma déjà
versionné `technical-probe-local/CrmProbe.credentials.js`, puis sélectionner
manuellement un credential natif configuré hors Git. L'export seul n'active
aucune connexion et n'embarque aucune valeur secrète. Le certificat public
local est fourni dans ce credential ; aucune valeur n'est incluse ici.

**19/19 vérifications JavaScript hors réseau** ont réussi sur Node 26.7.0 de
l'image n8n 2.40.7 : clés synthétiques, HTTPS simulé, signatures sur corps brut,
rejet de signature/corps/timestamp altérés, propriétés dupliquées, contrat
collection invalide (dont maxItems=1), callback rejeté et TLS non autorisé.
Les deux profils technical.probe et collection partagent ces contrôles.
Le premier lancement avec le Node du PATH ne supportait pas `--test` ; le
premier passage dans l'image a révélé une accolade manquante dans le banc de
test, corrigée avant le passage final vert. Aucun appel réel supplémentaire.

Des ajustements des scripts privés d'observation ont été nécessaires :
comparaison de dates de processus typées, sortie JSON PostgreSQL compacte et
comparaison sémantique des critères jsonb. La modification des critères avait
bien réussi malgré la première assertion textuelle trop stricte ; elle n'a
pas été réémise avant le traitement. Ces ajustements ne modifient pas le CRM.

Aucun fichier C# applicatif modifié, aucun nouveau build/test C# ni relance des
1 461 tests. Les courses process/process et process/cancel restent couvertes
par 9.4.2 ; ici seule la séquence réelle callback avant ACK puis traitement
humain a été exercée. Aucune Phase 9.5 commencée.

## État final et Git

- `Integrations:Enabled=false`, endpoint antérieur restauré ; clés, connexion
  et autres User Secrets inchangés.
- Workflow 9.4.3 désactivé et conservé ; workflow 9.3.3 toujours désactivé.
- n8n arrêté ; compte, MFA et credential existant inchangés par empreinte.
  Aucun reset, restauration ou remplacement de credential.
- API, Blazor, proxy et fenêtre Edge dédiée arrêtés ; aucun listener sur
  5678, 7075, 7096 ou 7443. PostgreSQL seul reste actif et `healthy`.
- Deux opportunités fictives, source/recherche et preuves de collecte
  conservées dans Development ; critères restaurés.
- Profil Edge temporaire conservé **hors Git**. Sa suppression automatique
  avait été refusée pendant 9.3.3 ; elle n'a pas été retentée ici.
  **Le profil n'a pas été supprimé.**
- Aucun secret HMAC, mot de passe/hash opérateur, cookie/session, credential
  réel, certificat privé, dump, log local, TRX, bin/obj ou profil navigateur
  dans les fichiers proposés. Les preuves privées restent hors dépôt.

Périmètre Git exact : **un fichier modifié et cinq nouveaux**, tous sous docs :

```text
M  docs/integrations/technical-probe-local/CrmProbe.node.js
?? docs/integrations/shared/CrmLocalHmac.js
?? docs/integrations/shared/CrmLocalHmac.test.js
?? docs/integrations/remote-collection-demo/CrmCollection.node.js
?? docs/integrations/remote-collection-demo/workflow.json
?? docs/phase-9-4-step-3-validation.md
```

`git diff --check` réussi, index vide, HEAD inchangé sur `main`.
Aucun staging, aucun commit, aucun push.
