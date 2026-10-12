# Exploitation locale de n8n

## Périmètre et invariants

Image conservée : `docker.n8n.io/n8nio/n8n:2.40.7`. Aucun connecteur CRM,
webhook métier, changement EF Core ou accès à la base CRM n'est ajouté.
Ne pas remplacer `.env`, les User Secrets, le volume existant ou la clé n8n.
Une sauvegarde et une restauration ne doivent jamais servir à réinitialiser
PostgreSQL. Les procédures destructives du guide d'infrastructure ne font pas
partie de cette étape.

Le réseau Compose par défaut reste partagé avec PostgreSQL. Les ports publiés
restent `127.0.0.1:5432` et `127.0.0.1:5678`. Cette restriction concerne les
connexions via l'hôte, pas celles entre conteneurs. Aucun identifiant PostgreSQL
n'est transmis à n8n. Aucun élargissement du binding API n'est nécessaire ici.

## Vérifier sans exposer les secrets

Depuis la racine du dépôt, avec le même nom de projet Compose que l'installation :

```powershell
docker info --format '{{.ServerVersion}}'
docker compose config --quiet
docker compose --profile integrations config --quiet
docker compose --profile integrations ps -a
docker volume ls --format '{{.Name}}'
```

Contrôler le code de sortie de chaque commande. Si Docker est inaccessible,
ne pas lancer de reset, de purge, de suppression de volume ni de réinstallation.
Consigner le blocage et reprendre après remise à disposition du moteur.

Ne pas publier `docker inspect` complet, `docker compose config` sans filtre,
les variables d'environnement, le fichier `config` n8n, les exports de credentials
ou les logs bruts. Pour inspecter la configuration effective, charger le JSON
Compose en mémoire et ne restituer que image, ports, volumes, réseau et restart.

Identifier le volume via les mounts du conteneur, sans supposer son préfixe.
Vérifier son label `com.docker.compose.volume=n8n_data` et qu'aucun autre
conteneur actif ne l'utilise. Avant tout démarrage d'une ancienne instance,
inspecter hors ligne les nombres de workflows, credentials et exécutions :
un workflow actif pourrait reprendre un traitement ou déclencher un appel externe.
Ne pas afficher les noms, corps, tokens ou valeurs de clé.

Pour SQLite, monter le volume en lecture seule dans un conteneur auxiliaire
de la même image, avec `--network none`, et utiliser `node:sqlite` avec
`DatabaseSync(path, {readOnly: true})`. Ne faire que des lectures, par exemple
`SELECT COUNT(*) FROM workflow_entity` et `credentials_entity`. Une erreur
d'ouverture n'autorise ni réparation ni suppression de WAL/SHM. Les fichiers
SQLite annexes doivent rester avec la base. Cette technique a été vérifiée
sur l'image locale 2.40.7 pendant l'étape 9.2.1.

## Politiques retenues

- `restart: on-failure:3` : reprise bornée en cas de sortie non nulle, sans
  lancement automatique au redémarrage du daemon. `unless-stopped` serait
  possible pour un service permanent, mais moins adapté à ce profil facultatif.
  Docker autorise au plus trois tentatives automatiques de reprise sur échec ;
  ce n'est ni trois tentatives par workflow ni une garantie de disponibilité.
  Une sortie 0 ne déclenche pas de reprise. Un arrêt manuel suspend la reprise ;
  les démarrages explicites restent possibles indépendamment du profil.
- Un conteneur unhealthy dont le processus reste vivant n'est pas redémarré
  par cette politique ; examiner le diagnostic avant intervention.
- Le profil `integrations` sélectionne les services lors des commandes Compose.
  Il n'arrête pas n8n déjà lancé et ne désactive pas sa politique de reprise.
  Sans activation du profil ni ciblage explicite de n8n, un démarrage Compose
  ordinaire sélectionne PostgreSQL seulement (vérifier aussi `COMPOSE_PROFILES`).
- Télémétrie, templates et notifications de mise à jour distants désactivés.
  Cela n'interdit pas les accès réseau des workflows ; aucun workflow métier
  ne doit être activé dans cette étape.
- Rétention explicite des exécutions : 336 heures, 10 000, pruning activé.
  Une des limites suffit à rendre l'historique éligible ; la purge est différée
  et certaines exécutions sont exclues. Ces valeurs ne garantissent ni une
  limite stricte de taille disque ni une durée minimale de conservation.
  Dans l'image 2.40.7, les statuts `new`, `running`, `waiting` et les exécutions
  annotées sont exclus de la sélection de purge. Les valeurs par défaut incluent
  un buffer de sécurité de 1 heure, un intervalle de suppression logique de
  60 minutes et de suppression physique de 15 minutes. La suppression physique
  concerne aussi les données associées ; le fichier SQLite ne rétrécit pas
  nécessairement, son espace libéré pouvant être réutilisé.
  Aucun VACUUM ni nettoyage manuel n'est prévu. Les réglages d'enregistrement
  propres aux workflows seront examinés avant de recevoir des données réelles.

## Sauvegarde à froid

1. Identifier le conteneur, son image exacte, son volume, sa politique de
   redémarrage et son état initial. Déterminer si la clé est dans le volume
   ou fournie extérieurement, sans l'afficher.
