#!/usr/bin/env python3
"""磁力仪上位机串口数据模拟器。

按上位机的协议配置（内置预设或上位机导出的协议 JSON）生成数据帧，通过串口
（通常是虚拟串口对的一端）发给上位机，用于手动验证接收、解析、保存、绘图和通信诊断。

    python tools/serial_simulator/serial_simulator.py             打开窗口
    python tools/serial_simulator/serial_simulator.py --list      列出可用协议
    python tools/serial_simulator/serial_simulator.py --headless --port COM1 --protocol 1 --rate 10 --seconds 30

帧格式规则与 MagnetometerSystem.Core 的 ConfigurableAsciiParser / ConfigurableBinaryParser 一致。
"""
from __future__ import annotations

import argparse
import copy
import json
import math
import os
import queue
import random
import re
import struct
import sys
import threading
import time
import uuid
from dataclasses import dataclass, field, replace
from pathlib import Path

try:
    import serial
    from serial.tools import list_ports
except ImportError:  # 窗口里给出安装提示
    serial = None
    list_ports = None

HERE = Path(__file__).resolve().parent
PRESET_DIR = HERE / "presets"
REPO_ROOT = HERE.parents[1]

# 与 Core 中枚举的声明顺序一致：JSON 里既可能是名称，也可能是整数
SEGMENT_TYPES = ("Header", "LengthField", "DataField", "Checksum", "Tail", "Padding")
FIELD_TYPES = ("Float", "Double", "Int16", "UInt16", "Int32", "UInt32")
CHECKSUM_ALGORITHMS = ("Xor", "Sum8", "CRC16")
CHECKSUM_TYPES = ("None", "Xor", "Sum8", "CRC16")
CRC16_VARIANTS = ("Modbus", "CcittFalse", "XModem", "Ibm")
CATEGORIES = ("Ascii", "Binary")
PARSER_KINDS = ("Auto", "Ctmbs3X2000")

STRUCT_CODES = {"Float": "f", "Double": "d", "Int16": "h", "UInt16": "H", "Int32": "i", "UInt32": "I"}
TYPE_SIZES = {"Float": 4, "Double": 8, "Int16": 2, "UInt16": 2, "Int32": 4, "UInt32": 4}
INT_RANGES = {"Int16": (-32768, 32767), "UInt16": (0, 65535),
              "Int32": (-2 ** 31, 2 ** 31 - 1), "UInt32": (0, 2 ** 32 - 1)}
FLOAT_MAX = 3.4028234663852886e38

CRC_NAMES = {"Modbus": "CRC-16/MODBUS", "CcittFalse": "CRC-16/CCITT-FALSE",
             "XModem": "CRC-16/XMODEM", "Ibm": "CRC-16/IBM(ARC)"}
LINE_ENDING_NAMES = {"\r\n": "CRLF", "\n": "LF", "\r": "CR"}
BAUD_RATES = ("9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600")
WAVEFORMS = ("正弦", "方波", "三角波", "恒定", "随机游走")


def enum_value(value, names, default):
    """System.Text.Json 的 JsonStringEnumConverter 同时接受名称和整数。"""
    if isinstance(value, bool):
        return default
    if isinstance(value, int):
        return names[value] if 0 <= value < len(names) else default
    if isinstance(value, str):
        for name in names:
            if name.lower() == value.strip().lower():
                return name
    return default


def hex_to_bytes(text) -> bytes:
    """同 ProtocolConfig.HexToBytes：去掉空格和 0x，奇数位补前导 0。"""
    digits = re.sub(r"[^0-9A-Fa-f]", "", str(text or "").replace("0x", "").replace("0X", ""))
    if len(digits) % 2:
        digits = "0" + digits
    return bytes.fromhex(digits)


def fixed_segment_bytes(seg: dict) -> bytes:
    """同 FrameSegment.FixedHexValue 的规整：只留十六进制字符，按 ByteCount 截断或右补零。"""
    count = seg_count(seg)
    digits = re.sub(r"[^0-9A-Fa-f]", "", str(seg.get("FixedHexValue") or "").replace("0x", "").replace("0X", ""))
    digits = digits[:count * 2].ljust(count * 2, "0")
    return bytes.fromhex(digits)


def seg_type(seg: dict) -> str:
    return enum_value(seg.get("Type"), SEGMENT_TYPES, "Padding")


def seg_count(seg: dict) -> int:
    try:
        return max(1, int(seg.get("ByteCount", 1)))
    except (TypeError, ValueError):
        return 1


def _number(item: dict, key: str, default: float) -> float:
    try:
        value = float(item.get(key, default))
    except (TypeError, ValueError):
        return default
    return value if math.isfinite(value) else default


# ---------------------------------------------------------------- 校验 ----

def _reflected_table(poly: int) -> list[int]:
    table = []
    for n in range(256):
        crc = n
        for _ in range(8):
            crc = (crc >> 1) ^ poly if crc & 1 else crc >> 1
        table.append(crc)
    return table


def _msb_table(poly: int) -> list[int]:
    table = []
    for n in range(256):
        crc = n << 8
        for _ in range(8):
            crc = ((crc << 1) ^ poly) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
        table.append(crc)
    return table


_REFLECTED_A001 = _reflected_table(0xA001)
_MSB_1021 = _msb_table(0x1021)


def crc16(data: bytes, variant: str) -> int:
    """与 Core/Protocol/Crc16.cs 相同的四种变体。"""
    if variant in ("Modbus", "Ibm"):
        crc = 0xFFFF if variant == "Modbus" else 0
        for b in data:
            crc = (crc >> 8) ^ _REFLECTED_A001[(crc ^ b) & 0xFF]
        return crc
    crc = 0xFFFF if variant == "CcittFalse" else 0
    for b in data:
        crc = ((crc << 8) & 0xFFFF) ^ _MSB_1021[((crc >> 8) ^ b) & 0xFF]
    return crc


def checksum8(data: bytes, algorithm: str) -> int:
    value = 0
    if algorithm == "Xor":
        for b in data:
            value ^= b
    else:
        value = sum(data) & 0xFF
    return value


def encode_number(value: float, data_type: str, big_endian: bool, scale: float, offset: float) -> bytes:
    """解析端算 物理值 = 原始值 × 倍率 + 偏移，这里反推原始值并按类型和字节序编码。"""
    raw = (value - offset) / scale
    if data_type in INT_RANGES:
        low, high = INT_RANGES[data_type]
        raw = min(high, max(low, int(round(raw))))
    elif data_type == "Float":
        raw = min(FLOAT_MAX, max(-FLOAT_MAX, raw))
    return struct.pack((">" if big_endian else "<") + STRUCT_CODES[data_type], raw)


# ---------------------------------------------------------------- 协议 ----

@dataclass(frozen=True)
class Channel:
    index: int
    name: str
    unit: str


