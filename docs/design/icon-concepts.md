# 软件图标方案与选定结果

状态：用户已选定 **B：磁铁与波形**。透明背景正式图标已接入本地开发分支的程序、主窗口和安装器；尚未发布新版本。

| 方案 | 视觉含义 | 结果 |
| --- | --- | --- |
| A：M 与采样波形 | Magnetometer 的 M 与连续采样曲线 | 保留审稿参考 |
| B：磁铁与波形 | 磁场测量与观察信号 | 用户选定，已制作正式资产 |
| C：多通道汇聚 | 多路数据进入统一采集工具 | 保留审稿参考 |

## 正式 B 图标

![正式 B 图标](../../src/MagnetometerSystem.App/Assets/Magnetometer.png)

资产：[透明 PNG 源图](../../src/MagnetometerSystem.App/Assets/Magnetometer.png)、[多尺寸 ICO](../../src/MagnetometerSystem.App/Assets/Magnetometer.ico)。深蓝圆角底、青色磁铁与波形、琥珀色磁极；外部透明，无审稿白色背景。

透明 PNG 由内置 imagegen 基于用户选定的 B 概念图编辑：保持磁铁与波形构图、深蓝/青色/琥珀配色，移除展示底板，外部透明，简化边缘与细节，不添加文字、投影或水印。

在 Windows 仓库根可重新生成 ICO：

```powershell
.\tools\Convert-AppIcon.ps1
```

[转换脚本](../../tools/Convert-AppIcon.ps1) 使用 PNG 源图生成 16、24、32、48、64、96、128、256 px 八个透明图标帧。PNG 是正式源资产，脚本只负责缩放和 ICO 封装。

## 接入与验证

- EXE 的 `ApplicationIcon` 使用此 ICO；主窗口通过 WPF 资源加载同一图标。
- Inno Setup 使用同一 ICO；快捷方式及卸载显示继承应用 EXE 图标。
- Debug 解决方案构建通过；现有 SkiaSharp NU1701 兼容警告仍存在。
- ICO 八帧尺寸、透明通道及构建后 EXE 的原生图标提取验证通过；已查看浅色/深色底的尺寸预览。
- 主窗口资源加载、折叠恢复及更新页面相关的四项 WPF 测试通过。测试记录位于本地忽略目录 `.codex_tmp/test-results/icon-ui/icon-ui.trx`。
- Inno Setup 图标编译探测通过；探测包仅用于验证脚本和图标，不是完整发布包。
- 尚未执行完整安装、桌面快捷方式或不同 DPI 任务栏的实际交互检查；本次没有替换已发布的 V0.5.0 产物。

## 原始审稿图

![A：M 与采样波形](icon-A-waveform.png)

![B：磁铁与波形](icon-B-magnet.png)

![C：多通道汇聚](icon-C-channels.png)

三张概念图由内置 imagegen 生成。共同提示：Windows 桌面磁力仪采集/绘图软件，深蓝圆角方块、青色主体与少量琥珀色，简洁厚线条、清晰负空间，避免文字、水印、复杂网格及设备模型；白色背景仅用于审稿。各自构图为 M 形采样波形加读数点、马蹄磁铁连接波形、三条曲线汇入环形传感标记。