2. Planifier un arrêt de n8n seulement s'il fonctionne. Un arrêt est nécessaire
   ici pour une copie cohérente de SQLite, de ses fichiers WAL/SHM éventuels,
   de la configuration et des données binaires. Ne pas arrêter PostgreSQL.
3. Vérifier l'arrêt effectif et l'absence d'autre processus écrivant ce volume.
4. Monter le volume source **readonly** dans un conteneur auxiliaire de la même
   image, réseau désactivé. Archiver tout son contenu avec `tar`, y compris les
   fichiers cachés, dans un nouveau fichier daté ; ne jamais écraser une archive.
   Utiliser `umask 077`, préserver les droits et vérifier le code retour.
5. Vérifier que l'archive est lisible et contient la base et la configuration,
   sans afficher leur contenu. Conserver une empreinte de l'archive pour les
   transferts. Un export JSON des workflows n'est pas une sauvegarde complète.
6. Si la clé est extérieure au volume, la conserver séparément dans un stockage
   de secrets protégé. Ne jamais générer une nouvelle clé pour remplacer celle
   associée aux credentials sauvegardés.
7. Ne relancer que les services qui doivent fonctionner après l'intervention.

Le dossier `/backups/` à la racine du dépôt est ignoré par Git, mais cela ne le
chiffre pas. Les archives contiennent la clé et potentiellement des données
personnelles : limiter les accès locaux, utiliser un stockage chiffré approprié
avant transfert et définir leur durée de conservation. Ne pas les joindre à
un ticket, un commit ou un rapport. Une copie sur le même disque protège d'une
erreur locale, pas d'une panne du disque.
Sous Docker Desktop avec une destination Windows, `umask 077` ne remplace pas
la vérification des ACL du dossier hôte. Le fait que Git ignore un fichier
ne garantit pas sa confidentialité et ne protège pas un ajout forcé (`git add -f`).

## Restauration et installation vierge isolées

Aucune restauration ne doit cibler les volumes réels pendant cette phase.
Les essais utilisent exclusivement des données synthétiques, de nouveaux volumes
nommés et étiquetés, et l'image 2.40.7 déjà présente (`--pull never` si applicable).
Vérifier l'absence des noms avant création et arrêter si un nom est déjà utilisé.

1. Démarrer n8n sur un volume vierge avec `--network none`, sans publier de port,
   sans credentials ni workflow et avec les services distants désactivés.
2. Vérifier `/healthz/readiness` depuis ce conteneur avec Node ; ne pas utiliser
   la readiness comme preuve d'une authentification ou d'un connecteur métier.
3. Arrêter ce conteneur puis sauvegarder son volume à froid.
4. Extraire l'archive de test dans un **second volume neuf et vide**, jamais dans
   le volume source ou celui de développement. Préserver les droits/UID/GID.
   Un volume neuf peut appartenir à root : l'utilisateur `node` ne suffit pas
   pour restaurer toute l'arborescence. Pour cette extraction seulement, utiliser
   un auxiliaire `--user 0 --cap-drop ALL --cap-add CHOWN --cap-add FOWNER
   --cap-add DAC_OVERRIDE`, réseau désactivé, archive montée readonly, et
   `tar -xpf`. Le service n8n reste exécuté avec son utilisateur normal.
   Vérifier que la destination est vide avant extraction. Après un échec partiel,
   conserver le volume pour diagnostic et recommencer dans un autre volume neuf,
   sans supprimer ni écraser les fichiers déjà extraits.
5. Comparer les fichiers restaurés et la clé à la source en mémoire, en affichant
   seulement le résultat d'égalité. Vérifier l'intégrité SQLite en lecture seule.
6. Démarrer un second n8n isolé sur ce volume restauré ; contrôler sa readiness,
   puis l'arrêter. Ne pas remplacer la clé lors du démarrage.

Sans port publié, ces essais ne perturbent ni 5678 ni 5432 et ne donnent aucun
accès aux fournisseurs. Les volumes de test sont conservés dans cette étape :
leur suppression ultérieure demande une décision distincte. Un essai vierge
valide le mécanisme de restauration, pas le déchiffrement de credentials réels.

## Démarrage et retour à l'état initial

Un démarrage de n8n existant ne doit intervenir qu'après examen des workflows
et sauvegarde si une modification du conteneur est nécessaire. Pour appliquer
la configuration n8n sans recréer PostgreSQL :

```powershell
docker compose --profile integrations up -d --no-deps --pull never n8n
docker compose --profile integrations ps -a
```

Après contrôle, arrêter seulement les services démarrés pour la vérification
s'ils étaient arrêtés initialement. Ne pas exécuter `down --volumes`, `volume rm`
ou `prune`. Garder le projet et les noms de volumes identiques.

## Références officielles

- [Redémarrage Docker](https://docs.docker.com/engine/containers/start-containers-automatically/)
- [Rétention des exécutions n8n](https://docs.n8n.io/hosting/scaling/execution-data/)
- [Clé de chiffrement n8n](https://docs.n8n.io/hosting/configuration/configuration-examples/encryption-key/)
- [Désactivation des services distants n8n](https://docs.n8n.io/hosting/configuration/configuration-examples/isolation/)
