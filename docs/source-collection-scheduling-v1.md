# Planification des collectes V1 — Phase 7.4

## Modèle et API

Une SavedSearch possède au maximum une `SourceCollectionSchedule`. Une absence
de configuration signifie non planifiée. L'entité dédiée évite de modifier les
colonnes historiques et isole la configuration du mécanisme de collecte.
Elle contient SavedSearchId (PK), WorkspaceId, PipelineId, PipelineStageId,
Enabled, DailyUtcMinute (0–1439) et NextCollectionAt (timestamp with time zone).
Les FK composées garantissent le Workspace de la recherche et du pipeline et
l'appartenance de l'étape au pipeline. Toutes utilisent Restrict.

Après création de la recherche avec les routes existantes, configurer :

```http
PUT /api/saved-searches/{id}/schedule
Content-Type: application/json

{
  "enabled": true,
  "dailyUtcTime": "09:30",
  "pipelineStageId": "<guid>"
}
```

Réponse 204, 404 si recherche absente/hors Workspace, 400 si configuration
invalide. `enabled` est obligatoire. Activation : heure strictement `HH:mm`
en UTC et étape obligatoires. Les secondes, offsets, chaînes cron, propriétés
inconnues et date fournie par le client sont rejetés. Aucun appel réseau.
Workspace, SavedSearch, pipeline, étape et SourceConfiguration doivent être
actifs/non archivés ; la source et la recherche doivent aussi être Enabled.
La source doit appartenir au même Workspace. L'adaptateur, ses paramètres et
l'URL sont validés seulement au traitement par le Worker.

Une sélection de Workspace indisponible (aucun ou plusieurs actifs) retourne
également 400 avec un message contrôlé. Les erreurs de binding/validation du
body retournent `InvalidRequest`, sans recopier les propriétés ou valeurs rejetées.
Une erreur technique inattendue retourne un ProblemDetails 500
`CollectionScheduleInternalError`. Les détails SQL et stack traces restent dans
les logs Serilog, même en Development.

Les anciens POST/PUT SavedSearch restent inchangés. Leurs DTO de lecture,
y compris la liste, ajoutent :

```json
{
  "schedule": {
    "enabled": true,
    "dailyUtcTime": "09:30",
    "pipelineStageId": "<guid>",
    "nextCollectionAt": "2026-10-08T09:30:00+00:00"
  }
}
```

Sans configuration : enabled=false, les trois autres champs null.
La configuration est indépendante du booléen métier `SavedSearch.Enabled`.
Il n'y a pas de route run-now : l'enqueue manuel Phase 7.1 demeure disponible.
Les routes suivent la sélection de Workspace locale V1 existante. Le scheduler
parcourt les Workspaces explicitement persistés, sans utiliser le fournisseur
de Workspace courant. Il ne suppose pas qu'un seul Workspace sera toujours actif.

## Désactivation et cycle de vie

```http
PUT /api/saved-searches/{id}/schedule
Content-Type: application/json

{ "enabled": false }
```

Cette forme ne reçoit ni heure ni étape. Elle conserve la configuration existante
mais met Enabled=false et NextCollectionAt=null ; sans configuration, c'est un
no-op. Aucun job queued/running n'est annulé ou supprimé.

Archiver la SavedSearch ou mettre son Enabled métier à false désactive aussi
la planification, dans la même transaction. Restaurer/réactiver la recherche
ne réactive pas sa planification. Une activation explicite avec heure et cible
recalcule une échéance future. Changer le pipeline via le PUT SavedSearch efface
la configuration de planning, car la cible précédente n'appartient plus au bon
pipeline. Les jobs historiques continuent à interdire ce changement comme avant ;
en cas de refus, toute la transaction, y compris la suppression du planning, est
annulée. Les modifications ordinaires de nom/URL/critères ne déplacent pas l'échéance.

Si une recherche due possède une ressource devenue inactive (Workspace, recherche,
pipeline, étape ou source), le scheduler désactive sa planification et met la date
à null, avec un warning structuré. Il ne crée pas de job et poursuit les autres
recherches. La remise en état seule ne réactive pas le planning. Les autres
ressources sont revalidées au moment du contrôle ; une modification après ce
contrôle reste possible et sera revalidée par le Worker avant collecte/ingestion.
Une archive directe en SQL est également détectée lors du prochain passage dû.

Les FK protègent les références, y compris sur une configuration désactivée.
Aucune suppression de job ou d'historique n'est ajoutée. Les routes actuelles
d'archivage ne font pas de suppression physique de SavedSearch.

## Calcul UTC et échéances manquées

`SourceCollectionScheduleCalculator` centralise le calcul. Pour l'instant UTC t
et la minute quotidienne m, construire minuit UTC du jour de t + m minutes.
Si cette date est strictement supérieure à t, la conserver ; sinon prendre le
lendemain à la même heure. À 09:30:00 exactement, un planning à 09:30 donne demain.
Aucune conversion dépendante du serveur ou du fuseau du Workspace n'intervient.
L'horloge PostgreSQL sert à la configuration et au déclenchement.

Une activation ou modification de l'heure/cible calcule une échéance future.
Un PUT identique sur un planning déjà actif conserve son échéance, même passée :
répéter une requête ne doit pas repousser une occurrence due.

