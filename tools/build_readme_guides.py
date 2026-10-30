"""Build privacy-redacted screenshots and bilingual SVG/PNG tutorial diagrams.

Local inputs: artifacts/readme-source/*.png (not committed).
Run on Windows with Pillow: python tools/build_readme_guides.py
SVGs embed only redacted screenshots; PNG exports work directly on GitHub.
"""
from pathlib import Path
from base64 import b64encode
from html import escape
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "docs/images"


def redact():
    # Originals are local build inputs under ignored artifacts/, never published.
    regions = {
        'windows-wifi.png': [(24,210,233,44),(261,208,219,30),(70,357,111,31)],
        'windows-bluetooth.png': [(24,210,233,44),(261,208,219,30)],
        'windows-settings.png': [(37,170,400,22)],
        'windows-devices.png': [(24,199,330,33)],
        'windows-network.png': [],
        'android-wifi.png': [(650,420,460,90),(170,690,380,80),(0,0,1220,150)],
    }
    for name, boxes in regions.items():
        screen = Image.open(ROOT / 'artifacts/readme-source' / name).convert('RGB')
        paint = ImageDraw.Draw(screen)
        for left, top, width, height in boxes:
            # Opaque, synthetic tiles. No original pixels survive redaction.
            tile = 16 if name.startswith('android') else 7
            paint.rectangle((left,top,left+width,top+height), fill='#C1CAD5')
            for row,y in enumerate(range(top,top+height,tile)):
                for col,x in enumerate(range(left,left+width,tile)):
                    value = 172 + ((row*7+col*11)%5)*11
                    paint.rectangle((x,y,min(x+tile-1,left+width),min(y+tile-1,top+height)),fill=(value,value+4,min(value+9,255)))
        screen.save(OUT / name, optimize=True)


