# ProspectionCrm

CRM local de suivi de prospection : entreprises, contacts, opportunités, pipelines,
actions et autres ressources métier exposées par une API ASP.NET Core.
Le frontend Blazor WebAssembly consomme déjà l'API pour ces principaux écrans ;
certaines fonctionnalités restent des préparations ou des démonstrations locales.

## Architecture actuelle

- .NET 10, ASP.NET Core et Blazor WebAssembly, exécutés hors Docker.
- Entity Framework Core avec Npgsql et PostgreSQL 18.
- Docker Compose pour PostgreSQL ; n8n 2.40.7 facultatif via le profil `integrations`.
- Serilog comme provider des abstractions .NET `ILogger<T>`, en console et fichiers locaux.
- Stockage filesystem derrière `IFileStorage` / `LocalFileStorage`, sans raccordement
  upload/download métier pour le moment.
- Tests xUnit dans `tests/ProspectionCrm.Api.Tests` : stockage local, bootstrap,
  pipelines/étapes, concurrence, clonage, import/export et reconstruction PostgreSQL.

La solution `ProspectionCrm.slnx` regroupe les deux applications et le projet de tests.
Le schéma comprend notamment workspaces, prospection, candidatures, profils,
documents et historiques métier. Le moteur d’automatisation interne est décrit
ci-dessous. Les entités d’IA restent préparatoires, sans provider externe intégré.

La Phase 8.2 ajoute une [file persistante d’automatisation métier](docs/automation-jobs-v1.md)
avec déduplication PostgreSQL, claim atomique, leases et API d’administration.
Elle conserve les événements même lorsque l’automatisation est désactivée ;
aucune AutomationRule n’est consommée automatiquement et aucun effet externe n’est déclenché.

La Phase 8.3 ajoute le [dispatch et l’évaluation déterministe V1](docs/automation-evaluation-v1.md) :
événements `manual`, jobs ciblés et preview de la règle courante avec la policy 8.1.
L’action `create-crm-task` produit seulement un plan typé ; aucun CrmTask ni historique
d’exécution n’est créé. Aucun worker d’automatisation n’est démarré.

La Phase 8.4 ajoute le [worker d’automatisation métier](docs/automation-worker-v1.md),
désactivé par défaut : seule la décision `automatic` peut créer un `CrmTask` local.
Le runtime conserve les tentatives, protège l’idempotence par job dans PostgreSQL,
applique quotas et circuit breaker, et réconcilie les leases après crash.
Les modes `manual` et `assist` restent sans effet métier ni approbation persistante.

## Installation locale

### 1. Prérequis et clone

- Git et SDK .NET 10 (`dotnet --version`).
- Docker avec Docker Compose et moteur démarré (`docker compose version`).
- Outil EF Core CLI 10.0.12 pour appliquer les migrations.

Dans PowerShell :

```powershell
git clone https://github.com/HellzDraft/ProspectionCrm.git
Set-Location ProspectionCrm
dotnet tool install --global dotnet-ef --version 10.0.12
```

Si `dotnet-ef` est déjà installé, utiliser :

```powershell
dotnet tool update --global dotnet-ef --version 10.0.12
```

Exécuter les commandes suivantes depuis la racine du dépôt.

### 2. Configuration locale et secrets

Sur un nouveau clone, créer `.env` :

```powershell
Copy-Item .env.example .env
```

Ne pas écraser un `.env` existant. Remplacer `change-me` par un mot de passe local
dans `POSTGRES_PASSWORD`. Les valeurs par défaut de `POSTGRES_DB` et `POSTGRES_USER`
sont `prospectioncrm`.

