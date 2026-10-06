# Ingestion manuelle de résultats — Phase 6.1

Cette route reçoit des résultats déjà fournis par le client. Elle n'exécute aucune
recherche réseau, collecte planifiée ou automatisation. La configuration et la
recherche sauvegardée doivent déjà exister ; aucun catalogue métier n'est créé.

## Contrat HTTP

`POST /api/saved-searches/{savedSearchId}/ingestions`

```json
{
  "pipelineStageId": "<GUID de l'étape cible>",
  "items": [
    {
      "title": "Développeur Unity",
      "externalId": "job-123",
      "sourceUrl": "https://example.org/jobs/123",
      "companyName": "Studio exemple",
      "location": "France",
      "description": "Description fournie par le client"
    }
  ]
}
```

L'étape est explicitement fournie, jamais déduite de son nom, de son ordre ou du
pipeline par défaut. Elle doit être non archivée et appartenir exactement au
pipeline non archivé de la recherche. Recherche et configuration doivent être
non archivées et `Enabled = true`, dans le workspace courant.

| Champ | Règle |
| --- | --- |
| `pipelineStageId` | GUID obligatoire et non vide |
| `items` | Tableau obligatoire de 1 à 100 éléments non null |
| `title` | Obligatoire, non blanc, maximum 200 caractères |
| `externalId` | Facultatif, non blanc si fourni, maximum 500 ; opaque et sensible à la casse |
| `sourceUrl` | Facultative, maximum 2048 ; URL HTTP(S) absolue sans identifiants utilisateur |
| `companyName` | Facultatif, non blanc si fourni, maximum 200 ; comparaison uniquement |
| `location` | Facultative, maximum 200 |
| `description` | Facultative, maximum 10000 ; stockée dans `Opportunity.Notes` à la création seulement |

Chaque élément doit fournir au moins `externalId`, `sourceUrl` ou `companyName`.
Un couple titre/société sans URL ni identifiant peut retrouver une opportunité
existante ; s'il ne la retrouve pas, le lot échoue en 409 `MissingPersistentIdentity`.
Il n'existe aucun champ société libre sur Opportunity : une nouvelle opportunité
doit donc disposer d'une URL ou d'un identifiant externe pour être reconnue au rejeu.

Aucune Company n'est créée ni affectée à partir de son nom. `CompanyName` ne sert
qu'à comparer le nom de la Company déjà liée aux opportunités existantes. Plusieurs
sociétés peuvent avoir le même nom ; plusieurs opportunités candidates produisent
un conflit. Le titre seul, ou titre + société absente, n'est jamais une identité.

Les propriétés inconnues sont refusées, y compris `publishedAt`, `metadata`,
`priorityCode` ou des timestamps de provenance. Les dates de publication et les
métadonnées ne sont pas conservées. Le corps HTTP est limité à 2 000 000 octets
(un dépassement de transport peut produire 413).

## Réponses

Un succès renvoie **201**, y compris pour un rejeu : une nouvelle SourceExecution
est créée pour chaque lot admis. `Location` pointe vers
`GET /api/source-executions/{executionId}`.

```json
{
  "execution": {
    "id": "<GUID>",
    "sourceConfigurationId": "<GUID>",
    "savedSearchId": "<GUID>",
    "triggerTypeCode": "manual",
    "statusCode": "succeeded",
    "startedAt": "<date UTC>",
    "finishedAt": "<date UTC>",
    "itemsFound": 1,
    "itemsCreated": 1,
    "itemsUpdated": 0,
    "itemsIgnored": 0,
    "errorMessage": null
  },
  "items": [
    { "index": 0, "opportunityId": "<GUID>", "outcome": "created" }
  ]
}
```

Les résultats restent dans l'ordre du lot, avec index à partir de zéro. Les issues
sont `created`, `updated` ou `ignored`. `updated` signifie **redécouverte**, avec
observation de la provenance, jamais remplacement des champs métier Opportunity.

| Statut | Cas |
| --- | --- |
| 400 | JSON/DTO/lot invalide, URL invalide, longueurs dépassées, absence d'identité utilisable |
| 404 | Recherche, configuration, pipeline ou étape inexistante ou hors workspace |
| 409 | Workspace V1 incompatible, état inactif, étape d'un autre pipeline, identité ambiguë ou non persistable, collision concurrente reconnue |
| 500 | Échec technique pendant le traitement, avec rollback métier |

