# -*- coding: utf-8 -*-
"""生成未压缩 BGR AVI 测试视频（纯 stdlib）。Windows 属性处理器直接读 BITMAPINFOHEADER 宽高。"""
import struct, sys, os

def make_avi(path, w, h, frames, fps=10):
    stride = (w * 3 + 3) & ~3
    frame_size = stride * h

    def frame_data(idx):
        rows = []
        for y in range(h - 1, -1, -1):          # bottom-up
            row = bytearray()
            for x in range(w):
                r = (x * 255 // w) & 0xFF
                g = (y * 255 // h) & 0xFF
                b = (idx * 40) & 0xFF
                if w // 4 < x < w * 3 // 4 and h // 4 < y < h * 3 // 4:
                    r = g = b = 255             # 中央白块，肉眼确认比例
                row += bytes((b, g, r))
            row += b"\x00" * (stride - w * 3)
            rows.append(bytes(row))
        return b"".join(rows)

    def chunk(fourcc, data):
        return fourcc + struct.pack("<I", len(data)) + data + (b"\x00" if len(data) & 1 else b"")

    def list_chunk(fourcc, data):
        return b"LIST" + struct.pack("<I", len(data) + 4) + fourcc + data

    # BITMAPINFOHEADER
    bih = struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, frame_size, 0, 0, 0, 0)
    # AVISTREAMHEADER（56 字节）
    strh = b"vids" + b"DIB " + struct.pack(
        "<IHHIIIIIIIIhhhh",
        0,            # dwFlags
        0, 0,         # wPriority, wLanguage
        0,            # dwInitialFrames
        1, fps,       # dwScale, dwRate → 帧率 = rate/scale
        0,            # dwStart
        frames,       # dwLength
        frame_size,   # dwSuggestedBufferSize
        0, 0,         # dwQuality, dwSampleSize
        0, 0, 0, 0)   # rcFrame
    assert len(strh) == 56, len(strh)
    # AVIMAINHEADER（56 字节 = 14 dwords）
    avih = struct.pack("<14I",
                       10**6 // fps,   # dwMicroSecPerFrame
                       frame_size * fps,  # dwMaxBytesPerSec
                       0,              # dwPaddingGranularity
                       0x10,           # dwFlags: AVIF_HASINDEX
                       frames,         # dwTotalFrames
                       0,              # dwInitialFrames
                       1,              # dwStreams
                       frame_size,     # dwSuggestedBufferSize
                       w, h,           # dwWidth, dwHeight
                       0, 0, 0, 0)     # dwReserved
    assert len(avih) == 56, len(avih)

    hdrl = list_chunk(b"hdrl", chunk(b"avih", avih) +
                      list_chunk(b"strl", chunk(b"strh", strh) + chunk(b"strf", bih)))

    movi_data = bytearray()
    offsets = []
    for i in range(frames):
        d = frame_data(i)
        offsets.append(len(movi_data))
        movi_data += chunk(b"00dc", d)
    movi = list_chunk(b"movi", bytes(movi_data))

    idx = bytearray()
    for i in range(frames):
        idx += b"00dc" + struct.pack("<III", 0x10, frame_size, offsets[i] + 8)
    idx1 = chunk(b"idx1", bytes(idx))

    body = b"AVI " + hdrl + movi + idx1
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", len(body)) + body)
    print("wrote", path, os.path.getsize(path), "bytes", "%dx%dx%d" % (w, h, frames))

if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    make_avi(os.path.join(out, "sv-landscape.avi"), 1920, 1080, 30)
    make_avi(os.path.join(out, "sv-portrait.avi"), 1080, 1920, 30)
