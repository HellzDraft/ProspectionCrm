# Collecte RSS/Atom manuelle V1 — Phase 6.3

La source `rss` accepte RSS 2.0 et Atom 1.0 publics, sans authentification.
`SavedSearch.SearchUrl` contient l'URL HTTPS du flux. Elle est conservée dans le
snapshot historique : **ne jamais y mettre de token, clé API ou secret**, y compris
dans la query. L'adaptateur n'interprète pas `SourceConfiguration.ConfigurationJson` :
il ne l'envoie jamais en header ou paramètre. Son hash seul est historisé.

## Appel et critères

Créer une SourceConfiguration `SourceTypeCode = "rss"`, active, puis une SavedSearch
active rattachée au pipeline souhaité, avec par exemple :

```json
{
  "maxItems": 100,
  "defaultCompanyName": "Entreprise facultative"
}
```

`CriteriaJson` est un objet strict : propriétés inconnues et répétées refusées,
`maxItems` entier 1–100 (100 par défaut), société facultative non blanche, trimée,
200 caractères maximum. `null` explicite n'est pas une société valide. Aucun filtre
de mots-clés n'est appliqué. Le CRUD générique reste générique ; la validation RSS
intervient avant le réseau, sur `/collect`.

```http
POST /api/saved-searches/{savedSearchId}/collect
Content-Type: application/json

{"pipelineStageId":"<guid>"}
```

Le body exige `pipelineStageId` et refuse les propriétés inconnues. L'étape doit être
active et appartenir au pipeline de la recherche. Le Workspace V1 doit être actif
et unique ; les recherches étrangères renvoient 404 avant tout accès réseau.

Succès : **201**, `Location: /api/source-executions/{executionId}` et
`SourceCollectionDto { ingestion, summary }`. `ingestion` reprend exactement le
résultat existant. `summary` contient :

- `sourceTypeCode: "rss"`, `feedFormat: "rss2" | "atom1"` ;
- `entriesRead` : toutes les entrées du document borné ;
- `entriesMapped` : entrées transmises à l'ingestion ;
- `entriesSkipped` : entrées inutilisables seulement ;
- `wasTruncated` : plus de `maxItems` entrées utilisables.

Les entrées utilisables au-delà de `maxItems` ne sont pas comptées comme ignorées.
Les doublons sont transmis dans l'ordre du document : ils restent à la charge de
l'ingestion. `SourceExecution.ItemsFound` vaut uniquement `entriesMapped` en succès.
`POST /ingestions`, ses DTO et les routes d'historique/provenance restent inchangés.

## Architecture et cohérence

`SourceAdapterRegistry` résout les `ISourceAdapter` injectés par code ordinal exact ;
les doublons de codes provoquent une erreur de configuration. `RssAtomSourceAdapter`
combine `IRssFeedTransport` et `RssAtomFeedParser`. L'adaptateur n'a aucune dépendance
EF et ne crée ni exécution, ni opportunité, ni provenance. Ajouter un adaptateur
nécessite son inscription DI, sans changement au moteur d'ingestion.

`SourceCollectionService` valide les ressources et matérialise le contexte sans
tracking. Aucun verrou, transaction ou connexion PostgreSQL ne reste ouvert durant
la récupération. Un SHA-256 hexadécimal minuscule est calculé sur une projection JSON
canonique explicite (ordre fixe, dates UTC) :

- recherche : IDs, nom, SearchUrl, CriteriaJson, Enabled, UpdatedAt, ArchivedAt ;
- configuration : IDs, nom, type, BaseUrl, hash ConfigurationJson, Enabled et dates ;
- pipeline : IDs, nom, type et dates ;
- étape : IDs, nom, catégorie, SortOrder et dates.

