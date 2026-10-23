# Résilience des intégrations — matrice v1

Audit du 23 octobre 2026 sur `984e5756815456f79f8d78e1a8484e2e7d41a003`.
Les garanties ci-dessous sont celles des preuves historiques et du code examiné,
pas de nouveaux incidents exécutés. Aucun exactly-once fournisseur distribué,
aucune résistance à une panne électrique ou restauration coordonnée n'est certifié.

## Sources et portée

- E : [email 9.5.4.2](phase-9-5-step-4-2-validation.md),
  [tests livraison email](../tests/ProspectionCrm.Api.Tests/EmailSendDeliveryTests.cs),
  [registre email testé](integrations/email-send-demo/CrmEmailSend.test.js).
- C : [calendrier 9.6.4.2](phase-9-6-step-4-2-validation.md),
  [tests livraison calendrier](../tests/ProspectionCrm.Api.Tests/CalendarEventDeliveryTests.cs),
  [concurrence calendrier](../tests/ProspectionCrm.Api.Tests/CalendarEventDeliveryConcurrencyTests.cs),
  [tests SQLite](integrations/calendar-event-demo/CalendarEventStore.test.js).
- S/D : [supervision 9.7.1](phase-9-7-step-1-validation.md),
  [diagnostic 9.7.2.1](phase-9-7-step-2-1-validation.md).
- H : [récupération 9.7.2.2](phase-9-7-step-2-2-validation.md).
- B : [budget 9.7.3.1](phase-9-7-step-3-1-validation.md),
  [tests budget](../tests/ProspectionCrm.Api.Tests/IntegrationDispatchBudgetTests.cs).
- R/O : [politique](integration-retention-policy-v1.md),
  [contrôle 9.7.3.2](phase-9-7-step-3-2-validation.md),
  [runbook](integration-operations-runbook-v1.md),
  [contrôle 9.7.3.3](phase-9-7-step-3-3-validation.md).
- Sauvegardes : [exploitation n8n](n8n-operations.md),
  [sauvegardes chiffrées](n8n-encrypted-backups.md).

Les tests CRM utilisent PostgreSQL jetable et faux transports. E/C ont aussi
validé une chaîne HTTPS/HMAC locale avec n8n et fournisseurs simulés. Cela ne
qualifie pas Gmail, Microsoft, un vrai fournisseur calendrier ou ses invitations.

## Matrice des incidents

