# User guide

[Project overview](../README.en.md) · [简体中文](使用说明.md)

## Connections and protocols

Choose serial or TCP and enter the connection parameters. If the device requires a start-sampling command, send it from the command panel after connecting.

The protocol defines how bytes become readings and what each channel means. Configure line endings, delimiters, and field mappings for ASCII, or frame structure, data types, byte order, scaling, and checksums for binary data. Confirm channel order, names, and units against device output, then save the configuration or export it as JSON.

The two ZDZ_C08 presets require firmware-confirmed CRC parameters before acquisition; the defaults block acquisition with an explanation. See [protocol checksums and channel units](协议校验与单位.md) (Chinese) for setup, older protocol JSON files, variable-length segments, and preset units.

The nominal sampling rate is session metadata. It neither sends a command nor proves the device's actual output rate. The plot refresh rate controls UI updates. Historical replay follows reading timestamps and the selected playback speed.

## Recording, replay, and export

A session is prepared before connecting, and valid readings are saved automatically. An original reading is the channel value after protocol parsing and before correction, including protocol-defined scaling or unit conversion. The raw-byte viewer supports communication debugging; the database stores readings rather than the entire wire byte stream.

Pausing plots, collapsing panels, display filtering, and display downsampling keep original-data recording active. Charts retain the latest 100,000 points per channel for display and interval analysis. The complete session consists of records committed to SQLite.

A storage failure stops accepting new measurements and disconnects. Accepted pending batches remain in memory. Fix the cause and choose Retry Save to finish the old session before reconnecting. The pending queue is not a durable recovery log: power loss or forced termination can lose uncommitted data. Normal disconnect and shutdown wait for storage to finish.

Select a session in data management to query, replay, or export CSV. Replay supports pause, seek, and speed control, and is disabled while a live connection exists. Export options include time ranges, channels, precision, original values, and saved correction results.

A separate device-storage download workflow is not implemented. The ZDZ Read Stored Data preset cannot be sent; custom commands and free HEX transmission also lack download isolation. CSV export of locally saved sessions remains available. See [device-storage download boundaries](协议校验与单位.md#设备存储下载边界) (Chinese).

## Commands and responses

Select a protocol-associated command, enter its parameters, and send it, or use free ASCII/HEX transmission. Check the required terminators, byte order, and checksum.

Written status confirms a local connection write. A matching-response status confirms receipt of a message satisfying the configured criteria. Device execution still depends on the protocol's meaning or observed behavior. Sends are serialized until a response, timeout, or disconnect cancellation. Free sends without response criteria wait for timeout or cancellation. Protocols without request IDs can still misattribute an ACK that arrives after a timeout.

## Data correction

Orthogonality and offset/gain tools are optional; ordinary capture does not require correction profiles. Applying correction preserves original values. Historical batch correction can save separate results.

An orthogonality profile's fitting unit must match the target channels. `uT`, `µT`, and `μT` are equivalent, but offsets are not converted automatically. Older profiles without recorded units remain unknown and require refitting or an explicitly unit-tagged import.

Orthogonality fitting collection and session import currently require exactly three magnetic channels in X/Y/Z order, or six in X1/Y1/Z1/X2/Y2/Z2 order, all with the same explicit unit. Arbitrary fitting-channel selection is not implemented. Sources containing temperature, extra channels, or incomplete metadata are rejected. Prepare a CSV with explicit axis columns and declare its unit for these sources. Ordinary capture, plotting, and correction with explicit channel mappings remain available.

Historical batch corrections are saved as separate versions. Their identifiers include both profile IDs, channel mappings, fitting units, and a calculation-parameter fingerprint. Editing parameters under the same profile ID preserves previous results. Select a saved correction version when exporting; the CSV `CorrectionVersion` column records its full identifier. Legacy single-profile results remain selectable and exportable.

## Data locations and upgrades

- Default database: `%LOCALAPPDATA%\MagnetometerSystem\magnetometer.db`.
- Logs: the application directory's `logs` folder, falling back to `%LOCALAPPDATA%\MagnetometerSystem\logs` if necessary.
- Close the application and back up the database before upgrading or migrating. Legacy fixed-column records remain in `readings_legacy_*` / `corrected_readings_legacy_*`. Their sessions report that migration is required; they are not automatically converted for replay or export.
- The development version supports Automatic, Gitee and GitHub update sources in Settings → Software updates; the preference is saved immediately. Automatic mode compares stable versions, prefers complete assets for the same version, and uses Gitee when both are complete. A network download failure can switch to a mirror of the same version and package kind, using that mirror's own checksum manifest. Missing/invalid checksums, hash mismatch and user cancellation do not trigger fallback.
- The update dialog shows the download platform and offers manual selection when both platforms have the same version. Downloads are restarted and validated independently; an older mirror is never substituted.
- Published V0.5.0 checks Gitee only. Manual downloads remain available. See the [release procedure](发布流程.md) (Chinese) for packaging and publication.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Connected, but no data | Whether the device streams automatically, connection parameters, required start command |
| Bytes arrive, but no readings | Line endings, delimiters, frame length, byte order, checksum range, field mappings |
| Wrong plotted channels or values | Channel indices, units, scaling, display offsets, calculated-channel sources |
| Plots update, but storage fails | Storage errors, pending writes, database-directory permissions; plotting does not prove a committed transaction |
| Written command has no effect | Bytes received by the peer, terminators, checksum, response criteria, device execution conditions |

Include the application version, reproduction steps, connection parameters, protocol JSON, minimal incoming/outgoing frames, and relevant logs when reporting an issue. See [testing and acceptance](testing-and-acceptance.md) (Chinese) for test coverage and device-validation records.

## Feedback (development builds)

Open Feedback from the main window status bar or About dialog. Enter the usage scenario and problem or feature description. Name and contact are optional and visible only to the maintainer. No account or login is needed. The window leaves acquisition controls available, saves a local draft, and preserves the submission ID for retries. The deployed service has been authorized, and automatic GitHub Issue creation has been verified; the maintainer decides whether to act on each submission. Published V0.5.0 packages do not yet include this feature.
