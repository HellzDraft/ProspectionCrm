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
documents et historiques métier. La présence d'entités d'automatisation ou d'IA
ne signifie pas qu'un moteur ou un provider externe est intégré.

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
`20260925101154_InitialCrmSchema`, puis
`20260929150147_Phase42PipelineLifecycleAndDefault` :

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

Les tests métier de Phase 4 nécessitent **Docker démarré avec des conteneurs Linux**.
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
La CI actuelle restaure et compile en Release ; elle n'exécute pas encore les tests.

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

### 8. Initialisation métier explicite — Phase 5, Emploi .NET

Après le bootstrap, appeler séparément :

```powershell
Invoke-RestMethod -Method Post -Uri 'http://localhost:5016/api/setup/initial-pipelines'
```

Cet endpoint sans corps crée uniquement « Emploi .NET » et ses sept étapes, puis le
choisit comme défaut si aucun défaut n'existe. Il renvoie 201 avec le `PipelineDto`
et un `Location` vers son détail, ou 200 s'il a déjà été initialisé. Les répétitions
ne réinitialisent ni les modifications utilisateur ni un défaut retiré ensuite.
Un pipeline homonyme non reconnu renvoie 409 sans écriture ; le renommer explicitement
permet l'initialisation, sans adoption automatique. La stratégie d'identité stable,
les conflits et la transaction sont détaillés dans [API Pipeline V1](docs/pipelines-v1.md).

Le bootstrap technique crée toujours uniquement Owner + Workspace. Les migrations
et le démarrage de l'API ne créent aucun pipeline. Cette livraison ajoute l'appel
API explicite, sans appel automatique depuis Blazor. « Freelance/Malt », « Emploi Jeu
Vidéo » et « Business Jeu Vidéo » restent reportés. Aucun moteur de collecte,
scoring, IA, automatisation, SavedSearch ou SourceConfiguration n'est ajouté.

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
