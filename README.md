# ProspectionCrm

Application de suivi de prospection commerciale avec une API ASP.NET Core et un front Blazor WebAssembly. Les services CRM du front utilisent actuellement des données en mémoire.

## Prérequis

- SDK .NET 10 installé (`dotnet --version`).

## Compiler

Depuis la racine du dépôt :

```sh
dotnet restore
dotnet build
```

La solution `ProspectionCrm.slnx` regroupe les deux projets sous `src`.

## Lancer l'application

Dans deux terminaux distincts, depuis la racine :

```sh
dotnet run --project src/ProspectionCrm.Api --launch-profile http
```

API : http://localhost:5016 (document OpenAPI : `/openapi/v1.json` en développement). Aucun endpoint métier n'est encore exposé.

```sh
dotnet run --project src/ProspectionCrm.Blazor --launch-profile http
```

Front Blazor : http://localhost:5054.

## PostgreSQL et EF Core

Prérequis supplémentaires : Docker avec Docker Compose et son moteur démarré.
Le service local utilise PostgreSQL 18, accessible uniquement sur `127.0.0.1:5432`, avec un volume persistant.

1. Copier `.env.example` vers `.env` à la racine (PowerShell : `Copy-Item .env.example .env`, ou shell Unix : `cp .env.example .env`). Remplacer `change-me` par un mot de passe local. `.env` est ignoré par Git.
2. Vérifier la configuration et démarrer PostgreSQL :

   ```sh
   docker compose config --quiet
   docker compose up -d
   docker compose ps
   ```

3. Configurer la connexion de l'API avec le même mot de passe que dans `.env` :

   ```sh
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=prospectioncrm;Username=prospectioncrm;Password=<PASSWORD>" --project src/ProspectionCrm.Api
   ```

   Remplacer `<PASSWORD>` par la valeur locale. Le fichier `.env` est lu par Docker Compose, pas par ASP.NET Core. User Secrets est chargé en environnement Development ; ailleurs, utiliser par exemple la variable `ConnectionStrings__DefaultConnection`. Aucun mot de passe n'est stocké dans les fichiers de configuration versionnés.

4. Appliquer la migration existante `InitialCrmModel` :

   Dans la Package Manager Console de Visual Studio :

   ```powershell
   Update-Database -Project ProspectionCrm.Api -StartupProject ProspectionCrm.Api -Args '--environment Development'
   ```

   Ou avec la CLI, installer `dotnet-ef` (utiliser `dotnet tool update` à la place de `install` s'il est déjà installé) :

   ```sh
   dotnet tool install --global dotnet-ef --version 10.0.12
   dotnet ef database update --project src/ProspectionCrm.Api --startup-project src/ProspectionCrm.Api -- --environment Development
   ```

   La migration est déjà générée dans `src/ProspectionCrm.Api/Data/Migrations` ; il ne faut pas la recréer. Commandes utilisées pour sa génération, à titre de référence :

   ```powershell
   Add-Migration InitialCrmModel -Project ProspectionCrm.Api -StartupProject ProspectionCrm.Api -OutputDir Data\Migrations -Args '--environment Development'
   ```

   Équivalent CLI :

   ```sh
   dotnet ef migrations add InitialCrmModel --project src/ProspectionCrm.Api --startup-project src/ProspectionCrm.Api --output-dir Data/Migrations -- --environment Development
   ```

5. Lancer l'API :

   ```sh
   dotnet run --project src/ProspectionCrm.Api --launch-profile http
   ```

Les migrations sont appliquées explicitement, jamais au démarrage. `dotnet restore` et `dotnet build` ne nécessitent ni serveur PostgreSQL ni chaîne de connexion.

Le modèle comprend uniquement `Companies`, `Contacts`, `Opportunities` et `CrmTasks` (EF ajoute sa table technique d'historique des migrations). Les identifiants sont des `Guid`. Les dates sont des `DateTimeOffset` en UTC, stockées en `timestamp with time zone` ; fournir un décalage zéro pour les dates affectées explicitement. `CreatedAt` est initialisé avec `DateTimeOffset.UtcNow` ; `UpdatedAt` et `CompletedAt` ne sont pas renseignés automatiquement.

Les liens vers Company et Contact sont facultatifs : leur suppression conserve les objets liés et met leur clé étrangère à null. La suppression d'une Opportunity supprime ses CrmTasks en cascade ; les tâches générales sans Opportunity restent indépendantes. Tous les index métier sont non uniques. Le front continue à utiliser ses données en mémoire.

Pour arrêter PostgreSQL sans supprimer les données :

```sh
docker compose down
```

Les variables `POSTGRES_*` initialisent un volume vide : modifier `.env` ne change pas les identifiants d'une base déjà initialisée.

## Tests et CI

```sh
dotnet test
```

Aucun projet de tests n'est présent actuellement.

Le workflow GitHub Actions `.github/workflows/build.yml` restaure les dépendances et compile la solution en Release lors des push sur `main` et des pull requests vers `main`.
