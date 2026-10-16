# Phase 9.5.1 — Comptes email et synchronisation entrante persistante

## Référence et périmètre

Implémentation locale du 16 octobre 2026 sur `main`, départ vérifié à
`ab2f778a6a48b6a544be6209ee2f606733bd4602` (`Document verified remote collection through n8n`).
HEAD, référence locale `origin/main` et `git ls-remote origin refs/heads/main`
concordaient ; working tree propre et index vide. Aucune consigne AGENTS.md
n'était présente dans le dépôt ou ses répertoires parents.

Les modèles EmailMessage, Contact, Company, Opportunity, la policy Automation,
les contrats/persistances 9.3, les rapports 9.3 et 9.4.1–9.4.3, les services
de collecte distante et d'ingestion, le DbContext/conventions EF et les API
métier existantes ont été examinés. `EmailMessagesController`,
`EmailMessageService`, son interface et son DTO existaient déjà : ils sont étendus.

La tranche fournit le modèle, les services transactionnels, le contrat fermé,
les routes humaines minimales, la réception HMAC et une migration additive.
Elle ne fournit aucun transport email réel : `IEmailSyncTransport` est enregistré
avec `UnavailableEmailSyncTransport`. `EmailSyncDispatcher` est interne à
l'orchestration DI, sans route de dispatch ni worker. Les tests injectent un
transport en mémoire. Aucune configuration n'active ce transport par défaut.

Docker Desktop, initialement arrêté, a été démarré pour les PostgreSQL 18
jetables Testcontainers. Aucune migration sur Development, aucun lancement
de n8n, aucun fournisseur ou OAuth réel, aucun email envoyé/préparé pour envoi.
`AutomationSafetyPolicy` est inchangée. Aucun frontend ajouté, aucune 9.5.2 engagée.

## Architecture et responsabilités

```text
CRM (opérateur, workspace authentifié)
  -> EmailSyncJob + IntegrationOperation pending (même transaction)
  -> IntegrationAttempt started + job running (commit avant transport)
  -> n8n/provider simulé par IEmailSyncTransport dans les tests
  -> callback HTTPS/HMAC email.sync
  -> IntegrationReceipt durable + job/opération result-received
  -> traitement explicite EmailSyncService
  -> InboundEmailImportService
  -> EmailMessage normalisé
  -> cursor commit + job/opération terminaux + receipt traité
```

| Modèle | Responsabilité |
| --- | --- |
| EmailAccount | Identité stable du compte fournisseur et disponibilité |
| EmailSyncState | Curseur courant et révision, un état par compte |
| EmailSyncJob | Intention, snapshots du curseur/révisions, résultat et diagnostic |
| IntegrationOperation / Attempt | Corrélation et état logique / résultat technique du dispatch |
| IntegrationReceipt | Unique copie technique durable du lot, canonique et empreinte SHA-256 |
| EmailMessage | Message métier normalisé et identité fournisseur par compte |

Aucune troisième copie des corps : le job ne contient que curseurs, références,
compteurs et, en cas de conflit, `ConflictingExternalMessageId`.
Les références Contact/Company/Opportunity des nouveaux messages sont nulles.
Aucun contact, entreprise ou opportunité n'est inféré ni créé depuis un email.

## Comptes et compatibilité historique

`EmailAccount` conserve WorkspaceId, ConnectionId obligatoire, ProviderCode,
ExternalAccountId, EmailAddress, DisplayName, StatusCode, Revision et dates.
ProviderCode est un code ASCII minuscule `[a-z][a-z0-9-]{0,49}` ; identité externe
non blanche de 500 caractères maximum, adresse 320, nom affiché 200.
ExternalAccountId est l'identité fournisseur stable déclarée, pas l'adresse.
Son authenticité ne peut être vérifiée auprès d'un fournisseur dans cette tranche.

