# Worker d’automatisation métier — Phase 8.4

> Extension Phase 8.6 : [supervision et reset explicite du circuit](automation-supervision-v1.md).
> Le guard partage désormais ses calculs avec la supervision. Les marqueurs de
> reset excluent les anciens outcomes du circuit sans modifier les historiques,
> les quotas ou AvailableAt. Le worker reste hébergé dans l’API et désactivé par défaut.

> Extension Phase 8.5 : voir [demandes d’action et décisions humaines](automation-action-requests-v1.md).
> Les détails et le rapport 8.4 ci-dessous décrivent la version de départ. Pour les
> nouveaux traitements manual/assist, une demande persistante et un job
> awaiting-approval remplacent le skipped définitif. Les effets approuvés restent
> soumis au kill switch, mais sont exclus des quotas et du circuit automatiques.

## Portée et activation

Le consumer AutomationWorker est un BackgroundService hébergé dans l’API.
Il consomme les AutomationJob persistants et peut créer un CrmTask local.
Aucun exécutable Worker séparé n’est ajouté.

Le worker est **désactivé par défaut**, y compris en Development. Après application
explicite de la migration 20261008124357_Phase84AutomationExecutionRuntime, définir avant le démarrage :

~~~powershell
$env:AutomationWorker__Enabled = "true"
~~~

Ce paramètre technique démarre la boucle. Il ne remplace pas
AutomationRuntimeSettings.IsEnabled, le kill switch métier par workspace.
Une action nouvelle exige aussi une règle valide et la décision automatic de
la policy 8.1. Les migrations ne sont jamais exécutées au démarrage de l’API.

| Option AutomationWorker | Défaut | Bornes |
| --- | --- | --- |
| Enabled | false | booléen |
| IdleDelaySeconds | 5 | 1–300 |
| ErrorDelaySeconds | 10 | 1–300 |
| RecoveryIntervalSeconds | 30 | 1–300 |
| DeferredDelaySeconds | 60 | 1–86400 |
| WorkspaceBatchSize | 50 | 1–100 |
| MaxAttempts | 3 | 1–10 |
| InitialRetryDelaySeconds | 30 | 1–3600 |
| MaxRetryDelaySeconds | 120 | initial–86400 |

ValidateOnStart refuse les options invalides, même worker désactivé.
Les horloges et délais sont injectables ; les tests de boucle évitent les longues
attentes réelles. Le lease reste celui de la queue 8.2 : cinq minutes par défaut.

## Architecture et workspaces

AutomationWorkSource découvre les workspaces avec jobs pending disponibles
et, séparément, leases expirés ou historiques running abandonnés.
Chaque lecture est bornée et ordonnée par WorkspaceId. Deux curseurs de pagination
par clé, pour consommation et recovery, évitent de monopoliser les premiers IDs.

Le worker utilise IServiceScopeFactory, un scope court par opération/workspace et
un seul processor à la fois par instance. Aucun DbContext ne survit à une itération.
Les instances sont coordonnées par PostgreSQL. Le worker n’utilise jamais
ICurrentWorkspaceProvider : les IDs internes viennent des jobs persistants.
L’API conserve ses conventions de workspace courant.

À vide, le worker attend IdleDelay ; une erreur non absorbée attend ErrorDelay.
L’annulation interrompt requêtes et délais. Une erreur d’un processor ne termine
pas le BackgroundService. La recovery parcourt au plus WorkspaceBatchSize
workspaces par intervalle ; une grande file nécessite plusieurs intervalles
pour un tour complet.

## Processor, autorisation et transactions

IAutomationJobProcessor reçoit le claim et sa capacité LeaseOwner.
Le processor relit le job réel ; il ne fait pas confiance à un objet modifié
après claim. L’ownership exige workspace, ID, owner, numéro de claim et
expiration contrôlée avec l’horloge PostgreSQL.

L’évaluation initiale est hors transaction. Une première transaction courte :

1. prend un advisory lock dédié à l’automatisation du workspace ;
2. verrouille le job et vérifie son lease après toute attente ;
3. réconcilie les historiques abandonnés et les résultats déjà committés ;
4. relit règle/settings courants, applique les reports et le budget technique ;
5. persiste l’historique running d’une nouvelle tentative.