| Incident | Preuve existante | Garantie démontrée | Lacune / risque résiduel |
| --- | --- | --- | --- |
| Crash avant départ réseau | E `PersistedCrashBeforeOrAfterProviderCallRecoversUnknownWithoutResending(false)` ; C `CrashCheckpointSurvivesNewServiceAndProviderLookupDoesNotRepeatEffect(false)` ; B refus sans mutation | Avant commit, pas de tentative durable si transaction annulée ; après commit d'admission, tentative started et intention dispatching persistantes, redispatch refusé. Réconciliation humaine historique peut constater unknown, sans envoi. | E reconstruit un checkpoint durable ; C appelle BeginAsync puis recrée le service. Pas un kill du processus API à chaque instruction. Un crash ne convertit pas immédiatement started en unknown : l'état reste ambigu jusqu'au diagnostic/traitement explicite. |
| Crash après départ, avant ACK/finalisation | E/C mêmes tests, variante true ; tests timeout/disconnect ; C SQLite `process crash …` avant/après commit | Pas de seconde tentative automatique ; ACK seul ne prouve pas le succès. L'effet fictif éventuellement appliqué reste recherchable par sa clé. SQLite annule avant commit ou conserve effet + ledger + preuve après commit. | Atomicité locale SQLite, pas transaction distribuée PostgreSQL/fournisseur. Le simulateur email fichier n'a pas la preuve de crash interprocessus aux mêmes checkpoints. Coupure physique/disque non testée. |
| Callback perdu ou tardif | E parcours C et `AmbiguousDispatchNeverRetriesAndLateEvidenceReconciles` ; C `UnknownNeverRetriesAndLateProofCanFinish`, `AmbiguousEvidenceThenLateProofIsDurable` | Unknown conservé, aucun succès fabriqué ; preuve tardive persistée puis rapprochement explicite sans nouvel effet fournisseur. Reçu invalide conservé. | Sans preuve retrouvable, résultat indéterminé durable. Aucun délai ne prouve le non-effet ; récupération automatique non fournie. |
| Redémarrage entre réception et traitement | E parcours C : reçu durable, arrêt/redémarrage API avant reconcile ; C checkpoints CREATE/UPDATE 9.6.4.2 ; H rollback sur panne d'audit | Réception séparée de l'effet métier ; contexte/preuve conservés après redémarrage. Traitement calendrier accepté et audit atomiques ; panne annule l'ensemble. | Pas un exercice de restauration d'un backup antérieur. Audit des refus séparé, potentiellement absent en cas de panne. |
| Replay après redémarrage | E test JS `durable one acceptance and identical proof across restart…` ; C `atomic create…new process`, replay signé 9.6.4.2 | Même clé + commande retrouve la preuve existante ; même MessageId/contenu ne crée pas de second reçu/effet ; contenu différent refusé. Registres conservés indispensables. | Garanties perdues si fichier/clé manque ou si backup ancien accepté. Aucun tombstone inter-systèmes ni horizon universel de replay. |
| Preuve contradictoire | C `ContradictoryProofIsRetainedAndDiagnosableBeforeOrAfterSuccess` ; E `ProviderIdentityConflictRollsBackAllProcessingAndPreservesReceipt` ; H refus avec conservation | Conflit calendrier durable et preuves protégées ; récupération contrôlée refuse contradiction/conflit sans acquittement. Le diagnostic ne qualifie pas une preuve non évaluée sur métadonnées seules. | Ne pas généraliser à toute contradiction email : certaines réconciliations historiques après sent acquittent des résultats sans toutes les garanties H. Email/sync/collecte exclus du recover contrôlé. |
| n8n indisponible | E tests HTTP 4xx/5xx/TLS/timeout ; C timeout/disconnect/http-error, transports testés en 9.6.4.2 ; S n8nHealth=not-assessed | Erreur réseau ambiguë conservée unknown, pas de retry ajouté ; service explicitement désactivé refuse avant départ. Aucune santé n8n déduite de l'absence de résultat. | Pas de preuve d'une panne réseau réelle prolongée, de haute disponibilité ou de délai de rétablissement. Les politiques Docker/workflows ne garantissent pas l'idempotence. |
| Registre d'idempotence manquant/corrompu | C `missing, truncated and corrupt registries fail closed without reset` ; lecture de CrmEmailSend.node.js | Calendrier refuse une base absente/corrompue, ne la reconstruit pas depuis la commande. Email refuse un fichier présent incomplet ou une commande différente sous la même clé. | **Critique conditionnelle R1 :** email recrée le dossier et un fichier absent ; perte de clé indistinguable d'un premier envoi simulé. Une base calendrier ancienne mais intègre n'est pas détectée comme désynchronisée (R2). |
| Budget saturé | B `RefusalPreservesPendingApprovalAndAllowsLaterHumanStart`, `TwoApiHostsWithIndependentGatesAdmitExactlyOneStart`, `EmailAndCalendarShareBudgetWithoutMutatingRefusedIntent`, bornes UTC et unknown | Contrôle + tentative sous verrou PostgreSQL commun, READ COMMITTED ; compte par workspace/connexion sur les trois types. Refus avant tentative/réseau, intention et approbation inchangées ; verrou relâché avant réseau. | Protection CRM si activée uniformément et historique présent. Ne garantit pas les quotas fournisseur ; restauration perdant des tentatives peut sous-compter (R2). Pas de qualification de charge massive. |
| Restauration CRM/n8n désynchronisée | R/O identifient le risque ; 9.2 valide capture/restauration n8n, pas le couple CRM/ledger | Sauvegardes et copies cohérentes locales testées historiquement ; aucune garantie de reprise métier du couple n'est déduite de leur intégrité. | **R2 : non démontré.** CRM ancien peut perdre preuve, approbation ou tentative ; registre ancien peut perdre effet/clé/révision ; curseurs et workflows peuvent reprendre au mauvais point. Remise en service à bloquer. |

## Garanties transversales et réserves

S/D couvrent isolation workspace, 404 pour identifiant étranger, pagination bornée,
projections sans contenu sensible et transactions READ ONLY avec empreintes avant/après.
E/C couvrent authentification des callbacks, liaison opération/connexion/compte,
approbation figée et absence de succès fondé sur le seul ACK. H couvre cookie humain,
antiforgery, révision et ensemble exact des reçus, concurrence et audit atomique des
actions acceptées. H ne couvre que CREATE/UPDATE calendrier avec preuve persistée
non traitée et préconditions compatibles ; aucun recover email/sync/collecte ajouté.

L'absence de répétition automatique démontrée concerne les départs email/calendrier
et leurs traitements contrôlés. Elle n'est pas une certification de tous les jobs,
workflows arbitraires ou comportements de fournisseurs. Les workflows restaurés
peuvent contenir des déclencheurs actifs : réseau coupé et workflows inactifs sont
deux protections distinctes. Les clés d'idempotence et reçus traités restent protégés.

