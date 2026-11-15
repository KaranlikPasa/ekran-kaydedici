# Ekran Kaydedici

Wayland / Hyprland için GTK4 ve Libadwaita tabanlı, düşük kaynak tüketimli, Medal tarzı basit ekran kaydedici.

## Özellikler

- **Anlık Tekrar (Medal Modu · F8):** Arka planda RAM tamponunda (`$XDG_RUNTIME_DIR/ekran-kaydedici-buffer`) son 15 / 30 / 60 / 90 saniyeyi tutar; kısayol tuşuna basınca kaydı durdurmadan anında `.mp4` klip kaydeder.
- **Düşük Sistem Tüketimi:** Intel VAAPI (`h264_vaapi` + DMA-BUF) donanım kodlaması kullanır (bulunamazsa `libx264 ultrafast` yedeğine geçer). SSD aşınması yapmaz.
- **Ses Desteği:** Sistem sesi, mikrofon veya her ikisini aynı anda (geçici PipeWire mikseri ile) kaydedebilir.
- **Manuel Kayıt:** Tam ekran veya `slurp` ile bölge seçerek doğrudan kayıt alabilir.
- **Sistem Tepsisi & Arka Plan:** `--background` parametresiyle pencere açmadan başlar, `StatusNotifierItem` üzerinden tepsi simgesi sunar.

## Gereksinimler

- Python 3 + `python-gobject` (`GTK 4`, `Libadwaita`)
- `wf-recorder`
- `ffmpeg`
- `slurp` (bölge seçimi için)
- `pulseaudio` / `pipewire-pulse` (`pactl`)
- `libnotify` (`notify-send`) ve `glib2` (`gdbus`)

## Kullanım

```bash
# Arayüzü aç
./launch.sh

# Arka planda (penceresiz) başlat
./launch.sh --background

# Çalışan arka plan örneğinden anlık klip kaydet
./clip.sh
```

### Hyprland Örnek Yapılandırma

`exec-once` / başlangıç:
```lua
hl.exec_cmd("$HOME/Projects/ekran-kaydedici/launch.sh --background")
```

`F8` kısayolu:
```lua
hl.bind("F8", hl.dsp.exec_cmd("$HOME/Projects/ekran-kaydedici/clip.sh"), { locked = true, description = "Ekran Kaydedici: Son klibi kaydet (F8)" })
```

Kayıtlar varsayılan olarak `~/Videos/EkranKayitlari/` klasörüne kaydedilir.

---

## Windows Sürümü (Windows 10/11 x64)

Windows sürümü `windows/` klasöründe **.NET 8 (WinForms)** + **NAudio (WASAPI)** + **FFmpeg** tabanlı olarak yer alır:

- **İndirme:** GitHub [Releases](https://github.com/KaranlikPasa/ekran-kaydedici/releases) sayfasından `EkranKaydedici-Windows-x64.zip` dosyasını indirip çıkartın ve `EkranKaydedici.exe` dosyasını çalıştırın (içinde `ffmpeg.exe` dahildir, ek kurulum gerekmez).
- **Özellikler:**
  - Global **F8** kısayolu (`RegisterHotKey`), pencere kapalıyken sağ alt sistem tepsisinde arka planda çalışır.
  - Ekstra "Stereo Mix" ayarı gerekmeden doğrudan **WASAPI Loopback** ile sistem sesi + mikrofon yakalar.
  - `ddagrab` (DXGI Desktop Duplication) + donanım kodlayıcıyı (`h264_nvenc` / `h264_qsv` / `h264_amf`, yedek `libx264`) otomatik algılar.
  - İstenirse **Windows ile arka planda başlat** seçeneği sunar.

### Windows'ta Kaynaktan Derleme

```powershell
dotnet test windows/EkranKaydedici.sln -c Release
dotnet publish windows/EkranKaydedici/EkranKaydedici.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
