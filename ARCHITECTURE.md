# DONNA — Architecture logicielle

> Assistant IA résident pour Windows 11 (Gemini ou Groq). Il tourne en arrière-plan et
> permet d'appeler l'IA **depuis n'importe quel champ de saisie de n'importe quelle
> application**, sans jamais changer de fenêtre.

---

## 1. Concept

DONNA capte ce que tu tapes (partout dans Windows), détecte une **formule de
déclenchement**, envoie le tout à l'IA, puis **réécrit le texte en place**.

### La formule

```
<source>  donna  <prompt>␣␣
└─ texte ┘ └trig┘ └instruction┘ └ double espace = validation
```

Au **double espace**, DONNA :
1. envoie `<source>` + `<prompt>` à l'IA (Gemini ou Groq, selon la clé) ;
2. **efface la formule tapée** (source + `donna` + prompt + les 2 espaces) ;
3. **injecte la réponse** à la place (frappe Unicode via `SendInput`, jamais de collage).

### Si rien n'est tapé avant `donna` (texte collé ou déjà présent)

`TypingBuffer` ne voit que ce qui est **tapé au clavier** : ni le texte collé (Ctrl+V
réinitialise le buffer par prudence), ni le texte déjà présent dans le champ avant que
DONNA démarre. Si `donna instruction␣␣` est tapé sans rien devant, DONNA lit le contenu
réel du champ via **UI Automation** (voir §2 et §7.6) plutôt que d'appeler l'IA avec une
source vide.

Historique : une première version lisait ce cas par **sélection clavier**
(Maj/Ctrl+Origine puis Ctrl+C) — abandonnée après avoir détruit des documents entiers en
conditions réelles (voir §7.6 pour le post-mortem complet).

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
| `donna instruction␣␣` (rien tapé avant) | lecture du champ via UI Automation |

---

## 2. Principe de fonctionnement (le flux)