Les erreurs métier renvoient ProblemDetails avec `code`, `itemIndex` nullable et
`executionId` nullable. Les validations automatiques ASP.NET renvoient leur
ValidationProblemDetails habituel. Aucun classement ne dépend du texte d'une
exception. Les détails SQL ne sont pas exposés au client.

## Normalisation et identités

URL : suppression des blancs périphériques, passage du schéma et de l'autorité
(hôte, avec port éventuel) en minuscules invariantes. Les blancs internes, caractères
de contrôle, antislashs, URL relatives et identifiants utilisateur sont refusés.
Le chemin, la query et le fragment restent inchangés : casse, ordre des paramètres,
paramètres de suivi, encodage pourcent, slash final et port explicite sont conservés.
Pas de décodage, résolution de redirection, ajout de slash ou suppression de fragment.
Les URL déjà stockées sont comparées avec la même fonction mais jamais réécrites.
Une ancienne URL invalide ne participe pas à la comparaison URL.

Titre/société : `Trim()`, remplacement de chaque suite d'espaces reconnus par
`\s` par un espace ASCII, puis `ToUpperInvariant()`. Ni suppression d'accents,
ni translittération, ni fuzzy matching. Ces clés servent uniquement à comparer :
le titre, la localisation et la description fournis restent inchangés au stockage.
ExternalId est comparé exactement, sans trim ni changement de casse.

La recherche couvre **tout le workspace**, toutes les étapes/pipelines et les
opportunités archivées. Ordre de priorité :

1. `(SourceConfigurationId, ExternalId)` ;
2. URL normalisée des OpportunitySource ;
3. titre normalisé + nom normalisé de la Company liée, dans le même workspace.

Toutes les identités disponibles sont examinées. Si leur union désigne plusieurs
opportunités, même avec un ExternalId valide, le résultat est un 409
`AmbiguousIdentity`. Une URL déjà portée par plusieurs opportunités est également
ambiguë. Aucune fusion ou sélection arbitraire n'est effectuée.

Une création utilise l'étape demandée et `PriorityCode = normal`, sans société,
contact, score, archive ou modification du défaut. Une redécouverte ne change
**aucun champ Opportunity**, y compris `UpdatedAt`.

## Provenance

Une nouvelle provenance reçoit configuration/recherche/exécution d'origine,
`SourceLabel = SourceConfiguration.Name`, les identités disponibles et les dates
serveur. Une provenance existante conserve ses identités, son libellé et ses
références d'origine. `LastSeenAt` devient le maximum de sa valeur précédente,
de `FirstSeenAt` et du début de l'exécution ; il ne recule jamais.

Une nouvelle URL pour un ExternalId connu crée un alias URL distinct, sans effacer
l'ancienne URL. Une URL déjà attachée à l'opportunité ne peut pas être réinsérée
sous une autre configuration : l'index SQL existant l'interdit. Si un nouvel
ExternalId accompagne cette URL, une provenance portant cet ExternalId et une URL
null est ajoutée ; la provenance URL existante est conservée et observée.
Sans nouvel ExternalId, seule la provenance URL existante est observée.

Pour une redécouverte par titre/société seul, une provenance sans ExternalId ni URL
est créée ou réutilisée pour cette opportunité/configuration.

Cela conserve les identités reconnues sans prétendre fournir un historique complet
des observations. `SourceExecutionId` et `SavedSearchId` restent les références de
première création de la provenance, pas celles de sa dernière observation.

## Transaction et concurrence

`IngestionService` possède une transaction PostgreSQL **ReadCommitted** couvrant
le lot ; il ne chaîne pas les services CRUD existants.

Un verrou transactionnel consultatif `pg_advisory_xact_lock` sérialise les seules
ingestions d'un même workspace. Sa clé est constituée des huit premiers octets
SHA-256, lus en Int64 big-endian, de
`ProspectionCrm/manual-ingestion/{workspaceId:D}`. Ce périmètre correspond à celui
de la déduplication, y compris entre recherches et configurations différentes.
Il n'y a aucun verrou global ni verrou de table ; un autre workspace peut avancer.

