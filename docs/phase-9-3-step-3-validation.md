# Phase 9.3.3 — Provisionnement humain et échange réel technical.probe

## État au 15 octobre 2026

La première étape est confirmée par l'utilisateur : provisionnement, import des
User Secrets API, HTTPS approuvé, connexion Blazor, consultation et déconnexion
réussis. Cette confirmation interactive complète le compte rendu historique du
14 octobre ci-dessous ; ses mentions « en attente » décrivent l'état à cette date.

La deuxième étape a maintenant produit **un aller-retour réel CRM → n8n → CRM**
en HTTPS avec HMAC dans les deux sens : une opération, une tentative, une réception,
puis traitement explicite et état `succeeded`. La reprise après redémarrage de
l'API est validée avec cette même sonde. Les détails et la limite du contrôle
historique `ConnectionId=NULL` figurent dans la section « Deuxième étape ».
La validation définitive de 9.3.3 reste conditionnée au succès de la CI distante
du commit documentaire. La Phase 9.4 n'est pas commencée.

Les chemins privés de la machine sont remplacés ici par des repères génériques :
`<dossier-prive-provisionnement>`, `<dossier-prive-sonde>`, `<powershell-7>` et
`<depot>`. Les commandes historiques ci-dessous sont documentaires, avec ces
substitutions ; elles ne doivent pas être relancées pour cette validation Git.

## Provisionnement humain local — préparation du 14 octobre 2026

La base Development réelle est préparée. La saisie du mot de passe, le
provisionnement, l'import et le contrôle navigateur restent **en attente de
l'opérateur**. Aucune validation interactive n'est encore acquise et la Phase
9.3.3 complète n'est pas terminée. Le raccordement réel à n8n est hors de cette étape.