### Chemin normal — source tapée au clavier

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
        │  (formule complétée, source non vide)
        ▼
  GeminiClient / GroqClient ── envoie source + prompt à l'IA (fournisseur détecté par clé)
        │
        ▼
  ResponseCleaner ── enlève préambules / guillemets / markdown
        │
        ▼
  TextInjector  ── N Backspaces (efface la formule) puis frappe Unicode (injecte la réponse)
        │
        ▼
  [Le champ affiche la réponse — le focus n'a jamais bougé]
```

### Chemin de repli — rien tapé avant `donna`

```
  TypingBuffer détecte la formule, Source == ""
        │
        ▼
  UiaFieldAccessor.TryReadFocusedField() ── lit l'élément focalisé via ValuePattern
        │                                    (repli TextPattern), sur un thread MTA
        │                                    (Task.Run — voir §7.6)
        │  (non supporté ? → erreur claire, rien n'est modifié, on s'arrête ici)
        ▼
  Source = texte lu, tronqué de la longueur exacte de la formule tapée
  (découpage de chaîne — aucune touche envoyée pour "effacer" quoi que ce soit)
        │
        ▼
  GeminiClient / GroqClient ── même appel que le chemin normal
        │
        ▼
  ResponseCleaner
        │
        ▼
  UiaFieldAccessor.TryWrite() ── ValuePattern.SetValue, PUIS relecture pour vérifier
        │                         (détecte les apps JS qui acceptent l'écriture sans
        │                         mettre à jour leur état interne — voir §7.6)
        │  (échec de vérification ? → erreur claire, le champ n'a pas été modifié
        │   par DONNA au-delà du SetValue lui-même — voir §7.6 sur la nuance d'atomicité)
        ▼
  Mémorise (élément, texte d'origine) pour "Annuler" (menu du tray)
```

Réinitialisation du buffer déclenchée par :
  MouseHook (clic) · ForegroundWatcher (changement de fenêtre) · Entrée / Échap / Tab / flèches

**Règle d'or du buffer :** DONNA ne connaît pas le contenu réel du champ tant qu'elle n'a
pas besoin de le lire. Elle maintient sa *propre* reconstruction de ce qui a été tapé
depuis le dernier reset. Elle n'efface **jamais** plus que son propre buffer (chemin
normal) ni ne modifie un champ avant d'être sûre de pouvoir aboutir (chemin UI
Automation) → le texte préexistant du champ reste intact en cas d'échec, dans les deux cas.

---

## 3. Stack technique

| Élément | Choix |
|---|---|
| Runtime | **.NET 10** (`net10.0-windows`), C# 13 |
| UI | **WinForms** |
| Publication | **single-file, self-contained** (un seul `.exe`, pas d'install .NET requise) |
| Sérialisation config | `System.Text.Json` |
| Chiffrement des clés API | **DPAPI** (`System.Security.Cryptography.ProtectedData`) |
| API IA | **Google Gemini** et **Groq** (REST via `HttpClient`), fournisseur détecté par préfixe de clé |
| Lecture/écriture de champ sans source tapée | **UI Automation** via COM (`Interop.UIAutomationClient` — pas `System.Windows.Automation`/WPF, voir §7.6) |
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
│   ├── NativeInput.cs         Fabrique SendInput partagée (structures Win32, Backspace + Unicode)
│   ├── TextInjector.cs        SendInput       — Backspaces + frappe Unicode (jamais de presse-papiers)
│   └── UiaFieldAccessor.cs    UI Automation (COM) — lecture/écriture de champ sans source tapée
├── Core/
│   └── TypingBuffer.cs        Buffer + machine à états du trigger      ← CŒUR TESTABLE
├── Ai/
│   ├── AiProvider.cs           Détection Gemini/Groq par préfixe de clé
│   ├── AiClientExceptions.cs   Exceptions partagées (quota, erreur API)
│   ├── GeminiClient.cs         Appel REST Gemini (generateContent)
│   ├── GroqClient.cs           Appel REST Groq (chat/completions, compatible OpenAI)
│   └── ResponseCleaner.cs      Nettoyage de la réponse                   ← testable
├── Ui/
│   ├── PillOverlay.cs         Pastille de statut flottante SANS vol de focus (WS_EX_NOACTIVATE)
│   └── SettingsForm.cs        Réglages à onglets : Clés API / Général / Avancé
├── Config/
│   ├── AppConfig.cs           Modèle de configuration (POCO)
│   ├── ConfigStore.cs         Lecture/écriture %APPDATA%\Donna\config.json
│   ├── KeyRing.cs             Trousseau multi-clés + rotation sur échec  ← testable
│   └── DpapiSecret.cs         Chiffre/déchiffre les clés via DPAPI
└── Autostart.cs               Active/désactive le démarrage automatique (HKCU\...\Run)

Donna.Tests/                   xUnit — TypingBuffer, ResponseCleaner, KeyRing, AiProvider, KeyEvent...
installer/donna.iss            Script Inno Setup
build.ps1                      dotnet publish → ISCC → Donna-Setup.exe
```

---

## 5. Rôle détaillé de chaque module

### Racine

- **`Program.cs`** — point d'entrée `[STAThread]`. Crée un **mutex nommé** pour empêcher
  deux instances de DONNA de tourner en même temps. Lance `DonnaContext`.
- **`DonnaContext.cs`** — hérite d'`ApplicationContext`. C'est le **chef d'orchestre** :
  instancie les hooks, le buffer, les clients IA, l'injecteur, l'accesseur UI Automation,
  la pastille, l'icône de barre des tâches (menu : Réglages / Annuler / Quitter), et
  branche les événements entre eux. Gère aussi le trousseau de clés et la mémorisation de
  la dernière transformation (pour Annuler).
- **`Autostart.cs`** — bascule ON/OFF le démarrage à l'ouverture de session via la clé
  `HKCU\...\Run`.

### `Input/` — la couche Win32 (la plus délicate)

- **`KeyboardHook.cs`** — installe un hook bas niveau `WH_KEYBOARD_LL`. Reçoit chaque
  appui/relâchement de touche dans tout Windows. Les évènements injectés par
  `TextInjector` (`LLKHF_INJECTED`) sont ignorés pour ne pas polluer le buffer.
- **`MouseHook.cs`** — `WH_MOUSE_LL`. Sert surtout à **réinitialiser le buffer au clic**
  (le curseur peut avoir changé de position).
- **`ForegroundWatcher.cs`** — `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`. **Reset du
  buffer** quand la fenêtre active change.
- **`KeyTranslator.cs`** — convertit un code de touche (virtual key) en **caractère
  Unicode** avec `ToUnicodeEx`, en gérant la disposition **AZERTY**, les **touches mortes**
  (`^` + `e` → `ê`) et **AltGr** (`@ # € [ ]`). ⚠️ **Le point le plus difficile de la partie
  clavier.**
- **`NativeInput.cs`** — fabrique partagée des structures `SendInput` Win32 (frappe
  Backspace classique et frappe Unicode), utilisée uniquement par `TextInjector`.
- **`TextInjector.cs`** — `SendInput` en un seul appel (Backspaces puis frappe Unicode
  caractère par caractère). Ne touche **jamais** au presse-papiers ni à une sélection —
  voir §7.6 pour l'historique de l'ancienne approche par Ctrl+V, abandonnée.
- **`UiaFieldAccessor.cs`** — lit et écrit le contenu d'un champ via UI Automation
  (`ValuePattern`/`TextPattern`), pour le cas où rien n'a été tapé avant `donna`. Aucune
  touche injectée, aucun presse-papiers, aucune sélection. Écriture vérifiée par
  relecture. Voir §7.6 pour le design complet et ses limites.

### `Core/` — la logique pure (100 % testable)

- **`TypingBuffer.cs`** — reconstruit le texte tapé et détecte la formule. Aucune
  dépendance Win32. Renvoie un `TriggerMatch(Source, Prompt, CharsToDelete, TriggerLength)` —
  `CharsToDelete` couvre toute la formule (chemin normal), `TriggerLength` couvre
  seulement déclencheur + prompt + 2 espaces (chemin UI Automation, pour découper la
  source sans rien effacer au clavier).

### `Ai/`

- **`AiProvider.cs`** — détecte le fournisseur d'une clé API par son préfixe (`gsk_` →
  Groq, sinon Gemini). Permet de mélanger des clés de plusieurs fournisseurs dans le même
  trousseau (`KeyRing`).
- **`AiClientExceptions.cs`** — `AiQuotaExceededException` et `AiApiException`, partagées
  entre `GeminiClient` et `GroqClient`.
- **`GeminiClient.cs`** — appel REST vers l'API publique Gemini `generateContent` (PAS
  l'API « Interactions », qui exige un token OAuth2 complet et échoue avec une simple clé
  API — vérifié en conditions réelles). Construit une requête qui **sépare le texte
  source de l'instruction** et demande explicitement à l'IA de renvoyer **uniquement le
  résultat** (pas de préambule, pas de guillemets) — le prompt système autorise aussi
  bien la correction de texte que la réponse à des demandes générales (génération de
  code, de requêtes, explications...).
- **`GroqClient.cs`** — même contrat, via l'API Groq compatible OpenAI
  (`chat/completions`).
- **`ResponseCleaner.cs`** — filet de sécurité : enlève les préambules type « Voici… »,
  les guillemets encadrants, les blocs markdown. Testable.

### `Ui/`

- **`PillOverlay.cs`** — petite pastille flottante qui affiche l'état (⏳ envoi… / ✅ / ❌)
  **sans jamais prendre le focus** (fenêtre `WS_EX_NOACTIVATE` + `WS_EX_TOPMOST`).
- **`SettingsForm.cs`** — fenêtre de réglages à onglets :
  - **Clés API** : saisie des clés Gemini et/ou Groq (une par ligne, stockées chiffrées
    via DPAPI).
  - **Général** : mot déclencheur, démarrage auto, modèle Gemini.
  - **Avancé** : logs de diagnostic.

### `Config/`

- **`AppConfig.cs`** — modèle des réglages (POCO sérialisable JSON).
- **`ConfigStore.cs`** — charge/sauvegarde `%APPDATA%\Donna\config.json`.
- **`KeyRing.cs`** — gère **plusieurs clés API** (mélange possible de fournisseurs) et
  **tourne** de l'une à l'autre sur n'importe quel échec (quota, clé invalide, mauvais
  fournisseur...) — pas seulement le quota, DONNA n'a aucune garantie a priori qu'une clé
  donnée fonctionne. Testable.