Le fingerprint ne repose pas uniquement sur UpdatedAt. L'appel interne enrichi à
`IIngestionService` vérifie ce contexte après acquisition de `WorkspaceIdentityLock`
et relecture sous verrous de lignes `FOR SHARE` (y compris l'étape). Les modifications
concurrentes restent bloquées jusqu'à la fin de la transaction d'ingestion. Une
différence renvoie 409 `CollectionConfigurationChanged` sans ingérer l'ancien flux.
La méthode existante d'ingestion sans précondition est conservée.

## Mapping

| Champ | RSS 2.0 | Atom 1.0 |
|---|---|---|
| Title | `channel/item/title` | `feed/entry/title` dans le namespace Atom |
| ExternalId | `guid` trimé, casse conservée | `id` trimé, casse conservée |
| SourceUrl | `link`, sinon GUID permalink utilisable | premier `link` alternate utilisable, sinon premier href utilisable |
| Description | `content:encoded`, sinon `description` | `content`, sinon `summary` |

Les titres/descriptions sont décodés en texte, balises retirées, espaces regroupés et
extrémités trimées. Les titres sont tronqués à 200 caractères et les descriptions à
10000, sans couper une paire UTF-16. Le XHTML Atom est converti en texte ; un contenu
externe `src` n'est jamais téléchargé. Un titre vide ou une entrée sans ID ni URL est
ignoré. Une description vide devient null.

Un ID supérieur à 500 caractères est omis si une URL utilisable existe ; sinon il
devient `rss-sha256:<sha256 UTF-8 de l'ID trimé>`. Les liens relatifs sont résolus
contre l'URI finale du flux ; seules les URL HTTP(S) absolues sans UserInfo et au plus
2048 caractères sont conservées. Pour RSS, `isPermaLink` vaut true par défaut selon
RSS 2.0 ; un GUID opaque/relatif ou explicitement `isPermaLink="false"` ne sert pas
d'URL. Les URL d'entrées sont **stockées uniquement**, jamais téléchargées.

CompanyName vient uniquement de `defaultCompanyName`. Location reste null. Aucune
Company n'est créée automatiquement. Date de publication, auteurs, catégories,
images, pièces jointes et métadonnées arbitraires ne sont pas conservés.

## Transport et SSRF

`RssCollection` dans appsettings.json est validé au démarrage : timeout 15 s (1–60),
réponse 2 000 000 octets maximum (1024–2 000 000), redirects 3 (0–3), maxItems par
défaut 100 (1–100), User-Agent fixe `ProspectionCrm/1.0` uniquement.

La requête exige HTTPS, port effectif 443, nom DNS, sans UserInfo. Le fragment est
retiré. IP littérales, localhost, sous-domaines .localhost, .local, .internal,
home.arpa et noms sans point sont refusés. Aucun credential, cookie, Authorization,
proxy implicite ou redirect automatique. Les redirects sont suivis manuellement et
revalidés ; leur nombre et le délai sont bornés pour toute la chaîne.

Le `SocketsHttpHandler.ConnectCallback` résout le DNS **à la connexion**, rejette
l'hôte entier si une adresse est interdite et connecte explicitement une adresse
validée, dans un ordre déterministe. Il n'y a pas de seconde résolution DNS par le
socket. Host/SNI et validation TLS restent ceux du domaine original. Le trust système
et l'usage certificat serveur restent vérifiés, sans callback acceptant les certificats
invalides. Les téléchargements de certificats AIA et les requêtes de révocation sont
désactivés pour éviter un chemin HTTP indépendant de cette politique sortante ; un
serveur doit fournir sa chaîne complète ou utiliser des intermédiaires déjà disponibles.
Les connexions
ne sont pas réutilisées entre tentatives ; HTTP/1.1 évite tout transport alternatif
qui contournerait ce callback. Seules les adresses validées sont essayées en cas
d'échec TCP ; aucune requête HTTP n'est relancée par une politique de retry.

La classification est conservatrice : exclusions privées, loopback, link-local,
CGNAT, multicast, unspecified, documentation, benchmark, réserves et mécanismes de
transition. Les IPv4 mappées suivent la règle IPv4. IPv6 est limité à 2000::/3 hors
blocs spéciaux exclus ; certaines exceptions anycast publiques de blocs réservés
restent volontairement refusées. Références : registres
[IANA IPv4](https://www.iana.org/assignments/iana-ipv4-special-registry/) et
[IANA IPv6](https://www.iana.org/assignments/iana-ipv6-special-registry/),
[ConnectCallback .NET](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0).
Cette liste doit être revue lors d'une évolution des registres.

`ResponseHeadersRead`, Content-Length contrôlé lorsqu'il est disponible et lecture
incrémentale plafonnent les octets **décompressés**, y compris gzip/deflate/Brotli.
Seuls les 2xx sont acceptés. Types : application/rss+xml, application/atom+xml,
application/xml, text/xml, application/octet-stream ou absence de Content-Type.
Dans les deux derniers cas, le premier caractère après BOM/espaces doit être `<`.
Le parser valide ensuite réellement XML et le format ; text/html est refusé.

XmlReader interdit DTD/entités externes, utilise XmlResolver=null, borne les
caractères et la profondeur à 64, sans ressource externe. Il parcourt le document
complet borné et respecte l'annulation pendant la validation et le mapping.

## Erreurs et historique

Tous les ProblemDetails de collecte portent `code`, un `detail` contrôlé,
`executionId`, `itemIndex`, `upstreamStatusCode` (nullables). Aucun body/header
upstream, IP, URL complète, SQL ou stack trace n'est renvoyé. Les événements de
collecte ne contiennent que des IDs, codes et compteurs contrôlés.

| HTTP | Codes principaux |
|---|---|
| 400 | InvalidRequest ; validation de lot interne conservée |
| 404 | ResourceNotFound |
| 409 | WorkspaceUnavailable, InactiveResource, WrongPipeline, UnsupportedSourceType, MissingFeedUrl, InvalidRssCriteria, UnsafeFeedUrl, CollectionConfigurationChanged ; conflits d'ingestion conservés |
| 422 | ResponseTooLarge, UnsupportedContentType, InvalidFeed, NoUsableFeedEntries |
| 502 | DnsResolutionFailed, UnsafeResolvedAddress, UpstreamTlsFailure, UpstreamHttpError, UnsafeRedirect, TooManyRedirects, UpstreamTransportError |
| 504 | UpstreamTimeout |
| 500 | CollectionInternalError ; PersistenceFailure d'ingestion conservé |

Les rejets avant tentative réseau ne créent aucune exécution. Une panne inattendue
pendant les lectures préalables renvoie aussi un 500 contrôlé, sans détails SQL.
Après démarrage de
la tentative, les échecs de transport/parser/contexte créent une SourceExecution
terminale `failed`, trigger `manual`, versions 1, contexte et destination initiaux,
StartedAt au début de la tentative, FinishedAt renseigné, tous compteurs à zéro,
ErrorMessage contrôlé et **aucun item**. Les GET execution/history/items restent
utilisables, avec HistoryAvailable=true et page vide.

Une annulation pendant récupération/parsing tente de conserver `cancelled` /
`CollectionCancelled` avec un token indépendant limité à 10 s, puis relance
OperationCanceledException. La finalisation d'échec utilise un scope DB neuf ; si
elle échoue (panne DB ou suppression des références initiales), le problème d'origine
reste prioritaire et executionId est null. Il n'y a pas de retry de finalisation.

Après remise du lot à IngestionService, ses statuts, code, itemIndex et executionId
sont conservés. Son succès/conflit possède sa propre exécution : l'orchestrateur
n'en ajoute aucune seconde. Les compteurs métier et la déduplication restent ceux
du moteur existant.

## Validation et limites

`RssAtomFeedParserTests` couvre formats, mapping, limites, identités, critères,
XML hostile et ordre/doublons. `RssFeedTransportSecurityTests` remplace DNS/sockets
ou handler et vérifie refus, rebinding, redirects, types, taille et timeout, sans
Internet. `RssCollectionTests` utilise PostgreSQL 18/Testcontainers et les vrais
services/API/parser avec transport déterministe ; il couvre aussi les gates de
concurrence/annulation, l'absence de connexion durant le réseau et un parcours HTTP
depuis une base vierge avec bootstrap et 4 pipelines/28 étapes.

Aucun fichier EF ni migration modifié : dernière migration
`20261006101923_Phase622PersistentSourceIdentities`, GetPendingMigrations vide et
HasPendingModelChanges=false vérifiés sur reconstruction. Le workflow CI complet
et l'artifact TRX `test-results` sont conservés.

Aucun Worker, BackgroundService, queue, planification, retry/backoff métier, n8n
opérationnel, scraping, source authentifiée, cache conditionnel, scoring, IA,
email, automatisation ou interface Blazor n'est ajouté. La normalisation et la
déduplication du moteur restent inchangées.
