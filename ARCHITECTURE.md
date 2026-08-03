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

### Les trois modes

`TypingBuffer` ne voit que ce qui est **tapé au clavier** : ni le texte collé (Ctrl+V
réinitialise le buffer par prudence), ni le texte déjà présent dans le champ avant que
DONNA démarre. Une source tapée vide recouvre donc deux situations bien différentes, et
DONNA (`TransformModeSelector`, voir §2 et §5) choisit entre trois modes :

| Mode | Condition | Comportement |
|---|---|---|
| **1 — Source tapée** | `trigger.Source` non vide | Chemin historique : injection via `TextInjector`. Fonctionne partout. |
| **2 — Lecture UI Automation** | Source vide, ET la lecture du champ (voir §7.6c) réussit et laisse du texte après retrait de la formule | Le texte lu devient la source. Écriture à deux niveaux (§7.6c) : `SetValue`, repli clavier vérifié sinon. |
| **3 — Génération pure** | Source vide, ET la lecture échoue OU ne laisse rien après retrait de la formule | Aucune source : l'IA génère à partir du seul prompt. Injection via `TextInjector`, comme le mode 1 — fonctionne partout, aucune dépendance à UI Automation. |

**Point essentiel (régression corrigée) :** un échec de lecture UI Automation (application
non supportée, curseur pas en fin de champ) ne doit **jamais** empêcher le mode 3 — ce
n'est pas une erreur, juste l'absence de texte à lire. Confondre "source vide" avec
"lecture UI Automation obligatoire" a un temps cassé la génération pure dans toute
application non supportée par UI Automation (voir §9).

Historique : une première version lisait le cas "texte collé/déjà présent" par
**sélection clavier** (Maj/Ctrl+Origine puis Ctrl+C) — abandonnée après avoir détruit des
documents entiers en conditions réelles (voir §7.6 pour le post-mortem complet).

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

| Ce que tu tapes | Mode | Comportement |
|---|---|---|
| `texte donna instruction␣␣` | 1 | transforme `texte` selon `instruction` |
| `mon texte donna␣␣` | 1 | action par défaut (correction) sur le texte tapé |
| `donna instruction␣␣` (texte collé/déjà présent) | 2 | lecture du champ via UI Automation, ce texte devient la source |
| `donna écris un haïku␣␣` (champ vide ou lecture non supportée) | 3 | génération pure (pas de source) |
| `j'écoute madonna␣␣` | — | **ne déclenche pas** (`donna` collé à un mot) |
| `texte donna reformule␣` | — | **ne déclenche pas** (un seul espace) |

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
  TypingBuffer  ── reconstruit le texte tapé + détecte la formule (TriggerMatch)
        │
        │  Source tapée non vide ?
        ├─ OUI ──────────────────────────────────► Mode 1
        │
        └─ NON : UiaFieldAccessor.TryReadFocusedField(TypedSuffix)
                 (lecture, sur un thread MTA via Task.Run — voir §7.6c)
                 puis TransformModeSelector.SelectMode(trigger, texteLu)
                       │
                       │  Lecture OK ET reste du texte après retrait de la formule ?
                       ├─ OUI ─────────────────────► Mode 2
                       └─ NON (non supporté, curseur ailleurs, ou rien à part
                              la formule) ──────────► Mode 3 (JAMAIS une erreur)
        │
        ▼
  GeminiClient / GroqClient ── envoie source + prompt à l'IA (fournisseur détecté par clé)
        │
        ▼
  ResponseCleaner ── enlève préambules / guillemets / markdown
        │
        ▼
  Mode 1 ou 3 : TextInjector.Replace(trigger.CharsToDelete, réponse)
                (N Backspaces puis frappe Unicode — fonctionne partout)
  Mode 2      : VerifiedFieldWriter.Write(élément, texteOrigine, réponse)
                (SetValue, repli clavier vérifié sinon — voir §7.6c),
                puis mémorise (élément, réponse, source) pour "Annuler"
        │
        ▼
  [Le champ affiche la réponse — le focus n'a jamais bougé]
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
│   ├── NativeInput.cs         Fabrique SendInput partagée (structures Win32, Backspace, Unicode, Maj+Entrée)
│   ├── TextInjector.cs        SendInput       — Backspaces + frappe Unicode (jamais de presse-papiers)
│   ├── UiaFieldAccessor.cs    UI Automation (COM) — lecture (mode 2/3) et écriture niveau 1 (SetValue)
│   └── VerifiedFieldWriter.cs Écriture à deux niveaux : SetValue, repli clavier vérifié sinon
├── Core/
│   ├── TypingBuffer.cs         Buffer + machine à états du trigger      ← CŒUR TESTABLE
│   └── TransformModeSelector.cs Choix du mode 1/2/3 — logique pure     ← TESTABLE
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

Donna.Tests/                   xUnit — TypingBuffer, TransformModeSelector, VerifiedFieldWriter,
                                ResponseCleaner, KeyRing, AiProvider, KeyEvent, ConfigStore...
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
- **`NativeInput.cs`** — fabrique partagée des structures `SendInput` Win32 (Backspace,
  frappe Unicode, Maj+Entrée pour les sauts de ligne), utilisée uniquement par `TextInjector`.
- **`TextInjector.cs`** — `SendInput` en un seul appel (Backspaces puis frappe caractère
  par caractère). Ne touche **jamais** au presse-papiers ni à une sélection — voir §7.6
  pour l'historique de l'ancienne approche par Ctrl+V, abandonnée. Chaque saut de ligne de
  la réponse est injecté en **Maj+Entrée**, jamais Entrée seule ni le caractère `\r`/`\n`
  brut : Windows traduit un appui réel sur Entrée en `WM_CHAR(0x0D)`, donc injecter ce
  caractère produirait le même évènement qu'un vrai appui sur Entrée — ce qui enverrait le
  message dans WhatsApp/Slack/Teams au lieu d'y insérer un saut de ligne. Maj+Entrée est le
  raccourci universellement reconnu par ces applications pour "nouvelle ligne, ne pas
  envoyer", et se comporte comme Entrée seule dans un champ multiligne simple (aucune
  régression). Les `\r\n`/`\r` isolés sont normalisés en `\n` avant l'injection.
