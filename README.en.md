# MagnetometerSystem

[![Latest release](https://img.shields.io/github/v/release/YIALU/MagnetometerSystem)](https://github.com/YIALU/MagnetometerSystem/releases/latest)
[![Windows CI](https://github.com/YIALU/MagnetometerSystem/actions/workflows/windows-ci.yml/badge.svg?branch=master)](https://github.com/YIALU/MagnetometerSystem/actions/workflows/windows-ci.yml)
[![License](https://img.shields.io/github/license/YIALU/MagnetometerSystem)](LICENSE)

[简体中文](README.md) · [Download latest release](https://github.com/YIALU/MagnetometerSystem/releases/latest) · **[Quick start tutorial](docs/quick-start.en.md)** · [User guide](docs/user-guide.en.md) · [Changelog](docs/变更日志.md)

**MagnetometerSystem is a general-purpose magnetometer desktop application for everyday debugging and experimental recording.** Connect a device through serial or TCP, receive data using a user-defined protocol, automatically save original readings, and plot live curves.

The project centers on **customizable protocols, data recording, and live plotting**. Protocol configuration defines channel counts, names, and units, making it possible to work with different data formats and observe magnetic, temperature, and other auxiliary channels together. Correction and advanced analysis are optional tools.

> **New here?** The [quick start tutorial](docs/quick-start.en.md) walks through four illustrated steps: choose or configure a protocol → connect the device → use the plots → export data.

![Plot view: the data-link strip at the top, with X, Y and Z magnetic channels in separate plots](docs/images/01-replay.png)

## Screenshots

| Data: sessions, channels and CSV export | Analysis: noise and drift | Correction: orthogonality wizard |
| --- | --- | --- |
| ![Data page](docs/images/02-data.png) | ![Analysis page](docs/images/03-analysis.png) | ![Correction page](docs/images/04-calibration.png) |

The screenshots show simulated three-axis data replayed from a saved session; live capture uses the same plot view. The interface is in Chinese.

## Features

- **Connections and protocols:** serial or TCP. ASCII and binary protocols configure field mappings, frame structure, byte order, scaling, checksums, and channel units, with JSON import/export. Send parameterized commands from protocol-associated command groups, or free ASCII/HEX.
- **Reliable recording:** a session is prepared before connecting, and original readings (values after protocol parsing, before correction) are saved to a local database automatically. Pausing the display, collapsing panels, or switching pages does not affect recording; disconnect and shutdown wait for the final batch, and a storage failure stops reception with a clear message so you can fix the cause and retry.
- **Live plots:** a combined plot or one plot per channel; temperature and other channels with different units get separate axes. Adjust channel visibility, colors, order, and display offsets, add total-field, gradient, or formula channels, and use rolling statistics, interval statistics, and display filters.
- **Communication diagnostics:** the data-link strip at the top of the window always shows receive → parse → save counters and the measured rate. Raw frames gives a verdict and rejection reason for each frame, a pasted sample can be test-parsed with the current protocol without connecting, and the command log records reply verdicts and latency.
- **Data management:** browse saved sessions, replay them by recorded timestamps (unavailable while connected), and export CSV by time range, channel, and precision; analyze noise, drift, and data quality for saved sessions.
- **Optional correction:** orthogonality fitting parameters for live or historical data; original values are always preserved. The offset/gain page currently only stores and manages parameters; capture, charts and export never apply them.
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

### Where data is stored

| Content | Location |
| --- | --- |
| Capture database | `data\magnetometer.db` in the program folder; `%LOCALAPPDATA%\MagnetometerSystem\magnetometer.db` if that folder is not writable |
| Program logs | `logs` in the program folder; `%LOCALAPPDATA%\MagnetometerSystem\logs` if that folder is not writable |

Settings shows the path in use and can open its folder. Close the application and back up the database file before upgrading, migrating, or reinstalling. Upgrading in place (the installed version's automatic update, or extracting the portable package over its folder) does not touch the `data` folder; if you extract the portable package to a new folder, copy the old `data` folder across. Versions before v0.6.16 kept the database in `%LOCALAPPDATA%\MagnetometerSystem`; the new version copies it into `data` on first start and leaves the old file in place; do not run older versions after that, as data they write will not appear in the new version. Uninstalling the installed version asks whether to delete data and settings too, and keeps them by default.

## Quick start

See the **[quick start tutorial](docs/quick-start.en.md)** for full steps with screenshots. Everyday capture takes four steps:

1. **[Choose or configure a protocol](docs/quick-start.en.md#step-1-choose-or-configure-a-protocol):** load a preset or saved protocol from the library on the Connection page and adjust fields, channel names and units to the device output; check it first by pasting a sample into the parse test.
2. **[Connect the device](docs/quick-start.en.md#step-2-connect-the-device-and-start-capturing):** choose serial or TCP, enter the parameters, click Connect and start saving, and confirm that receive → parse → save all grow in the top strip.
3. **[Use the plots](docs/quick-start.en.md#step-3-use-the-plots):** on the Acquire page switch single/multi plots, adjust the time window and axis, and use the side panel for channels, statistics and intervals. Pausing the display does not affect saving.
4. **[Export data](docs/quick-start.en.md#step-4-export-data):** stop, wait for the final batch, then select the session on the Data page and export CSV by time, channel and precision.

Commands, replay, noise analysis and correction are [advanced features](docs/quick-start.en.md#advanced-features); see the [user guide](docs/user-guide.en.md).

### Trying it without a device

- **Parse test:** without connecting, paste a data sample into Parse test on the Connection page and test-parse it with the current protocol to see each frame's verdict and rejection reason; useful for checking a protocol configuration.
- **Simulated data:** [`tools/serial_simulator`](tools/serial_simulator/README.md) (Chinese; needs Python and a virtual serial pair) keeps sending frames built from a protocol configuration, so you can try receiving, saving, plotting, and replay end to end. Simulated data is for learning the software and manual testing; it does not replace real-device acceptance.

## Development

Built with **.NET 8, WPF, CommunityToolkit.Mvvm, ScottPlot, and SQLite**. Source builds require Windows and the .NET 8 SDK. Visual Studio users need the .NET desktop development workload.

Run from the repository root:

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug
dotnet run --project src/MagnetometerSystem.App/MagnetometerSystem.App.csproj
dotnet test MagnetometerSystem.sln -c Debug --no-build -m:1
```

`src/MagnetometerSystem.App` contains the desktop UI, `Core` handles protocols, communication, and calculations, and `Infrastructure` implements storage, configuration, and export. Tests are in `tests`. For manual testing, use the serial simulator above; `magnetometer_test.py` in the repository root is an older command-line sender, described in [testing and acceptance](docs/testing-and-acceptance.md) (Chinese).

## Documentation

- [Quick start tutorial](docs/quick-start.en.md): illustrated steps for protocol, connection, plots and export.
- [User guide](docs/user-guide.en.md): configuration, data management, correction, and troubleshooting.
- [Protocol checksums and channel units](docs/协议校验与单位.md) (Chinese): checksum setup, presets, and units.
- [Testing and acceptance](docs/testing-and-acceptance.md) (Chinese): test instructions, evidence, and device acceptance.
- [Release procedure](docs/发布流程.md) (Chinese): versioning and packaging.
- [Feedback service](docs/feedback-deployment.md) (Chinese): deployment and maintenance of the anonymous feedback service.
- [Contributing](CONTRIBUTING.md) (Chinese): commits, pull requests, and reviews.
- [AGENTS.md](AGENTS.md) (Chinese): engineering entry points and constraints for agents.
- [Early plans and task documents](docs/archive/README.md) (Chinese): the old plans and TASK documents numbered 00–18, kept for history only; they do not describe current features or verification status.

## Reporting problems

- **In-app feedback:** open Feedback from the status bar or the About dialog to submit anonymously, no GitHub account needed; you can attach recent logs with user and computer names removed.
- **GitHub Issues:** if you have a GitHub account, you can also open an [issue](https://github.com/YIALU/MagnetometerSystem/issues).

When reporting a problem, include the application version, steps to reproduce, connection parameters, and the protocol JSON, plus a sample of sent/received frames if relevant.

## License

Licensed under the [Apache License 2.0](LICENSE).
