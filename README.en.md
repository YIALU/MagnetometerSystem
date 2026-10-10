# MagnetometerSystem

[简体中文](README.md) · [Download latest release](https://github.com/YIALU/MagnetometerSystem/releases/latest) · [User guide](docs/user-guide.en.md) · [Changelog](docs/变更日志.md)

**MagnetometerSystem is a general-purpose magnetometer desktop application for everyday debugging and experimental recording.** Connect a device through serial or TCP, receive data using a user-defined protocol, automatically save original readings, and plot live curves.

The project centers on **customizable protocols, data recording, and live plotting**. Protocol configuration defines channel counts, names, and units, making it possible to work with different data formats and observe magnetic, temperature, and other auxiliary channels together. Correction and advanced analysis are optional tools.

## Features

- **Connections and protocols:** serial or TCP. ASCII and binary protocols configure field mappings, frame structure, byte order, scaling, checksums, and channel units, with JSON import/export. Send parameterized commands from protocol-associated command groups, or free ASCII/HEX.
- **Reliable recording:** a session is prepared before connecting, and original readings (values after protocol parsing, before correction) are saved to a local database automatically. Pausing the display, collapsing panels, or switching pages does not affect recording; disconnect and shutdown wait for the final batch, and a storage failure stops reception with a clear message so you can fix the cause and retry.
- **Live plots:** a combined plot or one plot per channel; temperature and other channels with different units get separate axes. Adjust channel visibility, colors, order, and display offsets, add total-field, gradient, or formula channels, and use rolling statistics, interval statistics, and display filters.
- **Communication diagnostics:** the data-link strip at the top of the window always shows receive → parse → save counters and the measured rate. Raw frames gives a verdict and rejection reason for each frame, a pasted sample can be test-parsed with the current protocol without connecting, and the command log records reply verdicts and latency.
- **Data management:** browse saved sessions, replay them by recorded timestamps (unavailable while connected), and export CSV by time range, channel, and precision; analyze noise, drift, and data quality for saved sessions.
- **Optional correction:** orthogonality fitting and offset/gain parameters for live or historical data; original values are always preserved.
- **Feedback:** submit problems and requests anonymously from within the application, with no login; name, contact and optional program logs (with user and computer names removed) are visible only to the maintainer.

See the [user guide](docs/user-guide.en.md) for details.

## Download and run

Supports **Windows 10/11 x64**. Release packages include the .NET 8 runtime.

Download the latest version from [GitHub Releases](https://github.com/YIALU/MagnetometerSystem/releases/latest) or [Gitee releases](https://gitee.com/yialu/MagnetometerSystem/releases). Each release provides three files:

| File | Usage |
| --- | --- |
| `MagnetometerSystem-v<version>-setup.exe` | Installer: install and launch the application |
| `MagnetometerSystem-v<version>-portable-win-x64.zip` | Portable package: extract and run `MagnetometerSystem.App.exe` |
| `SHA256SUMS.txt` | Verify the downloaded installer or portable package |

The application supports GitHub and Gitee updates: choose automatic comparison or a specific platform in Settings → Software updates. A new version prompt does not start a download or installation; choose Download update to proceed. The About dialog shows the installed version, and the [changelog](docs/变更日志.md) lists changes per version.

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

`src/MagnetometerSystem.App` contains the desktop UI, `Core` handles protocols, communication, and calculations, and `Infrastructure` implements storage, configuration, and export. Tests are in `tests`. Without a device, [`tools/serial_simulator`](tools/serial_simulator/README.md) (Chinese) generates frames from a protocol configuration for manual testing over a virtual serial pair; it does not replace real-device acceptance.

## Documentation

- [User guide](docs/user-guide.en.md): configuration, data management, correction, and troubleshooting.
- [Protocol checksums and channel units](docs/协议校验与单位.md) (Chinese): checksum setup, presets, and units.
- [Testing and acceptance](docs/testing-and-acceptance.md) (Chinese): test instructions, evidence, and device acceptance.
- [Release procedure](docs/发布流程.md) (Chinese): versioning and packaging.
- [Feedback service](docs/feedback-deployment.md) (Chinese): deployment and maintenance of the anonymous feedback service.
- [Contributing](CONTRIBUTING.md) (Chinese): commits, pull requests, and reviews.
- [AGENTS.md](AGENTS.md) (Chinese): engineering entry points and constraints for agents.
