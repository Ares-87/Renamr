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
| Serie / anime | **TMDbLib** (TMDb TV), **TheTVDB v4**, **TVmaze**, **AniDB** via HttpClient | AniDB prima di tutti per gli anime, poi TMDb (stessa chiave dei film), TheTVDB se c'è la chiave, TVmaze senza chiave. |
| Musica | **AcoustID** (impronta Chromaprint con `fpcalc.exe`) + **MusicBrainz** (`MusicBrainzAPI`, namespace `Hqub.MusicBrainz`) | Vedi §5 sul perché `fpcalc` invece di AcoustID.NET. |
| Resilienza | **Polly v8**: `AddStandardResilienceHandler` sui client REST; `ResiliencePipeline` esplicita per TMDbLib | Retry esponenziale con jitter, circuit breaker, timeout per tentativo e totale, rispetto di `Retry-After`. |

## 2. Layer e dipendenze

```
Renamr.App            (WinUI 3: Views, XAML, servizi di piattaforma: picker, DPAPI, Esplora File)
Renamr.Linux          (Avalonia + FluentAvalonia: stesse Views per Linux, chiave AES-GCM, file manager XDG)
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
    Providers/Movies|Tv|Music# TMDb (film e serie), OMDb, TheTVDB, TVmaze, AniDB, AcoustID, MusicBrainz
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
   7. data di creazione (`FileCreationTime`, dove il file system lo permette) e `SetLastWriteTimeUtc` **per ultimi**, perché ogni scrittura precedente aggiornerebbe `LastWriteTime`.

   Un errore resta confinato alla riga; la coda continua. La simulazione (Dry Run) esegue i passi 1–2 e il controllo conflitti, poi si ferma.
4. **Annulla** — il journal JSONL in `%LOCALAPPDATA%\Renamr\journal` permette di ripristinare i nomi (anche dopo un crash).

## 5. Decisioni non ovvie

- **Data a mezzogiorno UTC.** `1999-03-31T00:00Z` in Esplora File a New York diventerebbe il 30 marzo; alle 12:00 UTC il giorno è lo stesso in ogni fuso.
- **"Supporto creato" in Esplora File.** Per MP4/MOV la colonna legge `moov/mvhd.creation_time`, per MKV `Segment/Info/DateUTC`: TagLib non li scrive, quindi `Mp4HeaderDatePatcher` e `MatroskaDatePatcher` li aggiornano in place (4–8 byte, il file non cambia dimensione). Se un MKV non ha `DateUTC` non lo si inserisce (servirebbe riscrivere l'header): resta il tag `DATE_RELEASED`.
- **File oltre 4 GB.** Niente riscrittura dei tag (troppo rischiosa e lenta): solo patch dell'intestazione e date del file system. Se la data nell'intestazione è stata aggiornata non c'è avviso; altrimenti un avviso E303 spiega cosa manca.
- **Metadati interni opzionali.** La casella "Scrivi metadati interni" (ricordata nelle impostazioni, `Output.WriteEmbeddedMetadata`) spegne la scrittura dei tag e dell'intestazione: restano nome e date del file system.
- **File.Replace non supportato** (alcuni dischi di rete, exFAT/FAT32): l'originale viene spostato da parte, la copia messa al suo posto, il vecchio cancellato solo alla fine.
- **Data per contenitore.** ID3v2.4 `TDRC`+`TDRL`, Vorbis `DATE`+`ORIGINALDATE`, MP4 `©day`, Matroska `DATE_RELEASED`, RIFF `ICRD`, ASF `WM/OriginalReleaseTime`.
- **Path traversal.** Confronto con la radice terminata dal separatore (`D:\Media\` non contiene `D:\MediaPrivati`), rifiuto di ADS (`file:stream`), percorsi di dispositivo, reparse point lungo il percorso; i valori dai database non possono introdurre `/` e un segmento `..` diventa `_`.
- **fpcalc invece di AcoustID.NET.** AcoustID.NET richiede un decoder audio in-process (NAudio/Bass) e non è più mantenuto; `fpcalc.exe` è il binario ufficiale Chromaprint, decodifica tutto via FFmpeg e gira in un processo separato con timeout. Il lookup AcoustID restituisce già gli ID MusicBrainz e le date di release.
- **Release più antica.** Per la musica la data "reale" è quella della prima release ufficiale, non della ristampa, salvo che i tag indichino già l'album.
- **Rate limit.** AniDB (1 richiesta ogni 2 s, dump titoli al massimo una volta al giorno, cache 24 h) e MusicBrainz (1/s) hanno un throttle globale: superarli porta al ban dell'IP.
- **Chiavi API** cifrate con DPAPI legata all'utente Windows (su Linux AES-GCM con una chiave in `~/.local/share/Renamr/secret.key`, permessi 600); il file `settings.json` non le contiene mai in chiaro.

## 6. Cosa è stato verificato e cosa no

- `Renamr.Core`, `Renamr.Services`, `Renamr.Presentation` e i test **compilano con .NET 10 senza warning** (analizzatori `latest-recommended`, warning trattati come errori).
- **114 test xUnit verdi**: parser, template, similarità e cascata dei provider, boundary check (traversal, prefissi, symlink), lock, ReadOnly, move senza sovrascrittura, date, scrittura tag reale su MP3/FLAC/MP4/MKV, pipeline completa (rinomina + tag + date + sottotitoli, dry run, file bloccato che non ferma la coda, conflitti, cartelle da template, annulla), ViewModel su un "thread UI" simulato, impostazioni cifrate.
- Le date scritte sono state controllate anche con **ffprobe**: `creation_time=1999-03-31T12:00:00Z` su MP4/M4A/MKV, `date=1999-03-31` su MP3.
- I test girano su Linux. La data di creazione su NTFS montato con ntfs-3g è verificata su un volume NTFS vero (immagine creata con `mkntfs`, test con `RENAMR_NTFS_TEST_DIR`, controllo nella MFT con `ntfsinfo`); i codici HRESULT Win32 sono coperti da test sul classificatore.
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

## 8. Fonti dei dati e lingua dei titoli

| Fonte | Chiave | Titoli nella lingua scelta |
|---|---|---|
| TMDb (film) | gratuita, registrazione | sì (titolo e trama tradotti dalla community) |
| TMDb (serie, anime) | la stessa dei film | sì, serie ed episodi; se un episodio non è tradotto si usa il titolo inglese |
| OMDb (film) | gratuita con limiti | no, solo inglese |
| TheTVDB (serie) | registrazione | sì, traduzioni di serie ed episodi (`/translations/ita`) |
| TVmaze (serie, anime) | nessuna | solo il titolo della serie, dagli AKA del paese; episodi in inglese, quindi se il nome file contiene già il titolo dell'episodio ("Silo S03E01 Chi sei tu") si tiene quello |
| AniDB (anime) | client gratuito | titolo ufficiale nella lingua se esiste, altrimenti romaji |
| MusicBrainz (musica) | nessuna | non applicabile |
| AcoustID (musica) | gratuita | non applicabile |

La chiave TMDb può essere la "Chiave API" o il "Token di accesso in lettura" (la chiave viene estratta dal token). Se un database rifiuta la chiave o non risponde, l'anteprima lo dice con un avviso invece di passare in silenzio alla fonte successiva.

La lingua si sceglie in Impostazioni ("it-IT"), dal selettore in basso o dal menu contestuale della lista; ogni provider la converte nel formato che usa (`LanguagePreference`). Cambiarla rifà subito le ricerche. Senza chiave TMDb l'app lo segnala con un avviso nell'anteprima, perché i titoli degli episodi resterebbero in inglese.

## 9. Scelte rapide e versione

- **Tasto destro sulla lista**: formato del nome (preset in `TemplatePresets`, con anteprima sulla riga nel tooltip), lingua dei titoli, copia del nuovo nome, Esplora File. Un formato scelto diventa il template del tipo e i nomi si ricalcolano in locale (`RenamePlanner.Rerender`), senza nuove ricerche online.
- **Versione**: `<Version>` in `Directory.Build.props`, mostrata come "Renamr v1.5.0" nella barra del titolo (tooltip con il commit). Si aumenta a ogni pull request.

## 10. Estendere

- **Nuovo provider**: implementare `IMetadataProvider` (nome, priorità, tipi supportati, `SearchAsync` che lancia `ProviderException` sugli errori di servizio) e aggiungere una riga in `ServiceCollectionExtensions`.
- **Nuovo segnaposto**: un caso in `NameTemplateEngine.Resolve`.
- **Nuovo formato contenitore**: un ramo in `TagLibMetadataWriter.ApplyTags`.

## 11. Versione Linux

`src/Renamr.Linux` è la stessa app per Linux: Avalonia 12 con il tema e i controlli di **FluentAvalonia** (gli stessi pennelli, InfoBar, ProgressRing, ContentDialog e menu di WinUI), così la finestra ha la stessa struttura, gli stessi testi e lo stesso flusso di `Renamr.App`. ViewModel, servizi e pipeline sono quelli condivisi; il progetto contiene solo Views e servizi di piattaforma. Uno Platform è stato scartato perché avrebbe richiesto di riscrivere anche la parte Windows per ottenere lo stesso risultato.

```bash
# Requisiti: .NET 10 SDK. Per la musica: sudo apt install libchromaprint-tools (fornisce fpcalc)
dotnet build Renamr.Linux.slnf
dotnet run --project src/Renamr.Linux               # oppure: dotnet run --project src/Renamr.Linux -- /media/disco
dotnet publish src/Renamr.Linux -c Release -r linux-x64 -o out/linux   # cartella autonoma, avvio con ./out/linux/Renamr
```

**Date su Linux.** Il kernel non ha una chiamata per cambiare la data di creazione: `utimensat` modifica solo accesso e modifica, e su Linux .NET ripiega `SetCreationTimeUtc` sulla data di modifica. `FileCreationTime` scrive quindi la data di creazione solo dove un driver la espone come attributo esteso:

| File system | Data di creazione | Come |
|---|---|---|
| NTFS con **ntfs-3g** (dischi esterni di Windows) | sì, nella MFT, Windows la vede | `system.ntfs_crtime` (FILETIME) |
| Cartelle di rete **SMB/CIFS** | sì, sul server | `user.cifs.creationtime`, solo su volumi CIFS/SMB2 (non verificato dal vivo) |
| NTFS con il driver del kernel `ntfs3` | no, per quanto noto (non verificato dal vivo) | il driver espone solo gli attributi DOS: montare con ntfs-3g |
| ext4, Btrfs, XFS, exFAT, FAT32 | no | resta la data reale |

La data di modifica viene sempre impostata; i metadati interni (tag, `DateUTC` di MKV, `mvhd` di MP4) si scrivono come su Windows. Quando la cartella aperta sta su un disco che non permette di cambiare la data di creazione, l'anteprima lo dice con un avviso.

**Altre differenze.** Il controllo dei file bloccati usa i lock di Linux, che i player in genere non prendono: su Linux rinominare un file aperto è comunque sicuro, perché il programma continua a leggere lo stesso file. "Mostra nella cartella" usa l'interfaccia D-Bus `org.freedesktop.FileManager1` (Nautilus, Dolphin, Nemo) con ripiego su `xdg-open`.

## 12. Modalità "Rinomina file"

In alto nella finestra un selettore sceglie tra **Film e serie** (tutto quanto descritto sopra) e **Rinomina file**: qualunque gruppo di file, rinominato con regole decise dall'utente. Lo stesso flusso in 3 fasi (cartella, anteprima, azione con Dry Run e Annulla), ma **solo il nome cambia**: nessuna ricerca online, nessun metadato interno, nessuna data. Musica, film e serie riconosciuti restano nella prima modalità con il comportamento di sempre.

- **Regole** (`Renamr.Core/BatchRename`): record immutabili applicati in ordine al nome senza estensione. Numerazione (inizio, passo, cifre automatiche o fisse, separatore, all'inizio/alla fine/al posto del nome, ripartenza per cartella), nuovo nome da modello, sostituisci testo (anche con espressioni regolari e `$1`, tutte le volte o solo la prima/l'ultima), sostituzioni multiple, aggiungi testo (anche a N caratteri dalla fine o prima/dopo un testo), rimuovi caratteri (per posizione o per tipo), sposta testo, scambia parti, rinumera un numero già presente, rifila, nomi da elenco, maiuscole/minuscole (anche invertite), pulizia (separatori, parentesi, accenti, cifre), lettere in numeri ed estensione. Le regole aggiunte nella v1.15 (spunto: i metodi di Advanced Renamer) stanno in `BatchRulesMore.cs`. Ogni regola si può spegnere, spostare o togliere.
- **Segnaposto** (`BatchTokens`): `{nome}`, `{originale}`, `{n}`/`{n:000}`, `{cartella}`, `{data}`/`{data:dd-MM-yyyy}`, `{ora}`, `{creazione}`, `{estensione}`. I due punti nelle date diventano punti perché Windows non li ammette.
- **Ordine e file**: ordine naturale per nome ("2" prima di "10"), data di modifica o creazione, dimensione, estensione, anche inverso; filtro per estensioni o modelli (`jpg png`, `IMG_*`) e sottocartelle facoltative. I file nascosti sono esclusi.
- **Anteprima**: `BatchRenameEngine` è puro calcolo e `BatchRenamePlanner.Plan` riusa l'elenco già letto, quindi la tabella si aggiorna mentre si scrive (150 ms dopo l'ultimo tasto, righe aggiornate al loro posto). I nomi sono validati con le regole di Windows anche su Linux (i dischi esterni sono spesso NTFS).
- **Conflitti**: due file non possono finire sullo stesso nome, e un file che resta com'è occupa il suo. Un file che verrà rinominato invece libera il nome: le rinumerazioni "1 ➔ 2, 2 ➔ 3" sono lecite. `BatchRenameExecutor` rinomina prima chi ha la destinazione libera (le catene si sciolgono da sole) e manda i cicli veri ("A ➔ B, B ➔ A") su un nome temporaneo `.renamr-…`. Ogni spostamento va nel journal, quindi Annulla funziona come per i film.
- **Memoria**: regole, filtro, ordine e ultima modalità stanno in `batch-rename.json` accanto a `settings.json` (`BatchRenameStore`), separati dalle chiavi API cifrate.

## 13. Lingua dell'interfaccia

- Lingue: italiano, inglese, spagnolo, francese, tedesco, portoghese. I testi stanno in `Renamr.Core/Localization/*.json`
  (incorporati nell'assembly); `Strings.Current` li espone come proprietà (`Strings.Keys.cs`, una riga per chiave).
- All'avvio vale la lingua scelta in Impostazioni ➔ Interfaccia (`interface.json`, accanto a settings.json), altrimenti
  quella del sistema, altrimenti l'inglese. È distinta dalla "lingua dei titoli" dei database.
- Viste: in WinUI `{x:Bind loc:Strings.Current.Chiave}`, in Avalonia `{l:Tr Chiave}`. Si leggono una volta: cambiando
  lingua la finestra si ricrea con lo stesso ViewModel (cartella, regole e anteprima restano).
- Per aggiungere un testo: la chiave in tutti i file JSON e una riga in `Strings.Keys.cs`. `LocalizationTests` controlla
  che le lingue abbiano le stesse chiavi e gli stessi segnaposto `{0}`, e che le viste usino solo chiavi esistenti.
- I segnaposto dei modelli di "Rinomina file" si mostrano in italiano ({nome}, {cartella}…) o in inglese ({name}, {folder}…);
  entrambi funzionano sempre.
- In modalità "Rinomina file" le Impostazioni mostrano solo l'interfaccia: formati, chiavi API e riconoscimento riguardano
  solo "Film, Serie e Musica".

## 14. Logo, icona e layout

- Sorgenti in `assets/brand`: `renamr-logo.svg` (navy, tema chiaro), `renamr-logo-dark.svg` (bianco, tema scuro), `renamr-icon.svg` (simbolo quadrato con la "m" stilizzata), i PNG esportati e `renamr.ico` multi-risoluzione, forniti da Daniele. Le immagini delle app (`src/*/Assets`: `logo-*`, `wordmark-*`, `renamr-icon.png`, `renamr.ico`) si ricavano da questi file con ImageMagick (logo alto 144 px, wordmark senza sottotitolo alto 80 px). Colori: navy `#0F1B3D`, blu accento `#2F6BFF` (anche colore d'accento dell'app Linux).
- Le due app usano PNG ricavati da queste sorgenti (`Assets/`): logo grande nella schermata iniziale, solo scritta nella barra del titolo, scelti per tema (`ThemeDictionaries` in WinUI, `Resources.ThemeDictionaries` in Avalonia). `renamr.ico` è l'icona di Renamr.exe e della finestra Windows; `renamr-icon.png` quella della finestra Linux.
- Layout: in alto logo e versione, cartella aperta a destra; sotto, le due modalità e l'unico pulsante Impostazioni, sempre visibile; poi il contenuto e la barra azione.

