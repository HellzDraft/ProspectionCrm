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

API : http://localhost:5016 (exemple : `/weatherforecast`).

```sh
dotnet run --project src/ProspectionCrm.Blazor --launch-profile http
```

Front Blazor : http://localhost:5054.

## Tests et CI

```sh
dotnet test
```

Aucun projet de tests n'est présent actuellement.

Le workflow GitHub Actions `.github/workflows/build.yml` restaure les dépendances et compile la solution en Release lors des push sur `main` et des pull requests vers `main`.
