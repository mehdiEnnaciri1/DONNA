# DONNA — Architecture logicielle

> Assistant Gemini résident pour Windows 11. Il tourne en arrière-plan et permet
> d'appeler Gemini **depuis n'importe quel champ de saisie de n'importe quelle
> application**, sans jamais changer de fenêtre.

---

## 1. Concept

DONNA capte ce que tu tapes (partout dans Windows), détecte une **formule de
déclenchement**, envoie le tout à l'API Gemini, puis **réécrit le texte en place**.

### La formule

```
<source>  donna  <prompt>␣␣
└─ texte ┘ └trig┘ └instruction┘ └ double espace = validation
```

Au **double espace**, DONNA :
1. envoie `<source>` + `<prompt>` à Gemini ;
2. **efface toute la formule** (source + `donna` + prompt + les 2 espaces) ;
3. **colle la réponse** à la place.

### Exemple concret (dans un mail Outlook)

Tu tapes :

```
Bonjour je voulais savoir si le devis est pret donna rends ça formel␣␣
```

~1 seconde plus tard, le champ contient directement :

```
Bonjour, je souhaiterais savoir si le devis est prêt.
```

### Cas particuliers

| Ce que tu tapes | Comportement |
|---|---|
| `texte donna instruction␣␣` | transforme `texte` selon `instruction` |
| `donna écris un haïku␣␣` | **prompt seul** → génération pure (pas de source) |
| `mon texte donna␣␣` | **source seule** → action par défaut sur le texte |
| `j'écoute madonna␣␣` | **ne déclenche pas** (`donna` collé à un mot) |
| `texte donna reformule␣` | **ne déclenche pas** (un seul espace) |

---

## 2. Principe de fonctionnement (le flux)

```
[Touche pressée n'importe où dans Windows]
        │
        ▼
  KeyboardHook  ── code brut de la touche (VK + scan code)
        │
        ▼
  KeyTranslator ── traduit en caractère Unicode (AZERTY + touches mortes)
        │
        ▼
  TypingBuffer  ── reconstruit le texte tapé + détecte la formule
        │  (formule complétée ?)
        ▼
  GeminiClient  ── envoie source + prompt à l'API Gemini
        │
        ▼
  ResponseCleaner ── enlève préambules / guillemets / markdown
        │
        ▼
  TextInjector  ── N Backspaces (efface la formule) puis Ctrl+V (colle la réponse)
        │
        ▼
  [Le champ affiche la réponse — le focus n'a jamais bougé]

Réinitialisation du buffer déclenchée par :
  MouseHook (clic) · ForegroundWatcher (changement de fenêtre) · Entrée / Échap / Tab / flèches
```

**Règle d'or du buffer :** DONNA ne connaît pas le contenu réel du champ. Elle
maintient sa *propre* reconstruction de ce qui a été tapé depuis le dernier reset.
Elle n'efface **jamais** plus que son propre buffer → le texte préexistant du champ
reste intact. Dans le moindre doute (clic, changement de fenêtre…), le buffer est vidé.

---

## 3. Stack technique

