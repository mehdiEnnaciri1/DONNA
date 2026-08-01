# DONNA

Assistant IA résident pour Windows 11, propulsé par **Google Gemini** ou **Groq**. DONNA
tourne en arrière-plan et permet d'appeler l'IA **depuis n'importe quel champ de saisie de
n'importe quelle application** (mail, éditeur, navigateur, bloc-notes...), sans jamais
changer de fenêtre ni voler le focus.

## Comment ça marche

Tape n'importe où dans Windows :

```
<texte>  donna  <instruction>␣␣
```

Au double espace final, DONNA envoie `<texte>` + `<instruction>` à l'IA, efface la
formule que tu viens de taper, et injecte la réponse à la place — en ~1 seconde, sans
jamais changer la fenêtre active.

**Exemple** (dans un mail) :

```
Tapé :   Bonjour je voulais savoir si le devis est pret donna rends ça formel␣␣
Résultat : Bonjour, je souhaiterais savoir si le devis est prêt.
```

Autres cas :

| Ce que tu tapes | Comportement |
|---|---|
| `j'écoute madonna␣␣` | ne déclenche pas (`donna` collé à un mot) |

### Les trois modes

DONNA choisit automatiquement l'un de ces trois modes selon ce qui est tapé — jamais
d'erreur ni de blocage pour choisir, ce n'est qu'une question de savoir d'où vient la
source :

| Formule tapée | Mode | Comportement |
|---|---|---|
| `donna <instruction>␣␣` (rien avant) | **3 — Génération pure** | Aucune source : l'IA génère directement à partir de l'instruction. Fonctionne partout, aucune dépendance à UI Automation. |
| `<texte> donna <instruction>␣␣` | **1 — Source tapée** | Transforme `<texte>` selon `<instruction>`. Injection via frappe Unicode, fonctionne partout. |
| `<texte> donna␣␣` | **1 — Source tapée** | Action par défaut (correction) sur `<texte>` tapé. |
| `donna <instruction>␣␣` (texte collé/déjà présent, rien tapé avant) | **2 — Lecture UI Automation** | DONNA lit le contenu réel du champ (sans injecter de touche, sans presse-papiers, sans jamais créer de sélection), l'utilise comme source. Si la lecture échoue (application non supportée, curseur pas en fin de champ) → repli automatique sur le **mode 3**, jamais une erreur bloquante. |

