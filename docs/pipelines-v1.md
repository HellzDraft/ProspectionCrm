# API Pipeline et PipelineStage — Phases 4.2 à 4.4

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
Il n'existe aucun endpoint DELETE Pipeline ou PipelineStage.

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

## Configuration des étapes — Phase 4.3

Les routes sont portées par `PipelineStagesController`, avec un service dédié
`IPipelineStageService` / `PipelineStageService`, enregistré en scoped. Les réponses
réutilisent `PipelineStageDto`. Toutes les recherches vérifient le workspace courant
et le pipeline indiqué : une étape d'un autre pipeline, une ressource absente ou
hors workspace renvoie 404.

| Route | Résultat |
| --- | --- |
| `GET /api/pipelines/{pipelineId}/stages` | 200, étapes actives triées par SortOrder puis ID ; 404 si parent absent/hors workspace |
| `GET /api/pipelines/{pipelineId}/stages?includeArchived=true` | 200, inclut les étapes archivées dans le même ordre |
| `GET /api/pipelines/{pipelineId}/stages/{stageId}` | 200 ou 404 ; étape archivée directement consultable |
| `POST /api/pipelines/{pipelineId}/stages` | 201 et Location vers le détail ; 400, 404 ou 409 |
| `PUT /api/pipelines/{pipelineId}/stages/{stageId}` | 204 ; 400, 404 ou 409 |
| `POST /api/pipelines/{pipelineId}/stages/{stageId}/archive` | 204, 404 ou 409 |
| `POST /api/pipelines/{pipelineId}/stages/{stageId}/restore` | 204, 404 ou 409 |

`PipelineStageWriteRequest` expose uniquement `Name`, `Description`, `CategoryCode`.
Le nom obligatoire est trimé avant validation et stockage (1 à 200 caractères).
La description est nullable, limitée à 2000 caractères. La catégorie est exactement
`active`, `success` ou `failure`, sans normalisation de casse ni d'espaces.
Une entrée invalide renvoie 400. Le PUT remplace ces trois champs ; une description
omise devient null. Les propriétés JSON supplémentaires sont ignorées : elles ne
permettent de modifier ni `PipelineId`, ni `SortOrder`, ni `ArchivedAt`, ni Opportunity.

La première étape reçoit `SortOrder = 0`. Chaque création suivante reçoit le maximum
des positions du pipeline, étapes archivées comprises, plus un. Les trous ne sont
pas comblés et aucune étape existante n'est renumérotée. Si le maximum atteint
`int.MaxValue`, la création renvoie 409 sans écriture ni dépassement arithmétique.

Chaque écriture ouvre une transaction PostgreSQL ReadCommitted et verrouille la
ligne du pipeline parent avec `SELECT ... FOR UPDATE`, filtré par ID et workspace.
Le verrou précède la lecture des étapes et le calcul du maximum ; calcul, insertion
et sauvegarde restent dans cette même transaction jusqu'au commit. Deux créations
concurrentes du même pipeline sont ainsi sérialisées ; la seconde voit le commit
de la première avant de calculer sa position. L'index unique existant sur
`(PipelineId, SortOrder)` reste en place. Aucun verrou de workspace n'est pris par
ce service et les écritures d'étapes des autres pipelines peuvent avancer.
Le verrou entre aussi en conflit avec l'UPDATE d'archivage du parent : après attente,
une écriture d'étape relit son état archivé et est refusée si nécessaire.

Archiver renseigne `ArchivedAt`, restaurer l'efface ; les changements effectifs
actualisent `UpdatedAt`. Les appels répétés sont idempotents, timestamps compris.
L'étape garde toujours sa position et la restauration la rend de nouveau modifiable.
Modifier une étape archivée renvoie 409. Si le parent est archivé, les lectures
restent possibles mais toutes les écritures d'étapes renvoient 409, y compris
archive/restore répétés. Il faut restaurer le parent avant de changer ses étapes.

Aucune de ces opérations ne modifie les Opportunities, leurs rattachements ou les
autres étapes. Les règles Opportunity existantes restent applicables : création et
nouvelle affectation exigent une étape et un parent actifs ; une Opportunity déjà
rattachée peut encore être modifiée en conservant son étape archivée ou son parent
archivé. La catégorie ne déclenche aucune modification automatique d'Opportunity.

Le réordonnancement dispose du contrat distinct de Phase 4.4 décrit ci-dessous. Aucun
catalogue/template d'étapes, clonage, import-export ou pipeline initial n'est ajouté.
Le modèle persistant, ses configurations et son snapshot restent inchangés : aucune
migration Phase 4.3, même vide, n'est nécessaire ni créée.

## Réordonnancement des étapes actives — Phase 4.4

`PUT /api/pipelines/{pipelineId}/stages/order` reçoit un `PipelineStageOrderRequest`
contenant uniquement `stageIds`, tableau obligatoire de GUID dans l'ordre final voulu :

```json
{
  "stageIds": [
    "11111111-1111-1111-1111-111111111111",
    "33333333-3333-3333-3333-333333333333",
    "22222222-2222-2222-2222-222222222222"
  ]
}
```

Le client fournit exactement une fois chaque étape active du pipeline. Il ne fournit
aucun `SortOrder` numérique. Une liste vide est valide uniquement s'il n'existe aucune
étape active, y compris lorsque le pipeline ne contient que des archives. Un champ
absent ou null est invalide. Les champs JSON supplémentaires sont ignorés selon la
convention existante et ne peuvent imposer de position numérique.