Configurer la connexion de l'API dans .NET User Secrets avec les mêmes identifiants :

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=prospectioncrm;Username=prospectioncrm;Password=<MOT_DE_PASSE_LOCAL>" --project src/ProspectionCrm.Api
```

Remplacer le placeholder avant exécution. Si le mot de passe contient des caractères
spéciaux, respecter l'échappement PowerShell et la syntaxe des chaînes de connexion.

- `.env` est réservé à Docker Compose et ignoré par Git ; ASP.NET Core ne le lit pas.
- L'API charge User Secrets en environnement `Development`.
- `appsettings.json` contient la configuration commune non sensible ;
  `appsettings.Development.json` reste versionné et contient actuellement `{}`.
- Une configuration sensible pourra être injectée par variables d'environnement,
  notamment `ConnectionStrings__DefaultConnection`, sans infrastructure de production ici.
- Aucun secret réel ne doit être versionné, y compris dans `.env.example`.

Changer `POSTGRES_*` dans `.env` ne change pas les identifiants d'un volume déjà initialisé.

### 3. PostgreSQL et migrations

```powershell
docker compose config --quiet
docker compose up -d
docker compose ps
```

Attendre que PostgreSQL soit `healthy`. Il écoute uniquement sur `127.0.0.1:5432`
et conserve ses données dans le volume Compose `postgres_data`.

Appliquer explicitement toutes les migrations versionnées, dans l'ordre :
`20260925101154_InitialCrmSchema`,
`20260929150147_Phase42PipelineLifecycleAndDefault`,
`20261006091016_Phase621IngestionHistoryFoundation`,
`20261006101923_Phase622PersistentSourceIdentities`,
`20261007064337_Phase71PersistentCollectionJobs`,
`20261007074738_Phase72CollectionWorkerLeases`,
`20261007083908_Phase73CollectionRetries`,
`20261007094120_Phase74CollectionScheduling`,
`20261007124044_Phase81AutomationRuntimeSettings`,
`20261008083956_Phase82AutomationJobs`, puis
`20261008124357_Phase84AutomationExecutionRuntime` :

```powershell
dotnet ef database update --project src/ProspectionCrm.Api --startup-project src/ProspectionCrm.Api -- --environment Development
```

Cette commande restaure/compile le projet si nécessaire. Ne pas créer une nouvelle
migration pour installer le projet. Les migrations ne sont **jamais appliquées
automatiquement au démarrage** de l'API.

**Base vierge :** les migrations créent le schéma, sans fixtures ni workspace initial.
L'API et OpenAPI démarrent, mais les opérations métier de la V1 attendent exactement
un workspace actif. Après démarrage de l'API, appeler explicitement le bootstrap V1
décrit ci-dessous. Il ne reconstitue aucune donnée de validation existante.
Ne pas réinitialiser une base de développement pour obtenir ces données.

### 4. Compiler et tester

```powershell
dotnet restore ProspectionCrm.slnx
dotnet build ProspectionCrm.slnx --configuration Release
dotnet test ProspectionCrm.slnx --configuration Release
```

Les tests de `LocalFileStorage` vérifient lecture/écriture, suppression, collisions,
annulation et protection des chemins. Ils utilisent des dossiers temporaires nettoyés,
sans PostgreSQL, Docker, n8n ni accès au véritable `data/files`.
Ils ne constituent pas une couverture complète du métier.

Les tests métier des Phases 4 à 8 nécessitent **Docker démarré avec des conteneurs Linux**.
Testcontainers crée un PostgreSQL 18 jetable par test, sur un port hôte aléatoire,
et y applique les migrations réelles. Le premier lancement peut télécharger
`postgres:18` et l'image de nettoyage Testcontainers. Aucun conteneur, volume,
`.env` ou User Secret de développement n'est utilisé ou supprimé.
Sans Docker, ces tests échouent ; ils ne sont pas ignorés silencieusement.
Pour exécuter uniquement les tests de stockage sans Docker :

```powershell
dotnet test ProspectionCrm.slnx --configuration Release --filter FullyQualifiedName~LocalFileStorageTests
```

Restore et build ne nécessitent pas de serveur PostgreSQL.
GitHub Actions (`.github/workflows/build.yml`) exécute désormais restore, build Release
et **toute la suite de tests**, sur chaque push sur main et pull request vers main.
Le runner Linux GitHub hébergé utilise son Docker local pour les PostgreSQL 18 isolés
de Testcontainers. `docker info` doit réussir ; aucun test Docker n'est ignoré en cas
d'indisponibilité. Aucune base de service partagée, base de développement, `.env` ou
User Secret n'est requis. Le job dispose de `contents: read` et d'un timeout de 45 minutes.

Le test `Phase6ReconstructionTests` repart d'un PostgreSQL totalement vierge, applique
toutes les migrations, puis reconstruit par HTTP bootstrap, quatre pipelines/28 étapes,
source, recherche, ingestion, rejeu, fallback durable, conflit et lecture historique.
Il vérifie aussi archives, suppression physique, snapshots conservés, isolation Workspace
et absence de mutation par GET. La Phase 6.2.6 consolide ainsi la Phase 6.2 sans changer
les contrats métier. La migration finale est `20261008124357_Phase84AutomationExecutionRuntime`,
sans migration en attente ni divergence du modèle EF.

Commande locale équivalente à la CI (Docker doit être démarré) :

```powershell
docker info
dotnet restore ProspectionCrm.slnx
dotnet build ProspectionCrm.slnx --configuration Release --no-restore
dotnet test ProspectionCrm.slnx --configuration Release --no-build --logger "trx;LogFileName=ProspectionCrm.Tests.trx" --results-directory TestResults
```

Les résultats TRX sont déposés dans `TestResults`. Dans GitHub Actions, l'artifact
`test-results` est conservé 14 jours et déposé même si les tests échouent, dès lors
que des fichiers ont été produits. Ouvrir le run puis télécharger cet artifact pour
consulter le détail des résultats ; le succès du build seul ne valide plus la CI.

### 5. Démarrer API et frontend

Dans deux terminaux, depuis la racine :

```powershell
dotnet run --project src/ProspectionCrm.Api --configuration Release --no-build --launch-profile http
```

```powershell
dotnet run --project src/ProspectionCrm.Blazor --configuration Release --no-build --launch-profile http
```

| Service | URL locale |
| --- | --- |
| API et endpoints métier | <http://localhost:5016/api/companies> (exemple, nécessite un workspace actif) |
| Document OpenAPI JSON, en Development | <http://localhost:5016/openapi/v1.json> |
| Frontend Blazor | <http://localhost:5054> |
| n8n, si démarré | <http://localhost:5678> |

Le frontend configure `ApiBaseUrl` sur `http://localhost:5016/` et l'API autorise
les origines locales Blazor en Development. Le profil HTTP peut afficher un
avertissement indiquant qu'aucun port HTTPS de redirection n'est déterminé.
Arrêter les applications avec `Ctrl+C`.

