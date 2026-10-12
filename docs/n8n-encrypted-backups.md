# Sauvegardes chiffrées n8n — stratégie et validation synthétique 9.2.2

## Statut et choix

**Parcours local validé sur données synthétiques ; sauvegarde réelle protégée
hors machine non opérationnelle.** Le complément du 12 octobre 2026 a utilisé
restic portable 0.19.1, restauré une archive synthétique identique, puis démarré
et arrêté le n8n restauré isolé. Aucun volume ou backup réel n'a été manipulé.

Constat historique de l'audit initial : ni installation ni initialisation de dépôt
restic, aucun chiffrement ni transfert ; `restic`, `age`, `7z` et `gpg` non trouvés
dans le PATH, démonstration reportée. Lors du complément autorisé, GPG fourni avec
Git a été trouvé hors PATH et le binaire restic a été extrait hors dépôt, sans
installation système, modification de PATH ou élévation. Les résultats ci-dessous
complètent ce constat, sans transformer les anciennes archives en backups actuels.

Retenir **restic**, outil libre utilisable sous Windows et Linux, pour disposer
d'un dépôt chiffré, de snapshots, de contrôles d'intégrité et d'une politique de
conservation sans dépendre d'un fournisseur. Un simple chiffrement d'archive avec
age est plus limité pour gérer cette conservation ; aucun second outil n'est
nécessaire au parcours recommandé. La version portable éprouvée est 0.19.1 ;
contrôler à nouveau provenance et empreinte en cas de nouveau téléchargement,
et valider l'emplacement et la garde du secret avant toute utilisation réelle.

Le format de dépôt restic v2 est documenté et lisible par restic >= 0.14.0.
Les données sont chiffrées avec AES-256-CTR et authentifiées par Poly1305-AES ;
le mot de passe protège les clés du dépôt via une dérivation scrypt. Ne pas
implémenter ces primitives soi-même. Les tailles/nombres de fichiers du dépôt
peuvent rester observables ; le chiffrement n'empêche ni leur suppression ni
l'indisponibilité du support.

