# MagnetometerSystem

[简体中文](README.md) · [Download V0.5.2](https://github.com/YIALU/MagnetometerSystem/releases/tag/v0.5.2) · [User guide](docs/user-guide.en.md) · [Changelog](docs/变更日志.md)

**MagnetometerSystem is a general-purpose magnetometer desktop application for everyday debugging and experimental recording.** Connect a device through serial or TCP, receive data using a user-defined protocol, automatically save original readings, and plot live curves.

The project centers on **customizable protocols, data recording, and live plotting**. Protocol configuration defines channel counts, names, and units, making it possible to work with different data formats and observe magnetic, temperature, and other auxiliary channels together. Correction and advanced analysis are optional tools.

## Features

| Feature | Capabilities |
| --- | --- |
| **Device connections** | Serial and TCP with configurable parameters; a "data link" strip on every page shows receive → parse → save counters and the measured rate |
| **Frame diagnostics** | Raw frames lists each parse result: accepted, rejected with the reason (for example computed vs. received checksum), and bytes dropped while resynchronizing; Parse test decodes a pasted sample with the current protocol without connecting |
| **Custom protocols** | ASCII or binary formats, field mappings, frame structure, byte order, scaling, checksums, channel units, and protocol JSON import/export |
| **Automatic recording** | Original readings organized into sessions, with received counts, saved counts, and storage status |
| **Live plots** | A combined plot or multiple plots in one/two columns; temperature and other channels with different units use separate axes in the combined plot |
| **Acquire page** | Plot + channels / statistics / interval / filter / correction side panel + traffic / raw frames / events dock; collapsible panels and a focus mode that restores the layout |
| **Channels and calculations** | Visibility, colors, order, display offsets, and total-field, gradient, or formula channels |
| **Statistics and analysis** | Rolling statistics; drag on the plot to select an interval, hover for original values; moving-average and median display filters; the Analysis page computes noise, drift, and data quality for saved sessions by channel and time range |
| **Device commands** | Protocol-associated command groups, parameterized commands, ASCII/HEX sending; the traffic log records direction, byte count, reply verdict, and latency per row |
| **Data page** | Session list and details, replay (timestamp-based with speed, pause, and seek; disabled while connected), CSV export by channel |
| **CSV export** | Time and channel selection, export precision, original values, and saved correction results |
| **Data correction** | Calibration page: four-step orthogonality wizard (live continuous / manual 48 points, file import, saved session) with a profile library, and offset/gain parameter management; optional live/historical correction |

## Download and run

Supports **Windows 10/11 x64**. Release packages include the .NET 8 runtime.

| Download | Usage |
| --- | --- |
| [V0.5.2 installer](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.2/MagnetometerSystem-v0.5.2-setup.exe) | Install and launch the application |
| [V0.5.2 portable package](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.2/MagnetometerSystem-v0.5.2-portable-win-x64.zip) | Extract and run `MagnetometerSystem.App.exe` |
| [SHA256 checksums](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.2/SHA256SUMS.txt) | Verify the downloaded installer or portable package |

See the [changelog](docs/变更日志.md) for version changes. The current version is **V0.5.2** and supports GitHub and Gitee updates: choose automatic comparison or a specific platform in Settings → Software updates. Automatic mode supports downloads from mirrors of the same version. A new version prompt does not start a download or installation; choose Download update to proceed.

## Quick start

1. **Set up the connection:** choose serial or TCP and enter the connection parameters.
2. **Configure the protocol:** select a preset or edit a custom protocol; check fields, channel names, and units.
3. **Connect and capture:** enter the nominal sampling rate, connect, and check incoming data and storage status. The nominal rate records metadata; adjust actual output using device-supported commands or settings.
4. **Inspect and analyze:** on the Acquire page, select channels and a time window, switch plot layouts, and open side panels as needed; Raw frames in the bottom dock shows the verdict for each frame. Pausing the display, collapsing panels, or switching pages keeps original-data recording active.
5. **Replay and export:** stop, wait for the final batch ("session ended, final batch committed" under Events), then select a session on the Data page to replay or export as CSV; use the Analysis page for long-range noise and drift.

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

The application also includes a simple anonymous feedback form and the selected application icon. Scenario and description are required; name and contact are optional and kept private on the receiver. The service is deployed, and anonymous submission through automatic GitHub Issue creation has been verified. See [feedback deployment](docs/feedback-deployment.md).
