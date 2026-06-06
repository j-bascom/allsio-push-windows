# Allsio Push — Project Notes

## Deploying a Release to GitHub

### 1. Bump the version

Update the version string in **two places** — they must match:

- `AllsioPush.App/AllsioPush.App.csproj` — three lines:
  ```xml
  <Version>2026.6.X</Version>
  <AssemblyVersion>2026.6.X.0</AssemblyVersion>
  <FileVersion>2026.6.X.0</FileVersion>
  ```
- `build-release.ps1` — one line:
  ```powershell
  $version = "2026.6.X"
  ```

Version format is `YYYY.M.patch` (e.g. `2026.6.15`). No leading zeros on month or patch.

### 2. Commit and push

```powershell
git add -A
git commit -m "chore: bump version to 2026.6.X"
git push origin main
```

### 3. Build the release package

```powershell
.\build-release.ps1
```

This runs `dotnet publish` (self-contained, win-x64) then `vpk pack`, producing:

```
releases/
  AllsioPush-2026.6.X-full.nupkg   # delta update package
  AllsioPush-win-Portable.zip       # portable zip
  AllsioPush-win-Setup.exe          # installer
  releases.win.json                 # Velopack update feed
  assets.win.json
  RELEASES
```

### 4. Upload to GitHub and publish

Read the GH_TOKEN from the registry (it is not in the shell environment):

```powershell
$token = (Get-ItemProperty -Path 'HKCU:\Environment' -Name 'GH_TOKEN').GH_TOKEN

vpk upload github `
    --repoUrl https://github.com/j-bascom/allsio-push-windows `
    --publish `
    --releaseName "v2026.6.X" `
    --tag "v2026.6.X" `
    --outputDir releases `
    --token $token
```

This creates a GitHub release tagged `vX.X.X`, uploads the three release assets plus the Velopack feed files, and publishes it immediately. Installed copies of the app will auto-update within 4 hours (or via tray → Check for Updates).