- **`UiaFieldAccessor.cs`** — lit le contenu d'un champ (modes 2/3) et écrit en **niveau 1**
  via `ValuePattern.SetValue`, pour le cas où rien n'a été tapé avant `donna`. Aucune touche
  injectée pour la lecture, aucun presse-papiers, aucune sélection. Deux méthodes de
  lecture : `TryReadFocusedField(expectedSuffix)` (choisit le pattern dont le résultat se
  termine par la formule tapée — détection initiale du mode, sur l'élément qui a le focus
  à cet instant) et `TryReadElementText(element)` (le résultat le plus informatif des deux
  patterns, sur un **élément précis passé en paramètre** — jamais "celui qui a le focus
  maintenant" — utilisé par `VerifiedFieldWriter` pour sonder l'état du champ ciblé sans
  risquer de valider un autre élément si le focus a bougé entre-temps). Voir §7.6 pour le
  design complet et ses limites.
- **`VerifiedFieldWriter.cs`** — écriture à deux niveaux pour le mode 2 : `UiaFieldAccessor.TryWrite`
  (niveau 1, `SetValue`) en priorité, puis un **repli clavier vérifié** (niveau 2) si
  indisponible ou si la vérification échoue — Backspace exacts (comptés depuis une
  relecture fraîche du champ, jamais une valeur supposée) puis injection via `TextInjector`.
  `SendInput` ne fait que mettre les touches en file : chaque étape attend un changement
  RÉELLEMENT observé (sondage borné, `await Task.Delay`, jamais `Thread.Sleep` ni une
  relecture immédiate) avant de décider quoi que ce soit, et cible toujours l'élément passé
  en paramètre (`TryReadElementText`), jamais le focus courant. En cas d'échec, tente de
  restaurer le texte d'origine plutôt que de laisser un état intermédiaire — sauf quand
  aucun changement n'a été observé du tout, auquel cas le texte d'origine est par
  construction encore intact et DONNA abandonne sans envoyer une touche de plus. Voir §7.6c.

### `Core/` — la logique pure (100 % testable)

