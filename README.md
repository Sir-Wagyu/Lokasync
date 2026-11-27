# Lokasync

Lokasync is a Windows desktop application for translating PC game text into Indonesian using any OpenAI-compatible LLM endpoint, including local routers and local models.

Many story-heavy indie and PC games never get an official Indonesian translation. Community translators usually have to juggle separate unpackers, manual regex replacements, spreadsheets, and repacking scripts for each game engine, and one broken formatting tag or variable can crash a dialogue box in-game. Lokasync puts extraction, tag protection, resumable translation, manual review, backup, and patching into one Windows tool.

## What Lokasync does

Lokasync scans a game installation folder, detects the engine, extracts translatable text into a local SQLite database, translates it batch by batch through your configured LLM endpoint, and writes the verified Indonesian text back into the game files.

Before any file on disk changes, Lokasync creates a SHA-256 verified backup of the original files so you can restore the game to its untouched state in one click.

## Supported game formats

| Engine / Format | Detection & Target Files | Patching Approach |
| :--- | :--- | :--- |
| Unreal Engine | `.pak` archives and loose `.locres` files | Extracts string tables via `CUE4Parse` and generates a separate mod archive (`Lokasync_ID_P.pak`) under `Content\Paks\~mods\` without modifying base `.pak` files |
| Unity | `*_Data\resources.assets`, `*_Data\sharedassets*.assets`, and packed `*_Data\data.unity3d` bundles | Reads and patches `TextAsset`, `LocalizationAsset`, Unity Localization `StringTable`, and I2 Localization (`I2.Loc.LanguageSource` / `LanguageSourceAsset`) using `AssetsTools.NET` and `AssetsTools.NET.MonoCecil`; writes large bundles via a streaming 128 KiB LZ4 UnityFS writer |
| RPG Maker MV / MZ | `www\data\` or `data\` containing `Map*.json` and `CommonEvents.json` | Extracts and patches dialogue commands (`code: 401`, `405`, and choice arrays `102`) in place while preserving JSON structure |
| Loose text files | `.srt`, `.json`, and `.xml` dialogue files | Patches text nodes and subtitle cues while preserving BOM, line endings, timestamps, XML comments, and CDATA blocks; ignores Unity runtime folders (`*_Data\Managed`, `Mono`, `Plugins`, `Resources`) and .NET XML documentation files |

## How the pipeline protects game files

### Tag and variable masking
Game dialogue often mixes prose with rich-text markup (`<color=#FF0000>`, `<b>`), runtime variables (`{player_name}`, `%s`, `\N[1]`), and control sequences (`\n`, `\t`). Before sending a batch to the LLM, `Lokasync.Sanitizer` replaces those tokens with placeholders (`[TAG_0]`, `[VAR_0]`, `[NL_0]`, `[TAB_0]`). When the translation returns, `TryUnmask` verifies that every placeholder came back intact. If a placeholder is missing or altered, Lokasync flags the row as `FailedValidation` and falls back to the original source text when patching so broken markup never enters the game.

### Resumable sequential translation
`Lokasync.Translator` processes batches sequentially (`MaxConcurrency = 1`) with configurable delay and random jitter between requests to stay within strict rate limits. Every finished batch checkpoints to SQLite at `%LocalAppData%\Lokasync\Projects\<ProjectHash>.db`. If the router returns HTTP `401`, `403`, or `429`, the worker pauses immediately without losing completed rows. When your quota resets or you switch models, clicking Resume continues from the remaining `Pending` entries. If a model drops or adds keys in a batch response, the worker splits the batch and retries up to two times.

### Tone, glossary, and inline review
Plain literal translations often feel stiff in games with a distinct setting. In the Glossary & Tone tab, you can define `genre_tone` and fixed glossary terms (character names, locations, currencies, titles) that are injected into every translation prompt. If you have not played the game yet, the **Auto-Generate Tone & Glossary (AI)** button samples up to 60 representative lines from the extracted text and asks your active LLM to draft a tone guide and initial glossary for you to review and edit.

After translation finishes, the inline Translation Grid lets you search entries, filter by `FailedValidation` or `OverflowWarning` (flagged when translated text exceeds source length by more than 25%), edit any row directly, and re-patch without re-translating the whole game.

## Using the prebuilt Windows executable

If a prebuilt `Lokasync.App.exe` is attached to the GitHub Releases page, download and run it directly on 64-bit Windows. The release binary is published as a self-contained single file, so it does not require a separate .NET 8 installation.

1. Open `Lokasync.App.exe` and stay on the **Dashboard & Router** tab.
2. In the router panel on the right, enter your OpenAI-compatible chat completions URL (include the full path, for example `http://127.0.0.1:8045/v1/chat/completions`), API key, model name, batch size, and delay settings, then save the profile.
3. In the project panel on the left, select your game's root installation folder. Lokasync detects the engine automatically.
4. Open **Glossary & Tone** to enter your tone and glossary, or click **Auto-Generate Tone & Glossary (AI)**, then save.
5. Open **Pipeline & Live Log** and click **Start**. Once translation finishes, review any flagged rows in **Translation Grid & Re-Patch**, then click **Patch Game**.
6. To revert the game files back to original at any time, click **Restore Original**.

Runtime profiles, project databases, and backups live outside the repository under `%LocalAppData%\Lokasync\`:

- `%LocalAppData%\Lokasync\profiles.json` stores your router profiles and API keys.
- `%LocalAppData%\Lokasync\Projects\<ProjectHash>.db` stores extracted entries, translation checkpoints, and narrative context.
- `%LocalAppData%\Lokasync\Backups\<ProjectHash>\` stores SHA-256 verified original game files.

## Building from source

### Prerequisites

- Windows 10 or Windows 11 (x64)
- .NET 8 SDK (`8.0.x`)

### Build and run tests

```powershell
dotnet build Lokasync.sln
dotnet test Lokasync.sln
dotnet test tests\Lokasync.E2E.Tests\Lokasync.E2E.Tests.csproj
```

All automated tests run offline against synthetic game fixtures, in-memory/temporary SQLite databases, and mock HTTP handlers.

### Run the desktop app in development

```powershell
dotnet run --project src\Lokasync.App\Lokasync.App.csproj
```

### Publish a standalone Windows executable

```powershell
dotnet publish src\Lokasync.App\Lokasync.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o Publish\win-x64
```

The output file is written to `Publish\win-x64\Lokasync.App.exe`.

## Project structure

```text
Lokasync.sln
├── src/
│   ├── Lokasync.Core/         # Shared models, adapter/sanitizer/translator interfaces, and SQLite repositories
│   ├── Lokasync.Sanitizer/    # Regex masking and strict unmask validation for tags and variables
│   ├── Lokasync.Translator/   # OpenAI-compatible batch client, tolerant JSON extractor, queue worker, and AI glossary generator
│   ├── Lokasync.Adapters/     # Engine fingerprinter and adapters for Unreal, Unity, RPG Maker, and loose files
│   └── Lokasync.App/          # WPF desktop UI (CommunityToolkit.Mvvm), SHA-256 backup service, and pipeline orchestration
└── tests/
    ├── Lokasync.Tests/        # Unit, integration, and hardening tests (xUnit)
    └── Lokasync.E2E.Tests/    # Opaque-box end-to-end test suite across synthetic fixtures
```
