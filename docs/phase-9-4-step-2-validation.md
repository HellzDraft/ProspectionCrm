# Phase 9.4.2 — Traitement métier des résultats de collecte distante

## Référence et périmètre

Départ sur `main` au commit
`76967824b9aabf596e2e12cd8d82fbba72e6d9b0`
(`Add asynchronous remote collection lifecycle`). Le HEAD distant et
`origin/main` ont été contrôlés identiques ; working tree propre et index vide.
La [CI Build #62](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38734955300)
est `completed / success`.

Cette tranche ajoute le traitement humain explicite d'une réception distante
déjà persistée. Le transport est simulé dans les tests. Aucun appel à n8n réel,
aucune modification de son workflow, aucune activation d'intégration réelle,
aucune migration ni création d'opportunité dans Development.
La Phase 9.4.3 n'est pas exécutée.

## Architecture

`RemoteCollectionResultProcessor` utilise le workspace de l'identité humaine.
Il verrouille et vérifie le job, l'intention, l'opération, la réception et la
tentative, puis revalide les ressources CRM avant de convertir les candidats.
Il appelle **le même `IIngestionService` / `IngestionService`** que la collecte
locale. Il ne contient ni transport HTTP, ni moteur de déduplication alternatif.

```text
réception durable (result-received, ProcessedAt=NULL)
  -> action humaine process-result
  -> transaction PostgreSQL + verrous
  -> vérification du rattachement, du contrat et du hash de réception
  -> revalidation du contexte CRM et du fingerprint
  -> IngestionRequest -> IngestionService existant
  -> SourceExecution, OpportunitySource, SourceExecutionItem
  -> job + tentative + opération terminaux, réception traitée
  -> COMMIT unique
```

Le GET job conserve son DTO : état, `SourceExecutionId` et
`IntegrationOperationId` suffisent. Le détail métier reste dans les lectures
SourceExecution et historique existantes ; aucun contenu canonique, secret,
commande ou snapshot supplémentaire n'est exposé.

## Route humaine

`POST /api/source-collection-jobs/{id}/process-result` exige le cookie opérateur
humain et un jeton antiforgery valide. La policy humaine et le schéma humain
sont explicites. Une signature machine seule ne donne aucun accès.

La route ne prend aucun corps : un contenu ou un transfert chunked est refusé
avec 400. Aucun candidat, workspace ou identifiant de destination n'est accepté
du client. Le job n'a pas de révision optimiste ; le verrou de ligne et la
précondition `result-received` arbitrent les appels concurrents.

| Issue | HTTP | État persistant |
| --- | --- | --- |
| Ingestion réussie | 204 | job/tentative/opération succeeded, receipt traité |
| Résultat distant outcome=failed | 204 | job/tentative/opération failed, receipt traité |
| Contexte changé ou conflit métier définitif | 409 + code contrôlé | job/tentative/opération failed, receipt traité |
| Job hors workspace ou absent | 404 | inchangé |
| Job non result-received, y compris déjà traité | 409 RemoteResultNotReady | inchangé |
| Rattachement persistant incohérent | 409 RemoteResultBindingMismatch | inchangé, diagnostic conservé |
| Erreur technique inattendue | 500 contrôlé | rollback intégral avant commit |

Une seconde action sur un job déjà traité reçoit donc un conflit explicite ;
elle ne réingère jamais. Aucun corps distant ni exception interne n'est renvoyé
dans les erreurs HTTP.

## Transaction et ordre des verrous

Le processeur possède la transaction PostgreSQL, en Read Committed. Ordre :

1. advisory lock d'identités du workspace (`WorkspaceIdentityLock`), commun à
   tous les writers d'identités et à l'ingestion existante ;
2. job `FOR UPDATE`, dans le workspace courant ;
3. intention `FOR SHARE` ;
4. opération `FOR UPDATE`, dans ce même workspace ;
5. réception `FOR UPDATE` ;
6. tentative courante `FOR UPDATE` ;
7. ressources CRM `FOR SHARE` : workspace, SavedSearch, SourceConfiguration,
   Pipeline, PipelineStage.

Réception, annulation et reprise conservent leur ordre job puis opération
(l'annulation ne verrouille que job puis tentative). Elles ne prennent pas
l'advisory lock d'identités. Le transport ne s'exécute jamais dans cette
transaction. Les mutations de configuration attendent les verrous SHARE ;
si une mutation gagne avant leur acquisition, la revalidation observe son état.

`RemoteCollectionPrecondition` est un type explicite dérivé de
`CollectionPrecondition`, réservé à l'orchestration interne. Il contient le
workspace, fingerprint, StartedAt du vrai job, JobId, numéro de tentative,
OperationId, ReceiptId, ExecutionId et TriggerTypeCode. Il ne fabrique aucun
`CollectionJobAttempt` ni lease.

`IngestionService` conserve sa transaction propre pour les appels historiques.
Pour cette précondition distante seulement, il exige la transaction déjà
ouverte, vérifie le rattachement durable et y participe sans la committer.
Il conserve son savepoint métier : les conflits d'identités annulent toutes
les écritures d'opportunités/provenance mais gardent l'exécution d'échec et
ses inputs historiques. Le processeur attache cette exécution au vrai job et à
la tentative en même temps que leur passage à l'état terminal.

Le même commit contient l'exécution, l'ingestion ou son historique d'échec,
`SourceExecutionId`, les états job/tentative, `FinishedAt`,
`IntegrationReceipt.ProcessedAt`, l'état de l'opération et `CompletedAt`.
`AttemptCount` ne change pas. Les timestamps terminaux utilisent un maximum
avec les dates persistées pour respecter les contraintes malgré une différence
entre les horloges application et PostgreSQL.

Une annulation de requête ou une exception technique inattendue n'est pas
transformée en échec métier distant : la transaction externe entière rollback.
Aucun retry automatique d'ingestion n'est ajouté.

## Revalidation et mapping

Avant ingestion, le processeur compare le workspace, connexion, type, opération,
message de commande, corrélation, requestId, version, limite du lot, état de la
tentative et SHA-256 du contenu persisté. Les contrats fermés de 9.4.1 sont
reparsés défensivement. La connexion logique courante doit correspondre à
l'intention ; désactiver le transport seul n'empêche pas de traiter une réception.

Les ressources actives et `CollectionFingerprint` sont vérifiés une seconde
fois après la réception, sous verrous conservés jusqu'au commit. Un workspace,
search, source, pipeline ou stage devenu invalide, un changement de source/type,
de critères ou de fingerprint interdisent toute ingestion.
Le déplacement du stage vers un autre pipeline est déjà interdit par la FK
historique du job. Le service d'ingestion recontrôle également le fingerprint
et ses préconditions habituelles.

Un workspace déjà archivé avant la requête est refusé par l'authentification
humaine. Une modification survenue après cette validation d'identité est aussi
détectée dans le traitement sous verrou.

| Candidat persisté | IngestionItemRequest |
| --- | --- |
| externalId | ExternalId |
| title | Title |
| sourceUrl | SourceUrl |
| location | Location |
| description | Description |

`CompanyName` reste null. `SavedSearchId` et `PipelineStageId` viennent
exclusivement du job. StartedAt et TriggerTypeCode viennent de la tentative
réelle du job ; aucun paramètre distant ne choisit une destination CRM.

## Succès, échec et annulation

- Succès : job/tentative/opération `succeeded`, réception traitée, exécution
  rattachée, lease absent, ErrorCode null.
- Outcome distant `failed` : aucune ingestion ni SourceExecution créée ;
  job/tentative/opération `failed`, code `RemoteCollectionFailed`, réception traitée.
- Contexte changé : aucune ingestion ; code `RemoteContextChanged`.
  Connexion logique changée : `RemoteConnectionMismatch`. Même clôture terminale.
- Contrat/hash persisté invalide : aucune ingestion ; `RemoteResultInvalid`,
  même clôture terminale, contenu original conservé pour diagnostic.
- Conflit d'identités métier : code existant tel que `AmbiguousIdentity`,
  rollback des business writes, SourceExecution d'échec et historique conservés,
  job/tentative/opération failed et réception traitée.
- Job déjà cancelled/failed : traitement refusé sans mutation. Une réception
  tardive peut rester non traitée comme trace ; aucun résultat ne rouvre ce job.
- Rattachement structurel incohérent : conflit sans modifier une réception ou
  une opération dont la cohérence n'a pas pu être établie. Diagnostic manuel,
  sans retry ni réparation implicite.

Les routes `/probes/.../process` restent réservées à `technical.probe`.
La réception d'un replay identique reste 202 même après traitement, sans effet
métier supplémentaire.

Un succès métier peut précéder la fin du transport. La reprise des tentatives
transport détachées couvre désormais aussi les jobs `succeeded` : elle marque
un transport interrompu `unknown`, sans modifier le succès métier. Un ACK tardif
ne fait que clôturer la tentative technique.

## Provenance, historique et nouvelle collecte

Le premier traitement des candidats synthétiques `remote-demo-001` et
`remote-demo-002` crée exactement deux opportunités et deux provenances avec
ExternalId. SourceExecution compte Found=2, Created=2, Updated=0, Ignored=0.

Une nouvelle collecte logique (nouveau job/opération/message, mêmes ExternalId)
retrouve ces mêmes opportunités. Elle crée une nouvelle SourceExecution :
Found=2, Created=0, Updated=2, Ignored=0. Les identifiants de provenance,
SourceConfigurationId, SavedSearchId d'origine, SourceExecutionId d'origine,
SourceLabel et FirstSeenAt sont conservés ; LastSeenAt avance.

Les inputs distants, leurs champs normalisés, payloads location/description,
OpportunityId/snapshots et liens de provenance passent par l'historique existant.
Les codes `CreatedNewOpportunity`, `MatchedConsistentIdentities`,
`RolledBackAfterFailure`, `AmbiguousIdentity`, etc. restent inchangés.
La comparaison des snapshots text/jsonb est sémantique, pas fondée sur l'ordre
des propriétés JSON.

Un replay du même message ou de l'action humaine ne constitue jamais une
nouvelle collecte et ne crée pas de seconde SourceExecution. Aucune Company,
aucun email, calendrier ou job d'automatisation externe n'est créé.

## Reprise et concurrence vérifiées

Les tests utilisent des connexions/scopes distincts et des barrières de commit :

- réception durable puis nouveau host avant traitement ;
- interruption après écritures métier ou avant commit : rollback complet,
  réception encore traitable et unique ingestion à la reprise explicite ;
- commit réussi mais réponse perdue : second appel en conflit, sans doublon ;
- après succès : état terminal lisible et réception absente des pending receipts ;
- process/process et replay/process : une seule ingestion ;
- process/cancel : les deux ordres de victoire sont couverts, tout ou rien ;
- process/config-change : les deux ordres sont couverts ; verrou PostgreSQL
  vérifié par `FOR UPDATE NOWAIT`, puis succès ou échec contrôlé selon le gagnant ;
- aucune écriture non commitée visible depuis une autre connexion ;
- précondition distante sans transaction ou avec mauvais rattachement refusée ;
- traitement avant ACK : ACK tardif et reprise technique après interruption.

## Migration et validation locale

Aucune migration nécessaire, aucun modèle EF changé. La dernière migration
reste `20261015104738_Phase941RemoteCollectionLifecycle`. Les contraintes
existantes permettent l'attachement et la clôture atomiques du résultat.
Aucune migration historique n'est modifiée.

Historique des contrôles du 15 octobre 2026 :

1. Premier build : erreur de compilation du nouveau processeur (`SourceExecution`
   au lieu de la propriété existante `IngestionDto.Execution`), corrigée.
   Les deux warnings CS8618 préexistants de `Settings.razor` étaient aussi visibles.
2. Build Release suivant : réussi, 0 erreur, 0 avertissement.
3. Premier ciblage dédié : **32/33 réussis**, 1 échec, 0 ignoré. Seule l'assertion
   comparant les snapshots en texte échouait à cause de la représentation jsonb ;
   correction en comparaison JSON sémantique, sans changement du comportement métier.
4. Ciblage dédié complété : **40/40 réussis**, 0 échec, 0 ignoré, 20 s.
5. Régressions ingestion/identités/Phases 6–7/RSS/intégrations/migrations :
   **658/658 réussis**, 0 échec, 0 ignoré, 14 min 15 s.
6. Suite globale finale, sans rebuild/restauration : **1 461/1 461 réussis**,
   0 échec, 0 ignoré, 28 min 13 s. Un seul passage global pour 9.4.2 ;
   aucun changement de code depuis le ciblage dédié final et les régressions.
7. EF `has-pending-model-changes --configuration Release --no-build` :
   aucun changement de modèle. Exécution Testing avec connexion de design
   factice sur le port 1 ; aucune connexion à Development. L'outil EF local
   10.0.10 signale sa différence de version avec le runtime 10.0.12.

Artefacts TRX locaux ignorés dans `TestResults/phase942/` :
`phase942-initial.trx`, `phase942-targeted.trx`, `phase942-regressions.trx`,
`phase942-full.trx`.
Toutes les données et credentials des tests sont synthétiques, avec PostgreSQL
18 jetable et transports simulés. Aucun service réel n'est réactivé.

Commandes de validation (les options TRX ne produisent que des artefacts locaux
ignorés) :

```powershell
dotnet build ProspectionCrm.slnx --configuration Release --no-restore
dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~RemoteCollectionResultProcessingTests' --logger 'trx;LogFileName=phase942-targeted.trx' --results-directory TestResults/phase942
dotnet test tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~Ingestion|FullyQualifiedName~PersistentSourceIdentity|FullyQualifiedName~Phase6|FullyQualifiedName~Rss|FullyQualifiedName~SourceCollection|FullyQualifiedName~Integration|FullyQualifiedName~RemoteCollection' --logger 'trx;LogFileName=phase942-regressions.trx' --results-directory TestResults/phase942
dotnet test ProspectionCrm.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=phase942-full.trx' --results-directory TestResults/phase942
```

## État Git et fichiers

Travail sur `main`, sans staging, commit ni push.
HEAD inchangé : `76967824b9aabf596e2e12cd8d82fbba72e6d9b0`.
`git diff --check` et le contrôle whitespace des nouveaux fichiers réussissent.
L'inventaire du rapport correspond exactement au working tree : **9 fichiers**,
soit **5 suivis modifiés et 4 nouveaux**, index vide. Aucune migration historique
modifiée, aucun artefact TRX/bin/obj ajouté ni donnée Development réelle incluse.
Le contrôle des changements ne révèle aucun secret réel, credential n8n, cookie,
hash opérateur réel, certificat privé, dump, log local ou profil navigateur.

Fichiers modifiés :

- `src/ProspectionCrm.Api/Controllers/SourceCollectionJobsController.cs`
- `src/ProspectionCrm.Api/Program.cs`
- `src/ProspectionCrm.Api/Services/Collection/CollectionFingerprint.cs`
- `src/ProspectionCrm.Api/Services/Collection/RemoteCollectionState.cs`
- `src/ProspectionCrm.Api/Services/IngestionService.cs`

Nouveaux fichiers :

- `docs/phase-9-4-step-2-validation.md`
- `src/ProspectionCrm.Api/Services/Collection/RemoteCollectionPrecondition.cs`
- `src/ProspectionCrm.Api/Services/Collection/RemoteCollectionResultProcessor.cs`
- `tests/ProspectionCrm.Api.Tests/RemoteCollectionResultProcessingTests.cs`

## Limites et préparation exacte de 9.4.3

Pas de traitement automatique, de retry automatique d'ingestion, de réparation
de rattachement incohérent, d'annulation externe ni d'interface Blazor ajoutés.
Le traitement humain explicite et les lectures existantes constituent cette tranche.

La validation réelle n8n reste à réaliser séparément :

1. Préparer un workflow de collecte renvoyant exactement deux candidats de test
   avec ExternalId stables `remote-demo-001` et `remote-demo-002`, titres stables,
   et uniquement les champs autorisés.
2. Employer le contrat 9.4.1 réel : le résultat associé à collection.request
   conserve `type=collection.request`, avec commandMessageId, requestId,
   outcome et items ; « collection.result » désigne ici son rôle de callback.
3. Préparer HTTPS/HMAC et la connexion logique autorisée, puis autoriser et
   effectuer séparément l'activation temporaire des services et du workflow.
4. Première collecte : recevoir durablement, déclencher le traitement humain,
   vérifier deux opportunités, provenance, historique et clôture atomique.
5. Seconde collecte logique avec les mêmes ExternalId : nouvelle SourceExecution,
   deux redécouvertes, aucun doublon, LastSeenAt mis à jour.
6. Rejouer le callback identique : 202 sans nouvelle exécution ; répéter
   process-result : 409 sans seconde ingestion.
7. Changer une configuration après réception et avant traitement : refus
   contrôlé, aucune ingestion, clôture failed et trace durable.
8. Redémarrer après réception durable, puis traiter explicitement le résultat.
9. Désactiver le workflow et `Integrations`, arrêter les services temporaires,
   conserver uniquement les preuves expurgées. Ne pas versionner de secret,
   cookie, profil navigateur, dump, log local ou credential n8n.

Aucune de ces opérations réelles n'est effectuée en 9.4.2.
