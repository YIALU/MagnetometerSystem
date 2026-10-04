# MagnetometerSystem

[简体中文](README.md)

A **.NET 8 / WPF** desktop workbench for everyday magnetometer debugging. Its primary workflow is to **receive data using a user-defined protocol, automatically store original readings after connecting, and plot live curves**.

The protocol defines channel count, names, and units. Sampling rates are not constrained by device categories. Orthogonality correction, offset/gain calibration, and calculated channels are optional extensions.

## Connect, capture, and export

1. **Configure the connection:** select serial or TCP and enter serial parameters or the remote address and port.
2. **Select or edit a protocol:** configure ASCII delimiters and field mappings, or binary frame segments, data types, byte order, scaling, and checksums. Verify channel order, names, and units. Protocols can be imported and exported as JSON.
3. **Enter the nominal sampling rate and connect:** this value is metadata; it does not change the device sampling rate. Device configuration requires a command supported by that device.
4. **Check reception and storage:** the capture session is prepared before opening the connection. Valid readings enter the storage and plotting paths automatically. Check received bytes, parsed values, and storage status separately.
5. **Inspect the curves:** select channels, a time window, axes, and single/multiple plots. Pausing the display, collapsing panels, and display downsampling must not interrupt original-data storage.
6. **Finish and export:** disconnect and wait for pending writes to finish. Select the session in data management to inspect, replay, or export CSV.

An **original reading** is a channel value after protocol parsing and before correction, including protocol-defined scaling or unit conversion. The received-byte viewer is a debugging aid; the database is not a recording of the complete wire byte stream.

## Features

| Area | Capabilities |
| --- | --- |
| Serial / TCP | Device connections, incoming-byte inspection, connection status |
| Configurable protocols | ASCII and binary frames, field mapping, byte order, scaling, checksums, JSON import/export, specialized parsers where required |
| Automatic storage | Session metadata and original channel readings, historical queries, replay, CSV export |
| Historical replay | Timestamp-based playback speed, pause and seek, with all channels plotted directly on the history page; disabled while a live connection exists |
| Live plots | Single combined plot or multiple plots in one/two columns; temperature shares the time axis and uses a separate right axis in single-plot mode |
| Display and analysis | Channel colors/order/display offsets, automatic axes, rolling statistics, interval analysis, display filtering, total-field/gradient/formula channels |
| Panel layout | Expand connection, counters, channels, communications, and analysis when needed; small windows scroll when multiple panels are open; focus mode collapses auxiliary panels and restores their previous state and inputs on exit |
| Device commands | Protocol-associated command groups, parameterized frames, ASCII/HEX transmission, communication logs |
| Optional correction | Orthogonality collection and profiles, offset/gain calibration, historical correction; ordinary capture does not require these tools |

**Written locally, acknowledged, and executed are different states.** A successful send call confirms a local write. Device acceptance and execution must be established using the protocol response or observed device behavior.

Charts retain the latest **100,000 points per channel** for display and interval analysis. The complete session consists of data committed to SQLite. Display channels are no longer truncated at 64, and a 65-channel regression test is included; larger channel counts and higher throughput still require measurement on the intended device and computer.

## Run and develop

- Windows 10/11.
- .NET 8 SDK for source builds. Visual Studio users need the .NET desktop development workload.
- WPF application execution and UI tests require Windows. Release packages can include the .NET runtime.

Run from the repository root:

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug
dotnet run --project src/MagnetometerSystem.App/MagnetometerSystem.App.csproj
dotnet test MagnetometerSystem.sln -c Debug
```

See the [release procedure](docs/发布流程.md) for packaging. [Directory.Build.props](Directory.Build.props) is the version source; `AppVersion.cs` reads version, commit, and build metadata. The release script validates version tags and working-tree state.

## Data and troubleshooting

The default database is `%LOCALAPPDATA%\MagnetometerSystem\magnetometer.db`.

Logs use the application directory's `logs` folder where writable, otherwise `%LOCALAPPDATA%\MagnetometerSystem\logs`. Close the application and retain a database backup before upgrading or migrating. Legacy fixed-column tables are retained as `readings_legacy_*` / `corrected_readings_legacy_*`, and their sessions report that migration is required. These legacy records are not automatically converted for replay or export.

Failed write batches remain in memory, with an error status, and can be retried after the storage problem is resolved. This is not a durable recovery log: power loss or forced process termination can lose uncommitted data. Normal shutdown waits for storage; resolve storage errors before exiting.

| Symptom | Check first |
| --- | --- |
| Connected, but no data | Whether the device streams automatically, connection parameters, required start-sampling command |
| Bytes arrive, but no readings | Delimiters/line endings, frame length/byte order/checksum range, field mappings |
| Wrong channels or values | Channel indices, units, scaling; distinguish original values, display offsets, and derived values |
| Curves update, but storage fails | Storage errors, pending writes, database-directory access; a plot update does not prove a committed transaction |
| Command appears sent, but nothing happens | Bytes received by the peer, terminators/checksum, response matching, device execution conditions |

Include the application version, reproduction steps, connection parameters, protocol JSON, minimal received/sent frames, and relevant logs when reporting an issue.

## Validation boundaries

Automated tests cover modules such as parsers, command frames, calculations, SQLite, and CSV. See [testing and acceptance](docs/testing-and-acceptance.md) for business-flow coverage, execution instructions, and current verification status.

Passing unit tests does not establish that a physical serial link, USB driver, device response, or WPF interaction has been verified. TCP loopback exercises real local sockets; physical and virtual serial-pair acceptance requires separate infrastructure. Test counts and coverage percentages belong to actual run artifacts, not a permanent README claim.

## Repository navigation

```text
src/
  MagnetometerSystem.App/             WPF views, ViewModels, application composition, UI dispatch
  MagnetometerSystem.Core/            Protocols, communications, readings, data bus, calculations, interfaces
  MagnetometerSystem.Infrastructure/  SQLite, configuration, CSV, update services
tests/                               Unit and business-flow tests
docs/                                Acceptance, release, and historical design documents
```

- Agent entry point: [AGENTS.md](AGENTS.md).
- Validation and hardware acceptance: [testing-and-acceptance.md](docs/testing-and-acceptance.md).
- Device-specific reference: [Windows/Linux board protocol](docs/Windows与Linux板通信协议.md).
- `docs/00-*`, `01-*`, and `TASK-*` documents are historical plans. Some retain old device categories or obsolete test status. Use the implementation, tests, and current README to establish present behavior.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the GitHub PR and review workflow.