### 6. Bootstrap métier V1 explicite

Sur la base migrée, avec l'API démarrée :

```powershell
Invoke-RestMethod -Method Post -Uri 'http://localhost:5016/api/setup/bootstrap'
```

`POST /api/setup/bootstrap` ne prend aucun paramètre et ne nécessite aucun corps.
Il n'est jamais appelé automatiquement au démarrage. Cette route suit l'API locale
V1 actuelle, sans authentification ; le propriétaire créé n'est pas un compte de connexion.

- **201 Created** si `UserAccounts` et `Workspaces` sont toutes deux vides :
  création d'un propriétaire et d'un workspace actif lié à ce propriétaire.
- **200 OK** s'il existe exactement un utilisateur et exactement un workspace,
  actif et lié à cet utilisateur : retour de la paire existante sans modification,
  même si ses noms, son email ou son fuseau diffèrent des valeurs initiales.
- **409 Conflict**, au format `ProblemDetails`, pour tout autre état :
  état partiel, plusieurs utilisateurs/workspaces, workspace archivé ou lien
  propriétaire incohérent. Aucune réparation ni suppression automatique.
  Le titre est `Incompatible V1 setup state` et `detail` explique le refus.

Les réponses 200/201 ont les mêmes six champs JSON :

```json
{
  "ownerUserId": "<GUID généré>",
  "ownerEmail": "owner@prospectioncrm.invalid",
  "ownerDisplayName": "Propriétaire local V1",
  "workspaceId": "<GUID généré>",
  "workspaceName": "Workspace local V1",
  "timeZoneId": "Europe/Paris"
}
```

Ces valeurs initiales sont centralisées dans `BootstrapDefaults`. Les identifiants
sont des GUID générés par les entités et les dates de création sont en UTC.
Le bootstrap ne crée ni Pipeline, ni PipelineStage, ni Opportunity, ni fixture.
La sélection courante conserve sa règle d'exactement un workspace actif ;
le bootstrap est plus strict et refuse aussi un workspace archivé supplémentaire.

