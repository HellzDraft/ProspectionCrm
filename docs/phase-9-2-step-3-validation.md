# Phase 9.2 — Étape 3 : sauvegarde réelle et application des sessions

**État au 13 octobre 2026 : restauration réelle hors ligne et sessions 24/-1
validées ; reconnexion avec mot de passe et 2FA confirmée par l'utilisateur.**
Il s'agit d'un contrôle interactif sur le n8n réel, pas d'un test automatisé
ni d'une connexion sur une instance restaurée. La cause des deux écarts de mtime
a été reproduite, puis la finalisation des dates de répertoires a été vérifiée
sur un nouveau volume de contrôle. Le premier essai est conservé inchangé.
La restauration correspond exactement aux dates stockées dans le tar ; celui-ci
ne conserve que les secondes entières, pas les fractions de seconde de la source.
Cette précision est explicitement acceptée pour ce jalon local ; aucune
conservation intégrale de toutes les métadonnées d'origine n'est annoncée.
Voir le [diagnostic et la reprise conditionnelle](#diagnostic-mtime-et-reprise-conditionnelle-du-13-octobre-2026).
n8n a été recréé seul avec les deux paramètres, vérifié puis arrêté. Après la
confirmation humaine, une inspection readonly distincte constate les deux services
arrêtés, code 0 ; n8n porte de nouveaux horodatages, PostgreSQL conserve les siens.
Voir la [confirmation et l'état final observé](#confirmation-humaine-et-contrôle-documentaire-final).
Aucun démarrage de n8n sur les copies de contrôle.
Le script opérateur a déjà réussi : ne pas le relancer. La capture n'a pas été
recréée. L'historique de l'interruption et de la correction du cache est conservé.
Aucun staging, commit ou push. La Phase 9.2 reste ouverte et la Phase 9.3 n'est
pas commencée.

Les sections suivantes jusqu'à « Reprise du 13 octobre » conservent le compte
rendu historique de la préparation du 12 octobre, qui s'était arrêtée avant
capture. Leurs prérequis en attente et l'avertissement de ne pas lancer le script
sont remplacés par les statuts de reprise successifs ci-dessous. Les commandes
opérateur présentées comme prêtes lors des étapes antérieures sont désormais
historiques et ne doivent pas être relancées sur ce dépôt initialisé.
Les constats de blocage de la première extraction et d'attente de reconnexion
restent également historiques. Le diagnostic consigne la résolution et
l'application effective ; la dernière section ajoute la confirmation humaine
et l'état des services observé ensuite, sans réécrire les contrôles antérieurs.

## Référence et état initial

- Branche `main`, HEAD `dcf3ac283c1f8e17f6d7aa6105b2b0a86d1af983`.
- Index vide et working tree propre au début de cette intervention.
- CI Build #56 réussie selon la confirmation de l'utilisateur ; pas de nouveau
  déclenchement ni de contrôle distant requis pour cette préparation locale.
- Compose, [exploitation](n8n-operations.md), [sauvegardes chiffrées](n8n-encrypted-backups.md)
  et [rapport 9.2.2](phase-9-2-step-2-validation.md) examinés. Les preuves et
  rapports historiques restent inchangés.

État effectivement relevé, sans supposer que les services étaient encore arrêtés :

| Service | Conteneur / ID complet | État initial |
| --- | --- | --- |
| n8n | prospectioncrm-n8n-1 / 920fb3c09006f4e7a8b961d7cb7c31be0a2dfbae123a03dc80323c04269cff70 | exited, sortie 0 |
| PostgreSQL | prospectioncrm-postgres-1 / 21c4200431ecb101f142eeddde520a8fb421bb60344e91907bcf59576f874300 | exited, sortie 0 |

Projet Compose existant : `prospectioncrm`, répertoire `F:\Projets\ProspectionCrm`,
fichier déclaré par les conteneurs : `compose.yml`. Aucun fichier override standard
trouvé ; `COMPOSE_FILE` et `COMPOSE_PROJECT_NAME` absents de l'environnement de
la session. Le projet résolu correspond aux labels des conteneurs.

Volume réel n8n : `prospectioncrm_n8n_data`, monté sur `/home/node/.n8n`.
Le seul conteneur le référençant est le n8n réel arrêté ; aucun writer actif
identifié via Docker. Aucun montage Windows direct du volume n'est effectué.
PostgreSQL conserve `prospectioncrm_postgres_data` et n'a fait l'objet d'aucun
démarrage, arrêt ou accès SQL.

Image n8n du conteneur et du tag local : `docker.n8n.io/n8nio/n8n:2.40.7`, même ID
`sha256:ffeb52485f78b1b06c9a832205853cf75da72a07a514c9a27724df85979d6c34`.
Aucun pull ou changement de version.

## Contrôles hors ligne exécutés par Codex

Auxiliaire de cette même image, `--pull never --network none --read-only
--cap-drop ALL --security-opt no-new-privileges`, volume réel monté readonly.
Lecture SQLite avec `DatabaseSync(..., {readOnly: true})`, requêtes agrégées
uniquement. Le fichier config a servi à vérifier en mémoire la présence d'une
clé, sans l'afficher ni en publier une empreinte.

| Métadonnée | Résultat observé |
| --- | --- |
| Propriétaires | 1, aucun désactivé |
| userManagement.isInstanceOwnerSetUp | true |
| Propriétaires avec MFA activée | 1 |
| Workflows / credentials / exécutions | 0 / 0 / 0 |
| Workflows actifs / publiés | 0 / 0 |
| Exécutions new/running/waiting | 0 |
| Clé n8n dans le volume | Présente |
| Surcharge N8N_ENCRYPTION_KEY ou N8N_ENCRYPTION_KEY_FILE | Absente de la configuration du conteneur |
| Fichiers réguliers / taille cumulée | 9 / 5 692 203 octets |
| Types de fichiers inattendus | 0 |

L'initialisation du propriétaire, la 2FA et la reconnexion restent des validations
interactives **rapportées par l'utilisateur**. Les agrégats ci-dessus corroborent
l'état propriétaire/MFA sans constituer une connexion automatisée. Aucun email,
hash de mot de passe, secret MFA, code de récupération ou credential extrait.

L'auxiliaire readonly ponctuel a été retiré par `--rm`. Aucun volume créé ou
supprimé, aucune instance n8n supplémentaire démarrée. Ces observations devront
être reconfirmées avant une future capture/recréation si l'utilisateur utilise
l'instance entre-temps.

## Vérification du stockage proposé

Chemin demandé : `F:\Sauvegardes\ProspectionCrm\n8n`, hors dépôt Git.

- F: est un disque local fixe NTFS ; 172 935 938 048 octets libres au contrôle
  (environ 161 Gio), largement suffisants pour les copies de la taille observée.
- `F:\Sauvegardes` et ses descendants proposés sont absents ; aucune collision.
  Aucun lien/jonction identifié dans les composants existants du chemin.
- Aucun recouvrement avec les racines OneDrive/SyncRootManager recensées
  (deux entrées examinées) ; aucun client de synchronisation courant parmi
  OneDrive, Dropbox, GoogleDriveFS, Syncthing, Nextcloud, pCloud et Resilio observé
  en cours d'exécution. Cela ne constitue pas un inventaire exhaustif de tout
  logiciel ou tâche de sauvegarde tiers installé sur le PC.
- **Le partage administratif Windows `F$` couvre F:.** Aucun partage ordinaire
  couvrant la destination n'a été trouvé. L'absence absolue de partage demandée
  n'est donc pas établie ; ce fait n'est pas masqué sous la notion de disque local.

La confirmation d'acceptation de `F$` avec des ACL privées, ou le choix d'un autre
emplacement, a été demandée et reste en attente. Aucun partage, logiciel de
synchronisation, pare-feu, ACL du disque ou dossier parent existant modifié.
**La racine de sauvegarde F: n'a pas été créée ni utilisée.** Les ACL des futurs
dossiers de capture devront être posées et vérifiées avant toute écriture sensible.

## Restic et préparation du passage opérateur

Exécutable existant, sans téléchargement ou installation supplémentaire :
`%LOCALAPPDATA%\ProspectionCrmTests\phase922-60c6548b88e6\tool\portable\restic_0.19.1_windows_amd64.exe`.

Le ZIP dont la signature avait été vérifiée en 9.2.2 a toujours le SHA-256
`DA948AD707ED690426473AABA2046CD61F8F90F6F0E7DAB6BE0D5796531DE67D`.
L'exécutable est identique octet par octet à son entrée dans ce ZIP ; SHA-256 :
`B0DD1FD21EEA5D8FE1325F55F7118213C21F36DE8A261E04C0624A5AB9FD7830`.
Version effectivement exécutée : `restic 0.19.1 compiled with go1.26.4 on windows/amd64`.
La signature n'a pas été présentée comme une nouvelle vérification dans cette
intervention. Aucun volume, dépôt ou secret synthétique réutilisé ; seul l'outil
portable précédemment vérifié est retenu.

Mise à jour du 12 octobre 2026 : l'utilisateur confirme qu'il va préparer un mot
de passe réel long, unique, distinct de ses autres mots de passe, avec récupération
indépendante du PC et saisie uniquement dans son terminal personnel. Cette
stratégie de garde est confirmée ; sa mise en place effective reste à confirmer
avant la capture. Aucune valeur n'a été demandée ou fournie. La saisie
du secret ne doit pas transiter par les outils Codex. Le terminal personnel de
l'utilisateur est le point de passage prévu par la demande : aucun mécanisme
de transmission, fichier de mot de passe ou variable d'environnement n'est créé.

Un dossier contenant seulement du code opérateur sans secret a été créé hors Git :
`%LOCALAPPDATA%\ProspectionCrmOperations\phase923-20261012-151755-69bd483f2858`.
Il contient `operator-restic.ps1` et `README-operateur.md`. La DACL est protégée
contre l'héritage ; seuls l'opérateur courant et SYSTEM ont FullControl. Aucun
changement d'ACL sur un parent. Ici `phase923` désigne 9.2, étape 3, pas la Phase 9.3.

Racine prévue pour l'opération, **non créée** :
`F:\Sauvegardes\ProspectionCrm\n8n\operation-20261012-151755-69bd483f2858`.

| Emplacement prévu | Fonction | État actuel |
| --- | --- | --- |
| capture/n8n-post-init.tar | Archive réelle complète en clair | Absente |
| capture/manifest.json | Date UTC, image/ID, version restic, volume, référence, taille et SHA-256 | Absent |
| repository/ | Nouveau dépôt restic réel, format v2 | Non initialisé |
| restore/ | Nouvelle destination privée de vérification | Non créée |
| cache/ | Cache indépendant des sources | Non créé |
| evidence/ | Preuves non sensibles et résultat opérateur | Non créé |

Le script est préparé pour **la seule séquence restic**, sans capture Docker,
extraction de volume ni application de sessions. Il refuse les chemins absents,
les liens/jonctions, les ACL inattendues et toute collision de dépôt/destination.
Il n'utilise ni argument secret, ni fichier de mot de passe, ni secret synthétique.

Après levée des deux prérequis et création de la capture/manifeste par Codex,
commande exacte à exécuter depuis le terminal PowerShell personnel de l'utilisateur.
Elle utilise le chemin du PowerShell 7 existant vérifié, sans supposer que `pwsh`
figure dans le PATH du terminal personnel :

```powershell
& "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" -NoProfile -File "$env:LOCALAPPDATA\ProspectionCrmOperations\phase923-20261012-151755-69bd483f2858\operator-restic.ps1"
```

**Ne pas exécuter maintenant : la capture n'existe pas encore.** Le contrôle
préalable du script bloquera ; ne pas fabriquer un manifeste ou utiliser
l'archive historique à sa place. Aucune élévation ni changement de politique
d'exécution prescrit. En cas de restriction, consigner le blocage.

La séquence préparée initialise le dépôt une seule fois, sauvegarde exactement
le tar et le manifeste avec `--host prospectioncrm-local --tag n8n-cold
--group-by host,tags`, enregistre le snapshot, exécute `check --read-data`, trouve
le sous-dossier via `ls --json`, restaure le snapshot exact avec
`restore "${snapshotId}:$subfolder" --overwrite never --verify` dans une destination
neuve et compare les deux empreintes. Chaque code natif est contrôlé explicitement
via `$LASTEXITCODE`. Aucun résultat partiel n'autorise la suite, aucun nettoyage,
forget, prune, repair ou unlock forcé.

Résultats non sensibles à retourner après exécution : statut
`RESTIC_SEQUENCE_VERIFIED`, snapshot exact, codes backup/check/restore, empreintes
source/restaurée du tar et du manifeste. Le script écrit ces résultats dans
`evidence/operator-result.json`. Aucun mot de passe, archive, log brut ou contenu
SQLite/config à transmettre. En cas d'échec, retourner seulement étape et code.

## Statut des étapes suivantes

| Étape | Exécutant prévu | Statut réel |
| --- | --- | --- |
| Nouvelle capture post-initialisation | Codex après prérequis | Non réalisée |
| Chiffrement et contrôle restic | Utilisateur dans son terminal personnel | Non exécutés ; aucun snapshot réel |
| Restauration du tar/manifeste | Utilisateur, puis comparaison par Codex | Non réalisée |
| Extraction dans un nouveau volume réel de contrôle | Codex après résultat opérateur | Non réalisée ; aucun volume sensible créé |
| Fichiers, permissions, clé, SQLite, propriétaire/MFA restaurés | Codex hors ligne | Non contrôlés, faute de capture/restauration |
| Application des sessions au seul n8n réel | Codex après tous les contrôles | Non réalisée |
| Reconnexion après déconnexion explicite, mot de passe et 2FA | Utilisateur | En attente après application |
| Expiration après 24 h | Observation ultérieure | Non observée ; horloge inchangée |

Le futur volume de contrôle sera une **copie réelle sensible**, jamais présenté
comme synthétique. Aucun n8n ne sera démarré sur cette copie. L'extraction et les
contrôles readonly devront préserver la clé et vérifier les métadonnées sans
exporter ou déchiffrer de credentials. Aucun remplacement du volume source.

L'application future reste conditionnée à une capture complète, au succès restic,
à l'archive restaurée identique et aux contrôles hors ligne. À ce stade :

- Les paramètres 24/-1 sont déjà committés, mais absents de la configuration
  du conteneur réel. Celui-ci conserve ses anciens réglages.
- La comparaison des modèles Compose effectifs confirme exactement les deux
  ajouts de session par rapport à 9.2.1. Le hash Compose de cette configuration
  historique correspond au label du conteneur actuel.
- L'identité de l'image locale est confirmée. Ces contrôles seront rejoués avant
  recréation, avec l'identité du volume et l'absence de traitements à effet externe.
- Aucune commande `up`, `start`, `stop`, `down` ou recréation réelle exécutée.
  Aucune modification de propriétaire, MFA, cookies, clé ou secret JWT.
- La révocation globale des anciens jetons n'est pas démontrée. Une base active
  peut écrire normalement après démarrage : aucune égalité SQLite après démarrage
  ne sera promise.

## Contrôles finaux et réserves

Compose valide avec et sans `integrations`, codes retour 0. Les liens locaux et
ancres du rapport, sa présentation et la syntaxe PowerShell du script ont été
contrôlés. `git diff --check` réussi, index vide. Seul ce rapport est ajouté au
dépôt ; aucun script, secret, archive, binaire, cache ou dépôt restic dans Git.
`compose.yml`, les guides et les rapports historiques restent inchangés.

État final des deux services réels : identités et horodatages inchangés, toujours
arrêtés. Aucun état à rétablir. Les archives historiques et artefacts synthétiques
sont conservés. Seul le dossier privé de code opérateur hors Git a été créé ;
il ne contient aucune sauvegarde ni secret.

Les points de passage sont ceux de la demande utilisateur : garde du secret prête
avant capture ; absence de partage à clarifier face à `F$` ; saisie native restic
dans un terminal personnel. Aucun de ces prérequis n'est remplacé par une
supposition ou un mot de passe géré par Codex. Aucun refus automatique d'autorisation
n'a été reçu pendant cette préparation.

La sauvegarde locale chiffrée, lorsqu'elle sera réalisée, ne constituera pas une
copie protégée hors machine ; cette destination reste non choisie. Aucun transfert
hors machine, aucun travail CRM/EF, réseau/TLS/authentification de Phase 9.3.

**INTERVENTION PARTIELLE — ÉTAPE OPÉRATEUR OU BLOCAGE IDENTIFIÉ**

## Reprise du 13 octobre 2026 — capture réelle vérifiée

L'utilisateur confirme le mot de passe réel long et unique, enregistré dans
Bitwarden, récupérable depuis son téléphone indépendamment du PC, avec fiche de
secours papier protégée. Sa valeur n'a été ni demandée, ni reçue, ni générée par
Codex. Saisie exclusivement dans son terminal personnel. L'utilisateur accepte
le seul partage administratif `F$` existant avec les ACL privées prévues ; aucune
autorisation de nouveau partage, d'élargissement d'accès ou de modification réseau.

Docker Desktop était arrêté au début de cette reprise. Il a été lancé normalement,
sans élévation, reset, mise à jour ou changement de configuration. Docker 29.6.1
est ensuite accessible. Les deux conteneurs réels sont toujours arrêtés ; leurs
identités, images, volumes et horodatages correspondent à la préparation précédente.
Aucune commande de démarrage/arrêt/recréation des services réels, aucun accès SQL
ou montage du volume PostgreSQL. Docker Desktop reste lancé.

Prérequis revérifiés :

- Compose valide avec et sans `integrations` ; projet, volume réel n8n et image
  exacte inchangés. Aucun conteneur actif ne référence le volume n8n avant et
  après chaque auxiliaire. Pas de surcharge externe de la clé n8n.
- F: fixe NTFS, 170 738 098 176 octets libres au contrôle initial ; aucun chemin
  cible préexistant, lien ou jonction. Deux racines de synchronisation examinées,
  aucun recouvrement ni client courant de synchronisation observé. Seul `F$`
  couvre la destination. Le chiffrement BitLocker n'a pas pu être vérifié avec
  les droits disponibles ; aucune affirmation de chiffrement du disque.
- ZIP restic et exécutable : empreintes historiques identiques ; exécutable
  identique à l'entrée du ZIP. Version 0.19.1 confirmée, aucun téléchargement.
- Racines nouvellement créées de `F:\Sauvegardes` à l'opération, puis dossiers
  `capture` et `evidence` : DACL protégées, opérateur et SYSTEM uniquement,
  posées à la création avant toute écriture sensible. ACL de chaque artefact
  contrôlée. Aucun dossier parent préexistant n'a vu ses ACL modifiées.

La racine réservée le 12 octobre est maintenant utilisée, sans collision :
`F:\Sauvegardes\ProspectionCrm\n8n\operation-20261012-151755-69bd483f2858`.
La date réelle de capture est inscrite en UTC dans le manifeste du 13 octobre.

| Résultat | Valeur |
| --- | --- |
| Statut | `COLD_CAPTURE_VERIFIED` |
| Vérification terminée | 2026-10-13T10:55:39.8394347Z |
| Archive | `capture/n8n-post-init.tar`, 5 702 656 octets |
| SHA-256 du tar | `EB716ED16DBC09C1CEF79ED4709CC7FB8F23ED232703FAA42880E9B991032E30` |
| SHA-256 de `capture/manifest.json` | `AD19972DCBB0266C7D0B7A9FFA14377AB4EDC84CDBCE4D5C28D64DFFBB009160` |
| Contenu | 9 fichiers réguliers, 3 répertoires, 5 692 203 octets de fichiers |
| SQLite | `integrity_check` réussi en lecture seule |
| Propriétaire | 1, initialisé, non désactivé, MFA activée |
| Workflows / credentials / exécutions | 0 / 0 / 0 |
| Clé dans le volume | Présente ; aucune valeur ou empreinte de clé publiée |
| Tar / source | Ensemble des chemins, octets, UID/GID, modes et dates de modification identiques |

La capture utilise l'image identifiée par son ID exact, réseau `none`, racine
readonly, capacités retirées, `no-new-privileges`, volume n8n readonly. `tar`
archive tout le volume, fichiers cachés compris, avec `umask 077` et création
exclusive par redirection `noclobber`. Aucun fichier existant écrasé.
Lecture complète du tar sans extraction ; chemins absolus, traversées, doublons,
liens et types inattendus refusés. Comparaison des empreintes de chaque fichier
avec le volume en mémoire seulement ; seules des preuves agrégées sont enregistrées.

Trois auxiliaires arrêtés avec code 0 sont conservés, sans `--rm`, conformément
à l'absence de suppression demandée : suffixes `audit`, `capture` et `verify`
des noms `prospectioncrm-923-20261013-…-69bd483f2858`. Aucun volume Docker créé.
Les fichiers réels restent en clair sous ACL privées à ce stade.

Preuves locales non sensibles dans `evidence/` : `services-before.json`,
`services-after-capture.json`, `capture-result.json`, `operator-preflight.json`.
Le manifeste inclut la provenance, les dates, versions, taille, empreinte du tar
et résultats de contrôle ; aucun contenu SQLite/config ni secret.

Le script opérateur existant a reçu un mode `-PreflightOnly`, qui retourne avant
toute saisie ou écriture, ainsi qu'un contrôle explicite des résultats du manifeste.
Sa syntaxe et le précontrôle ont réussi avec le PowerShell exact ci-dessous :
`OPERATOR_PREFLIGHT_VERIFIED`. Dépôt, restauration, cache et résultats opérateur
toujours absents, donc aucune collision. Aucun appel à restic `init`, `backup`,
`check` ou `restore` effectué par Codex.

**Commande désormais prête**, à exécuter uniquement dans le terminal PowerShell
personnel de l'utilisateur, sans `-PreflightOnly` :

```powershell
& "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" -NoProfile -File "$env:LOCALAPPDATA\ProspectionCrmOperations\phase923-20261012-151755-69bd483f2858\operator-restic.ps1"
```

Répondre `PRET` au contrôle de garde, puis saisir le secret aux invites natives
restic. Retourner uniquement le statut final et les résultats non sensibles de
`evidence/operator-result.json`, ou l'étape et le code en cas d'échec. Ne pas
relancer après un échec sans examiner le point d'arrêt et les collisions.

Les archives historiques et `compose.yml` sont inchangés, confirmé par SHA-256.
L'index Git reste vide. Aucun transfert externe, suppression, commit ou push.
Les réglages 24/-1 restent absents du conteneur n8n réel ; leur application
demeure conditionnée à la sauvegarde chiffrée, sa restauration et aux contrôles
hors ligne prévus sur un nouveau volume sensible isolé, sans démarrer n8n sur
cette copie. **En attente de l'étape opérateur ; sessions non appliquées.**

## Correction ciblée du cache après interruption restic

### Interruption et constat en lecture seule

Après `PRET`, l'utilisateur a interrompu par `Ctrl+C` la saisie à l'invite
`enter password for new repository:`. Messages rapportés :
`signal interrupt received, cleaning up` et
`Fatal: unable to read password: context canceled`.
Aucune valeur de mot de passe n'a été communiquée.

Le contrôle en lecture seule a confirmé l'arrêt des processus restic/opérateur,
les empreintes et ACL intactes, `repository` absent, `cache` existant mais vide
et privé, aucune restauration ni résultat opérateur. Les services réels étaient
arrêtés et inchangés. L'ancien précontrôle refusait le cache préexistant, même vide.
Il n'y avait aucun dépôt à réinitialiser ni capture à refaire.

### Correction autorisée et périmètre du diff

Correction du seul script local existant
`%LOCALAPPDATA%\ProspectionCrmOperations\phase923-20261012-151755-69bd483f2858\operator-restic.ps1`,
avec la même opération et les mêmes chemins. Copie du script avant correction
conservée dans le dossier de tests séparé cité ci-dessous.

- Le cache est retiré du refus inconditionnel de collision et soumis à son propre
  contrôle. Cache absent : création inchangée, puis validation. Cache existant :
  chemin exact, dossier ordinaire, absence de lien/jonction sur le chemin, vacuité
  incluant les éléments cachés/système et ACL privées opérateur/SYSTEM héritables.
- L'énumération du parent distingue l'absence effective d'une erreur. Les lectures
  utilisent `-Force -ErrorAction Stop`. Toute erreur ou ambiguïté bloque avec un
  diagnostic non sensible ; aucune réparation de droits ni suppression.
- Le cache est revérifié après `PRET`, avant tout `init`. Le résultat du précontrôle
  indique désormais `cacheState` et `cacheValidated`, sans prétendre le cache absent.
- L'avertissement demandé sur la saisie invisible et le collage unique depuis
  Bitwarden apparaît avant les invites restic.
- Pour respecter « sans lancer restic », `restic version` et son contrôle de code
  sont déplacés après la sortie `-PreflightOnly`. Ils sont conservés au lancement
  opérateur ; le SHA-256 du binaire reste contrôlé pendant le précontrôle.

Diff examiné : aucune modification des paramètres ou chemins de l'opération,
des fonctions de protection préexistantes, des vérifications capture/manifeste,
des empreintes ou de la séquence restic depuis `$common` jusqu'au résultat final.
Les refus d'un dépôt, d'une restauration, d'un résultat ou d'un checkpoint
préexistants sont conservés. Aucune reprise automatique ajoutée.

### Contrôles effectivement exécutés

**21 contrôles réussis** sur des fixtures séparées, conservées hors Git dans
`%LOCALAPPDATA%\ProspectionCrmTests\phase923-cache-precheck-20261013-7011b8d1116e`.
Ce dossier contient des tests de précontrôle, pas une nouvelle opération de
sauvegarde. Les tests chargent uniquement les fonctions et blocs concernés via
l'AST du script ; ils n'exécutent pas sa séquence opérateur.

| Contrôle | Résultat |
| --- | --- |
| Syntaxe PowerShell | Valide |
| Cache absent et création prévue | Accepté, création privée vérifiée sur fixture |
| Cache vide privé existant | Accepté sans modification |
| Fichier ordinaire, fichier caché/système ou sous-dossier dans le cache | Refusés |
| Chemin inattendu ou fichier à la place du dossier | Refusés |
| Jonction du cache ou d'un parent | Refusées |
| ACL sans SYSTEM ou non héritables | Refusées, sans correction automatique |
| Erreurs simulées de lecture du parent, du cache ou des ACL | Refusées, jamais assimilées à la vacuité |
| Collisions dépôt, restauration, résultat et checkpoint | Toujours refusées |
| Comparaison des protections et de la séquence restic avant/après | Identiques hors correction décrite |
| Invocation restic avant sortie `-PreflightOnly` | Aucune, confirmé par examen de l'AST |

Précontrôle réel exécuté avec le PowerShell exact de la commande opérateur et
`-PreflightOnly`, code de sortie 0, à `2026-10-13T11:34:23.1687305Z` :
`OPERATOR_PREFLIGHT_VERIFIED`, `cacheState=empty-private-directory`,
`cacheValidated=true`, dépôt/restauration absents, aucune saisie ni écriture.
Comparaison des 10 éléments de l'opération réelle avant/après : mêmes fichiers,
empreintes, attributs, dates de modification et ACL, y compris le cache vide.
Les tests n'ont écrit ni dans la capture, ni dans son cache, ni dans les destinations
de dépôt/restauration. Aucun binaire restic exécuté pendant cette correction.

Preuves : `test-results.json`, `real-preflight-result.json`,
`operator-restic.before.ps1`, `operator-restic.diff` et le script de tests dans
la racine de tests ci-dessus. Les preuves historiques de l'opération restent intactes.
SHA-256 du script corrigé :
`FCEBFD557AC185E912AC32E643C0B5D5B103AF45AE16A739101313BE945853E0`.

La capture de 5 702 656 octets est toujours utilisable ; ses empreintes et celles
du manifeste restent celles du tableau de capture. La commande opérateur publiée
plus haut reste exactement la même, sans `-PreflightOnly` pour le lancement par
l'utilisateur. Saisie uniquement dans son terminal personnel, aux invites restic.
Aucune manipulation de volume/conteneur, application de sessions, suppression
d'éléments existants, transfert externe, staging, commit ou push. Phase 9.3 non
commencée. **Chiffrement et restauration toujours en attente de l'opérateur.**

## Reprise après succès opérateur — contrôle hors ligne bloquant

### Résultats exécutés par l'utilisateur

Le 13 octobre 2026, l'utilisateur a exécuté le script opérateur corrigé dans son
terminal personnel, avec saisie native du secret. Son résultat transmis est
retrouvé à l'identique dans `evidence/operator-result.json` :

| Résultat opérateur | Valeur |
| --- | --- |
| Statut | `RESTIC_SEQUENCE_VERIFIED` |
| Date UTC | 2026-10-13T11:38:19.6745166Z |
| Version restic | 0.19.1 |
| Snapshot exact | `11163be51570338223c6db292896ea6be5dc2d7aff01ded6ea523c84aaab5ec5` |
| Codes backup / check --read-data / restore | 0 / 0 / 0 |
| Tar source et restauré | SHA-256 `EB716ED16DBC09C1CEF79ED4709CC7FB8F23ED232703FAA42880E9B991032E30` |
| Manifeste source et restauré | SHA-256 `AD19972DCBB0266C7D0B7A9FFA14377AB4EDC84CDBCE4D5C28D64DFFBB009160` |

Ces commandes restic sont celles de l'utilisateur, pas de Codex. Le checkpoint
`operator-snapshot.json` porte le même snapshot ; son champ `not-yet-run` décrit
l'instant du checkpoint avant `check`, et le résultat final consigne la réussite
de la suite. Codex n'a relancé ni le script ni restic et n'a demandé aucun secret.
Le script opérateur conserve son SHA-256 après correction du cache.

### Contrôles de reprise par Codex

Branche `main`, HEAD `dcf3ac283c1f8e17f6d7aa6105b2b0a86d1af983`, index vide.
Le rapport local attendu est conservé. Aucun changement métier ou migration.
Les services réels ont les mêmes IDs et horodatages qu'avant la capture et sont
tous deux arrêtés, sortie 0. Aucun processus restic/opérateur actif identifié.

Empreintes recalculées sur les quatre fichiers source/restaurés : conformes au
tableau ci-dessus. Contrôle initial de 295 éléments dans la racine d'opération :
ACL privées opérateur/SYSTEM, aucune redirection par lien/jonction. Le dépôt
contient sa configuration et un fichier snapshot ; aucun fichier de verrou
observé. Aucun accès authentifié au dépôt n'était nécessaire. `F$` reste le seul
partage couvrant la destination ; aucun partage, réseau ou ACL parent modifié.

Image du conteneur et tag local identiques à l'ID épinglé du manifeste. Projet
Compose `prospectioncrm`, volume `prospectioncrm_n8n_data`, provenance Compose
`n8n_data` et pilote local confirmés. Aucun utilisateur actif du volume via Docker.

Comparaison des modèles effectifs en mémoire : l'environnement cible ne diffère
que par les ajouts `N8N_USER_MANAGEMENT_JWT_DURATION_HOURS=24` et
`N8N_USER_MANAGEMENT_JWT_REFRESH_TIMEOUT_HOURS=-1`. Le modèle cible privé de ces
deux entrées retrouve le hash du conteneur actuel
`103e09f4a1beb9378c767d2558523cee0b23c04b4d85729a90568512c3341edf`.
Hash cible : `0d60ad021b956a6d2ff0ffc32936306868ff0dd695a412318db3b91cd22ccde9`.
Les modèles complets et autres variables ne sont ni affichés ni enregistrés.
`compose.yml` reste inchangé, SHA-256 historique conservé.

### Extraction sensible et résultat hors ligne

Structure de l'archive restaurée examinée avant extraction : 12 entrées, dont
9 fichiers et 3 répertoires ; chemins absolus, traversées, doublons, liens et
types inattendus refusés. Aucun contenu sensible affiché.
Un auxiliaire readonly, sans réseau, a comparé le volume réel arrêté à cette
archive : tous les octets et métadonnées correspondent encore à la capture,
SQLite est intègre, aucun workflow, credential, exécution ou traitement en attente.

Après vérification de l'absence du nom, création du seul nouveau volume
`prospectioncrm_n8n_restorecheck_20261013_69bd483f2858`, étiqueté comme copie
**sensible de restauration réelle**, avec référence d'opération et snapshot.
Extraction dans ce volume neuf vérifié vide, archive montée readonly, réseau
`none`, image exacte, `--user 0 --cap-drop ALL --cap-add CHOWN --cap-add FOWNER
--cap-add DAC_OVERRIDE` et `no-new-privileges`. Le volume réel n'est pas monté
dans l'auxiliaire d'extraction. `tar -xpf` termine avec code 0.

La comparaison stricte de la copie extraite termine avec code 1. Le diagnostic
suivant est effectué en lecture seule, sans modifier la copie :

| Contrôle de la copie extraite | Résultat |
| --- | --- |
| Ensemble des entrées | 12 attendues / 12 présentes, aucune manquante ou supplémentaire |
| Contenu des 9 fichiers | Identique à l'archive, empreintes comparées seulement en mémoire |
| Types, tailles, modes, UID/GID | Identiques ; UID/GID 1000/1000, config mode 0600 |
| Dates de modification des fichiers | Identiques |
| Dates de modification des répertoires | **Deux écarts** : racine et un sous-répertoire |
| SQLite `integrity_check`, readonly | Réussi |
| Propriétaire | 1, configuré, non désactivé, MFA activée |
| Workflows / credentials / exécutions | 0 / 0 / 0 |
| Configuration et clé n8n | Identiques au volume réel, comparaison uniquement en mémoire |

La racine attendue porte `2026-10-12T14:01:16Z` et le sous-répertoire concerné
`2026-09-28T15:55:28Z`. Dans la copie extraite, ces deux dates valent
`2026-10-13T11:51:17Z`, correspondant au moment de l'extraction. Les fichiers
restaurés ne présentent aucune altération détectée ; l'écart porte sur ces deux
métadonnées de répertoire. Le contrôle complet demeure non validé.

La consigne impose de s'arrêter avant application des sessions si un contrôle
échoue : aucune commande Compose `up`, aucun démarrage n8n sur la copie réelle,
aucune correction des dates, réparation, nouvelle extraction ou restauration
sur le volume réel. Le volume de contrôle est conservé tel quel.

### Inventaire conservé et état final

Dans la racine d'opération existante :

- `capture/` : tar et manifeste d'origine en clair, inchangés ;
- `repository/` : dépôt restic local chiffré, conservé ;
- `restore/` : tar et manifeste restaurés en clair, inchangés ;
- `cache/` : cache opérateur conservé, ACL privées ;
- `evidence/` : preuves historiques conservées et nouvelles preuves non sensibles
  `resume-services-before-20261013.json`, `offline-restore-failure-20261013.json`
  et `resume-final-20261013.json`.

Les auxiliaires `prospectioncrm-923-source-check-20261013-69bd483f2858`,
`prospectioncrm-923-restore-extract-20261013-69bd483f2858`,
`prospectioncrm-923-restored-check-20261013-69bd483f2858` et
`prospectioncrm-923-restore-diagnostic-20261013-69bd483f2858` sont conservés,
arrêtés avec codes respectifs 0, 0, 1 et 0. Ils n'exécutent que des outils
d'archivage/vérification et aucun serveur n8n. La copie dans le volume Docker
reste soumise aux permissions Linux restaurées et aux droits d'accès au moteur
Docker ; les ACL Windows vérifiées concernent les fichiers hôtes, pas ce volume.

Des scripts de vérification sans secret ont été ajoutés dans le dossier privé
de code opérateur existant ; `operator-restic.ps1` reste inchangé. Son README
indique désormais que la séquence a réussi et ne doit pas être relancée.

n8n et PostgreSQL sont toujours arrêtés avec leurs identités et horodatages
initiaux ; aucune action de rétablissement nécessaire. Les paramètres 24/-1
ne sont **pas appliqués**. Reconnexion humaine en attente après une future
application validée ; aucune connexion automatisée, lecture de cookie/JWT,
observation d'expiration 24 h ou preuve de révocation globale.

Compose validé avec/sans profil, liens locaux/ancres et `git diff --check`
contrôlés. Périmètre Git final : ce rapport local et les deux guides n8n actualisés
pour refléter le succès restic local et le blocage hors ligne. Aucun secret,
artefact de sauvegarde, script opérateur ou binaire ajouté dans Git. Aucun staging,
commit, push, nettoyage ni transfert externe. La copie protégée hors machine reste
non réalisée. La Phase 9.2 reste ouverte ; la Phase 9.3 n'est pas commencée.

**INTERVENTION PARTIELLE — BLOCAGE IDENTIFIÉ**

## Diagnostic mtime et reprise conditionnelle du 13 octobre 2026

Cette reprise autorisée conserve le premier essai, corrige seulement l'extraction
des dates de répertoires et applique ensuite les sessions après validation.
Ni nouvelle capture ni nouvelle séquence restic. Les preuves historiques et les
champs du manifeste décrivant l'état à la capture ne sont pas réécrits.

### Champ, références et précision

Le contrôle initial comparait `mtime` par `Math.floor(stat.mtimeMs / 1000)` à
l'entier `mtime` lu dans le tar : secondes Unix entières. Il ne comparait ni
`ctime`, ni `atime`, ni date de création. Les valeurs natives en nanosecondes
de la source n'avaient pas été enregistrées à la capture. La référence historique
en secondes est corroborée par l'archive validée à ce moment-là ; elle ne permet
pas de reconstituer les fractions originales.

Le diagnostic a lu `fs.lstatSync(..., {bigint: true}).mtimeNs` sur les volumes
montés readonly. Les valeurs ci-dessous sont des entiers, sans conversion en date
formatée ni tolérance. `storage`, vide, sert de témoin conforme.

| Chemin relatif | Référence capture / tar (secondes) | Tar converti en nanosecondes | Première copie, mtime native (nanosecondes) | Écart copie − tar (nanosecondes) | Écart au contrôle initial (secondes) |
| --- | --- | --- | --- | --- | --- |
| `.` | 1791813676 | 1791813676000000000 | 1791892277944794802 | 78601944794802 | 78601 |
| `nodes` | 1790610928 | 1790610928000000000 | 1791892277899523171 | 1281349899523171 | 1281349 |
| `storage` | 1790610928 | 1790610928000000000 | 1790610928000000000 | 0 | 0 |

État **de la source relevé lors du diagnostic, avant application des sessions**,
distinct de la mesure historique de capture :

| Chemin | mtime native relevée (nanosecondes) | Fraction non stockée dans le tar (nanosecondes) |
| --- | --- | --- |
| `.` | 1791813676743441291 | 743441291 |
| `nodes` | 1790610928805512026 | 805512026 |
| `storage` | 1790610928941517602 | 941517602 |

Les 12 entrées de la source examinée avaient une fraction non nulle. À cet instant,
les services et leurs horodatages étaient toujours ceux de la capture, et tous
les octets, droits et mtime à la précision initiale correspondaient encore.
Cela ne remplace pas une mesure native historique absente.

**Précision du contrat conservée :** les formulations historiques « métadonnées
identiques » de ce rapport signifient, pour le mtime source, égalité en secondes
entières. Le tar ne conserve pas les fractions ; aucune égalité native exacte
source/tar n'est revendiquée. Le contrôle de la nouvelle restauration est plus
strict : nanosecondes natives exactement égales aux secondes du tar multipliées
par un milliard. Aucun écart n'est arrondi, ignoré ou accepté par une tolérance.
Cette clarification a été signalée avant application des sessions ; aucune
valeur de la source n'a servi à compléter l'archive.

### Outil, ordre des membres et cause démontrée

Image exacte inchangée :
`sha256:ffeb52485f78b1b06c9a832205853cf75da72a07a514c9a27724df85979d6c34`.
`/bin/tar` résout vers `/bin/busybox`, version **1.37.0**, compilation
`2026-08-17 20:58:01 UTC` ; Node **v26.7.0**. SHA-256 BusyBox :
`5ed782f868f3967340c62888a69f32eea9e12336e6163603c0cb95f6c2c18d1c`.
Aucune installation, modification d'image ou téléchargement.

Commande initiale retrouvée dans l'auxiliaire d'extraction conservé :

```sh
set -eu; contents=$(find /destination -mindepth 1 -print -quit); test -z "$contents"; tar -xpf /archive/n8n-post-init.tar -C /destination
```

Le volume de destination était neuf et vide ; `.` dans le tar désigne sa racine
montée. Archive readonly, destination seule en écriture, aucun montage du volume
réel dans cet auxiliaire. Extraction entre `2026-10-13T11:51:17.65695187Z` et
`2026-10-13T11:51:17.971590108Z`. Premier contrôle démarré ensuite, à
`2026-10-13T11:51:18.439319722Z`.

Les en-têtes ont la signature de style GNU `ustar ` (espace final), version
` \0`, et des mtime octales entières : racine `15263164054`, sous-répertoires
`15256506760`. Ce n'est pas la signature POSIX `ustar\0` / `00`. Aucun en-tête
PAX de membre ou global et aucune extension de précision sub-seconde.
Ordre de l'archive, indices à partir de zéro :

| Indice | Membre ou catégorie |
| --- | --- |
| 0 | `.` |
| 1 | `nodes` |
| 2 | `nodes/package.json` |
| 3 à 5 | Fichiers réguliers à la racine |
| 6 | `storage`, répertoire vide |
| 7 et 8 | `database.sqlite-wal` et `database.sqlite-shm` |
| 9 | `config` |
| 10 | Fichier régulier à la racine |
| 11 | `database.sqlite` |

La reproduction ci-dessous établit que cet extracteur applique les dates de
répertoires avant leurs enfants ; la création ultérieure des enfants modifie
les dates des parents, sans rétablissement différé. La racine subit le même
effet. Le répertoire vide conserve sa date. L'ordre inversé des membres dans
une seconde archive **synthétique seulement** élimine l'écart avec la même
commande. Il s'agit d'une cause observée localement, pas d'une attribution
générique à Windows ou Docker.

Le script initial relevait les métadonnées **avant** l'ouverture SQLite.
WAL et SHM sont déjà membres du tar. La nouvelle observation avant/après
`integrity_check` readonly conserve les 12 entrées, contenus, mtime et ctime,
avec empreinte d'arborescence identique :
`9cc475d542410be923598ca6bbcd320fb4c912ab5719b2b4821c8f84ebcabe10`.
Aucun fichier temporaire ou WAL/SHM n'a été créé par ces vérifications.

### Reproduction et correction testées

Volume exclusivement synthétique neuf, conservé :
`prospectioncrm_mtime_synthetic_20261013_69bd483f2858`.
Artefacts privés :
`%LOCALAPPDATA%\ProspectionCrmTests\phase923-mtime-20261013-69bd483f2858`.
La petite arborescence comprend racine, `nodes`, `storage` vide, quatre fichiers
dont une base SQLite synthétique, UID/GID 1000 et dates distinctes avec fractions.
Même image, même `/bin/tar`, même extraction `tar -xpf` dans une destination vide.

| Essai | Immédiatement après extraction | Après contrôle readonly | Résultat |
| --- | --- | --- | --- |
| Ordre original | Écart sur `.` et `nodes`, `storage` conforme | Aucune modification ; mêmes écarts | Cause reproduite |
| Finalisation après extraction | Mêmes deux écarts avant finalisation | Tous les champs conformes après finalisation, puis stables | Correction réussie |
| Contrôle négatif distinct | Copie finalisée conforme, puis `nodes` décalé de 0,5 s | Un écart `mtimeNs` attendu, 1700000100500000000 au lieu de 1700000100000000000 | Refus confirmé |
| Membres synthétiques réordonnés, répertoires en dernier | Toutes les dates conformes sans finalisation | Inchangées après SQLite | Effet de l'ordre confirmé |

Les quatre essais et le setup terminent avec code 0 ; le test négatif réussit
parce qu'il exige la détection de l'écart. Les dates brutes intermédiaires sont
conservées dans `evidence/mtime-synthetic-results-20261013.json`, statut
`SYNTHETIC_MTIME_CAUSE_AND_CORRECTION_VERIFIED`.

La disponibilité d'une option native différant le rétablissement a été vérifiée :
`tar --delay-directory-restore --help` renvoie 1, option non reconnue.
La correction emploie donc le `touch` BusyBox déjà présent, après extraction :
`/bin/touch -c -h -m -d @<secondes_archive> -- <répertoire_vérifié>`.
Valeurs exclusivement issues du tar, ordre `nodes`, `storage`, `.` ; arguments
séparés via `spawnSync`, sans interpolation shell.

Le finaliseur refuse les racines non prévues, chemins absolus, traversées,
doublons, liens ou types inattendus. Il valide d'abord l'ensemble de l'arbre,
les octets et métadonnées des fichiers, les modes et UID/GID. Seules les dates
des répertoires peuvent différer avant finalisation. Chaque chemin est résolu
et ses composants inspectés sans suivre de lien. Après `touch`, type, inode,
périphérique, mode, UID/GID et atime sont vérifiés inchangés ; les contenus et
métadonnées archivées des fichiers le sont aussi. Le ctime de répertoire change
normalement lors du réglage de mtime ; ce n'est pas un timestamp stocké dans ce tar.
Aucun assouplissement des permissions, création ou réécriture de fichier.

### Nouvelle restauration réelle hors ligne

Même tar restauré, SHA-256 revérifié avant/après ; manifeste inchangé.
Refus des collisions, puis création du volume neuf
`prospectioncrm_n8n_restorecheck2_20261013_69bd483f2858`.
L'ancien `prospectioncrm_n8n_restorecheck_20261013_69bd483f2858` reste inchangé,
empreinte précédente revérifiée après la nouvelle restauration.

Extraction et finalisation dans le second volume uniquement, puis tous les
contrôles sur montages readonly, sans réseau. Aucun n8n démarré sur ces copies.
Résultat `CORRECTED_REAL_RESTORATION_OFFLINE_VERIFIED` à
`2026-10-13T12:17:06.3153189Z` :

| Contrôle | Résultat |
| --- | --- |
| Arborescence | 12 entrées, dont 9 fichiers et 3 répertoires ; aucun ajout/manque |
| Octets des 9 fichiers, tailles, UID/GID, modes | Conformes à l'archive |
| mtime de tous les fichiers/répertoires | Exactement conformes aux valeurs stockées, sans tolérance |
| Racine, mtime en ns | 1791813676000000000 |
| `nodes` et `storage`, mtime en ns | 1790610928000000000 |
| SQLite readonly | Intègre |
| Propriétaire / MFA | 1, configuré, non désactivé, MFA activée |
| Workflows / credentials / exécutions | 0 / 0 / 0 |
| Configuration et clé | Identiques à la source, comparaison en mémoire seulement |
| Avant/après vérifications | Arbre inchangé, aucune écriture |

Empreinte agrégée identique avant/après les contrôles readonly de la seconde copie :
`15d4198319feb8fc7337456a9d798569a95fc7bbcfd7f124db65f188bf546b44`.
Les deux volumes contiennent des **copies réelles sensibles**, soumises aux droits
Linux et d'accès au moteur Docker. Les ACL Windows concernent les fichiers hôtes.

### Application conditionnelle des sessions effectivement exécutée

Juste avant application : nouvelle comparaison readonly du volume réel à la
capture (octets, droits, mtime à la précision initiale), SQLite et agrégats.
Aucun workflow actif ou publié, credential, exécution ni traitement en attente.
Projet, fichier Compose, ID exact d'image et volume identiques ; aucun writer
actif ; les deux services toujours arrêtés. Modèle Compose antérieur reconstitué
en mémoire et son hash vérifié ; seuls les deux paramètres autorisés diffèrent.
Toutes les conditions de reprise sont alors satisfaites.

Commande exécutée avec code 0 depuis `F:\Projets\ProspectionCrm` :

```powershell
docker compose --profile integrations up -d --no-deps --pull never n8n
```

| Contrôle effectif | Résultat |
| --- | --- |
| Nouveau conteneur n8n | `72c0a39ba8971192008d818dfd9d63a9fd018afc5c52417a8c072187c1c6c77b` |
| Image et volume | Mêmes ID d'image et `prospectioncrm_n8n_data` |
| Durée / refresh | `24` / `-1`, seules différences d'environnement |
| Autres réglages | Commande, entrypoint, utilisateur, healthcheck, restart, montages et réseau inchangés |
| Santé | `healthy` observé |
| Readiness conteneur / hôte local | HTTP 200 / 200 |
| Publication | `127.0.0.1:5678` uniquement |
| Démarrage n8n UTC | 2026-10-13T12:22:11.458106178Z |
| Arrêt n8n UTC | 2026-10-13T12:22:29.642191833Z, `exited 0` |
| PostgreSQL | Même ID, état et horodatages initiaux ; jamais démarré ni modifié |

L'arrêt ciblé a rétabli l'état initial de n8n. Après cet arrêt, contrôle readonly
du volume réel : SQLite intègre, propriétaire configuré/MFA conservée, nombres
de workflows/credentials/exécutions toujours nuls, configuration et clé identiques
à la copie de référence. Aucun remplacement de volume. Le démarrage réel peut
écrire normalement dans SQLite ; aucune identité binaire post-démarrage de toute
la base avec la capture n'est revendiquée. La capture reste valide pour l'état
sauvegardé ; sa précision de mtime et sa date ne sont pas modifiées.

Résultat `OFFLINE_RESTORATION_AND_SESSIONS_VERIFIED` à
`2026-10-13T12:22:31.0442498Z`. Preuves non sensibles successives dans `evidence/` :
`mtime-diagnosis-20261013.json`, `archive-format-20261013.json`,
`mtime-synthetic-results-20261013.json`, `offline-restore-corrected-20261013.json`,
`sessions-preapply-20261013.json`, `sessions-healthy-20261013.json` et
`sessions-applied-20261013.json`. Tous les auxiliaires et artefacts sont conservés ;
ils sont arrêtés. Aucun nettoyage des copies, caches ou volumes.

### Contrôle humain restant et périmètre final

État historique à l'issue du contrôle de `12:27:35Z` : la reconnexion restait
en attente. Les consignes ci-dessous sont conservées comme trace du passage
à l'utilisateur ; la confirmation ultérieure, en dernière section, lève cette
attente. Elles ne constituent pas une demande de recommencer ce contrôle.

L'utilisateur peut démarrer **le seul conteneur n8n déjà configuré** :

```powershell
docker start prospectioncrm-n8n-1
```

Ouvrir `http://127.0.0.1:5678`, se déconnecter explicitement puis se reconnecter
avec le mot de passe habituel et la 2FA. Aucun secret, code, cookie ou JWT à
transmettre. Après le contrôle, rétablir l'arrêt :

```powershell
docker stop --timeout 30 prospectioncrm-n8n-1
```

`--timeout` évite l'avertissement de dépréciation observé pour `--time` lors de
l'arrêt effectué. Aucune expiration réelle après 24 heures observée, aucune
révocation globale des jetons démontrée, aucune reconnexion humaine automatisée.

Les scripts de diagnostic, inventaire, finalisation, reproduction, restauration
et application sont **hors Git**, dans le dossier privé opérateur existant :
`mtime-diagnosis-20261013.cjs`, `mtime-archive-inventory.py`,
`mtime-tree-20261013.cjs`, `mtime-synthetic-20261013.cjs`,
`verify-corrected-copy-20261013.cjs`, `restore-corrected-20261013.ps1` et
`apply-sessions-20261013.ps1`. Les deux derniers sont déjà exécutés et ne sont
pas des commandes de reprise. Le README local est actualisé ; `operator-restic.ps1`
conserve son empreinte et ne doit pas être relancé.
Le vérificateur final `final-checks-20261013.ps1` est également hors Git.

Périmètre proposé pour un futur commit : uniquement ce rapport à ajouter et les
deux guides suivis `docs/n8n-operations.md`, `docs/n8n-encrypted-backups.md`.
L'affichage global des fichiers modifiés n'élargit pas ce périmètre. Aucun staging,
commit ou push exécuté. Syntaxes PowerShell/JavaScript/Python, liens locaux/ancres,
Compose avec/sans profil et `git diff --check` vérifiés. Index vide ; `compose.yml`,
archives historiques, tar/manifeste source et restaurés inchangés.
Preuve `evidence/mtime-sessions-final-checks-20261013.json`, statut
`FINAL_CHECKS_VERIFIED` à `2026-10-13T12:27:35.8250262Z` : 7 scripts PowerShell,
7 JavaScript, 2 Python, 4 documents Markdown et 15 liens locaux/ancres contrôlés ;
305 éléments privés de l'opération vérifiés avant création de cette preuve.
Les services correspondent toujours à l'état arrêté documenté, auxiliaires arrêtés.

La sauvegarde restic reste locale et chiffrée ; capture, tar restauré et copies
Docker restent sensibles en clair. Aucune copie protégée hors machine ni transfert
externe. Aucun travail métier/EF Core, aucune Phase 9.3. **Phase 9.2 toujours ouverte.**

**RESTAURATION HORS LIGNE ET SESSIONS VALIDÉES — RECONNEXION HUMAINE EN ATTENTE**

## Confirmation humaine et contrôle documentaire final

### Confirmation du 13 octobre 2026

L'utilisateur confirme que la reconnexion à n8n avec son mot de passe et sa 2FA
fonctionne après application des paramètres de session **24 / -1**. Cette
validation est un **contrôle interactif confirmé par l'utilisateur**, sur
l'instance réelle. Ce n'est pas un test automatisé de Codex ni une connexion
sur une instance restaurée. Aucune nouvelle connexion n'est demandée et aucune
valeur de mot de passe, code MFA, cookie ou JWT n'est reçue.

L'utilisateur accepte explicitement la précision du tar en secondes entières
pour ce jalon local. Les fractions de seconde de la source n'ont pas été
sauvegardées ; la restauration fidèle aux valeurs archivées ne constitue pas
une conservation intégrale de toutes les métadonnées d'origine.

### Résultats et provenance des validations

| Élément | Résultat acquis | Provenance et limite |
| --- | --- | --- |
| Sauvegarde restic réelle | Backup, `check --read-data`, restore : codes 0 ; snapshot et empreintes corroborés localement | Exécution par l'utilisateur dans son terminal personnel ; pas de nouvelle invocation restic pendant cette revue |
| Restauration réelle | Seconde copie conforme pour les 9 fichiers, attributs vérifiés, mtime archivés, SQLite, propriétaire/MFA, configuration et clé | Contrôle hors ligne par Codex ; aucun n8n démarré sur une copie restaurée, aucune connexion à celle-ci |
| Premier écart de mtime | Cause reproduite : parents datés avant création des enfants ; premier volume inchangé | Finalisation reproductible depuis l'archive, enfants avant parents, test négatif de 0,5 s concluant ; fractions source absentes |
| Sessions | Deux paramètres appliqués au n8n réel ; `24` et `-1` retrouvés par inspection readonly | Application et tests de santé/readiness déjà consignés ; pas de nouvelle recréation ni de nouveau test Docker |
| Reconnexion | Mot de passe et 2FA fonctionnent après application | Confirmation interactive de l'utilisateur, sans automatisation |
| Services | n8n et PostgreSQL arrêtés, code 0 | Inspection séparée décrite ci-dessous ; aucun arrêt déduit de la confirmation humaine |

### État des services observé séparément

Lecture de `docker inspect` à **2026-10-13T12:48:09.9674160Z** ; sortie complète
traitée en mémoire, seuls les champs non sensibles utiles sont retenus.
Aucune commande start/stop/up, aucun accès SQL, montage, test Docker ou requête
HTTP pendant ce contrôle documentaire.

| Service | ID constaté | État | Dernier démarrage UTC | Dernier arrêt UTC |
| --- | --- | --- | --- | --- |
| n8n | `72c0a39ba8971192008d818dfd9d63a9fd018afc5c52417a8c072187c1c6c77b` | `exited`, `running=false`, code 0 | 2026-10-13T12:38:39.091787071Z | 2026-10-13T12:40:04.87127488Z |
| PostgreSQL | `21c4200431ecb101f142eeddde520a8fb421bb60344e91907bcf59576f874300` | `exited`, `running=false`, code 0 | 2026-10-12T13:04:47.188228195Z | 2026-10-12T13:07:01.133635969Z |

Le conteneur n8n conserve son ID après application, son image exacte, le volume
`prospectioncrm_n8n_data`, les valeurs de session 24/-1 et la configuration de
publication `127.0.0.1:5678`. Ses horodatages sont postérieurs à ceux des tests
automatisés précédents ; ils ne sont pas attribués à une action de cette revue.
PostgreSQL conserve son ID, son image, son montage et ses horodatages initiaux.

Le champ `State.Health.Status` mémorisé vaut `unhealthy` sur les deux conteneurs
arrêtés. Ce constat est distinct de leur état `exited 0` et ne constitue pas
une nouvelle mesure de santé en fonctionnement. Les valeurs `healthy` et
HTTP 200 documentées plus haut sont les observations faites lors de l'application
des sessions ; aucune cause du champ actuel n'est déduite et aucun test n'est relancé.

### Relecture, corrections et périmètre Git

Référence vérifiée : branche `main`, HEAD
`dcf3ac283c1f8e17f6d7aa6105b2b0a86d1af983`, index vide. Le diff complet des
fichiers suivis et l'intégralité de ce rapport non suivi ont été examinés.
Les seuls fichiers Git concernés sont :

| Fichier | État Git |
| --- | --- |
| `docs/n8n-operations.md` | Modifié, non stagé |
| `docs/n8n-encrypted-backups.md` | Modifié, non stagé |
| `docs/phase-9-2-step-3-validation.md` | Non suivi, à ajouter lors d'un futur commit autorisé |

Corrections documentaires : remplacement des statuts actuels « reconnexion en
attente » par la confirmation interactive ; ajout de l'état séparément observé ;
acceptation explicite de la précision à la seconde ; limitation de la formulation
trop générale « conserve les métadonnées Unix » aux attributs vérifiés ; titre
du guide de sauvegarde élargi à la validation réelle 9.2.3. Les valeurs natives
de la source sont datées comme relevées pendant le diagnostic, sans les présenter
comme son état actuel après reconnexion. Le premier échec, sa cause, la correction,
les tests et les anciens statuts sont conservés avec leur portée historique.

Cohérence des trois documents, liens locaux/ancres, syntaxe des liens externes
et `git diff --check` vérifiés. Les destinations externes ne sont pas contactées.
Relecture et recherche ciblée : aucune valeur de secret, clé privée, JWT, donnée
SQLite/config ou pièce de sauvegarde dans les trois fichiers proposés. Les
identifiants d'image/conteneur/snapshot et empreintes de fichiers consignés sont
des références de contrôle, pas des secrets. Aucun nouveau fichier de sauvegarde,
binaire ou script ne fait partie du diff ou des fichiers non suivis à ajouter.
`compose.yml`, le code métier, les migrations et les anciens rapports n'ont
aucun changement dans le périmètre Git constaté.

Les scripts opérateur et leur README restent dans
`%LOCALAPPDATA%\ProspectionCrmOperations\phase923-20261012-151755-69bd483f2858`,
hors dépôt. Ils ne sont ni modifiés ni exécutés pendant cette revue et ne font
pas partie du futur commit documentaire. Les preuves et sauvegardes locales
restent également hors de ce périmètre ; aucune suppression, aucun transfert,
aucun staging, commit ou push. Les contrôles opérationnels antérieurs ne sont
pas rejoués pour produire cette mise à jour documentaire.

### Réserves conservées

L'expiration après 24 heures n'a pas été observée et la révocation globale des
anciens jetons n'est pas démontrée. La restauration réelle est validée hors ligne,
sans connexion sur une instance restaurée. Les copies en clair et les deux
volumes de contrôle sensibles restent conservés ; la copie protégée hors machine
n'est pas réalisée. Les fractions de seconde ne sont pas sauvegardées, avec
acceptation explicite de cette précision pour ce jalon local.

La préparation documentaire du jalon est terminée ; **la Phase 9.2 reste ouverte**
et la Phase 9.3 n'est pas commencée.

**PRÊT POUR COMMIT DU JALON**