## Lacunes critiques conditionnelles et correctifs minimaux proposés

### R1 — Perte du registre email simulé

Constat statique : `CrmEmailSend.node.js` appelle mkdir récursif puis open `wx`.
Un fichier absent est traité comme première commande, sans preuve indépendante
qu'aucune acceptation n'a déjà eu lieu. Ce risque est déjà annoncé par la politique
de rétention ; ce n'est pas un envoi réel constaté pendant cet audit.

Reproduction **à préparer uniquement dans un futur test fictif**, non exécutée ici :
construire une commande example.invalid ; représenter son acceptation dans une
fixture A avec preuve P ; utiliser une seconde racine neuve B sans son fichier,
sans toucher A ; la lecture du code prédit qu'un appel local du profil avec la même
commande produit une nouvelle preuve/identité fictive. Aucun callback, réseau ou
email réel ; ce test d'effet local nécessiterait un périmètre distinct du protocole
9.7.4.2 ci-dessous, qui interdit le dispatch. La seule comparaison A/B peut déjà
démontrer la clé perdue sans réexécuter la commande.

Plus petit durcissement applicatif proposé pour la **racine absente** : initialisation
administrative explicite d'un registre identifié, refus si cette racine/identité manque,
sans création implicite depuis result(). Cela ne suffit pas pour un fichier isolé
perdu ou un backup ancien. Dans ces cas, la barrière de remise en service R2 est
indispensable ; ne pas annoncer une garantie complète sur la base d'un simple marqueur.
Aucun correctif implémenté dans cet audit.

### R2 — Paire ancienne ou désynchronisée pourtant structurellement valide

Reproduction isolée proposée : fixtures cohérentes P0/N0 et P1/N1, P1/N1 contenant
un effet/clé/preuve/tentative supplémentaires ; comparer P0/N1 puis P1/N0 en lecture
seule. Les FK et integrity_check peuvent tous réussir alors que le graphe temporel
ne correspond plus. P0 peut aussi montrer un budget libre artificiel. Aucun replay
nécessaire pour constater ces divergences.

Correctif minimal de remise en service proposé : une barrière opérateur **bloquante**
avant démarrage des composants actifs, fondée sur un manifeste commun de capture,
identité du registre et inventaire des relations, puis validation humaine. Refuser
manifeste absent, paire différente ou ambiguïté ; conserver transports/workers et
workflows désactivés. Cette barrière n'est pas aujourd'hui une fonctionnalité CRM
automatique : un opérateur qui la contourne n'est pas protégé par les seules FK.
Un manifeste doit être protégé et validé, pas seulement recopier des dates identiques.

## Préconditions avant toute remise en service future

1. Même jeu de capture identifié et bornes de capture documentées après arrêt des
   producteurs ; aucune écriture en vol non prise en compte. Versions de schéma,
   images, nœuds personnalisés et configuration compatibles.
2. PostgreSQL complet : opérations/tentatives/reçus, plans/approbations, audits,
   liens métier, comptes, calendriers, curseurs et révisions. Contrôler FK et triggers,
   mais aussi références logiques sans FK ; aucune reconstruction depuis le seul statut.
3. n8n complet : base de workflows/exécutions et registres JSON/SQLite, preuves,
   ledger/effets, identités externes et révisions. Captures cohérentes avec journaux
   SQLite nécessaires ; un export workflow n'est pas une sauvegarde de ces registres.
4. Cohérence des clés de commande/idempotence/corrélation, événements et messages
   importés, curseurs de synchronisation : pas de saut, reset ou avance supposé sûr.
   Une réponse tardive doit pouvoir retrouver son opération et son contexte exact.
5. Clé de chiffrement n8n et credentials compatibles avec la capture, secrets stockés
   séparément et accessibles à l'opérateur habilité ; aucune réinitialisation de clé.
   HMAC entrants/sortants, scopes et éventuelle rotation à contrôler sans exposer les
   valeurs. La signature d'une preuve n'établit pas sa validité métier.
6. Workflows et triggers inactifs, transports et workers désactivés, aucune tâche de
   reprise automatique ; activation persistée dans une sauvegarde ne doit pas être
   honorée avant contrôle. Politique de rétention/pruning à examiner avant démarrage
   pour ne pas perdre des preuves anciennes restaurées.
7. Historique du budget conservé pour ses fenêtres UTC ; pas de remise à zéro par
   restauration. Si sa complétude n'est pas démontrée, maintenir le blocage.
