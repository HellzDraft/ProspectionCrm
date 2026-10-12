# Phase 9.2.2 — Accès administrateur, sauvegardes et préparation réseau

Audit du 12 octobre 2026. **Faits vérifiés**, **recommandations** et **contrôles
reportés** sont distingués ci-dessous. Aucun commit ni push.

**Lecture chronologique :** « Référence et périmètre » et les sections A à E
conservent les constats du premier audit, antérieurs à la confirmation interactive
du propriétaire. Le [complément F](#f-complément-autorisé--sauvegardes-synthétiques-et-décisions)
décrit l'intervention suivante autorisée : essais restic synthétiques, sessions
préparées et décisions réseau. Ses résultats et son verdict sont l'état courant
du périmètre ; les constats historiques ne sont pas des contrôles rejoués.

## Référence et périmètre

- Branche initiale `main`, HEAD `cb19790b0094e3a90c2e67119e17edc5644acce9`.
- Working tree initial propre. CI #55 réussie selon la référence utilisateur,
  non revérifiée pendant cette mission.
- Docker Engine 29.6.1 disponible ; n8n 2.40.7 et PostgreSQL existants arrêtés.
  Les deux conteneurs d'essai 9.2.1 sont également arrêtés.
- Lecture préalable du Compose, des trois documents d'exploitation/validation,
  de Program.cs, des launchSettings et des configurations de sécurité pertinentes.
- Aucun changement de Compose, code API, modèle, migration, secrets, pare-feu,
  image, réseau ou volume ; aucune mise à jour de version ou installation d'outil.
- Les archives réelles n'ont été ni chiffrées, ni déplacées, ni remplacées,
  ni supprimées, ni transférées. Leur contenu n'a pas été consulté.

## A. Propriétaire et authentification de l'éditeur

### Données observées, sans information personnelle

Base SQLite ouverte readonly depuis un auxiliaire de la même image, sans réseau.
Seuls le schéma, des agrégats et deux paramètres non secrets ont été interrogés :

| Contrôle | Résultat |
| --- | --- |
| Lignes avec rôle global:owner | 1 |
| Propriétaires désactivés | 0 |
| Propriétaires avec mfaEnabled | 0 |
| userManagement.isInstanceOwnerSetUp | false |
| userManagement.authenticationMethod | Paramètre absent |
| Workflows / credentials / exécutions | 0 / 0 / 0 |

**La présence de la ligne propriétaire ne démontre pas un compte initialisé et
utilisable. Le marqueur d'initialisation est false.** Le code de l'image crée
un shell utilisateur puis finalise le propriétaire via `OwnershipService.setupOwner`.
La route `POST /rest/owner/setup` porte `skipAuth: true` ; le service refuse une
prise de possession lorsqu'un propriétaire configuré est déjà détecté.
Cette route n'a pas été appelée. Le marqueur peut être confronté à l'UI par
l'utilisateur ; aucune lecture de mot de passe n'a servi à inférer son état.

Il faut considérer l'instance comme nécessitant une initialisation interactive
contrôlée. L'instance réelle est restée arrêtée pour ne pas ouvrir inutilement
la fenêtre de première prise de possession. Aucun compte réinitialisé ou créé.

### Méthodes disponibles et limites de Community Edition

| Mécanisme | Conclusion |
| --- | --- |
| Connexion locale par email/mot de passe | Disponible dans l'image ; configuration réelle du propriétaire à terminer/confirmer |
| MFA par application d'authentification | Disponible, N8N_MFA_ENABLED=true par défaut ; non activée pour le propriétaire observé |
| SSO SAML / LDAP | Fonctionnalités sous licence, non retenues pour Community gratuite |
| SSO OIDC | Documenté pour Enterprise, non retenu pour Community gratuite |
| Clé API n8n | Authentification d'API distincte ; ne remplace pas la connexion à l'éditeur |
| Authentification de webhook | Distincte de l'éditeur ; aucun webhook créé ni configuré ici |

La présence de modules SSO dans l'image commune ne donne pas accès à leurs
fonctions sous licence. Aucun contenu de licence, secret ou credential n'a été
lu. Le périmètre Community provient de la référence projet et des restrictions
documentées, pas d'une extraction d'un jeton de licence.

Sources : [éditions](https://docs.n8n.io/hosting/community-edition-features/),
[gestion des utilisateurs](https://docs.n8n.io/user-management/),
[MFA](https://docs.n8n.io/user-management/two-factor-auth/),
[OIDC](https://docs.n8n.io/user-management/oidc/).
Le code local confirme les contrôles SAML/LDAP dans `dist/license.js`, ainsi
que les handlers email, MFA et l'initialisation du propriétaire.

### Sessions et cookies : code vérifié, connexion non effectuée

Les paramètres ont été résolus avec le chargeur `GlobalConfig` de l'image,
sans démarrer n8n et sans volume métier. Les noms des variables du conteneur
confirment l'absence de surcharge des durées, de SameSite et de MFA.

- `N8N_SECURE_COOKIE=false`, seul réglage explicite concerné dans Compose.
- Cookie émis par `AuthService.issueCookie` : `HttpOnly=true`, `SameSite=Lax`,
  `Secure=false` dans ce contexte HTTP local.
- Durée par défaut du JWT et du cookie : 168 heures (7 jours).
- `jwtRefreshTimeoutHours=0` signifie **25 % de la durée**, pas renouvellement
  désactivé : ici 42 heures. Une requête authentifiée valide dans cette fenêtre
  peut renouveler le cookie. Sept jours n'est donc pas une expiration absolue
  depuis la première connexion si la session est renouvelée.
- Les contrôles JWT, utilisateur désactivé, invalidation et, selon la session,
  browserId sont présents. Aucun token réel ni cookie de session n'a été lu,
  généré par une connexion de test, affiché ou modifié.

`HttpOnly` limite la lecture par JavaScript ; `SameSite=Lax` réduit certains
scénarios intersites sans constituer une autorisation. `Secure=false` ne désactive
pas la connexion propriétaire, mais autorise le transport HTTP du cookie.
HTTP ne chiffre pas les échanges. Ne pas réutiliser ce réglage sur Internet.
Conserver une URL canonique `http://localhost:5678` pour l'usage humain, sans
alterner avec `127.0.0.1` (hôtes de cookies distincts). Les cookies ne constituent
pas une isolation par port entre les services d'un même hôte.

### Contrôle interactif restant à effectuer par l'utilisateur

1. Choisir une session Windows de confiance ; vérifier les autres accès locaux
   et conteneurs sur le réseau partagé avant une courte fenêtre d'initialisation.
2. Après ces prérequis, démarrer seulement n8n existant, sans recréation ni reset,
   ouvrir `http://localhost:5678` et vérifier si l'initialisation est proposée.
3. Si proposée, finaliser soi-même le propriétaire avec des identifiants personnels
   conservés hors dépôt et hors chat. Si l'écran indique un état différent du
   marqueur, arrêter l'opération et analyser sans reset.
4. Activer volontairement la MFA dans Settings > Personal ; garder les codes de
   récupération dans un emplacement protégé, sans les transmettre à l'agent.
5. Vérifier connexion, déconnexion et refus d'accès aux données dans une fenêtre
   privée déconnectée. Ne pas exporter les cookies, headers, stockage du navigateur
   ou un HAR ; consigner uniquement succès/échec et date.
6. Vérifier ultérieurement l'expiration/renouvellement selon la durée retenue,
   sans modifier l'horloge système. Revenir à l'état arrêté après cette intervention.

Cette procédure n'a pas été exécutée. Le compte, la MFA et la durée souhaitée
doivent être validés par le propriétaire avant clôture de l'accès administrateur.

## B. Sauvegarde chiffrée proposée

La [stratégie détaillée](n8n-encrypted-backups.md) recommande restic : tar cohérent
du volume, chiffrement/authentification côté client, secret restic distinct de
la clé n8n, snapshots et contrôles `check --read-data`, restauration sur volumes
isolés, copie du dépôt chiffré hors machine. Proposition de conservation :
7 quotidiens, 4 hebdomadaires, 6 mensuels ; point avant évolution conservé jusqu'à
validation. Aucun fournisseur imposé, support externe hors ligne possible.

Non trouvé dans le PATH : restic, age, 7z, gpg. Aucun outil installé et aucune
démonstration de chiffrement exécutée. Les procédures sont documentées à partir
des sources officielles, pas validées avec un binaire local. La restauration
non chiffrée sur données vierges reste couverte par le rapport 9.2.1.

Décisions requises : outil/version, destination, garde/récupération du mot de
passe, fenêtre de capture à froid, conservation, objectif de perte acceptable
et traitement ultérieur des archives en clair déjà présentes. Ni chiffrement
ni purge de ces archives n'est autorisé par cette mission.

## C. Audit réseau

### État observé

- Éditeur n8n publié sur `127.0.0.1:5678`, PostgreSQL sur `127.0.0.1:5432`.
  Les services étant arrêtés, aucun listener correspondant n'est actif.
- Dans l'image n8n, l'adresse d'écoute par défaut est `::` à l'intérieur du
  conteneur, sans surcharge N8N_LISTEN_ADDRESS observée. Ce n'est pas le binding
  de publication Windows, qui reste loopback.
- Réseau commun `prospectioncrm_default` : bridge, internal=false, sous-réseau
  observé 172.18.0.0/16. Les deux conteneurs y sont attachés. Pas de segmentation
  n8n/PostgreSQL ; ne pas fournir de credentials CRM à n8n.
- API Windows : profils `http://localhost:5016` et
  `https://localhost:7096;http://localhost:5016`. Aucun listener de ces ports
  n'était actif. Les profils sont une configuration de lancement, pas une preuve
  des bindings d'une future exécution avec d'autres variables/options.
- `Program.cs` utilise `UseHttpsRedirection`, `UseAuthorization`, CORS Development
  limité à Blazor et OpenAPI Development. Aucun `AddAuthentication`,
  `UseAuthentication`, attribut `Authorize` trouvé dans les sources examinées.
  Aucun endpoint health dédié à l'API n'est présent. `AllowedHosts=*` n'est pas
  une restriction réseau. CORS ne protège pas les appels serveur-à-serveur.
- Pare-feu Windows ActiveStore : profils Domain/Private/Public activés,
  entrée par défaut Block, sortie Allow. La recherche de règles entrantes actives
  par noms Docker/n8n/Prospection/ASP.NET n'a rien retourné. Cette recherche n'est
  pas un audit exhaustif des exceptions par application, port ou règles tierces.
  Aucune règle modifiée ; aucun scan réseau externe réalisé.

Références dépôt : `src/ProspectionCrm.Api/Program.cs`,
`src/ProspectionCrm.Api/Properties/launchSettings.json`,
`src/ProspectionCrm.Api/appsettings.json`, `compose.yml`.

### Test non destructif conteneur vers Windows

Endpoint temporaire synthétique Python, aucun fichier persistant, écoute
**127.0.0.1:64748** uniquement, GET `/healthz` retourne 204 sans données.
Le port a été choisi automatiquement puis libéré dans un bloc finally.
Auxiliaire Node de l'image n8n existante, réseau `prospectioncrm_default`,
filesystem readonly, sans volume métier, sans credential.

| Contrôle | Résultat réellement observé |
| --- | --- |
| Résolution host.docker.internal | 192.168.65.254 dans cette session |
| Depuis le conteneur vers 127.0.0.1:64748 | ECONNREFUSED : loopback du conteneur |
| Depuis le conteneur vers host.docker.internal:64748/healthz | HTTP 204 |
| Après test | Listener temporaire fermé, auxiliaire supprimé, aucun volume créé |

**Le chemin réseau conteneur → Windows loopback fonctionne sur cet hôte Docker
Desktop.** Ce résultat ne prouve pas la compatibilité de Kestrel, la confiance TLS,
les redirects HTTPS ni l'autorisation de l'API, qui n'a pas été démarrée.
L'adresse résolue et le port de test ne doivent pas être codés en dur.

Le test supplémentaire hôte → conteneur sur un serveur synthétique a été rejeté
avant exécution par le contrôle automatique d'autorisation (« blocked by policy »,
sans motif plus précis). Aucun conteneur de ce test n'a été créé. Aucun contournement
n'a été tenté. Le HTTP 200 hôte → n8n réel de 9.2.1 reste une preuve antérieure,
pas un test rejoué ici. L'API et l'instance n8n réelles sont restées arrêtées.

### Risques déduits et architecture minimale proposée pour 9.3

Le binding loopback empêche la publication directe sur les interfaces réseau
Windows, mais **n'isole pas l'API des conteneurs locaux**, comme l'essai le montre.
Un compte/processus de l'hôte peut également atteindre ces ports. Un utilisateur
ayant le contrôle du daemon Docker peut monter les volumes : ce privilège doit
être réservé à des opérateurs de confiance. Aucun test d'attaque n'est réalisé.

Flux à préparer, sans endpoint métier créé dans cette phase :

| Sens | Adresse locale envisagée | Contrôle obligatoire en 9.3 |
| --- | --- | --- |
| CRM Windows → n8n | http://127.0.0.1:5678/webhook/<route dédiée> | Authentification du webhook indépendante de la session éditeur |
| n8n → CRM Windows | http://host.docker.internal:5016/api/integrations/<route dédiée> | Identité machine limitée, validation de signature/rejeu et permissions serveur |
| Variante HTTPS CRM | https://host.docker.internal:7096/... | Certificat avec SAN adapté et chaîne approuvée par le conteneur |

Ces chemins sont des propositions de contrats, pas des routes existantes.
Conserver un seul transport HTTP JSON asynchrone, avec opération persistée,
accusé de réception puis callback corrélé. Ne pas ajouter Redis, un broker,
un second moteur d'automatisation ni une API d'administration n8n à ce stade.

**Arbitrage transport** : HTTP local peut servir au diagnostic sans secret,
comme ici. Son utilisation pour des credentials machine exige une acceptation
explicite du périmètre de confiance local. Préférer HTTPS pour les échanges
authentifiés ; côté n8n cela demanderait une terminaison TLS locale approuvée,
et côté API un certificat adapté à host.docker.internal. Le certificat de
développement localhost ne doit pas être supposé valable pour cet autre nom.
Ne pas désactiver la validation TLS. Vérifier les redirections sans les suivre
aveuglément, notamment un Location inaccessible depuis le conteneur.

**Arbitrage autorisations** : protéger les routes d'intégration ne protège pas
les routes CRM existantes, notamment approve/reject et administration. Définir
avant 9.3 la politique utilisateur/machine applicable à ces routes, sans laisser
le credential de connecteur approuver une action. Aucune authentification globale
n'est implémentée ici. Une façade limitée ne suffit pas si les endpoints CRM
sous-jacents restent directement accessibles sans autorisation.

**Segmentation recommandée**, non appliquée : réseau dédié à n8n distinct de
PostgreSQL. Elle supprime le chemin direct n8n → postgres:5432 sur le bridge,
mais ne suffit pas à bloquer host.docker.internal:5432 ou les autres services hôte.
Le cloisonnement sortant éventuel et ses effets doivent être arbitrés/testés avant
exécution de workflows non fiables. Ne pas passer l'API sur 0.0.0.0, modifier le
pare-feu ou activer le mode host networking pour résoudre ce point.

Sur Linux/VPS, ne pas supposer host.docker.internal fourni ni le même routage
loopback : tester le DNS/gateway et définir un réseau privé/TLS propre à la cible.
Pas de déploiement distant dans cette étape.
[Référence Docker Desktop](https://docs.docker.com/desktop/features/networking/).

### Procédure réseau restante, avant une future activation

1. Terminer l'initialisation propriétaire et décider HTTP local ou TLS, ainsi
   que les politiques d'autorisation CRM ; conserver les services arrêtés sinon.
2. Préparer en 9.3 un endpoint technique minimal sans données, indépendant des
   actions métier. Fixer les workers désactivés pour un test API contrôlé.
3. Vérifier les adresses réellement écoutées ; tester depuis l'image n8n exacte
   avec timeout borné, DNS host.docker.internal et redirects désactivés. Ne
   jamais contourner un refus par un binding public ou une option TLS insecure.
4. Vérifier HTTP 401/403 sans identité/avec une identité hors portée, puis le
   succès autorisé uniquement lorsque l'utilisateur a configuré les secrets
   hors Git. Ne pas enregistrer les headers sensibles.
5. Tester un appel de santé hôte → n8n après levée du blocage d'exécution, sans
   workflow. Consigner pass/fail, revenir à l'état initial et libérer les probes.

## D. Contrôles et modifications locales

| Contrôle | Résultat / limite |
| --- | --- |
| Git initial | main, HEAD attendu, propre |
| Compose avec et sans integrations | Validation réussie, codes 0 |
| Services sélectionnés sans profil | postgres uniquement |
| Ports et réseau | Inspection ciblée, valeurs indiquées ci-dessus |
| Propriétaire | Agrégats et marqueur lus readonly, aucune donnée personnelle extraite |
| Session/MFA | Code et paramètres de l'image vérifiés ; pas de connexion réelle |
| Conteneur → hôte | DNS et HTTP 204 sur endpoint synthétique loopback |
| Hôte → conteneur supplémentaire | Rejet d'autorisation avant exécution ; reporté |
| Chiffrement/restauration restic | Procédures proposées, non exécutées |
| Tests .NET et migrations | Non exécutés, aucun code applicatif modifié |

Fichiers de cette mission uniquement :

- `docs/n8n-operations.md` modifié : état propriétaire et liens vers les compléments.
- `docs/n8n-encrypted-backups.md` ajouté : stratégie et procédures à valider.
- `docs/phase-9-2-step-2-validation.md` ajouté : présent audit.

Le rapport historique 9.2.1 reste inchangé. La ligne utilisateur qu'il signalait
sans certifier son initialisation est maintenant qualifiée par le marqueur false.
Les liens documentaires, clôtures de blocs de code et espaces de fin de ligne
des trois documents ont été contrôlés avec succès ; `git diff --check` réussit.
Les archives et `.env` restent ignorés par Git, aucune donnée sensible ajoutée.
Branche/HEAD inchangés, aucun staging, commit ou push. Tous les services existants
et volumes conservés ; aucune archive réelle manipulée ; aucune installation.

## E. Décisions avant clôture de 9.2

1. Propriétaire : initialiser/confirmer interactivement, choisir MFA et durée de
   session ; garder les moyens de récupération hors machine et hors chat.
2. Sauvegardes : approuver restic, son déploiement portable/version, le support
   hors machine, le gardien du mot de passe et la conservation ; prévoir ensuite
   une démonstration de chiffrement/restauration uniquement synthétique.
3. Réseau : accepter explicitement HTTP dans un périmètre local de confiance
   ou retenir TLS ; définir les autorisations des routes CRM existantes avant
   l'arrivée de credentials machine et arrêter la segmentation souhaitée.
4. Lever le blocage du test hôte → conteneur si une nouvelle preuve est requise ;
   ne pas confondre la connectivité synthétique réussie avec un échange CRM validé.

**Verdict de l'audit initial : ARBITRAGES / CORRECTIONS NÉCESSAIRES.** Le travail d'audit et de
préparation est livré ; les contrôles interactifs et décisions ci-dessus empêchent
de déclarer les conditions d'exploitation finales validées. Aucune Phase 9.3
n'est engagée. Attendre la validation utilisateur avant toute nouvelle phase.

## F. Complément autorisé — sauvegardes synthétiques et décisions

Intervention du 12 octobre 2026, après validation utilisateur du parcours d'essai.
Les [commandes reproductibles](n8n-encrypted-backups.md#démonstration-synthétique-reproductible)
et la [procédure réelle future](n8n-encrypted-backups.md#procédure-future-de-sauvegarde)
sont distinctes. Aucune donnée réelle n'a été sauvegardée, restaurée ou transférée.

### État de départ et confirmation interactive

- Branche `main`, HEAD `cb19790b0094e3a90c2e67119e17edc5644acce9`, index vide.
- Changements locaux attendus conservés : `docs/n8n-operations.md` modifié,
  `docs/n8n-encrypted-backups.md` et ce rapport non suivis. Aucun écart Git inattendu.
- Lecture des trois documents, de Compose et du rapport 9.2.1 effectuée.
- L'utilisateur **rapporte** avoir terminé l'initialisation du propriétaire,
  activé la 2FA et vérifié la reconnexion dans l'éditeur. Aucun de ces gestes
  n'est présenté comme un test automatisé ; ni identifiant, QR code, secret MFA
  ni code de récupération demandé ou lu. Pas de nouvelle inspection SQLite réelle.
- L'archive réelle de 9.2.1 précède cette initialisation et **ne couvre pas l'état
  actuel**. Elle reste inchangée ; une nouvelle capture post-initialisation sera
  nécessaire avant une future modification du conteneur.

État effectivement relevé au début, puis identique après essais (horodatages UTC) :

| Conteneur réel | ID abrégé | État | StartedAt | FinishedAt |
| --- | --- | --- | --- | --- |
| prospectioncrm-n8n-1 | 920fb3c09006 | exited | 2026-10-12T13:49:50.352938493Z | 2026-10-12T14:01:16.801189853Z |
| prospectioncrm-postgres-1 | 21c4200431ec | exited | 2026-10-12T13:04:47.188228195Z | 2026-10-12T13:07:01.133635969Z |

Les IDs complets et les horodatages ont été comparés en mémoire. Aucun démarrage,
arrêt, recréation ou montage de leurs volumes dans les essais. Image d'essai déjà
présente, sans pull : `docker.n8n.io/n8nio/n8n:2.40.7`, identifiant
`sha256:ffeb52485f78b1b06c9a832205853cf75da72a07a514c9a27724df85979d6c34`.

### Outil portable et contrôles de provenance

Restic absent du PATH. Version stable choisie dans les releases officielles :
**0.19.1**, non prerelease. Téléchargements publics uniquement :

- [ZIP Windows amd64](https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_windows_amd64.zip)
- [SHA256SUMS](https://github.com/restic/restic/releases/download/v0.19.1/SHA256SUMS)
  et [signature détachée](https://github.com/restic/restic/releases/download/v0.19.1/SHA256SUMS.asc)
- [Clé publique officielle](https://restic.net/gpg-key-alex.asc), empreinte confrontée
  à la [documentation d'installation](https://restic.readthedocs.io/en/stable/020_installation.html).

| Contrôle | Résultat |
| --- | --- |
| SHA-256 du ZIP comparé au manifeste | DA948AD707ED690426473AABA2046CD61F8F90F6F0E7DAB6BE0D5796531DE67D ; égalité |
| Vérificateur disponible hors PATH | GPG de Git, 2.2.25-unknown |
| Empreinte primaire attendue et VALIDSIG | CF8F18F2844575973F79D4E191A6868BD3F7A907 |
| Vérification de SHA256SUMS.asc sur SHA256SUMS | Code 0, signature effectivement vérifiée |
| Version réellement exécutée | restic 0.19.1 compiled with go1.26.4 on windows/amd64 |

Intégrité SHA-256 **et** signature vérifiées avant extraction/exécution. La confiance
repose sur l'empreinte publiée officiellement, pas sur un marquage local de confiance
GPG. Trousseau public dédié ; aucun trousseau personnel modifié. L'import GPG initial
a signalé une erreur IPC de connexion à l'agent ; l'import et la vérification publics
avec `--no-autostart`, sans agent privé, ont ensuite réussi avec code 0. Aucun refus
du contrôle automatique d'autorisation pendant ce complément, aucune élévation.

Binaire extrait sous la racine de test, aucun installateur/gestionnaire de paquets,
aucune modification de PATH. Aucun fichier du projet n'a été envoyé. Les journaux
de vérification publique sont conservés dans `evidence/` hors Git.

### Isolement et secret de test

Racine unique, absence contrôlée avant création :
`%LOCALAPPDATA%\ProspectionCrmTests\phase922-60c6548b88e6`.
Tous les téléchargements, caches, archives et dépôts de ce complément sont hors
du dépôt Git. Aucun lien ou jonction vers un emplacement réel utilisé.

Les noms Docker ci-dessous ont été contrôlés absents avant création et portent
le label `prospectioncrm.test=phase922-60c6548b88e6` :

| Usage | Volume | Conteneur conservé |
| --- | --- | --- |
| Source | prospectioncrm_922_60c6548b88e6_source | prospectioncrm-922-60c6548b88e6-source |
| Restauré | prospectioncrm_922_60c6548b88e6_restored | prospectioncrm-922-60c6548b88e6-restored |

Instances sous l'utilisateur `node`, réseau `none`, aucun port publié, aucune
connexion CRM/fournisseur. Services distants n8n désactivés. Les auxiliaires
d'inspection utilisent un filesystem readonly ; seule l'extraction utilise root
avec CHOWN/FOWNER/DAC_OVERRIDE, conformément à la correction 9.2.1.

Mot de passe restic synthétique de 32 octets aléatoires, créé indépendamment de
la clé n8n ; second mot de passe aléatoire pour le test négatif. Valeurs générées
en mémoire puis écrites sans affichage, jamais dans les arguments ou Git.
Fichiers dans `secrets/`, absents des sources sauvegardées. DACL racine protégée,
FullControl seulement pour l'opérateur courant et SYSTEM ; héritage vérifié sur
les deux fichiers. Aucun mot de passe réel, variable `.env` ou credential utilisé.

### Résultats du parcours de sauvegarde et restauration

| Vérification exécutée | Résultat observé |
| --- | --- |
| n8n source neuf | Readiness HTTP 200 depuis son propre conteneur, puis arrêt code 0 |
| Capture à froid | Source readonly ; tar de tout `.` avec umask 077 ; code 0 |
| Archive source | 5 697 024 octets ; 9 entrées : 6 fichiers et 3 répertoires, racine comprise |
| Structure tar | config et database.sqlite présents ; aucun chemin absolu, traversée ou lien ; UID/GID 1000/1000 |
| Dépôt restic neuf | Format v2, initialisation code 0 |
| Première sauvegarde | Un seul fichier source (le tar), 5 697 024 octets ; code 0 |
| check --read-data | Succès après premier snapshot, puis après second : 2 snapshots / 3 packs, aucune erreur |
| Mot de passe incorrect | Refus attendu, code 12, diagnostic wrong password/no key found |
| Première restauration complète | Échec code 1 sur timestamp du parent restauré C/Users ; essai conservé |
| Restauration du sous-dossier de capture | Même snapshot exact, destination neuve restore-retry, --overwrite never --verify ; code 0 |
| Empreinte du tar restauré | Identique à l'original |
| Extraction dans le second volume | Volume vide vérifié ; tar -xpf avec capacités ciblées ; code 0 |
| Comparaison avant démarrage | 9 entrées identiques : chemins, types, UID/GID/modes et octets des 6 fichiers |
| Clé source/restaurée | Présente et identique, comparaison en mémoire uniquement |
| SQLite restaurée readonly | PRAGMA integrity_check = ok ; 0 workflow, 0 credential, 0 exécution synthétiques |
| n8n restauré | Readiness HTTP 200, puis arrêt code 0 |

SHA-256 du tar source et restauré :
`CB40B25C81F869588BA3A0663C3F2F10E9E4D2C95AFCC3B1681420CB366DE082`.

Snapshot sauvegardé et restauré :
`51c59841e252260db10bd734aa10d641c3a54f5a832f56f8ac87ae810dc66860`.

Deuxième snapshot, même tar copié sous `capture2/` pour tester les chemins :
`d1cbdd4e3380ca05d9675b4d1c1e2fc4e304b3fb5a771d38d158297720eff222`.

La première restauration a buté sur les métadonnées Windows des parents d'un
chemin absolu. L'échec n'a pas été ignoré : aucune extraction de cet essai partiel.
Le chemin interne de capture a été relevé avec `restic ls` ; la syntaxe
`<snapshot exact>:<sous-dossier>` a restauré le seul tar dans une autre destination.
Cette procédure corrigée figure dans le guide. Aucun droit système ni ACL d'un
dossier existant hors racine d'essai n'a été modifié pour résoudre cet échec.

Tous les exécutables natifs ont fait l'objet d'un contrôle explicite de
`$LASTEXITCODE`. Aucun succès global déduit d'une restauration partielle. La
readiness ne démontre ni connexion utilisateur, ni déchiffrement de credentials
réels, ni restauration d'une instance chargée. Aucun secret réel présent dans l'essai.

### Simulation de conservation

Deux commandes `forget --dry-run --keep-daily 7 --keep-weekly 4 --keep-monthly 6`,
avec paramètres de dépôt, fichier de mot de passe et cache dédiés :

| Regroupement | Groupes observés | Snapshots conservés / suppression simulée |
| --- | --- | --- |
| Défaut host,paths | 2, car capture et capture2 diffèrent | 2 / 0 |
| host,tags, filtre hôte prospectioncrm-synthetic et tag n8n-cold | 1 | 2 / 0 |

Le dernier snapshot est retenu pour les périodes courantes ; le premier est
également retenu comme plus ancien disponible lorsque l'historique ne couvre
pas les périodes demandées (raisons restic daily/weekly/monthly et oldest).
Les listes d'IDs avant/après sont strictement identiques. Les fichiers JSON bruts
ont été contrôlés pour compter les tableaux `keep`/`remove`, y compris `remove=null`.

La conservation n'établit aucune fréquence de capture. Un nouvel hôte forme
également un groupe distinct ; conserver un identifiant stable, un tag propre
à l'instance et les filtres appropriés. **Une journée de simulation ne valide pas
six mois de rétention.** Aucun forget effectif, prune, repair ou nettoyage global.

### Sessions : prise en charge, déclaration et runtime

Variables vérifiées dans `@n8n/config/dist/configs/user-management.config.js`
de l'image 2.40.7 ; comportement dans `dist/auth/auth.service.js` :

- `jwtSessionDurationHours=24` : expiration des nouveaux JWT à 86 400 secondes.
- `jwtRefreshTimeoutHours=-1` : accepté après `sanitize()`, seuil de
  renouvellement négatif ; la comparaison sur un JWT valide non expiré ne déclenche
  pas de renouvellement. 0 activerait une fenêtre égale à 25 % de la durée.
- Le jeton est vérifié avant ce renouvellement ; aucun jeton réel consulté.

Chargement réellement exécuté dans un auxiliaire sans volume, réseau `none`,
filesystem readonly, avec les deux variables passées via `-e` :

```javascript
const root = '/usr/local/lib/node_modules/n8n/node_modules/';
require(root + 'reflect-metadata');
const { Container } = require(root + '@n8n/di');
const { GlobalConfig } = require(root + '@n8n/config');
const c = Container.get(GlobalConfig);
c.userManagement.sanitize();
const { jwtSessionDurationHours: duration, jwtRefreshTimeoutHours: refresh } = c.userManagement;
if (duration !== 24 || refresh !== -1) throw Error('Session configuration mismatch');
console.log(JSON.stringify({ duration, refresh, durationType: typeof duration, refreshType: typeof refresh }));
```

Sortie : nombres 24 et -1, code 0. Le code est lancé par `docker run --rm
--pull never --network none --read-only --cap-drop ALL --security-opt no-new-privileges
-e N8N_USER_MANAGEMENT_JWT_DURATION_HOURS=24
-e N8N_USER_MANAGEMENT_JWT_REFRESH_TIMEOUT_HOURS=-1 --entrypoint node <image> -e <code>`.
[Sémantique officielle des variables](https://docs.n8n.io/hosting/configuration/environment-variables/user-management-smtp-2fa/).

| État | Conclusion |
| --- | --- |
| Cible retenue par l'utilisateur | 24 h, sans renouvellement automatique |
| Prise en charge de 2.40.7 | Confirmée par code et chargeur réel isolé |
| Déclaration Compose | Deux variables ajoutées, seules modifications de configuration |
| Application au conteneur réel | Non : variables absentes de sa configuration, conteneur non recréé/redémarré |
| Comportement runtime actuel | Configuration antérieure conservée, défauts 168 h / fenêtre de 42 h |
| Expiration effective après 24 h | Non observée ; aucun test d'attente de 24 h ni changement d'horloge |

La modification de durée ne garantit pas la révocation ou le raccourcissement
des jetons déjà émis. Application réelle et vérification interactive restent
reportées après nouvelle sauvegarde et validation de l'intervention.

### Décisions réseau retenues pour 9.3

Ces décisions remplacent les alternatives de transport encore ouvertes dans C/E,
sans effacer le constat historique et sans implémentation :

1. Exposition locale maintenue, aucune ouverture publique.
2. HTTPS obligatoire pour les vrais secrets d'intégration, certificats validés,
   SAN/chaîne adaptés aux noms effectivement utilisés ; aucun contournement TLS.
   Les exemples HTTP du premier audit sont réservés au diagnostic sans secret.
3. Identité machine n8n distincte de l'identité humaine CRM et limitée aux seules
   opérations d'intégration explicitement autorisées. Routes administratives,
   approbations et autres routes CRM sensibles protégées séparément ; le credential
   machine ne doit pas donner accès implicitement aux actions humaines.
4. La 2FA n8n protège l'éditeur, pas l'API CRM ou ses webhooks.
5. Loopback insuffisant contre les conteneurs dans l'environnement observé.
   Le résultat host.docker.internal de C reste limité à l'endpoint synthétique :
   aucun test Kestrel, TLS ou autorisation CRM ajouté ou inféré.

Aucun pare-feu, binding, certificat, code d'authentification, webhook ou workflow
modifié. Le refus d'autorisation historique du test hôte → conteneur de C n'a
pas été contourné ni rejoué dans ce complément. La Phase 9.3 n'est pas commencée.

### Artefacts conservés et limites d'exploitation

Sous `%LOCALAPPDATA%\ProspectionCrmTests\phase922-60c6548b88e6` :

- `tool/` : ZIP, manifeste, signature, clé publique et exécutable portable vérifié.
- `gnupg/` : trousseau public dédié et métadonnées de vérification.
- `capture/n8n-synthetic.tar`, `capture2/n8n-synthetic.tar` : données synthétiques
  en clair, nécessaires à l'essai ; aucun contenu réel.
- `repository/` : dépôt restic v2 avec les deux snapshots indiqués.
- `restore/` : première restauration conservée après échec de métadonnées.
- `restore-retry/n8n-synthetic.tar` : restauration réussie et vérifiée.
- `secrets/` : deux fichiers de mot de passe d'essai, ACL contrôlées, valeurs
  non reproduites ; conserver avec accès limité tant que l'essai doit être lisible.
- `cache/` et `evidence/` : caches et preuves locales (GPG, résumés backup,
  inventaires de snapshots, simulation, comparaison pré-démarrage et états Docker).

Les deux volumes synthétiques restent présents ; les deux conteneurs source et
restauré sont **arrêtés, sortie 0**, réseau `none`, aucun port publié, utilisateur
`node`. Seuls les auxiliaires ponctuels `--rm` ont été retirés automatiquement.
Aucun volume supprimé, y compris les anciens essais 9.2.1.

La destination hors machine **reste non choisie**. Ce dépôt synthétique local
n'est pas une sauvegarde hors machine opérationnelle. Restent à décider : support,
garde/récupération du mot de passe réel, fenêtre et fréquence de sauvegarde,
durée de conservation, traitement autorisé des archives en clair. La procédure
future crée une nouvelle capture post-initialisation, la chiffre, la vérifie et
prévoit une copie protégée vérifiée hors machine ; **elle n'est pas exécutée ici**.
Aucune restauration non vérifiée ou réelle autorisée sur les volumes en service.

### Contrôles finaux et périmètre Git

- Compose valide avec et sans profil `integrations`, codes 0 ; sans profil,
  PostgreSQL seulement. Comparaison des modèles effectifs HEAD/working tree en
  mémoire : après retrait des deux variables de session ajoutées, égalité complète.
  PostgreSQL, images, volumes, ports, réseaux, cookies, restart et rétention inchangés.
- Liens documentaires locaux et ancres contrôlés ; documentation générale et
  rapport 9.2.1 conservés comme références historiques, liens existants maintenus.
- `git diff --check` réussi ; contrôle des nouveaux documents inclus dans les
  vérifications d'espaces et de liens. Aucun fichier sensible ou artefact ajouté
  au dépôt ; `.env` et `backups/` restent ignorés, index vide.
- Aucun test .NET ou migration : aucun fichier applicatif modifié. Aucun test
  de credentials réels, de panne Docker, de purge réelle ou de six mois de rétention.
- Branche/HEAD inchangés ; aucun staging, commit ou push.

Liste exacte des fichiers concernés à la fin :

| État Git | Fichier | Changement |
| --- | --- | --- |
| M | compose.yml | Deux paramètres de session uniquement |
| M | docs/n8n-operations.md | État rapporté propriétaire/2FA, sessions préparées, exigences réseau, liens et limites |
| ?? | docs/n8n-encrypted-backups.md | Stratégie initiale complétée, parcours synthétique, correction Windows et procédure réelle future |
| ?? | docs/phase-9-2-step-2-validation.md | Audit historique conservé et présent complément de preuves |

**PRÊT POUR AUDIT FINAL DU PÉRIMÈTRE.** Cette conclusion porte sur le complément
autorisé et ses tests synthétiques ; elle ne clôture pas la Phase 9.2, ne vaut
pas validation de la sauvegarde réelle hors machine et n'autorise ni application
runtime, ni commit/push, ni démarrage de 9.3.
