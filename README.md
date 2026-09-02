# Azurite Tray

Azurite Tray is a Windows notification-area application that controls a local
[Azurite](https://github.com/Azure/Azurite) instance.

The application can use Azurite from these sources:

- A global npm installation
- A Visual Studio installation

You can select the source from the tray menu. The application saves this selection
in `%LOCALAPPDATA%\AzuriteTray\source.txt`.

## Features

- Start and stop Azurite from the Windows notification area
- Change between npm and Visual Studio installations
- Find Visual Studio installations with `vswhere.exe`
- Stop the active Azurite process when the application exits
- Publish as a Native AOT application for `win-x64` and `win-arm64`

Azurite stores its data in `C:\azurite`. The debug log is
`C:\azurite\debug.log`.

## Requirements

- Windows
- .NET 10 SDK
- Visual Studio C++ build tools for Native AOT publishing
- One supported Azurite installation

For npm, install Node.js and Azurite:

```powershell
npm install --global azurite
```

For Visual Studio, install the Azure development workload. The application uses
`vswhere.exe` when it is available.

## Publish

Publish the x64 application:

```powershell
dotnet publish source\AzuriteTray.App\AzuriteTray.App.csproj --configuration Release --runtime win-x64
```

Publish the ARM64 application:

```powershell
dotnet publish source\AzuriteTray.App\AzuriteTray.App.csproj --configuration Release --runtime win-arm64
```

## License

This project uses the [MIT License](LICENSE).