Référence : dépôt `HellzDraft/ProspectionCrm`, branche `main`, HEAD
`060ae6c0b4e5dc15648a481c2e79ddf35ad8e7ed`.
La [CI #60](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38543689580)
de cette référence est réussie : 1 336 tests, aucun échec ni ignoré.
L'index et le working tree étaient propres au début de cette intervention.
Aucune suite complète ni test applicatif n'a été relancé.

### Destination et état initial

La connexion Development des User Secrets API a été rapprochée, sans affichage
de ses valeurs sensibles, du conteneur existant `prospectioncrm-postgres-1` :
hôte local, port `5432`, même base et même rôle que le PostgreSQL existant.
La publication Docker est limitée à `127.0.0.1:5432` ; le volume existant
`prospectioncrm_postgres_data` a été conservé. Le serveur est PostgreSQL
`18.6 (Debian 18.6-1.pgdg13+2)`.

Le conteneur était arrêté ; il a été démarré seul, sans recréation de conteneur
ou de volume. L'API et Blazor étaient arrêtés et leurs ports libres. Aucun autre
client de cette base n'était actif au contrôle préalable à la migration.
n8n était arrêté et n'a pas été démarré.

La lecture SQL a trouvé exactement un compte et un workspace actif, avec le
rattachement existant suivant, utilisé par les commandes préparées :

- utilisateur : `10000000-0000-0000-0000-000000000001` ;
- workspace : `20000000-0000-0000-0000-000000000001`.

Aucun nom, email, contenu métier, identifiant de connexion SQL ou secret n'a
été affiché. Les valeurs des User Secrets existants n'ont pas été modifiées.
Aucune configuration `Operator` n'y était présente, ni dans les variables
d'environnement contrôlées. La connexion SQL et l'autre clé existante sont conservées.

### Point de retour PostgreSQL créé et vérifié

Un nouveau point de retour PostgreSQL ciblé a été créé avant les migrations,
sans réutiliser ni manipuler les sauvegardes n8n/restic antérieures :

```text
<dossier-prive-provisionnement>\postgres-before-phase932.dump
```

- Création : 14 octobre 2026 à 12:12:13, Europe/Paris.
- Format custom `pg_dump`, 140 324 octets, 296 entrées dans la table des matières.
- Dossier privé hors Git : accès limité au compte Windows courant et à SYSTEM ;
  ACL du fichier de sauvegarde également contrôlée.
- `pg_dump` terminé avec code 0 ; `pg_restore --list` réussi.
- Décodage complet par `pg_restore --exit-on-error --file=/dev/null` réussi,
  sans connexion à une destination de restauration et sans restauration de base.
- Présence de l'historique EF, des comptes et des workspaces vérifiée dans
  la table des matières. L'empreinte du fichier a été conservée dans les
  métadonnées privées puis revérifiée avant migration.

Les identifiants nécessaires à `pg_dump` sont lus dans l'environnement du
conteneur existant ; aucun mot de passe n'est passé dans les arguments ou affiché.
Ce contrôle confirme la production et la lisibilité de l'archive ; il ne constitue
pas un essai de restauration. L'archive et ses métadonnées restent conservées.

### Migrations réellement appliquées

Avant intervention, seule `20260925101154_InitialCrmSchema` était présente.
Cet historique correspondait exactement au début de la chaîne versionnée.
Les 14 migrations suivantes étaient en attente et ont toutes été appliquées,
dans cet ordre :

```text
20260929150147_Phase42PipelineLifecycleAndDefault
20261006091016_Phase621IngestionHistoryFoundation
20261006101923_Phase622PersistentSourceIdentities
20261007064337_Phase71PersistentCollectionJobs
20261007074738_Phase72CollectionWorkerLeases
20261007083908_Phase73CollectionRetries
20261007094120_Phase74CollectionScheduling
20261007124044_Phase81AutomationRuntimeSettings
20261008083956_Phase82AutomationJobs
20261008124357_Phase84AutomationExecutionRuntime
20261009115938_Phase85AutomationActionRequests
20261009144211_Phase86AutomationCircuitResets
20261013143800_Phase931IntegrationPersistence
20261014075029_Phase932IntegrationConnection
```

Commande exécutée depuis le dépôt, avec `ASPNETCORE_ENVIRONMENT` et
`DOTNET_ENVIRONMENT` fixés à `Development` uniquement dans le processus, et les
quatre indicateurs transport/workers/scheduler forcés à `false` :

```powershell
dotnet ef database update 20261014075029_Phase932IntegrationConnection --project '<depot>\src\ProspectionCrm.Api\ProspectionCrm.Api.csproj' --configuration Release --no-build
```

Code de sortie 0. Les sorties détaillées sont conservées dans le dossier privé,
sans publication de diagnostics susceptibles de contenir des informations locales.
Les variables temporaires ont été rétablies ; aucune variable Windows globale
n'a été modifiée. Aucun `Down`, reset, drop de base, rebaseline ou restauration.

Contrôles SQL après application :

- les 15 migrations de la chaîne sont présentes, aucune en attente ;
- dernière migration : `20261014075029_Phase932IntegrationConnection` ;
- `IntegrationOperations.ConnectionId` est un UUID nullable et sa contrainte
  `CK_IntegrationOperations_Connection` est présente et validée ;
- compte et workspace existants conservés, rattachement inchangé ; comparaison
  des empreintes des lignes réussie sur les propriétés préexistantes du workspace,
  la nouvelle colonne `DefaultPipelineId` étant exclue de cette comparaison ;
- nombres de pipelines et d'opportunités inchangés : un de chaque, déjà existants ;
- aucune opération d'intégration ; paramétrage runtime d'automatisation désactivé,
  en mode `manual`, conformément à la migration versionnée.

### HTTPS et services

Commande effectivement exécutée :

```powershell
dotnet dev-certs https --check --trust
```

Résultat : code 0, certificat de développement `localhost` existant et approuvé,
valide du 1er juillet 2026 au 1er juillet 2027. Aucun certificat créé/exporté,
aucune modification de confiance, aucun `--clean`, aucun contournement TLS.

| Composant | État observé à la livraison |
| --- | --- |
| PostgreSQL existant | Démarré pour cette intervention, laissé disponible pour le provisionnement, publication loopback uniquement |
| API | Arrêtée ; démarrage en attente de provisionnement/import, profil HTTPS `https://localhost:7096` |
| Blazor | Arrêté ; démarrage manuel préparé, profil HTTPS `https://localhost:7075` |
| n8n | Non démarré pendant cette intervention |
| Transport, workers de collecte/automatisation, scheduler | Désactivés dans la configuration versionnée ; démarrage API préparé avec les quatre valeurs forcées à `false` dans le processus |

### Passage opérateur : provisionnement et import

Les scripts sans secret sont préparés dans le dossier privé ci-dessous.
Leur syntaxe PowerShell a été vérifiée. Le script de sauvegarde a été exécuté ;
les scripts de provisionnement, d'import et de démarrage **n'ont pas été exécutés**.
Le fichier temporaire de credentials et le marqueur d'import n'existent pas encore.

```text
<dossier-prive-provisionnement>
```

Les commandes utilisent le chemin complet de PowerShell 7 constaté sur cette
machine, pour ne pas dépendre de sa présence dans le PATH du terminal personnel.
Aucune modification de politique d'exécution n'est nécessaire ni effectuée.

**La saisie est invisible. À chaque invite, saisissez ou collez une seule fois
le mot de passe CRM enregistré dans Bitwarden, puis appuyez sur Entrée.**

Dans un terminal PowerShell personnel, exécuter une seule fois :

```powershell
& '<powershell-7>' -NoProfile -File '<dossier-prive-provisionnement>\provision-operator.ps1'
```

Ce script vérifie l'absence de configuration `Operator`, de variable susceptible
de remplacer les User Secrets, de fichier cible et d'API en cours d'exécution.
Il fixe l'environnement Development et désactive les quatre indicateurs uniquement
pour son processus. Son appel, avec les chemins privés remplacés, est le suivant :

```powershell
dotnet run --project '<depot>\src\ProspectionCrm.Api\ProspectionCrm.Api.csproj' --configuration Release --no-build --no-restore --no-launch-profile -- --provision-operator --user-id '10000000-0000-0000-0000-000000000001' --workspace-id '20000000-0000-0000-0000-000000000001' --output '<dossier-prive-provisionnement>\operator-provisioning.json'
```

Ne pas exécuter cette seconde commande en plus du script : elle décrit son appel.
Les deux invites sont la saisie et sa confirmation ; le mot de passe doit respecter
la borne existante de 12 à 256 caractères. Aucun `--bootstrap-empty`, aucune
réattribution du propriétaire, aucun mot de passe dans les arguments ou dans ce rapport.

Après réussite, le script appelle `import-operator.ps1`, qui :

1. refuse de remplacer une configuration Operator déjà existante ;
2. vérifie les trois clés, les deux UUID attendus et le format Identity V3 du hash,
   sans afficher leurs valeurs sensibles ;
3. transmet le JSON sur l'entrée standard de `dotnet user-secrets set --project`
   pour le projet API uniquement, sans argument contenant le hash ;
4. relit les trois valeurs importées et vérifie la conservation exacte de toutes
   les autres valeurs existantes, dont la connexion SQL ;
5. supprime uniquement `operator-provisioning.json`, après confirmation de l'import
   et vérification que ce fichier n'a pas changé ;
6. écrit `operator-import-status.json`, qui contient uniquement l'heure et le bilan
   des vérifications, puis affiche le message de réussite.

En cas d'erreur, ne pas relancer aveuglément le provisionnement : les fichiers
restent disponibles pour un diagnostic ciblé, sans réinitialisation automatique.
Si le provisionnement a réussi mais que l'import seul doit être repris après
diagnostic, sa commande est :

```powershell
& '<powershell-7>' -NoProfile -File '<dossier-prive-provisionnement>\import-operator.ps1'
```

### Démarrage après import confirmé

Dans un terminal personnel réservé à l'API :

```powershell
& '<powershell-7>' -NoProfile -File '<dossier-prive-provisionnement>\start-api.ps1'
```

Le script vérifie les paramètres Operator attendus et l'absence d'instance/port
API déjà utilisé. Il impose `Development`, `Integrations__Enabled=false`,
`AutomationWorker__Enabled=false`, `SourceCollectionWorker__Enabled=false` et
`SourceCollectionScheduler__Enabled=false` pour ce processus, puis exécute :

```powershell
dotnet run --project '<depot>\src\ProspectionCrm.Api\ProspectionCrm.Api.csproj' --configuration Release --no-build --no-restore --launch-profile https
```

Dans un second terminal personnel réservé à Blazor :

```powershell
& '<powershell-7>' -NoProfile -File '<dossier-prive-provisionnement>\start-blazor.ps1'
```

Le script refuse un port Blazor déjà utilisé, impose Development pour son processus
et exécute :

```powershell
dotnet run --project '<depot>\src\ProspectionCrm.Blazor\ProspectionCrm.Blazor.csproj' --configuration Release --no-build --no-restore --launch-profile https
```

Les profils versionnés restent inchangés et annoncent uniquement les URL HTTPS
localhost prévues. Le frontend vise `https://localhost:7096/`. Aucun secret n'est
placé dans Blazor. Aucune ouverture LAN/Internet, modification du pare-feu,
modification de l'antiforgery ou création de clé HMAC.

### Contrôle navigateur manuel — en attente

Le parcours est à réaliser uniquement par l'utilisateur :

1. ouvrir `https://localhost:7075` ;
2. se connecter avec le nouveau mot de passe CRM ;
3. consulter la liste des pipelines existants, sans créer ni modifier de donnée ;
4. se déconnecter et constater le retour à l'état non connecté.

Les quatre résultats restent **non confirmés**. Toute confirmation ultérieure sera
consignée comme retour interactif de l'utilisateur, et non comme test automatisé
exécuté par Codex. Aucun compte ou code 2FA n8n n'est nécessaire ; aucun cookie,
mot de passe ou hash ne doit être transmis dans la conversation.

### Arrêt des seuls composants de cette intervention

Après le contrôle, utiliser **Ctrl+C** dans chacun des deux terminaux API/Blazor
ouverts pour cette intervention. Puis arrêter le seul PostgreSQL démarré ici :

```powershell
docker stop prospectioncrm-postgres-1
```

Le conteneur et son volume sont conservés. Ne pas utiliser `docker compose down`,
supprimer de volume ou arrêter les autres composants. Cet arrêt final n'est pas
encore exécuté : PostgreSQL reste nécessaire au passage opérateur.

## État Git et limites de cette livraison

Seul nouveau fichier dans Git : `docs/phase-9-3-step-3-validation.md`.
Les scripts, sauvegarde, métadonnées et journaux locaux restent hors dépôt.
`git diff --check` et le contrôle du nouveau document sont réussis ; index vide,
aucun staging, commit ou push. Le HEAD reste celui de 9.3.2.

Aucun défaut applicatif empêchant le parcours n'a été observé pendant cette
préparation ; le provisionnement interactif et le navigateur restent à exercer.
Aucune action n8n, aucun raccordement fournisseur, aucune manipulation des
sauvegardes antérieures. La suite de 9.3.3 consacrée à l'échange réel est différée.

## Deuxième étape — échange réel du 15 octobre 2026

### Contrôle initial et périmètre

Branche `main`, HEAD `060ae6c0b4e5dc15648a481c2e79ddf35ad8e7ed`, index vide.
Seul fichier local initial : ce rapport, encore non suivi. Docker Desktop
29.6.1 disponible ; `prospectioncrm-postgres-1` healthy, publication
`127.0.0.1:5432`. n8n arrêté ; API et Blazor arrêtés. Aucun paramètre d'intégration
dans les User Secrets avant préparation : la valeur versionnée `Enabled=false`
s'appliquait. Les trois paramètres Operator sont présents, sans affichage des
valeurs sensibles. Aucune migration supplémentaire.

Avant démarrage, lecture SQLite du volume n8n en lecture seule, depuis un
auxiliaire de la même image, sans réseau : zéro workflow, credential et exécution ;
un propriétaire, non désactivé, avec MFA ; marqueur d'initialisation `true`.
Le conteneur existant est démarré par `docker compose --profile integrations start n8n`.
Image et version constatées : `docker.n8n.io/n8nio/n8n:2.40.7` ; volume conservé
`prospectioncrm_n8n_data`, conteneur non recréé, readiness 200 et santé healthy.
Aucun reset, restauration, changement de compte ou manipulation des sauvegardes.

### Canal HTTPS local réellement utilisé

```text
API Windows
  → https://localhost:7443/webhook/crm-technical-probe
  → proxy Kestrel Windows, loopback seulement
  → http://127.0.0.1:5678/webhook/crm-technical-probe
  → workflow n8n dans Docker Desktop
  → https://localhost:7096/api/integrations/results
     connexion réseau via host.docker.internal, identité TLS localhost
  → API Windows, loopback seulement
```

Les liaisons écoutées ont été vérifiées : `127.0.0.1` et `::1` pour 7443, 7096
et 7075 ; `127.0.0.1` pour le port n8n 5678 publié. Aucun `0.0.0.0`, changement
de pare-feu, DNS Windows, fichier hosts, CORS ou profil de lancement applicatif.
Docker Desktop peut atteindre le loopback de l'hôte via sa route hôte : cette
propriété est testée ici, elle ne doit pas être supposée sur une autre machine.

Le proxy utilise le certificat de développement **déjà approuvé** dans
`CurrentUser/My`, empreinte publique
`9343B8121AE68AAF62753E4894B659676D1BE80A`, valide du 1er juillet 2026 au
1er juillet 2027. Aucun certificat créé et aucune clé privée exportée. Seule
sa partie publique PEM est fournie au client n8n comme ancre de confiance locale.

Le client HTTPS du nœud conserve `hostname: localhost`, le SNI et la vérification
de nom standard. Sa fonction `lookup` résout uniquement l'adresse de connexion
par `dns.lookup('host.docker.internal', ...)`. `rejectUnauthorized: true` reste
actif ; aucun `checkServerIdentity` personnalisé, aucune désactivation TLS.
Le nom logique et le certificat concordent. Ce choix évite de remplacer le nom
TLS par une IP ou d'élargir le binding API.

Avant activation des User Secrets d'intégration : .NET `HttpClient`, sans proxy
ambiant ni callback de certificat, obtient 200 sur
`https://localhost:7443/healthz/readiness` et valide TLS sur l'URL finale du
webhook (404 avant sa disponibilité). Depuis le conteneur n8n, HTTPS vers
`https://localhost:7096/api/session` donne le 401 anonyme attendu avec
`socket.authorized=true` et `servername=localhost`.

Le proxy n'autorise que GET `/healthz/readiness` et POST
`/webhook/crm-technical-probe`, cible brute exacte, sans query string. Il copie
les octets du corps, `Content-Type` et les trois en-têtes HMAC ; il ne transmet
pas les cookies humains. Corps borné à 4 096 octets, aucune redirection suivie,
aucun proxy/credential/cookie ambiant, destination HTTP fixée à n8n loopback.
Le seul tronçon HTTP est cette liaison locale explicite de terminaison TLS.

### Identité logique et stockage

| Paramètre public | Valeur |
| --- | --- |
| ConnectionId stable | `2500eecc-06eb-4568-92e2-00e9038610ac` |
| WorkspaceId | `20000000-0000-0000-0000-000000000001` |
| Clé CRM → n8n, ID | `crm-probe-out-20261015` |
| Clé n8n → CRM, ID | `n8n-probe-out-20261015` |
| Endpoint CRM | `https://localhost:7443/webhook/crm-technical-probe` |
| Callback logique | `https://localhost:7096/api/integrations/results` |

Deux valeurs aléatoires cryptographiques distinctes de 32 octets ont été créées.
Les clés, leurs liens connexion/workspace, IDs et activation sont importés dans
les **User Secrets du projet API uniquement**. Timeout 15 secondes, concurrence 2,
fenêtre temporelle 300 secondes. L'API et Blazor étaient arrêtés lors de la
modification des secrets. Relecture sans divulgation : configuration importée
conforme et toutes les valeurs antérieures non concernées inchangées.

Côté n8n, un credential de type local `crmProbe`, nommé
`CRM phase 9.3.3 local probe`, contient les deux clés et le certificat **public**.
Il est chiffré par le mécanisme natif n8n avec la clé existante de l'instance,
dans son volume existant. Import CLI depuis un fichier transitoire mode 0600
dans le conteneur, alimenté sur stdin et supprimé immédiatement après import.
Les secrets ne sont ni des paramètres de workflow, ni des sorties du nœud.
Aucun secret dans Blazor, `ConfigurationJson`, les exports ou le rapport.

Le dossier local privé des outils, du certificat public, des preuves et journaux
est le suivant ; ses ACL sont limitées au compte Windows courant et à SYSTEM :

```text
<dossier-prive-sonde>
```

Le proxy autonome et sa configuration sont sous `proxy/` dans ce dossier ;
le certificat privé reste dans le magasin Windows. Il s'agit d'un utilitaire
local hors dépôt, sans changement du C# applicatif. Son arrêt ferme 7443 ; aucune
installation de service ni tâche de démarrage n'a été créée.

### Workflow, octets et protocole

Workflow `Phase 9.3.3 - technical.probe local`, ID local `phase933TechnicalProbe` :

1. **Receive raw command** : Webhook POST, chemin `crm-technical-probe`,
   `rawBody=true`, réponse déléguée au nœud Respond to Webhook.
2. **Verify and callback** : petit nœud local `CUSTOM.crmProbe`, credential natif
   requis. Vérification des trois en-têtes, formats, clé attendue et fenêtre ±300 s ;
   calcul du SHA256 sur le Buffer brut, HMAC v1 et comparaison en temps constant.
   Ensuite seulement, décodage UTF-8 strict et validation de version/type/UUID,
   structure et propriétés du contrat. Construction d'un nouveau message de
   résultat, conservation du contexte et de `probeId`, `outcome=succeeded`.
   Le même Buffer résultat est signé et envoyé en HTTPS, avec l'autre clé.
3. **Return transport status** : 202 seulement après le callback CRM 202 confirmé
   et sa réponse `status=received`. Un rejet de signature donne 401 ; contrat
   invalide après signature valide : 400. Une erreur de callback ne produit pas
   d'ACK 202.

Le code installé de **n8n 2.40.7** a été lu : l'option Raw Body expose
`req.rawBody` en base64 dans `binary.data`. Le nœud utilise
`helpers.getBinaryDataBuffer(0, 'data')`, jamais une reconstruction de `json.body`.
La [documentation officielle du Webhook](https://docs.n8n.io/integrations/builtin/core-nodes/n8n-nodes-base.webhook/)
décrit l'option Raw Body ; le contrôle du code installé et les appels réels
établissent son comportement pour cette version.

Les sept lignes HMAC sont celles de 9.3.2 : UTF-8, LF sans LF final, SHA256
hexadécimal minuscule, audience `crm-to-n8n` puis `n8n-to-crm`, méthode POST,
cibles exactes ci-dessus. Les clés Base64 sont décodées en octets avant HMAC.
Le nœud est dédié à cette sonde locale ; il n'ouvre aucune connexion PostgreSQL,
ne dispose d'aucun credential CRM métier et n'appelle que le callback fixé.
Les workers collecte/automatisation et le scheduler restent désactivés.

Contrôle des octets avant la sonde : un corps JSON volontairement espacé, signé
sur ses octets exacts mais ne respectant pas le contrat, donne **400** ; les mêmes
octets avec mauvaise signature donnent **401**. Cela distingue la validation
cryptographique de la validation du JSON. Ces deux exécutions n'envoient aucun
callback et ne créent aucune opération CRM.

Un export du workflow réellement installé a été produit puis réduit à une liste
de champs autorisés, désactivé et débarrassé de toutes les références credentials,
identifiants d'instance/version, projets et métadonnées de partage. Fichiers :

- [workflow.json](integrations/technical-probe-local/workflow.json) ;
- [CrmProbe.node.js](integrations/technical-probe-local/CrmProbe.node.js) ;
- [CrmProbe.credentials.js](integrations/technical-probe-local/CrmProbe.credentials.js).

Les deux fichiers JS sont le code effectivement installé dans
`/home/node/.n8n/custom/phase933`. Le fichier credentials décrit les champs ; il
ne contient aucune valeur. Pour réimporter ailleurs, installer ces types locaux,
créer le credential dans n8n avec les secrets et la confiance propres à la
destination, puis le sélectionner dans le nœud. L'export n'est pas activable
sans ce rattachement explicite. Le routage Docker Desktop et les URL fixes du
nœud doivent être revérifiés ; ce n'est pas un workflow de production générique.

### Parcours positif observé

L'utilisateur s'est connecté dans une fenêtre Edge dédiée sur Blazor HTTPS et
l'a confirmé dans cette conversation. Contrôle indépendant : `/api/session`
200 avec cette session, et formulaire de connexion absent. Le navigateur intégré
Codex étant indisponible, le passage utilise ce navigateur local distinct.
Les commandes humaines suivantes passent par la session authentifiée et un
jeton antiforgery frais ; aucun mot de passe/cookie n'est lu ou affiché dans le
rapport. L'interface Blazor existante ne contient pas de page de pilotage de
sondes : les commandes explicites utilisent les routes API prévues par 9.3.2.

| Identifiant | Valeur |
| --- | --- |
| operationId | `270a1aae-dcea-4394-a398-7f206286ef60` |
| correlationId | `97180cf8-7d8c-4026-b85a-808caba4e9ce` |
| commandMessageId | `9eb55858-99e7-4ba6-813a-7bbff7fa20fc` |
| probeId | `eb976270-1748-40e5-9d83-f4910ca26c82` |
| attemptId | `4ac853b1-360b-41b0-86a0-03c889727162` |
| result messageId | `904d072f-7d2e-454a-b79c-0b2d1c1c24e6` |
| Exécution n8n du parcours positif | `3`, statut d'exécution `success` |

Horaires suivants en **Europe/Paris (UTC+02:00)**, le 15 octobre 2026 :

| Observation | Heure et état |
| --- | --- |
| Création explicite, HTTP 201 | 11:47:51.645768 ; `pending`, révision 0 |
| Début de l'unique tentative | 11:48:20.671388 ; `awaiting-result`, révision 1, tentative `started` |
| Réception durable du callback signé | 11:48:20.838289 ; `result-received`, révision 2, `ProcessedAt=null` |
| ACK transport enregistré | 11:48:20.880238 ; tentative `acknowledged`, état logique toujours `result-received` |
| Redémarrage API seule | 11:49:16 ; réception toujours pending après redémarrage |
| Traitement humain explicite, HTTP 204 | 11:49:38.768216 ; `succeeded`, révision 3 |
| `ProcessedAt` et `CompletedAt` | `2026-10-15T09:49:38.768216+00:00` |

Un observateur SQL en lecture seule a effectivement capturé `pending`, puis
`awaiting-result/started`, `result-received/started` et enfin
`result-received/acknowledged`. Ce sont des états observés pendant l'envoi,
pas seulement une déduction à partir des dates finales.

La preuve d'exécution n8n indique signature entrante validée, **258 octets bruts**,
SHA256 `d64206647c7caffe38e65f5a166994e3953903ca9afefe9070fc4e83cf382c00`,
callback HTTPS validé, statut CRM 202 et `alreadyRecorded=false`.
La réception persistée prouve le passage de l'authentification et de
l'autorisation machine réelles du CRM. L'ACK seul n'a jamais été considéré comme
le succès logique.

### Tests négatifs, idempotence et reprise

| Contrôle | Résultat et portée |
| --- | --- |
| Rejeu du même callback dans les 300 s | Même corps UTF-8 extrait de l'exécution n8n, même timestamp et ID de clé ; HMAC déterministe identique recalculé hors affichage. HTTP 202, `alreadyRecorded=true` |
| Mauvaise signature | Même callback, signature volontairement invalide : HTTP 401 |
| Cookie humain seul | Session opérateur valide, POST callback sans HMAC : HTTP 401 |
| Absence de mutation après ces trois appels | Réponse de consultation opération/tentative/réception strictement identique avant et après ; une seule réception |
| Ancienne opération `ConnectionId=NULL` dans la base réelle | **Non applicable sur cette base** : zéro opération avant le test, zéro ligne NULL ensuite. Aucune fausse opération historique ajoutée |
| Protection des anciennes opérations | Test existant `IntegrationSecurityMigrationTests.UpgradeKeepsUnboundLegacyOperationsClosedAndDowngradePreservesHistoricalData` exécuté seul, PostgreSQL 18 jetable : 1 réussi, 0 échec, 0 ignoré. Refus réel du store après migration sur cette base isolée ; ne constitue pas un callback HTTP sur une ancienne ligne de Development |
| Reprise légère | `pending-receipts` contient la même réception durable avant/après arrêt puis redémarrage de l'API seule ; contenu identique, `ProcessedAt=null` |
| Traitement après reprise | POST `/process` avec révision 2 : 204 ; état final `succeeded`, révision 3, `ProcessedAt` défini ; liste pending vide |
| Absence de nouvel appel n8n pour la reprise | Toujours trois exécutions n8n : deux contrôles entrants préalables et l'unique sonde positive ; une seule tentative CRM |

Une seule sonde créée et conservée ; aucune seconde sonde nécessaire. Pas de
crash forcé, de restauration ou de nouvel envoi pendant la reprise. Les statuts
`success` des deux exécutions n8n négatives signifient que le workflow a correctement
produit son refus HTTP, pas qu'un résultat CRM a été accepté.

Les nombres et empreintes des lignes ont été comparés avant/après sur 13 tables :
`Opportunities`, `SourceCollectionJobs`, `SourceCollectionJobAttempts`,
`SourceCollectionSchedules`, `SourceExecutions`, `SourceExecutionItems`,
`AutomationJobs`, `AutomationExecutions`, `AutomationActionRequests`,
`EmailMessages`, `CalendarEvents`, `Workspaces`, `UserAccounts`.
Tous sont identiques. L'opportunité préexistante est conservée ; aucun objet métier,
email, calendrier, collecte ou appel fournisseur n'a été créé. Les appels du
workflow restent limités au callback local.

### État final et limites

- PostgreSQL conservé démarré et healthy, volume et données conservés.
- Workflow dépublié (`active=0`) avant arrêt de n8n ; n8n arrêté, code 0.
  Workflow, credential chiffré et types locaux restent disponibles dans le volume.
- API, Blazor, proxy et navigateur de test arrêtés ; ports 7096, 7075 et 7443 libres.
- Le profil Edge dédié reste sous `operator-browser-profile/` dans le dossier
  privé hors Git. La commande de suppression de ce profil temporaire a été rejetée
  par le contrôle automatique, sans motif détaillé ; aucune suppression effectuée.
- `Integrations:Enabled=false` remis dans les User Secrets après arrêt API/Blazor.
  Les deux clés et l'UUID de connexion sont conservés hors Git pour la suite.
- Propriétaire n8n toujours initialisé, non désactivé, MFA présente ; aucun changement
  de compte/2FA et aucune nouvelle connexion interactive n8n revendiquée.
- Une opération `succeeded`, une tentative `acknowledged`, une réception traitée
  restent en base comme preuve technique locale normale.

Aucun code C# applicatif, `compose.yml`, profil de lancement ou paramètre versionné
de sécurité modifié. Suite complète non relancée ; seul le test historique ciblé
ci-dessus a été exécuté. Le proxy autonome a été compilé sans avertissement ni
erreur, puis vérifié sur les connexions TLS réelles. Les JS du nœud et le JSON
exporté ont été contrôlés ; les valeurs réelles des clés ont été recherchées sans
affichage dans les fichiers candidats Git et les journaux privés.

Au terme de la validation technique, avant la publication Git autorisée
séparément, seuls ce rapport et les trois exports cités plus haut étaient ajoutés
au working tree. `git diff --check` et contrôle équivalent des fichiers non suivis
réussis ; index alors vide, aucun staging, commit ou push à cette étape.
Aucun secret, certificat privé, fichier temporaire, dump, log ou credential
contenant des valeurs ne fait partie de la livraison Git.

**Le critère central de l'échange réel de 9.3.3 est acquis.** La restriction du
contrôle historique NULL est explicite dans la matrice ci-dessus. Le canal reste
une installation locale dédiée, avec activation volontaire nécessaire pour une
reprise ; aucune ouverture de la Phase 9.4 ni automatisation métier.