## 15. README e release

- Il `README.md` è in inglese (scelta di Daniele) con tre screenshot Windows e tre Linux in `docs/screenshots`
  (`windows-*.png`, `linux-*.png`: inizio, anteprima film, Rinomina file), interfaccia in inglese su una libreria
  dimostrativa con soli titolo e anno. Questo documento resta in italiano.
- Gli screenshot Windows li rifà `.github/workflows/screenshots.yml` su un runner Windows (a mano, o a ogni push su un
  branch che tocca `build/screenshots`) e li committa sul branch. I database online sono sostituiti da
  `build/screenshots/mock.py`, un proxy HTTPS con risposte fisse; `build/screenshots/windows.ps1` crea i file di
  esempio e apre l'app con la cartella come argomento (`Renamr.exe D:\Film`). Gli screenshot Linux si fanno allo
  stesso modo con lo stesso proxy, sotto Xvfb.
- `.github/workflows/release.yml` compila e testa su runner Windows e Linux a ogni pull request e push su `main`
  (è anche la prima compilazione automatica di `Renamr.App`). Un tag `vX.Y.Z` uguale a `<Version>` in
  `Directory.Build.props` pubblica una release con `Renamr-X.Y.Z-win-x64.zip`, `-win-arm64.zip`, `-linux-x64.tar.gz` e
  `-linux-arm64.tar.gz`; GitHub aggiunge da solo gli archivi del codice sorgente. Un tag diverso dalla versione ferma il workflow.
