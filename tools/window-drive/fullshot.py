"""全屏截图（含独立 Popup HWND 与子窗口），纯 ctypes + GDI，无外部依赖。

window_drive 的 capture 只抓目标窗口自己的矩形，设置窗、ComboBox 下拉这类
"另一个顶层窗口"或者超出主窗的部分都拍不到。这里直接 BitBlt 屏幕。

用法：
    python fullshot.py out.bmp [x y w h]
不给矩形时抓主屏全部。

注意两点，都是踩过的：
  1. 必须先声明 PerMonitorV2，否则 GetSystemMetrics 返回的是 125% 缩放后的
     1536x960，BitBlt 也按虚拟坐标抓，图会缺右下角。
  2. GetDIBits 用正高度（自下而上），拿到缓冲区后手工翻行；给负高度在部分
     环境下被忽略，图会上下颠倒。
"""

import ctypes
import struct
import sys
import zlib
from ctypes import Structure, byref, create_string_buffer, sizeof, windll
from ctypes import wintypes as wt

user32 = windll.user32
gdi32 = windll.gdi32

try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass


class BITMAPINFOHEADER(Structure):
    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", wt.LONG),
        ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", wt.LONG),
        ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


def shot(path: str, rect=None) -> str:
    if rect:
        x, y, w, h = rect
    else:
        x, y = 0, 0
        w = user32.GetSystemMetrics(0)
        h = user32.GetSystemMetrics(1)

    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    old = gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)

    bi = BITMAPINFOHEADER()
    bi.biSize = sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = h
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0
    buf = create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, byref(bi), 0)

    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, hdc)

    stride = w * 4
    raw = buf.raw
    rows = [raw[i * stride:(i + 1) * stride] for i in range(h)]
    topdown = b"".join(reversed(rows))      # GetDIBits 给的是自下而上

    # 直接出 PNG：BMP 的高度符号约定太容易搞反，PNG 没有这个问题
    body = bytearray()
    view = memoryview(topdown)
    for row_i in range(h):
        line = bytearray(view[row_i * stride:(row_i + 1) * stride])
        line[0::4] = view[row_i * stride + 2:(row_i + 1) * stride:4]
        line[2::4] = view[row_i * stride:(row_i + 1) * stride:4]
        body.append(0)                       # filter type 0
        body.extend(line)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(body), 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)

    return f"已保存 {path}（{w}x{h}，屏幕区域 {x},{y}）"


if __name__ == "__main__":
    args = [int(v) for v in sys.argv[2:6]] if len(sys.argv) >= 6 else None
    print(shot(sys.argv[1], args))