Voir [Applications supportées](#applications-supportées) ci-dessous pour le mode 2 —
c'est le seul des trois qui dépend de l'application ciblée.

Le mot déclencheur (`donna` par défaut), le modèle et les autres réglages sont
configurables depuis la fenêtre **Réglages** (clic droit sur l'icône DONNA dans la barre
système).

Voir [ARCHITECTURE.md](ARCHITECTURE.md) pour le détail complet du fonctionnement interne
(flux de données, buffer de frappe, hooks Win32, UI Automation, etc.).

## Stack technique

| Élément | Choix |
|---|---|
| Runtime | .NET 10 (`net10.0-windows`), C# |
| UI | WinForms |
| Publication | single-file, self-contained (`.exe` unique, aucune install .NET requise) |
| API IA | Google Gemini et Groq (REST via `HttpClient`), détection automatique par clé |
| Lecture de champ sans source tapée | UI Automation (COM, `Interop.UIAutomationClient`) |
| Clés API | chiffrées **DPAPI**, liées au compte Windows courant — jamais en clair sur disque |
| Tests | xUnit |
| Installeur | Inno Setup |

## Installation & configuration

1. Récupère une clé API Gemini sur [aistudio.google.com](https://aistudio.google.com), ou
   une clé Groq sur [console.groq.com](https://console.groq.com).
2. Lance `Donna.exe` (ou l'installeur `Donna-Setup.exe`) — une icône apparaît dans la
   barre système.
3. Clic droit sur l'icône → **Réglages** → onglet **Clés API** → colle ta/tes clé(s) (une
   par ligne, Gemini et/ou Groq mélangées — le fournisseur est détecté automatiquement au
   format de la clé). La première est essayée en priorité ; DONNA bascule sur la suivante
   en cas d'échec (quota, clé invalide...).
4. Clique **Enregistrer**. C'est prêt : tape `donna` n'importe où pour tester.

Le fichier de configuration (clés chiffrées, préférences) vit dans
`%APPDATA%\Donna\config.json`.

### Annuler la dernière transformation

Si DONNA a corrigé un champ en mode 2 (lecture UI Automation, voir ci-dessus) et que le
résultat ne convient pas, **clic droit sur l'icône → "Annuler la dernière
transformation"** restaure le texte d'origine dans ce même champ — grisé quand il n'y a
rien à annuler. Passe par le même mécanisme d'écriture que la transformation elle-même
(voir ci-dessous), donc fonctionne aussi bien dans WhatsApp Web que dans le Bloc-notes.
Ne conserve qu'un seul niveau d'annulation (pas d'historique), et uniquement pour le mode
2 — le mode 1 (source tapée) n'a pas besoin de ce filet, puisque DONNA n'y efface jamais
rien tant que l'appel IA n'a pas réussi.

### Diagnostic

L'onglet **Avancé** des Réglages propose **"Activer les logs"** : écrit les erreurs dans
`%APPDATA%\Donna\logs\donna.log`. Désactivé par défaut (DONNA observe toute la frappe
système ; rien n'est journalisé sans activation explicite).

## Applications supportées

Les **modes 1 et 3** (source tapée / génération pure) fonctionnent **dans toutes les
applications** : ils reposent uniquement sur l'injection de frappe (`TextInjector`), pas
sur UI Automation.

Le **mode 2** (lecture du champ sans source tapée) dépend de ce que l'application expose
via UI Automation en LECTURE — l'écriture, elle, a un repli : si `ValuePattern.SetValue`
est refusé (WhatsApp Web, Word...), DONNA calcule le nombre exact de Backspace depuis le
texte réellement lu, les envoie, vérifie par relecture, puis injecte la réponse en frappe
Unicode — le même mécanisme que le mode 1, qui fonctionne partout où l'injection
fonctionne (voir ARCHITECTURE.md §7.6 pour le détail).

| Application | Mode 2 — lecture | Mode 2 — écriture | Remarque |
|---|---|---|---|
| Bloc-notes classique | ✅ | ✅ SetValue | Contrôle Edit standard, les deux patterns marchent nativement |
| WhatsApp Web, Slack, Gmail (champs `contenteditable`) | ✅ | ✅ repli clavier | `SetValue` refusé sur ces champs ; le repli clavier vérifié prend le relais |
| Word | ✅ | ✅ repli clavier | Document riche, pas un simple champ de saisie ; `SetValue` refusé, repli clavier |
| Champs de navigateur simples (adresse, formulaires) | ✅ | ✅ SetValue | — |
| **VS Code (éditeur de code)** | ❌ | ❌ | L'éditeur Monaco n'expose pas son contenu via UI Automation (accessibilité activée seulement en mode lecteur d'écran) — limitation connue, pas de contournement prévu. **Les modes 1 et 3 y fonctionnent normalement.** |

Quand le mode 2 échoue en lecture (application non supportée, ou curseur pas en fin de
champ), DONNA **ne bloque jamais** : elle retombe automatiquement sur le mode 3
(génération pure).

**Sauts de ligne dans les messageries** : une réponse multi-lignes est injectée avec
Maj+Entrée pour chaque saut de ligne, jamais Entrée seule — Entrée seule enverrait le
message dans WhatsApp/Slack/Teams avant la fin de l'injection.

**Avertissement pour les applications web pilotées par JavaScript** (React et
équivalents) : certaines peuvent accepter une écriture sans mettre à jour leur propre état
interne (le texte s'affiche mais l'application "ne le voit pas", et il peut disparaître à
l'envoi). DONNA relit systématiquement la valeur après chaque écriture (SetValue ou repli
clavier) pour détecter ce cas et afficher une erreur explicite plutôt que de laisser
croire à un succès — mais si l'application accepte l'écriture *sans que la relecture le
détecte*, DONNA ne peut pas s'en apercevoir. Vérifie toujours que le résultat est bien pris
en compte par l'application (pas seulement affiché) sur une application que tu n'as pas
encore testée.

Dans tous les cas où la lecture ou l'écriture échoue définitivement, **rien n'est modifié
dans le champ** (ou le texte d'origine est restauré si l'échec survient en cours
d'écriture) : DONNA abandonne proprement plutôt que de deviner ou de laisser un état
intermédiaire.

## Build depuis les sources

```powershell
dotnet build            # build de développement
dotnet test              # suite de tests xUnit (TypingBuffer, ResponseCleaner, KeyRing...)
.\build.ps1              # publish self-contained + Inno Setup → dist\Donna-Setup.exe
```

## Structure du projet

```
Donna/            Application principale (WinForms)
├── Input/          Hooks clavier/souris bas niveau, traduction AZERTY, injection de texte,
│                   lecture/écriture de champ via UI Automation
├── Core/            Logique pure testable (détection de la formule de déclenchement)
├── Ai/              Clients Gemini et Groq + nettoyage de la réponse
├── Ui/              Pastille de statut, fenêtre de Réglages
├── Config/          Configuration, chiffrement des clés, rotation multi-clés
└── Assets/          Icône de l'application

Donna.Tests/       Tests xUnit
installer/         Script Inno Setup
build.ps1          Script de publication (dotnet publish → installeur)
```

Détail module par module : voir [ARCHITECTURE.md](ARCHITECTURE.md).

## Sécurité & vie privée

- Traitement **100 % local** : DONNA observe la frappe pour détecter la formule de
  déclenchement, mais seul le texte explicitement soumis (avant `donna`, après `donna`,
  ou lu dans le champ si rien n'a été tapé) part vers l'API du fournisseur.
- Les clés API ne sont **jamais stockées en clair** : chiffrées via DPAPI, illisibles sur
  une autre machine ou par un autre utilisateur Windows.
- Aucune journalisation par défaut.

## Limitations connues

- Le buffer de frappe de DONNA se réinitialise sur clic, changement de fenêtre, ou
  touches de navigation (flèches, Origine/Fin, Entrée, Échap, Tab) — par sécurité, pour
  ne jamais effacer plus que ce que DONNA a elle-même vu taper. Si tu corriges une faute
  avec les flèches avant de taper `donna`, seul le texte tapé après le dernier
  déplacement du curseur sera considéré comme "tapé" (mode 1) ; le mode 2 exige que le
  curseur soit resté en fin de champ depuis la fin du texte à lire, sinon il retombe sur
  le mode 3.
- Mode 2 : traite **tout le contenu actuel du champ** comme source, pas seulement une
  portion autour du curseur (UI Automation ne donne pas la position du curseur pour
  toutes les applications) — la réponse remplace l'intégralité du champ.
- VS Code (éditeur de code) n'est pas supporté pour le mode 2 — voir
  [Applications supportées](#applications-supportées). Les modes 1 et 3 y fonctionnent normalement.
- En cas d'échec (appel IA, lecture, ou écriture même après le repli clavier), DONNA
  n'efface ni ne modifie rien de définitif : la formule tapée reste visible (mode 1/3) ou
  le texte d'origine est restauré (mode 2, voir ARCHITECTURE.md §7.6).
- Icône temporaire simple (monogramme "D") — pas encore de charte graphique dédiée.
