# RpgLingo

Automatic translator for RPG Maker MV/MZ games. Drop the executable in the game folder, run it, and the game is translated. Supports DeepL, Google Cloud and Azure with automatic fallback between services.

## Requirements

- Windows (self-contained .exe, no .NET installation needed)
- An API key for at least one translation service:

| Service | Free tier | How to get a key |
|---------|-----------|------------------|
| DeepL API | 500K chars/month | [deepl.com/pro-api](https://www.deepl.com/pro-api) |
| Google Cloud Translation | $300 free credits (1 year) | [cloud.google.com](https://cloud.google.com/translate/docs/setup) |
| Azure Translator | 2M chars/month | [azure.microsoft.com](https://learn.microsoft.com/en-us/azure/ai-services/translator/) |

You can add **multiple keys for the same service** to multiply your free quota. For example, three DeepL free accounts give you 1.5M chars/month.

## Usage

1. Download `RpgLingo.exe` from [Releases](https://github.com/your-username/RpgLingo/releases).
2. Copy it into the root folder of your RPG Maker MV/MZ game (where `Game.exe` is).
3. Run `RpgLingo.exe`.

On first run, the program guides you through adding your API keys and choosing source/target languages. This configuration is stored globally in `%LocalAppData%/RpgLingo/` and reused for all future games.

The program runs in four phases:

### Phase 1 — Analysis

Scans all game files, counts characters, and shows how much quota each endpoint has left:

```
  Languages: English (en) → Español (es)
  Is this correct? (y/n): y

  Analyzing files...

    Map001.json                  12,340 chars (2,100 cached)
    CommonEvents.json             3,200 chars (0 cached)
    Items.json                      890 chars (0 cached)

    Total strings:      1,245
    Total characters:   16,430
    Already cached:        2,100
    To translate:          14,330

  Available quota:
    DeepL Free #1: 425,000 chars remaining
    DeepL Free #2: 500,000 chars remaining

  Continue with translation? (y/n):
```

No API calls are made during this phase.

### Phase 2 — Backup

Copies the game's `data` folder to `data_{language}` (e.g. `data_es`, `data_fr`). Original files are never modified.

### Phase 3 — Translation

Translates all text in the copied files. Progress is shown in real time. If interrupted (crash, power loss, Ctrl+C), run again and it will pick up where it left off — the cache saves periodically, not just at the end.

### Phase 4 — Apply

After translation, the program asks if you want to apply it to the game:

```
  Apply the translation to the game? (y/n): y
  Originals saved in: data_original
  Translation applied. The game will start translated.
```

This renames `data` → `data_original` (backup) and copies `data_{language}` → `data` (active). To revert to the original language, delete `data` and rename `data_original` back to `data`.

## Glossary

On first run for each game, RpgLingo scans the game files and generates a `glossary.json` next to the executable with all character names, items, skills, weapons, enemies, classes, and states. You can edit this file to add translations for terms that should stay consistent:

```json
[
  { "Term": "Dark Knight", "Translation": "Caballero Oscuro", "Note": "Class" },
  { "Term": "Excalibur", "Translation": "Excalibur", "Note": "Arma" },
  { "Term": "Heal", "Translation": "Curar", "Note": "Habilidad" }
]
```

Terms with a translation are replaced with placeholders before sending to the API and restored afterward, so they always appear exactly as you defined them. Terms without a translation are left as-is in the original language.

## Configuration

All settings are stored in `%LocalAppData%/RpgLingo/config.json`:

```json
{
  "Endpoints": [
    {
      "Service": "DeepL",
      "ApiKey": "your-key-here",
      "CharLimit": 500000,
      "CharsUsed": 123000,
      "Label": "DeepL Free #1"
    },
    {
      "Service": "Azure",
      "ApiKey": "your-azure-key",
      "Region": "westeurope",
      "CharLimit": 2000000,
      "CharsUsed": 0,
      "Label": "Azure Free"
    }
  ],
  "SourceLanguage": "en",
  "TargetLanguage": "es",
  "MaxLineLength": 55,
  "CacheMaxSizeMB": 512
}
```

Endpoints are used in order — the first one with remaining quota handles the translation. When it runs out, the next one takes over automatically. You can manage endpoints through the interactive setup wizard on first run, or by editing the JSON file directly.

Source and target languages are stored as defaults but you can change them at the start of each run — either permanently or just for that session.

The translation cache (`translation_cache.json`) is shared across all games. Reset the usage counters at the start of each month via the setup wizard.

## Features

- **Endpoint cascade** — Add as many API keys as you want, in priority order. Supports mixing services (e.g. two DeepL keys, then one Azure as fallback). The first with available quota is used.
- **Configurable languages** — Source and target languages are configurable (ISO 639-1 codes). Change them globally or just for a single session.
- **Dialog grouping** — Consecutive dialog lines (code 401) are joined into a single block before translating, giving the API full context for higher quality results.
- **Smart line wrapping** — Translated text is redistributed into lines that fit the dialog window (~55 chars, configurable) using a dynamic programming algorithm for visually balanced lines.
- **Translation cache** — Every translated string is cached to disk and shared across games. Repeated text costs zero API calls. The cache saves every 10 translations, so even a crash loses almost nothing.
- **Glossary** — Auto-generated per game from character names, items, skills, etc. Fill in translations to ensure consistency. Uses unicode placeholders that translation APIs won't touch.
- **Control code protection** — All RPG Maker escape sequences (`\C[n]`, `\V[n]`, `\I[n]`, etc.) and script variables (`$gameVariables[n]`, `<tag:value>`, `set_npm(...)`) are detected, protected during translation, and restored afterward.
- **Batch translation** — Sends up to 50 texts per API call when using DeepL, dramatically reducing latency for object files and choices.
- **Non-destructive** — Originals are always preserved in `data_original`. Multiple translations can coexist side by side (`data_es`, `data_fr`, `data_ja`).
- **Dry run** — Counts all characters and shows quota impact before making any API calls.
- **Resume support** — If interrupted, picks up where it left off. The cache and per-file progress markers survive crashes.
- **Session statistics** — After each run, shows translations, cache hits, failures, API calls, characters translated, speed (chars/s), and more.
- **Auto-apply** — Optionally applies the translation to the game automatically by renaming folders, with a simple path to revert.

## Supported event codes

| Code | Type |
|------|------|
| 401 | Dialog text |
| 405 | Scroll text |
| 102 | Choices |
| 402 | Choice answers |

Additionally, RpgLingo translates Items, Weapons, Armors, Skills, Enemies, Classes, States, and System.json (game title, menu terms, battle commands, equipment types, element names).

## Building from source

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

NuGet dependencies: `DeepL.net`, `Google.Cloud.Translation.V2`, `Azure.AI.Translation.Text`

## License

[GNU Affero General Public License v3.0](LICENSE)