def guide(name, screenshot, title, subtitle, labels, mobile=False):
    # Screen coordinates in labels use the original screenshot's coordinate space.
    from PIL import Image
    path = OUT / screenshot
    with Image.open(path) as original:
        iw, ih = original.size
    scale = 330 / iw if mobile else 508 / iw
    x, y = 28, 116
    sw, sh = iw * scale, ih * scale
    height = max(650, int(sh + 150))
    label_x = 420 if mobile else 600
    encoded = b64encode(path.read_bytes()).decode()
    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1040" height="{height}" viewBox="0 0 1040 {height}" role="img">',
           f'<title>{escape(title)}</title><desc>{escape(subtitle)}</desc>',
           '<defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0 0 L8 4 L0 8" fill="#D13D37"/></marker></defs>',
           '<rect width="1040" height="100%" rx="18" fill="#F1F5FB"/>',
           '<g font-family="Segoe UI, Noto Sans CJK SC, Microsoft YaHei, sans-serif">',
           f'<text x="28" y="44" font-size="26" font-weight="700" fill="#173451">{escape(title)}</text>',
           f'<text x="28" y="79" font-size="17" fill="#465B72">{escape(subtitle)}</text>',
           f'<image x="{x}" y="{y}" width="{sw}" height="{sh}" xlink:href="data:image/png;base64,{encoded}"/>']
    for index, (box, label_y, heading, lines) in enumerate(labels, 1):
        bx, by, bw, bh = box
        bx, by, bw, bh = x + bx * scale, y + by * scale, bw * scale, bh * scale
        target_x, target_y = bx + bw, by + bh / 2
        svg += [f'<rect x="{bx:.1f}" y="{by:.1f}" width="{bw:.1f}" height="{bh:.1f}" rx="7" fill="none" stroke="#D13D37" stroke-width="3"/>',
                f'<path d="M{label_x-12} {label_y} H{label_x-32} L{target_x+8:.1f} {target_y:.1f}" fill="none" stroke="#D13D37" stroke-width="2.5" marker-end="url(#arrow)"/>',
                f'<circle cx="{label_x+10}" cy="{label_y}" r="15" fill="#1B6EF3"/>',
                f'<text x="{label_x+10}" y="{label_y+6}" text-anchor="middle" fill="white" font-size="17" font-weight="700">{index}</text>',
                f'<text x="{label_x+36}" y="{label_y+7}" font-size="21" font-weight="700" fill="#173451">{escape(heading)}</text>']
        for line_index, line in enumerate(lines):
            svg.append(f'<text x="{label_x+36}" y="{label_y+40+line_index*27}" font-size="17" fill="#465B72">{escape(line)}</text>')
    svg += ['</g></svg>']
    (OUT / f"{name}.svg").write_text('\n'.join(svg), encoding='utf-8')
    # Raster export uses explicit Windows CJK fonts for reliable GitHub rendering.
    canvas = Image.new('RGB', (2080,height*2), '#F1F5FB')
    draw = ImageDraw.Draw(canvas)
    regular = 'C:/Windows/Fonts/msyh.ttc'
    bold = 'C:/Windows/Fonts/msyhbd.ttc'
    def text_at(px,py,value,size=17,strong=False,color='#465B72'):
        draw.text((int(px*2),int(py*2)),value,font=ImageFont.truetype(bold if strong else regular,size*2),fill=color,anchor='lt')
    text_at(28,20,title,26,True,'#173451')
    text_at(28,62,subtitle)
    screen = Image.open(path).convert('RGB').resize((round(sw*2),round(sh*2)),Image.Resampling.LANCZOS)
    canvas.paste(screen,(x*2,y*2))
    import math
    for index,(box,label_y,heading,lines) in enumerate(labels,1):
        bx,by,bw,bh=box
        bx,by,bw,bh=x+bx*scale,y+by*scale,bw*scale,bh*scale
        draw.rounded_rectangle(tuple(round(n*2) for n in (bx,by,bx+bw,by+bh)),radius=14,outline='#D13D37',width=5)
        end=(bx+bw+8,by+bh/2)
        elbow=(label_x-32,label_y)
        draw.line([(int((label_x-12)*2),label_y*2),(int(elbow[0]*2),label_y*2),(int(end[0]*2),int(end[1]*2))],fill='#D13D37',width=5)
        angle=math.atan2(end[1]-elbow[1],end[0]-elbow[0])
        tip=(end[0]*2,end[1]*2)
        draw.polygon([tip,(tip[0]-23*math.cos(angle-.45),tip[1]-23*math.sin(angle-.45)),(tip[0]-23*math.cos(angle+.45),tip[1]-23*math.sin(angle+.45))],fill='#D13D37')
        draw.ellipse(((label_x-5)*2,(label_y-15)*2,(label_x+25)*2,(label_y+15)*2),fill='#1B6EF3')
        draw.text(((label_x+10)*2,label_y*2),str(index),font=ImageFont.truetype(bold,34),fill='white',anchor='mm')
        text_at(label_x+36,label_y-12,heading,21,True,'#173451')
        for line_index,line in enumerate(lines):
            text_at(label_x+36,label_y+25+line_index*27,line)
    canvas.resize((1040,height),Image.Resampling.LANCZOS).save(OUT / f'{name}.png',optimize=True)