Après redémarrage, une date passée produit **au maximum un job de rattrapage**.
La prochaine échéance est calculée directement dans le futur depuis l'heure
PostgreSQL après l'insertion, sans itérer sur les jours manqués. Elle avance au
commit du scheduler, indépendamment du futur résultat du Worker.

Si un job identique `(Workspace, SavedSearch, PipelineStage)` est queued/running,
l'occurrence est coalescée : aucun nouveau job, log informatif, échéance avancée
de la même façon. Cela inclut les jobs manuels et les retries en backoff. Un job
terminal ne bloque pas une nouvelle occurrence. Annuler un job déjà créé ne
ramène pas la prochaine échéance en arrière.

## Atomicité et concurrence

Chaque élément du batch utilise une transaction courte et indépendante :

1. sélection indexée des échéances dues, tri NextCollectionAt puis SavedSearchId ;
2. verrou de la SavedSearch avec `FOR NO KEY UPDATE OF s SKIP LOCKED` ;
3. relecture du planning et revalidation des ressources avec IDs explicites ;
4. INSERT scheduled/queued avec compteur 0 et dates PostgreSQL, ou coalescence ;
5. calcul puis persistance de la prochaine échéance, commit.

Les endpoints de configuration, modification et archivage sérialisent sur la
même ligne SavedSearch. Si le scheduler commit avant une désactivation, son job
reste créé ; si la désactivation commit d'abord, aucune occurrence n'est produite.
Le mode NO KEY UPDATE reste compatible avec les verrous KEY SHARE des FK d'un
enqueue manuel, tout en excluant les autres décisions de planning. Deux
schedulers ne consomment donc pas la même échéance. Le verrou est libéré au
commit/rollback et ne dépend pas de l'état mémoire d'une instance.

L'INSERT utilise `ON CONFLICT` ciblant uniquement l'index unique partiel Phase 7.1.
Une concurrence avec l'enqueue manuel ne devient pas une erreur fatale. La création
du job et l'avancement du planning sont indivisibles : une erreur avant commit
ne laisse pas de job orphelin ni de date avancée sans décision.

Une erreur technique par recherche est journalisée via ILogger/Serilog ; sa
transaction est annulée et cette recherche est exclue du reste du cycle. Les
autres recherches peuvent avancer. La date en échec reste due pour le prochain
cycle. Une panne empêchant même la sélection attend le prochain poll. Un grand
nombre d'erreurs persistantes peut réduire le débit du batch ; la V1 ne possède
pas de file de réparation ou d'alerte dédiée. Aucun détail technique ne figure
dans les DTO. Aucun réseau, DNS, parsing ou ingestion dans le scheduler.

## Options, DI et séparation des responsabilités

```json
"SourceCollectionScheduler": {
  "Enabled": false,
  "PollIntervalSeconds": 30,
  "BatchSize": 50
}
```

Validation au démarrage, même désactivé : poll 1–3600 secondes, batch 1–500.
Variables d'environnement : `SourceCollectionScheduler__Enabled`,
`SourceCollectionScheduler__PollIntervalSeconds`, `SourceCollectionScheduler__BatchSize`.
Options lues au démarrage. Aucune migration n'est appliquée automatiquement.

Le BackgroundService singleton crée un scope par cycle ; DbContext et service
de scheduling sont scoped. Chaque cycle traite au plus BatchSize recherches
puis attend PollIntervalSeconds, même si le batch était plein. L'annulation est
propagée et interrompt aussi l'attente ; une erreur ne termine pas la boucle.
Le délai de poll régule les lectures, il ne détient aucune échéance métier.

Scheduler crée des commandes ; Queue claim atomiquement ; Worker collecte et
ingère ; Retry reprend le même job avec AvailableAt persistant. Le TriggerTypeCode
scheduled reste identique sur toutes ses tentatives et SourceExecution. Il faut
activer séparément `SourceCollectionWorker:Enabled` pour consommer les jobs ;
activer seulement le scheduler les laisse queued et coalesce les occurrences suivantes.

## Migration et validation

`20261007094120_Phase74CollectionScheduling` ajoute uniquement une table vide,
ses contraintes et index. Les anciennes migrations sont inchangées. Aucune
SavedSearch ancienne n'est automatiquement planifiée, aucun job rétroactif créé.
Appliquer les migrations avec les services arrêtés avant de démarrer cette version.
Down supprime les configurations de planning uniquement. Jobs, tentatives,
SourceExecution et toutes les anciennes tables restent conservés. Un nouvel Up
recrée la table vide : les plannings perdus au downgrade nécessitent une nouvelle
configuration explicite.

Les tests couvrent calcul UTC/minute, API et cycle de vie, intégrité PostgreSQL,
upgrade 7.3 et Down/Up préservant les données, reconstruction vierge, EF sans
migration en attente ni divergence, coalescence, concurrence, redémarrage,
isolation des erreurs et Workspaces, puis la chaîne scheduler/Worker/RSS/ingestion
avec historique et retry scheduled. La suite des phases précédentes reste exécutée.

## Limites V1

Une heure quotidienne UTC, sans cron libre, fuseau horaire utilisateur ni
calendrier ouvré. Pas de n8n, Blazor, déclencheurs événementiels automatiques,
scraping authentifié, IA/scoring ou email. Pas de journal distinct des occurrences
coalescées : elles sont loguées. Pas de promesse de ponctualité à la seconde ;
le poll, les verrous et le backlog peuvent retarder la mise en file. Les limites
du Worker et de ses effets externes décrites dans la documentation jobs demeurent.
