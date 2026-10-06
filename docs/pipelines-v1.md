# API Pipeline et PipelineStage — Phase 4 et initialisation Phase 5

Toutes les routes utilisent `ICurrentWorkspaceProvider` : exactement un workspace
actif en V1. Un identifiant absent ou appartenant à un autre workspace renvoie 404.
Le bootstrap ne crée toujours aucun pipeline.
La Phase 5 ajoute un appel d'initialisation métier distinct, décrit ci-dessous.

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
| `POST /api/pipelines/{pipelineId}/clone` | 201 et Location vers le clone ; 400, 404 ou 409 |
| `GET /api/pipelines/{pipelineId}/export` | 200, document JSON V1 ; 404 si absent/hors workspace |
| `POST /api/pipelines/import` | 201 et Location vers le nouveau pipeline ; 400 si document invalide |

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
prennent tous un verrou PostgreSQL `FOR NO KEY UPDATE` sur le workspace, dans une transaction
ReadCommitted, avant de lire le pipeline. Cela sérialise leurs appels concurrents
pour ce workspace. Les autres workspaces ne sont pas verrouillés.
Une écriture SQL directe peut encore archiver un pipeline référencé comme défaut :
aucun trigger n'est introduit pour cette règle métier inter-tables.

Depuis la Phase 4.5, `NO KEY UPDATE` remplace ici `UPDATE` : les opérations de cycle
de vie/défaut restent mutuellement exclusives, mais les vérifications FK `KEY SHARE`
lors de l'insertion d'un clone peuvent avancer. Cela évite un interblocage entre un
clone tenant le verrou de la source puis insérant un pipeline, et un archivage tenant
le verrou de workspace puis attendant la source. Aucune clé de workspace n'est modifiée.

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

Le réordonnancement et le clonage disposent des contrats distincts de Phases 4.4 et
4.5 décrits ci-dessous. L'import/export relève de la Phase 4.6 ; aucun catalogue/template ou pipeline initial n'est ajouté.
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
Le clonage et l'import/export relèvent des Phases 4.5 et 4.6 ci-dessous ; la Phase 5 reste hors périmètre. Aucune modification du
modèle persistant ni migration Phase 4.4 n'est nécessaire.

## Clonage de configuration — Phase 4.5

`POST /api/pipelines/{pipelineId}/clone` reçoit uniquement le nouveau nom explicite :

```json
{ "name": "Emploi .NET — Variante Remote" }
```

`PipelineCloneRequest` expose uniquement `Name`. Le nom est obligatoire, non blanc,
trimé avant validation et stockage, limité à 200 caractères après trim. Les noms
identiques, y compris celui de la source, sont autorisés. Les champs JSON supplémentaires
sont ignorés selon la convention existante : ils ne permettent d'imposer ni identifiants,
ni workspace, configuration, étapes, positions, archivage ou état de défaut.

| Statut | Signification |
| --- | --- |
| 201 | `PipelineDto` du clone et Location vers `GET /api/pipelines/{newPipelineId}` |
| 400 | Nom absent, null, blanc ou trop long, ou corps invalide |
| 404 | Source absente ou hors workspace courant |
| 409 | Profil candidat préféré de la source hors workspace courant |

La source est recherchée via `ICurrentWorkspaceProvider`. Une source archivée peut
être clonée et reste inchangée, toujours archivée. Le nouveau pipeline appartient au
même workspace, possède un nouveau GUID et copie `TypeCode`, `Description`, `IsVisible`
et, si renseigné et du même workspace, `PreferredCandidateProfileId`. Il est toujours
actif (`ArchivedAt = null`), avec un nouveau `CreatedAt` et `UpdatedAt = null`.

Audit du profil préféré : la relation EF est une FK nullable simple vers
`CandidateProfile.Id`, avec suppression `SetNull`. Le profil possède un `WorkspaceId`,
mais la FK ne garantit pas l'égalité des workspaces. Le service contrôle donc cette
appartenance avant copie ; une référence hors workspace provoque un 409 sans clone.
Un profil du même workspace, même archivé, reste référencé sans être dupliqué ni modifié.
Une référence null reste null. Aucune contrainte ou architecture de profil n'est changée.