Le processor revalide ensuite règle et settings sous verrous partagés dans la
transaction courte de mutation. Le calcul pur est répété sous verrou pour fermer
la course avec une désactivation ou un changement de configuration. Aucun réseau
n’est utilisé. Le verrou dédié ne bloque pas l’ensemble du CRM.

La seconde transaction regroupe tâche et historique succeeded.
Elle revérifie le lease avant l’executor, après le verrou Opportunity et après
SaveChanges, avant commit. Une expiration fait rollback de l’effet et de sa
finalisation. Complete est ensuite appelé sur la queue hors de cette transaction.
La fenêtre de crash entre ces deux commits est explicitement supportée.

Une règle absente/inaccessible ou un job sans règle produit un rejet définitif :
job failed, AutomationProcessingRejected. Le schéma exige une règle obligatoire
pour l’historique ; aucune règle ni execution fictive n’est inventée.
Des settings absents pour une règle existante donnent une execution skipped,
automation-settings-missing, puis un job completed.

## Modes et reports

| Situation | Historique | Job | Effet |
| --- | --- | --- | --- |
| Kill switch désactivé | aucun au contrôle initial | pending, +DeferredDelay | aucun |
| manual | skipped, eligible-manual | completed | aucun |
| assist / approval-required | skipped, eligible-approval-required | completed | aucun |
| automatic valide | succeeded, task-created | completed | tâche CRM |
| règle disabled/archived, condition false, scope/configuration/contexte invalide | skipped, raison stable | completed | aucun |
| Opportunity absente/étrangère | skipped, opportunity-not-found | completed | aucun |
| Opportunity archivée | skipped, opportunity-archived | completed | aucun |
| quota ou circuit ouvert | aucun au contrôle initial | pending, échéance déterminée | aucun |

Si un quota ou le kill switch change entre création du running et revalidation
finale, cette tentative devient skipped avec IsDeferred=true. Ce diagnostic
exceptionnel n’est pas un checkpoint de completion : le job reste retraitable.
Les contrôles initiaux évitent d’accumuler des historiques sur un blocage durable.

Un succès ou skipped définitif déjà committé permet de terminer le job après
crash, même avec kill switch coupé : aucune nouvelle action n’est effectuée.

En 8.4, **manual et assist n’ont aucun workflow d’approbation persistant**.
Ces événements sont observés puis terminés sans mutation. La Phase 8.5 ajoutera
les demandes et décisions humaines ; aucun ValidationRequest n’est créé ici.

## Historique et schéma

Migration : 20261008124357_Phase84AutomationExecutionRuntime. Les migrations précédentes sont inchangées.
Les liens nullable préservent les historiques et tâches manuels existants.

AutomationExecution ajoute :

- AutomationJobId et AttemptNumber, uniques ensemble pour les lignes liées ;
- ActionTypeCode et ReasonCode, exposés par l’API ;
- IsAutomaticAttempt, distinguant les erreurs techniques automatiques ;
- IsDeferred, pour un report après début de tentative ;
- EffectApplied, preuve durable et unique par job d’un effet appliqué ;
- OutcomeSequence, ordre des succès appliqués et échecs automatiques définitifs
  du workspace, attribué sous verrou d’automatisation.

La FK composite WorkspaceId/AutomationJobId interdit un lien vers un job d’un
autre workspace. Les FK utilisent Restrict. Les contraintes PostgreSQL protègent
tentatives, états terminaux, reports et preuves d’effet. Un index sert aux quotas ;
l’index unique workspace/outcome sert aussi au circuit. Aucune table de compteurs.

TriggeredAt et StartedAt désignent le début réel de tentative, pas le dispatch.
FinishedAt est renseigné à la terminaison. Les dates applicatives utilisent
TimeProvider et UTC ; les leases utilisent l’horloge PostgreSQL.
OutcomeSequence rend l’ordre indépendant de timestamps égaux ou d’un ajustement
de l’horloge.