def main():
    redact()
    vector = ET.parse(ROOT / 'android/app/src/main/res/drawable/ic_launcher.xml').getroot()
    ns = '{http://schemas.android.com/apk/res/android}'
    paths = ''.join(f'<path fill="{p.attrib[ns+"fillColor"]}" d="{p.attrib[ns+"pathData"]}"/>' for p in vector)
    (OUT / 'app-icon.svg').write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108"><title>BlueAudioSink</title>{paths}</svg>', encoding='utf-8')
    for lang in ('en', 'zh'):
        zh = lang == 'zh'
        guide(f'wifi-pc-{lang}', 'windows-wifi.png',
              '01 · 电脑：切换到 Wi-Fi' if zh else '01 · PC: select Wi-Fi',
              '先让电脑和手机接入同一路由器。' if zh else 'Connect the PC and phone to the same local network first.', [
                  ((363,38,64,36),175,'点击 Wi-Fi' if zh else 'Choose Wi-Fi',['保持电脑软件打开。'] if zh else ['Keep the PC app open.']),
                  ((17,91,471,80),310,'这里看是否收到音频' if zh else 'Check the audio status',['此图为已连接、当前静音。','手机播放音频后再检查。'] if zh else ['Shown: connected, currently silent.','Play audio on your phone to test.']),
                  ((17,356,162,31),465,'找不到电脑？' if zh else 'PC not listed on the phone?',['在手机点“手动添加”，','输入这里显示的电脑地址。'] if zh else ['Tap Manual add on the phone.','Enter the PC address shown here.'])])
        guide(f'wifi-phone-{lang}', 'android-wifi.png',
              '02 · 手机：选电脑，开始发送' if zh else '02 · Phone: select the PC and send',
              '实机截图：已开始发送。无需 USB 或开发者设置。' if zh else 'Real screenshot after starting. No USB or developer options needed.', [
                  ((60,620,1090,200),250,'先选择电脑' if zh else 'Select your PC first',['点击设备列表中的电脑。','没有发现时可点“刷新”。'] if zh else ['Tap your PC in the device list.','Use Refresh if it is missing.']),
                  ((60,1350,1090,240),470,'点击“开始发送”' if zh else 'Tap Start sending',['按系统提示允许音频采集。','开始后按钮变为“停止发送”。'] if zh else ['Approve the Android capture prompt.','The button becomes Stop sending.']),
                  ((60,360,1090,220),680,'确认显示“发送中”' if zh else 'Look for Sending',['再播放音乐或视频，回电脑检查。','部分应用不允许采集声音。'] if zh else ['Play music/video and check the PC.','Some apps block audio capture.'])], mobile=True)
        guide(f'settings-{lang}', 'windows-settings.png',
              '03 · 设置：统一调整播放设备' if zh else '03 · Settings: choose the playback device',
              '蓝牙与 Wi-Fi 共用同一设置入口。' if zh else 'One Settings page for Bluetooth and Wi-Fi.', [
                  ((431,38,60,35),180,'设置 → 常规' if zh else 'Settings → General',['进入设置不会停止当前音频。'] if zh else ['Opening settings keeps audio running.']),
                  ((27,158,453,42),330,'选择电脑耳机或音箱' if zh else 'Select headphones or speakers',['两种模式都在这里调整输出。'] if zh else ['Choose the PC output here for either mode.']),
                  ((27,337,310,63),475,'卡顿时再加缓冲' if zh else 'Increase buffer if audio stutters',['先用默认值；仍卡顿再调高。','更高缓冲会增加延迟。'] if zh else ['Start with the default, then increase it.','A larger buffer adds latency.'])])
        guide(f'devices-{lang}', 'windows-devices.png',
              '切换手机：设置 → 连接设备' if zh else 'Switch phones: Settings → Devices',
              '一次接收一台手机，不同时混音。' if zh else 'Receive one phone at a time; simultaneous mixing is not supported.', [
                  ((18,133,185,33),200,'选择手机类型' if zh else 'Choose the device type',['蓝牙手机 / Wi-Fi 手机。'] if zh else ['Bluetooth phones / Wi-Fi phones.']),
                  ((18,198,471,35),370,'点击要接收的手机' if zh else 'Select the phone to receive',['Wi-Fi 选定后锁定该来源。','手机仍需自行开始发送。'] if zh else ['Wi-Fi selection locks the audio source.','Start sending on that phone too.'])])
        guide(f'bluetooth-{lang}', 'windows-bluetooth.png',
              '蓝牙：先配对，再连接' if zh else 'Bluetooth: pair first, then connect',
              '先在 Windows 蓝牙设置中配对手机；无需安装发送端。' if zh else 'Pair the phone in Windows Bluetooth settings first. No sender APK needed.', [
                  ((300,38,61,35),190,'切换“蓝牙”' if zh else 'Select Bluetooth',['在设置中选好手机和播放设备。'] if zh else ['Select the phone and PC output in Settings.']),
                  ((17,275,232,36),360,'点击“连接”' if zh else 'Click Connect',['然后在手机上播放音频，','必要时把媒体输出切到电脑。'] if zh else ['Play audio on the phone.','Select the PC as its Bluetooth output.'])])
        guide(f'network-{lang}', 'windows-network.png',
              '无法收音时：尝试防火墙放行' if zh else 'No Wi-Fi audio? Try the firewall helper',
              '正常收音时无需开启；这只是一个排查步骤。' if zh else 'Skip this if audio already works. This is an optional troubleshooting step.', [
                  ((207,89,75,33),185,'设置 → 网络' if zh else 'Settings → Network',['先确认双方在同一局域网。'] if zh else ['First check both devices share a network.']),
                  ((17,268,232,36),365,'点击“尝试开启放行”' if zh else 'Try Allow through firewall',['在 Windows 管理员提示中确认。','只适用于专用网络。'] if zh else ['Approve the Windows administrator prompt.','The rule applies to private networks only.'])])


if __name__ == '__main__':
    main()