L'unicité `(WorkspaceId, ProviderCode, ExternalAccountId)` inclut les comptes
archivés et révoqués. Deux comptes d'un même fournisseur sont autorisés.
WorkspaceId, ConnectionId, ProviderCode et ExternalAccountId sont immuables
par les routes humaines. L'adresse et le nom peuvent changer sans changer
l'identité fournisseur. Toute mise à jour exige la Revision courante et l'incrémente.

États : `active`, `disabled`, `revoked`. `ArchivedAt` est indépendant.
Seul `active` non archivé peut synchroniser. Disable/archive sont réversibles ;
revoked ne peut pas être réactivé dans cette tranche. Une modification du compte,
même rétablie avant traitement, invalide le snapshot de révision du job.

La connexion 9.3.2 est un identifiant logique de configuration, pas une table.
Création, enqueue, dispatch et traitement vérifient la paire
`IntegrationOptions.WorkspaceId/ConnectionId`. Plusieurs comptes peuvent partager
cette connexion. L'activation du transport 9.3 n'est pas nécessaire pour administrer
les comptes ou traiter un résultat déjà durable ; sa liaison logique doit rester
identique. Aucun credential, mot de passe, access/refresh token ou secret OAuth
n'est conservé dans ces modèles ou DTO ; aucune référence de credential n'est
nécessaire ici. Les futurs credentials restent hors PostgreSQL CRM.

`EmailMessage.EmailAccountId` est nullable pour conserver exactement les lignes
historiques, y compris outbound. Aucun compte n'est inventé et aucun historique
n'est rattaché rétroactivement. L'ancien index unique
`(WorkspaceId, ProviderCode, ExternalMessageId)` est conservé, restreint aux lignes
sans compte et identifiant externe non nul. Le nouvel index unique porte sur
`(WorkspaceId, EmailAccountId, ExternalMessageId)` pour les lignes avec compte.
Une FK composite impose le même workspace **et le même fournisseur** que le compte.
L'import 9.5.1 fournit toujours un compte et un identifiant externe non nuls.
Un éventuel message legacy identique reste indépendant : aucun rapprochement implicite.

## Curseur et cycle de synchronisation

Le curseur est opaque, nullable, au plus 2 048 caractères, sans interprétation
métier ni secret. `EmailSyncState.Revision` ne change qu'au commit d'un succès.
`LastAttemptAt` est mis à jour au début du dispatch, sans changer le curseur.
`LastSuccessfulSyncAt` et `UpdatedAt` accompagnent le commit de succès.

L'enqueue crée atomiquement le job queued et son opération pending. Le lien
`IntegrationOperationId` est donc obligatoire ; aucune fenêtre de job orphelin.
PreviousCursor, StateRevision, AccountRevision et MaxItems sont figés à l'enqueue.
La commande est reconstructible depuis ces champs et les identifiants persistés
de l'opération ; aucun JSON de commande supplémentaire n'est nécessaire.

| Étape | Job | Opération | Curseur courant |
| --- | --- | --- | --- |
| Enqueue | queued | pending | inchangé |
| Début du dispatch | running | awaiting-result | inchangé |
| ACK | awaiting-result | awaiting-result | inchangé |
| Transport incertain | dispatch-unknown | awaiting-result | inchangé |
| Résultat durable | result-received | result-received | inchangé |
| Import réussi | succeeded | succeeded | NextCursor au même commit |
| Échec métier/fournisseur | failed | failed | inchangé |
| Annulation locale | cancelled | failed | inchangé |

Un seul job actif par compte : verrou de compte et index unique partiel couvrant
queued/running/awaiting-result/dispatch-unknown/result-received.
Il n'y a pas de lease SourceCollectionJob, de retry automatique ni de scheduler email.
Un refus définitif de transport clôture failed ; une nouvelle intention explicite
peut ensuite partir depuis le curseur conservé.

`ResolveUnknownAsync` est un service interne explicite : après deux minutes,
une tentative started interrompue devient unknown. Un job running devient
dispatch-unknown ; un job déjà traité/annulé n'est pas rouvert. Aucun renvoi
aveugle de commande incertaine. Le dispatch est borné à 30 secondes et sa
finalisation à 5 secondes ; après un crash, le résultat durable reste traitable.

