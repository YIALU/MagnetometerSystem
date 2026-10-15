# Quick start

[Project overview](../README.en.md) · [简体中文](快速上手.md) · [Full user guide](user-guide.en.md)

A first capture takes four steps: **choose or configure a protocol → connect the device → use the plots → export data**. Commands, replay, noise analysis and correction are covered under [Advanced features](#advanced-features); everyday capture does not need them.

The screenshots use the built-in protocol 磁梯度数采卡-pt (21 channels) with data from the serial simulator.

The interface is in Chinese, so the original labels are shown in brackets. The left rail holds the pages: Acquire (采集), Connection (连接), Commands (命令), Data (数据), Analysis (分析), Correction (校正) and Settings (设置). The data-link strip at the top always shows receive → parse → save counters and the measured rate; the Connect and start saving (连接并开始保存) button is at the top right.

## Step 1: Choose or configure a protocol

The protocol tells the application how to split incoming bytes into channel readings; it defines the number of channels, their names and units. Open the Connection page:

![Connection page: connection settings, nominal rate and protocol library on the left; protocol editor and parse test on the right](images/tutorial/01-protocol.png)

1. **Use an existing protocol:** pick a preset or a saved protocol in the Protocol library (协议库) drop-down and click Load selected (载入所选). Load a shared protocol JSON with Import… (导入…).
2. **Adjust it to the device output** (Protocol panel on the right):
   - **ASCII text (ASCII 文本):** enter the separator, tick Header row (首行为表头（跳过）) if needed, and fill in each channel's index, name, unit, column, scale and offset in the field table. Lines ending in LF or CRLF are recognized; if the device ends lines with CR only, the editor has no option for it, so set `AsciiLineEnding` to `"\r"` in the protocol JSON and load it with Import….
   - **Binary (二进制):** add segments in firmware order with + Header (+ 帧头), + Length (+ 长度), + Data (+ 数据), + Checksum (+ 校验), + Tail (+ 帧尾) and so on, and set the data type, byte order, channel index and unit of each data segment. The byte map above shows the frame to scale, with the frame length at the top right.
3. **Check the channels:** make sure order, names and units match the device. The unit decides which axis a channel uses; a temperature channel with `°C` gets its own axis.
4. **Save:** Save current (保存当前) stores it in the library; Export… (导出…) writes a JSON file to share or back up.

### Check the protocol with a parse test first (recommended)

You can check a protocol without a device. Paste a sample of device output into Parse test (解析测试) at the bottom of the Connection page (choose text or HEX) and click Parse (解析). The parse log lists accepted readings, rejected candidate frames and the reason (frame length, checksum, fixed value mismatch and so on). After a connection you can also click Use recently received data (用最近收到的数据).

![Parse test: a pasted sample parsed frame by frame](images/tutorial/02-parse-test.png)

Checksum settings, preset units and old protocol JSON are covered in [protocol checksums and units](协议校验与单位.md) (Chinese).

## Step 2: Connect the device and start capturing

1. On the left of the Connection page choose **Serial (串口)** or **TCP**:
   - Serial: select the port (click Refresh (刷新) if it is missing) and set baud rate, data bits, parity and stop bits to match the device.
   - TCP: enter the device IP address and port. TCP reconnects automatically after a drop, and the session stays open meanwhile.
2. Enter the **nominal sampling rate (标称采样率)**. It is recorded with the session and **never sent to the device**; the actual output rate is shown as the measured parse rate at the top. To change the device rate, send a device command from the Commands page.
3. Click **Connect and start saving (连接并开始保存)** at the top right. A new session is created before reception starts; parsed readings are saved to the local database and plotted on the Acquire page.
4. Check that all three counters in the data-link strip are increasing:

![Capturing: the data-link strip shows receive, parse and save counters while the plot scrolls](images/tutorial/03-acquiring.png)

| What the strip shows | Meaning and what to do |
| --- | --- |
| Receive grows, parse does not | Bytes arrive but the protocol does not match: open Raw frames → Parse log (原始报文 → 解析记录) at the bottom for the rejection reason and go back to step 1 |
| Receive stays at 0 | Check port, baud rate or IP; some devices need a start command from the Commands page |
| Save shows a failure | Reception stops and received readings stay in memory; fix the cause (such as disk space or folder permissions) and click Retry write (重试写入) |

The bottom panel has three tabs: Raw frames (原始报文), whose Parse log (解析记录) gives a verdict per frame and Data blocks (数据块) shows raw bytes in arrival order; Traffic (收发) with commands and replies; and Events (事件) with connection, first frame, session start/end and save failures.

5. To finish, click **Stop and disconnect (停止并断开)**. All data is in the database once the button returns from "saving final batch" and Events shows that the session ended and the final batch was committed.

## Step 3: Use the plots

The Acquire page shows the plot in the middle, a collapsible tool panel on the right and the frames/events panel at the bottom.

**Single plot:** the selected channels share one plot area, and different units get separate axes automatically (nT on the left, the depth channel's m axis on the right below).

![Single plot: three gradient channels and depth in one plot, depth on its own right-hand axis](images/tutorial/04-chart-single.png)

**Multi plot (2 columns):** one plot per channel in two columns; the Interval tab on the right shows statistics for the selected time span.

![Multi plot in two columns: six magnetic channels, interval statistics on the right](images/tutorial/04-chart-multi.png)

**Plot toolbar**

- **Single / Multi (单图 / 多图):** single draws all selected channels in one plot, with separate axes for different units such as temperature; multi draws one plot per channel in 1 or 2 columns.
- **Time window (时间窗口):** how many recent seconds to show.
- **Main Y axis (主 Y 轴):** Auto fits the data; Manual takes lower and upper limits.
- **Pause / resume display (暂停显示 / 继续显示):** freezes the view; **capture and saving continue while paused**.
- **Zero / undo zero (一键归零 / 取消归零):** shifts each curve by its window mean to around 0 for comparing fluctuations; display only, saving and statistics are unaffected.
- **Focus plot (专注曲线):** hides the side and bottom panels so the plot fills the window; Exit focus (退出专注) restores them.
- **Mouse:** the wheel zooms the time axis, Ctrl + wheel zooms the Y axis (with a manual range); hovering shows each channel's raw value at the crosshair time.
- More (更多) toggles auto-scroll to latest and the grid, sets the display point limit, and clears the plot (display only; saved data is unaffected).

**Side panel**

- **Channels (通道):** the checkbox controls plotting only, not saving; each channel shows its latest raw value; change color, display offset and order.
- **Statistics (统计):** mean, standard deviation, peak-to-peak, RMS, minimum and maximum over the recent window, using raw readings.
- **Interval (区间):** drag with the left mouse button on the plot (or type start and end seconds) to get statistics for that span, and export it with Export this span (导出这一段).
- **Calculate (计算):** add total-field `sqrt(A²+B²+C²)`, gradient `(A−B)/baseline` or formula channels.
- **Filter (滤波):** display only; saving, the side-panel Statistics and Interval tabs and export always use raw readings. The per-plot "display window" (显示窗口) statistics in multi plot follow the displayed values, so display offset and filtering change them.

The plot keeps the latest 100,000 points per channel for display and interval analysis; use the Analysis page on a saved session for longer spans.

## Step 4: Export data

After stopping, open the Data page:

![Data page: session list on the left, session details and CSV export on the right](images/tutorial/05-export.png)

1. Select a session in the list on the left; search by name or note and filter by date. Rename (重命名) and Edit note (编辑备注) help find it later.
2. Set the CSV export options on the right as needed:
   - **Start / end time:** empty means from the beginning / to the end, format `yyyy-MM-dd HH:mm:ss.fff`;
   - **Channels:** tick the channels to export (Select all / none);
   - **Decimal places:** empty keeps full precision;
   - **Include header / header includes units;**
   - **Data source:** raw readings by default; sessions with generated correction data can also export a saved correction version.
3. Click Export CSV… (导出 CSV…) and choose where to save.

For a short span, you can also drag-select it in the Interval tab of the Acquire side panel and use Export this span.

## Advanced features

Use these as needed; default capture does not depend on them. See the [user guide](user-guide.en.md) for details.

- **Commands:** fill in parameters from the protocol's command group, or send free ASCII/HEX. "Written" only means the local send finished; whether the device acted shows in the reply or the data.
- **Replay:** select a session on the Data page and click Replay (回放) to replay by recorded timestamps at a chosen speed. Replay is view only and never writes to a session; it is unavailable while connected.
- **Analysis:** select a time range of a saved session on the Analysis page to inspect noise, drift and data quality.
- **Correction:** the orthogonality wizard fits parameters for live display or for generating correction data for saved sessions; raw readings are always kept. The offset/gain page currently only stores and manages parameters and never applies them to capture, plots or export.
- **Settings:** update platform, database and log locations.
- **Feedback:** submit problems and requests anonymously from the status bar or the About dialog.

## Without a device

- Use the **parse test** from step 1 to check a protocol.
- [`tools/serial_simulator`](../tools/serial_simulator/README.md) (Chinese; needs Python and a virtual serial pair) keeps sending frames built from a protocol, so you can try steps 2 to 4 end to end. Simulated data is for learning the software and manual testing; it does not replace real-device acceptance.