Seules les étapes actives sont copiées, par `SortOrder` croissant puis ID. Elles
reçoivent de nouveaux GUID, le même nom, description et catégorie, un nouveau
`CreatedAt`, `UpdatedAt = null` et `ArchivedAt = null`. Elles sont rattachées au clone
et leurs positions sont normalisées à `0..n-1`. Par exemple, des positions actives
0, 2 et 4 deviennent 0, 1 et 2. Les archives sont exclues ; les positions et données
de la source ne sont jamais modifiées. Sans étape active, le clone n'a aucune étape.

Le clone ne devient jamais automatiquement le défaut (`IsDefault = false`).
`Workspace.DefaultPipelineId` reste inchangé, même si la source est le défaut.
L'opération `set-default` existante reste disponible pour un choix ultérieur.

Le clonage ne copie aucun graphe d'entités : il construit explicitement un nouveau
Pipeline et ses seules étapes actives. Aucune Opportunity, tâche, candidature,
proposition, email, événement calendrier, activité, règle ou historique d'automatisation,
ni autre donnée opérationnelle n'est copiée ou déplacée. Aucun CandidateProfile n'est créé.

Une transaction ReadCommitted verrouille la source avec `FOR UPDATE`, comme les
écritures d'étapes des Phases 4.3/4.4. La lecture de la source utilise les valeurs
retournées après acquisition du verrou, sans réutiliser d'entité suivie antérieurement ;
les étapes sont ensuite lues sans tracking. Le pipeline et toutes ses étapes sont
insérés dans cette transaction et committés ensemble. Une erreur d'insertion d'étape
annule aussi la création du pipeline. Le clone ne peut donc observer les positions
temporaires d'un réordonnancement en cours. Deux clonages de la même source sont
sérialisés et peuvent réussir avec des identifiants indépendants. Le clonage ne prend
aucun verrou explicite de workspace ; les clones d'autres pipelines peuvent avancer.

Le modèle persistant reste inchangé : aucune migration Phase 4.5. L'import/export est
décrit en Phase 4.6 ; aucun clonage inter-workspace, catalogue/template global, pipeline initial ou logique Phase 5.

## Import/export JSON portable — Phase 4.6

Le format V1 est une configuration de pipeline, **pas une sauvegarde de base**.
`schemaVersion` représente la version du schéma d'échange, indépendamment de la
version de ProspectionCrm. Seule la valeur entière `1` est supportée.

```json
{
  "schemaVersion": 1,
  "pipeline": {
    "name": "Emploi .NET",
    "typeCode": "employment",
    "description": "Pipeline de prospection .NET",
    "isVisible": true,
    "stages": [
      { "name": "À analyser", "description": null, "categoryCode": "active" },
      { "name": "Candidature envoyée", "description": null, "categoryCode": "active" }
    ]
  }
}
```

Les propriétés ci-dessus constituent la liste exhaustive des propriétés permises.
Toutes sont obligatoires sauf `description`, nullable et pouvant être omise dans le
pipeline et les étapes. `isVisible` doit être explicitement un booléen, même false.
`stages` est obligatoire et non null ; `[]` est valide, un élément null ne l'est pas.

### Export

`GET /api/pipelines/{pipelineId}/export` renvoie ce document pour un pipeline du
workspace courant, actif ou archivé. Une source absente ou hors workspace renvoie
404. Une projection EF sans tracking, en une seule requête de lecture, sélectionne
uniquement la configuration et les étapes actives triées par `SortOrder`, puis `Id`.
Le tableau porte leur ordre : aucun `SortOrder` numérique n'est exporté. L'export
n'écrit rien, n'ajoute aucun timestamp et ne prend aucun verrou explicite. Deux
exports d'un état inchangé donnent la même structure métier ordonnée.

