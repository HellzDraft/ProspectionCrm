# Infrastructure locale

Pour l'installation de l'API, les secrets, les migrations et les tests, commencer
par le [README](../README.md). Cette page détaille les opérations Docker locales.

Exécuter les commandes depuis la racine du dépôt, avec Docker démarré et
un fichier `.env` local configuré à partir de `.env.example`.
Ne pas remplacer un `.env` existant : changer les variables `POSTGRES_*`
ne change pas les identifiants d'une base déjà initialisée.

## Services

- PostgreSQL 18 : `127.0.0.1:5432`, volume `postgres_data` existant.
- n8n 2.40.7 : profil facultatif `integrations`, interface sur
  <http://localhost:5678>, volume `n8n_data` monté dans `/home/node/.n8n`.
- API ASP.NET Core et Blazor : exécutés hors Docker, inchangés.

n8n utilise SQLite par défaut. Sa base et sa clé de chiffrement générée
automatiquement restent dans son volume. Aucune variable supplémentaire
n'est nécessaire dans `.env.example` ; aucun secret n8n n'est versionné.
Créer le compte propriétaire dans l'interface lors du premier accès.
Les cookies non sécurisés sont autorisés uniquement pour cet usage HTTP local.
Cette configuration n'est pas destinée à une exposition publique.

n8n ne reçoit aucun identifiant PostgreSQL et n'a aucune connexion configurée
vers la base métier. Les deux services partagent le réseau Compose par défaut :
il ne s'agit pas d'une isolation réseau entre eux. Aucun workflow, provider
ou webhook CRM n'est configuré, et aucun service ne dépend de n8n.

La version 2.40.7 a été vérifiée le 28 septembre 2026 :
[release officielle stable](https://github.com/n8n-io/n8n/releases/tag/n8n%402.40.7)
et [canal npm stable](https://registry.npmjs.org/-/package/n8n/dist-tags).
Le healthcheck appelle `/healthz/readiness` avec Node fourni par l'image ;
il attend notamment que la base interne soit prête.

L'image peut avertir que Python est absent et que le mode de runner interne
sera supprimé dans une future version. Ces messages ne bloquent pas l'interface.
Aucun runner externe ni workflow Python n'est configuré dans cette étape.

## Démarrage et vérification

```sh
# PostgreSQL seul (ne démarre pas n8n)
docker compose config --quiet
docker compose up -d

# PostgreSQL + n8n
docker compose --profile integrations config --quiet
docker compose --profile integrations up -d

# État des deux services, y compris ceux arrêtés
docker compose --profile integrations ps -a
```

Le profil ne coupe pas un n8n déjà démarré. Pour revenir à PostgreSQL seul :

```sh
docker compose --profile integrations stop n8n
```

## Arrêt et redémarrage sans perte de données

```sh
docker compose --profile integrations stop
docker compose --profile integrations up -d

# Autre possibilité : retirer les conteneurs et le réseau, garder les volumes
docker compose --profile integrations down
```

Ne pas ajouter `--volumes` à un arrêt normal. Garder le même nom de projet
Compose permet de réutiliser les mêmes volumes au prochain démarrage.

## Réinitialisations destructives — uniquement sur décision explicite

**DESTRUCTIF : toutes les données PostgreSQL ET n8n sont supprimées.**
Sauvegarder les données nécessaires avant cette commande :

```sh
docker compose --profile integrations down --volumes
```

**DESTRUCTIF pour n8n uniquement :** comptes, credentials, workflows et clé
de chiffrement n8n sont perdus. PostgreSQL reste intact et peut rester démarré.
Dans PowerShell, récupérer le volume réellement monté avant de retirer le
conteneur. Si celui-ci est absent, le créer d'abord :

```powershell
docker compose --profile integrations create n8n
```

Puis identifier et vérifier le volume avant la suppression :

```powershell
$n8nContainer = docker compose --profile integrations ps -a -q n8n
if (-not $n8nContainer) { throw 'Conteneur n8n absent.' }
$n8nVolume = docker inspect $n8nContainer --format '{{range .Mounts}}{{if eq .Destination "/home/node/.n8n"}}{{.Name}}{{end}}{{end}}'
if (-not $n8nVolume) { throw 'Volume n8n introuvable.' }
$volumeRole = docker volume inspect $n8nVolume --format '{{index .Labels "com.docker.compose.volume"}}'
if ($volumeRole -ne 'n8n_data') { throw 'Le volume identifié ne correspond pas à n8n_data.' }
docker compose --profile integrations stop n8n
docker compose --profile integrations rm -f n8n
docker volume rm $n8nVolume
docker compose --profile integrations up -d n8n
```

Ne jamais supprimer `postgres_data` pour réinitialiser n8n.

## Reconstruire sans toucher aux données de développement

Un test sur une base vierge doit utiliser un autre projet Compose (`-p`), des
volumes distincts et un port hôte libre. Le seul changement de nom de projet ne
suffit pas : le port `5432` du fichier principal resterait en conflit.

Pour remplacer ce port avec un fichier de surcharge temporaire, Compose 2.24.4+
permet `ports: !override` (une fusion ordinaire ajouterait un port). Par exemple :

```yaml
services:
  postgres:
    ports: !override
      - "127.0.0.1:0:5432"
```

Le port `0` demande un port libre à Docker ; le récupérer avec `docker compose`
et les mêmes options `-p` / `-f`, suivi de `port postgres 5432`. Utiliser des
identifiants temporaires via un `--env-file` extérieur au dépôt et injecter la
connexion isolée dans `ConnectionStrings__DefaultConnection` pour EF et l'API.
Ne pas modifier les User Secrets ni le `.env` de développement.

Vérifier la configuration résolue et les noms des volumes avant le démarrage.
Après validation, arrêter l'API temporaire et PostgreSQL temporaire. Le nettoyage
avec `down --volumes` est **destructif pour le projet ciblé** : réutiliser exactement
le nom du projet temporaire et ses fichiers Compose, après contrôle de ses volumes.
Ne jamais exécuter cette commande sans ce ciblage pour nettoyer un test isolé.

Une base migrée est vide de données métier. Le bootstrap explicite
`POST /api/setup/bootstrap`, décrit dans le README, crée uniquement le propriétaire
local V1 et son workspace actif. Aucun jeu de validation existant n'est recréé ici.
Les tests automatisés de Phase 4 utilisent Testcontainers et leurs propres
instances PostgreSQL jetables, sans passer par le Compose de développement.
`Phase4ReconstructionTests` vérifie une base sans table applicative, l'application
de toutes les migrations, le démarrage de l'API sans bootstrap implicite, puis le
parcours bootstrap → pipeline/étapes → ordre/défaut → Opportunity → archives →
clonage → export/import et isolation workspace. La commande ciblée figure dans
le README. Aucun volume ni identifiant de développement n'est utilisé.
