# Renamr

Riconosce e rinomina film, serie TV, anime e musica usando TMDb, OMDb, TheTVDB, TVmaze, AniDB, AcoustID e MusicBrainz,
e allinea date del file system e metadati interni alla data di uscita reale.

- Architettura, decisioni e stato delle verifiche: [docs/ARCHITETTURA.md](docs/ARCHITETTURA.md)
- Stack: .NET 10 · WinUI 3 (Windows App SDK 1.8) · CommunityToolkit.Mvvm · TagLibSharp · TMDbLib · Polly

In Visual Studio: apri `Renamr.sln` e premi F5. Il progetto di avvio è già `Renamr.App` su piattaforma x64 (ARM64 disponibile).

Da riga di comando:

```powershell
dotnet test tests/Renamr.Tests
dotnet build src/Renamr.App -c Release -p:Platform=x64
```