Le contrat exclut les ID du pipeline et des étapes, `WorkspaceId`, `SortOrder`,
`ArchivedAt`, `CreatedAt`, `UpdatedAt`, `IsDefault` et `PreferredCandidateProfileId`.
Le profil préféré est une référence locale non portable, même si son workspace
est celui du pipeline. Aucun CandidateProfile, Opportunity, CrmTask, Application,
Proposal, EmailMessage, CalendarEvent, ActivityEntry, donnée d'automatisation,
autre donnée opérationnelle ou historique n'est exporté.

### Import et validation

`POST /api/pipelines/import` reçoit directement le document et crée **toujours un
nouveau pipeline** dans le workspace courant. Aucun pipeline existant n'est mis à
jour, fusionné ou remplacé. Les noms identiques sont autorisés. Le nouveau pipeline
et ses étapes reçoivent de nouveaux GUID et `CreatedAt`, avec `UpdatedAt` et
`ArchivedAt` null. Le profil préféré reste null, `IsDefault` vaut false et
`Workspace.DefaultPipelineId` reste inchangé, même s'il n'existait aucun défaut.
Les positions sont normalisées à `0..n-1` selon l'ordre du tableau reçu.

La limite V1 est **1000 étapes par document importé** ; elle est contrôlée avant
toute création, sans ajouter de contrainte générale au modèle Pipeline ni limiter
l'export. Les noms sont trimés, obligatoires, non blancs et limités à 200 caractères
après trim. Les descriptions nullables sont limitées à 2000 caractères. Les codes
restent sensibles à la casse, sans trim : `employment`, `freelance`, `business`,
`custom` pour le pipeline ; `active`, `success`, `failure` pour les étapes.

Le contrôleur désérialise le `JsonElement` reçu vers `PipelineTransferDocument`
avec des options System.Text.Json **locales à l'import** : noms camelCase exacts,
`UnmappedMemberHandling = Disallow`, types stricts sans conversion d'une chaîne en
nombre. Les membres `required` imposent la présence des champs obligatoires ; le
service contrôle les nulls, la version et les invariants métier avant insertion.
Toute propriété inconnue à la racine, dans le pipeline ou une étape est refusée,
y compris un ID, un profil préféré ou un champ d'une version future. Les contrats
CRUD et clonage conservent leur comportement JSON antérieur.

Une version entière différente de 1 renvoie 400 avec un `ProblemDetails` mentionnant
explicitement `schemaVersion` et la seule version supportée. Les propriétés absentes,
nulls interdits, types incorrects, JSON malformés et erreurs métier renvoient aussi
400 ; les erreurs de désérialisation ne deviennent pas des 500.

Après validation complète, le service construit explicitement le pipeline et ses
seules étapes, puis les insère dans une transaction unique. Toute erreur DB pendant
l'insertion d'une étape annule l'ensemble : aucun pipeline ou étape partiel ne
subsiste. Aucun verrou explicite de pipeline existant ni infrastructure de verrouillage
n'est nécessaire. Aucune donnée opérationnelle n'est créée, copiée ou déplacée.

Le succès renvoie 201, le `PipelineDto` complet du nouveau pipeline et `Location`
vers `GET /api/pipelines/{newPipelineId}`. Un round-trip export/import préserve les
champs portables et l'ordre actif, avec de nouvelles identités et sans profil préféré.
Les noms importés sont normalisés par trim selon les règles métier existantes.

Le modèle EF, ses configurations et migrations restent inchangés ; aucune migration,
même vide, n'est nécessaire. Aucun import multi-pipelines, remplacement, sauvegarde
de base, catalogue/template, pipeline initial ou sujet Phase 5 n'est ajouté.

## Migration et tests

La chaîne complète comporte `20260925101154_InitialCrmSchema`, puis
`20260929150147_Phase42PipelineLifecycleAndDefault`. Les Phases 4.3 à 4.6
n'ajoutent aucune migration. Le contrôle `HasPendingModelChanges()` et la comparaison
des migrations appliquées/disponibles font partie du test de reconstruction Phase 4.

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