Un callback peut précéder l'ACK, voire être traité avant cet ACK. La finalisation
du transport termine sa tentative sans écraser le résultat métier. Après une
annulation ou un refus terminal, un premier callback tardif conserve une trace
immédiatement marquée traitée ; le job reste terminal, sans import ni curseur avancé.
Un job annulé avant tout dispatch refuse un callback, faute de tentative débutée.

## Contrat fermé email.sync v1

Commande : `version`, `messageId`, `operationId`, `correlationId`, `type`, `payload`.
Payload : `accountRef` (UUID EmailAccount logique non secret), `previousCursor`
(string ou null), `maxItems` (1–100). Taille maximale 16 Kio UTF-8.
Résultat : mêmes en-têtes, `commandMessageId` et payload contenant `accountRef`,
`outcome`, `nextCursor`, `messages`. Taille totale maximale **512 Kio UTF-8**,
corps brut et forme canonique inclus. La contrainte existante IntegrationReceipt
de 512 Kio suffit, sans relever la limite de collection ou de sonde.

- Version exactement 1, type exactement `email.sync`, UUID non vides au format D.
- Outcome `succeeded` : **0 à 100 messages**, jamais plus que le MaxItems du job.
  Le succès vide est volontaire : une boîte sans nouveaux messages peut avancer
  son curseur. Outcome `failed` : zéro message et nextCursor null.
- nextCursor null représente explicitement un curseur nul après succès ; en
  échec il ne remplace jamais l'ancien curseur.
- Aucun WorkspaceId, destination métier, callback URL libre, secret, attachment
  ou HTML fournisseur accepté. `BodyText` est du texte uniquement ; ce n'est pas
  un champ de stockage HTML ou un extracteur MIME.
- Propriétés inconnues, dupliquées, manquantes, autre casse, mauvais types,
  commentaires, virgules finales et profondeur excessive sont refusés.
- L'authentification signe les octets d'origine ; la canonicalisation ne sert
  qu'à l'idempotence. Erreurs de parsing sans reprise du contenu reçu.

| Champ message | Règle |
| --- | --- |
| externalMessageId | Obligatoire, non blanc, ≤ 500 ; unique dans le lot |
| externalThreadId | Absent/null ou non blanc, ≤ 500 |
| fromAddress | Obligatoire, adresse seule, ≤ 320 |
| toAddresses | Tableau obligatoire ; vide autorisé pour un message Bcc |
| ccAddresses | Absent/null ou tableau |
| subject | Absent/null ou string ≤ 1 000 ; vide conservé |
| bodyText | Absent/null ou string ≤ 32 000 ; vide conservé |
| occurredAt | Obligatoire, date ISO avec offset explicite ou Z |

Au plus 100 destinataires To+Cc **avant déduplication**, chaque adresse non vide,
sans espaces/caractères de contrôle, validée par `MailAddress.TryCreate` et égalité
avec l'adresse seule. Pas de prétention à un validateur RFC exhaustif. Casse des
adresses conservée ; doublons exacts supprimés et ordre ordinal normalisé dans
chaque tableau. Cc absent/null équivaut à un tableau vide. Stockage PostgreSQL jsonb.
Les caractères NUL sont refusés dans les textes persistés.

Messages triés par ExternalMessageId pour la forme canonique, propriétés JSON
réordonnées, UUID normalisés, dates converties en UTC et tronquées à la précision
microseconde PostgreSQL. Les espaces/casses du corps et du sujet sont conservés.
Null et chaîne vide pour BodyText/Subject restent distincts.

## Atomicité, conflits et verrouillage

