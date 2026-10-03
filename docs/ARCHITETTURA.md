# Renamr · Architettura della soluzione

Applicazione desktop Windows per riconoscere, rinominare e "datare" correttamente archivi di film, serie TV, anime e musica.

## 1. Scelte di stack

| Tema | Scelta | Motivo |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | .NET 8 e .NET 9 escono dal supporto il 10 novembre 2026; .NET 10 è LTS fino a novembre 2028. Nessuna API usata è specifica di 10: il retarget a `net8.0` richiede solo di cambiare `TargetFramework` e `LangVersion` (le proprietà `partial` del Toolkit MVVM richiedono C# 14 o `preview`). |
| UI | **WinUI 3 + Windows App SDK 1.8**, app non pacchettizzata e self-contained | Controlli Fluent nativi, Mica, tema chiaro/scuro automatico. La 1.8 è l'ultima linea 1.x stabile; la 2.x è uscita da poco e il passaggio va valutato a parte. |
| MVVM | **CommunityToolkit.Mvvm 8.4** | `[ObservableProperty]` su proprietà `partial` (compatibili AOT/WinRT), `[RelayCommand]` async con cancellazione automatica, `IMessenger` per il pannello errori. |
| Tag | **TagLibSharp 2.3** | ID3v2.4, Vorbis/FLAC, atomi MP4, tag Matroska, RIFF INFO, ASF. |
| Film | **TMDbLib 3.0** (TMDb) + **OMDb** via HttpClient | TMDb primario, OMDb come fallback. |
| Serie / anime | **TheTVDB v4**, **TVmaze**, **AniDB** via HttpClient | TheTVDB primario se c'è la chiave, TVmaze senza chiave, AniDB prima di tutti per gli anime. |
| Musica | **AcoustID** (impronta Chromaprint con `fpcalc.exe`) + **MusicBrainz** (`MusicBrainzAPI`, namespace `Hqub.MusicBrainz`) | Vedi §5 sul perché `fpcalc` invece di AcoustID.NET. |
| Resilienza | **Polly v8**: `AddStandardResilienceHandler` sui client REST; `ResiliencePipeline` esplicita per TMDbLib | Retry esponenziale con jitter, circuit breaker, timeout per tentativo e totale, rispetto di `Retry-After`. |

## 2. Layer e dipendenze

```
Renamr.App            (WinUI 3: Views, XAML, servizi di piattaforma: picker, DPAPI, Esplora File)
   │
   ▼
Renamr.Presentation   (ViewModel, Messenger, interfacce UI. Nessun riferimento a WinUI: testabile)
   │
   ▼
Renamr.Services       (I/O sicuro, TagLib, provider online, Polly, pipeline, impostazioni)
   │
   ▼
Renamr.Core           (dominio puro: modelli, errori, Scene Cleaner, template, scoring, interfacce)
```

Le frecce indicano "dipende da". Core non conosce né il disco né la rete; Services non conosce la UI; Presentation non conosce WinUI.
Così l'80% del codice si compila e si testa su qualunque sistema, e la UI potrebbe essere rifatta in WPF-UI riusando i ViewModel.

## 3. Struttura delle cartelle

```
Renamr.sln
Directory.Build.props        # C# latest, nullable, analizzatori, warning = errori
Directory.Packages.props     # versioni NuGet centralizzate
global.json                  # SDK .NET 10
src/
  Renamr.Core/
    Abstractions/            # IMetadataProvider, IFileNameParser, INameTemplateEngine, ISettingsStore…
    Models/                  # ParsedMediaName, MediaQuery, MediaMetadata, MatchCandidate, RenamePlanEntry…
    Errors/                  # RenamrErrorCode, RenamrError (messaggi leggibili), OperationResult
    Parsing/                 # SceneCleaner + SceneTags (regex generate a compile time)
    Matching/                # TitleSimilarity (Levenshtein + Jaccard), ConfidenceScorer
    Templating/              # NameTemplateEngine (segnaposto, formati, sanificazione NTFS, cartelle)
    Options/                 # RenamrSettings: template, soglie, chiavi
  Renamr.Services/
    IO/                      # PathBoundary, SafeFileOperations, IoErrorClassifier
    Metadata/                # TagLibMetadataWriter/Reader, Mp4HeaderDatePatcher, MatroskaDatePatcher
    Providers/Movies|Tv|Music# TMDb, OMDb, TheTVDB, TVmaze, AniDB, AcoustID, MusicBrainz
    Matching/                # CascadingMetadataResolver (fallback a cascata)
    Resilience/              # pipeline Polly, RequestThrottle (AniDB, MusicBrainz)
    Pipeline/                # MediaScanner → RenamePlanner → RenameExecutor/MediaFileProcessor, RenameJournal
    Settings/                # JsonSettingsStore (chiavi API cifrate)
    ServiceCollectionExtensions.cs
  Renamr.Presentation/
    ViewModels/              # MainViewModel, FileItemViewModel, IssuesViewModel, SettingsViewModel
    Messages/                # FileIssueMessage, RunStartedMessage, RunCompletedMessage
    Services/                # IFolderPickerService, IShellService
  Renamr.App/
    App.xaml(.cs)            # composition root (Generic Host + DI)
    MainWindow.xaml(.cs)     # finestra a 3 fasi
    Views/SettingsDialog     # template con anteprima live, chiavi, soglie
    Helpers/Ui.cs            # funzioni x:Bind (al posto dei converter)
    Services/                # FolderPicker, Shell, DPAPI
    Tools/                   # fpcalc.exe (da aggiungere)
tests/Renamr.Tests/          # 77 test xUnit + file multimediali minuscoli generati con ffmpeg
```

## 4. Flusso

1. **Selezione** — drop-zone o picker. Il percorso viene normalizzato subito (`Path.GetFullPath`) e diventa la radice del `PathBoundary`.
2. **Anteprima** (`RenamePlanner`, nessuna scrittura) — `MediaScanner` elenca i file (senza seguire junction/symlink), `SceneCleaner` estrae titolo/anno/S/E, il `CascadingMetadataResolver` interroga i provider in parallelo limitato, `NameTemplateEngine` calcola il nuovo nome, il `PathBoundary` valida la destinazione, i conflitti (due file sullo stesso nome, file già esistente) diventano errori di riga.
3. **Azione** (`RenameExecutor`, sequenziale) — per ogni file `MediaFileProcessor` esegue una mini-transazione:
   1. boundary check di sorgente e destinazione, usando da lì in poi solo i percorsi normalizzati;
   2. probe del lock con `FileShare.None` (player, client torrent);
   3. rimozione di `ReadOnly` (ripristinato alla fine);
   4. tag interni con TagLib **su copia temporanea + `File.Replace`**, così l'originale non può restare a metà;
   5. `File.Move(overwrite: false)` atomico, registrato subito nel journal (fsync);
   6. sottotitoli/NFO con lo stesso nome seguono il file;
   7. `SetCreationTimeUtc` / `SetLastWriteTimeUtc` **per ultimi**, perché ogni scrittura precedente aggiornerebbe `LastWriteTime`.

   Un errore resta confinato alla riga; la coda continua. La simulazione (Dry Run) esegue i passi 1–2 e il controllo conflitti, poi si ferma.
4. **Annulla** — il journal JSONL in `%LOCALAPPDATA%\Renamr\journal` permette di ripristinare i nomi (anche dopo un crash).

## 5. Decisioni non ovvie

- **Data a mezzogiorno UTC.** `1999-03-31T00:00Z` in Esplora File a New York diventerebbe il 30 marzo; alle 12:00 UTC il giorno è lo stesso in ogni fuso.
- **"Supporto creato" in Esplora File.** Per MP4/MOV la colonna legge `moov/mvhd.creation_time`, per MKV `Segment/Info/DateUTC`: TagLib non li scrive, quindi `Mp4HeaderDatePatcher` e `MatroskaDatePatcher` li aggiornano in place (4–8 byte, il file non cambia dimensione). Se un MKV non ha `DateUTC` non lo si inserisce (servirebbe riscrivere l'header): resta il tag `DATE_RELEASED`.
- **File oltre 4 GB.** Niente riscrittura dei tag (troppo rischiosa e lenta): solo patch dell'intestazione e date del file system, con un avviso.
- **Data per contenitore.** ID3v2.4 `TDRC`+`TDRL`, Vorbis `DATE`+`ORIGINALDATE`, MP4 `©day`, Matroska `DATE_RELEASED`, RIFF `ICRD`, ASF `WM/OriginalReleaseTime`.
- **Path traversal.** Confronto con la radice terminata dal separatore (`D:\Media\` non contiene `D:\MediaPrivati`), rifiuto di ADS (`file:stream`), percorsi di dispositivo, reparse point lungo il percorso; i valori dai database non possono introdurre `/` e un segmento `..` diventa `_`.
- **fpcalc invece di AcoustID.NET.** AcoustID.NET richiede un decoder audio in-process (NAudio/Bass) e non è più mantenuto; `fpcalc.exe` è il binario ufficiale Chromaprint, decodifica tutto via FFmpeg e gira in un processo separato con timeout. Il lookup AcoustID restituisce già gli ID MusicBrainz e le date di release.
- **Release più antica.** Per la musica la data "reale" è quella della prima release ufficiale, non della ristampa, salvo che i tag indichino già l'album.
- **Rate limit.** AniDB (1 richiesta ogni 2 s, dump titoli al massimo una volta al giorno, cache 24 h) e MusicBrainz (1/s) hanno un throttle globale: superarli porta al ban dell'IP.
- **Chiavi API** cifrate con DPAPI legata all'utente Windows; il file `settings.json` non le contiene mai in chiaro.

## 6. Cosa è stato verificato e cosa no

- `Renamr.Core`, `Renamr.Services`, `Renamr.Presentation` e i test **compilano con .NET 10 senza warning** (analizzatori `latest-recommended`, warning trattati come errori).
- **77 test xUnit verdi**: parser, template, similarità e cascata dei provider, boundary check (traversal, prefissi, symlink), lock, ReadOnly, move senza sovrascrittura, date, scrittura tag reale su MP3/FLAC/MP4/MKV, pipeline completa (rinomina + tag + date + sottotitoli, dry run, file bloccato che non ferma la coda, conflitti, cartelle da template, annulla), ViewModel su un "thread UI" simulato, impostazioni cifrate.
- Le date scritte sono state controllate anche con **ffprobe**: `creation_time=1999-03-31T12:00:00Z` su MP4/M4A/MKV, `date=1999-03-31` su MP3.
- I test girano su Linux: `SetCreationTimeUtc` lì non è verificabile (il test lo controlla solo su Windows) e i codici HRESULT Win32 sono coperti da test sul classificatore.
- **Non compilato qui: `Renamr.App` (WinUI 3)**. Il compilatore XAML di Windows App SDK gira solo su Windows. I file XAML sono XML valido e i ViewModel a cui si legano sono compilati e testati, ma la prima build su Windows può richiedere piccoli ritocchi.
- **Provider online non chiamati dal vivo**: nessuna chiave API nel container. La logica di matching è testata con provider finti; gli endpoint seguono la documentazione pubblica di ciascun servizio.

## 7. Build ed esecuzione su Windows

```powershell
# Requisiti: Windows 10 1809+ / 11, .NET 10 SDK, Visual Studio 2026 con "Sviluppo di app WinUI" (oppure solo l'SDK)
dotnet test tests/Renamr.Tests
dotnet build src/Renamr.App -c Release -p:Platform=x64
dotnet publish src/Renamr.App -c Release -r win-x64 -p:Platform=x64 --self-contained
```

Poi: copiare `fpcalc.exe` in `src/Renamr.App/Tools/` per il riconoscimento acustico e inserire le chiavi nelle Impostazioni (icona ingranaggio).

## 8. Estendere

- **Nuovo provider**: implementare `IMetadataProvider` (nome, priorità, tipi supportati, `SearchAsync` che lancia `ProviderException` sugli errori di servizio) e aggiungere una riga in `ServiceCollectionExtensions`.
- **Nuovo segnaposto**: un caso in `NameTemplateEngine.Resolve`.
- **Nuovo formato contenitore**: un ramo in `TagLibMetadataWriter.ApplyTags`.