class Protocol:
    """一份上位机协议配置（JSON 字段名与 ProtocolConfig 相同），以及按它生成帧的方法。"""

    def __init__(self, config: dict, origin: str, path: Path | None = None):
        self.config = copy.deepcopy(config)
        self.origin = origin
        self.path = path
        self.category = enum_value(self.config.get("Category"), CATEGORIES, "Ascii")
        self.parser_kind = enum_value(self.config.get("ParserKind"), PARSER_KINDS, "Auto")
        self._migrate_checksum_placeholder()
        self.error = self._validate()

    # ---- 基本信息 ----

    @property
    def name(self) -> str:
        return str(self.config.get("Name") or "未命名协议")

    @property
    def display_name(self) -> str:
        return f"[{self.origin}] {self.name}"

    @property
    def is_binary(self) -> bool:
        return self.category == "Binary"

    @property
    def segments(self) -> list[dict]:
        return self.config.get("Segments") or []

    @property
    def fields(self) -> list[dict]:
        return self.config.get("FieldMappings") or []

    def channels(self) -> list[Channel]:
        items = ([s for s in self.segments if seg_type(s) == "DataField"]
                 if self.is_binary and self.segments else self.fields)
        result = []
        for n, item in enumerate(items):
            unit = item.get("Unit")
            result.append(Channel(int(item.get("ChannelIndex", n) or 0),
                                  str(item.get("Name") or f"CH{n}"),
                                  "nT" if unit is None else str(unit)))
        if not result and not self.is_binary:
            # 未配置字段映射时解析器按列全收，这里发三列
            result = [Channel(0, "X", "nT"), Channel(1, "Y", "nT"), Channel(2, "Z", "nT")]
        return sorted(result, key=lambda c: c.index)

    def header_line_count(self) -> int:
        if self.is_binary:
            return 0
        skip = int(self.config.get("AsciiSkipLines") or 0)
        return max(skip, 1 if self.config.get("AsciiHasHeader") else 0)

    # ---- 检查 ----

    @property
    def checksum_segment(self) -> dict | None:
        return next((seg for seg in self.segments if seg_type(seg) == "Checksum"), None)

    @property
    def checksum_disabled(self) -> bool:
        seg = self.checksum_segment
        return seg is not None and seg.get("ChecksumEnabled", True) is False

    def _migrate_checksum_placeholder(self):
        """与上位机 ProtocolConfig.MigrateChecksumPlaceholder 一致：v0.5.3 及以前保存的数采卡协议副本
        把 CRC 字节存为占位段并强制要求校验，加载时换成“未启用”的校验段。"""
        segs = self.segments
        if not (self.is_binary and segs and self.config.get("RequireChecksum")) or self.checksum_segment is not None:
            return
        index = next((i for i, seg in enumerate(segs) if seg_type(seg) == "Padding" and seg_count(seg) == 2
                      and seg.get("Name") == "CRC参数待确认（请配置校验段）"), None)
        if index is None:
            return
        segments = copy.deepcopy(segs)
        segments[index] = dict(segments[index], Type="Checksum", Name="CRC16", ByteCount=2, FixedHexValue="",
                               ValidateFixedValue=False, ChecksumEnabled=False, ChecksumAlgorithm="CRC16",
                               Crc16Variant="Modbus", ChecksumBigEndian=False, ChecksumStartIndex=1)
        self.config["Segments"] = segments
        self.config["RequireChecksum"] = False

    def _validate(self) -> str | None:
        if self.parser_kind == "Ctmbs3X2000":
            return "CTMBS-3-X2000 是 TCP 端口 81 的请求/推送协议，串口模拟器不支持。"
        channels = self.channels()
        if [c.index for c in channels] != list(range(len(channels))):
            return "通道索引必须从 0 连续且唯一。"
        items = ([s for s in self.segments if seg_type(s) == "DataField"]
                 if self.is_binary and self.segments else self.fields)
        if any(_number(i, "Scale", 1.0) == 0 for i in items):
            return "有字段倍率为 0，无法反推原始值。"
        if not self.is_binary:
            if self.config.get("AsciiLineEnding", "\r\n") not in LINE_ENDING_NAMES:
                return "ASCII 行结束符应为 CRLF、LF 或 CR。"
            return None
        return self._validate_segments() if self.segments else self._validate_legacy()

    def _validate_segments(self) -> str | None:
        segs = self.segments
        for kind in ("Header", "Tail", "LengthField", "Checksum"):
            if sum(1 for s in segs if seg_type(s) == kind) > 1:
                return f"只支持一个 {kind} 段。"
        for i, s in enumerate(segs):
            kind = seg_type(s)
            if kind == "Header" and i != 0:
                return "帧头必须在首段。"
            if kind == "Tail" and i != len(segs) - 1:
                return "帧尾必须在末段。"
            if kind == "LengthField" and seg_count(s) not in (1, 2):
                return "长度字段必须为 1 或 2 字节。"
            if kind == "DataField":
                data_type = enum_value(s.get("DataType"), FIELD_TYPES, "Float")
                if seg_count(s) != TYPE_SIZES[data_type]:
                    return f"字段 {s.get('Name')} 的字节数与类型 {data_type} 不符。"
            if kind == "Checksum":
                algorithm = enum_value(s.get("ChecksumAlgorithm"), CHECKSUM_ALGORITHMS, "Xor")
                if seg_count(s) != (2 if algorithm == "CRC16" else 1):
                    return "CRC16 校验段必须为 2 字节，其他校验段必须为 1 字节。"
                start = int(s.get("ChecksumStartIndex") or 0)
                if start < 0 or start >= i:
                    return "校验范围起始段必须位于校验段之前。"
        total = sum(seg_count(s) for s in segs)
        if not 0 < total <= 131072:
            return "二进制帧长度必须为 1~131072 字节。"
        length = next((s for s in segs if seg_type(s) == "LengthField"), None)
        if length is not None:
            limit = 255 if seg_count(length) == 1 else 65535
            if self._payload_length() > limit:
                return f"数据区 {self._payload_length()} 字节，超出 {seg_count(length)} 字节长度字段的范围。"
        if self.config.get("RequireChecksum") and not any(seg_type(s) == "Checksum" for s in segs):
            return "协议要求校验，但没有校验段。"
        return None

    def _validate_legacy(self) -> str | None:
        if not self.fields:
            return "旧式二进制协议没有字段映射。"
        if self.config.get("HasLengthByte", True):
            count = int(self.config.get("LengthByteCount") or 1)
            if count not in (1, 2):
                return "长度字段必须为 1 或 2 字节。"
            if self._legacy_data_length() > (255 if count == 1 else 65535):
                return "数据区超出长度字段的范围。"
        if self.config.get("RequireChecksum") and enum_value(self.config.get("Checksum"), CHECKSUM_TYPES, "None") == "None":
            return "协议要求校验，但校验方式为“无”。"
        return None

    # ---- 描述 ----

    def frame_length(self) -> int:
        if not self.is_binary:
            return len(self.build({c.index: 0.0 for c in self.channels()}))
        if self.segments:
            return sum(seg_count(s) for s in self.segments)
        return len(self.build({c.index: 0.0 for c in self.channels()}))

    def describe(self) -> str:
        if self.error:
            return f"不可用：{self.error}"
        channels = self.channels()
        if not self.is_binary:
            delimiter = self.config.get("AsciiDelimiter")
            shown = {",": "逗号", "\t": "Tab", " ": "空格", ";": "分号", "": "逗号/空格/Tab"}.get(
                delimiter if delimiter is not None else ",", repr(delimiter))
            ending = LINE_ENDING_NAMES.get(self.config.get("AsciiLineEnding", "\r\n"), "?")
            text = f"ASCII 文本行 · 分隔符 {shown} · 行尾 {ending} · {len(channels)} 通道"
            if self.header_line_count():
                text += f" · 开头 {self.header_line_count()} 行表头"
            return text
        mode = "段式" if self.segments else "旧式字段映射"
        return f"二进制{mode} · 帧长 {self.frame_length()} B · {len(channels)} 通道 · {self.checksum_text()}"

    def checksum_text(self) -> str:
        if self.segments:
            seg = self.checksum_segment
            if seg is None:
                return "无校验"
            if self.checksum_disabled:
                return f"校验字段 {seg_count(seg)} B 未启用（填随机字节）"
            algorithm = enum_value(seg.get("ChecksumAlgorithm"), CHECKSUM_ALGORITHMS, "Xor")
            start = int(seg.get("ChecksumStartIndex") or 0)
            start_name = self.segments[start].get("Name", start) if 0 < start < len(self.segments) else "帧首"
            if algorithm == "CRC16":
                variant = enum_value(seg.get("Crc16Variant"), CRC16_VARIANTS, "Modbus")
                order = "大端" if seg.get("ChecksumBigEndian") else "小端"
                return f"{CRC_NAMES[variant]}（{order}，自“{start_name}”起）"
            return f"{'XOR' if algorithm == 'Xor' else '累加和'}校验（自“{start_name}”起）"
        checksum = enum_value(self.config.get("Checksum"), CHECKSUM_TYPES, "None")
        if checksum == "None":
            return "无校验"
        if checksum == "CRC16":
            return CRC_NAMES[enum_value(self.config.get("Crc16Variant"), CRC16_VARIANTS, "Modbus")]
        return "XOR 校验" if checksum == "Xor" else "累加和校验"

    def export_config(self) -> dict:
        data = copy.deepcopy(self.config)
        data["Id"] = str(uuid.uuid4())
        return data

    # ---- 生成帧 ----

    def build(self, values: dict[int, float], fault: str | None = None, decimals: int = 3,
              rng: random.Random | None = None) -> bytes:
        """fault: None 正常帧；"bad" 校验错（无校验时破坏帧尾/固定值/数值）；"truncate" 只发前半帧。"""
        rng = rng or random.Random()
        if not self.is_binary:
            return self._build_ascii(values, fault, decimals, rng)
        frame = self._build_segments(values, fault, rng) if self.segments else self._build_legacy(values, fault)
        if fault == "truncate":
            frame = frame[:max(1, len(frame) // 2)]
        return frame

    def header_lines(self) -> bytes:
        count = self.header_line_count()
        if not count:
            return b""
        delimiter = self.config.get("AsciiDelimiter") or ","
        ending = self.config.get("AsciiLineEnding") or "\r\n"
        lines = [delimiter.join(c.name for c in self.channels())]
        lines += [f"simulator header line {n + 2}" for n in range(count - 1)]
        return "".join(line + ending for line in lines).encode("utf-8")

    def _build_ascii(self, values, fault, decimals, rng) -> bytes:
        delimiter = self.config.get("AsciiDelimiter")
        delimiter = "," if not delimiter else delimiter
        ending = self.config.get("AsciiLineEnding") or "\r\n"
        if self.fields:
            columns = max(int(f.get("ByteOffset") or 0) for f in self.fields) + 1
            parts = ["0"] * columns
            for f in self.fields:
                value = values.get(int(f.get("ChannelIndex") or 0), 0.0)
                raw = (value - _number(f, "Offset", 0.0)) / _number(f, "Scale", 1.0)
                parts[int(f.get("ByteOffset") or 0)] = f"{raw:.{decimals}f}"
            mapped = sorted({int(f.get("ByteOffset") or 0) for f in self.fields})
        else:
            parts = [f"{values.get(c.index, 0.0):.{decimals}f}" for c in self.channels()]
            mapped = list(range(len(parts)))
        if fault == "bad":
            parts[rng.choice(mapped)] = "ERR"
        elif fault == "truncate":
            parts = parts[:mapped[-1]]  # 少最后一个映射列
        return (delimiter.join(parts) + ending).encode("ascii")

    def _payload_bounds(self) -> tuple[int, int]:
        """同解析器：长度字段的值 = 长度段之后到校验段/帧尾之前的全部字节（含 Padding）。"""
        offsets, pos = [], 0
        for s in self.segments:
            offsets.append(pos)
            pos += seg_count(s)
        index = next(i for i, s in enumerate(self.segments) if seg_type(s) == "LengthField")
        start = offsets[index] + seg_count(self.segments[index])
        ends = [offsets[i] for i, s in enumerate(self.segments)
                if offsets[i] >= start and seg_type(s) in ("Checksum", "Tail")]
        return start, min(ends) if ends else pos

    def _payload_length(self) -> int:
        start, end = self._payload_bounds()
        return end - start

    def _build_segments(self, values, fault, rng) -> bytes:
        segs = self.segments
        offsets, pos = [], 0
        for s in segs:
            offsets.append(pos)
            pos += seg_count(s)
        frame = bytearray(pos)
        length_index = checksum_index = None
        for i, (s, off) in enumerate(zip(segs, offsets)):
            kind, count = seg_type(s), seg_count(s)
            if kind in ("Header", "Tail", "Padding"):
                frame[off:off + count] = fixed_segment_bytes(s)
            elif kind == "DataField":
                data_type = enum_value(s.get("DataType"), FIELD_TYPES, "Float")
                value = values.get(int(s.get("ChannelIndex") or 0), 0.0)
                frame[off:off + count] = encode_number(value, data_type, bool(s.get("BigEndian")),
                                                       _number(s, "Scale", 1.0), _number(s, "Offset", 0.0))
            elif kind == "LengthField":
                length_index = i
            elif kind == "Checksum":
                checksum_index = i

        if length_index is not None:
            seg, off = segs[length_index], offsets[length_index]
            length = self._payload_length()
            if fault == "bad" and (checksum_index is None or self.checksum_disabled) and not self._has_corruptible_anchor():
                length = 0  # 无校验、无帧尾时，用错误长度制造坏帧
            if seg_count(seg) == 1:
                frame[off] = length & 0xFF
            else:
                frame[off:off + 2] = length.to_bytes(2, "big" if seg.get("LengthBigEndian") else "little")

        if checksum_index is not None and self.checksum_disabled:
            # 固件预留了校验字段但没有计算：填随机字节，上位机不比对
            seg, off = segs[checksum_index], offsets[checksum_index]
            frame[off:off + seg_count(seg)] = bytes(rng.randrange(256) for _ in range(seg_count(seg)))
            if fault == "bad":
                self._corrupt_anchor(frame, offsets)
        elif checksum_index is not None:
            seg, off = segs[checksum_index], offsets[checksum_index]
            start_index = int(seg.get("ChecksumStartIndex") or 0)
            start = offsets[start_index] if 0 < start_index < len(segs) else 0
            covered = bytes(frame[start:off])
            algorithm = enum_value(seg.get("ChecksumAlgorithm"), CHECKSUM_ALGORITHMS, "Xor")
            if algorithm == "CRC16":
                crc = crc16(covered, enum_value(seg.get("Crc16Variant"), CRC16_VARIANTS, "Modbus"))
                frame[off:off + 2] = crc.to_bytes(2, "big" if seg.get("ChecksumBigEndian") else "little")
            else:
                frame[off] = checksum8(covered, algorithm)
            if fault == "bad":
                frame[off] ^= 0x5A
        elif fault == "bad":
            self._corrupt_anchor(frame, offsets)
        return bytes(frame)

    def _has_corruptible_anchor(self) -> bool:
        return any(seg_type(s) == "Tail" or (seg_type(s) == "Padding" and s.get("ValidateFixedValue"))
                   for s in self.segments)

    def _corrupt_anchor(self, frame: bytearray, offsets: list[int]):
        for s, off in zip(reversed(self.segments), reversed(offsets)):
            if seg_type(s) == "Tail" or (seg_type(s) == "Padding" and s.get("ValidateFixedValue")):
                frame[off] ^= 0xFF
                return

    def _legacy_data_length(self) -> int:
        if not self.config.get("HasLengthByte", True) and int(self.config.get("FixedDataLength") or 0) > 0:
            return int(self.config["FixedDataLength"])
        return max((int(f.get("ByteOffset") or 0)
                    + TYPE_SIZES[enum_value(f.get("DataType"), FIELD_TYPES, "Double")] for f in self.fields), default=0)

    def _build_legacy(self, values, fault) -> bytes:
        cfg = self.config
        data = bytearray(self._legacy_data_length())
        for f in self.fields:
            data_type = enum_value(f.get("DataType"), FIELD_TYPES, "Double")
            off = int(f.get("ByteOffset") or 0)
            value = values.get(int(f.get("ChannelIndex") or 0), 0.0)
            data[off:off + TYPE_SIZES[data_type]] = encode_number(
                value, data_type, bool(f.get("BigEndian")), _number(f, "Scale", 1.0), _number(f, "Offset", 0.0))
        frame = bytearray(hex_to_bytes(cfg.get("FrameHeader", "AA55")))
        if cfg.get("HasLengthByte", True):
            count = int(cfg.get("LengthByteCount") or 1)
            frame += len(data).to_bytes(count, "big" if cfg.get("LengthBigEndian") else "little")
        frame += data
        checksum = enum_value(cfg.get("Checksum"), CHECKSUM_TYPES, "None")
        tail = hex_to_bytes(cfg.get("FrameTail", ""))
        if checksum != "None":
            covered = bytes(frame[int(cfg.get("ChecksumStartOffset") or 0):])
            if checksum == "CRC16":
                crc = crc16(covered, enum_value(cfg.get("Crc16Variant"), CRC16_VARIANTS, "Modbus"))
                value = bytearray(crc.to_bytes(2, "big" if cfg.get("ChecksumBigEndian") else "little"))
            else:
                value = bytearray([checksum8(covered, checksum)])
            if fault == "bad":
                value[0] ^= 0x5A
            frame += value
        frame += tail
        if fault == "bad" and checksum == "None" and tail:
            frame[-1] ^= 0xFF
        return bytes(frame)


def load_protocol_file(path: Path, origin: str) -> Protocol:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(data, dict):
        raise ValueError("不是协议配置对象")
    return Protocol(data, origin, path)


def app_protocol_dirs() -> list[tuple[str, Path]]:
    """上位机把“保存”的协议写在程序目录的 Protocols 下。"""
    candidates = [(f"上位机 {cfg}", REPO_ROOT / "src" / "MagnetometerSystem.App" / "bin" / cfg / "net8.0-windows" / "Protocols")
                  for cfg in ("Debug", "Release")]
    local = os.environ.get("LOCALAPPDATA")
    if local:
        candidates.append(("上位机安装版", Path(local) / "Programs" / "MagnetometerSystem" / "Protocols"))
    return [(label, path) for label, path in candidates if path.is_dir()]


def discover_protocols() -> tuple[list[Protocol], list[str]]:
    protocols, errors = [], []
    sources = [("内置", PRESET_DIR)] + app_protocol_dirs()
    for label, folder in sources:
        for path in sorted(folder.glob("*.json")):
            try:
                protocols.append(load_protocol_file(path, label))
            except (OSError, ValueError) as ex:
                errors.append(f"跳过 {path.name}：{ex}")
    return protocols, errors


# ---------------------------------------------------------------- 信号 ----

@dataclass(frozen=True)
class SignalSettings:
    waveform: str = "正弦"
    amplitude: float = 50.0   # nT
    frequency: float = 0.2    # Hz
    noise: float = 0.5        # nT，高斯标准差


AXIS_BASE = {"X": 28000.0, "Y": -2500.0, "Z": 42000.0, "H": 28000.0, "F": 50000.0}
AXIS_PHASE = {"X": 0.0, "Y": 2.0944, "Z": 4.1888}


def _axis(name: str) -> str | None:
    match = re.search(r"[XYZHF]", name.upper())
    return match.group(0) if match else None


class SignalGenerator:
    """按通道名称和单位给出接近真实的数值：磁分量围绕地磁场起伏，温度、GPS、姿态各有合理范围。"""

    def __init__(self, channels: list[Channel], rng: random.Random):
        self.channels = channels
        self.rng = rng
        self.walk: dict[int, float] = {c.index: 0.0 for c in channels}
        self.spike = False
        by_name = {c.name.upper(): c.index for c in channels}
        self.profiles = {}
        for c in channels:
            name, unit, axis = c.name.strip(), c.unit.strip(), _axis(c.name)
            sensor = re.search(r"(\d+)", name)
            sensor_no = int(sensor.group(1)) if sensor else 1
            if name.startswith("Δ") and axis and f"{axis}1" in by_name and f"{axis}2" in by_name:
                profile = ("delta", by_name[f"{axis}1"], by_name[f"{axis}2"])
            elif unit in ("°C", "℃") or "温度" in name:
                profile = ("temperature", 25.0)
            elif unit == "°":
                upper = name.upper()
                base = 30.5 if ("纬" in name or "LAT" in upper) else 114.3 if ("经" in name or "LON" in upper) else 0.0
                profile = ("gps", base)
            elif unit == "m/s²":
                profile = ("accel", 9.80665 if axis == "Z" else 0.0)
            elif unit == "°/s":
                profile = ("gyro", 0.0)
            elif unit == "m":
                profile = ("depth", 5.0)
            else:
                base = AXIS_BASE.get(axis or "F", 50000.0) + (sensor_no - 1) * 35.0
                phase = AXIS_PHASE.get(axis or "X", 0.0) + (sensor_no - 1) * 0.15
                profile = ("magnetic", base, phase)
            self.profiles[c.index] = profile

    @staticmethod
    def wave(waveform: str, phase: float) -> float:
        s = math.sin(phase)
        if waveform == "方波":
            return 1.0 if s >= 0 else -1.0
        if waveform == "三角波":
            return 2.0 / math.pi * math.asin(s)
        if waveform == "恒定":
            return 0.0
        return s

    def sample(self, t: float, s: SignalSettings) -> dict[int, float]:
        gauss, values = self.rng.gauss, {}
        spike = 10.0 * max(s.amplitude, 100.0) if self.spike else 0.0
        self.spike = False
        omega = 2.0 * math.pi * s.frequency * t
        for index, profile in self.profiles.items():
            kind = profile[0]
            if kind == "magnetic":
                if s.waveform == "随机游走":
                    self.walk[index] = self.walk[index] * 0.999 + gauss(0.0, max(s.amplitude, 1.0) * 0.02)
                    swing = self.walk[index]
                else:
                    swing = s.amplitude * self.wave(s.waveform, omega + profile[2])
                values[index] = profile[1] + swing + gauss(0.0, s.noise) + spike
            elif kind == "temperature":
                values[index] = profile[1] + 0.5 * math.sin(2.0 * math.pi * t / 600.0) + gauss(0.0, 0.01)
            elif kind == "gps":
                values[index] = profile[1] + gauss(0.0, 1e-6)
            elif kind == "accel":
                values[index] = profile[1] + 0.05 * self.wave(s.waveform, omega) + gauss(0.0, 0.01)
            elif kind == "gyro":
                values[index] = 0.5 * self.wave(s.waveform, omega) + gauss(0.0, 0.02)
            elif kind == "depth":
                values[index] = profile[1] + 0.2 * math.sin(2.0 * math.pi * t / 30.0)
        for index, profile in self.profiles.items():
            if profile[0] == "delta":
                values[index] = values.get(profile[1], 0.0) - values.get(profile[2], 0.0)
        return values


# ---------------------------------------------------------------- 发送 ----

@dataclass(frozen=True)
class RunSettings:
    rate: float = 10.0          # 帧/秒（标称）
    batch: int = 1              # 每次写入的帧数，>1 即粘包
    decimals: int = 3           # ASCII 小数位
    signal: SignalSettings = field(default_factory=SignalSettings)
    noise_pct: float = 0.0      # 帧间插入噪声字节的概率 %
    bad_pct: float = 0.0        # 坏校验帧概率 %
    truncate_pct: float = 0.0   # 截断帧概率 %
    split: bool = False         # 随机拆成小块分多次写入（分包）
    echo: bool = False          # 把收到的数据原样回发


@dataclass
class Stats:
    good: int = 0
    bad: int = 0
    truncated: int = 0
    noise: int = 0
    bytes_sent: int = 0
    rx_bytes: int = 0
    write_timeouts: int = 0
    skipped: int = 0
    last_frame: bytes = b""
    last_values: dict = field(default_factory=dict)

    @property
    def frames(self) -> int:
        return self.good + self.bad + self.truncated


class FrameSource:
    """按协议、信号和故障设置逐帧生成字节，并记录生成了什么。"""

    def __init__(self, protocol: Protocol, rng: random.Random | None = None):
        self.protocol = protocol
        self.rng = rng or random.Random()
        self.generator = SignalGenerator(protocol.channels(), self.rng)
        self.sim_time = 0.0
        self.stats = Stats()

    def next(self, settings: RunSettings, fault: str | None = None) -> bytes:
        out = bytearray()
        if fault is None and settings.noise_pct > 0 and self.rng.random() * 100 < settings.noise_pct:
            out += self.noise()
        self.sim_time += 1.0 / max(settings.rate, 0.01)
        values = self.generator.sample(self.sim_time, settings.signal)
        if fault is None:
            roll = self.rng.random() * 100
            if roll < settings.bad_pct:
                fault = "bad"
            elif roll < settings.bad_pct + settings.truncate_pct:
                fault = "truncate"
        frame = self.protocol.build(values, fault, settings.decimals, self.rng)
        if fault == "bad":
            self.stats.bad += 1
        elif fault == "truncate":
            self.stats.truncated += 1
        else:
            self.stats.good += 1
            self.stats.last_values = values
        self.stats.last_frame = frame
        return bytes(out + frame)

    def noise(self) -> bytes:
        self.stats.noise += 1
        n = self.rng.randint(1, 16)
        if self.protocol.is_binary:
            return bytes(self.rng.randrange(256) for _ in range(n))
        # 文本协议里的噪声单独成行，只让这一行被拒，不连累下一帧
        junk = "".join(self.rng.choice("abcxyzQW@!?~^%&*") for _ in range(n))
        return (junk + (self.protocol.config.get("AsciiLineEnding") or "\r\n")).encode("ascii")


class SimulatorEngine:
    """一个后台线程负责一个串口：按帧率发送模拟帧，同时读取上位机发来的数据（命令）。"""

    def __init__(self, port: str, baud: int, protocol: Protocol, settings: RunSettings,
                 rng: random.Random | None = None):
        if serial is None:
            raise RuntimeError("缺少 pyserial：请先运行 pip install pyserial")
        self.port_name = port
        self.port = serial.Serial(port, baud, timeout=0, write_timeout=1)
        self.rng = rng or random.Random()
        self.source = FrameSource(protocol, self.rng)
        self.settings = settings  # 整体替换，线程每轮读取一次
        self.sending = False
        self.events: queue.Queue = queue.Queue()
        self._requests: queue.Queue = queue.Queue()
        self._stop = threading.Event()
        self._rx_pending = bytearray()
        self._rx_last = 0.0
        self._t0 = 0.0
        self._scheduled = 0
        self._rate = 0.0
        self._thread = threading.Thread(target=self._run, name="serial-simulator", daemon=True)

    @property
    def stats(self) -> Stats:
        return self.source.stats

    @property
    def alive(self) -> bool:
        return self._thread.is_alive()

    def start(self):
        self._thread.start()

    def request(self, action: str, argument=None):
        """线程安全：start / stop / frame(fault) / noise / header / spike / protocol(Protocol)。"""
        self._requests.put((action, argument))

    def close(self, timeout: float = 2.0):
        self._stop.set()
        if self._thread.is_alive():
            self._thread.join(timeout)
        elif self.port.is_open:
            self.port.close()

    # ---- 线程内 ----

    def _emit(self, kind: str, payload):
        self.events.put((kind, payload))

    def _run(self):
        try:
            while not self._stop.is_set():
                self._handle_requests()
                self._receive()
                if self.sending:
                    self._send_due()
                time.sleep(0.001 if self.sending else 0.01)
            self._flush_rx()
        except (serial.SerialException, OSError) as ex:
            self._emit("error", f"串口错误：{ex}")
        finally:
            try:
                self.port.close()
            except Exception:  # noqa: BLE001 - 关闭阶段只求尽量释放
                pass
            self._emit("closed", None)

    def _handle_requests(self):
        while True:
            try:
                action, argument = self._requests.get_nowait()
            except queue.Empty:
                return
            settings = self.settings
            if action == "start":
                self.sending = True
                self._rate = 0.0  # 下一轮重新起算发送节拍
                header = self.source.protocol.header_lines()
                if header:
                    self._write(header, settings)
                    self._emit("info", f"已发送 {self.source.protocol.header_line_count()} 行表头")
            elif action == "stop":
                self.sending = False
            elif action == "frame":
                self._write(self.source.next(settings, argument), settings)
            elif action == "noise":
                self._write(self.source.noise(), settings)
            elif action == "header":
                self._write(self.source.protocol.header_lines(), settings)
            elif action == "spike":
                self.source.generator.spike = True
            elif action == "protocol":
                stats = self.source.stats
                stats.last_values = {}
                self.source = FrameSource(argument, self.rng)
                self.source.stats = stats
                self._rate = 0.0

    def _send_due(self):
        settings = self.settings
        rate = max(settings.rate, 0.01)
        now = time.perf_counter()
        if rate != self._rate:
            self._rate, self._t0, self._scheduled = rate, now, 0
        due = int((now - self._t0) * rate) - self._scheduled
        batch = max(1, settings.batch)
        if due < batch:
            return
        burst = max(batch, int(rate * 0.25) + 1)
        if due > burst * 8:
            # 写出被阻塞过久（例如对端没有读取），丢掉积压，别在恢复后猛发
            self.stats.skipped += due - burst
            self._emit("warn", f"发送跟不上节拍，跳过 {due - burst} 帧")
            self._scheduled += due - burst
            due = burst
        due = min(due, burst)
        chunk = bytearray()
        for _ in range(due):
            chunk += self.source.next(settings)
        self._scheduled += due
        self._write(bytes(chunk), settings)

    def _write(self, data: bytes, settings: RunSettings):
        if not data:
            return
        pieces = [data]
        if settings.split and len(data) > 1:
            pieces, pos = [], 0
            while pos < len(data):
                n = self.rng.randint(1, max(1, len(data) // 3))
                pieces.append(data[pos:pos + n])
                pos += n
        for n, piece in enumerate(pieces):
            try:
                self.stats.bytes_sent += self.port.write(piece) or 0
            except serial.SerialTimeoutException:
                self.stats.write_timeouts += 1
                if self.stats.write_timeouts in (1, 10, 100) or self.stats.write_timeouts % 1000 == 0:
                    self._emit("warn", f"写出超时 {self.stats.write_timeouts} 次：对端可能没有打开或没有读取")
                return
            if n + 1 < len(pieces):
                time.sleep(self.rng.uniform(0.0, 0.002))

    def _receive(self):
        waiting = self.port.in_waiting
        now = time.perf_counter()
        if waiting:
            data = self.port.read(waiting)
            self.stats.rx_bytes += len(data)
            self._rx_pending += data
            self._rx_last = now
            if self.settings.echo:
                self._write(data, replace(self.settings, split=False))
        elif self._rx_pending and now - self._rx_last > 0.03:
            self._flush_rx()

    def _flush_rx(self):
        if self._rx_pending:
            self._emit("rx", bytes(self._rx_pending))
            self._rx_pending.clear()


# ---------------------------------------------------------------- 显示辅助 ----

def format_bytes(data: bytes, is_text: bool, limit: int = 64) -> str:
    if is_text:
        text = data[:limit * 2].decode("ascii", errors="replace")
        return text.replace("\r", "␍").replace("\n", "␊") + ("…" if len(data) > limit * 2 else "")
    return data[:limit].hex(" ").upper() + (" …" if len(data) > limit else "")


def describe_rx(data: bytes) -> str:
    text = "".join(chr(b) if 32 <= b < 127 else "." for b in data[:64])
    return f"收到 {len(data)} B：{format_bytes(data, False, 32)}  |{text}|"


def format_value(value: float) -> str:
    magnitude = abs(value)
    if magnitude >= 1000:
        return f"{value:.2f}"
    if magnitude >= 1:
        return f"{value:.4f}"
    return f"{value:.6f}"


def safe_filename(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|]+', "_", name).strip() or "protocol"


# ---------------------------------------------------------------- 窗口 ----

def run_gui(protocols: list[Protocol], load_errors: list[str]):
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk
    from tkinter import font as tkfont

    if sys.platform == "win32":
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except (AttributeError, OSError):
            pass

    class SimulatorWindow:
        def __init__(self, root: tk.Tk):
            self.root = root
            self.protocols = protocols
            self.protocol: Protocol | None = None
            self.engine: SimulatorEngine | None = None
            self.sending = False
            self.settings = RunSettings()
            self._rate_mark = (time.perf_counter(), 0)
            self._actual_rate = 0.0

            root.title("磁力仪串口模拟器")
            root.minsize(780, 600)
            # 高 DPI 下 Treeview 默认行高会截掉文字，按字体行距算
            linespace = tkfont.nametofont("TkDefaultFont").metrics("linespace")
            ttk.Style(root).configure("Treeview", rowheight=int(linespace * 1.35))
            self.port_var = tk.StringVar()
            self.baud_var = tk.StringVar(value="115200")
            self.protocol_var = tk.StringVar()
            self.rate_var = tk.StringVar(value="10")
            self.batch_var = tk.StringVar(value="1")
            self.decimals_var = tk.StringVar(value="3")
            self.wave_var = tk.StringVar(value=WAVEFORMS[0])
            self.amp_var = tk.StringVar(value="50")
            self.freq_var = tk.StringVar(value="0.2")
            self.noise_var = tk.StringVar(value="0.5")
            self.noise_pct_var = tk.StringVar(value="0")
            self.bad_pct_var = tk.StringVar(value="0")
            self.trunc_pct_var = tk.StringVar(value="0")
            self.split_var = tk.BooleanVar(value=False)
            self.echo_var = tk.BooleanVar(value=False)
            self._build()
            for var in (self.rate_var, self.batch_var, self.decimals_var, self.wave_var, self.amp_var,
                        self.freq_var, self.noise_var, self.noise_pct_var, self.bad_pct_var,
                        self.trunc_pct_var, self.split_var, self.echo_var):
                var.trace_add("write", lambda *_: self._apply_settings())
            self.baud_var.trace_add("write", lambda *_: self._show_protocol_info())

            self._refresh_ports()
            self._reload_protocol_list()
            if self.protocols:
                self._select_protocol(0)
            if serial is None:
                self._log("缺少 pyserial，无法打开串口。请运行：python -m pip install pyserial", "error")
            for message in load_errors:
                self._log(message, "warn")
            self._log("先打开串口（如 COM1），上位机连接配对的另一端（如 COM2），再点“开始发送”。")
            root.protocol("WM_DELETE_WINDOW", self._on_close)
            root.after(100, self._poll)

        # ---- 布局 ----

        def _build(self):
            main = ttk.Frame(self.root, padding=8)
            main.pack(fill="both", expand=True)
            main.columnconfigure(0, weight=1)
            main.rowconfigure(5, weight=1)

            box = ttk.LabelFrame(main, text="串口", padding=(8, 4))
            box.grid(row=0, column=0, sticky="ew")
            ttk.Label(box, text="端口").pack(side="left")
            self.port_combo = ttk.Combobox(box, textvariable=self.port_var, width=10)
            self.port_combo.pack(side="left", padx=(4, 2))
            ttk.Button(box, text="刷新", width=5, command=self._refresh_ports).pack(side="left")
            ttk.Label(box, text="波特率").pack(side="left", padx=(12, 4))
            ttk.Combobox(box, textvariable=self.baud_var, width=8, values=BAUD_RATES).pack(side="left")
            self.open_button = ttk.Button(box, text="打开串口", command=self._toggle_port)
            self.open_button.pack(side="left", padx=(12, 0))
            self.port_status = ttk.Label(box, text="未打开", foreground="gray")
            self.port_status.pack(side="left", padx=8)

            box = ttk.LabelFrame(main, text="协议", padding=(8, 4))
            box.grid(row=1, column=0, sticky="ew", pady=(6, 0))
            box.columnconfigure(0, weight=1)
            self.protocol_combo = ttk.Combobox(box, textvariable=self.protocol_var, state="readonly")
            self.protocol_combo.grid(row=0, column=0, sticky="ew")
            self.protocol_combo.bind("<<ComboboxSelected>>",
                                     lambda _: self._select_protocol(self.protocol_combo.current()))
            ttk.Button(box, text="加载 JSON…", command=self._load_json).grid(row=0, column=1, padx=(6, 0))
            self.export_button = ttk.Button(box, text="导出给上位机…", command=self._export_json)
            self.export_button.grid(row=0, column=2, padx=(6, 0))
            ttk.Button(box, text="复制一帧", command=self._copy_frame).grid(row=0, column=3, padx=(6, 0))
            self.info_label = ttk.Label(box, justify="left", wraplength=720)
            self.info_label.grid(row=1, column=0, columnspan=4, sticky="w", pady=(4, 0))
            box.bind("<Configure>", lambda e: self.info_label.configure(wraplength=max(300, e.width - 24)))

            box = ttk.LabelFrame(main, text="发送与信号", padding=(8, 4))
            box.grid(row=2, column=0, sticky="ew", pady=(6, 0))
            self._spin(box, 0, 0, "帧率 Hz", self.rate_var, 0.1, 5000, 1)
            self._spin(box, 0, 1, "每次写入帧数", self.batch_var, 1, 1000, 1)
            self._spin(box, 0, 2, "ASCII 小数位", self.decimals_var, 0, 12, 1)
            ttk.Label(box, text="波形").grid(row=1, column=0, sticky="w", pady=(4, 0))
            ttk.Combobox(box, textvariable=self.wave_var, values=WAVEFORMS, state="readonly", width=8).grid(
                row=1, column=1, sticky="w", padx=(4, 16), pady=(4, 0))
            self._spin(box, 1, 1, "幅值 nT", self.amp_var, 0, 1e6, 10)
            self._spin(box, 1, 2, "频率 Hz", self.freq_var, 0, 1000, 0.1)
            self._spin(box, 1, 3, "噪声 σ nT", self.noise_var, 0, 1e6, 0.5)

            box = ttk.LabelFrame(main, text="故障注入（验证分包、粘包、噪声、坏校验和重新同步）", padding=(8, 4))
            box.grid(row=3, column=0, sticky="ew", pady=(6, 0))
            self._spin(box, 0, 0, "噪声字节 %", self.noise_pct_var, 0, 100, 1)
            self._spin(box, 0, 1, "坏校验 %", self.bad_pct_var, 0, 100, 1)
            self._spin(box, 0, 2, "截断帧 %", self.trunc_pct_var, 0, 100, 1)
            ttk.Checkbutton(box, text="随机分包写入", variable=self.split_var).grid(row=0, column=6, padx=(4, 8))
            ttk.Checkbutton(box, text="回显收到的数据", variable=self.echo_var).grid(row=0, column=7)
            row = ttk.Frame(box)
            row.grid(row=1, column=0, columnspan=8, sticky="w", pady=(4, 0))
            self.oneshot_buttons = [
                ttk.Button(row, text="发一个坏帧", command=lambda: self._request("frame", "bad")),
                ttk.Button(row, text="发一个截断帧", command=lambda: self._request("frame", "truncate")),
                ttk.Button(row, text="发一段噪声", command=lambda: self._request("noise")),
                ttk.Button(row, text="注入尖峰", command=lambda: self._request("spike")),
            ]
            self.header_button = ttk.Button(row, text="发送表头行", command=lambda: self._request("header"))
            for button in self.oneshot_buttons + [self.header_button]:
                button.pack(side="left", padx=(0, 6))

            row = ttk.Frame(main)
            row.grid(row=4, column=0, sticky="ew", pady=(8, 0))
            self.send_button = ttk.Button(row, text="▶ 开始发送", width=12, command=self._toggle_sending)
            self.send_button.pack(side="left")
            self.single_button = ttk.Button(row, text="发送单帧", command=lambda: self._request("frame"))
            self.single_button.pack(side="left", padx=(6, 0))
            self.stats_label = ttk.Label(row, text="")
            self.stats_label.pack(side="left", padx=(12, 0))

            pane = ttk.PanedWindow(main, orient="horizontal")
            pane.grid(row=5, column=0, sticky="nsew", pady=(6, 0))
            frame = ttk.Frame(pane)
            self.tree = ttk.Treeview(frame, columns=("idx", "name", "unit", "value"), show="headings", height=9)
            for col, text, width, anchor in (("idx", "#", 34, "e"), ("name", "通道", 90, "w"),
                                             ("unit", "单位", 64, "w"), ("value", "最近值", 110, "e")):
                self.tree.heading(col, text=text)
                self.tree.column(col, width=width, anchor=anchor, stretch=col == "value")
            scroll = ttk.Scrollbar(frame, orient="vertical", command=self.tree.yview)
            self.tree.configure(yscrollcommand=scroll.set)
            self.tree.pack(side="left", fill="both", expand=True)
            scroll.pack(side="right", fill="y")
            pane.add(frame, weight=2)
            frame = ttk.Frame(pane)
            self.log_text = tk.Text(frame, height=10, wrap="char", font=("Consolas", 9), state="disabled")
            self.log_text.tag_configure("rx", foreground="#1565c0")
            self.log_text.tag_configure("warn", foreground="#b26a00")
            self.log_text.tag_configure("error", foreground="#c62828")
            scroll = ttk.Scrollbar(frame, orient="vertical", command=self.log_text.yview)
            self.log_text.configure(yscrollcommand=scroll.set)
            self.log_text.pack(side="left", fill="both", expand=True)
            scroll.pack(side="right", fill="y")
            pane.add(frame, weight=3)

            self.preview_label = ttk.Label(main, text="", font=("Consolas", 9), wraplength=740, justify="left")
            self.preview_label.grid(row=6, column=0, sticky="ew", pady=(6, 0))
            main.bind("<Configure>", lambda e: self.preview_label.configure(wraplength=max(300, e.width - 16)))
            self._update_buttons()

        def _spin(self, parent, row, col, label, var, low, high, step):
            ttk.Label(parent, text=label).grid(row=row, column=col * 2, sticky="w", pady=(4, 0) if row else 0)
            ttk.Spinbox(parent, textvariable=var, from_=low, to=high, increment=step, width=8).grid(
                row=row, column=col * 2 + 1, sticky="w", padx=(4, 16), pady=(4, 0) if row else 0)

        # ---- 设置 ----

        def _apply_settings(self):
            cur = self.settings

            def num(var, current, low, high, cast=float):
                try:
                    value = cast(float(var.get()))
                except (ValueError, tk.TclError):
                    return current
                return min(high, max(low, value)) if math.isfinite(value) else current

            wave = self.wave_var.get()
            self.settings = RunSettings(
                rate=num(self.rate_var, cur.rate, 0.1, 5000),
                batch=num(self.batch_var, cur.batch, 1, 1000, int),
                decimals=num(self.decimals_var, cur.decimals, 0, 12, int),
                signal=SignalSettings(
                    waveform=wave if wave in WAVEFORMS else cur.signal.waveform,
                    amplitude=num(self.amp_var, cur.signal.amplitude, 0, 1e6),
                    frequency=num(self.freq_var, cur.signal.frequency, 0, 1000),
                    noise=num(self.noise_var, cur.signal.noise, 0, 1e6)),
                noise_pct=num(self.noise_pct_var, cur.noise_pct, 0, 100),
                bad_pct=num(self.bad_pct_var, cur.bad_pct, 0, 100),
                truncate_pct=num(self.trunc_pct_var, cur.truncate_pct, 0, 100),
                split=bool(self.split_var.get()),
                echo=bool(self.echo_var.get()))
            if self.engine:
                self.engine.settings = self.settings
            self._show_protocol_info()

        # ---- 串口 ----

        def _refresh_ports(self):
            ports = sorted((p.device for p in list_ports.comports()), key=_port_key) if list_ports else []
            self.port_combo.configure(values=ports)
            if not self.port_var.get():
                self.port_var.set("COM1" if "COM1" in ports else (ports[0] if ports else "COM1"))

        def _toggle_port(self):
            if self.engine:
                self._close_engine()
                return
            if serial is None:
                messagebox.showerror("缺少 pyserial", "请先运行：python -m pip install pyserial")
                return
            if self.protocol is None:
                messagebox.showwarning("没有协议", "请先选择协议。")
                return
            try:
                baud = int(self.baud_var.get())
            except ValueError:
                messagebox.showerror("波特率无效", "请输入整数波特率。")
                return
            port = self.port_var.get().strip()
            try:
                self.engine = SimulatorEngine(port, baud, self.protocol, self.settings)
            except (serial.SerialException, ValueError, OSError) as ex:
                messagebox.showerror("打开串口失败",
                                     f"{port}：{ex}\n\n常见原因：端口已被上位机或其他程序占用（模拟器和上位机要用虚拟串口对的两端），"
                                     "或虚拟串口没有创建。")
                return
            self.engine.start()
            self._rate_mark = (time.perf_counter(), 0)
            self.port_status.configure(text=f"{port} 已打开 · {baud}", foreground="#2e7d32")
            self.open_button.configure(text="关闭串口")
            self._actual_rate = 0.0
            self._log(f"已打开 {port}，波特率 {baud}")
            self._update_buttons()

        def _close_engine(self):
            engine, self.engine = self.engine, None
            self.sending = False
            if engine:
                engine.close()
                self._drain(engine)
                self._log(f"已关闭 {engine.port_name}；本次共发 {engine.stats.frames} 帧、{engine.stats.bytes_sent} B")
                self.stats_label.configure(text="已关闭 · " + self._stats_text(engine.stats, None))
            self.port_status.configure(text="未打开", foreground="gray")
            self.open_button.configure(text="打开串口")
            self._update_buttons()

        def _toggle_sending(self):
            if not self.engine:
                return
            if self.sending:
                self._request("stop")
                self.sending = False
                self._log("已停止发送")
            else:
                if self.protocol is None or self.protocol.error:
                    messagebox.showwarning("协议不可用", self.protocol.error if self.protocol else "请先选择协议。")
                    return
                self._request("start")
                self.sending = True
                self._log(f"开始发送：{self.protocol.name}，{self.settings.rate:g} 帧/秒")
            self._update_buttons()

        def _request(self, action, argument=None):
            if self.engine:
                if action != "stop" and (self.protocol is None or self.protocol.error):
                    return
                self.engine.request(action, argument)

        def _update_buttons(self):
            opened = self.engine is not None
            usable = opened and self.protocol is not None and not self.protocol.error
            sending = opened and self.sending
            self.send_button.configure(text="■ 停止发送" if sending else "▶ 开始发送",
                                       state="normal" if usable else "disabled")
            for button in self.oneshot_buttons + [self.single_button]:
                button.configure(state="normal" if usable else "disabled")
            header = usable and self.protocol.header_line_count() > 0
            self.header_button.configure(state="normal" if header else "disabled")
            self.export_button.configure(state="normal" if self.protocol and not self.protocol.error else "disabled")

        # ---- 协议 ----

        def _reload_protocol_list(self):
            self.protocol_combo.configure(values=[p.display_name for p in self.protocols])

        def _select_protocol(self, index: int):
            if not 0 <= index < len(self.protocols):
                return
            self.protocol = self.protocols[index]
            self.protocol_combo.current(index)
            self._show_protocol_info()
            for item in self.tree.get_children():
                self.tree.delete(item)
            for c in self.protocol.channels():
                self.tree.insert("", "end", iid=str(c.index), values=(c.index, c.name, c.unit, ""))
            self.preview_label.configure(text="")
            if self.engine:
                if self.protocol.error and self.sending:
                    self.engine.request("stop")
                    self.sending = False
                self.engine.request("protocol", self.protocol)
                self._log(f"切换协议：{self.protocol.name}")
            self._update_buttons()

        def _show_protocol_info(self):
            p = self.protocol
            if p is None:
                self.info_label.configure(text="没有可用协议。")
                return
            lines = [p.describe()]
            if not p.error:
                channels = p.channels()
                names = "、".join(f"{c.name}({c.unit})" if c.unit else c.name for c in channels[:10])
                lines.append(f"通道：{names}" + (f" 等 {len(channels)} 个" if len(channels) > 10 else ""))
                if p.is_binary:
                    bytes_per_second = p.frame_length() * self.settings.rate
                    try:
                        capacity = int(self.baud_var.get()) / 10
                    except ValueError:
                        capacity = 0
                    if capacity and bytes_per_second > capacity * 0.9:
                        lines.append(f"⚠ 当前帧率约需 {bytes_per_second:,.0f} B/s，超过该波特率约 {capacity:,.0f} B/s 的上限。"
                                     "虚拟串口通常不受波特率限制，实体串口会堵塞。")
            if p.checksum_disabled:
                lines.append("校验字段未启用：与固件一致，CRC 位置填随机字节，上位机不比对；“坏校验”改为破坏帧尾或固定值。")
            if p.path is not None and p.origin != "内置":
                lines.append(f"来源：{p.path}")
            if not p.error:
                lines.append(f"上位机：连接与本端口配对的另一端（如 COM1↔COM2 时选 COM2），波特率 {self.baud_var.get()}，"
                             f"协议“{p.name}”。")
            self.info_label.configure(text="\n".join(lines), foreground="#c62828" if p.error else "")

        def _load_json(self):
            initial = next((str(path) for _, path in app_protocol_dirs()), str(Path.home()))
            filename = filedialog.askopenfilename(title="加载上位机导出的协议 JSON", initialdir=initial,
                                                  filetypes=[("JSON 文件", "*.json"), ("所有文件", "*.*")])
            if not filename:
                return
            try:
                protocol = load_protocol_file(Path(filename), "文件")
            except (OSError, ValueError) as ex:
                messagebox.showerror("加载失败", f"{filename}\n{ex}")
                return
            self.protocols.append(protocol)
            self._reload_protocol_list()
            self._select_protocol(len(self.protocols) - 1)
            self._log(f"已加载协议文件：{filename}")

        def _export_json(self):
            p = self.protocol
            if p is None or p.error:
                return
            initial = next((str(path) for _, path in app_protocol_dirs()), str(Path.home()))
            filename = filedialog.asksaveasfilename(
                title="导出协议，供上位机“导入…”", initialdir=initial, initialfile=f"{safe_filename(p.name)}.json",
                defaultextension=".json", filetypes=[("JSON 文件", "*.json")])
            if not filename:
                return
            try:
                Path(filename).write_text(json.dumps(p.export_config(), ensure_ascii=False, indent=2) + "\n",
                                          encoding="utf-8")
            except OSError as ex:
                messagebox.showerror("导出失败", str(ex))
                return
            self._log(f"已导出：{filename}")
            messagebox.showinfo("已导出", f"已保存到：\n{filename}\n\n在上位机“连接”页点“导入…”选择该文件，然后连接。")

        def _copy_frame(self):
            p = self.protocol
            if p is None or p.error:
                return
            frame = FrameSource(p).next(replace(self.settings, noise_pct=0, bad_pct=0, truncate_pct=0))
            text = frame.decode("ascii", errors="replace") if not p.is_binary else frame.hex(" ").upper()
            self.root.clipboard_clear()
            self.root.clipboard_append(text)
            self._log(f"已复制一帧（{len(frame)} B）到剪贴板，可粘贴到上位机的协议试解析。")

        # ---- 刷新 ----

        def _poll(self):
            engine = self.engine
            if engine:
                self._drain(engine)
                if not engine.alive:
                    self._close_engine()
                else:
                    self._update_stats(engine)
            self.root.after(150, self._poll)

        def _drain(self, engine: SimulatorEngine):
            while True:
                try:
                    kind, payload = engine.events.get_nowait()
                except queue.Empty:
                    return
                if kind == "rx":
                    self._log("← " + describe_rx(payload), "rx")
                elif kind in ("warn", "error", "info"):
                    self._log(payload, kind)

        def _update_stats(self, engine: SimulatorEngine):
            stats = engine.stats
            now = time.perf_counter()
            mark_time, mark_frames = self._rate_mark
            if now - mark_time >= 0.5:
                instant = (stats.frames - mark_frames) / (now - mark_time)
                self._actual_rate = instant if self._actual_rate == 0 else self._actual_rate * 0.6 + instant * 0.4
                self._rate_mark = (now, stats.frames)
            self.stats_label.configure(text=self._stats_text(stats, self._actual_rate if self.sending else 0.0))
            values = stats.last_values
            for index, value in values.items():
                if self.tree.exists(str(index)):
                    self.tree.set(str(index), "value", format_value(value))
            if stats.last_frame and self.protocol:
                self.preview_label.configure(
                    text=f"最近一帧（{len(stats.last_frame)} B）：{format_bytes(stats.last_frame, not self.protocol.is_binary)}")

        @staticmethod
        def _stats_text(stats: Stats, rate: float | None) -> str:
            text = (f"正常 {stats.good:,} 帧 · 坏校验 {stats.bad} · 截断 {stats.truncated} · 噪声 {stats.noise}"
                    f" · 已发 {stats.bytes_sent:,} B")
            if rate is not None:
                text += f" · 实际 {rate:.1f} 帧/秒"
            text += f" · 收到 {stats.rx_bytes:,} B"
            if stats.write_timeouts:
                text += f" · 写超时 {stats.write_timeouts}"
            if stats.skipped:
                text += f" · 跳过 {stats.skipped}"
            return text

        def _log(self, text: str, tag: str = "info"):
            stamp = time.strftime("%H:%M:%S")
            self.log_text.configure(state="normal")
            self.log_text.insert("end", f"{stamp} {text}\n", tag)
            lines = int(self.log_text.index("end-1c").split(".")[0])
            if lines > 500:
                self.log_text.delete("1.0", f"{lines - 500}.0")
            self.log_text.see("end")
            self.log_text.configure(state="disabled")

        def _on_close(self):
            self._close_engine()
            self.root.destroy()

    root = tk.Tk()
    SimulatorWindow(root)
    root.mainloop()


def _port_key(name: str):
    match = re.match(r"([A-Za-z]+)(\d+)$", name)
    return (match.group(1), int(match.group(2))) if match else (name, 0)


# ---------------------------------------------------------------- 命令行 ----

def pick_protocol(protocols: list[Protocol], key: str) -> Protocol:
    path = Path(key)
    if path.suffix.lower() == ".json" and path.is_file():
        return load_protocol_file(path, "文件")
    if key.isdigit() and 1 <= int(key) <= len(protocols):
        return protocols[int(key) - 1]
    matches = [p for p in protocols if key in p.name]
    if len(matches) == 1:
        return matches[0]
    raise SystemExit(f"找不到唯一匹配“{key}”的协议，用 --list 查看序号。")


def run_headless(args, protocol: Protocol):
    if protocol.error:
        raise SystemExit(f"协议不可用：{protocol.error}")
    settings = RunSettings(rate=args.rate, batch=args.batch, noise_pct=args.noise, bad_pct=args.bad,
                           truncate_pct=args.truncate, split=args.split, echo=args.echo)
    engine = SimulatorEngine(args.port, args.baud, protocol, settings,
                             random.Random(args.seed) if args.seed is not None else None)
    print(f"{args.port} @ {args.baud}：{protocol.name}，{protocol.describe()}")
    engine.start()
    engine.request("start")
    deadline = time.monotonic() + args.seconds if args.seconds > 0 else None
    next_report = time.monotonic() + 1
    try:
        while engine.alive and (deadline is None or time.monotonic() < deadline):
            time.sleep(0.05)
            while not engine.events.empty():
                kind, payload = engine.events.get_nowait()
                if kind == "rx":
                    print("← " + describe_rx(payload))
                elif kind in ("warn", "error", "info"):
                    print(payload)
            if time.monotonic() >= next_report:
                next_report += 1
                s = engine.stats
                print(f"正常 {s.good} · 坏校验 {s.bad} · 截断 {s.truncated} · 噪声 {s.noise} · {s.bytes_sent} B", flush=True)
    except KeyboardInterrupt:
        pass
    finally:
        engine.close()
    s = engine.stats
    print(f"结束：正常 {s.good} 帧，坏校验 {s.bad}，截断 {s.truncated}，噪声 {s.noise}，"
          f"共 {s.bytes_sent} B，收到 {s.rx_bytes} B，写超时 {s.write_timeouts}")


def main(argv=None):
    parser = argparse.ArgumentParser(description="磁力仪上位机串口数据模拟器（不带参数时打开窗口）")
    parser.add_argument("--list", action="store_true", help="列出可用协议后退出")
    parser.add_argument("--headless", action="store_true", help="不开窗口，按参数直接发送")
    parser.add_argument("--port", default="COM1")
    parser.add_argument("--baud", type=int, default=115200)
    parser.add_argument("--protocol", default="1", help="协议序号（见 --list）、名称片段或 JSON 文件路径")
    parser.add_argument("--rate", type=float, default=10.0, help="帧/秒")
    parser.add_argument("--batch", type=int, default=1, help="每次写入帧数（>1 即粘包）")
    parser.add_argument("--seconds", type=float, default=0, help="发送时长，0 表示直到 Ctrl+C")
    parser.add_argument("--noise", type=float, default=0, help="插入噪声字节的概率 %%")
    parser.add_argument("--bad", type=float, default=0, help="坏校验帧概率 %%")
    parser.add_argument("--truncate", type=float, default=0, help="截断帧概率 %%")
    parser.add_argument("--split", action="store_true", help="随机分包写入")
    parser.add_argument("--echo", action="store_true", help="把收到的数据原样回发")
    parser.add_argument("--seed", type=int, help="随机种子，便于复现")
    args = parser.parse_args(argv)
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")

    protocols, errors = discover_protocols()
    if args.list:
        for message in errors:
            print(message)
        for n, p in enumerate(protocols, 1):
            print(f"{n:2}. {p.display_name}\n    {p.describe()}")
        return
    if args.headless:
        if serial is None:
            raise SystemExit("缺少 pyserial：python -m pip install pyserial")
        run_headless(args, pick_protocol(protocols, args.protocol))
        return
    run_gui(protocols, errors)


if __name__ == "__main__":
    main()
