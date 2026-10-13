# Phase 9.2 — Étape 4 : préparation locale de la copie hors machine

**COPIE DE TRANSFERT PRÉPARÉE ET VÉRIFIÉE LOCALEMENT**

Un ZIP du seul dépôt restic réel a été créé et extrait dans un nouveau dossier
de contrôle privé. L'arborescence complète et les octets de tous les fichiers
sont identiques à la source, qui est restée inchangée pendant l'opération.
Lors de cette préparation locale, aucune copie hors machine n'était encore
réalisée et la Phase 9.2 restait ouverte. Aucun accès à Google Drive, transfert
externe ou mot de passe demandé par l'agent. La confirmation utilisateur et la
décision de clôture intervenues ensuite sont consignées en fin de rapport.

## Référence et source confirmées

Référence Git constatée : branche `main`, HEAD
`4e43f7edee7c6872a83600251d46154f495c239a`, index vide et working tree propre
au départ. CI Build #57 réussie selon la confirmation de l'utilisateur ; aucune
nouvelle interrogation distante ni exécution de CI pendant cette préparation.

Le [rapport 9.2.3](phase-9-2-step-3-validation.md#confirmation-humaine-et-contrôle-documentaire-final),
le [guide de sauvegarde](n8n-encrypted-backups.md#copie-hors-machine-sans-fournisseur-imposé)
et le script opérateur local désignent la même opération. Le script définit
explicitement le dépôt comme son sous-dossier `repository`. Source sélectionnée :

```text
F:\Sauvegardes\ProspectionCrm\n8n\operation-20261012-151755-69bd483f2858\repository
```

Le dossier parent de l'opération n'a pas été sélectionné. Lecture des preuves
locales existantes : `operator-result.json`, statut `RESTIC_SEQUENCE_VERIFIED`,
snapshot `11163be51570338223c6db292896ea6be5dc2d7aff01ded6ea523c84aaab5ec5`,
codes backup / check / restore 0 ; `sessions-applied-20261013.json`, statut
`OFFLINE_RESTORATION_AND_SESSIONS_VERIFIED`. Les champs historiques d'attente de
ces preuves restent ceux de leurs dates ; la reconnexion ultérieure est consignée
dans le rapport 9.2.3. Aucune de ces opérations n'a été rejouée.

## Précontrôles locaux

Chemins absolus vérifiés, racine exacte et composants ordinaires, aucun lien ou
jonction dans la source, ses descendants ou la nouvelle destination. Énumération
avec les éléments cachés et système et arrêt sur erreur de lecture. Aucun flux
NTFS nommé supplémentaire trouvé. Aucun fichier caché/système présent dans cette
source ; la méthode ne les exclut pas.

| Catégorie source | Fichiers | Octets |
| --- | --- | --- |
| `config` | 1 | 155 |
| `data/` | 2 | 234 433 |
| `index/` | 1 | 1 135 |
| `keys/` | 1 | 473 |
| `snapshots/` | 1 | 473 |
| `locks/` | 0 | 0 |
| Total | 6 | 236 669 |

261 sous-répertoires, soit **262 répertoires avec la racine**, vides compris.
Le fichier snapshot attendu est présent. Aucun contenu de fichier `keys` affiché.

Contrôles avant et après : aucun processus restic, script opérateur actif ou autre
processus identifié par une ligne de commande ciblant le dépôt ; dossier `locks`
entièrement vide. Les six fichiers source sont gardés ouverts en lecture avec
`FileShare.Read`, qui refuse les ouvertures concurrentes en écriture ou suppression.
Un `FileSystemWatcher` surveille noms, créations, suppressions, renommages,
tailles, dernière écriture et sécurité dans le dépôt : **zéro événement et zéro
erreur de surveillance**. Les inventaires avant/après complètent ce contrôle.
Aucun unlock, arrêt forcé ou nettoyage.

F: est un disque local fixe NTFS. Au précontrôle d'empaquetage :
**170 726 014 976 octets libres**, pour un besoin conservateur calculé de
17 524 986 octets couvrant ZIP, copie de contrôle et marge. La racine OneDrive
recensée est `C:\Users\jills\OneDrive`, sans recouvrement. Les clés locales de
configuration de synchronisation examinées n'indiquent pas d'autre chemin ;
aucun client courant OneDrive, GoogleDriveFS, Dropbox, Syncthing, Nextcloud,
pCloud ou Resilio observé actif. Le nouveau dossier n'est inscrit dans aucune
synchronisation par cette opération. Ce relevé local ne constitue pas un audit
exhaustif de tout logiciel tiers installé.

Le dossier de transfert neuf a d'abord hérité des ACL privées de son parent,
puis sa propre DACL a été protégée : compte opérateur et SYSTEM uniquement,
FullControl héritable. Les ACL de tous les nouveaux éléments ont été contrôlées.
**Aucune permission de source ou de parent modifiée.** Le partage administratif
`F$` préexistant reste présent ; aucun partage ou paramètre réseau changé.

## Seul fichier à transférer ultérieurement

Nom exact :

```text
ProspectionCrm-n8n-restic-repository-20261013T134952Z-768a7affb58c.zip
```

Chemin absolu :

```text
F:\Sauvegardes\ProspectionCrm\n8n\transfer-20261013T134952Z-768a7affb58c\ProspectionCrm-n8n-restic-repository-20261013T134952Z-768a7affb58c.zip
```

| Propriété | Résultat |
| --- | --- |
| Taille | **267 841 octets** |
| Taille en Mio, 1 Mio = 1 048 576 octets | **0,255433 Mio** |
| SHA-256 | `37C5BA479DC1FE3DF3E904F1F7D7E8976E685DF9557CB3CCE024E76D0E7CC1CB` |
| Racine interne unique | `repository/` |
| Entrées ZIP | 268 : 6 fichiers et 262 répertoires |
| Méthode ZIP | Store, méthode 0, sans compression supplémentaire |
| Chiffrement ZIP | Aucun ; aucun second mot de passe |
| Fin de vérification UTC | 2026-10-13T13:54:08.2491639Z |

Le ZIP regroupe le dépôt dont les données sont déjà protégées par restic ; il
**n'ajoute pas de chiffrement**. Il ne contient ni `capture`, ni `restore`, ni
`cache`, ni scripts opérateur, secrets séparés ou logs. Aucun `n8n-post-init.tar`
en clair, manifeste voisin ou fichier de mot de passe inclus. Les fichiers
`keys` nécessaires au dépôt restic font partie de sa copie complète.

Les environ 7 Go libres sur Google Drive sont une indication de l'utilisateur,
non vérifiée sur le compte. La taille locale préparée est très inférieure à cette
capacité déclarée. Cela ne prouve ni un envoi, ni la présence d'une copie distante.

## Procédure exécutée et comparaison

Script local dédié `prepare-transfer.ps1`, PowerShell **7.6.5**, .NET **10.0.11**,
API `System.IO.Compression.ZipArchive`. Les anciens scripts opérateur n'ont été
ni modifiés ni exécutés. Aucune commande restic ou Docker, aucune restauration
n8n, aucun essai synthétique. Aucun secret nécessaire à cette copie de fichiers.

1. Refus des collisions sur ZIP, dossier de contrôle et dossier de preuves.
   Ouverture exclusive du ZIP neuf avec `FileMode.CreateNew` ; aucune écriture
   sur un fichier existant.
2. Inventaire complet de la source : répertoires relatifs, chemins relatifs des
   fichiers, tailles et SHA-256. Conservation de tous les répertoires vides.
3. Création du ZIP depuis les six fichiers source ouverts en lecture, sous la
   seule racine `repository/`. Aucun dossier voisin sélectionné.
4. Lecture de toutes les entrées ZIP, vérification de leur correspondance exacte
   à l'inventaire, refus des doublons, chemins absolus, traversées et liens.
   Extraction dans `control/repository`, uniquement dans des chemins neufs.
5. Inventaire complet des fichiers extraits et nouvel inventaire source.
   Comparaison de l'ensemble des chemins, tailles et SHA-256 de chaque fichier,
   ainsi que de tous les chemins de répertoire.
6. Contrôle des attributs, dates de création/dernière écriture et ACL de la source
   avant/après : inchangés. ACL du parent de transfert inchangées.
   Calcul du SHA-256 du ZIP, puis recalcul final indépendant de son empreinte.

Résultats : **aucun fichier manquant, aucun ajout, aucune différence de contenu** ;
inventaires source avant/après et copie extraite identiques. **Source inchangée.**
Le script termine avec code 0, statut
`TRANSFER_COPY_PREPARED_AND_LOCALLY_VERIFIED`. Les erreurs des API sont bloquantes ;
aucune erreur ignorée pour poursuivre la création ou la comparaison.
Une lecture indépendante du ZIP avec la bibliothèque Python standard confirme
les 268 entrées, la racine unique, la méthode Store, l'absence de chiffrement ZIP,
sa taille et son empreinte ; code 0.

La comparaison source/copie porte sur **l'arborescence et les octets**, pas sur
une égalité des dates ou ACL restaurées par ZIP. Les ACL privées de destination
sont vérifiées séparément. Aucune exigence de précision de date supérieure à
celle du ZIP, aucun ajustement de données pour satisfaire un contrôle. Les
limites de précision du tar n8n documentées en 9.2.3 restent inchangées.

## Nouveaux artefacts locaux conservés

Racine privée hors Git, hors dépôt restic et hors des racines de synchronisation
recensées :

```text
F:\Sauvegardes\ProspectionCrm\n8n\transfer-20261013T134952Z-768a7affb58c
```

| Chemin relatif dans cette racine | Fonction |
| --- | --- |
| `ProspectionCrm-n8n-restic-repository-20261013T134952Z-768a7affb58c.zip` | Seul fichier préparé pour un futur envoi manuel |
| `control/repository/` | Copie du dépôt chiffré extraite pour comparaison : 6 fichiers, 262 répertoires racine comprise |
| `evidence/prechecks.json` | Précontrôles, capacité et protections |
| `evidence/source-before.json` | Inventaire source avant empaquetage |
| `evidence/source-after.json` | Inventaire source après copie de contrôle |
| `evidence/extracted-inventory.json` | Inventaire complet extrait |
| `evidence/transfer-result.json` | Résultat, taille, SHA-256 et limites |
| `prepare-transfer.ps1` | Script local de cette préparation, hors Git |

Total de cette racine : **13 fichiers, 539 815 octets de fichiers, 265 répertoires
racine comprise**. Tous sont conservés ; ne pas envoyer la racine complète, la
copie de contrôle ou les preuves avec le ZIP. Aucune suppression effectuée.
La copie extraite contient le dépôt restic protégé, sans extraction des données
n8n en clair. Les copies sensibles en clair et volumes de contrôle antérieurs
restent conservés et n'ont pas été manipulés pendant cette mission.

## Git et limites constatés lors de la préparation locale

Le seul changement Git de cette mission est ce nouveau rapport non suivi :
`docs/phase-9-2-step-4-validation.md`. Branche et HEAD inchangés, index vide ;
aucune modification des trois documents du jalon précédent, de `compose.yml`,
du code métier ou des migrations. Aucun ZIP, script local, inventaire détaillé,
cache, dépôt restic ou artefact opérateur ajouté à Git. `git diff --check` réussi ;
le rapport non suivi est également vérifié pour les espaces de fin de ligne,
les blocs Markdown et les liens locaux.

Aucun commit, push, accès Google, transfert externe, changement Docker, opération
sur les volumes, configuration, compte, MFA ou secret pendant cette préparation.
À cette date, la copie protégée hors machine n'était pas réalisée et la Phase 9.2
restait ouverte ; aucune Phase 9.3 n'avait été engagée. La comparaison locale ne
constitue pas une vérification d'une copie récupérée depuis le stockage distant.

**COPIE DE TRANSFERT PRÉPARÉE ET VÉRIFIÉE LOCALEMENT**

## Confirmation utilisateur et clôture du périmètre local — 13 octobre 2026

L'utilisateur confirme avoir déposé manuellement dans son Google Drive personnel
le ZIP du dépôt restic préparé et vérifié ci-dessus. **Copie hors machine réalisée
selon confirmation utilisateur**, sans contrôle automatisé du transfert.

Sur demande explicite de l'utilisateur, **le téléchargement de contrôle et la
comparaison après transfert ne sont pas effectués**. Aucune validation du compte
Google, de ses permissions, du contenu distant ou du transfert n'est déduite de
cette confirmation. Les vérifications locales historiques restent celles de la
préparation ; aucun parcours de sauvegarde, restauration ou connexion n'est rejoué.

**La Phase 9.2 est clôturée pour le périmètre local retenu, par décision de
l'utilisateur**, avec cette limite de vérification distante acceptée. Les copies
en clair et volumes de contrôle sensibles restent conservés. Cette décision
autorise la première tranche backend documentée dans
[Phase 9.3.1 — contrats et persistance](phase-9-3-step-1-validation.md), sans
activation de communication vers n8n.