ContextJson contient seulement IDs job/tentative et codes trigger/action/catégorie/
raison. Les définitions inconnues ne sont pas copiées dans ActionTypeCode.
Aucun payload, configuration ou diagnostic d’exception n’est copié.

GET /api/automation-executions accepte le filtre optionnel automationJobId,
avec la même isolation de workspace. Les DTO exposent lien, tentative, action,
raison, EffectApplied et IsDeferred. Aucune API de mutation d’execution.

L’upgrade préserve les données 8.3. Down retire les métadonnées/index/FK 8.4, sans
supprimer tâches, jobs, règles ou historiques. Les métadonnées retirées ne sont
pas recréées par Up. Arrêter les workers avant migration/downgrade ; ne pas
mélanger des versions runtime incompatibles.

## Executor et idempotence

IAutomationActionExecutor a un seul executor V1 : CreateCrmTaskAutomationExecutor.
Il reçoit le plan typé CreateCrmTaskPlan et travaille dans la transaction du
processor, sans effet externe.

Il recherche d’abord une tâche portant AutomationJobId, puis verrouille et valide
l’Opportunity du workspace. Elle doit être active, comme pour le service manuel.
La création conserve Title et Description littéraux, IsCompleted=false,
CreatedAt UTC à la mutation et :

- DueInDays absent : DueAt=null ;
- DueInDays=N : DueAt=instant réel d’exécution UTC + N jours.

CrmTask.AutomationJobId est nullable, FK Restrict, avec **index unique partiel
PostgreSQL**. La preuve EffectApplied reste dans l’historique si la tâche est
supprimée : un replay ne la recrée pas et ne recompte pas l’effet.

Après crash entre commit métier et completion, recovery libère le lease ;
le nouveau claim voit le checkpoint succeeded et termine le job. Il ne crée
ni nouvelle tentative métier ni effet. Une tâche déjà liée sans historique
est adoptée une fois avec effect-already-applied ; le quota est compté de façon
conservatrice au moment de cette réconciliation.

Le service manuel des tâches ne crée pas d’ActivityEntry. La Phase 8.4 conserve
cette convention : AutomationExecution suffit pour la trace de l’automatisation.

## Recovery, retries et erreurs

AutomationJobRecovery appelle la recovery 8.2 par workspace puis marque les
anciennes executions running ne correspondant plus à un claim actif :
failed, lease-expired, FinishedAt renseigné. Ce travail est borné et idempotent.
Le processor fait aussi cette réconciliation au démarrage.

Une annulation laisse le running durable pour recovery. Un ancien owner ne peut
ni muter ni finaliser une tentative récupérée. Un effet committé est reconnu
avant de considérer une nouvelle exécution.

Seules les exceptions Npgsql explicitement transitoires, deadlocks et conflits
de sérialisation sont retryables. Les exceptions inconnues sont terminales.
Les validations/non-matchs sont skipped, jamais retryés.

Une erreur avant la création du running reçoit aussi une tentative failed
lorsque le stockage permet de la diagnostiquer. Elle consomme le même budget,
sans être classée automatique avant une décision d’éligibilité connue.
La voie de diagnostic respecte le kill switch et les checkpoints déjà committés.

MaxAttempts porte sur les tentatives techniques failed, abandons réconciliés
compris, **pas** sur AttemptCount de queue. Les reports temporaires peuvent
augmenter AttemptCount sans épuiser ce budget. Avec les valeurs par défaut :

- premier échec transitoire : release à +30 secondes ;
- deuxième : release à +120 secondes ;
- troisième : failed définitif.

Backoff = min(plafond, délai initial × 4^(nombre d’échecs−1)).
Après crash/abandon, la recovery conserve AvailableAt ; la reprise peut être
immédiate. Budget épuisé : le prochain claim finalise l’échec sans executor
ni nouvelle tentative métier fictive.

Historique failed et release/fail sont atomiques. Si la base ne permet pas ce
diagnostic, le lease reste à récupérer. Une erreur de completion ne réécrit
jamais un succès en failed. LastError reste dans le catalogue public 8.2.

ILogger utilise WorkspaceId, AutomationJobId, AutomationRuleId, AttemptCount,
AutomationExecutionId et reasonCode. Aucun objet exception, SQL, contexte ou
configuration complets n’est ajouté aux logs du runtime.