Une transaction PostgreSQL `ReadCommitted` englobe lecture, décision et création.
Un unique `LOCK TABLE "UserAccounts", "Workspaces" IN SHARE ROW EXCLUSIVE MODE`
précède les lectures : les appels concurrents, même sur plusieurs instances API,
attendent le commit précédent puis relisent l'état validé. Ce verrou bloque aussi
les écritures concurrentes sur ces deux tables, sans bloquer les lectures ordinaires.
Il est libéré au commit ou au rollback, y compris en cas d'erreur ou d'annulation.
Il reste local au bootstrap et ne modifie ni le modèle ni les migrations.

### 7. Pipelines configurables — périmètre Phase 4

L'API propose les types `employment`, `freelance`, `business`, `custom`, les étapes
configurables, leur réordonnancement, la visibilité, l'archivage/restauration,
le pipeline par défaut, le clonage et l'import/export JSON V1. Les contrats et
limites sont décrits dans [API Pipeline V1](docs/pipelines-v1.md). Ces capacités
API ne signifient pas que chaque opération possède déjà son écran Blazor.

Le test `Phase4ReconstructionTests` vérifie le parcours complet sur un PostgreSQL
vierge et isolé, des migrations au bootstrap explicite puis aux opérations métier.
Il contrôle aussi l'absence de divergence modèle/snapshot EF. Pour le rejouer seul :

```powershell
dotnet test ProspectionCrm.slnx --configuration Release --filter FullyQualifiedName~Phase4ReconstructionTests
```

### 8. Initialisation métier explicite — Phase 5.5, catalogue initial complet

Après le bootstrap, appeler séparément :

```powershell
Invoke-RestMethod -Method Post -Uri 'http://localhost:5016/api/setup/initial-pipelines'
```

Cet endpoint sans corps initialise, dans cet ordre, « Emploi .NET »,
« Freelance / Malt », « Emploi Jeu Vidéo » et « Business Jeu Vidéo », chacun avec sept étapes (28 au total).
Lors de la toute première initialisation, Emploi .NET devient
le défaut uniquement si aucun défaut n'existe. La réponse `InitialPipelinesDto`
contient `pipelines` (les quatre configurations courantes, archives comprises) et
`createdPipelineIds` (les IDs créés par cet appel). Le statut est 201 si au moins
un pipeline a été créé, sinon 200 ; aucun `Location` vers un pipeline arbitraire.
Ce DTO remplace le `PipelineDto` unique de Phase 5.2.

Sur un workspace déjà initialisé en Phase 5.4, seul Business Jeu Vidéo est ajouté :
les trois pipelines existants, leurs modifications/archives et le choix de défaut
restent inchangés, y compris un défaut retiré. Un upgrade direct depuis Phase 5.2
ajoute Freelance / Malt, Emploi Jeu Vidéo et Business Jeu Vidéo sans toucher à Emploi .NET ni au défaut.
Depuis Phase 5.3, seuls Emploi Jeu Vidéo et Business Jeu Vidéo sont créés.
Les appels suivants ne réinitialisent aucune donnée utilisateur.
Un homonyme non reconnu renvoie 409 sans mutation partielle ; le renommer explicitement
permet l'initialisation, sans adoption automatique. L'appel est atomique pour les
quatre templates. Identités, conflits et transaction sont détaillés dans
[API Pipeline V1](docs/pipelines-v1.md).

La clé permanente du quatrième template est `business-game-dev`. Pour le workspace
`11111111-2222-3333-4444-555555555555`, son UUIDv8 est
`1a4f8704-0699-8f43-9177-c7f0e3d1f036`.

Le bootstrap technique crée toujours uniquement Owner + Workspace. Les migrations
et le démarrage de l'API ne créent aucun pipeline. Cette livraison ajoute l'appel
API explicite, sans appel automatique depuis Blazor. La Phase 5 possède désormais
son catalogue initial complet, entièrement configurable après création. Cette étape
d'initialisation ne crée aucune SavedSearch ou SourceConfiguration et ne déclenche
ni collecte, ni scoring, ni automatisation.

### 9. Ingestion manuelle et historique — Phases 6.1 à 6.2.4

`POST /api/saved-searches/{savedSearchId}/ingestions` reçoit une étape cible explicite
et un lot de 1 à 100 résultats fournis par le client. Il déduplique dans le workspace
entier, archives comprises, et enregistre Opportunity, provenance et SourceExecution
sans modifier les données utilisateur des opportunités retrouvées. Une erreur annule
les écritures métier du lot tout en conservant l'exécution échouée lorsque la base
reste disponible. Cette route n'effectue aucun accès réseau et ne crée aucune Company.

