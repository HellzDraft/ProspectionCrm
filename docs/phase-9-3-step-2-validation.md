# Phase 9.3.2 — Authentification, autorisations et transport HTTPS

Implémentation locale du 14 octobre 2026, sur `main`, HEAD de départ
`45ef44b4480f73f29d666e3c3b7fcb6ba760306f`. Index et working tree propres au
départ. La [CI #59](https://github.com/HellzDraft/ProspectionCrm/actions/runs/38425587075)
de 9.3.1 est réussie ; son constat est ajouté au rapport précédent sans effacer #58.

## Périmètre et fichiers

- `Security/OperatorSecurity.cs`, `SecurityRegistration.cs`, `OperatorProvisioning.cs`,
  `Controllers/SessionController.cs`, `Program.cs` : cookie opérateur, validation
  d'identité, autorisation restrictive, antiforgery, CORS et provisionnement console.
- `CurrentWorkspaceProvider` résout exclusivement le contexte humain authentifié.
  Les workers conservent leurs workspaces explicites issus des jobs persistés.
  Approbations/rejets et réinitialisations de circuit enregistrent l'acteur authentifié,
  avec vérification du propriétaire et verrou partagé sur le workspace.
- `IntegrationSecurity.cs`, `IntegrationTransport.cs`, `IntegrationController.cs` :
  HMAC, réception bornée, client HTTP dédié, création/consultation/envoi/traitement
  explicites de sondes et résolution contrôlée des tentatives interrompues.
- `IntegrationStore`, entité/configuration `IntegrationOperation`, migration 9.3.2
  et snapshot : rattachement nullable à une connexion logique ; autorisation avant
  tous les chemins de réception, y compris doublon et conflit de contenu.
- Blazor : `SessionService.cs`, `Login.razor`, `SessionMenu.razor`, `MainLayout`,
  enregistrement du client et URL API HTTPS. Aucun remaniement des autres pages.
- Tests : nouvelles classes `OperatorSecurityTests`, `OperatorDecisionSecurityTests`,
  `IntegrationSecurityTests`, `IntegrationTransportTests`, `IntegrationSecurityMigrationTests`,
  `BlazorSessionSecurityTests`,
  fabriques `SecurityTestHost`, `IntegrationTlsServer`, `BusinessTestIdentity` ;
  adaptation des fabriques métier, acteurs de décision et attentes de migration historiques.

Le contrat JSON v1 `technical.probe` reste strict et sans effet métier. La signature
authentifie les octets bruts ; la canonicalisation existante sert uniquement à
l'idempotence. Les révisions, verrous, transactions, contraintes et séparation
tentative/réception/traitement de 9.3.1 sont conservés.

## Matrice des accès

| Routes | Anonyme | Cookie opérateur valide | HMAC machine valide |
| --- | --- | --- | --- |
| `GET /api/session/antiforgery` | Oui : amorçage du jeton CSRF | Oui : jeton lié à l'identité | Pas de privilège particulier |
| `POST /api/session/login` | Mot de passe + antiforgery, 5 essais/minute | Même contrôle | Aucun droit d'authentification humaine |
| `GET /api/session`, `POST /api/session/logout` | 401 | Oui ; CSRF pour logout | 401 |
| Toutes les routes CRM, documents, configurations, collecte, automatisation, supervision | 401 | Workspace de l'identité ; CSRF pour mutations | 401, avec ou sans en-têtes HMAC |
| `/api/setup/bootstrap`, `/api/setup/initial-pipelines` | 401 | Oui, contrôles de cohérence conservés | 401 |
| Approbations/rejets et réinitialisation de circuit | 401 | Propriétaire authentifié, rattachement revérifié | 401 |
| `POST /api/integrations/results` | 401 | Le cookie seul donne 401 | Connexion et workspace autorisés ; sinon 403 |
| Routes humaines de sonde ci-dessous | 401 | Oui ; création/envoi nécessitent une intégration active valide | 401 |
| OpenAPI en Development | 401 | Autorisation humaine par défaut | 401 |

Seules l'amorce CSRF et la connexion sont anonymes. Aucun diagnostic CRM anonyme.
Les preflights CORS `OPTIONS` des origines autorisées sont traités par le middleware
et ne donnent accès à aucune donnée. La politique par défaut et la politique
fallback exigent le schéma humain. Le résultat machine exige son propre schéma
et constitue la seule exception à l'antiforgery MVC. Les réponses sont des statuts
API 401/403, sans redirection HTML. Le HTTP non chiffré est refusé (400).

### Session humaine

`PasswordHasher<OperatorOptions>` Identity V3 vérifie le hash configuré côté serveur.
Le formulaire transmet seulement le mot de passe. Le serveur fixe l'utilisateur
et le workspace ; il vérifie que le compte existe, possède ce workspace et que
celui-ci est actif. La présence d'une ligne propriétaire n'est jamais une connexion.

Cookie `__Host-ProspectionCrm`, `Secure`, `HttpOnly`, `SameSite=Strict`, chemin `/`,
sans domaine. Cookie de session navigateur, validité serveur maximale **8 heures**,
aucun renouvellement glissant. Chaque requête revérifie la configuration et le
rattachement en base. Modifier/supprimer le hash ou les identifiants invalide les
cookies existants. Logout supprime le cookie navigateur ; une copie volée reste
soumise à l'échéance et à la révocation globale par changement de configuration.
Les clés ASP.NET Data Protection du serveur doivent être protégées par le compte
d'exécution et conservées si l'on souhaite garder les sessions après redémarrage.
Les tests utilisent un fournisseur Data Protection éphémère.

Les essais de connexion utilisent un seau global de **5 requêtes par minute par
processus**, sans file d'attente, puis 429. Ce choix correspond à l'unique opérateur
local ; un déploiement multi-instance nécessiterait une limite partagée. Les erreurs
de credentials sont génériques. Configuration absente ou invalide : aucune session
émise ; activation d'une intégration avec sécurité incohérente : démarrage refusé.

Antiforgery standard ASP.NET Core, cookie `__Host-ProspectionCrm-CSRF` sécurisé et
en-tête `X-CSRF-TOKEN`. Le frontend demande le jeton avant chaque mutation,
connexion et déconnexion incluses ; le jeton anonyme n'est pas réutilisable après
connexion. Les credentials ne sont activés que pour le client lié à l'URL API ;
toute destination extérieure est refusée, ainsi que les redirections Fetch.
Aucun mot de passe, clé HMAC ou cookie de session dans localStorage ou appsettings frontend.

## Protocole HMAC v1

HTTPS obligatoire. Trois en-têtes uniques, sans liste ni doublon :

| En-tête | Format |
| --- | --- |
| `X-CRM-Key-Id` | 1–64 caractères ASCII `[a-zA-Z0-9_-]`, sensible à la casse |
| `X-CRM-Timestamp` | UTC Unix seconds, exactement 10 chiffres décimaux |
| `X-CRM-Signature` | 64 caractères hexadécimaux minuscules, HMAC-SHA256 |

Le message signé est l'UTF-8, sans BOM, des sept lignes suivantes, séparées par
**LF (0A), sans LF final** :

```text
crm-hmac-v1
<audience>
<key-id>
<timestamp>
POST
<cible HTTP exacte>
<sha256 hexadécimal minuscule des octets du corps>
```

Audience `crm-to-n8n` à l'aller, `n8n-to-crm` au retour. La réception accepte
exactement `/api/integrations/results`, sans query string, changement de casse,
slash final, préfixe ni réécriture proxy. L'aller signe `PathAndQuery` de l'unique
URL configurée ; cette configuration refuse userinfo, query string et fragment.
La version est fixée par la première ligne `crm-hmac-v1` : aucun autre format
n'est accepté. La clé est une valeur serveur de 32 octets encodée en Base64.
La comparaison finale utilise `CryptographicOperations.FixedTimeEquals`.

Fenêtre par défaut **±300 secondes incluses**, configurable entre 30 et 300 secondes,
avec `TimeProvider`. Une requête périmée est refusée avant réception. Une nouvelle
tentative peut resigner les mêmes octets avec un nouveau timestamp, sans changer
les UUID logiques. Les octets sont signés avant décodage/désérialisation. UTF-8 strict,
`application/json`, charset absent ou `utf-8`, pas de `Content-Encoding` ; la lecture
s'arrête à 4 097 octets et refuse au-delà de 4 096, même en chunked sans longueur.

Vecteur **synthétique public**, impropre à toute configuration réelle : clé composée
des octets `00 01 … 1f`, audience `n8n-to-crm`, clé `vector-1`, timestamp `1791936000`,
méthode `POST`, cible `/api/integrations/results`, corps ASCII `{}`.

```text
SHA256(body) = 44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a
HMAC        = 5c6e8b91b62ac15358e704d5fa951f3c6feeb58079265db3b42890fdd2645e3d
```

Le vecteur est également vérifié indépendamment avec Python `hmac`/`hashlib`, puis
figé dans le test .NET. Changer audience, ID de clé, timestamp, méthode, cible ou
espacement du corps change la signature. `{}` illustre seulement la cryptographie,
ce n'est pas une enveloppe `technical.probe` valide.

### Autorisation et rotation

Chaque clé entrante est liée **dans la configuration serveur** à `ConnectionId`
et `WorkspaceId`. Ces UUID ne viennent jamais du JSON externe. Les clés sortantes
et entrantes doivent être distinctes, vérifié sur leurs octets. Une opération
persiste la connexion logique, indépendante de l'ID de clé.

Rotation prévue : ajouter une nouvelle clé entrante activée avec la même connexion
et le même workspace, valider la signature côté émetteur, basculer l'émetteur,
puis désactiver l'ancienne (`Enabled=false`) après la période de chevauchement
choisie. Changer la clé sortante exige une coordination explicite du destinataire.
Conserver l'UUID de connexion pour une rotation ; changer cet UUID représente une
autre connexion. Pour révoquer la dernière clé entrante active sans remplacement,
mettre aussi `Integrations:Enabled=false` : une intégration activée exige au moins
une clé entrante active pour sa connexion. Les changements de secrets peuvent nécessiter un redémarrage
si leur fournisseur de configuration ne notifie pas les rechargements. Aucun secret
de production n'est provisionné par cette intervention.

`ReceiveAsync` exige désormais un `IntegrationAccess`. Il verrouille l'opération
filtrée par workspace/connexion avant de consulter un doublon. Un ID de réception
réutilisé qui appartient à un autre périmètre est aussi refusé avant comparaison.
Les anciennes opérations `ConnectionId=NULL` ne sont pas rattachées automatiquement.
Réception : HTTP 202 `{ "status": "received", "alreadyRecorded": false|true }`,
uniquement après persistance atomique ou constat d'un doublon durable. Conflit de
contenu autorisé : 409 ; accès hors périmètre : 403, sans détail de la réception.

## Transport et commandes humaines

Client typé géré par `IHttpClientFactory`, URL HTTPS exacte côté serveur, validation
TLS standard, aucun callback permissif, aucun proxy/credential ambiant/cookie,
aucune redirection automatique ni décompression. Aucun changement au transport RSS.
Timeout total par défaut 15 s, intervalle admis 1–60 s ; connexion 10 s maximum
dans cette borne ; en-têtes réponse 8 Kio, corps ACK 4 096 octets maximum.
Concurrence par processus : 2 par défaut, 1–8 autorisées, refus immédiat 409 si saturé.
Le sémaphore est fixé au démarrage ; modifier la concurrence exige un redémarrage.

| Commande | Effet |
| --- | --- |
| `POST /api/integrations/probes` | Crée une sonde dans le workspace et la connexion configurés |
| `GET /api/integrations/probes/{id}` | État logique, tentatives et dates de réception/traitement |
| `GET /api/integrations/pending-receipts` | Jusqu'à 100 réceptions durables non traitées du workspace |
| `POST /api/integrations/probes/{id}/send`, `{ "revision": n }` | Une tentative explicite, aucun retry automatique |
| `POST /api/integrations/probes/{id}/process`, `{ "revision": n }` | Traitement transactionnel de la sonde déjà reçue |
| `POST /api/integrations/probes/{id}/attempts/{attemptId}/resolve-unknown` | Résout seulement une tentative `started` vieille d'au moins 2 minutes en `unknown` |

La transaction `BeginAttemptAsync` est terminée avant HTTP. Le serveur de test
peut donc livrer le callback pendant l'attente de l'ACK. `FinishAttemptAsync`
n'altère jamais l'état logique. Seul HTTP **202**, avec réponse complètement lue
dans les bornes, vaut `acknowledged`. Les autres réponses hors 5xx sont `failed` ;
5xx, timeout, erreur réseau ambiguë, réponse ACK trop grosse/incomplète sont `unknown`.
Un refus TLS avant envoi est `failed`. Aucun de ces statuts ne réussit la sonde.

Après départ, le délai réseau est indépendant de l'annulation du navigateur,
puis la finalisation dispose de 5 s. Un crash ou un échec de cette finalisation
laisse `started`, visible en consultation. Attendre au moins 2 minutes, constater
l'état, puis utiliser explicitement `resolve-unknown`. Cela ne renvoie rien.
Toute nouvelle tentative nécessite un autre appel humain avec la révision courante.
La reprise d'une réception durable utilise `pending-receipts` puis `process` et
ne nécessite aucun nouvel envoi réseau. Pas de worker de transport dans cette phase.

## Provisionnement ultérieur, non exécuté sur l'installation réelle

Les profils HTTPS cohérents sont API **https://localhost:7096** et Blazor
**https://localhost:7075**. L'API refuse HTTP. Les profils `https` n'annoncent plus
de port HTTP. Le certificat local doit être déjà valide pour `localhost` et reconnu
par le navigateur ; aucun certificat ni changement de confiance n'est installé ici.
Ne pas remplacer un seul côté par `127.0.0.1` : origine, nom du certificat et cookies
changent. Un cookie est lié au nom d'hôte, pas au port ; réserver ces hôtes locaux
aux applications de confiance. CORS autorise seulement les origines HTTPS exactes
de `Operator:AllowedOrigins`, avec credentials, méthodes GET/HEAD/POST/PUT/PATCH/DELETE/OPTIONS
et en-têtes Content-Type/X-CSRF-TOKEN. Aucun wildcard.

Procédure console locale à effectuer plus tard, après revue de la destination SQL
et application volontaire des migrations par l'opérateur. La commande n'applique
elle-même aucune migration et n'ouvre aucun serveur HTTP.

1. Configurer la connexion serveur hors Git (User Secrets en Development ou
   variables d'environnement) et choisir un fichier temporaire **hors dépôt**,
   dans un répertoire privé du compte opérateur. Arrêter l'API pour le provisionnement.
2. Installation vierge : `dotnet run --project src/ProspectionCrm.Api --no-launch-profile -- --provision-operator --bootstrap-empty --output <chemin-prive>`.
   Cette option exige une installation vide et réutilise `BootstrapService`.
3. Propriétaire existant : consulter localement les UUID en base puis
   `dotnet run --project src/ProspectionCrm.Api --no-launch-profile -- --provision-operator --user-id <UUID> --workspace-id <UUID> --output <chemin-prive>`.
   Le rattachement réel est contrôlé ; aucune réattribution du propriétaire.
4. Saisir deux fois un mot de passe de 12–256 caractères dans la console masquée.
   Aucun mot de passe en argument ni en journal. Le fichier neuf contient uniquement
   `Operator:UserId`, `Operator:WorkspaceId`, `Operator:PasswordHash`. Un fichier
   existant n'est jamais écrasé ; aucune réinitialisation automatique.
5. Importer explicitement les valeurs dans la configuration **API seulement**,
   par exemple `Get-Content -Raw -LiteralPath '<chemin-prive>' | dotnet user-secrets set --project src/ProspectionCrm.Api`,
   puis supprimer le fichier temporaire privé. Pour User Secrets, définir
   `ASPNETCORE_ENVIRONMENT=Development` lors de l'exécution console : `--no-launch-profile`
   ne charge pas automatiquement cet environnement. Un environnement non Development
   utilise ses variables/fournisseurs de configuration serveur.
6. Démarrer les deux applications avec `--launch-profile https`, ouvrir Blazor,
   se connecter. Aucune inscription publique, récupération email ou mot de passe par défaut.

La configuration versionnée conserve `Integrations:Enabled=false`. Pour un futur
raccordement validé, renseigner hors Git :

```text
Integrations:Enabled                 = true (uniquement après préparation)
Integrations:Endpoint                = https://<hote-valide>/<chemin-exact>
Integrations:ConnectionId            = <UUID stable de connexion>
Integrations:WorkspaceId             = <UUID autorisé de l'opérateur>
Integrations:TimeoutSeconds          = 15
Integrations:MaxConcurrentSends       = 2
Integrations:ClockWindowSeconds       = 300
Integrations:OutgoingKey:Id           = <identifiant de clé sortante>
Integrations:OutgoingKey:Enabled      = true
Integrations:OutgoingKey:SecretBase64 = <secret serveur distinct de 32 octets>
Integrations:OutgoingKey:ConnectionId = <même connexion>
Integrations:OutgoingKey:WorkspaceId  = <même workspace>
Integrations:IncomingKeys:0:Id           = <identifiant de clé entrante>
Integrations:IncomingKeys:0:Enabled      = true
Integrations:IncomingKeys:0:SecretBase64 = <autre secret serveur de 32 octets>
Integrations:IncomingKeys:0:ConnectionId = <connexion autorisée>
Integrations:IncomingKeys:0:WorkspaceId  = <workspace autorisé>
```

## Migration et validation

Migration additive `20261014075029_Phase932IntegrationConnection` : une colonne UUID
nullable et une contrainte refusant l'UUID vide ; aucune réécriture des migrations
historiques, aucune reprise automatique. Les attentes de dernière migration sont
actualisées et les assertions historiques d'ordre/montée/descente conservées.
Les tests utilisent exclusivement PostgreSQL 18 jetable (Testcontainers).

Les tests métier historiques utilisent une identité et un fournisseur de workspace
**uniquement dans le projet de tests**. Leurs scénarios de bootstrap/corruption
restent testables sans passer par un enrollment public. Ils isolent l'antiforgery
pour tester le métier. Les nouveaux tests de sécurité utilisent les vrais cookies,
PasswordHasher, antiforgery, autorisation, HMAC et stockage PostgreSQL, sans branche
Development/Testing permissive dans l'application.

Le client Blazor est testé avec son véritable handler et un transport simulé :
jetons CSRF renouvelés, credentials Include, redirections refusées, destinations
étrangères bloquées avant tout appel et navigation après 401. CORS et lecture/écriture
avec cookies sont aussi testés dans le pipeline API. Le rendu/interactif du navigateur
WASM n’a pas été validé manuellement sur une installation provisionnée.

Transport simulé : annulation du navigateur, saturation de concurrence, absence
d'appel quand désactivé. Transport réel : Kestrel HTTPS éphémère, CA isolée via
`CertificateChainPolicy.CustomRootTrust`, aucun callback désactivant la validation.
Chaîne, nom d'hôte et période de validité sont vérifiés. Sous Windows, Schannel
nécessite un conteneur temporaire de clé privée supprimé à la libération du certificat
(pas de `PersistKeySet`) ; aucun magasin de confiance n'est modifié. Voir les références
[CertificateChainPolicy](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions.certificatechainpolicy?view=net-10.0)
et [limitations SslStream/Schannel](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting).
La CA est générée pour le test, jamais un certificat réel. Le test de réception
Kestrel exerce également le vrai TLS entrant, la cible HTTP brute (variantes refusées)
et la borne de corps en transfert chunked. Le parcours positif principal utilise
TLS réel à l’aller et le pipeline ASP.NET TestServer pour le callback signé ; le test
Kestrel entrant complète explicitement cette couverture.

Résultats locaux définitifs du 14 octobre 2026 :

| Contrôle | Résultat |
| --- | --- |
| `dotnet build ProspectionCrm.slnx -c Release --no-restore` | Réussi, 0 erreur ; 2 avertissements CS8618 préexistants dans `Settings.razor` (`UserName`, `Email`), hors périmètre |
| Passage ciblé sécurité/intégrations/migrations/workers, incluant la sécurité RSS | **229/229**, 0 échec, 0 ignoré, 2 min 58 s |
| Complément réception Kestrel TLS réelle | **1/1**, 0 échec |
| Complément client Blazor (véritable handler, transport simulé) | **6/6**, 0 échec |
| **Suite complète Release**, `dotnet test ProspectionCrm.slnx -c Release --no-build --no-restore` | **1 336/1 336**, 0 échec, 0 ignoré, **27 min 59 s**, code 0 |
| `dotnet ef migrations has-pending-model-changes --project src/ProspectionCrm.Api --configuration Release --no-build` | Aucun écart modèle/migration, code 0 |
| `git diff --check` et contrôle des nouveaux fichiers | Réussis ; aucun espace final |

Les compteurs des fichiers TRX `phase932-targeted.trx` et `phase932-full.trx`
ont également été relus. Les passages se recouvrent : leurs nombres ne s'additionnent
pas pour former un nombre de tests distincts. La suite complète couvre le code
applicatif final ; seul le rapport et la normalisation des fins de ligne ont été
finalisés pendant/après son exécution. Le build Release a été relancé ensuite.
Cette validation globale **locale** ne constitue pas une nouvelle CI publiée : aucun
commit/push n'a été effectué et la CI #59 concerne exclusivement la 9.3.1.

Le contrôle EF emploie `ASPNETCORE_ENVIRONMENT=Testing` et une connexion de conception
inerte vers `127.0.0.1:1`, sans accès à Development. Les outils EF 10.0.10 signalent
leur ancienneté face au runtime 10.0.12 ; le contrôle réussit sans mise à jour d'outil.
Le test de migration vérifie une base vierge, la montée depuis 9.3.1 avec opération
existante non rattachée, son refus d'accès machine, la descente/remontée et la
préservation des données. Les tests historiques de migrations restent présents.

Pendant le développement, un premier passage a révélé l'absence d'enregistrement
du filtre MVC antiforgery (corrigée par `AddControllersWithViews`). Le banc de test
nécessitait aussi de fournir `RawTarget` sous TestServer, puis d'importer la clé TLS
dans un conteneur temporaire compatible Schannel sous Windows. Le test Kestrel
entrant valide ensuite `RawTarget` sans cette adaptation. Ces défauts de premier
passage sont corrigés et les tests concernés réussissent dans la suite complète.
Une compilation lancée pendant un test a rencontré un verrou de DLL Windows ;
les compilations et exécutions suivantes ont été sérialisées. Aucun assouplissement
de l'authentification, de l'autorisation ou de TLS n'a été introduit pour ces corrections.

## État Git de livraison locale

`main`, HEAD `45ef44b4480f73f29d666e3c3b7fcb6ba760306f`, inchangés.
Index vide ; aucun staging, commit ou push. 55 fichiers suivis modifiés et
22 nouveaux fichiers, tous dans le périmètre ci-dessous. Aucun changement
inattendu préexistant. Les résultats TRX et les binaires restent ignorés.

```text
docs/phase-9-3-step-1-validation.md
docs/phase-9-3-step-2-validation.md
src/ProspectionCrm.Api/Controllers/AutomationActionRequestsController.cs
src/ProspectionCrm.Api/Controllers/AutomationCircuitBreakerController.cs
src/ProspectionCrm.Api/Controllers/AutomationSupervisionErrorsAttribute.cs
src/ProspectionCrm.Api/Controllers/IntegrationController.cs
src/ProspectionCrm.Api/Controllers/SessionController.cs
src/ProspectionCrm.Api/Data/Configurations/IntegrationConfiguration.cs
src/ProspectionCrm.Api/Data/Migrations/20261014075029_Phase932IntegrationConnection.Designer.cs
src/ProspectionCrm.Api/Data/Migrations/20261014075029_Phase932IntegrationConnection.cs
src/ProspectionCrm.Api/Data/Migrations/ProspectionCrmDbContextModelSnapshot.cs
src/ProspectionCrm.Api/Entities/IntegrationOperation.cs
src/ProspectionCrm.Api/Program.cs
src/ProspectionCrm.Api/Properties/launchSettings.json
src/ProspectionCrm.Api/Security/OperatorProvisioning.cs
src/ProspectionCrm.Api/Security/OperatorSecurity.cs
src/ProspectionCrm.Api/Security/SecurityRegistration.cs
src/ProspectionCrm.Api/Services/Automation/AutomationActionRequestService.cs
src/ProspectionCrm.Api/Services/Automation/AutomationCircuitBreakerService.cs
src/ProspectionCrm.Api/Services/Automation/AutomationSupervisionWorkspace.cs
src/ProspectionCrm.Api/Services/CurrentWorkspaceProvider.cs
src/ProspectionCrm.Api/Services/Integrations/IntegrationSecurity.cs
src/ProspectionCrm.Api/Services/Integrations/IntegrationStore.cs
src/ProspectionCrm.Api/Services/Integrations/IntegrationTransport.cs
src/ProspectionCrm.Api/appsettings.json
src/ProspectionCrm.Blazor/Layout/MainLayout.razor
src/ProspectionCrm.Blazor/Layout/SessionMenu.razor
src/ProspectionCrm.Blazor/Pages/Login.razor
src/ProspectionCrm.Blazor/Program.cs
src/ProspectionCrm.Blazor/Properties/launchSettings.json
src/ProspectionCrm.Blazor/Services/SessionService.cs
src/ProspectionCrm.Blazor/wwwroot/appsettings.json
tests/ProspectionCrm.Api.Tests/AutomationActionRequestFixture.cs
tests/ProspectionCrm.Api.Tests/AutomationActionRequestMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationCircuitMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationEventDispatcherTests.cs
tests/ProspectionCrm.Api.Tests/AutomationJobFixture.cs
tests/ProspectionCrm.Api.Tests/AutomationJobMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationRuntimeMigrationTests.cs
tests/ProspectionCrm.Api.Tests/AutomationSettingsTests.cs
tests/ProspectionCrm.Api.Tests/AutomationSupervisionFixture.cs
tests/ProspectionCrm.Api.Tests/BlazorSessionSecurityTests.cs
tests/ProspectionCrm.Api.Tests/BootstrapTests.cs
tests/ProspectionCrm.Api.Tests/BusinessTestIdentity.cs
tests/ProspectionCrm.Api.Tests/IngestionHistoryReadTests.cs
tests/ProspectionCrm.Api.Tests/IngestionHistoryTests.cs
tests/ProspectionCrm.Api.Tests/IngestionTests.cs
tests/ProspectionCrm.Api.Tests/InitialPipelineTests.cs
tests/ProspectionCrm.Api.Tests/IntegrationPersistenceMigrationTests.cs
tests/ProspectionCrm.Api.Tests/IntegrationPersistenceTests.cs
tests/ProspectionCrm.Api.Tests/IntegrationSecurityMigrationTests.cs
tests/ProspectionCrm.Api.Tests/IntegrationSecurityTests.cs
tests/ProspectionCrm.Api.Tests/IntegrationTlsServer.cs
tests/ProspectionCrm.Api.Tests/IntegrationTransportTests.cs
tests/ProspectionCrm.Api.Tests/OperatorDecisionSecurityTests.cs
tests/ProspectionCrm.Api.Tests/OperatorSecurityTests.cs
tests/ProspectionCrm.Api.Tests/OpportunitySourceTests.cs
tests/ProspectionCrm.Api.Tests/PersistentSourceIdentityConcurrencyTests.cs
tests/ProspectionCrm.Api.Tests/Phase4ReconstructionTests.cs
tests/ProspectionCrm.Api.Tests/Phase6ReconstructionTests.cs
tests/ProspectionCrm.Api.Tests/Phase7ApiErrorTests.cs
tests/ProspectionCrm.Api.Tests/Phase7ReconstructionTests.cs
tests/ProspectionCrm.Api.Tests/PipelineCloneTests.cs
tests/ProspectionCrm.Api.Tests/PipelineStageOrderTests.cs
tests/ProspectionCrm.Api.Tests/PipelineStageTests.cs
tests/ProspectionCrm.Api.Tests/PipelineTests.cs
tests/ProspectionCrm.Api.Tests/PipelineTransferTests.cs
tests/ProspectionCrm.Api.Tests/ProspectionCrm.Api.Tests.csproj
tests/ProspectionCrm.Api.Tests/RssCollectionTests.cs
tests/ProspectionCrm.Api.Tests/SecurityTestHost.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionJobMigrationTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionJobTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionRetryTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulerLifecycleTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionSchedulingTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionWorkerLifecycleTests.cs
tests/ProspectionCrm.Api.Tests/SourceCollectionWorkerTests.cs
```

## Prérequis 9.3.3 et limites

Le côté n8n doit encore être réalisé et vérifié : lecture des octets exacts,
reconstruction du message signé, validation de l'audience/clé/horloge/cible,
ACK 202, création du résultat v1 et callback signé, doublons, crash/reprise et rotation.
n8n ne vérifie pas automatiquement ce protocole. Il faudra également provisionner
volontairement l'opérateur, les secrets distincts, les URL et certificats valides,
puis valider le parcours navigateur installé et le raccordement réel.

Aucune connexion à n8n réel ou à un fournisseur, aucun email, collecte externe ou
effet métier externe ; aucune modification d'image, volume, pare-feu, confiance système,
sauvegarde, Drive, restic, session n8n ou 2FA. Aucune migration sur Development réel.
Aucun secret réel demandé, généré ou installé. Les credentials des tests sont
synthétiques et les sorties de build/test restent ignorées par Git.
