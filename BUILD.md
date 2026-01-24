# All Sky Camera Plugin - Build and Deployment Guide

## Prerequisites

1. **Visual Studio 2022** or later with:
   - .NET 8.0 SDK
   - C# workload
   - WPF workload

2. **NINA Installation**
   - Download and install NINA from [nighttime-imaging.eu](https://nighttime-imaging.eu/)
   - Note the installation path (usually `C:\Program Files\N.I.N.A\`)

3. **Update Project References**
   - Open `AllSkyCameraPlugin.csproj`
   - Verify the NINA assembly paths match your installation
   - Update `$(ProgramFiles)\N.I.N.A\` if NINA is installed elsewhere

## Building the Plugin

### Option 1: Visual Studio

1. Open `AllSkyCameraPlugin.csproj` in Visual Studio
2. Right-click the project → **Restore NuGet Packages**
3. Build → **Build Solution** (or press `Ctrl+Shift+B`)
4. Built files will be in `bin\Debug\net8.0-windows\` or `bin\Release\net8.0-windows\`

### Option 2: Command Line

```powershell
# Restore dependencies
dotnet restore

# Build in Debug mode
dotnet build -c Debug

# Build in Release mode
dotnet build -c Release
```

## Deployment

### Manual Deployment

1. Build the plugin in Release mode
2. Copy all files from `bin\Release\net8.0-windows\` to:
   ```
   %LOCALAPPDATA%\NINA\Plugins\AllSkyCameraPlugin\
   ```
3. Restart NINA
4. Go to **Options → Plugins** to configure

### Automated Deployment Script

Create a `deploy.ps1` file:

```powershell
# Build the plugin
dotnet build -c Release

# Create deployment package
$pluginDir = "$env:LOCALAPPDATA\NINA\Plugins\AllSkyCameraPlugin"
if (!(Test-Path $pluginDir)) {
    New-Item -ItemType Directory -Path $pluginDir -Force
}

# Copy files
Copy-Item -Path "bin\Release\net8.0-windows\*" -Destination $pluginDir -Recurse -Force

Write-Host "Plugin deployed successfully to: $pluginDir"
Write-Host "Please restart NINA to load the plugin."
```

Run with:
```powershell
.\deploy.ps1
```

## Testing

### Unit Testing (Optional)

Create a test project:

```powershell
dotnet new xunit -n AllSkyCameraPlugin.Tests
dotnet add AllSkyCameraPlugin.Tests reference AllSkyCameraPlugin.csproj
```

### Integration Testing with NINA

1. Deploy the plugin to NINA
2. Start NINA
3. Check **Help → Logs** for any errors
4. Navigate to **Equipment → Safety Monitor**
5. Look for "All Sky Camera Safety Monitor"
6. Test connection with your RTSP camera

## Common Build Issues

### Issue: NINA assemblies not found

**Solution**: Update the NINA assembly paths in `.csproj`:

```xml
<Reference Include="NINA.Core">
  <HintPath>C:\YOUR\NINA\PATH\NINA.Core.dll</HintPath>
</Reference>
```

### Issue: NuGet restore fails

**Solution**: Clear NuGet cache:
```powershell
dotnet nuget locals all --clear
dotnet restore
```

### Issue: Emgu.CV runtime errors

**Solution**: Ensure `Emgu.CV.runtime.windows` package is installed and the native DLLs are being copied to output.

## Creating a Release Package

1. Build in Release mode
2. Create a ZIP file with the following structure:
   ```
   AllSkyCameraPlugin-v1.0.0.zip
   ├── AllSkyCameraPlugin.dll
   ├── Emgu.CV.dll
   ├── Microsoft.ML.dll
   ├── [other dependencies]
   └── README.txt (installation instructions)
   ```

3. Include a README.txt with installation steps

### PowerShell Script for Packaging

```powershell
$version = "1.0.0"
$outputZip = "AllSkyCameraPlugin-v$version.zip"
$buildPath = "bin\Release\net8.0-windows"

# Build
dotnet build -c Release

# Create package
Compress-Archive -Path "$buildPath\*" -DestinationPath $outputZip -Force

Write-Host "Release package created: $outputZip"
```

## Debugging

### Attach to NINA Process

1. Start NINA
2. In Visual Studio: **Debug → Attach to Process**
3. Find `NINA.exe` and attach
4. Set breakpoints in your plugin code
5. Trigger plugin functionality in NINA

### Enable Verbose Logging

Add to your code:
```csharp
Logger.SetLogLevel(LogLevelEnum.DEBUG);
```

Check logs at:
```
%LOCALAPPDATA%\NINA\Logs\
```

## CI/CD Setup (GitHub Actions)

Create `.github/workflows/build.yml`:

```yaml
name: Build Plugin

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: windows-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 8.0.x
    
    - name: Restore dependencies
      run: dotnet restore
    
    - name: Build
      run: dotnet build -c Release --no-restore
    
    - name: Create package
      run: Compress-Archive -Path bin/Release/net8.0-windows/* -DestinationPath AllSkyCameraPlugin.zip
    
    - name: Upload artifact
      uses: actions/upload-artifact@v3
      with:
        name: plugin-package
        path: AllSkyCameraPlugin.zip
```

## Next Steps

1. Test thoroughly with your all-sky camera setup
2. Optimize AI analysis algorithms based on your camera's characteristics
3. Consider training custom ML models for your specific sky conditions
4. Share with the NINA community!

## Support Resources

- [NINA Plugin Documentation](https://nighttime-imaging.eu/docs/master/site/plugins/)
- [NINA GitHub Repository](https://github.com/Nighttime-Imaging/N.I.N.A/)
- [NINA Discord Community](https://discord.gg/nighttime-imaging)