La Phase 6.2.1 ajoute la migration `20261006091016_Phase621IngestionHistoryFoundation` :
chaque nouvelle exécution conserve ses entrées dans `SourceExecutionItem`, ses liens
de provenance dans `SourceExecutionItemSource` et un snapshot versionné du contexte,
sans recopier ConfigurationJson. `HistoryVersion = 1` distingue ces exécutions du
legacy sans historique détaillé (`HistoryAvailable = false`).
Les compteurs rejected, rolled-back, not-processed et cancelled distinguent l'élément
fautif, les décisions annulées, les entrées non traitées et l'annulation en cours.
La Phase 6.2.4 expose le contexte et les observations historiques en lecture seule,
paginée et isolée par workspace. La Phase 6.2.2 persiste WorkspaceId et NormalizedSourceUrl
sur OpportunitySource, impose les FK du même workspace et les unicités URL par workspace
et ExternalId par configuration. Elle ajoute le fallback historique et dans le lot,
ainsi que le verrou partagé avec les CRUD OpportunitySource, Opportunity et Company.
Les provenances observées deviennent immuables par le CRUD (409 sur modification
ou suppression), et aucune nouvelle provenance vide n'est acceptée.
La migration Phase622PersistentSourceIdentities préserve le legacy null/null mais
refuse toute incohérence, URL invalide ou collision à résoudre avant l'upgrade.
Les écritures SQL externes restent responsables de fournir la véritable clé normalisée.

Les quatre routes de lecture sont :

- `GET /api/source-executions/{id}/history` : contexte JSON enregistré et exécution ;
- `GET /api/source-executions/{id}/items` : éléments par ItemIndex croissant ;
- `GET /api/opportunities/{opportunityId}/observations` : observations par date décroissante ;
- `GET /api/opportunities/{opportunityId}/sources/{sourceId}/observations` : observations distinctes d'une provenance.

Les pages utilisent `offset=0`, `limit=50` (maximum 200), des filtres exacts d'issue et
de décision, et pour les observations des bornes de date UTC inclusives. La route
Opportunity accepte aussi configuration/recherche ; la route provenance accepte le rôle.
Les JSON sont des objets, les IDs vivants restent distincts des IDs snapshot. Une
exécution legacy reste accessible en 200 avec `legacy-execution` et une page vide.
Les archives sont consultables ; après suppression physique, l'historique reste
accessible depuis l'exécution. Aucune route de modification d'observation ni migration
n'est ajoutée. La dernière migration de la Phase 6 reste `20261006101923_Phase622PersistentSourceIdentities`.

Le contrat, la normalisation conservative, les compteurs et les limites de provenance
sont décrits dans [Ingestion manuelle V1](docs/manual-ingestion-v1.md).

## Collecte RSS/Atom manuelle — Phase 6.3

`POST /api/saved-searches/{savedSearchId}/collect` reçoit seulement
`{"pipelineStageId":"<guid>"}`. Une recherche active de source `rss` peut récupérer
un flux public RSS 2.0 ou Atom 1.0 via HTTPS, puis réutiliser l'ingestion et sa
déduplication existantes. La réponse 201 contient `ingestion` et `summary`.

`CriteriaJson` accepte uniquement `maxItems` (1–100, défaut 100) et
`defaultCompanyName` facultatif. SearchUrl ne doit contenir aucun secret ; aucune
authentification réseau n'est supportée. Le transport borne délai, taille et redirects,
et protège les connexions DNS contre SSRF/rebinding. Les échecs après tentative sont
historisés sans item ; les changements de configuration pendant le réseau sont rejetés
sous verrou avant ingestion. La Phase 6.3 n’ajoutait pas de Worker ; la Phase 7.2
réutilise cette orchestration pour les jobs asynchrones.

Voir [le contrat RSS/Atom V1](docs/rss-atom-collection-v1.md) pour le mapping,
les protections, les erreurs, les tests et les limites.

## Docker et n8n facultatif

```powershell
docker compose --profile integrations up -d
```