- **`TypingBuffer.cs`** — reconstruit le texte tapé et détecte la formule. Aucune
  dépendance Win32. Renvoie un
  `TriggerMatch(Source, Prompt, CharsToDelete, TriggerLength, TypedSuffix)` —
  `CharsToDelete` couvre toute la formule (mode 1), `TriggerLength`/`TypedSuffix` couvrent
  seulement déclencheur + prompt + 2 espaces (modes 2/3). `TriggerMatch.TryExtractSourceFromFieldText`
  vérifie qu'un texte lu ailleurs (UI Automation) se termine bien par `TypedSuffix` avant
  d'en déduire la source — jamais une troncature aveugle par longueur (voir §7.6c, curseur
  pas en fin de champ).
- **`TransformModeSelector.cs`** — décide entre les modes 1/2/3 (voir §1) à partir d'un
  `TriggerMatch` et du texte éventuellement lu via UI Automation (`string?`, `null` si la
  lecture n'a pas été tentée ou a échoué). Logique pure, aucune dépendance Win32/COM —
  c'est ce qui permet de la tester entièrement en xUnit sans machine réelle.

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
- **Source** = tout ce qui précède `donna` dans le buffer (peut être vide → modes 2/3, voir §1).
- **Prompt** = tout ce qui suit `donna` jusqu'aux deux espaces (peut être vide).
- Il faut **au moins** une source **ou** un prompt : `donna␣␣` seul ne fait rien.
- **Mode 1 (source tapée)** — ce qui est effacé = tout le buffer (`CharsToDelete` =
  source + `donna` + prompt + 2 espaces), **jamais** plus.
- **Modes 2/3 (source vide)** — rien n'est effacé au clavier pour lire ; `TypedSuffix`
  (le texte exact de déclencheur + prompt + 2 espaces, pas juste sa longueur) sert à
  **vérifier** qu'un texte lu ailleurs (UI Automation) se termine bien par ceci avant d'en
  déduire la source (`TryExtractSourceFromFieldText`) — jamais une troncature aveugle par
  longueur, qui couperait du vrai contenu si le curseur n'était pas en fin de champ.
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
     encapsule chaque appel `UiaFieldAccessor`/`VerifiedFieldWriter` dans un `Task.Run`
     (thread du pool, MTA par défaut), et revient sur le thread STA (sans
     `ConfigureAwait(false)`) uniquement pour la pastille WinForms. Contrairement à la
     sélection clavier, rien ici ne dépend du pompage de messages du hook (aucune touche
     injectée pour la lecture), donc aucun risque d'interblocage de ce type.
   - **ValuePattern supporté mais vide (bug corrigé)** — sur un `contenteditable`
     (WhatsApp Web, Slack, Gmail...), `ValuePattern` est souvent *disponible* mais renvoie
     une chaîne vide (le vrai contenu n'est exposé que via `TextPattern`). Un simple repli
     `??` sur "supporté ou pas" ne suffit pas, puisqu'une chaîne vide n'est pas
     `null` : `TryReadFocusedField` essaie les deux patterns et retient celui dont le
     résultat est réellement exploitable (non vide, et se terminant par la formule tapée),
     pas le premier qui existe.
   - **Confusion mode 2 / mode 3 (régression corrigée)** — une version antérieure
     traitait toute source vide comme "il faut lire via UI Automation, sinon erreur",
     cassant la génération pure (mode 3) dans toute application non supportée. Corrigé en
     séparant clairement la DÉCOUVERTE du mode (`TransformModeSelector`, logique pure : une
     lecture absente ou inexploitable fait retomber sur le mode 3, jamais une erreur) de la
     LECTURE elle-même (`UiaFieldAccessor`, qui peut échouer sans conséquence).
   - **Écriture universelle (mode 2) — deux niveaux, `VerifiedFieldWriter`.**
     `ValuePattern.SetValue` est refusé par WhatsApp Web (`contenteditable`) et par Word
     (document riche, pas un simple champ de saisie) : le mode 2 y était donc inutilisable
     avec le seul niveau 1. Le niveau 2 (repli clavier vérifié) exploite le fait que
     l'injection Unicode (`TextInjector`) fonctionne déjà partout (c'est le mécanisme du
     mode 1) — le seul obstacle était de savoir combien effacer, et UI Automation donne
     maintenant le contenu exact du champ :
     1. Relire le champ **maintenant** (pas se fier à une valeur d'avant l'essai
        `SetValue`, qui a pu le modifier partiellement même en cas d'échec).
     2. Compter les Backspace nécessaires — en normalisant `\r\n` en `\n` d'abord
        (`VerifiedFieldWriter.CountBackspacesNeeded`) : un saut de ligne peut être lu comme
        2 caractères par UI Automation mais ne s'efface qu'avec **un seul** Backspace dans
        la plupart des contrôles.
     3. Envoyer les Backspace (jamais de sélection — la règle de sécurité du §7.6b tient
        intégralement ici aussi).
     4. **Attendre** (jamais relire immédiatement) que le champ **ciblé** reflète
        réellement le vidage — sondage borné (`await Task.Delay`, jamais `Thread.Sleep`)
        via `UiaFieldAccessor.TryReadElementText(element)` sur l'élément précis, jamais le
        focus courant. Effacement incomplet mais un changement a bien été observé →
        recompter depuis l'état réel et réessayer (nombre de tentatives borné). Aucun
        changement du tout observé avant le timeout → abandonner immédiatement avec une
        erreur claire, **sans envoyer une seule touche de plus** (le texte d'origine est
        alors par construction encore intact, puisque rien n'a bougé). Voir l'incident
        ci-dessous.
     5. Injecter la réponse via `TextInjector`, puis attendre de la même façon (sondage
        borné sur l'élément ciblé) que le champ reflète la nouvelle valeur.
   - **Vérifier une action asynchrone avant qu'elle ait eu lieu (bug corrigé) —**
     la toute première version du repli clavier envoyait les Backspace via `SendInput`
     puis relisait le champ **immédiatement**. `SendInput` ne fait que mettre les
     évènements en file d'attente : rien ne garantit qu'ils ont été traités par
     l'application ciblée au retour de l'appel. La relecture immédiate voyait donc
     souvent le champ encore inchangé, concluait à tort à un échec d'effacement, et
     renvoyait un nouveau lot de Backspace — jusqu'à effacer plusieurs fois trop de
     texte, y compris du contenu qui précédait la source. Même défaut dans la
     restauration de secours. C'est exactement le même piège que le presse-papiers
     (§7.6a) et la sélection clavier (§7.6b) : vérifier un effet côté application avant
     qu'il ait réellement eu lieu. Corrigé en sondant le champ **ciblé** (jamais le
     focus courant, qui a pu changer entre-temps) avec un délai borné entre deux
     lectures (`await Task.Delay`, jamais `Thread.Sleep` — qui bloquerait le thread
     sans nécessité, ni une relecture immédiate) : on ne décide qu'une fois un
     changement réellement observé, et si rien ne change avant l'expiration du délai,
     DONNA abandonne sans envoyer une touche de plus plutôt que de deviner.
   - **Délai fixe insuffisant sur un champ volumineux (bug corrigé) —** la première
     version du sondage utilisait un délai borné fixe (mesuré depuis le premier envoi de
     Backspace) : sur un champ contenant beaucoup de texte, l'effacement complet peut
     prendre plus longtemps que ce délai tout en progressant normalement, ce qui faisait
     expirer le sondage pendant que l'application traitait encore les touches en file —
     la tentative suivante en envoyait alors un nouveau lot par-dessus, sur-effaçant le
     champ. Corrigé en réarmant le délai à chaque changement réellement observé (même
     partiel) : `WaitForValueAsync` ne conclut à un blocage que si la valeur reste
     STABLE (aucun changement) pendant tout le délai, jamais simplement parce que le
     délai total écoulé dépasse un seuil fixe. Un champ lent mais qui progresse peut
     donc légitimement prendre plus de temps qu'un champ bloqué — c'est voulu.
   - **Applications JS (React et consorts) — limite acceptée.** Certaines peuvent
     accepter une écriture (`SetValue` ou repli clavier) sans mettre à jour leur état
     interne (le texte s'affiche mais l'application "ne le voit pas", et peut disparaître
     à l'envoi). La relecture de vérification détecte le cas où le champ ne reflète PAS ce
     qu'on vient d'écrire, mais ne peut pas détecter le cas plus subtil où le DOM montre
     bien la nouvelle valeur alors que l'état interne (React) reste désynchronisé —
     auquel cas la vérification réussirait à tort. Ce cas précis n'a pas pu être testé
     empiriquement en conditions de développement (l'environnement de test ne permettait
     pas de garder le focus réel sur une page de test) ; à surveiller en usage réel, en
     particulier sur les applications de messagerie web — toujours vérifier que le
     résultat est bien pris en compte par l'application (pas seulement affiché).
   - **Nuance sur l'« atomicité »** : `SetValue` est une opération COM unique — pour un
     contrôle Win32 standard (Bloc-notes), un échec ne modifie effectivement rien. Pour
     un champ piloté par JavaScript, un échec de la vérification signifie que l'écriture
     a eu un effet, mais que son résultat final (valeur acceptée, revertie, ou partielle)
     est incertain — DONNA refuse alors de déclarer un succès plutôt que d'affirmer à
     tort que rien n'a changé. Le repli clavier (niveau 2), lui, est vérifié à chaque
     étape et restaure activement en cas d'échec (voir ci-dessus).
   - **Couverture réelle, testée en direct** : Bloc-notes classique (lecture et écriture
     niveau 1 confirmées) et champs de navigateur Chrome (lecture confirmée) fonctionnent.
     **L'éditeur Monaco de VS Code n'expose pas son contenu** via ces patterns (limitation
     connue de Monaco : accessibilité activée seulement en mode lecteur d'écran) — DONNA
     échoue proprement avec un message clair sur cette application (modes 1 et 3 y
     fonctionnent normalement), sans tenter de contournement.

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
10. **Correctifs de robustesse UI Automation** — sélection du pattern par contenu exploitable
    plutôt que simple présence (`ValuePattern` vide sur `contenteditable`, voir §7.6c) ;
    vérification (`TryExtractSourceFromFieldText`) que le champ lu se termine bien par la
    formule tapée avant d'en déduire la source (curseur pas en fin de champ).
11. **Maj+Entrée pour les sauts de ligne** — `TextInjector` n'injecte plus jamais Entrée
    seule ni le caractère `\r`/`\n` brut, pour ne pas envoyer prématurément un message dans
    les messageries (WhatsApp, Slack, Teams...).
12. **Séparation mode 2 / mode 3** (`TransformModeSelector`) — correction d'une régression
    où toute source vide exigeait une lecture UI Automation réussie, cassant la génération
    pure dans les applications non supportées (voir §7.6c).
13. **Écriture universelle** (`VerifiedFieldWriter`) — repli clavier vérifié quand
    `SetValue` est refusé (WhatsApp Web, Word), avec restauration en cas d'échec ; Annuler
    utilise désormais le même mécanisme, et le menu du tray se grise quand il n'y a rien à
    annuler (voir §7.6c).
14. **Correction du repli clavier — attente au lieu de relecture immédiate** — la
    vérification post-Backspace relisait le champ immédiatement après `SendInput`
    (asynchrone), risquant jusqu'à 3× trop de Backspace envoyés ; corrigé par un sondage
    borné (`await Task.Delay`) sur l'élément ciblé explicitement, jamais le focus courant
    (voir §7.6c).

---

## 10. Tests (`Donna.Tests/`, xUnit)

Cible : toute la logique pure, sans Win32 ni COM (rien qui envoie de vraies frappes ou
touche au presse-papiers/UI Automation réel).

- `TypingBuffer` : exemple Outlook, source seule, prompt seul, frontière de mot
  (`madonna`), simple vs double espace, Backspace, Reset, non-contamination entre deux
  formules successives, découpage `CharsToDelete` vs `TriggerLength`/`TypedSuffix`,
  `TryExtractSourceFromFieldText` (succès, curseur pas en fin de champ, champ trop court).
- `TransformModeSelector` : les trois modes, priorité du mode 1 même si une lecture UIA
  est fournie, repli sur le mode 3 quand la lecture est absente ou inexploitable (jamais
  une erreur bloquante).
- `VerifiedFieldWriter` : `CountBackspacesNeeded` — normalisation `\r\n`/`\n` avant de
  compter (partie pure ; le sondage/repli clavier réel, qui dépend du temps et de vraies
  frappes, n'est pas testé ici, voir contraintes en tête de section).
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