| Élément | Choix |
|---|---|
| Runtime | **.NET 10** (`net10.0-windows`), C# 13 |
| UI | **WinForms** |
| Publication | **single-file, self-contained** (un seul `.exe`, pas d'install .NET requise) |
| Sérialisation config | `System.Text.Json` |
| Chiffrement des clés API | **DPAPI** (`System.Security.Cryptography.ProtectedData`) |
| API IA | **Google Gemini** (REST via `HttpClient`) |
| Tests | **xUnit** |
| Installeur | **Inno Setup** (`.iss`) |
| Démarrage auto | clé de registre `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

---

## 4. Arborescence du projet

```
Donna/
├── Donna.csproj              net10.0-windows, WinForms, single-file self-contained
├── Program.cs                Entrée [STAThread], mutex mono-instance
├── DonnaContext.cs           ApplicationContext : câblage global + icône barre des tâches
├── Input/
│   ├── KeyboardHook.cs        WH_KEYBOARD_LL  — capture bas niveau du clavier
│   ├── MouseHook.cs           WH_MOUSE_LL     — reset du buffer au clic
│   ├── ForegroundWatcher.cs   SetWinEventHook / EVENT_SYSTEM_FOREGROUND — reset au changement de fenêtre
│   ├── KeyTranslator.cs       ToUnicodeEx     — VK → caractère (AZERTY + touches mortes)
│   └── TextInjector.cs        SendInput       — Backspaces + Ctrl+V (avec sauvegarde du presse-papiers)
├── Core/
│   └── TypingBuffer.cs        Buffer + machine à états du trigger      ← CŒUR TESTABLE (déjà écrit)
├── Ai/
│   ├── GeminiClient.cs        Appel REST Gemini
│   └── ResponseCleaner.cs     Nettoyage de la réponse                   ← testable
├── Ui/
│   ├── PillOverlay.cs         Pastille de statut flottante SANS vol de focus (WS_EX_NOACTIVATE)
│   └── SettingsForm.cs        Réglages à onglets : Clés API / Général / Avancé
├── Config/
│   ├── AppConfig.cs           Modèle de configuration (POCO)
│   ├── ConfigStore.cs         Lecture/écriture %APPDATA%\Donna\config.json
│   ├── KeyRing.cs             Trousseau multi-clés + rotation sur quota  ← testable
│   └── DpapiSecret.cs         Chiffre/déchiffre les clés via DPAPI
└── Autostart.cs               Active/désactive le démarrage automatique (HKCU\...\Run)

Donna.Tests/                   xUnit — TypingBuffer + ResponseCleaner + KeyRing
installer/donna.iss            Script Inno Setup
build.ps1                      dotnet publish → ISCC → Donna-Setup.exe
```

---

## 5. Rôle détaillé de chaque module

### Racine

- **`Program.cs`** — point d'entrée `[STAThread]`. Crée un **mutex nommé** pour empêcher
  deux instances de DONNA de tourner en même temps. Lance `DonnaContext`.
- **`DonnaContext.cs`** — hérite d'`ApplicationContext`. C'est le **chef d'orchestre** :
  instancie les hooks, le buffer, le client Gemini, l'injecteur, la pastille, l'icône de
  barre des tâches (avec menu : Réglages / Quitter), et branche les événements entre eux.
- **`Autostart.cs`** — bascule ON/OFF le démarrage à l'ouverture de session via la clé
  `HKCU\...\Run`.

### `Input/` — la couche Win32 (la plus délicate)

- **`KeyboardHook.cs`** — installe un hook bas niveau `WH_KEYBOARD_LL`. Reçoit chaque
  appui/relâchement de touche dans tout Windows.
- **`MouseHook.cs`** — `WH_MOUSE_LL`. Sert surtout à **réinitialiser le buffer au clic**
  (le curseur peut avoir changé de position).
- **`ForegroundWatcher.cs`** — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`. **Reset du
  buffer** quand la fenêtre active change.
- **`KeyTranslator.cs`** — convertit un code de touche (virtual key) en **caractère
  Unicode** avec `ToUnicodeEx`, en gérant la disposition **AZERTY**, les **touches mortes**
  (`^` + `e` → `ê`) et **AltGr** (`@ # € [ ]`). ⚠️ **Le point le plus difficile du projet.**
- **`TextInjector.cs`** — `SendInput`. Envoie N `Backspace` puis colle la réponse via
  Ctrl+V. Doit **sauvegarder et restaurer le presse-papiers** de l'utilisateur.

### `Core/` — la logique pure (déjà écrite, 100 % testable)

- **`TypingBuffer.cs`** — reconstruit le texte tapé et détecte la formule. Aucune
  dépendance Win32. Renvoie un `TriggerMatch(Source, Prompt, CharsToDelete)`.

### `Ai/`

- **`GeminiClient.cs`** — appel REST vers l'API Gemini via `HttpClient`. Construit une
  requête qui **sépare le texte source de l'instruction** et demande explicitement à
  Gemini de renvoyer **uniquement le résultat** (pas de préambule, pas de guillemets).
- **`ResponseCleaner.cs`** — filet de sécurité : enlève les préambules type « Voici… »,
  les guillemets encadrants, les blocs markdown. Testable.

### `Ui/`

- **`PillOverlay.cs`** — petite pastille flottante qui affiche l'état (⏳ envoi… / ✅ / ❌)
  **sans jamais prendre le focus** (fenêtre `WS_EX_NOACTIVATE` + `WS_EX_TOPMOST`).
- **`SettingsForm.cs`** — fenêtre de réglages à onglets :
  - **Clés API** : saisie des clés Gemini (stockées chiffrées via DPAPI).
  - **Général** : mot déclencheur, démarrage auto, modèle Gemini.
  - **Avancé** : délais d'injection, logs, disposition clavier.

### `Config/`

- **`AppConfig.cs`** — modèle des réglages (POCO sérialisable JSON).
- **`ConfigStore.cs`** — charge/sauvegarde `%APPDATA%\Donna\config.json`.
- **`KeyRing.cs`** — gère **plusieurs clés API** et **tourne** de l'une à l'autre quand
  une atteint son quota / renvoie une erreur de limite. Testable.
- **`DpapiSecret.cs`** — chiffre/déchiffre les clés avec DPAPI (liées à l'utilisateur
  Windows courant → illisibles ailleurs).

---

## 6. Spécification précise de la formule

- **Validation** = exactement **deux espaces consécutifs** en fin de saisie.
- **Déclencheur** = le mot `donna` (configurable, insensible à la casse), qui doit être un
  **mot entier** : un espace (ou un bord) de chaque côté. `madonna` ne déclenche pas.
- **Source** = tout ce qui précède `donna` dans le buffer (peut être vide).
- **Prompt** = tout ce qui suit `donna` jusqu'aux deux espaces (peut être vide).
- Il faut **au moins** une source **ou** un prompt : `donna␣␣` seul ne fait rien.
- **Ce qui est effacé** = tout le buffer (= source + `donna` + prompt + 2 espaces), **jamais**
  plus. Le texte préexistant du champ n'est jamais touché.
- **Reset du buffer** : clic souris, changement de fenêtre, Entrée, Échap, Tab, flèches,
  Origine/Fin. Dans le doute → reset.

---

## 7. Points durs / risques techniques

1. **Reconstruction du texte tapé** (`KeyTranslator`) — AZERTY + touches mortes + AltGr via
   `ToUnicodeEx`. C'est là que vivront la plupart des bugs. Isolé dans un seul fichier.
2. **Désynchronisation du buffer** — la reconstruction peut diverger du champ réel.
   Solution : reset agressif à chaque événement ambigu.
3. **Injection sans vol de focus** — la pastille et l'injection ne doivent pas déplacer le
   focus, sinon le texte part dans la mauvaise fenêtre. Gérer aussi le presse-papiers et le
   timing des `SendInput`.
4. **Prompt + nettoyage** — cadrer la requête Gemini pour un résultat brut, et nettoyer
   quand même par-dessus (`ResponseCleaner`).
5. **Antivirus / SmartScreen** — hook clavier global + `SendInput` + démarrage auto =
   comportement « type keylogger ». Prévoir la **signature de code** et une conception
   transparente (traitement local, envoi du seul texte déclenché, clés chiffrées, UI visible).

---

## 8. Configuration & sécurité

- Fichier : `%APPDATA%\Donna\config.json`.
- Clés API **jamais en clair** : chiffrées via DPAPI (`DpapiSecret`), liées au compte
  Windows courant.
- Traitement **100 % local** : seul le texte explicitement déclenché part vers Gemini.
- Rotation multi-clés (`KeyRing`) pour tenir sous les quotas.

---

## 9. Ordre de construction recommandé

L'idée : **dé-risquer d'abord tout ce qui est testable sans Windows**, puis attaquer le Win32.

1. **Cœur logique** — `TypingBuffer` ✅ (fait) · `ResponseCleaner` · `KeyRing` — + tests xUnit.
2. **`GeminiClient`** — appel REST isolé (⚠️ vérifier l'API Gemini actuelle en ligne : endpoint,
   modèles, auth — ça change souvent).
3. **Hooks + `KeyTranslator`** — la partie Win32 difficile.
4. **`TextInjector`** — Backspaces + collage + presse-papiers.
5. **Câblage** — `Program.cs` (mutex) + `DonnaContext.cs`.
6. **UI** — `PillOverlay` + `SettingsForm`.
7. **Config + Autostart + Installeur** — `AppConfig`/`ConfigStore`/`DpapiSecret`, `Autostart`,
   `installer/donna.iss`, `build.ps1`.

---

## 10. Tests (`Donna.Tests/`, xUnit)

Cible : toute la logique pure, sans Win32.

- `TypingBuffer` : exemple Outlook, source seule, prompt seul, frontière de mot (`madonna`),
  simple vs double espace, Backspace, Reset.
- `ResponseCleaner` : suppression des préambules, guillemets, markdown.
- `KeyRing` : rotation sur quota, sélection de la clé suivante, épuisement.

---

## 11. Build & distribution

- **`build.ps1`** : `dotnet publish` (single-file, self-contained, win-x64) → `ISCC` (Inno
  Setup) → produit **`Donna-Setup.exe`**.
- Prévoir la **signature du binaire et de l'installeur** pour limiter les avertissements
  SmartScreen/antivirus.
