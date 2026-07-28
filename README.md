# DONNA

Assistant IA résident pour Windows 11, propulsé par **Google Gemini**. DONNA tourne en
arrière-plan et permet d'appeler Gemini **depuis n'importe quel champ de saisie de
n'importe quelle application** (mail, éditeur, navigateur, bloc-notes...), sans jamais
changer de fenêtre ni voler le focus.

## Comment ça marche

Tape n'importe où dans Windows :

```
<texte>  donna  <instruction>␣␣
```

Au double espace final, DONNA envoie `<texte>` + `<instruction>` à Gemini, efface la
formule que tu viens de taper, et colle la réponse à la place — en ~1 seconde, sans
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

Le mot déclencheur (`donna` par défaut), le modèle Gemini et les autres réglages sont
configurables depuis la fenêtre **Réglages** (clic droit sur l'icône DONNA dans la barre
système).

Voir [ARCHITECTURE.md](ARCHITECTURE.md) pour le détail complet du fonctionnement interne
(flux de données, buffer de frappe, hooks Win32, etc.).

## Stack technique

| Élément | Choix |
|---|---|
| Runtime | .NET 10 (`net10.0-windows`), C# |
| UI | WinForms |
| Publication | single-file, self-contained (`.exe` unique, aucune install .NET requise) |
| API IA | Google Gemini (REST via `HttpClient`) |
| Clés API | chiffrées **DPAPI**, liées au compte Windows courant — jamais en clair sur disque |
| Tests | xUnit |
| Installeur | Inno Setup |

## Installation & configuration

1. Récupère une clé API Gemini sur [aistudio.google.com](https://aistudio.google.com).
2. Lance `Donna.exe` (ou l'installeur `Donna-Setup.exe`) — une icône apparaît dans la
   barre système.
3. Clic droit sur l'icône → **Réglages** → onglet **Clés API** → colle ta clé (une par
   ligne — DONNA bascule automatiquement sur la suivante si la première atteint son
   quota).
4. Valide. C'est prêt : tape `donna` n'importe où pour tester.

Le fichier de configuration (clés chiffrées, préférences) vit dans
`%APPDATA%\Donna\config.json`.

### Diagnostic

L'onglet **Avancé** des Réglages propose **"Activer les logs"** : écrit les erreurs dans
`%APPDATA%\Donna\logs\donna.log`. Désactivé par défaut (DONNA observe toute la frappe
système ; rien n'est journalisé sans activation explicite).

## Build depuis les sources

```powershell
dotnet build            # build de développement
dotnet test              # suite de tests xUnit (TypingBuffer, ResponseCleaner, KeyRing)
.\build.ps1              # publish self-contained + Inno Setup → dist\Donna-Setup.exe
```

## Structure du projet

```
Donna/            Application principale (WinForms)
├── Input/          Hooks clavier/souris bas niveau, traduction AZERTY, injection de texte
├── Core/            Logique pure testable (détection de la formule de déclenchement)
├── Ai/              Client Gemini + nettoyage de la réponse
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
  déclenchement, mais seul le texte explicitement soumis (avant `donna`, après `donna`)
  part vers l'API Gemini.
- Les clés API ne sont **jamais stockées en clair** : chiffrées via DPAPI, illisibles sur
  une autre machine ou par un autre utilisateur Windows.
- Aucune journalisation par défaut.

## Limitations connues

- Le buffer de frappe de DONNA se réinitialise sur clic, changement de fenêtre, ou
  touches de navigation (flèches, Origine/Fin, Entrée, Échap, Tab) — par sécurité, pour
  ne jamais effacer plus que ce que DONNA a elle-même vu taper. Si tu corriges une faute
  avec les flèches avant de taper `donna`, seul le texte tapé après le dernier
  déplacement du curseur sera envoyé.
- En cas d'échec de l'appel Gemini (quota, réseau...), DONNA efface la formule tapée
  (source + `donna` + instruction) sans rien coller à la place.
- Icône temporaire simple (monogramme "D") — pas encore de charte graphique dédiée.