`PipelineCloneTests` vérifie le contrat HTTP, les copies explicites, la normalisation,
les sources archivées, le profil préféré (y compris la FK inter-workspace actuellement
permise et le refus métier en 409), le défaut inchangé et le rollback atomique. Les
snapshots de toutes les tables prouvent la préservation de la source et des données
opérationnelles. Les tests PostgreSQL suspendent de vraies écritures d'étape, notamment
un réordonnancement après sauvegarde des positions temporaires, pour vérifier l'attente
et la copie de l'état committé. Ils couvrent aussi deux clonages simultanés, un autre
pipeline indépendant et l'absence d'interblocage clonage/archivage du pipeline par défaut.

`PipelineTransferTests` couvre les exports actifs/archivés, les champs portables
exacts, l'ordre et son départage, l'isolation workspace, les exports sans étape
active et les round-trips. Les imports vérifient les identités, le défaut inchangé,
les codes, le trim, les limites, les tableaux de 0/1000/1001 étapes et les erreurs
structurelles à tous les niveaux. Des snapshots de toutes les tables vérifient
l'absence d'écriture à l'export, après refus et après rollback, ainsi que la
préservation des données opérationnelles après import. Le test de départage par ID
retire l'index unique uniquement dans sa base jetable, pour rendre observable ce
cas normalement impossible avec le schéma courant. Aucune migration n'est modifiée.

`Phase4ReconstructionTests` complète les tests ciblés avec un parcours HTTP unique
sur PostgreSQL vierge : migrations, démarrage sans création implicite, bootstrap
idempotent, pipeline masqué, étapes et archive, réordonnancement conservant les
positions archivées, défaut, Opportunity, refus d'affectation à une archive,
archivage/restauration du parent, clonage et round-trip export/import. Il vérifie
les nouvelles identités, le défaut retiré sans restauration automatique, la
préservation de l'Opportunity et l'isolation envers un autre workspace.

L'audit de Phase 4.7 a identifié une fenêtre entre la validation d'une nouvelle
affectation Opportunity et sa sauvegarde : un archivage pouvait s'y intercaler.
La création et la modification d'Opportunity utilisent désormais une transaction.
Pour une création ou un changement d'étape, un verrou `FOR SHARE` est pris sur le
pipeline cible avant de relire l'état actif de l'étape et du parent, et conservé
jusqu'au commit. Il bloque l'UPDATE d'archivage du parent et les écritures d'étapes
(`FOR UPDATE`), mais reste compatible avec d'autres affectations Opportunity.
Aucun verrou explicite de workspace n'est ajouté. Une modification conservant
l'étape historique n'exige toujours pas son activité et ne prend pas ce verrou.

Sept cas de concurrence complètent le parcours de reconstruction : création et
réaffectation face à l'archivage d'étape ou de parent, revalidation après attente
d'un archivage déjà engagé, et compatibilité entre affectations simultanées avec
indépendance des écritures sur un autre pipeline. Les quatre premiers cas ont
reproduit le défaut avant correction. Les contrats HTTP restent inchangés.

Les pipelines initiaux relèvent de Phase 5 ; Emploi .NET, Freelance / Malt,
Emploi Jeu Vidéo et Business Jeu Vidéo sont maintenant disponibles via l'appel explicite décrit ci-dessous. Aucun n'est créé au démarrage,
par le bootstrap ou par les migrations. La FK simple du profil préféré reste une limite
connue du modèle : le clonage contrôle le workspace et l'import/export exclut cette
référence locale. L'activité du pipeline par défaut est assurée par les services,
pas par un trigger contre les écritures SQL directes. Ces limites ne sont pas
transformées en changements de schéma pendant l'audit.

## Initialisation métier explicite — Phase 5.5 (quatre pipelines)