## Quotas et circuit breaker

Les quotas comptent seulement EffectApplied=true, une seule fois par job.
Échecs, skips, reports, previews, dispatchs et reprises d’un succès connu ne
consomment rien.

- Minute : fenêtre glissante de 60 secondes, borne basse exclue. Le report va
  jusqu’à la sortie de fenêtre du nombre nécessaire de succès antérieurs.
- Jour : 00:00:00 UTC inclus à minuit suivant exclu ; report au prochain minuit.

La journée ne dépend pas du fuseau machine ou workspace. Contrôle des quotas et
mutation partagent le verrou PostgreSQL propre au workspace ; ils sont répétés
dans la transaction finale, donc deux consumers ne dépassent pas la limite.

Le circuit parcourt OutcomeSequence : seul un effet appliqué ou un échec
automatique technique définitif participe. Skips et échecs encore retryables
ne changent pas la séquence. Un succès la remet à zéro ; MaxConsecutiveFailures
atteint reporte les nouveaux jobs automatiques à +DeferredDelay.
L’ordre retenu est celui des résultats finalisés durablement sous verrou.

Le circuit ne modifie pas IsEnabled et n’expire pas seul. En V1, augmenter
explicitement le seuil via les paramètres permet un nouvel essai si ce seuil
dépasse la séquence courante ; un succès réarme le circuit. Il n’existe pas
d’API de reset, de probe half-open ou de réarmement après atteinte du maximum
configurable. Cette limite d’administration est explicite.

## Validation et limites

Tests PostgreSQL : modes, non-matchs, mapping UTC, ownership, running visible
avant effet, rollback après perte du lease, contraintes uniques, reprise après
commit, suppression de tâche, retries, quotas minute/jour, concurrence,
circuit et isolation workspace. Tests d’hébergement : options, scopes,
recovery, délais/annulation. Migrations : base vierge, upgrade 8.3, Down/Up,
HasPendingModelChanges=false. API : historique filtré, isolation et
preview/dispatch sans mutation.

La suite antérieure reste nécessaire pour la collecte Phase 7 et les autres
contrats. Les résultats exacts sont indiqués dans le rapport de livraison.

Hors périmètre : projet Worker séparé, frontend Blazor, approbation persistante,
email, candidatures/propositions automatiques, calendrier, IA, n8n, webhook,
templating, scripting et autres actions que create-crm-task.

## Rapport de livraison du 8 octobre 2026

Départ vérifié : HellzDraft/ProspectionCrm, branche main,
HEAD 56943f6fed77f919d0d53387559d98cdf3cea5a7, working tree propre.
Le crash avait interrompu les tests avant toute écriture 8.4.

| Contrôle | Résultat |
| --- | --- |
| Build Release avant modification | réussi, 0 avertissement, 0 erreur |
| Référence complète reprise | 976 réussites, 0 échec, 0 ignoré, 26 min 25 s |
| Build Release après implémentation | réussi, 0 avertissement, 0 erreur |
| Tests ciblés runtime/worker/migrations | 76 réussites, dont 74 nouveaux tests 8.4 et 2 tests de migration 8.2 |
| Régression complète finale | 1 050 réussites, 0 échec, 0 ignoré, 31 min 12 s |
| Base vierge / upgrade 8.3 / Down-Up | validés sur PostgreSQL 18 jetable |
| Divergence du modèle EF | aucune ; HasPendingModelChanges=false |
| git diff --check | réussi |
| État Git final | 20 fichiers suivis modifiés, 18 nouveaux fichiers, aucun fichier staged ; HEAD inchangé |

Résultats détaillés : TestResults/phase84-baseline-resumed.trx,
TestResults/phase84-targeted-final.trx et TestResults/phase84-full-validation.trx.
TestResults est ignoré par Git. La première passe complète après implémentation
avait 1 048 réussites et deux assertions historiques de dernière migration
obsolètes ; elles ont été corrigées avant la passe complète verte ci-dessus.

Commandes de validation finale (le journal console est conservé dans le dossier
temporaire Windows sous prospection-phase84-full-validation.log) :

