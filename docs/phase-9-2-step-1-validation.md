# Phase 9.2 — Étape 1 : consolidation locale n8n

Vérification du 12 octobre 2026. Résultats locaux, soumis à audit utilisateur ;
ce rapport ne clôture pas toute la Phase 9.2.

## Référence et état initial

- Branche `main`, HEAD `a647d008f00921addf9e2d98cc9b079c4b15385d`.
- Working tree initial propre. Aucun commit ni push pendant l'intervention.
- Docker Engine disponible, version `29.6.1`.
- Conteneurs du projet PostgreSQL et n8n initialement arrêtés, sortie 0.
- Volumes réels : `prospectioncrm_postgres_data`, `prospectioncrm_n8n_data`.
- D'autres conteneurs PostgreSQL arrêtés et volumes préexistants sont présents :
  ils n'ont été ni démarrés, ni modifiés, ni supprimés.
- Image n8n locale et utilisée, avant/après : `2.40.7`, identifiant
  `sha256:ffeb52485f78b1b06c9a832205853cf75da72a07a514c9a27724df85979d6c34`.
- Volume n8n examiné readonly, sans réseau : configuration avec clé présente,
  SQLite avec WAL/SHM, 0 workflow, 0 credential, 0 exécution et 1 ligne utilisateur.
  Ce dernier compteur ne prouve ni la configuration du propriétaire ni une connexion UI.

## Décisions avant modification

La reprise et les seuils de rétention pouvaient être explicités : aucune exécution
historique ni workflow/credential à risque n'a été trouvé. Une sauvegarde à froid
a néanmoins précédé toute recréation du conteneur. Aucune clé n'a été remplacée.
L'image, les ports, le réseau et les volumes restent identiques.

Le choix `on-failure:3` respecte le caractère facultatif du service : reprise
bornée des sorties en erreur, pas de démarrage par le redémarrage du daemon.
Les seuils de pruning reprennent les défauts documentés (336 h et 10 000).
La réduction future de ces seuils exige un nouvel examen de l'historique.
Les appels de télémétrie, notifications de version et templates sont désactivés.

Le démarrage temporaire des services était nécessaire pour remplacer le constat
9.1 « fonctionnement non vérifié » par des observations de santé réelles.
PostgreSQL a seulement été démarré/arrêté, sans recréation ni commande SQL métier.
n8n a été recréé avec `up -d --no-deps --pull never n8n`, après sauvegarde.
Aucun workflow, API CRM ou fournisseur externe n'a été appelé.

## Modifications

- `compose.yml` : reprise bornée, rétention explicite, services distants désactivés.
- `docs/local-infrastructure.md` : résumé des politiques et lien d'exploitation.
- `docs/n8n-operations.md` : inspection sans secrets, sauvegarde cohérente,
  restauration isolée, limites réseau et retour à l'état initial.
- Ce rapport : preuves et limites de la validation.

## Vérifications réalisées

| Contrôle | Résultat |
| --- | --- |
| Compose sans profil et avec integrations, avant/après | Codes retour 0 |
| Sélection effective sans profil | PostgreSQL seulement |
| Image, volumes et ports | Version inchangée ; ports hôte uniquement 127.0.0.1 |
| Politique appliquée au conteneur n8n | on-failure, MaximumRetryCount=3 |
| Politique PostgreSQL | Inchangée : no |
| Clé injectée dans Compose | Aucune surcharge N8N_ENCRYPTION_KEY |
| PostgreSQL démarré | healthy via le healthcheck existant |
| n8n réel démarré | healthy ; readiness HTTP 200 depuis conteneur et hôte |
| n8n vierge sans réseau/port publié | Readiness HTTP 200 |
| Restauration d'une sauvegarde vierge non sensible | Fichiers, UID/GID, modes et clé identiques |
| SQLite restaurée | PRAGMA integrity_check = ok |
| n8n restauré sans réseau/port publié | Readiness HTTP 200 |
| Volume réel après arrêt | Clé identique à l'archive froide ; intégrité SQLite ok |
| Compteurs réels après arrêt | Identiques : 0 workflow/credential/exécution, 1 utilisateur |
| Retour à l'état initial des services | PostgreSQL et n8n arrêtés, sortie 0 |
| git diff --check | Réussi |

Le premier essai d'extraction dans un volume isolé a échoué sur les permissions
du répertoire `nodes`, avec l'utilisateur `node`. Le volume partiel a été conservé.
Un autre volume neuf a servi au second essai, avec auxiliaire root limité à
CHOWN/FOWNER/DAC_OVERRIDE pour l'extraction. Le service restauré utilise toujours
l'utilisateur normal de l'image. Cette exigence figure dans le guide.

Une assertion PowerShell initiale a appelé à tort Contains sur un PSCustomObject ;
elle a été corrigée et rejouée avec arrêt sur erreur. Les réglages de rétention,
les trois services distants désactivés et l'absence de surcharge de clé ont alors
été vérifiés avec succès.

## Artefacts locaux conservés, hors Git

- `backups/n8n-cold-20261012-150434.tar` : sauvegarde réelle à froid, 5 700 096 octets.
  SHA-256 : `985BF595295370B974BD54F1159ECEC45900575FC376C4A3B2A44854CCB01580`.
  Contient la clé : archive sensible, non chiffrée par tar, ignorée par Git.