Réception et traitement sont deux transactions séparées. La réception autorise
le workspace/connexion depuis HMAC puis depuis l'opération liée ; elle vérifie
commande, corrélation, version, type, compte et limite. Le même messageId et le
même contenu canonique + SHA-256 renvoient AlreadyRecorded, même après traitement.
Un contenu différent ou un deuxième messageId de résultat sur la même opération
produit un conflit contrôlé. Aucun de ces chemins n'importe de message.

Le traitement relit/verrouille le compte, le job, l'opération, le receipt et l'état.
Il revalide le contrat, le hash, la liaison et le curseur/révisions. L'import compare
**tous les messages existants avant toute insertion**. Même identité et contenu
normalisé identique : conservation. Contenu divergent : tout le lot échoue avec
`EmailMessageContentConflict` et identifiant fautif persisté ; aucun écrasement.
Les rattachements métier déjà présents sur un message conservé ne sont pas modifiés.

Le même commit contient les insertions EmailMessage, compteurs, état terminal et
FinishedAt du job, état terminal/CompletedAt de l'opération, ProcessedAt du receipt,
ainsi que le nouveau curseur et sa révision en cas de succès. Un défaut SQL, une
annulation ou une exception technique avant commit annule tout : le receipt reste
à traiter. Un crash après commit n'est pas réinterprété comme un échec métier.
Répéter process-result après une finalisation avec receipt traité est un no-op 204 ;
le GET du job conserve son résultat métier, même failed/cancelled.

Les échecs métier post-réception finalisent atomiquement failed avec curseur
inchangé et receipt traité : `EmailResultInvalid`, `EmailAccountUnavailable`,
`EmailAccountChanged`, `EmailConnectionMismatch`, `EmailCursorConflict`,
`EmailMessageContentConflict`. Outcome failed utilise uniquement
`EmailProviderFailed` et rend 204 pour le traitement technique effectué.
Les autres rejets métier rendent 409 après leur commit d'échec. Aucun texte libre
du fournisseur n'est placé dans ErrorCode.

