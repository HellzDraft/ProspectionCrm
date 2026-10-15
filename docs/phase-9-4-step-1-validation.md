# Phase 9.4.1 — Cycle de vie asynchrone de collecte distante

## Référence et périmètre

Travail local sur `main`, à partir de
`7eb3155949634ad99c431543ba6488b211dac2fa`, identique à `origin/main` et au HEAD
distant lors du contrôle initial. Index et working tree initialement propres.
La [CI #61](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38717244322)
de cette référence a été vérifiée `completed / success`.
Le rapport 9.3.3 et les implémentations/tests des Phases 6, 7 et 9.3 ont été relus.

Cette tranche livre le modèle, le dispatch simulable, la réception durable et
la reprise. Elle s'arrête à `result-received` : aucune ingestion distante,
aucune écriture d'Opportunity, de Company ou de SourceExecution distante.
La Phase 9.4.2 n'est pas commencée. Aucun staging, commit ou push.

## Décision d'architecture

Le type interne de source `remote-collection` emprunte un chemin explicite
`RemoteCollectionDispatcher` / `RemoteCollectionState` dans le worker.
`ISourceAdapter` reste synchrone ; RSS conserve son adapter, son orchestrateur,
son ingestion et ses protections SSRF/TLS/XML. L'endpoint de collecte synchrone
refuse une source distante avec `RemoteCollectionRequiresJob`.

Le resolver commun valide les ressources et les critères. Le dispatcher valide
en plus la configuration d'intégration activée et la correspondance du workspace.
Une configuration désactivée produit `RemoteIntegrationDisabled`, sans intention
ni appel réseau. Aucun paramétrage réel n'a été activé pour cette intervention.

```text
Local RSS : queued -> running [lease] -> IngestionService -> succeeded / failed
                              | erreur temporaire -> queued avec backoff

Distant   : queued -> running [lease]
                      -> intention + opération persistées, COMMIT
                      -> tentative transport persistée, COMMIT
                      -> HTTP HTTPS/HMAC hors transaction PostgreSQL
                      -> issue technique persistée
                      -> awaiting-result [sans lease]        si ACK 202
                      -> dispatch-unknown [sans lease]       si issue incertaine
                      -> queued avec backoff / failed       si rejet certain

Callback HMAC autorisé, lié et conforme au contexte :
running / awaiting-result / dispatch-unknown -> result-received [sans lease]
                                               |
                                    arrêt volontaire de 9.4.1

awaiting-result / dispatch-unknown / result-received -> cancelled localement
callback après cancelled -> réception traçable, job toujours cancelled
```

## États et tentatives

| État du job | Actif pour l'unicité | Lease | FinishedAt | ErrorCode |
| --- | --- | --- | --- | --- |
| `queued` | oui | absent | null | null |
| `running` | oui | présent | null | null |
| `awaiting-result` | oui | absent | null | null |
| `dispatch-unknown` | oui | absent | null | `RemoteDispatchUnknown` |
| `result-received` | oui | absent | null | null |
| `succeeded`, `failed`, `cancelled` | non | absent | renseigné | selon l'issue |

Les trois états distants ont un `StartedAt` renseigné, un `AttemptCount > 0`
et aucun `SourceExecutionId`. L'index actif inclut ces trois états pour
`(WorkspaceId, SavedSearchId, PipelineStageId)`. Enqueue manuel et scheduler
respectent cette même unicité. Les listes acceptent les nouveaux filtres ;
le DTO expose `IntegrationOperationId` nullable, sans secrets ni snapshot.

La tentative de job prend également `awaiting-result`, `dispatch-unknown` ou
`result-received`. Son `FinishedAt` reste null dans ces états : un ACK ne termine
pas la tentative de collecte métier. Un rejet définitif de dispatch ferme la
tentative en échec ; un retry crée une nouvelle tentative de job. Une annulation
ferme la tentative encore en attente. La sémantique locale historique est conservée.

`IntegrationAttempt` conserve ses états 9.3 : `started`, `acknowledged`, `failed`,
`unknown`. Son `FinishedAt` décrit uniquement l'issue technique du transport.
Un ACK ne place ni le job ni l'opération en `succeeded`.

## Intention persistée et autorité CRM

`RemoteCollectionIntents` possède :

- `JobId` comme clé primaire ; `OperationId` unique ;
- workspace, connexion logique et type d'opération ;
- `SourceConfigurationId`, fingerprint, snapshot d'historique ;
- commande canonique exacte et date de création.

La FK composite du job impose le workspace commun. Une FK PostgreSQL composite
vers l'index unique `(WorkspaceId, ConnectionId, Id, Type)` de l'opération impose
également sa connexion et son type `collection.request`. Le même job ne peut
posséder deux intentions ; une opération ne peut piloter deux jobs. Une sonde
`technical.probe` ne peut pas être liée. Les anciens `ConnectionId=NULL` restent
valides et non liés. Cette FK est ajoutée explicitement par la migration : un
alternate key EF sur `ConnectionId` rendrait à tort ce champ obligatoire pour
les anciennes sondes.

Le champ historique obligatoire `IntegrationOperation.ProbeId` est conservé.
Il reste non vide pour `technical.probe` ; il vaut le UUID vide, explicitement
contraint, pour `collection.request`. Il n'est jamais envoyé dans le contrat de
collecte. Le `requestId` distant est propre au payload figé de l'intention.
Le contrat technique probe v1 reste inchangé.

Le contexte est résolu sous des verrous de lecture pendant la courte transaction
de préparation. `CollectionFingerprint` capture identités, configuration,
activation, archivage et versions des search/source/pipeline/stage.
`IngestionHistory.Context` fournit le snapshot lisible ; les IDs de destination
et le workspace viennent du job et de l'intention. Le workspace doit être actif
à la préparation et à la première réception non terminale.

Un retry réutilise la même commande, le même requestId, le même messageId et la
même opération. Une modification de contexte ou de connexion entre tentatives
arrête ce retry (`RemoteContextChanged` / `RemoteConnectionMismatch`).
À la première réception, le contexte courant est de nouveau verrouillé et
comparé. Une modification produit 409 sans réception ni ingestion.
Un replay déjà enregistré reste idempotent si la configuration a changé depuis.

## Contrats v1 fermés

Commande : `version`, `messageId`, `operationId`, `correlationId`,
`type="collection.request"`, `payload`.

Payload commande : `requestId` UUID non vide, `query` chaîne de 0 à 200 caractères,
`maxItems` entier de 1 à 100. Les critères autorisés dans `SavedSearch.CriteriaJson`
sont exclusivement `query` et `maxItems`, facultatifs avec défauts `""` et `100`.
Le JSON de critères et la commande sont limités à 4 096 octets UTF-8.
Aucun workspace, destination CRM, URL de callback, secret ou ConfigurationJson
libre n'est envoyé. SearchUrl n'est pas transmis pour ce premier type interne.

Résultat : les mêmes en-têtes, plus `commandMessageId` ; payload
`requestId`, `outcome`, `items`. `succeeded` exige 1 à 100 candidats, sans dépasser
le maxItems de la commande ; `failed` exige un tableau vide. Dans les deux cas,
le résultat est seulement reçu, sans traitement métier dans cette tranche.

| Champ candidat | Obligation | Maximum de caractères |
| --- | --- | --- |
| `externalId` | obligatoire, non blanc, unique après trim dans le lot | 500 |
| `title` | obligatoire, non blanc | 200 |
| `sourceUrl` | facultatif, URL HTTP(S) valide sans identifiants | 2 048 |
| `location` | facultatif, non blanc si présent | 200 |
| `description` | facultatif, non blanc si présent | 10 000 |

Les champs facultatifs absents sont omis ; une valeur null explicite est refusée.
Ces tailles correspondent à `IngestionItemRequest`. Aucun champ Company n'est
introduit et aucune entreprise n'est créée automatiquement.

Le résultat brut et sa forme canonique sont chacun limités à **524 288 octets**.
Les limites de champs et de lot ne dispensent pas de cette limite totale.
Propriétés inconnues, omissions obligatoires, doublons JSON, casse alternative,
mauvais types, UUID invalides/vides et profondeur excessive sont refusés.
Les erreurs n'exposent ni corps JSON ni exception détaillée du parseur.

## Transport, retry et reprise

La garde existante du worker protège l'envoi concurrent avec une session advisory
lock dédiée, sans transaction pendant HTTP. Toutes les transitions à plusieurs
lignes utilisent l'ordre de verrous job puis opération. Le transport HTTPS/HMAC,
le contrôle de concurrence et les `IntegrationAttempt` réutilisent les garanties
9.3. Les routes `/probes/...` ne peuvent ni envoyer ni traiter une collecte.

| Preuve durable après interruption | Reprise |
| --- | --- |
| Intention/opération sans tentative transport | retry borné, même intention |
| Dernière tentative `failed` | retry borné selon MaxAttempts/backoff existants |
| Dernière tentative `started` | `unknown` technique puis `dispatch-unknown`, aucun renvoi |
| Dernière tentative `unknown` | `dispatch-unknown`, aucun renvoi |
| Dernière tentative `acknowledged` | `awaiting-result`, aucun renvoi |
| Réception durable | conserver `result-received`, aucun renvoi |

Les erreurs réseau, déconnexions, délais et HTTP 5xx restent incertains selon la
politique 9.3. Un rejet technique certain autorise une nouvelle tentative de
transport de la même commande. La saturation locale avant envoi est également
retryable. Les plafonds et délais du worker s'appliquent ; aucune durée métier
arbitraire n'est créée pour attendre un callback.

Les jobs `running` interrompus sont rapprochés après expiration du lease, sous
la garde existante. Un job sans lease dans un état distant ne devient pas
`CollectionAbandoned`. Si un callback a retiré le lease avant l'arrêt du processus,
la reprise traite aussi la tentative transport restée `started` : elle devient
`unknown` uniquement après acquisition de la garde, sans modifier le résultat ni
rouvrir un job annulé. Un worker vivant qui attend encore l'ACK conserve sa garde.

La résolution de `dispatch-unknown` est volontairement limitée : callback valide
vers `result-received`, ou annulation locale. Aucune route de retry aveugle ni
résolution arbitraire en « failed » n'est ajoutée. Le diagnostic conserve
l'intention, la commande et toutes les tentatives.

## Réception et annulation

Le même endpoint machine `POST /api/integrations/results` vérifie HTTPS, cible
exacte, HMAC sur les octets d'origine, clé active et fenêtre temporelle.
Le cookie humain seul reste insuffisant. Le lecteur est borné à 512 Kio + 1 ;
seul un document explicitement de type collecte peut dépasser la limite probe
de 4 096 octets. Cette sélection de taille ne donne aucune autorisation.

La réception autorise workspace et connexion avant les branches replay/conflit,
puis vérifie opération, corrélation, commandMessageId, version, type, requestId
et contexte CRM. Le contenu canonique et son SHA-256 sont comparés pour les replays.
La réception et les transitions opération/job/attempt sont atomiques. Des
livraisons concurrentes identiques donnent un seul enregistrement et des 202
idempotents ; un contenu différent sous le même message donne 409.

Un résultat peut précéder l'ACK. Il est enregistré et le lease est libéré ;
l'ACK tardif ne réécrit pas l'état métier. `ProcessedAt` reste null : la réception
survit au redémarrage, disponible pour le futur traitement explicite.

`cancel` accepte les trois états distants, en plus de queued, et reste idempotent.
La réception tardive d'un job annulé ou déjà en échec terminal conserve une trace
sans rouvrir le job. Elle ne requiert plus de contexte métier actif puisqu'aucun
traitement n'est permis. Aucun appel externe d'annulation n'est effectué.
Pendant le backoff `queued` d'un rejet certain, un premier callback reçoit 409 ;
il pourra être présenté de nouveau après le prochain dispatch ou l'annulation.

## Migration additive

`20261015104738_Phase941RemoteCollectionLifecycle` ajoute la table d'intention et
ses contraintes/index, élargit les contraintes d'états et l'index actif, autorise
le nouveau type d'opération et augmente la borne de stockage des résultats.
Aucune migration historique n'est réécrite ; aucune ancienne ligne n'est
requalifiée ou liée automatiquement.

Les tests utilisent exclusivement des PostgreSQL 18 jetables : base vierge,
upgrade depuis 9.3.2, conservation exacte des jobs locaux, tentatives, exécutions,
sondes avec/sans connexion, réceptions et opportunités historiques, puis Down/Up.
Le Down refuse explicitement une histoire distante non vide avant toute
suppression, pour préserver ces intentions et ne pas les convertir en jobs locaux.
Les tests historiques qui nommaient la dernière migration pointent maintenant
sur 9.4.1 ; les assertions d'ordre de migrations, d'inventaire des tables
historiques et de propriétés du DTO ont été ajustées. Le scénario RSS vérifie
explicitement que `IntegrationOperationId` reste null.

## Validation locale

- Build solution Release final : réussi, 0 erreur et 0 avertissement. Une
  reconstruction isolée a également réussi, en signalant les deux avertissements
  de nullabilité préexistants de `Settings.razor` (également observés sur le HEAD initial).
- Nouveaux tests 9.4.1 : **85/85 réussis**, aucun ignoré. Contrats, lifecycle HTTP
  avec transport simulé, base vierge/upgrade/contraintes/Down-Up.
- Premier passage global avant les derniers ajustements d'assertions : **1 416/1 421 réussis**,
  cinq échecs, aucun ignoré ; durée 30 min 6 s. Les cinq cas sont détaillés ci-dessous.
- Ciblage étendu intermédiaire : **117/118 réussis**. Le seul échec était l'assertion
  de position des migrations, désormais corrigée.
- **Dernier ciblage : 91/91 réussis**, aucun ignoré, en 42 s. Il couvre les 85
  nouveaux tests et les six cas historiques associés aux échecs précédents
  (les trois modes du scénario Phase 8 sont inclus). Tous les cas en échec dans
  la suite globale sont explicitement `Passed` dans ce dernier TRX.
- **Second passage global final du 15 octobre 2026 : 1 421/1 421 réussis,
  0 échec, 0 ignoré**, en **27 min 48 s**, code de sortie 0. Il valide l'état
  final après les ajustements et ciblages précédents, sans nouvelle correction.
- `dotnet ef migrations has-pending-model-changes --configuration Release --no-build` :
  aucun changement de modèle depuis la dernière migration. Le contrôle a été
  exécuté en environnement Testing avec une connexion de design factice, port 1.
- `git diff --check` : conforme ; contrôle des nouveaux fichiers également effectué
  par `git diff --no-index --check`, sans staging.

Les trois échecs structurels historiques concernaient les deux snapshots globaux
de tables (`SourceCollectionJobMigrationTests` et
`SourceCollectionSchedulingMigrationTests`) et la liste fermée de propriétés JSON
de `SourceCollectionJobTests.EnqueueIsDurableReadableAndNeverExecutes`.
Ils sont corrigés et repassent. L'ordre des migrations du premier test est aussi
mis à jour avec 9.3.2 puis 9.4.1.

Les deux autres échecs concernaient le compteur de jobs terminés du scénario
`AutomationPhase8EndToEndTests.EventToDispatchWorkerDecisionEffectAndSupervision`,
modes `automatic` et `manual`. Les trois modes passent sur un checkout isolé du
HEAD initial, puis dans le ciblage étendu et dans le dernier ciblage sur le code
final. Aucun code d'automatisation ni assertion de ce scénario n'a été modifié.
Ces échecs ne sont pas reproduits ; une sensibilité au timing est une hypothèse,
pas une cause démontrée. Le checkout isolé a été retiré après conservation du TRX.

La sélection historique collecte/RSS/Phases 6/7/ingestion/intégration/migrations
représente 543 cas dans le premier passage global, dont ces trois échecs structurels
ensuite corrigés et revalidés. Les autres cas de cette sélection passent.

Après ces corrections et ciblages, un unique second passage global a été exécuté
sur l'état final, sans modification préalable de fichiers, avec la commande demandée :

```powershell
dotnet test ProspectionCrm.slnx `
  --configuration Release `
  --no-build `
  --no-restore
```

Ce second passage est entièrement vert, y compris les cinq cas en échec lors du
premier passage. Aucun autre test ni build n'a été relancé ensuite. Seul le
présent rapport a été actualisé ; l'historique du premier passage est conservé.

Le ciblage couvre ACK sans succès métier, lease libéré, non-reclaim, unicité
manuelle et scheduler, concurrence de workers, verrous disponibles pendant HTTP,
échec certain avec réutilisation exacte de commande, unknown sans renvoi,
interruption à chaque frontière persistée, callback avant ACK, garde du worker
vivant, reprise après réception, annulation, mauvais workspace/connexion, limites
du lot et du corps, signature erronée, contexte modifié, et absence d'effet métier.
Les contrats couvrent aussi 101 items, doublons, types, UUID et tailles de champs.

Les TRX sont des artefacts locaux ignorés dans `TestResults/phase941/` :
`phase941-targeted.trx`, `phase941-full.trx`, `phase941-final-targeted.trx`,
`phase941-verified.trx` et `baseline-phase8.trx`.
Le dernier ciblage utilise `--artifacts-path TestResults/phase941/final-build`
pour garder ses binaires indépendants du passage global déjà en cours.
Le résultat du second passage global est celui de la sortie console de la commande
ci-dessus, exécutée sans logger TRX supplémentaire.
Les tests n'appellent aucun n8n réel. Les serveurs TLS de tests 9.3 sont locaux et
simulés. Aucun service réel n'est démarré ; aucune migration n'est appliquée à
Development. La configuration réelle `Integrations:Enabled=false` est conservée.
Le workflow n8n réel n'est pas modifié. Le profil Edge temporaire de 9.3.3 reste
hors Git ; aucune suppression n'est prétendue ni tentée dans cette tranche.

## Limites et plan précis de 9.4.2

1. Définir le traitement explicite d'une réception de collecte, autorisé dans le
   workspace de l'intention, avec protection contre concurrence/replay/cancel.
2. Revalider le fingerprint et l'état actif au moment du traitement, pas seulement
   à la réception. Prendre les destinations exclusivement depuis le job/intention.
3. Convertir les candidats en `IngestionRequest`, puis réutiliser `IngestionService`
   et ses identités persistantes. Adapter sa précondition/job fencing pour ce
   traitement sans lease de dispatch ; ne pas dupliquer l'ingestion.
4. Définir et tester l'atomicité entre ingestion, SourceExecution, clôture de
   tentative/job/opération et `ProcessedAt`, y compris reprise après crash.
   Un résultat failed doit donner une issue métier contrôlée sans candidats ingérés.
5. Préparer séparément le futur workflow fictif de deux éléments déterministes et
   son autorisation d'exécution réelle. Aucune création de ces opportunités, aucun
   import/modification du workflow réel, aucun enable de service dans 9.4.1.

En attendant, `result-received` reste actif, durable et non traité. La surveillance
opérationnelle et une éventuelle procédure de résolution manuelle d'un unknown
sans callback restent à concevoir séparément ; aucun resend implicite n'existe.
La stabilité du scénario Phase 8 sous charge reste un point à surveiller lors
de la CI ; les tests ciblés et le second passage global final sont verts.

## État final

`main` reste au HEAD `7eb3155949634ad99c431543ba6488b211dac2fa`, également confirmé
sur le HEAD distant `origin/main` après les tests. Index vide : **35 fichiers
modifiés et 11 nouveaux, tous non indexés**, listés ci-dessous. Aucun commit,
push ou déclenchement de CI pour cette tranche. Aucun checkout auxiliaire restant.
Après les tests, seul `prospectioncrm-postgres-1` reste en exécution dans Docker,
avec l'état `healthy` ; les conteneurs PostgreSQL jetables ont été supprimés par
Testcontainers. Aucun accès de migration à la base Development réelle.

## Inventaire exact des fichiers

Tous les fichiers ci-dessous sont non indexés.

### Nouveaux fichiers

- `docs/phase-9-4-step-1-validation.md`
- `src/ProspectionCrm.Api/Data/Configurations/RemoteCollectionIntentConfiguration.cs`
- `src/ProspectionCrm.Api/Data/Migrations/20261015104738_Phase941RemoteCollectionLifecycle.cs`
- `src/ProspectionCrm.Api/Data/Migrations/20261015104738_Phase941RemoteCollectionLifecycle.Designer.cs`
- `src/ProspectionCrm.Api/Dtos/Integrations/CollectionIntegrationEnvelopes.cs`
- `src/ProspectionCrm.Api/Entities/RemoteCollectionIntent.cs`
- `src/ProspectionCrm.Api/Services/Collection/RemoteCollectionDispatcher.cs`
- `src/ProspectionCrm.Api/Services/Collection/RemoteCollectionState.cs`
- `tests/ProspectionCrm.Api.Tests/CollectionIntegrationContractTests.cs`
- `tests/ProspectionCrm.Api.Tests/RemoteCollectionLifecycleTests.cs`
- `tests/ProspectionCrm.Api.Tests/RemoteCollectionMigrationTests.cs`

### Fichiers modifiés

- `src/ProspectionCrm.Api/Controllers/IntegrationController.cs`
- `src/ProspectionCrm.Api/Data/Configurations/IntegrationConfiguration.cs`
- `src/ProspectionCrm.Api/Data/Configurations/SourceCollectionJobAttemptConfiguration.cs`
- `src/ProspectionCrm.Api/Data/Configurations/SourceCollectionJobConfiguration.cs`
- `src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs`
- `src/ProspectionCrm.Api/Data/ProspectionCrmDbContext.cs`
- `src/ProspectionCrm.Api/Dtos/CollectionJobs/SourceCollectionJobDto.cs`
- `src/ProspectionCrm.Api/Entities/SourceCollectionJob.cs`
- `src/ProspectionCrm.Api/Entities/SourceCollectionJobAttempt.cs`
- `src/ProspectionCrm.Api/Program.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionContextResolver.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionJobFinalizer.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionJobProcessor.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionJobRecovery.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionJobService.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionOrchestrator.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionRetryPolicy.cs`
- `src/ProspectionCrm.Api/Services/Collection/SourceCollectionSchedulingService.cs`
- `src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs`
- `src/ProspectionCrm.Api/Services/Integrations/IntegrationStore.cs`
- `src/ProspectionCrm.Api/Services/Integrations/IntegrationTransport.cs`
- `tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs`
- `tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs`
- `tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs`
- `tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs`
- `tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs`
- `tests/ProspectionCrm.Api.Tests/IngestionHistoryReadTests.cs`
- `tests/ProspectionCrm.Api.Tests/IngestionHistoryTests.cs`
- `tests/ProspectionCrm.Api.Tests/IntegrationPersistenceMigrationTests.cs`
- `tests/ProspectionCrm.Api.Tests/IntegrationSecurityMigrationTests.cs`
- `tests/ProspectionCrm.Api.Tests/Phase6ReconstructionTests.cs`
- `tests/ProspectionCrm.Api.Tests/RssCollectionTests.cs`
- `tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs`
- `tests/ProspectionCrm.Api.Tests/SourceCollectionJobTests.cs`
- `tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingMigrationTests.cs`