~~~powershell
dotnet build ProspectionCrm.slnx --configuration Release --no-restore
dotnet test ProspectionCrm.slnx --configuration Release --no-build --logger 'trx;LogFileName=phase84-full-validation.trx' --logger 'console;verbosity=normal' --results-directory TestResults -- xUnit.MaxParallelThreads=4
dotnet ef migrations has-pending-model-changes --project src/ProspectionCrm.Api --startup-project src/ProspectionCrm.Api --configuration Release --no-build
git diff --check
git status --short
~~~

Les 1 050 cas sont exécutés sans filtre. La limite de parallélisme concerne le
runner ; les scénarios de concurrence gardent leurs consumers concurrents.
EF CLI 10.0.10 signale sa version antérieure au runtime 10.0.12, mais le contrôle
termine avec succès. Aucune migration n’est en attente dans les bases de test
après application de la chaîne ; la base de développement n’a pas été migrée.

Les tests historiques qui vérifient la dernière migration sont actualisés.
Le test d’upgrade 8.1 insère son historique par SQL dans l’ancien schéma :
utiliser l’entité EF actuelle tenterait d’écrire les colonnes 8.4 avant leur
création. Les comparaisons conservent toutes les colonnes historiques et
excluent seulement les métadonnées nouvellement ajoutées.

### Inventaire des fichiers

38 fichiers concernés, sans staging, commit ni push.

| Groupe | Fichiers nouveaux |
| --- | --- |
| Documentation | docs/automation-worker-v1.md |
| Runtime, sous src/ProspectionCrm.Api/Services/Automation/ | AutomationWorker.cs ; AutomationWorkerOptions.cs ; AutomationWorkSource.cs ; AutomationJobProcessor.cs ; AutomationJobRecovery.cs ; AutomationRuntimeStore.cs ; CreateCrmTaskAutomationExecutor.cs |
| Migration, sous src/ProspectionCrm.Api/Data/Migrations/ | 20261008124357_Phase84AutomationExecutionRuntime.cs ; 20261008124357_Phase84AutomationExecutionRuntime.Designer.cs |
| Tests, sous tests/ProspectionCrm.Api.Tests/ | AutomationRuntimeFixture.cs ; AutomationRuntimeBehaviorTests.cs ; AutomationRuntimeResilienceTests.cs ; AutomationRuntimeRevalidationTests.cs ; AutomationRuntimeApiTests.cs ; AutomationRuntimeMigrationTests.cs ; AutomationWorkerTests.cs ; AutomationWorkerIntegrationTests.cs |

| Groupe | Fichiers modifiés |
| --- | --- |
| Documentation | README.md |
| Hébergement, sous src/ProspectionCrm.Api/ | Program.cs ; appsettings.json |
| Entités, sous src/ProspectionCrm.Api/Entities/ | AutomationExecution.cs ; CrmTask.cs |
| Configuration EF, sous src/ProspectionCrm.Api/Data/Configurations/ | AutomationExecutionConfiguration.cs ; AutomationJobConfiguration.cs ; CrmTaskConfiguration.cs |
| Snapshot, sous src/ProspectionCrm.Api/Data/Migrations/ | ProspectionCrmDbContextModelSnapshot.cs |
| Lecture API, sous src/ProspectionCrm.Api/ | Controllers/AutomationExecutionsController.cs ; Dtos/AutomationExecutions/AutomationExecutionDto.cs ; Services/AutomationExecutionService.cs ; Services/IAutomationExecutionService.cs |
| Régressions, sous tests/ProspectionCrm.Api.Tests/ | AutomationJobMigrationTests.cs ; AutomationEventDispatcherTests.cs ; IngestionHistoryReadTests.cs ; IngestionHistoryTests.cs ; SourceCollectionJobMigrationTests.cs ; Phase6ReconstructionTests.cs ; RssCollectionTests.cs |

Le code métier de collecte Phase 7, Blazor, les migrations historiques et les
secrets ne sont pas modifiés. Les validations de migration utilisent des bases jetables ;
aucune migration n’est appliquée implicitement à la base de travail.
