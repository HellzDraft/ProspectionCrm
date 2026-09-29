# API Pipeline — Phase 4.2

Toutes les routes utilisent `ICurrentWorkspaceProvider` : exactement un workspace
actif en V1. Un identifiant absent ou appartenant à un autre workspace renvoie 404.
Le bootstrap ne crée toujours aucun pipeline.

| Route | Résultat |
| --- | --- |
| `GET /api/pipelines` | 200, pipelines actifs du workspace courant, y compris les masqués |
| `GET /api/pipelines?includeArchived=true` | 200, inclut les pipelines et étapes archivés |
| `GET /api/pipelines/{id}` | 200 ou 404 ; un pipeline archivé reste consultable par son ID |
| `POST /api/pipelines` | 201 et en-tête Location vers le détail ; 400 si entrée invalide |
| `PUT /api/pipelines/{id}` | 204, 400 ou 404 |
| `POST /api/pipelines/{id}/archive` | 204 ou 404 |
| `POST /api/pipelines/{id}/restore` | 204 ou 404 |
| `POST /api/pipelines/{id}/set-default` | 204, 404, ou 409 si archivé |

Le détail retourne le pipeline même s'il est archivé, avec uniquement ses étapes
actives par défaut. `GET /api/pipelines/{id}?includeArchivedStages=true` inclut
aussi ses étapes archivées. Sur la liste, `includeArchived=true` continue de
contrôler l'inclusion des pipelines archivés et de leurs étapes archivées.
Liste et détail utilisent le même DTO et trient les étapes par `SortOrder`, puis ID.
Le DTO conserve ses champs existants et ajoute `IsVisible` et `IsDefault`.
Il n'existe aucun endpoint DELETE Pipeline, ni CRUD PipelineStage dans cette phase.

## Création et modification

Le contrat commun accepte uniquement `Name`, `Description`, `TypeCode`, `IsVisible`.
Le nom est trimé avant validation et stockage, obligatoire, de 1 à 200 caractères.
La description nullable accepte au plus 2000 caractères. Les codes autorisés sont
exactement `employment`, `freelance`, `business`, `custom` (sensibles à la casse).
Les noms identiques sont autorisés. Les erreurs de validation renvoient 400.

`IsVisible` vaut true si omis, y compris dans le PUT, qui est un remplacement des
champs modifiables. Un pipeline masqué reste actif et peut être choisi comme défaut.
La modification d'un pipeline archivé reste possible, sans le restaurer.
Les propriétés JSON supplémentaires suivent le comportement ASP.NET existant :
elles sont ignorées et ne permettent pas de modifier le workspace, le défaut,
l'archivage, le profil préféré ou les ressources enfants.

La création rattache le pipeline au workspace courant, sans le définir par défaut,
sans créer d'étape, d'opportunité ou de données de démonstration.

## Archivage et défaut

L'archivage renseigne `ArchivedAt` et retire le défaut s'il désigne ce pipeline,
dans une seule transaction. Aucun remplacement automatique n'est choisi.
La restauration efface `ArchivedAt`, sans rétablir le défaut ni modifier la visibilité.
Les appels répétés archive/restore sont idempotents, y compris pour les timestamps.
Les étapes et opportunités existantes restent intactes.

La définition explicite du défaut remplace la référence précédente. Elle nécessite
un pipeline actif du workspace courant. La référence nullable est portée par
`Workspace.DefaultPipelineId` et sa navigation `DefaultPipeline`.

La FK composée `(Workspace.Id, DefaultPipelineId)` référence
`Pipeline(WorkspaceId, Id)`, avec clé alternative et suppression `Restrict`.
Cela garantit l'appartenance au workspace même en SQL direct, sans cascade cyclique.
Le modèle conserve les GUID générés pour les identifiants.

L'état actif du défaut est garanti par le service : archive, restore et set-default
prennent tous un verrou PostgreSQL `FOR UPDATE` sur le workspace, dans une transaction
ReadCommitted, avant de lire le pipeline. Cela sérialise leurs appels concurrents
pour ce workspace. Les autres workspaces ne sont pas verrouillés.
Une écriture SQL directe peut encore archiver un pipeline référencé comme défaut :
aucun trigger n'est introduit pour cette règle métier inter-tables.

## Migration et tests

`20260929150147_Phase42PipelineLifecycleAndDefault` ajoute la référence nullable,
`Pipeline.IsVisible NOT NULL DEFAULT true`, la clé alternative, l'index de la FK
et la FK composée. Les pipelines existants deviennent visibles, aucun défaut n'est
sélectionné. Les migrations précédentes ne sont pas modifiées.

Les tests HTTP/PostgreSQL utilisent des conteneurs PostgreSQL 18 jetables selon le
principe de Phase 4.1. Docker Linux doit être démarré. Ils couvrent aussi une montée
de version depuis le schéma initial avec un pipeline existant, les contraintes FK
et la concurrence archive/set-default. Aucun test ne cible la base de développement.

```powershell
dotnet build ProspectionCrm.slnx --configuration Release
dotnet test ProspectionCrm.slnx --configuration Release --no-build
```