| Statut | Signification |
| --- | --- |
| 204 | Réordonnancement terminé, ou ordre déjà identique ; aucun corps |
| 400 | Corps invalide, doublon, étape manquante, ID inconnu, archivé ou d'un autre pipeline/workspace |
| 404 | Pipeline absent ou hors workspace courant |
| 409 | Pipeline parent archivé, ou nombre insuffisant de positions temporaires libres |

Les étapes archivées sont exclues de la permutation et ne subissent aucune écriture,
y compris sur leurs timestamps. Les étapes actives se répartissent uniquement les
positions actives existantes, triées par valeur croissante. Les trous restent des
trous et les positions archivées restent occupées. Par exemple, avec A en 0, une
archive en 1, B en 2 et C en 7, la permutation `[C, A, B]` place C en 0, A en 2,
B en 7, sans toucher à l'archive en 1.

Le service utilise le même verrou PostgreSQL `FOR UPDATE` sur le pipeline parent
que les écritures de Phase 4.3, dans une transaction ReadCommitted. Les étapes sont
relues et la permutation validée **après acquisition du verrou**. Deux réordonnancements
sont sérialisés. Si une création termine avant un réordonnancement qui omet la nouvelle
étape, celui-ci renvoie 400 ; si le réordonnancement termine d'abord, la création ajoute
son étape au maximum global plus un. Aucun verrou supplémentaire de workspace n'est
pris : les autres pipelines peuvent avancer indépendamment.

L'index unique existant `(PipelineId, SortOrder)` et la contrainte `SortOrder >= 0`
restent inchangés. Pour éviter toute collision intermédiaire, le service :

1. Mémorise les positions actives et toutes les positions occupées, archives comprises.
2. Sélectionne autant de positions temporaires que d'étapes actives : les plus petits
   entiers non négatifs inoccupés, en parcourant les candidats dans l'ordre croissant.
3. Affecte ces positions aux étapes actives et effectue une première sauvegarde.
4. Affecte les positions actives initiales selon la permutation et sauvegarde à nouveau.
5. Commit la transaction, en conservant le verrou jusqu'à cette étape.

Les positions temporaires sont distinctes et disjointes de toutes les positions
initiales ; après la première sauvegarde, toutes les positions finales sont libres.
Le curseur de recherche est un `long`, contrôlé avant conversion en `int` : aucun
offset fixe, addition au maximum ou dépassement d'entier. Même une position initiale
égale à `int.MaxValue` peut être réordonnée en utilisant les espaces libres plus bas.
Si l'espace non négatif des `int` ne fournit pas assez de positions temporaires,
l'opération renvoie 409 avant toute mutation. Une erreur après la première sauvegarde
annule toute la transaction ; les positions temporaires ne sont jamais commitées.

Un ordre identique ne modifie aucun champ ni timestamp. Lors d'un changement, seules
les étapes dont la position finale change reçoivent un nouvel `UpdatedAt`. Aucune
Opportunity n'est écrite : son `PipelineStageId`, son archivage et tous ses autres
champs restent identiques. `OpportunityService` conserve ses règles d'affectation aux
étapes/parents actifs et n'est pas modifié.

Les invariants de Phase 4.3 restent applicables : création en fin, archive sans
déplacement, restauration à la position conservée, modification d'archive interdite
et aucune écriture d'étape lorsque le parent est archivé. Aucun réordonnancement
partiel, déplacement entre pipelines ou endpoint montée/descente n'est ajouté.
Les sujets des Phases 4.5, 4.6 et 5 restent hors périmètre. Aucune modification du
modèle persistant ni migration Phase 4.4 n'est nécessaire.

## Migration et tests

`20260929150147_Phase42PipelineLifecycleAndDefault` ajoute la référence nullable,
`Pipeline.IsVisible NOT NULL DEFAULT true`, la clé alternative, l'index de la FK
et la FK composée. Les pipelines existants deviennent visibles, aucun défaut n'est
sélectionné. Les migrations précédentes ne sont pas modifiées.

Les tests HTTP/PostgreSQL utilisent des conteneurs PostgreSQL 18 jetables selon le
principe de Phase 4.1. Docker Linux doit être démarré. Ils couvrent aussi une montée
de version depuis le schéma initial avec un pipeline existant, les contraintes FK
et la concurrence archive/set-default. Aucun test ne cible la base de développement.

`PipelineStageTests` couvre les six routes, les validations, l'isolation workspace
et pipeline, les positions et trous archivés, les champs protégés, les cycles
archive/restore, les parents archivés, la préservation des Opportunities et leurs
règles d'affectation, ainsi que l'absence de DELETE. Les tests de concurrence utilisent
de vraies transactions et connexions PostgreSQL : deux créations mises en attente
sur le parent, un archivage concurrent du parent et une création dans un autre
pipeline pendant que le premier est verrouillé. L'attente est observée via
`pg_stat_activity`, avec délai maximal, avant de libérer les verrous.

`PipelineStageOrderTests` complète cette couverture : permutations, ordre identique,
archives et trous, entrées invalides, isolation, bornes `int.MaxValue`, rollback de
la phase temporaire, préservation de tous les champs Opportunity et règles d'archives.
Les tests PostgreSQL réels couvrent deux réordonnancements concurrents, création et
réordonnancement concurrents, revalidation après création/archivage pendant l'attente
du verrou, et réordonnancement indépendant dans un autre pipeline.

```powershell
dotnet build ProspectionCrm.slnx --configuration Release
dotnet test ProspectionCrm.slnx --configuration Release --no-build
```