- **`DpapiSecret.cs`** — chiffre/déchiffre les clés avec DPAPI (liées à l'utilisateur
  Windows courant → illisibles ailleurs).

---

## 6. Spécification précise de la formule

- **Validation** = exactement **deux espaces consécutifs** en fin de saisie.
- **Déclencheur** = le mot `donna` (configurable, insensible à la casse), qui doit être un
  **mot entier** : un espace (ou un bord) de chaque côté. `madonna` ne déclenche pas.
- **Source** = tout ce qui précède `donna` dans le buffer (peut être vide → repli UI Automation).
- **Prompt** = tout ce qui suit `donna` jusqu'aux deux espaces (peut être vide).
- Il faut **au moins** une source **ou** un prompt : `donna␣␣` seul ne fait rien.
- **Chemin normal (source tapée)** — ce qui est effacé = tout le buffer (`CharsToDelete` =
  source + `donna` + prompt + 2 espaces), **jamais** plus.
- **Chemin de repli (source vide)** — rien n'est effacé au clavier ; `TriggerLength`
  (déclencheur + prompt + 2 espaces) sert uniquement à découper la formule du texte lu
  via UI Automation, par soustraction de chaîne.
- **Reset du buffer** : clic souris, changement de fenêtre, Entrée, Échap, Tab, flèches,
  Origine/Fin. Dans le doute → reset.