Sources : [format et cryptographie](https://restic.readthedocs.io/en/stable/100_references.html),
[création et versions de dépôt](https://restic.readthedocs.io/en/stable/030_preparing_a_new_repo.html).

## Trois éléments distincts à conserver

1. **Volume n8n cohérent** : SQLite, WAL/SHM éventuels, configuration avec sa clé,
   données binaires et autres fichiers. Le tar créé sous Linux conserve les
   métadonnées Unix malgré son stockage intermédiaire sur Windows.
2. **Secret de déchiffrement restic** : mot de passe long, aléatoire, distinct
   de la clé n8n, du compte propriétaire et du mot de passe PostgreSQL.
3. **Manifeste non secret dans la sauvegarde** : date UTC, image/tag/identifiant
   n8n, provenance du volume, version de l'outil, empreinte SHA-256 du tar et
   résultats des vérifications. Aucun token ni contenu de config dans le manifeste.

Ne jamais remplacer la clé n8n pendant une restauration. Si elle est injectée
extérieurement au volume, prévoir sa récupération depuis un stockage protégé
distinct, coordonnée avec la sauvegarde ; la sauvegarde du volume seule serait
alors insuffisante. Les archives 9.2.1 ne sont pas retraitées pendant cette mission.

## Garde des secrets et des données en clair

Le propriétaire choisit le mot de passe restic et le conserve dans son gestionnaire
de mots de passe, avec une procédure de récupération indépendante de la machine
sauvegardée et de l'instance n8n. Sans ce mot de passe, la sauvegarde est perdue.
Une copie de secours hors ligne protégée peut compléter le gestionnaire.

Pour les premières opérations : saisie interactive par l'utilisateur, jamais
dans les arguments, le chat, Git, `.env`, les logs ou un script. Une automatisation
future pourra utiliser `--password-command` avec un gestionnaire de secrets ;
ne pas la déployer avant validation de sa garde et de ses permissions. Éviter un
mot de passe en variable d'environnement ou dans un fichier texte durable.

Exception limitée à l'essai synthétique : deux mots de passe indépendants de
32 octets aléatoires ont été générés en mémoire, puis stockés dans `secrets/`,
hors Git et hors source sauvegardée. Les arguments ne contiennent que leurs
chemins. La DACL de la racine d'essai est protégée contre l'héritage ; seuls
l'opérateur courant et SYSTEM ont FullControl, hérité par les fichiers de secrets
et vérifié après création. Aucune valeur n'a été affichée. Ces secrets de test
restent conservés pour reproduire l'essai, jamais réutilisés en exploitation.

Répertoire de préparation local hors Git, ACL limitées au compte opérateur et
aux administrateurs requis, disque chiffré si disponible et vérifié. `umask 077`
du conteneur ne remplace pas les ACL Windows. Restic chiffre sa destination,
pas le tar source ni le tar restauré. Définir séparément le devenir des fichiers
temporaires en clair après validation des copies ; ne rien supprimer implicitement.
La révocation d'un accès au dépôt ne fait pas disparaître les anciennes copies.

## Fréquence et conservation proposées

- Lorsque n8n contient des données utiles : copie quotidienne dans une fenêtre
  d'arrêt convenue et avant changement important ; pas d'arrêt automatique ajouté.
- Objectif proposé de perte maximale : 24 h, sous réserve d'une sauvegarde réussie
  et copiée hors machine. Temps de restauration à mesurer, aucune promesse actuelle.
- Conserver 7 snapshots quotidiens, 4 hebdomadaires et 6 mensuels ; conserver aussi
  un point avant migration tant que cette migration n'est pas validée.
- Les périodes ne s'additionnent pas forcément et ne comptent que les périodes
  contenant des snapshots. La rétention des backups est indépendante du pruning
  des exécutions n8n ; elle peut conserver plus longtemps des données sensibles.
- Examiner uniquement un `forget --dry-run` après inventaire. `forget` et `prune`
  réels sont destructifs et nécessitent une autorisation distincte ; aucun ici.

Utiliser un tag constant `n8n-cold` et un identifiant d'hôte stable non personnel.
Si chaque archive a un chemin daté différent, grouper par `host,tags` plutôt que
par les chemins, et filtrer sur ce seul jeu de sauvegardes. Sinon chaque chemin
peut former un groupe indépendant et empêcher la rétention attendue.
Le nom d'hôte reste une clé de regroupement : un changement de `--host` crée
un groupe distinct même avec `host,tags`. Ne pas mélanger plusieurs instances
sous le même couple hôte/tags. La fréquence quotidienne est une décision
d'exploitation, pas un effet de `--keep-daily` ; aucune planification n'est créée.

Essai du 12 octobre : deux snapshots du même tar sous deux chemins différents,
mêmes hôte et tag. `forget --dry-run` par défaut (`host,paths`) produit deux groupes ;
`--group-by host,tags` en produit un. Les deux snapshots restent conservés par
les deux simulations : le second correspond aux périodes courantes, le premier
aux règles de sauvegarde la plus ancienne lorsque l'historique est insuffisant.
Les identifiants avant/après sont identiques. Une seule journée ne démontre
ni sept jours de fréquence ni six mois de conservation.
[Sémantique officielle de conservation](https://restic.readthedocs.io/en/stable/060_forget.html).

## Procédure future de sauvegarde

**Non exécutée sur l'instance réelle.** L'utilisateur rapporte l'initialisation
du propriétaire, la 2FA et la reconnexion. La capture réelle de 9.2.1 est
antérieure ; créer une nouvelle sauvegarde post-initialisation lors d'une
intervention autorisée, sans écraser cette référence historique.

Prérequis : outil/version approuvés, destination locale hors dépôt Git, capacité
disque suffisante, mot de passe gardé, arrêt n8n planifié et aucun autre writer.
Ne pas sauvegarder le disque virtuel Docker Desktop actif comme substitut d'une
copie SQLite cohérente. Le VSS Windows seul ne prouve pas cette cohérence.

1. Appliquer la [sauvegarde à froid](n8n-operations.md#sauvegarde-à-froid) dans un
   nouveau dossier privé daté. Ne pas réutiliser ni écraser les archives 9.2.1.
2. Contrôler chaque code retour, l'archive et son empreinte ; écrire le manifeste
   non secret. Rétablir l'état n8n prévu après la capture, sans changer PostgreSQL.
3. Initialiser une seule fois un nouveau dépôt restic local protégé, après
   vérification du chemin ; saisir le mot de passe interactivement. Ne pas
   réinitialiser un dépôt existant. Le dépôt ne doit pas se trouver dans la source.
4. Sauvegarder seulement le dossier de cette capture. Enregistrer l'identifiant
   exact du snapshot et le code retour 0 ; un résultat partiel est un échec.
5. Exécuter la vérification ci-dessous avant copie hors machine. Ne pas transmettre
   le dossier source en clair ni le secret restic.

Exemples de syntaxe à adapter après validation, **non exécutés**, sans mot de passe
dans la commande (`$n8nResticRepo`, `$n8nCaptureDir`, `$n8nRestoreDir` sont des chemins
absolus vérifiés hors du dépôt Git ; `$n8nSnapshotId` est le snapshot choisi) :

```powershell
restic -r $n8nResticRepo init --repository-version 2
if ($LASTEXITCODE -ne 0) { throw 'Initialisation restic échouée' }
restic -r $n8nResticRepo backup $n8nCaptureDir --tag n8n-cold --host prospectioncrm-local --group-by host,tags
if ($LASTEXITCODE -ne 0) { throw 'Sauvegarde restic incomplète ou échouée' }
restic -r $n8nResticRepo check --read-data
if ($LASTEXITCODE -ne 0) { throw 'Intégrité restic non validée' }
restic -r $n8nResticRepo snapshots --tag n8n-cold
if ($LASTEXITCODE -ne 0) { throw 'Inventaire restic échoué' }
```

`init` ne fait partie que de la première utilisation, pas de chaque sauvegarde.
[Sauvegarde restic](https://restic.readthedocs.io/en/stable/040_backup.html).

## Intégrité et restauration isolée

`check` seul vérifie surtout la structure ; `check --read-data` lit aussi les
données du dépôt. Faire ce contrôle complet sur la copie locale initiale, puis
périodiquement et sur la copie hors machine. Un échantillonnage ne prouve pas
l'intégrité de toutes les données. La lecture distante peut coûter du temps et
de la bande passante ; elle n'est pas exécutée dans cette phase.

1. Sélectionner un snapshot précis, effectuer `check --read-data`.
2. Créer une destination locale privée, neuve et vide, sans lien symbolique vers
   des données réelles. Examiner `restic ls $n8nSnapshotId` (code retour obligatoire)
   pour choisir le sous-dossier exact de capture dans l'arbre du snapshot.
   Sous Windows, restaurer ce sous-dossier évite de restaurer les métadonnées
   des parents système comme `C:\Users` dans la destination. La restauration
   complète initiale a échoué sur un timestamp de ce parent ; la reprise dans
   un nouveau dossier avec la sélection ci-dessous a réussi sans élévation.
   `$n8nSnapshotSubdir` est un chemin interne restic issu de `ls`, pas un chemin
   hôte supposé. Restaurer uniquement dans cette destination, sans `--delete` :

   ```powershell
   $n8nRestoreSelector = "${n8nSnapshotId}:$n8nSnapshotSubdir"
   restic -r $n8nResticRepo restore $n8nRestoreSelector --target $n8nRestoreDir --overwrite never --verify
   if ($LASTEXITCODE -ne 0) { throw 'Restauration échouée : conserver cet essai' }
   ```

   La restauration des descripteurs de sécurité Windows complets exige des
   privilèges supplémentaires ; seuls les DACL sont restaurés sans ces privilèges.
   Aucun n'a été demandé ici. Vérifier les ACL de destination ; les UID/GID/modes
   Linux de n8n sont préservés à l'intérieur du tar, puis restaurés sous Linux.
3. Comparer l'empreinte du tar restauré au manifeste ; vérifier sa structure,
   l'absence de chemin absolu/traversée et de lien sortant avant extraction.
4. Suivre la [restauration isolée](n8n-operations.md#restauration-et-installation-vierge-isolées)
   vers un volume neuf : auxiliaire d'extraction avec droits ciblés, UID/GID/modes
   préservés, n8n ensuite sous son utilisateur normal, sans réseau ni port publié.
5. Vérifier intégrité SQLite, disponibilité, fichiers attendus et préservation
   de clé sans jamais en afficher la valeur. Des données synthétiques sont requises
   pour la première démonstration. Aucune restauration réelle n'est autorisée ici.
6. Tester ultérieurement la récupération par l'opérateur depuis une autre machine,
   avec secret récupéré hors du poste original. Consigner seulement pass/fail,
   version, snapshot et durée ; ne pas conserver de sortie déchiffrée dans Git.

Un contrôle cryptographique réussi ne remplace pas le démarrage isolé ni la
vérification applicative. Une restauration de credentials réels nécessiterait
un protocole distinct sans appel fournisseur et sans extraction de leurs valeurs.
[Contrôles](https://restic.readthedocs.io/en/stable/045_working_with_repos.html),
[restauration](https://restic.readthedocs.io/en/stable/050_restore.html).

## Copie hors machine sans fournisseur imposé

**Destination toujours non choisie.** Le dépôt local d'essai ci-dessous ne
constitue pas une sauvegarde hors machine et ne contient aucune donnée réelle.

Premier choix économique : un support externe contenant uniquement le dépôt
restic chiffré, déconnecté et conservé dans un autre lieu. Une copie sur le même
disque, un support toujours branché ou une simple synchronisation ne suffit pas
à couvrir une panne physique et une suppression malveillante.

Copier l'intégralité du dépôt au repos, sans backup/forget/prune concurrent, puis
vérifier la copie. Une seconde destination restic peut aussi être alimentée par
`restic copy` après choix de ses secrets et validation ; ne pas synchroniser des
fichiers en cours de mutation. Éviter toute synchronisation avec suppression
automatique qui propagerait une perte. Ne pas mettre le mot de passe avec la copie.

Un stockage distant SFTP ou objet pourra remplacer le support après choix du
fournisseur, des accès, des coûts et d'une conservation indépendante. Le
chiffrement restic se fait côté client avant transfert. Prévoir des droits minimaux
et, si disponible, une copie immuable ou hors ligne. Aucun transfert, fournisseur,
abonnement ni planification n'est mis en place en 9.2.2.

## Démonstration synthétique reproductible

Les résultats, empreintes, snapshot exact et artefacts de l'exécution du
12 octobre sont dans le [complément de validation](phase-9-2-step-2-validation.md#f-complément-autorisé--sauvegardes-synthétiques-et-décisions).
Ne pas rejouer les créations sur ces chemins ou noms conservés. Pour un nouvel
essai, utiliser un suffixe aléatoire neuf, refuser toute collision et créer une
racine privée hors Git. Exemple d'organisation :

```text
<racine unique>/tool/            binaire officiel vérifié
<racine unique>/gnupg/           trousseau public de vérification dédié
<racine unique>/capture/         tar synthétique source uniquement
<racine unique>/capture2/        même tar sous un autre chemin, test de groupes
<racine unique>/repository/      dépôt restic v2 neuf
<racine unique>/restore/         premier essai conservé si échec
<racine unique>/restore-retry/   destination neuve du sous-dossier restauré
<racine unique>/secrets/         mots de passe de test hors sauvegarde
<racine unique>/cache/           cache restic séparé
<racine unique>/evidence/        résultats sans valeurs de secrets
```

Variables des extraits PowerShell ci-dessous : `$testRoot` désigne cette racine ;
`$image` vaut `docker.n8n.io/n8nio/n8n:2.40.7` ; `$sourceVolume`, `$restoreVolume`,
`$sourceContainer` et `$restoreContainer` sont des noms réservés à ce nouvel essai.
Les chemins et noms doivent avoir été contrôlés absents (`Test-Path`, inventaires
Docker avec vérification de `$LASTEXITCODE`) avant création. Un dossier présent
mais vide n'autorise pas sa réutilisation s'il n'a pas été créé pour cet essai.

Pour les fichiers de mots de passe d'essai, après création de la racine et des
sous-dossiers, appliquer et contrôler les ACL **avant** génération :

```powershell
$operatorSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$systemSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$acl = [System.Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($operatorSid)
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($operatorSid, $systemSid)) {
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $testRoot -AclObject $acl -ErrorAction Stop
$allowed = @($operatorSid.Value, $systemSid.Value)
$actualAcl = Get-Acl -LiteralPath $testRoot
if (-not $actualAcl.AreAccessRulesProtected) { throw 'Héritage ACL non bloqué' }
foreach ($name in @('restic-password.txt', 'wrong-password.txt')) {
    $file = Join-Path "$testRoot\secrets" $name
    if (Test-Path -LiteralPath $file) { throw 'Collision de secret' }
    # Contrôler aussi le dossier secrets avant toute écriture.
    $rules = (Get-Acl "$testRoot\secrets").GetAccessRules(
        $true, $true, [System.Security.Principal.SecurityIdentifier])
    if ($rules.Count -ne 2 -or @($rules | Where-Object {
        $_.IdentityReference.Value -notin $allowed -or $_.AccessControlType -ne 'Allow'
    }).Count) { throw 'ACL de secrets inattendue' }
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [System.IO.File]::WriteAllText($file, [Convert]::ToBase64String($bytes),
        [System.Text.UTF8Encoding]::new($false))
    [Array]::Clear($bytes)
    $rules = (Get-Acl $file).GetAccessRules(
        $true, $true, [System.Security.Principal.SecurityIdentifier])
    if ($rules.Count -ne 2 -or @($rules | Where-Object {
        $_.IdentityReference.Value -notin $allowed -or $_.AccessControlType -ne 'Allow'
    }).Count) { throw 'ACL du fichier inattendue' }
}
```

Créer le volume source, démarrer n8n sans port ni réseau et vérifier sa readiness
avec Node depuis le conteneur, comme dans le [guide d'exploitation](n8n-operations.md#restauration-et-installation-vierge-isolées).
Options effectivement utilisées : `--pull never --network none --cap-drop ALL
--security-opt no-new-privileges`, volume neuf sur `/home/node/.n8n`, trois services
distants désactivés et `N8N_LOG_LEVEL=error`. Ne pas afficher les logs ou la config.
Après readiness HTTP 200, arrêter seulement cette source synthétique, confirmer
`exited 0`, puis capturer tout le volume arrêté :

```powershell
docker run --rm --pull never --network none --read-only --cap-drop ALL --security-opt no-new-privileges --mount "type=volume,source=$sourceVolume,target=/source,readonly" --mount "type=bind,source=$testRoot\capture,target=/capture" --entrypoint sh $image -c 'umask 077; tar -cpf /capture/n8n-synthetic.tar -C /source .'
if ($LASTEXITCODE -ne 0) { throw 'Capture à froid échouée' }
$sourceHash = (Get-FileHash "$testRoot\capture\n8n-synthetic.tar" -Algorithm SHA256).Hash
$resticExe = "$testRoot\tool\portable\restic_0.19.1_windows_amd64.exe"
$common = @('--repo', "$testRoot\repository", '--password-file',
    "$testRoot\secrets\restic-password.txt", '--cache-dir', "$testRoot\cache")
& $resticExe @common init --repository-version 2
if ($LASTEXITCODE -ne 0) { throw 'Initialisation échouée' }
$backup = & $resticExe @common backup "$testRoot\capture\n8n-synthetic.tar" --tag n8n-cold --host prospectioncrm-synthetic --group-by host,tags --json
if ($LASTEXITCODE -ne 0) { throw 'Sauvegarde échouée' }
$summary = @($backup | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object message_type -eq summary)
if ($summary.Count -ne 1 -or -not $summary[0].snapshot_id) { throw 'Snapshot ambigu' }
$snapshotId = $summary[0].snapshot_id
& $resticExe @common check --read-data
if ($LASTEXITCODE -ne 0) { throw 'Intégrité non validée' }
& $resticExe --repo "$testRoot\repository" --password-file "$testRoot\secrets\wrong-password.txt" --cache-dir "$testRoot\cache" snapshots --json
if ($LASTEXITCODE -ne 12) { throw 'Refus du mauvais mot de passe non confirmé' }
```

L'exécutable 0.19.1 a renvoyé 12 et le diagnostic de mot de passe incorrect,
pas une erreur d'accès au chemin. Seul le tar est donné à `backup`, jamais
`$testRoot`, le dépôt, le cache ou `secrets/`. Les métadonnées des répertoires
parents et le nom de compte Windows font partie du snapshot ; aucun autre fichier
de ces répertoires n'est sauvegardé. Ne pas publier les sorties JSON brutes.

Sélectionner le sous-dossier de capture via `ls`, en conservant le snapshot exact :

```powershell
$inventory = & $resticExe @common ls $snapshotId --json
if ($LASTEXITCODE -ne 0) { throw 'Inventaire échoué' }
$archiveNode = @($inventory | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.type -eq 'file' -and $_.name -eq 'n8n-synthetic.tar' })
if ($archiveNode.Count -ne 1) { throw 'Archive ambiguë' }
$snapshotSubdir = $archiveNode[0].path.Substring(0, $archiveNode[0].path.LastIndexOf('/'))
# restore-retry doit être une destination neuve, vide et privée de ce seul essai.
& $resticExe @common restore "${snapshotId}:$snapshotSubdir" --target "$testRoot\restore-retry" --overwrite never --verify
if ($LASTEXITCODE -ne 0) { throw 'Restauration échouée : conserver cet essai' }
if ((Get-FileHash "$testRoot\restore-retry\n8n-synthetic.tar" -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'Empreinte restaurée différente'
}
```

Avant extraction, vérifier base/config présentes, noms relatifs sans traversée,
fichiers/répertoires ordinaires seulement ; refuser un lien non vérifié. Dans un
second volume neuf, extraction avec les capacités CHOWN/FOWNER/DAC_OVERRIDE :

```powershell
docker run --rm --pull never --network none --read-only --user 0 --cap-drop ALL --cap-add CHOWN --cap-add FOWNER --cap-add DAC_OVERRIDE --security-opt no-new-privileges --mount "type=bind,source=$testRoot\restore-retry,target=/archive,readonly" --mount "type=volume,source=$restoreVolume,target=/restore" --entrypoint sh $image -c 'test -z "$(ls -A /restore)" && tar -xpf /archive/n8n-synthetic.tar -C /restore'
if ($LASTEXITCODE -ne 0) { throw 'Extraction échouée : conserver le volume' }
```

Comparer les deux arborescences montées readonly : types, chemins, octets,
UID/GID/modes, clé en mémoire et `PRAGMA integrity_check` via `node:sqlite`
readonly. Ne démarrer le restauré qu'après succès, sous l'utilisateur `node`,
sans réseau ni port. Readiness HTTP 200, puis arrêt ; aucune connexion UI déduite.

Pour le test de regroupement, copier uniquement le tar dans `capture2/`, créer
un deuxième snapshot avec la même commande `backup`, le même hôte et tag, puis :

```powershell
$before = & $resticExe @common snapshots --json
if ($LASTEXITCODE -ne 0) { throw 'Inventaire avant échoué' }
& $resticExe @common forget --dry-run --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --json
if ($LASTEXITCODE -ne 0) { throw 'Simulation par défaut échouée' }
& $resticExe @common forget --dry-run --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --group-by host,tags --host prospectioncrm-synthetic --tag n8n-cold --json
if ($LASTEXITCODE -ne 0) { throw 'Simulation host,tags échouée' }
$after = & $resticExe @common snapshots --json
if ($LASTEXITCODE -ne 0) { throw 'Inventaire après échoué' }
if (((($before | ConvertFrom-Json).id | Sort-Object) -join ',') -ne
    ((($after | ConvertFrom-Json).id | Sort-Object) -join ',')) { throw 'Snapshots modifiés' }
& $resticExe @common check --read-data
if ($LASTEXITCODE -ne 0) { throw 'Contrôle final échoué' }
```

Conserver volumes, conteneurs arrêtés et artefacts pour l'audit. Aucun `forget`
effectif, `prune`, `repair`, suppression de volume ou nettoyage global.
