# H1Z1 Pre-Season 5

I'm recreating the server for H1Z1: King of the Kill's August 2017 client, before the Combat Update.

It's still a work in progress, with bugs and missing features. If you want to help, bug reports and pull requests are welcome.

[Gameplay 1](https://www.youtube.com/watch?v=qL_Sh2vITYw) · [Gameplay 2](https://www.youtube.com/watch?v=W2wlguQvdXQ)

## Play

Grab the Windows launcher/server ZIP from [Releases](https://github.com/Mixcria/h1z1-pre-season-5-project/releases).

1. Extract it and open `Cranberry.Launcher.exe`.
2. Create a local account, leaving the administrator option selected.
3. Click **Install / repair**, then **Play**.

Requires Windows 10/11 x64. The game download is about 14.6 GB.

The current download runs locally on your PC. LAN play isn't supported yet.

## Build

Requires Windows and the .NET 10 SDK.

```powershell
./Build-Local.ps1 -Output ./artifacts/Cranberry-Local -Zip
dotnet test ./server/Cranberry.slnx -c Release
```

[Player guide](PLAYER-GUIDE.txt) · [Contributing](CONTRIBUTING.md) · [Credits](THIRD-PARTY-NOTICES.md)
