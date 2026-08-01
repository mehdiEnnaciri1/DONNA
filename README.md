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
| `donna écris un haïku␣␣` | prompt seul → génération pure (pas de source) |
| `mon texte donna␣␣` | source seule → corrige orthographe/grammaire par défaut |
| `j'écoute madonna␣␣` | ne déclenche pas (`donna` collé à un mot) |

### Texte collé ou déjà présent dans le champ

Si tu n'as rien tapé avant `donna` (par exemple : tu viens de coller du texte, puis tu
tapes directement `donna corrige␣␣`), DONNA lit le contenu réel du champ via
**UI Automation** (l'API d'accessibilité de Windows), sans injecter de touche, sans
toucher au presse-papiers, sans jamais créer de sélection. Voir
[Applications supportées](#applications-supportées) ci-dessous — cette lecture ne
fonctionne pas partout.

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

Si DONNA a corrigé un champ par lecture UI Automation (texte collé, voir ci-dessus) et
que le résultat ne convient pas, **clic droit sur l'icône → "Annuler la dernière
transformation"** restaure le texte d'origine dans ce même champ. Ne conserve qu'un seul
niveau d'annulation (pas d'historique), et uniquement pour ce chemin — la source tapée au
clavier n'a pas besoin de ce filet, puisque DONNA n'y efface jamais rien tant que l'appel
IA n'a pas réussi.

### Diagnostic

L'onglet **Avancé** des Réglages propose **"Activer les logs"** : écrit les erreurs dans
`%APPDATA%\Donna\logs\donna.log`. Désactivé par défaut (DONNA observe toute la frappe
système ; rien n'est journalisé sans activation explicite).

## Applications supportées

La lecture d'un champ sans source tapée (texte collé ou déjà présent) dépend de ce que
l'application expose via UI Automation — DONNA ne devine jamais, elle affiche un message
clair et n'agit pas si l'application n'est pas supportée.

| Application | Lecture (texte collé) | Remarque |
|---|---|---|
| Bloc-notes classique | ✅ | Testé : lecture et écriture confirmées |
| Champs de navigateur (Chrome, adresses, formulaires) | ✅ | Testé en lecture |
| **VS Code (éditeur de code)** | ❌ | Le contenu de l'éditeur Monaco n'est pas exposé via UI Automation (accessibilité activée seulement en mode lecteur d'écran) — limitation connue, pas de contournement prévu |
| Word, applications web type React (WhatsApp Web, Slack...) | ⚠️ | À vérifier au cas par cas — voir avertissement ci-dessous |

**Avertissement pour les applications web pilotées par JavaScript** (React et
équivalents) : certaines peuvent accepter une écriture automatique sans mettre à jour leur
propre état interne (le texte s'affiche mais l'application "ne le voit pas", et il peut
disparaître à l'envoi). DONNA relit systématiquement la valeur après écriture pour
détecter ce cas et afficher une erreur explicite plutôt que de laisser croire à un succès
— mais si l'application accepte l'écriture *sans que la relecture le détecte*, DONNA ne
peut pas s'en apercevoir. Vérifie toujours visuellement après usage sur une application
que tu n'as pas encore testée.

Dans tous les cas où la lecture ou l'écriture échoue, **rien n'est modifié dans le
champ** : DONNA abandonne proprement plutôt que de deviner.

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
  déplacement du curseur sera envoyé (le reste tombe dans le repli UI Automation, voir
  ci-dessus).
- Lecture sans source tapée : traite **tout le contenu actuel du champ** comme source, pas
  seulement une portion autour du curseur (UI Automation ne donne pas la position du
  curseur pour toutes les applications) — la réponse remplace l'intégralité du champ.
- VS Code (éditeur de code) n'est pas supporté pour la lecture sans source tapée — voir
  [Applications supportées](#applications-supportées).
- En cas d'échec de l'appel IA (quota, réseau...), DONNA n'efface ni ne modifie rien :
  la formule tapée reste visible (source tapée au clavier) ou le champ reste intact
  (lecture UI Automation).
- Icône temporaire simple (monogramme "D") — pas encore de charte graphique dédiée.