8. Tous les écarts/unknown/conflits conservés et recensés ; aucune réussite ni
   approbation fabriquée. Décision de remise en service explicite et traçable.

## Protocole préparatoire 9.7.4.2 — non exécuté, non commencé

Le futur exercice n'est pas une restauration Development. Il devra être autorisé
séparément et rester sans dispatch, réseau externe, email ou invitation.

### Isolation préalable

- Créer des ressources portant un identifiant de test neuf : PostgreSQL temporaire
  et volumes n8n temporaires. Vérifier absence des noms, chemins résolus et absence
  de tout montage des volumes, sauvegardes ou secrets existants. Pas de reset d'une
  ressource en collision ; conserver les copies en échec pour diagnostic.
- Utiliser uniquement des images déjà disponibles et des données 100 % fictives,
  deux workspaces, identités example.invalid et secrets de test sans accès extérieur.
  Aucun dump Development ou export réel, même « anonymisé », n'est requis.
- Réseau des auxiliaires/conteneurs désactivé, aucun port publié, aucune route vers
  host.docker.internal ou Internet. PostgreSQL consulté par socket interne via exec ;
  analyse n8n hors ligne avant tout démarrage. Ne pas assouplir le réseau pour faire
  fonctionner un test. Les contrôles applicatifs éventuels restent en processus
  avec transport espion qui lève avant tout effet ; compteur d'appels attendu zéro.

### Captures fictives et variantes

Préparer des fixtures déterministes sur ces seules ressources temporaires :
P0/N0 avant effet fictif ; P1/N1 avec clé/effet/preuve/reçu enregistré mais non traité ;
P2/N2 avec état traité et audit, plus unknown, conflit, preuve tardive en attente,
curseurs et tentatives proches des bornes UTC. Les états sont construits par fixtures,
sans exécuter de workflow ni dispatcher. Initialiser un schéma de test compatible
uniquement dans PostgreSQL temporaire, jamais migrer Development.

Capturer après gel des writers : manifeste commun, versions, comptes et empreintes
des graphes (aucun secret dans les sorties). Restaurer chaque variante dans une
destination **neuve** ; ne pas écraser les fixtures sources :

| Variante | Contrôle attendu avant toute activité |
| --- | --- |
| P1/N1 cohérent, puis seconde ouverture | Identités, preuves, reçus, approbations, curseurs, audit et compteurs identiques ; lecture répétée sans mutation |
| P0/N1, P1/N0 | Divergence détectée, remise en service refusée, aucun replay pour « réparer » |
| Registre absent, tronqué, ou ancien mais intègre | Absence/corruption ou incohérence de manifeste bloquante ; pas d'initialisation implicite |
| Clé fictive incompatible, configuration de connexion/workspace divergente | Validation refusée sans affichage des valeurs ni fallback |
| Workflow fictif sauvegardé actif ou avec reprise en attente | Inspection hors ligne détecte le risque ; aucun démarrage de cet exemplaire avant procédure isolée de désactivation validée |
| Historique du budget tronqué, curseur/révision fournisseur décalé | Blocage même si schéma et SQLite sont intègres |

### Critères de reprise fail-closed de l'exercice

Le harnais futur doit produire une décision PASS/BLOCK et ses motifs sur métadonnées.
En l'absence de barrière automatisée existante, **BLOCK est une condition opérateur
à faire respecter par le harnais**, pas une capacité du produit déjà prouvée.
Pour toutes les variantes, zéro transport/callback/dispatch et aucune modification
des sources fictives après capture ; ne pas traiter ou acquitter une preuve pour
rendre les comptes concordants. Ne pas exécuter result()/execute() des simulateurs
comme simple contrôle de reprise : ces méthodes peuvent écrire un effet.

Les replays d'effets déjà couverts par E/C ne sont pas à répéter. Vérifier en lecture
seule la présence exacte des clés/preuves après restauration et seconde ouverture.
Si un nouveau test critique est nécessaire, limiter son ajout à la barrière de
cohérence/restauration ; toute exécution d'effet local supplémentaire demande de
revoir explicitement ce périmètre sans réseau ni fournisseur.

Un éventuel démarrage n8n temporaire n'est envisageable qu'après contrôle des états
actifs/reprises et du pruning sur la seule copie fictive, toujours network none et
sans ports. Sinon conserver la validation hors ligne et marquer le démarrage non
vérifié. PASS ne déclenche aucune activation de production. Conserver résultats,
empreintes et ressources de test jusqu'à revue ; aucun nettoyage global.