- `backups/n8n-phase92-fixture.tar` : sauvegarde de l'installation vierge de test.
- Volumes de test : `prospectioncrm_phase92_fresh_20261012`,
  `prospectioncrm_phase92_restore_20261012` (essai partiel),
  `prospectioncrm_phase92_restore_retry_20261012` (restauration réussie).
- Conteneurs de test arrêtés : `prospectioncrm-phase92-fresh`,
  `prospectioncrm-phase92-restored`, sans réseau ni port publié.

Aucun volume supprimé ou réinitialisé. Les conteneurs auxiliaires ponctuels
utilisaient --rm ; seuls ces conteneurs auxiliaires ont été nettoyés automatiquement.
Aucune restauration de l'archive réelle n'a été effectuée.

## Limites, risques et opérations reportées

- L'authentification dans l'éditeur et le déchiffrement de credentials réels
  n'ont pas été testés : aucun credential réel n'existe dans cette instance.
- Les seuils sont vérifiés dans la configuration ; aucun historique n'a été
  créé ou purgé pour tester le passage réel des seuils.
- Aucun crash forcé ni redémarrage du daemon pour éprouver la politique restart.
- Restriction de publication vérifiée dans Compose et les bindings Docker ;
  aucun test de pénétration réseau externe ou durcissement de production.
- Réseau partagé avec PostgreSQL conservé ; pas de pare-feu sortant des workflows.
- Archive réelle sur le même disque : décider du stockage chiffré, de l'accès
  et de la conservation hors machine avant l'arrivée de credentials/données réels.
- Aucun test .NET : aucun fichier applicatif ou EF modifié. Aucun build, migration,
  appel CRM, test métier ou mise à jour d'image requis pour ces modifications.
- Les validations de restauration couvrent une installation vierge, pas une
  instance chargée ni une rotation de clé.

## État final et suite proposée

HEAD et branche inchangés ; deux fichiers suivis modifiés et deux nouveaux
documents, sans staging. Les sauvegardes restent ignorées par Git. Git signale
sa normalisation LF/CRLF habituelle, sans erreur de diff --check.

Le démarrage et la persistance locale de n8n sont maintenant vérifiés dans ce
périmètre. La Phase 9.2 complète reste ouverte : audit de ces changements,
vérification du propriétaire dans l'éditeur, politique de sauvegarde hors machine,
décision sur le réseau avant communication CRM et conservation/nettoyage autorisé
des seuls artefacts de test. Aucun commit ou push avant validation utilisateur.

## Audit final ciblé avant commit — 12 octobre 2026

Diff suivi et contenu intégral des deux nouveaux documents examinés ; cohérence
avec le README et le guide d'infrastructure vérifiée. Aucun correctif Compose
nécessaire. Le guide d'exploitation précise désormais les sorties 0, les arrêts
manuels, la portée des trois reprises, les exclusions/délais de purge et les ACL
Windows pour les archives. Les essais fonctionnels ci-dessus sont ceux de
l'implémentation ; ils n'ont pas été rejoués en démarrant les services.

La version du package dans l'image locale est bien 2.40.7. Son chargeur réel
`@n8n/config` / `GlobalConfig` a été exécuté dans un auxiliaire sans réseau,
filesystem readonly, sans volume métier et sans démarrer n8n. Valeurs résolues :

| Variable | Type et valeur vérifiés |
| --- | --- |
| N8N_DIAGNOSTICS_ENABLED | booléen false |
| N8N_VERSION_NOTIFICATIONS_ENABLED | booléen false |
| N8N_TEMPLATES_ENABLED | booléen false |
| EXECUTIONS_DATA_PRUNE | booléen true |
| EXECUTIONS_DATA_MAX_AGE | nombre 336 |
| EXECUTIONS_DATA_PRUNE_MAX_COUNT | nombre 10000 |

Les défauts et types sont confirmés dans `@n8n/config/dist/configs/` de l'image ;
les exclusions de purge dans `@n8n/db/dist/repositories/execution.repository.js`.
La politique restart relève de Docker, pas d'une option n8n ; ses limites sont
documentées dans le guide avec la référence officielle Docker.

La comparaison en mémoire des Compose effectifs (HEAD contre working tree)
confirme l'identité du service PostgreSQL, des réseaux et des volumes. Les
validations Compose avec/sans profil réussissent ; sans profil, seul PostgreSQL
est sélectionné. Aucun service existant, volume ou paramètre runtime n'est modifié
pendant cet audit. Les quatre conteneurs du projet et des essais restent arrêtés.

`git check-ignore` confirme l'exclusion des deux archives, de `.env`, de
`secrets.json` et des extensions de clés examinées. Aucun de ces fichiers n'est
suivi. L'index est vide ; seuls les quatre fichiers documentaires/configuration
énumérés plus haut constituent le changement local. Les réserves de l'étape
restent inchangées : aucun credential réel testé, sauvegarde hors machine à
organiser, aucune panne forcée ni purge réelle éprouvée.

Conclusion de l'audit ciblé : **PRÊT POUR COMMIT**, dans le périmètre 9.2.1.
Cette conclusion n'autorise aucun commit ou push et ne clôture pas la Phase 9.2.