- Le buffer est **toujours réinitialisé après une formule traitée** (avec succès ou non) :
  sinon la frappe suivante s'accumule par-dessus l'ancienne formule déjà envoyée.

---

## 7. Points durs / risques techniques

1. **Reconstruction du texte tapé** (`KeyTranslator`) — AZERTY + touches mortes + AltGr via
   `ToUnicodeEx`. Isolé dans un seul fichier.
2. **Désynchronisation du buffer** — la reconstruction peut diverger du champ réel.
   Solution : reset agressif à chaque événement ambigu, et repli UI Automation quand la
   source est vide plutôt que d'échouer.
3. **Injection sans vol de focus** — l'injection ne doit jamais déplacer le focus, sinon
   le texte part dans la mauvaise fenêtre. `SendInput` avec `WS_EX_NOACTIVATE` sur la
   pastille suffit ; aucune API d'activation de fenêtre n'est utilisée nulle part.
4. **Prompt + nettoyage** — cadrer la requête IA pour un résultat brut, et nettoyer quand
   même par-dessus (`ResponseCleaner`).
5. **Antivirus / SmartScreen** — hook clavier global + `SendInput` + démarrage auto =
   comportement « type keylogger ». Prévoir la **signature de code** et une conception
   transparente (traitement local, envoi du seul texte déclenché, clés chiffrées, UI visible).
