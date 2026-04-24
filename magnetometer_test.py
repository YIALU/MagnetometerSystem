"""
磁力仪协议通信测试脚本
======================
用于通过虚拟串口对向 MagnetometerSystem 发送模拟数据，
支持 ASCII 和 Binary 两种协议格式。

使用方式:
  1. 配置好虚拟串口对（如 COM1 ↔ COM2）
  2. MagnetometerSystem 连接 COM1
  3. 本脚本连接 COM2 发送数据

依赖: pip install pyserial
"""

# D:\Python\python.exe "d:/Code/VIsual Studio_Workspace/MagnetometerSystem/magnetometer_test.py" -s dual

import serial
import struct
import time
import math
import random
import argparse
from datetime import datetime


# ============================================================
#  ASCII 协议发送
# ============================================================

def send_ascii_csv(ser: serial.Serial, channels: list[float], delimiter: str = ","):
    """发送 ASCII 逗号/空格分隔的数据行"""
    line = delimiter.join(f"{v:.4f}" for v in channels) + "\r\n"
    ser.write(line.encode("ascii"))
    return line.strip()


# ============================================================
#  Binary 协议发送
# ============================================================

def build_binary_frame(
    channels: list[float],
    header: bytes = b"\xAA\x55",
    tail: bytes = b"\x0D",
    data_type: str = "double",      # "double" | "float" | "int16" | "int32"
    big_endian: bool = False,
    has_length: bool = True,
    checksum: str = "none",          # "none" | "xor" | "sum8"
    checksum_start: int = 0,
) -> bytes:
    """按照 ProtocolConfig 的二进制帧格式构建帧"""

    endian = ">" if big_endian else "<"

    # 编码数据区
    type_map = {
        "double": ("d", 8),
        "float":  ("f", 4),
        "int16":  ("h", 2),
        "int32":  ("i", 4),
    }
    fmt_char, _ = type_map.get(data_type, ("d", 8))
    data_bytes = b""
    for v in channels:
        data_bytes += struct.pack(endian + fmt_char, v if data_type in ("double", "float") else int(v))

    data_len = len(data_bytes)

    # 组装帧
    frame = bytearray(header)

    if has_length:
        frame.append(data_len & 0xFF)  # 1 字节长度

    frame.extend(data_bytes)

    # 校验
    if checksum != "none":
        cs_data = bytes(frame[checksum_start:])
        if checksum == "xor":
            cs = 0
            for b in cs_data:
                cs ^= b
            frame.append(cs & 0xFF)
        elif checksum == "sum8":
            cs = sum(cs_data) & 0xFF
            frame.append(cs)

    frame.extend(tail)
    return bytes(frame)


# ============================================================
#  旋转矩阵工具
# ============================================================

def rotation_matrix(axis: str, angle: float) -> list[list[float]]:
    """绕指定轴的旋转矩阵 (axis = 'x', 'y', 'z')"""
    c, s = math.cos(angle), math.sin(angle)
    if axis == 'x':
        return [[1, 0, 0], [0, c, -s], [0, s, c]]
    elif axis == 'y':
        return [[c, 0, s], [0, 1, 0], [-s, 0, c]]
    else:  # z
        return [[c, -s, 0], [s, c, 0], [0, 0, 1]]


