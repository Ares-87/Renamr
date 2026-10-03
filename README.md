# Renamr

Riconosce e rinomina film, serie TV, anime e musica usando TMDb, OMDb, TheTVDB, TVmaze, AniDB, AcoustID e MusicBrainz,
e allinea date del file system e metadati interni alla data di uscita reale.
La modalità **Rinomina file** rinomina invece qualunque gruppo di file con regole (numerazione, sostituzioni, maiuscole, modelli con data e cartella…), senza toccare metadati e date.

- Architettura, decisioni e stato delle verifiche: [docs/ARCHITETTURA.md](docs/ARCHITETTURA.md)
- Stack: .NET 10 · WinUI 3 (Windows App SDK 2.5) · Avalonia 12 + FluentAvalonia per Linux · CommunityToolkit.Mvvm · TagLibSharp · TMDbLib · Polly

In Visual Studio: apri `Renamr.sln` e premi F5. Il progetto di avvio è già `Renamr.App` su piattaforma x64 (ARM64 disponibile).

La versione compare nella barra del titolo ("Renamr v1.6.0", il commit nel tooltip) e sta in `Directory.Build.props`: ogni pull request la aumenta.

Da riga di comando:

```powershell
dotnet test tests/Renamr.Tests
dotnet build src/Renamr.App -c Release -p:Platform=x64
```

Su Linux (stessa interfaccia, vedi [§11 di ARCHITETTURA.md](docs/ARCHITETTURA.md#11-versione-linux)):

```bash
dotnet run --project src/Renamr.Linux
dotnet publish src/Renamr.Linux -c Release -r linux-x64 -o out/linux
```