Ordre : verrou consultatif, workspace `FOR KEY SHARE`, recherche `FOR SHARE`,
configuration `FOR SHARE`, pipeline `FOR SHARE`, relecture de l'étape. Les valeurs
sont relues après attente. Le verrou parent protège la sélection d'étape face aux
services d'archivage/réordonnancement de Phase 4. Aucune ligne Opportunity existante
n'est modifiée et aucun verrou workspace `FOR NO KEY UPDATE` n'est ajouté.

Les requêtes CRUD historiques et SQL externes ne prennent pas le verrou consultatif.
Les index uniques de provenance restent la dernière protection pour les identités
qu'ils couvrent : une violation `23505` de l'un de leurs deux noms connus devient
409 `ConcurrentIdentityChange`, sans retry. La garantie complète de sérialisation
de déduplication porte sur les appels à ce nouvel endpoint.

## SourceExecution et atomicité

La validation du lot et des ressources précède toute création d'exécution. Les
refus à cette étape n'ajoutent aucune donnée.

Un lot admis crée une exécution `manual/running`, puis un savepoint. Les écritures
métier suivent ce savepoint. En succès, le statut devient `succeeded` et l'ensemble
est committé. Le POST retourne les timestamps relus dans PostgreSQL, avec la même
précision que le GET. Le statut `running` reste interne à la transaction et n'est
pas publié comme suivi temps réel.

En conflit ou erreur technique, rollback au savepoint, abandon de l'état EF suivi,
puis finalisation `failed` et commit de la seule exécution. Une annulation reçue
pendant le traitement tente de conserver `cancelled` avec la même procédure.
La finalisation dispose d'un délai indépendant de 10 secondes, sans retry.

| Compteur | Succès |
| --- | --- |
| `ItemsFound` | Nombre d'éléments reçus, doublons compris |
| `ItemsCreated` | Éléments ayant créé une nouvelle Opportunity |
| `ItemsUpdated` | Éléments non répétés ayant retrouvé une Opportunity et observé sa provenance |
| `ItemsIgnored` | Répétitions dans le lot de la même signature ExternalId/URL normalisée/titre normalisé/société normalisée |

Une répétition n'écrase pas les données même si sa localisation/description diffère.
Un rejeu dans un nouvel appel observe de nouveau la provenance et compte `updated`.
Sur échec/annulation, `ItemsCreated = ItemsUpdated = 0` et `ItemsIgnored = ItemsFound`
signifie que tout le lot a été rejeté. Ainsi, pour chaque exécution terminale,
`ItemsFound = ItemsCreated + ItemsUpdated + ItemsIgnored`.

Les timestamps sont UTC et `FinishedAt >= StartedAt`. `ErrorMessage` contient un
code contrôlé, sans données sensibles du lot. Les logs structurés portent workspace,
recherche et exécution ; les contenus de résultats ne sont pas journalisés par le service.

Une panne de connexion, un arrêt du processus ou l'impossibilité d'écrire l'exécution
elle-même peut empêcher la conservation du journal d'échec. La transaction protège
toujours contre les écritures métier partielles ; un commit dont l'accusé de réception
est perdu peut avoir abouti. Un rejeu permet de retrouver les identités persistées.

## Vérification et limites

`IngestionTests` teste les routes HTTP sur PostgreSQL 18 jetable : mappage, rejeu,
archives et champs utilisateur, toutes les identités et conflits, normalisation,
références et isolation, lots invalides, échec de provenance ou de finalisation,
compteurs, concurrence HTTP et revalidation après attente. Les contrôles EF restent
actifs. Aucun modèle, snapshot ou migration n'est modifié.

Le premier parcours privilégie une décision déterministe sur des lots bornés. Il
charge les opportunités/provenances du workspace en mémoire ; les index normalisés
et l'optimisation pour des volumes importants restent à étudier. Les noms de société
libres ne sont pas conservés. L'historique détaillé des observations, les adaptations
de provenance multi-recherches et la coordination avec les anciens CRUD restent
des sujets de Phase 6.2. Aucun Worker, queue, planification, réseau, n8n, retry
automatique, scoring, IA, email ou automatisation métier n'est ajouté.

```powershell
dotnet test ProspectionCrm.slnx --configuration Release --filter FullyQualifiedName~IngestionTests
dotnet build ProspectionCrm.slnx --configuration Release
dotnet test ProspectionCrm.slnx --configuration Release
```