Ordre de verrous (ReadCommitted, conservés jusqu'au commit) :

1. Workspace FOR SHARE, lorsqu'une action exige un workspace actif ;
2. EmailAccount FOR UPDATE ;
3. EmailSyncJob FOR UPDATE ;
4. IntegrationOperation FOR UPDATE ;
5. IntegrationReceipt FOR UPDATE, lorsqu'il existe ;
6. EmailSyncState FOR UPDATE ;
7. EmailMessages concernés, ordonnés par ExternalMessageId, FOR UPDATE.

Toutes les mutations suivent cet ordre ou un sous-ensemble ; réception, annulation
et finalisation du transport n'acquièrent pas de verrou Workspace ultérieur.
Les opérations nouvelles non visibles sont créées dans leur transaction d'enqueue.
Les tentatives ne sont modifiées que sous le verrou de leur opération.
La création de compte est arbitrée par INSERT ON CONFLICT et son index d'identité.
Aucune transaction ou verrou de ligne n'est conservé pendant le transport.
Les FK composées protègent workspace/compte/fournisseur et workspace/connexion/type
de l'opération ; cette dernière FK SQL s'appuie sur l'index 9.4, sans imposer une
connexion non nulle aux anciennes sondes.

## Routes et sécurité

| Route | Effet |
| --- | --- |
| GET /api/email-accounts | Comptes du workspace, archives comprises, sans secret |
| POST /api/email-accounts | Compte actif et état initial dans un commit |
| PUT /api/email-accounts/{id} | Adresse, nom, état, archive ; Revision attendue obligatoire |
| POST /api/email-accounts/{id}/sync-jobs | Enqueue 202, corps `{ "maxItems": 100 }` |
| GET /api/email-sync-jobs/{id} | État et diagnostic, compteurs, identifiant opération |
| POST /api/email-sync-jobs/{id}/process-result | Traitement explicite, sans corps |
| POST /api/email-sync-jobs/{id}/cancel | Annulation locale, sans corps, idempotente |
| GET /api/email-messages | Lecture existante, nouveau filtre emailAccountId |
| GET /api/email-messages/{id} | Lecture existante, nouveau champ EmailAccountId |
| POST /api/integrations/results | Callback HMAC existant, nouveau contrat strict |

Toutes les routes humaines utilisent le cookie opérateur et son workspace ; les
mutations exigent l'antiforgery existant. HMAC seul ne donne aucun droit humain,
cookie seul ne permet aucun callback. Les DTO de création/mise à jour/enqueue
refusent les champs inconnus. Les identifiants externes à un workspace ne donnent
pas accès à ses comptes, jobs ou messages. Les erreurs applicatives sont des codes
fermés, sans corps fournisseur ni valeurs secrètes.

La route humaine existante `/api/integrations/pending-receipts` est restreinte aux
métadonnées MessageId, OperationId, ReceivedAt et ProcessedAt, pour ne pas exposer
le contenu technique d'un email reçu. La méthode interne conserve son rôle de
reprise durable. Aucune route d'envoi ni création publique manuelle de message.
Les filtres historiques directionCode, ContactId, CompanyId, OpportunityId et dates
restent disponibles. La lecture historique des outbound reste inchangée.

## Migration et validations locales

Migration nouvelle : **20261016093720_Phase951InboundEmailSync** (+ Designer et snapshot).
Création EmailAccounts/EmailSyncStates/EmailSyncJobs, colonne nullable sur
EmailMessages, indexes/FK et extension du check de type IntegrationOperation.
Aucune migration historique éditée. Aucun compte, job ou curseur rétroactif créé.

Down/Up est vérifié sur une base 9.4.1 peuplée de messages inbound/outbound,
sondes avec et sans connexion, réception et collecte distante. Down refuse
transactionnellement toute présence de compte/état/job/opération email ou
message rattaché : il ne supprime jamais silencieusement l'historique 9.5.1.
La reconstruction vierge, les FK, les unicités et HasPendingModelChanges=false
sont vérifiés sur des bases jetables.

Les adaptations des tests historiques concernent leur dernière migration
attendue, l'exclusion des trois nouvelles tables dans les comparaisons des
tables préexistantes et le palier de descente/remontée autour de 9.4 décrit
ci-dessous. Leurs bornes historiques de montée/descente restent identiques.

| Passage | Résultat |
| --- | --- |
| Premier build Release | Réussi ; deux avertissements CS8618 préexistants de Settings.razor |
| Compilation initiale des nouveaux tests | Réussie ; cinq avertissements des nouveaux tests, corrigés ensuite |
| Build Release final, après fin du testhost intermédiaire | **Réussi, 0 erreur, 0 avertissement** |
| Premier ciblé 9.5.1 | 79 tests : 78 réussis, 1 échec, 0 ignoré |
| Ciblé après correction | **79/79**, 0 échec, 0 ignoré ; compilation Release sans avertissement |
| Premier passage de régression | 454 tests : 453 réussis, 1 échec de l'attente de descente 9.4, 0 ignoré |
| Vérification du correctif et ciblés finaux | **83/83** : 81 dédiés 9.5.1 + les 2 tests de migration distante ; 0 échec, 0 ignoré |
| Régressions finales | **454/454 dans la suite globale**, après vérification ciblée du correctif ; 0 échec, 0 ignoré |
| Suite globale finale | **1 542/1 542**, 0 échec, 0 ignoré, 27 min 59 s, code de sortie 0 |
| Cohérence EF CLI | Aucun changement depuis la migration ; configuration Testing inerte |
| git diff --check / index | Réussi, nouveaux fichiers également contrôlés ; index vide |

L'échec initial concernait exclusivement l'isolation du test de FK de message :
le message au mauvais fournisseur réutilisait aussi une identité existante et
PostgreSQL retournait 23505 avant 23503. Le test emploie maintenant une identité
distincte pour exercer la FK ; l'assertion dédiée de doublon reste inchangée.
Les TRX successifs sont conservés hors Git dans le dossier temporaire local
`ProspectionCrm-phase951`, sans remplacement du premier passage rouge.

Le premier passage de régression a aussi révélé une attente incorrecte adaptée
dans `RemoteCollectionMigrationTests` : lors d'une descente sous 9.4, le Down
9.5 vide est committé avant que le Down 9.4 refuse d'effacer son historique.
Le test vérifie maintenant cet état intermédiaire exact, la migration 9.5 en
attente, puis sa remontée et la conservation de l'intention distante.

Un lancement intermédiaire des tests a été déclenché trop tôt, avant la fin du
build : testhost a verrouillé les DLL sous Windows et provoqué un échec de copie
MSB3027/MSB3021 (20 avertissements, 4 erreurs). Ce passage utilisait les anciens
binaires et a reproduit l'ancienne attente de migration ; il n'est pas retenu
comme validation du code final (80/81 sur anciens binaires). Build et tests ont
ensuite été séquencés : build vert, puis 83/83 sur les binaires finaux.

Les classes dédiées couvrent comptes/statuts/isolation, contrat et tailles,
identité multi-compte, reprise dans un nouvel hôte, commit interrompu et réponse
perdue, panne SQL après insertion mais avant avancement du curseur, huit réceptions
et huit traitements concurrents sur connexions distinctes, enqueue concurrent,
transport sans verrou, callback avant ACK, traitement avant ACK, annulation,
révocation entre réception et traitement, curseur/révision modifiés, HMAC/CSRF,
lecture des messages et migration historique. Tous les identifiants, adresses,
clés et curseurs de tests sont synthétiques.

Répartition des **81 tests dédiés réussis**, vérifiée dans le TRX final :

| Classe | Tests |
| --- | ---: |
| EmailAccountSecurityTests | 14 |
| EmailSyncConcurrencyTests | 10 |
| EmailSyncContractTests | 31 |
| EmailSyncLifecycleTests | 24 |
| EmailSyncMigrationTests | 2 |

Les deux tests RemoteCollectionMigrationTests réussis dans le même passage
appartiennent aux régressions 9.4, d'où le total de 83 pour cette commande.

La suite globale finale a été exécutée après le build final et le passage
83/83, sans modification de code pendant son exécution. Elle valide localement
les 81 nouveaux cas et les 1 461 tests préexistants. Ce résultat est local ;
aucun commit, push ou nouveau run CI n'a été déclenché par cette tranche.

Commandes reproductibles (depuis la racine, Docker Linux disponible) :

```powershell
dotnet build ProspectionCrm.slnx --configuration Release --no-restore
dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~EmailSync|FullyQualifiedName~EmailAccountSecurity'
dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~Integration|FullyQualifiedName~RemoteCollection|FullyQualifiedName~MigrationTests|FullyQualifiedName~RssCollection|FullyQualifiedName~Ingestion|FullyQualifiedName~AutomationSafetyPolicy|FullyQualifiedName~OperatorSecurity|FullyQualifiedName~PipelineClone|FullyQualifiedName~PipelineTransfer'
dotnet test ProspectionCrm.slnx --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/ProspectionCrm.Api --configuration Release --no-build -- --environment Testing --ConnectionStrings:DefaultConnection 'Host=127.0.0.1;Port=1;Database=design;Username=design;Password=design'
git diff --check
```

La commande EF utilise une configuration Testing inerte, pas les User Secrets
Development. L'outil EF installé 10.0.10 signale son décalage avec le runtime
10.0.12 ; aucune mise à jour d'outil n'est nécessaire pour ce contrôle.

## Limites et préparation précise de 9.5.2

Le dispatch et la résolution des transports interrompus restent service-only.
Les jobs peuvent être administrés et annulés, mais aucune intégration email
réelle ne peut être lancée par cette livraison. Pas de pièces jointes, parsing
MIME/HTML, outbound, OAuth, renouvellement de credential, purge, planification,
reconnexion d'un compte révoqué ou rattachement métier automatique. La liste
historique des emails reste non paginée ; aucune interface complète n'est ajoutée.

Une future tranche devra définir séparément : l'implémentation de transport
email et son activation explicite ; la correspondance accountRef/identité réelle
vérifiée ; la conservation externe et la révocation des credentials ; le protocole
de pagination fournisseur avec cette limite de lot et les curseurs expirés ;
une éventuelle reconnexion conservant l'identité stable ; la supervision/reprise
opérateur du dispatch. Le contrat et l'atomicité existants devront être conservés
et testés avec le fournisseur choisi. Rien dans cette tranche n'autorise un envoi
email ni un changement de la policy d'automatisation.

## Fichiers et état Git final

Périmètre exact : **28 fichiers suivis modifiés, 21 fichiers nouveaux**, soit 49.
`M` = modifié, `??` = nouveau non suivi. Aucun staging, commit ou push effectué.

```text
 M src/ProspectionCrm.Api/Controllers/EmailMessagesController.cs
 M src/ProspectionCrm.Api/Controllers/IntegrationController.cs
 M src/ProspectionCrm.Api/Data/Configurations/EmailMessageConfiguration.cs
 M src/ProspectionCrm.Api/Data/Configurations/IntegrationConfiguration.cs
 M src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
 M src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs
 M src/ProspectionCrm.Api/Dtos/EmailMessages/EmailMessageDto.cs
 M src/ProspectionCrm.Api/Entities/EmailMessage.cs
 M src/ProspectionCrm.Api/Program.cs
 M src/ProspectionCrm.Api/Services/EmailMessageService.cs
 M src/ProspectionCrm.Api/Services/IEmailMessageService.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
 M src/ProspectionCrm.Api/Services/Integrations/IntegrationStore.cs
 M tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs
 M tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
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
?? docs/phase-9-5-step-1-validation.md
?? src/ProspectionCrm.Api/Controllers/EmailAccountsController.cs
?? src/ProspectionCrm.Api/Data/Configurations/EmailSyncConfiguration.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261016093720_Phase951InboundEmailSync.Designer.cs
?? src/ProspectionCrm.Api/Data/Migrations/20261016093720_Phase951InboundEmailSync.cs
?? src/ProspectionCrm.Api/Dtos/EmailMessages/EmailAccountDtos.cs
?? src/ProspectionCrm.Api/Dtos/Integrations/EmailSyncContracts.cs
?? src/ProspectionCrm.Api/Entities/EmailAccount.cs
?? src/ProspectionCrm.Api/Entities/EmailSyncJob.cs
?? src/ProspectionCrm.Api/Entities/EmailSyncState.cs
?? src/ProspectionCrm.Api/Services/Email/EmailAccountService.cs
?? src/ProspectionCrm.Api/Services/Email/EmailSyncDispatcher.cs
?? src/ProspectionCrm.Api/Services/Email/EmailSyncLocks.cs
?? src/ProspectionCrm.Api/Services/Email/EmailSyncService.cs
?? src/ProspectionCrm.Api/Services/Email/InboundEmailImportService.cs
?? tests/ProspectionCrm.Api.Tests/EmailAccountSecurityTests.cs
?? tests/ProspectionCrm.Api.Tests/EmailSyncConcurrencyTests.cs
?? tests/ProspectionCrm.Api.Tests/EmailSyncContractTests.cs
?? tests/ProspectionCrm.Api.Tests/EmailSyncFixture.cs
?? tests/ProspectionCrm.Api.Tests/EmailSyncLifecycleTests.cs
?? tests/ProspectionCrm.Api.Tests/EmailSyncMigrationTests.cs
```

HEAD et `main` restent à la référence initiale ; index vide. Les migrations
historiques, la policy Automation, les configurations et les workflows n8n sont
inchangés. Les conteneurs Development PostgreSQL et n8n sont restés arrêtés ;
seules les bases jetables ont servi aux tests. Aucun secret, TRX, dump, log,
credential, binaire ou artefact de test n'est ajouté à Git.