`POST /api/setup/initial-pipelines` est distinct de `POST /api/setup/bootstrap`.
Il ne prend aucun paramètre ni corps et cible le workspace courant V1. Le bootstrap
doit avoir créé le propriétaire/workspace au préalable. Aucun appel n'est effectué
au démarrage de l'API ou automatiquement par le frontend.

| Résultat | Contrat |
| --- | --- |
| 201 | Au moins un pipeline créé ; `InitialPipelinesDto` |
| 200 | Tous les pipelines déjà initialisés ; même DTO avec `createdPipelineIds` vide ; aucune écriture |
| 409 | Workspace absent/multiple/inactif, homonyme non reconnu ou identifiant réservé occupé hors workspace ; `ProblemDetails` sans création |

Le DTO contient :

- `pipelines` : les `PipelineDto` courants, dans l'ordre Emploi .NET, Freelance / Malt,
  Emploi Jeu Vidéo, puis Business Jeu Vidéo,
  y compris les pipelines et étapes archivés ; les noms/propriétés peuvent avoir été modifiés.
- `createdPipelineIds` : uniquement les IDs créés pendant cet appel, dans le même ordre.

Ce contrat remplace le corps `PipelineDto` unique de Phase 5.2. Aucun en-tête
`Location` n'est émis pour cette initialisation multiple ; chaque pipeline reste
consultable par `GET /api/pipelines/{id}`. Les futurs templates pourront compléter
la collection sans modifier la structure du DTO.

### Emploi .NET (inchangé depuis Phase 5.2)

- Nom : `Emploi .NET` ; type : `employment` ; visibilité : `true`.
- Description : `Pipeline de prospection pour les offres d'emploi .NET / C#, principalement autour de Bordeaux et en remote France/Europe.`
- Pipeline et étapes actifs, profil préféré null, descriptions d'étapes null.

| SortOrder | Étape | Catégorie |
| --- | --- | --- |
| 0 | À analyser | active |
| 1 | À candidater | active |
| 2 | Candidature envoyée | active |
| 3 | Entretien | active |
| 4 | Offre | success |
| 5 | Refusé | failure |
| 6 | Abandonné | failure |

### Freelance / Malt

- Nom : `Freelance / Malt` ; type : `freelance` ; visibilité : `true`.
- Description : `Pipeline de prospection pour les missions freelance C# / .NET / ASP.NET Core et Unity, principalement en remote ou autour de Bordeaux.`
- Pipeline et étapes actifs, profil préféré null, descriptions d'étapes null.
- Ne devient jamais automatiquement le défaut.

| SortOrder | Étape | Catégorie |
| --- | --- | --- |
| 0 | À analyser | active |
| 1 | À contacter | active |
| 2 | Proposition envoyée | active |
| 3 | Échange client | active |
| 4 | Mission gagnée | success |
| 5 | Refusée / perdue | failure |
| 6 | Abandonnée | failure |

### Emploi Jeu Vidéo

- Nom : `Emploi Jeu Vidéo` ; type : `employment` ; visibilité : `true`.
- Description : `Pipeline de prospection pour les offres d'emploi jeu vidéo Unity / C#, principalement en France ou en remote Europe.`
- Pipeline et étapes actifs, profil préféré null, descriptions d'étapes null.
- Ne devient jamais automatiquement le défaut.

| SortOrder | Étape | Catégorie |
| --- | --- | --- |
| 0 | À analyser | active |
| 1 | À candidater | active |
| 2 | Candidature envoyée | active |
| 3 | Entretien | active |
| 4 | Offre | success |
| 5 | Refusé | failure |
| 6 | Abandonné | failure |

### Business Jeu Vidéo

- Nom : `Business Jeu Vidéo` ; type : `business` ; visibilité : `true`.
- Description : `Pipeline de prospection business pour les studios, éditeurs, partenaires et structures d'accompagnement du jeu vidéo, notamment autour de CrewRats et des outils développés.`
- Pipeline et étapes actifs, profil préféré null, descriptions d'étapes null.
- Ne devient jamais automatiquement le défaut.

