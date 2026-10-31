import struct, zlib, sys

def load_png(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', 'not png'
    pos = 8
    idat = b''
    w = h = bitd = ctype = None
    while pos < len(data):
        ln = struct.unpack('>I', data[pos:pos+4])[0]
        typ = data[pos+4:pos+8]
        chunk = data[pos+8:pos+8+ln]
        pos += 12 + ln
        if typ == b'IHDR':
            w, h, bitd, ctype = struct.unpack('>IIBB', chunk[:10])
        elif typ == b'IDAT':
            idat += chunk
        elif typ == b'IEND':
            break
    raw = zlib.decompress(idat)
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
    assert bitd == 8, 'bitdepth %s' % bitd
    stride = w * ch
    out = bytearray(w * h * ch)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]; p += 1
        line = bytearray(raw[p:p+stride]); p += stride
        if f == 1:
            for i in range(ch, stride):
                line[i] = (line[i] + line[i-ch]) & 0xFF
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif f == 3:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                b = prev[i]
                c = prev[i-ch] if i >= ch else 0
                pp = a + b - c
                pa, pb, pc = abs(pp-a), abs(pp-b), abs(pp-c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[y*stride:(y+1)*stride] = line
        prev = line
    return w, h, ch, out

def analyze(path, label):
    w, h, ch, px = load_png(path)
    def rgb(x, y):
        i = (y*w + x)*ch
        return px[i], px[i+1], px[i+2]

    # 绿框 bbox（缩放基准）
    gx1, gx2, gy1, gy2 = 10**9, -1, 10**9, -1
    for y in range(h):
        for x in range(w):
            r, g, b = rgb(x, y)
            if g > 110 and r < 110 and b < 110:
                if x < gx1: gx1 = x
                if x > gx2: gx2 = x
                if y < gy1: gy1 = y
                if y > gy2: gy2 = y

    # 找"浅灰游程最多"的一行
    best_row, best_runs = -1, 0
    rows = {}
    for y in range(h):
        runs = 0; prev = False
        for x in range(w):
            r, g, b = rgb(x, y)
            on = r > 150 and g > 150 and b > 150
            if on and not prev: runs += 1
            prev = on
        rows[y] = runs
        if runs > best_runs: best_runs, best_row = runs, y

    y = best_row
    centers = []; prev = False; start = 0
    for x in range(w):
        r, g, b = rgb(x, y)
        on = r > 150 and g > 150 and b > 150
        if on and not prev: start = x
        if not on and prev: centers.append((start + x - 1) / 2.0)
        prev = on
    gaps = sorted(centers[i] - centers[i-1] for i in range(1, len(centers)))
    med = gaps[len(gaps)//2] if gaps else 0
    rect_w = gx2 - gx1

    print('%s: 图 %dx%d  绿框 %dx%d (x %d..%d, y %d..%d)' % (label, w, h, rect_w, gy2-gy1, gx1, gx2, gy1, gy2))
    print('   最密行 y=%d 游程 %d 个，线中心中位距 %.1fpx' % (y, len(centers), med))
    print('   归一化 中位距/绿框宽 = %.4f' % (med / rect_w if rect_w else 0))
    return med / rect_w if rect_w else 0

if __name__ == '__main__':
    a = analyze(sys.argv[1], '转换前')
    b = analyze(sys.argv[2], '转换后')
    print('归一化间距 转换后/转换前 = %.3f' % (b / a if a else 0))