- `fpcalc` non è incluso nei pacchetti: su Windows va copiato in `Tools\`, su Linux arriva da `libchromaprint-tools`.
- **File unico (v1.11.0).** Oltre a zip e tar.gz la release ha `Renamr-X.Y.Z-win-x64.exe`/`-win-arm64.exe` (WinUI
  self-contained con `PublishSingleFile` e `IncludeAllContentForSelfExtract`: al primo avvio si scompatta in una cartella
  temporanea, per questo `fpcalc.exe` si cerca anche accanto all'eseguibile vero) e `Renamr-X.Y.Z-linux-x64.AppImage`/
  `-linux-arm64.AppImage` (`build/linux/make-appimage.sh`). La CI avvia l'exe x64 (`build/windows/smoke-test.ps1`) e
  l'AppImage x64 e fallisce se non si apre la finestra.

## 16. Elenco dei file e scelta manuale (v1.13.0)

- **File singoli.** Oltre alla cartella si possono scegliere o trascinare file singoli, anche da cartelle diverse
  (`AddFilesCommand`). Ogni riga ha la sua cartella-recinto (`RenamePlanEntry.Root`, `PlanSource`): la cartella aperta se il
  file ci sta dentro, altrimenti la cartella del file. Il `PathBoundary` resta stretto per ogni file e il journal registra
  il recinto di ogni spostamento, così "Annulla" ricontrolla anche i file di altre cartelle. Con un'anteprima già pronta
  si analizzano solo i file nuovi.
- **Togli dall'elenco.** La X a destra della riga (o il menu contestuale) esclude il file; resta fuori anche rifacendo l'analisi
  finché non si apre un'altra cartella o si torna alla schermata iniziale. In "Rinomina file" la numerazione si ricalcola.
- **Scelta della corrispondenza.** Il planner conserva in `RenamePlanEntry.Candidates` tutti i risultati dei database, anche
  quelli scartati perché sotto la soglia minima. Cliccando una riga di film, serie o musica (o "Scegli…" sotto lo stato delle
  righe incerte o senza risultato) si apre `MatchPickerViewModel`: risultati già trovati più una ricerca libera (titolo, anno,
  tipo, stagione/episodio) che con `IMetadataResolver.SearchAllAsync` interroga tutti i database adatti senza fermarsi al
  primo. La scelta (`RenamePlanner.ApplyMatch`) vale come confermata (`ManualMatch`): si rinomina anche senza
  "Includi bassa confidenza".
- **Già corretto.** Le righe `Unchanged` non entrano nell'esecuzione né nel conteggio dell'avanzamento
  (`RenamePlanEntry.NeedsWork`): niente tag né date riscritti su quei file.