| SortOrder | Étape | Catégorie |
| --- | --- | --- |
| 0 | Cible identifiée | active |
| 1 | À contacter | active |
| 2 | Contacté | active |
| 3 | Échange en cours | active |
| 4 | Opportunité concrète | active |
| 5 | Accord / partenariat | success |
| 6 | Sans suite | failure |

Ce parcours générique couvre CrewRats, la recherche d'éditeur ou de partenaire,
la promotion d'outils, le réseau et les autres opportunités commerciales ou
stratégiques du jeu vidéo. Aucun motif ne devient une étape spécifique.

La première installation crée les quatre pipelines et leurs 28 étapes atomiquement.
`createdPipelineIds` contient alors les quatre IDs dans le même ordre que `pipelines`.
Seul Emploi .NET devient défaut si aucun défaut ni aucune identité initiale n'existait.

### Identité et répétitions

Le modèle existant ne comporte pas de marqueur de template et les noms ne sont pas
uniques. L'initialiseur réserve donc un ID déterministe par template et workspace, stocké dans
la PK existante, sans nouveau champ ni migration. La convention permanente est :
SHA-256 des octets UTF-8 des clés suivantes :

- `HellzDraft/ProspectionCrm/initial-pipelines/employment-dotnet/{workspaceId:D}` (inchangée) ;
- `HellzDraft/ProspectionCrm/initial-pipelines/freelance-malt/{workspaceId:D}` (inchangée) ;
- `HellzDraft/ProspectionCrm/initial-pipelines/employment-game-dev/{workspaceId:D}` (inchangée) ;
- `HellzDraft/ProspectionCrm/initial-pipelines/business-game-dev/{workspaceId:D}`.

Les 16 premiers octets sont lus en ordre réseau (`bigEndian: true`), avec
`hash[6] = (hash[6] & 0x0f) | 0x80` (UUIDv8) et
`hash[8] = (hash[8] & 0x3f) | 0x80` (variante RFC).
Pour le workspace `11111111-2222-3333-4444-555555555555`, les vecteurs figés sont :

- Emploi .NET : `a30b7775-d6de-8404-adb7-d7f12a07d164` ;
- Freelance / Malt : `facae35b-9c73-8a26-b928-aa1767b5d199` ;
- Emploi Jeu Vidéo : `66d1f9d1-d3fc-816b-b2b7-c32a9c45604e` ;
- Business Jeu Vidéo : `1a4f8704-0699-8f43-9177-c7f0e3d1f036`.

Le workspace est formaté en GUID canonique minuscule. Cette clé ne doit jamais changer
avec le nom, le contenu du template ou la version de l'application.

Un pipeline portant cet ID dans le workspace est reconnu même après renommage,
changement de type, modification/réordonnancement/archivage d'étapes ou archivage du
pipeline. Aucun champ, timestamp, étape ou choix de défaut n'est réécrit. Un défaut
retiré après la première création reste absent, même si le pipeline est encore actif.
Les étapes gardent des GUID ordinaires générés à leur création.

Pour chaque template absent, un homonyme `Emploi .NET`, `Freelance / Malt`, `Emploi Jeu Vidéo` ou `Business Jeu Vidéo`
avec un autre ID, actif ou archivé, provoque 409 : son nom ne prouve pas sa provenance et il n'est pas adopté.
Les noms identiques restent autorisés par les routes CRUD ordinaires. Ce contrôle
ne constitue pas une nouvelle contrainte d'unicité des noms. La garantie d'une seule
instance initialisée repose sur l'ID réservé, pas sur le nom.

Le clonage et l'import de Phase 4 génèrent de nouveaux IDs : ils ne transmettent
pas l'identité d'initialisation. L'export portable n'est donc pas une sauvegarde de
cette identité. Une suppression physique par SQL, hors API, effacerait le marqueur ;
un appel ultérieur pourrait recréer le pipeline. Les opérations d'archivage normales
conservent l'ID et ne provoquent jamais cette recréation.

