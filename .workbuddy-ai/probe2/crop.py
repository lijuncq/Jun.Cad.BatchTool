import sys, struct, zlib
from imgspacing import load_png

def save_png(path, w, h, rgb_rows):
    raw = b''.join(b'\x00' + bytes(r) for r in rgb_rows)
    def chunk(typ, data):
        c = struct.pack('>I', len(data)) + typ + data
        return c + struct.pack('>I', zlib.crc32(typ + data) & 0xFFFFFFFF)
    ihdr = struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)
    out = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')
    open(path, 'wb').write(out)

src, dst, y0, y1, scale = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
w, h, ch, px = load_png(src)

rows = []
for y in range(y0, min(y1, h)):
    row = []
    for x in range(w):
        i = (y*w + x)*ch
        r, g, b = px[i], px[i+1], px[i+2]
        row.extend([r, g, b] * scale)
    for _ in range(scale):
        rows.append(row)

save_png(dst, w*scale, len(rows), rows)
print('saved %s  %dx%d' % (dst, w*scale, len(rows)))