6. **Lecture d'un champ sans source tapée — post-mortem complet.**

   Trois approches ont été essayées, dans cet ordre :

   **a) Collage via presse-papiers (abandonnée).** `TextInjector` sauvegardait le
   presse-papiers, y mettait la réponse, envoyait Ctrl+V, puis restaurait l'ancien
   contenu. `SendInput` est asynchrone (dépose seulement l'évènement dans la file
   d'entrée) : rien ne garantissait que le Ctrl+V soit traité avant la restauration —
   parfois l'ancien contenu du presse-papiers (voire un contenu périmé d'un tout autre
   moment) se retrouvait collé à la place de la réponse. Remplacé par la frappe Unicode
   directe (`TextInjector` actuel), qui n'a plus jamais besoin du presse-papiers pour
   injecter du texte.

   **b) Sélection clavier + Ctrl+C (abandonnée, incident réel).** Pour lire le texte
   collé/préexistant sans source tapée, une version envoyait Maj+Origine (ou
   Ctrl+Maj+Origine pour tout le texte avant le curseur) puis Ctrl+C, lisait le
   presse-papiers, puis le restaurait. Deux défauts fatals, découverts en conditions
   réelles :
   - **Destruction de documents.** Au timeout (rien à copier, Ctrl+C n'aboutit pas), le
     Fin de désélection de secours pouvait être traité par l'application AVANT la
     sélection elle-même (toujours `SendInput` asynchrone) : la sélection totale se
     créait donc APRÈS sa propre annulation, sans plus rien pour la relâcher. La frappe
     suivante de l'utilisateur remplaçait alors tout le document sélectionné —
     trois documents perdus en une soirée de test avec la portée « tout le texte avant le
     curseur ».
   - **Interblocage.** `WH_KEYBOARD_LL` délivre les évènements au thread qui a installé le
     hook via SA file de messages. Faire attendre ce même thread (`Thread.Sleep`, même
     dans une boucle de sondage) empêche la boucle de messages de tourner — donc empêche
     le hook de dispatcher les évènements Maj/Ctrl/C qu'on vient d'injecter soi-même :
     interblocage garanti, le sondage attend un Ctrl+C qui ne peut jamais aboutir.
     `await Task.Delay` (jamais `Thread.Sleep`) rend la main à la boucle de messages entre
     deux sondages — nécessaire mais pas suffisant : la sélection reste une arme chargée
     tant qu'elle existe.

     **Conclusion retenue : DONNA ne doit plus jamais créer de sélection, quelle qu'en
     soit la portée.** Une sélection dangereuse ne peut pas être « annulée en toute
     sécurité » avec de l'input asynchrone (SendInput) — le problème est de conception,
     pas d'implémentation : aucun `try/finally`, délai ou second Fin ne corrige de façon
     fiable une désélection qui peut être traitée avant la sélection qu'elle est censée
     annuler.

   **c) UI Automation (actuelle).** `UiaFieldAccessor` lit et écrit le contenu d'un champ
   via les patterns d'accessibilité Windows (`ValuePattern.CurrentValue`/`SetValue`,
   repli `TextPattern.DocumentRange.GetText`) — **aucune touche injectée, aucun
   presse-papiers, aucune sélection**. La destruction devient impossible par
   construction : lire ne modifie rien, et écrire est une opération unique
   (`SetValue`) suivie d'une relecture de vérification, jamais une séquence
   effacer-puis-coller qui pourrait s'interrompre à mi-chemin.

   Détails de conception vérifiés empiriquement (pas devinés) :
   - **Package** : `Interop.UIAutomationClient` (COM), **pas**
     `System.Windows.Automation` (managé, nécessite `<UseWPF>true</UseWPF>`). Mesuré en
     publication self-contained single-file : +64 Ko pour la voie COM contre **+57 Mo**
     pour la voie WPF, à fonctionnalité strictement identique. Le choix ne se discute pas.
   - **Threading** : Microsoft recommande d'appeler les clients UI Automation depuis un
     thread **MTA** (à l'inverse du presse-papiers, qui exige STA). `DonnaContext`
     encapsule chaque appel `UiaFieldAccessor` dans un `Task.Run` (thread du pool, MTA par
     défaut), et revient sur le thread STA (sans `ConfigureAwait(false)`) uniquement pour
     la pastille WinForms. Contrairement à la sélection clavier, rien ici ne dépend du
     pompage de messages du hook (aucune touche injectée), donc aucun risque
     d'interblocage de ce type.
   - **Couverture réelle, testée en direct** : Bloc-notes classique (lecture ET écriture
     confirmées) et champs de navigateur Chrome (lecture confirmée) fonctionnent.
     **L'éditeur Monaco de VS Code n'expose pas son contenu** via ces patterns (limitation
     connue de Monaco : accessibilité activée seulement en mode lecteur d'écran) — DONNA
     échoue proprement avec un message clair sur cette application, sans tenter de
     contournement.
   - **Applications JS (React et consorts) — limite acceptée.** Certaines peuvent
     accepter un `SetValue` sans mettre à jour leur état interne (le texte s'affiche mais
     l'application "ne le voit pas", et peut disparaître à l'envoi). La relecture de
     vérification détecte le cas où le champ ne reflète PAS ce qu'on vient d'écrire, mais
     ne peut pas détecter le cas plus subtil où le DOM montre bien la nouvelle valeur
     alors que l'état interne (React) reste désynchronisé — auquel cas la vérification
     réussirait à tort. Ce cas précis n'a pas pu être testé empiriquement en conditions de
     développement (l'environnement de test ne permettait pas de garder le focus réel sur
     une page de test) ; à surveiller en usage réel, en particulier sur les applications
     de messagerie web.
   - **Nuance sur l'« atomicité »** : `SetValue` est une opération COM unique — pour un
     contrôle Win32 standard (Bloc-notes), un échec ne modifie effectivement rien. Pour
     un champ piloté par JavaScript, un échec de la vérification signifie que l'écriture
     a eu un effet, mais que son résultat final (valeur acceptée, revertie, ou partielle)
     est incertain — DONNA refuse alors de déclarer un succès plutôt que d'affirmer à
     tort que rien n'a changé.

---

## 8. Configuration & sécurité

- Fichier : `%APPDATA%\Donna\config.json`.
- Clés API **jamais en clair** : chiffrées via DPAPI (`DpapiSecret`), liées au compte
  Windows courant.
- Traitement **100 % local** : seul le texte explicitement déclenché (tapé ou lu via UI
  Automation) part vers l'IA.
- Rotation multi-clés, multi-fournisseurs (`KeyRing` + `AiProviderDetector`) sur tout échec.
- Logs de diagnostic optionnels (`DiagnosticLog`, désactivés par défaut) :
  `%APPDATA%\Donna\logs\donna.log`.

---

## 9. Historique de construction

1. **Cœur logique** — `TypingBuffer`, `ResponseCleaner`, `KeyRing` — + tests xUnit.
2. **Clients IA** — `GeminiClient` (endpoint public `generateContent`, pas l'API
   « Interactions » qui exige OAuth2), puis `GroqClient` en fournisseur alternatif.
3. **Hooks + `KeyTranslator`** — la partie Win32 clavier.
4. **`TextInjector`** — d'abord Backspaces + Ctrl+V + presse-papiers (abandonné, voir
   §7.6a), puis Backspaces + frappe Unicode directe (actuel).
5. **Câblage** — `Program.cs` (mutex) + `DonnaContext.cs`.
6. **UI** — `PillOverlay` + `SettingsForm`.
7. **Config + Autostart + Installeur** — `AppConfig`/`ConfigStore`/`DpapiSecret`, `Autostart`,
   `installer/donna.iss`, `build.ps1`.
8. **Lecture sans source tapée** — sélection clavier (abandonnée, voir §7.6b : destruction
   de documents + interblocage), puis UI Automation (actuel, voir §7.6c).
9. **Annulation** — mémorisation de la dernière transformation UI Automation (élément +
   texte d'origine), restaurable depuis le menu du tray.

---

## 10. Tests (`Donna.Tests/`, xUnit)

Cible : toute la logique pure, sans Win32 ni COM (rien qui envoie de vraies frappes ou
touche au presse-papiers/UI Automation réel).

- `TypingBuffer` : exemple Outlook, source seule, prompt seul, frontière de mot
  (`madonna`), simple vs double espace, Backspace, Reset, non-contamination entre deux
  formules successives, découpage `CharsToDelete` vs `TriggerLength`.
- `ResponseCleaner` : suppression des préambules, guillemets, markdown.
- `KeyRing` : rotation sur échec (quelle qu'en soit la raison), sélection de la clé
  suivante, épuisement.
- `AiProviderDetector` : détection Gemini/Groq par préfixe de clé.
- `KeyEvent` : détection `IsInjected`/`IsExtended` par bit de flag, y compris pour une
  frappe Unicode injectée (pas seulement une touche virtuelle classique).
- `ConfigStore` : chargement par défaut, aller-retour sauvegarde/lecture, création du
  dossier parent.

---

## 11. Build & distribution

- **`build.ps1`** : `dotnet publish` (single-file, self-contained, win-x64) → `ISCC` (Inno
  Setup) → produit **`Donna-Setup.exe`**.
- Taille mesurée du binaire self-contained single-file : **~116 Mo** (avec le client UI
  Automation COM ; aurait été ~173 Mo avec la voie WPF — voir §7.6).
- Prévoir la **signature du binaire et de l'installeur** pour limiter les avertissements
  SmartScreen/antivirus.