def mat_mul(A, B):
    """3x3 矩阵乘法"""
    return [[sum(A[i][k] * B[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def mat_vec(M, v):
    """3x3 矩阵乘 3x1 向量"""
    return [sum(M[i][j] * v[j] for j in range(3)) for i in range(3)]


# ============================================================
#  数据生成器
# ============================================================

# 中国中纬度地区典型地磁场矢量 (nT)
# 总场 ~54000 nT, 倾角 ~60°, 偏角 ~-6°
EARTH_FIELD = [20000.0, -2000.0, 46800.0]
# 验证: sqrt(20000² + 2000² + 46800²) ≈ 50910 nT


def generate_triaxial(t: float, noise: float = 5.0) -> list[float]:
    """
    生成三轴磁力仪模拟数据 (nT)。
    模拟传感器在地磁场中缓慢方位变化，总场保持 ~50000 nT。
    各分量围绕真实地磁场分量做小幅振荡（模拟传感器轻微晃动）。
    """
    # 传感器缓慢旋转产生的方位角变化
    yaw   = 0.3 * math.sin(2 * math.pi * 0.02 * t)
    pitch = 0.2 * math.sin(2 * math.pi * 0.03 * t + 1.0)
    roll  = 0.15 * math.sin(2 * math.pi * 0.015 * t + 2.0)

    R = mat_mul(rotation_matrix('z', yaw),
                mat_mul(rotation_matrix('y', pitch),
                        rotation_matrix('x', roll)))
    bx, by, bz = mat_vec(R, EARTH_FIELD)

    bx += random.gauss(0, noise)
    by += random.gauss(0, noise)
    bz += random.gauss(0, noise)
    return [bx, by, bz]


def generate_triaxial_rotation(t: float, noise: float = 5.0) -> list[float]:
    """
    生成三轴磁力仪 **全方位旋转** 校正数据 (nT)。

    物理模型:
      - 传感器在均匀地磁场 (~50000 nT) 中做全方位旋转
      - 理想传感器: 数据点分布在球面上, 总场恒定
      - 非理想传感器: 加入正交度误差(轴间不完全垂直)和灵敏度偏差
        → 数据点分布在椭球面上
      - 叠加零漂偏移(硬铁效应)

    用于正交度校正功能测试。
    """
    # 传感器大幅旋转 — 覆盖尽可能多的方位
    # 使用不同频率使轨迹不重叠，提高球面覆盖度
    yaw   = 2 * math.pi * 0.05 * t                          # 绕 Z 轴匀速旋转
    pitch = 0.9 * math.sin(2 * math.pi * 0.037 * t)         # 俯仰摆动 ±52°
    roll  = 0.7 * math.sin(2 * math.pi * 0.023 * t + 0.5)   # 横滚摆动 ±40°

    R = mat_mul(rotation_matrix('z', yaw),
                mat_mul(rotation_matrix('y', pitch),
                        rotation_matrix('x', roll)))

    # 理想读数 = 旋转矩阵 × 地磁场矢量
    ideal = mat_vec(R, EARTH_FIELD)

    # ---- 模拟传感器非理想特性 ----

    # 1) 正交度误差矩阵 (轴间角度偏差 ~1°-2°, 灵敏度偏差 ~1%-3%)
    ortho_error = [
        [1.00,  0.02,  -0.015],
        [0.01,  1.02,   0.025],
        [-0.018, 0.012, 0.98 ],
    ]
    distorted = mat_vec(ortho_error, ideal)

    # 2) 硬铁偏移 (零漂, nT)
    offset = [150.0, -80.0, 200.0]
    distorted[0] += offset[0]
    distorted[1] += offset[1]
    distorted[2] += offset[2]

    # 3) 测量噪声
    distorted[0] += random.gauss(0, noise)
    distorted[1] += random.gauss(0, noise)
    distorted[2] += random.gauss(0, noise)

    return distorted


def generate_single_axis(t: float, noise: float = 5.0) -> list[float]:
    """生成单轴磁力仪模拟数据 (nT), 总场 ~50000 nT"""
    total_field = math.sqrt(sum(v ** 2 for v in EARTH_FIELD))
    b = total_field + 50 * math.sin(2 * math.pi * 0.1 * t) + random.gauss(0, noise)
    return [b]


def generate_dual_triaxial(t: float, noise: float = 5.0) -> list[float]:
    """
    生成双三轴磁力仪模拟数据（6通道, nT）。
    两个传感器在同一地磁场中, 存在微小梯度差异。
    """
    gradient = 5.0  # 两传感器间梯度 nT/m（假设间距 1m）

    # 传感器缓慢旋转
    yaw   = 0.3 * math.sin(2 * math.pi * 0.02 * t)
    pitch = 0.2 * math.sin(2 * math.pi * 0.03 * t + 1.0)
    roll  = 0.15 * math.sin(2 * math.pi * 0.015 * t + 2.0)

    R = mat_mul(rotation_matrix('z', yaw),
                mat_mul(rotation_matrix('y', pitch),
                        rotation_matrix('x', roll)))

    b1 = mat_vec(R, EARTH_FIELD)
    # 传感器2 = 传感器1 + 微小梯度
    field2 = [EARTH_FIELD[0] + gradient, EARTH_FIELD[1] + gradient * 0.5, EARTH_FIELD[2] - gradient * 0.3]
    b2 = mat_vec(R, field2)

    for i in range(3):
        b1[i] += random.gauss(0, noise)
        b2[i] += random.gauss(0, noise)

    return b1 + b2


def generate_dual_triaxial_rotation(t: float, noise: float = 5.0) -> list[float]:
    """
    生成双三轴 **全方位旋转** 校正数据（6通道, nT）。
    两个传感器各有独立的正交度误差和偏移。
    """
    yaw   = 2 * math.pi * 0.05 * t
    pitch = 0.9 * math.sin(2 * math.pi * 0.037 * t)
    roll  = 0.7 * math.sin(2 * math.pi * 0.023 * t + 0.5)

    R = mat_mul(rotation_matrix('z', yaw),
                mat_mul(rotation_matrix('y', pitch),
                        rotation_matrix('x', roll)))

    ideal = mat_vec(R, EARTH_FIELD)

    # 传感器 1 误差
    ortho1 = [[1.00, 0.02, -0.015], [0.01, 1.02, 0.025], [-0.018, 0.012, 0.98]]
    offset1 = [150.0, -80.0, 200.0]
    d1 = mat_vec(ortho1, ideal)
    d1 = [d1[i] + offset1[i] + random.gauss(0, noise) for i in range(3)]

    # 传感器 2 误差（不同的误差参数）
    ortho2 = [[1.01, -0.01, 0.02], [0.015, 0.99, -0.018], [0.008, 0.022, 1.015]]
    offset2 = [-100.0, 120.0, -60.0]
    d2 = mat_vec(ortho2, ideal)
    d2 = [d2[i] + offset2[i] + random.gauss(0, noise) for i in range(3)]

    return d1 + d2


# ============================================================
#  主循环
# ============================================================

GENERATORS = {
    "single":       (generate_single_axis,              1),
    "triaxial":     (generate_triaxial,                  3),
    "dual":         (generate_dual_triaxial,             6),
    "calib":        (generate_triaxial_rotation,         3),
    "calib-dual":   (generate_dual_triaxial_rotation,    6),
}


def main():
    parser = argparse.ArgumentParser(
        description="磁力仪协议通信测试 — 通过虚拟串口发送模拟数据",
        formatter_class=argparse.RawTextHelpFormatter,
    )
    parser.add_argument("-p", "--port", default="COM2",
                        help="发送端串口号 (默认 COM2)")
    parser.add_argument("-b", "--baud", type=int, default=115200,
                        help="波特率 (默认 115200)")
    parser.add_argument("-r", "--rate", type=float, default=10.0,
                        help="发送频率 Hz (默认 10)")
    parser.add_argument("-s", "--sensor", choices=["single", "triaxial", "dual", "calib", "calib-dual"],
                        default="triaxial",
                        help="传感器类型:\n  single     = 单轴\n  triaxial   = 三轴 (默认)\n  dual       = 双三轴\n  calib      = 三轴全方位旋转(正交度校正用)\n  calib-dual = 双三轴全方位旋转(正交度校正用)")

    proto_group = parser.add_argument_group("协议设置")
    proto_group.add_argument("--protocol", choices=["ascii", "binary"], default="ascii",
                             help="协议类型 (默认 ascii)")
    proto_group.add_argument("--delimiter", default=",",
                             help="ASCII 分隔符 (默认逗号)")
    proto_group.add_argument("--header", default="AA55",
                             help="Binary 帧头 hex (默认 AA55)")
    proto_group.add_argument("--tail", default="0D",
                             help="Binary 帧尾 hex (默认 0D)")
    proto_group.add_argument("--dtype", choices=["double", "float", "int16", "int32"],
                             default="double",
                             help="Binary 数据类型 (默认 double)")
    proto_group.add_argument("--big-endian", action="store_true",
                             help="Binary 使用大端序")
    proto_group.add_argument("--no-length", action="store_true",
                             help="Binary 帧不含长度字节")
    proto_group.add_argument("--checksum", choices=["none", "xor", "sum8"], default="none",
                             help="Binary 校验方式 (默认 none)")
    proto_group.add_argument("--noise", type=float, default=5.0,
                             help="噪声标准差 nT (默认 5.0)")
    proto_group.add_argument("-n", "--count", type=int, default=0,
                             help="发送条数，0=无限 (默认 0)")

    args = parser.parse_args()

    gen_func, ch_count = GENERATORS[args.sensor]
    interval = 1.0 / args.rate

    header_bytes = bytes.fromhex(args.header)
    tail_bytes = bytes.fromhex(args.tail) if args.tail else b""

    print("=" * 60)
    print("  磁力仪协议通信测试脚本")
    print("=" * 60)
    print(f"  串口:       {args.port} @ {args.baud}")
    print(f"  传感器:     {args.sensor} ({ch_count} 通道)")
    print(f"  协议:       {args.protocol}")
    if args.protocol == "ascii":
        delim_display = repr(args.delimiter)
        print(f"  分隔符:     {delim_display}")
    else:
        print(f"  帧头:       {args.header}")
        print(f"  帧尾:       {args.tail or '(无)'}")
        print(f"  数据类型:   {args.dtype}")
        print(f"  字节序:     {'大端' if args.big_endian else '小端'}")
        print(f"  长度字节:   {'无' if args.no_length else '有'}")
        print(f"  校验:       {args.checksum}")
    print(f"  发送频率:   {args.rate} Hz")
    print(f"  噪声:       ±{args.noise} nT")
    print(f"  发送条数:   {'无限' if args.count == 0 else args.count}")
    print("=" * 60)
    print()

    try:
        ser = serial.Serial(args.port, args.baud, timeout=1)
    except serial.SerialException as e:
        print(f"[错误] 无法打开串口 {args.port}: {e}")
        print("  请确认:")
        print("  1. 虚拟串口软件已运行且端口对已创建")
        print("  2. 端口未被其他程序占用")
        return

    print(f"[{datetime.now():%H:%M:%S}] 串口已打开，开始发送数据...")
    print(f"  按 Ctrl+C 停止\n")

    t = 0.0
    sent = 0

    try:
        while True:
            channels = gen_func(t, args.noise)

            if args.protocol == "ascii":
                text = send_ascii_csv(ser, channels, args.delimiter)
                display = text
            else:
                frame = build_binary_frame(
                    channels,
                    header=header_bytes,
                    tail=tail_bytes,
                    data_type=args.dtype,
                    big_endian=args.big_endian,
                    has_length=not args.no_length,
                    checksum=args.checksum,
                )
                ser.write(frame)
                display = frame.hex(" ").upper()

            sent += 1
            ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
            values_str = "  ".join(f"{v:>12.4f}" for v in channels)
            print(f"  [{ts}] #{sent:>6d}  {values_str}  | {display}")

            if 0 < args.count <= sent:
                print(f"\n已发送 {sent} 条，结束。")
                break

            t += interval
            time.sleep(interval)

    except KeyboardInterrupt:
        print(f"\n\n[{datetime.now():%H:%M:%S}] 已停止，共发送 {sent} 条数据")
    finally:
        ser.close()
        print("串口已关闭。")


if __name__ == "__main__":
    main()
