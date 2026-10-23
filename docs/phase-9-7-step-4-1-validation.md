# Phase 9.7.4.1 — Audit de résilience

23 octobre 2026. Référence `main` / `origin/main` :
`984e5756815456f79f8d78e1a8484e2e7d41a003`. Dépôt initial propre.

Livrable : [matrice de résilience et protocole préparatoire](integration-resilience-matrix-v1.md).
Dix incidents couverts avec preuve existante, garantie, lacune et risque résiduel.

## Examen effectué

Rapports email 9.5.4.2, calendrier 9.6.4.2, 9.7.1 à 9.7.3.3, politique de rétention,
runbook et guides n8n rapprochés des tests livraison email/calendrier, tests SQLite,
registre email, diagnostic et budget. Inspection statique ciblée des simulateurs
pour distinguer registre manquant, corrompu et ancien mais intègre.

Les checkpoints de crash CRM ne sont pas présentés comme des coupures électriques :
email reconstruit un état durable dans sa fixture, calendrier utilise BeginAsync
puis un nouveau service ; les tests SQLite terminent réellement un processus aux
checkpoints. Après interruption, started/dispatching peut rester persisté : unknown
n'est pas une transition automatique garantie au moment du crash.

Garanties réutilisées : refus de redispatch après admission, ACK distinct de preuve,
preuve tardive durable, replay idempotent si registre conservé, protection des preuves
et approbations, isolation workspace, récupération humaine limitée au calendrier,
admission transactionnelle minute/jour et absence de verrou pendant le réseau.

## Résultats historiques, non réexécutés

| Référence | Résultat acquis utilisé |
| --- | --- |
| 9.5.4.2 | JS 46/46 ; ciblés 93/93 ; régressions 389/389 ; chaîne locale simulée et redémarrage avant traitement |
| 9.6.4.2 | JS 137/137 ; 115 scénarios dédiés distincts après correction du harnais ; régressions 335/335 ; replays/redémarrages et atomicité SQLite |
| 9.7.1 / 9.7.2.1 | Ciblés 259/259 et 125/125, lecture seule, isolation, diagnostics |
| 9.7.2.2 | 30 nouveaux et 126 régressions validés sur plusieurs passes, audit et refus ; aucun faux total de passe unique |
| 9.7.3.1 | 129/129 dont 17 nouveaux : budget, concurrence et absence de mutation au refus |
| 9.7.3.2 / 9.7.3.3 | Inventaires et distinction déclaré/historique/observé, sans nouveaux tests |

Ces résultats proviennent des rapports et tests existants, ne sont ni des tests
exécutés pendant cet audit ni un total à additionner (recouvrements).
Aucune nouvelle exécution nécessaire pour les garanties déjà couvertes.

## Lacunes et proposition minimale

- **R1, critique sous perte du registre email simulé :** mkdir récursif/open wx
  peuvent recréer une clé absente et produire une nouvelle acceptation fictive.
  Le refus calendrier sur base manquante ne doit pas être attribué à l'email.
  Reproduction isolée proposée avec deux racines fictives, non exécutée ; aucun
  email réel. Durcissement minimal de racine : initialisation administrative et
  refus d'identité de registre absente ; insuffisant seul contre perte d'une clé.
- **R2, critique sous restauration désynchronisée :** paire ancienne valide peut
  perdre preuve/clé/tentative ou décaler curseur/révision ; FK et integrity_check
  ne suffisent pas. Barrière de remise en service par manifeste coordonné et
  comparaison du graphe proposée, actuellement non implémentée comme protection
  automatique CRM. Reproduction P0/N1 et P1/N0 préparée, non exécutée.
- Pas de faille fournisseur réel démontrée ni d'exactly-once distribué revendiqué.
  Les contradictions email et l'audit séparé des refus conservent leurs limites.

La matrice détaille les préconditions de remise en service et un protocole 9.7.4.2 :
PostgreSQL/volumes n8n neufs, fixtures entièrement fictives, réseau coupé, transports
espions sans effet, points cohérents/désynchronisés, contrôle hors ligne des workflows,
credentials de test et pruning, PASS/BLOCK sans activation. **9.7.4.2 non commencée.**
Les manques critiques sont documentés plutôt que comblés par un correctif non demandé.

## Validation et inventaire Git

Audit documentaire uniquement : aucun code/test ajouté ou modifié, aucun build,
test ciblé ou suite globale lancé. Liens internes et fichiers de tests cités vérifiés ;
`git diff --check` et contrôle des espaces des nouveaux fichiers réussis.

Aucun accès aux données Development, aucun service/volume manipulé, restauration,
reset, migration, purge, dispatch, retry, email ou invitation. Aucun secret, dump,
registre ou artefact privé ajouté. Preuves historiques intactes ; aucun staging,
commit ou push.

Exactement **2 fichiers nouveaux, 0 fichier suivi modifié ; index vide** :

- `docs/integration-resilience-matrix-v1.md`
- `docs/phase-9-7-step-4-1-validation.md`
