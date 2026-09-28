# Infrastructure locale — Phase 3.2

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
conteneur (si nécessaire, créer celui-ci avec `docker compose --profile
integrations create n8n`) :

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