n8n est un outil d'intégration externe facultatif : le backend n'en dépend pas.
Il utilise SQLite et son propre volume `n8n_data`, sans identifiants ni connexion
configurée vers la base métier CRM. Aucun workflow ou provider n'est configuré.

Consulter [l'infrastructure locale](docs/local-infrastructure.md) pour l'état des
services, les arrêts/redémarrages sans perte de données, les volumes et les resets
explicitement destructifs. Ne pas utiliser de reset pour un arrêt normal.

## Logs et stockage local

Serilog écrit des événements JSON structurés en console et dans
`logs/prospectioncrm-YYYYMMDD.log`, avec rotation quotidienne, au plus 14 fichiers
et une limite d'âge de 14 jours. La rétention est appliquée lors de l'ouverture/rotation
des fichiers. Les événements incluent l'application et l'environnement ; le middleware
résume chaque requête HTTP. Le niveau général est `Information`, ASP.NET Core et EF Core
sont à `Warning`. Avec les commandes ci-dessus, les fichiers se trouvent sous
`src/ProspectionCrm.Api/logs/` et restent ignorés par Git.

`FileStorage:RootPath` vaut `data/files`, résolu par rapport au répertoire de contenu
de l'API, soit `src/ProspectionCrm.Api/data/files/` en développement. Sous Windows,
le dossier existant `Data` peut fournir la même racine, sans distinction de casse.
Ces fichiers sont exclus de Git et des éléments SDK par défaut ; les sources EF Core
restent versionnées. La racine est créée à la première sauvegarde.

`IFileStorage` expose sauvegarde, ouverture, existence et suppression à partir de clés
relatives, avec streams et annulation. `LocalFileStorage` refuse les sorties de racine
et les écrasements ; la suppression est idempotente. Cette infrastructure n'est pas
encore reliée à un endpoint ou formulaire d'upload/download.

## File persistante, Worker et planification — Phases 7.1 à 7.4

`POST /api/saved-searches/{savedSearchId}/collection-jobs` reçoit un
`pipelineStageId` non vide et renvoie **202 Accepted**, le job `queued` et sa
`Location`. La prévalidation CRM/adaptateur est partagée avec `/collect`, sans
réseau, ingestion ni SourceExecution à l'enqueue. Un index PostgreSQL interdit
les jobs actifs identiques ; une demande concurrente retourne 409 avec
`CollectionJobAlreadyPending` et l'ID existant si disponible.

`GET /api/source-collection-jobs/{id}` et `GET /api/source-collection-jobs` permettent
lecture et pagination filtrée dans le Workspace courant. `POST
/api/source-collection-jobs/{id}/cancel` annule atomiquement un job encore queued ;
une répétition est idempotente. Les jobs ne sont jamais supprimés par cette API.

La migration `20261007064337_Phase71PersistentCollectionJobs` crée une file vide.
La migration `20261007074738_Phase72CollectionWorkerLeases` ajoute le token et
l’expiration du lease. Le `BackgroundService` prend un job disponible par claim
PostgreSQL atomique (`FOR UPDATE SKIP LOCKED`, ordre `AvailableAt`, `EnqueuedAt`,
`Id` croissants). Chaque traitement utilise son propre scope DI, relit et revalide
la configuration du Workspace enregistré, puis appelle l’orchestration RSS/ingestion
existante. Le job conserve son lien vers l’exécution et termine en `succeeded` ou `failed`.

Le Worker est **désactivé par défaut**, y compris en Development. Pour l’activer
après application explicite des migrations, définir avant de démarrer l’API :

```powershell
$env:SourceCollectionWorker__Enabled = "true"
$env:SourceCollectionWorker__IdleDelaySeconds = "5"
$env:SourceCollectionWorker__LeaseDurationSeconds = "300"
```

La Phase 7.3 ajoute `20261007083908_Phase73CollectionRetries`, avec une table légère
conservant toutes les tentatives et leurs liens vers SourceExecution. Un échec
explicitement transitoire remet le même job queued, avec une disponibilité future.
Le job garde son origine manual et aucune exécution précédente n’est supprimée.

Options supplémentaires : MaxAttempts=3 (1–10), InitialRetryDelaySeconds=60 et
MaxRetryDelaySeconds=900. Le backoff est `min(maximum, initial × 2^(tentative−1))` :
60 puis 120 secondes par défaut, avant échec terminal à la troisième tentative.
Il est persisté dans AvailableAt et survit au redémarrage. Un job en backoff reste
annulable. Les erreurs de configuration, TLS/sécurité et les erreurs inconnues
sont terminales ; les statuts HTTP distants transitoires sont classifiés explicitement.

Avant chaque claim, le Worker réconcilie un petit batch de leases expirées. Il
finalise les succès déjà committés et décide retry/échec d’après la cause durable.
Une session active protégée par un verrou PostgreSQL n’est pas reprise. Si cette
session est perdue, le token interdit à l’ancien propriétaire d’écrire ou de
finaliser après récupération. Les cas ambigus terminent failed pour diagnostic.
Une connexion dédiée par traitement conserve ce verrou, sans transaction pendant
le réseau. Arrêter les workers avant l’upgrade ; ne pas mélanger 7.2 et 7.3 actives.

Les options sont validées au démarrage. L’attente à vide reste de 1 à 300 secondes,
le lease de 30 à 3600 secondes, sans heartbeat. Le délai initial de retry va de
1 à 3600 secondes ; son plafond est au moins égal à l’initial et au plus 86400.
Le Worker attend quand aucun travail ne progresse, sans timer de backoff en mémoire.

### Planification quotidienne UTC — Phase 7.4

`PUT /api/saved-searches/{id}/schedule` configure une heure UTC `HH:mm` et une
étape cible explicite. Les DTO SavedSearch exposent `schedule` : activation,
heure, étape et `nextCollectionAt`. La prochaine échéance est persistée dans
`SourceCollectionSchedules`, jamais déduite d'un timer en mémoire.

Le scheduler .NET distinct crée uniquement des jobs `scheduled`. Création et
avancement de l'échéance sont atomiques sous verrou PostgreSQL. Au redémarrage,
au plus un rattrapage est produit ; un job identique queued/running coalesce
l'occurrence et la prochaine échéance avance quand même. Les retries gardent
le même job et son origine scheduled. Archiver/désactiver la recherche suspend
sa planification ; restaurer la recherche ne la réactive pas.

Le scheduler est désactivé par défaut : `SourceCollectionScheduler:Enabled=false`,
`PollIntervalSeconds=30`, `BatchSize=50`. Activer séparément le Worker pour traiter
les jobs. Migration `20261007094120_Phase74CollectionScheduling`, sans configuration
ni job rétroactif. Voir [Planification des collectes V1](docs/source-collection-scheduling-v1.md)
pour l'API, les validations, la concurrence, les options et les limites.

`Phase7ReconstructionTests` reconstruit sur PostgreSQL vierge le parcours HTTP
bootstrap → pipelines → source RSS → SavedSearch → planning, puis scheduler → job
scheduled → Worker → ingestion → Opportunity/OpportunitySource → historique →
succeeded. Le transport RSS est déterministe ; les vrais services, le parser et
les migrations sont utilisés. `Phase7ApiErrorTests` vérifie aussi les réponses
d'erreur sans diagnostics sensibles, y compris en Development.

Toujours absents : cron libre, fuseaux horaires utilisateur, calendrier ouvré,
n8n opérationnel, déclencheurs événementiels automatiques, Blazor, IA/scoring/email.

Voir [le contrat des jobs V1](docs/source-collection-jobs-v1.md). Les contrats
synchrones `/collect`, `/ingestions`, historique et OpportunitySource restent inchangés.

## Phase 8.1 — Paramètres et garde-fous d’automatisation

Chaque Workspace possède une configuration persistante, désactivée par défaut en
mode `manual`, avec limites 10/minute, 100/jour et 3 échecs consécutifs.
`GET/PUT /api/automation-settings` expose les paramètres et les décisions de sécurité
pour `general`, `email` (au maximum assist) et `application` (toujours manual).
Le kill switch bloque toute autorisation lorsqu’il est désactivé.
Aucune règle n’est encore exécutée : ni Worker d’automatisation, ni effet externe,
ni application des quotas à des exécutions. La collecte Phase 7 reste inchangée.
Voir [les paramètres d’automatisation V1](docs/automation-runtime-settings-v1.md)
pour la matrice, l’API, la migration, le bootstrap et les limites de cette phase.
