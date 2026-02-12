# Releasing AI Weather Plugin

## Automated Release (recommended)

The GitHub Action at [.github/workflows/build-and-release.yaml](.github/workflows/build-and-release.yaml) handles everything automatically using the **official** [CreateManifest.ps1](https://github.com/isbeorn/nina.plugin.manifests/blob/main/tools/CreateManifest.ps1) from the NINA plugin manifests repo.

### One-time setup

1. **Fork** [isbeorn/nina.plugin.manifests](https://github.com/isbeorn/nina.plugin.manifests) — keep the repo name `nina.plugin.manifests`.
2. Create a **Personal Access Token** with write access to your fork.
3. Add the token as a repository secret named `PAT` in this plugin repo's Settings → Secrets.

### Publishing a release

1. Update the version in [Properties/AssemblyInfo.cs](Properties/AssemblyInfo.cs):
   ```csharp
   [assembly: AssemblyVersion("1.3.0.0")]
   [assembly: AssemblyFileVersion("1.3.0.0")]
   ```

2. Commit, tag, and push:
   ```bash
   git add -A
   git commit -m "Release 1.3.0.0"
   git tag 1.3.0.0
   git push origin main --tags
   ```

3. The GitHub Action will:
   - Build the plugin in Release mode
   - Run the official `CreateManifest.ps1` against the compiled DLL (reads **all** metadata including `FeaturedImageURL`, `LongDescription`, etc.)
   - Create a GitHub Release with the ZIP + manifest attached
   - Open a PR to `isbeorn/nina.plugin.manifests` with the manifest (if your fork + PAT are set up)

4. Wait for the manifest PR to be reviewed and merged. Once merged, the plugin (with icon!) appears in NINA's Plugin Manager.

## Local build (optional)

For testing locally before pushing a tag:

```powershell
.\prepare-nina-release.ps1            # beta (default)
.\prepare-nina-release.ps1 -Beta:$false  # release channel
```

This downloads and runs the same official `CreateManifest.ps1`. Output goes to `release/`.

## Plugin metadata

All manifest fields are read from [Properties/AssemblyInfo.cs](Properties/AssemblyInfo.cs) — the official `CreateManifest.ps1` extracts them from the compiled DLL via PE metadata. Key attributes:

| Attribute | Purpose |
|---|---|
| `AssemblyTitle` | Plugin name |
| `Guid` | Plugin identifier |
| `AssemblyFileVersion` | Plugin version |
| `AssemblyCompany` | Author |
| `AssemblyDescription` | Short description |
| `AssemblyMetadata("FeaturedImageURL", ...)` | **Plugin icon** in NINA |
| `AssemblyMetadata("LongDescription", ...)` | Detailed description |
| `AssemblyMetadata("Repository", ...)` | GitHub repo URL |
| `AssemblyMetadata("License", ...)` | License type |
| `AssemblyMetadata("Tags", ...)` | Search tags |
| `AssemblyMetadata("MinimumApplicationVersion", ...)` | Min NINA version |

## Local deploy (development)

```powershell
.\deploy.ps1
```

Builds, copies to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\AIWeather\`, and restarts NINA.
