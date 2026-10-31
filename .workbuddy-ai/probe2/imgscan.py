import sys
from imgspacing import load_png

def scan(path, label, y, x1, x2):
    w, h, ch, px = load_png(path)
    def rgb(x, yy):
        i = (yy*w + x)*ch
        return px[i], px[i+1], px[i+2]

    centers = []; widths = []; prev = False; start = 0
    for x in range(x1, x2):
        r, g, b = rgb(x, y)
        on = r > 130 and g > 130 and b > 130
        if on and not prev: start = x
        if not on and prev:
            centers.append((start + x - 1) / 2.0)
            widths.append(x - start)
        prev = on
    if prev:
        centers.append((start + x2 - 1) / 2.0); widths.append(x2 - start)

    gaps = [round(centers[i] - centers[i-1], 1) for i in range(1, len(centers))]
    print('%s  y=%d  x=%d..%d' % (label, y, x1, x2))
    print('   线宽(px): %s' % widths)
    print('   相邻间距(px): %s' % gaps)
    print()

if __name__ == '__main__':
    p1 = 'C:/Users/lijun/.workbuddy-ai/clipboard-images/clipboard-2026-10-31T06-06-28-289Z-ccf77078.png'
    p2 = 'C:/Users/lijun/.workbuddy-ai/clipboard-images/clipboard-2026-10-31T06-06-28-291Z-97bf6223.png'
    for y in (40, 50, 60, 70):
        scan(p1, '转换前', y, 150, 560)
    for y in (30, 40, 50):
        scan(p2, '转换后', y, 130, 440)