### Transaction, défaut et périmètre

`InitialPipelineService` utilise `ICurrentWorkspaceProvider` et une transaction
ReadCommitted. Il verrouille d'abord le workspace avec `FOR NO KEY UPDATE`, comme
les opérations de cycle de vie/défaut, puis recherche les IDs réservés. Deux appels
simultanés relisent l'état après attente : un seul crée, l'autre reconnaît l'existant.
Le verrou reste compatible avec les vérifications FK `KEY SHARE` des clones.
Il ne prend pas de verrou de pipeline existant et ne crée pas de cycle inverse
pipeline → workspace. Les opérations explicites de choix du défaut utilisent le
même verrou ; un défaut déjà choisi est conservé.

Tous les IDs et homonymes sont contrôlés avant toute insertion. La première
sauvegarde insère uniquement les pipelines absents et leurs sept étapes chacun.
Si aucune identité initiale n'existait avant l'appel et que le workspace n'a pas
de défaut, une seconde sauvegarde choisit Emploi .NET dans la même transaction.
Tout défaut utilisateur existant est conservé. Toute erreur, y compris sur le
quatrième pipeline, ses étapes ou le défaut, annule les créations de l'appel.
Les DTO sont lus par `IPipelineService`, avec les contrats de lecture de Phase 4.

### Upgrade Phase 5.4 → 5.5

Les IDs réservés reconnaissent les trois pipelines existants même après renommage,
modification ou archivage. Seul Business Jeu Vidéo est créé, avec ses sept étapes.
La réponse 201 conserve `InitialPipelinesDto` : `pipelines` contient les quatre DTO
dans l'ordre contractuel et `createdPipelineIds` uniquement l'ID Business Jeu Vidéo.
Aucun champ, étape, ordre, archive ou timestamp préexistant n'est réécrit.
Le défaut reste strictement inchangé : Emploi .NET, autre choix utilisateur ou
absence de défaut. L'upgrade ne choisit ni ne restaure jamais un défaut.

Un homonyme actif ou archivé du quatrième template, ou une erreur d'insertion,
laisse tout l'état Phase 5.4 intact. Après réussite, les appels suivants renvoient
200 avec `createdPipelineIds` vide, sans écriture. Deux upgrades simultanés créent
exactement un Business Jeu Vidéo : un résultat annonce sa création, l'autre non.

Depuis Phase 5.2, seuls Freelance / Malt, Emploi Jeu Vidéo et Business Jeu Vidéo
sont créés. Depuis Phase 5.3, seuls Emploi Jeu Vidéo et Business Jeu Vidéo sont créés.
Les IDs réellement créés apparaissent dans cet ordre dans `createdPipelineIds` ;
les données et le défaut existants restent intacts.

`InitialPipelineTests` couvre les configurations exactes des quatre templates,
les migrations/bootstrap/démarrage sans création implicite, le modèle EF inchangé,
les répétitions, modifications et archives, les upgrades Phase 5.2/5.3/5.4,
les quatre vecteurs UUID, les homonymes, les IDs occupés et l'isolation workspace.
Les erreurs imposées sur Business Jeu Vidéo ou ses étapes vérifient le rollback
complet sur installation vierge et upgrade Phase 5.4. La concurrence est testée
sur installation vierge et upgrades Phase 5.3/5.4, avec défaut absent, préexistant
ou choisi pendant l'attente.
Les bases de tests sont jetables ; aucune initialisation n'est exécutée sur la base
de développement par les tests.

La Phase 5 possède désormais son catalogue initial complet ; les quatre pipelines restent entièrement configurables après création. Cette livraison n'implémente
aucune collecte, scoring, règle de mots-clés, exclusion AAA, IA, automatisation,
SavedSearch, SourceConfiguration ou interface Blazor spécifique. Aucun
modèle EF, snapshot ou migration n'est modifié.

```powershell
dotnet build ProspectionCrm.slnx --configuration Release
dotnet test ProspectionCrm.slnx --configuration Release --no-build
```
