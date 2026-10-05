# MagnetometerSystem

[简体中文](README.md) · [Download V0.5.0](https://github.com/YIALU/MagnetometerSystem/releases/tag/v0.5.0) · [User guide](docs/user-guide.en.md) · [Changelog](docs/变更日志.md)

**MagnetometerSystem is a general-purpose magnetometer desktop application for everyday debugging and experimental recording.** Connect a device through serial or TCP, receive data using a user-defined protocol, automatically save original readings, and plot live curves.

The project centers on **customizable protocols, data recording, and live plotting**. Protocol configuration defines channel counts, names, and units, making it possible to work with different data formats and observe magnetic, temperature, and other auxiliary channels together. Correction and advanced analysis are optional tools.

## Features

| Feature | Capabilities |
| --- | --- |
| **Device connections** | Serial and TCP, configurable connection parameters, connection status, and raw communication inspection |
| **Custom protocols** | ASCII or binary formats, field mappings, frame structure, byte order, scaling, checksums, channel units, and protocol JSON import/export |
| **Automatic recording** | Original readings organized into sessions, with received counts, saved counts, and storage status |
| **Live plots** | A combined plot or multiple plots in one/two columns; temperature and other channels with different units use separate axes in the combined plot |
| **Plot workspace** | Collapsible connection, channel, communication, and analysis panels, plus focus mode and layout restoration |
| **Channels and calculations** | Visibility, colors, order, display offsets, and total-field, gradient, or formula channels |
| **Statistics and analysis** | Rolling statistics, interval selection and analysis, moving-average and median display filters |
| **Device commands** | Protocol-associated command groups, parameterized commands, ASCII/HEX sending, and communication logs |
| **Historical replay** | Session queries and timestamp-based playback with speed control, pause, and seek |
| **CSV export** | Time and channel selection, export precision, original values, and saved correction results |
| **Data correction** | Optional offset/gain calibration, orthogonality collection and profiles, and live/historical correction |

## Download and run

Supports **Windows 10/11 x64**. Release packages include the .NET 8 runtime.

| Download | Usage |
| --- | --- |
| [V0.5.0 installer](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.0/MagnetometerSystem-v0.5.0-setup.exe) | Install and launch the application |
| [V0.5.0 portable package](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.0/MagnetometerSystem-v0.5.0-portable-win-x64.zip) | Extract and run `MagnetometerSystem.App.exe` |
| [SHA256 checksums](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.0/SHA256SUMS.txt) | Verify the downloaded installer or portable package |

See the [changelog](docs/变更日志.md) for version changes. The development version is **V0.5.1 (not yet released)** and supports GitHub and Gitee updates: choose automatic comparison or a specific platform in Settings → Software updates. Automatic mode supports downloads from mirrors of the same version. The published V0.5.0 still checks Gitee only.

## Quick start

1. **Set up the connection:** choose serial or TCP and enter the connection parameters.
2. **Configure the protocol:** select a preset or edit a custom protocol; check fields, channel names, and units.
3. **Connect and capture:** enter the nominal sampling rate, connect, and check incoming data and storage status. The nominal rate records metadata; adjust actual output using device-supported commands or settings.
4. **Inspect and analyze:** select channels and a time window, switch plot layouts, and expand statistics or analysis panels as needed. Pausing the plot display keeps original-data recording active.
5. **Replay and export:** disconnect, wait for storage to finish, then select a session to replay or export as CSV.

See the [user guide](docs/user-guide.en.md) for protocol setup, command responses, correction, and troubleshooting.

## Development

Built with **.NET 8, WPF, CommunityToolkit.Mvvm, ScottPlot, and SQLite**. Source builds require Windows and the .NET 8 SDK. Visual Studio users need the .NET desktop development workload.

Run from the repository root:

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug
dotnet run --project src/MagnetometerSystem.App/MagnetometerSystem.App.csproj
dotnet test MagnetometerSystem.sln -c Debug --no-build -m:1
```

`src/MagnetometerSystem.App` contains the desktop UI, `Core` handles protocols, communication, and calculations, and `Infrastructure` implements storage, configuration, and export. Tests are in `tests`.

## Documentation

- [User guide](docs/user-guide.en.md): configuration, data management, correction, and troubleshooting.
- [Protocol checksums and channel units](docs/协议校验与单位.md) (Chinese): checksum setup, presets, and units.
- [Testing and acceptance](docs/testing-and-acceptance.md) (Chinese): test instructions, evidence, and device acceptance.
- [Release procedure](docs/发布流程.md) (Chinese): versioning and packaging.
- [Contributing](CONTRIBUTING.md) (Chinese): commits, pull requests, and reviews.
- [AGENTS.md](AGENTS.md) (Chinese): engineering entry points and constraints for agents.

Development builds also include a simple anonymous feedback form and the selected application icon. Scenario and description are required; name and contact are optional and kept private on the receiver. The service is deployed, and anonymous submission through automatic GitHub Issue creation has been verified. These changes are not part of the published V0.5.0 packages. See [feedback deployment](docs/feedback-deployment.md).
