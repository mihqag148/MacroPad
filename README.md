# RYNOR ONE — ZMK firmware

Firmware và cấu hình phần cứng RYNOR ONE cho nice!nano v2 / nRF52840.

## Đã giữ nguyên

- 12 phím macro.
- Encoder EC11: A=P1.04, B=P1.06.
- Encoder push: ROW0/COL3 = P0.09 / P0.31.
- 12 phím dùng matrix điện **3 hàng phím trên ROW1-ROW3 × 4 COL**.
- K1-K4 = ROW1/COL0-3, K5-K8 = ROW2/COL0-3, K9-K12 = ROW3/COL0-3.
- ROW0 chỉ dùng cho encoder push tại COL3.
- Diode: COL -> switch -> diode -> ROW, cathode/black stripe về ROW (`col2row`).
- 3 layer: OFFICE, MEDIA, FUSION 360.
- Encoder push: OFFICE -> MEDIA -> FUSION 360 -> OFFICE.
- Encoder xoay:
  - OFFICE: Volume +/-
  - MEDIA: Next / Previous
  - FUSION 360: Page Up / Page Down
- BLE HID qua ZMK.
- ZMK Studio qua USB.

## Chân đang dùng

### Matrix

- ROW0 P0.09
- ROW1 P0.10
- ROW2 P1.11
- ROW3 P1.13
- COL0 P1.15
- COL1 P0.02
- COL2 P0.29
- COL3 P0.31

### Encoder

- A P1.04
- B P1.06
- C GND

### TFT ST7789 1.47 inch

- MOSI P0.17, SCLK P0.20, CS P0.22, DC P0.24, RST P1.00; SPIM3 32 MHz.
- Panel vật lý/native 172×320; x-offset 34, y-offset 0. UI logic vẫn 320×172.
- MADCTL (`mdac`) = `0xA0` để dùng landscape đúng chiều thực tế trên RYNOR ONE
  sau khi xoay màn 180° so với bản trước.
- RGB565, `colmod=0x05`, RAMCTRL `[00 F0]`, `CONFIG_LV_COLOR_16_SWAP=y`.
  Đảo byte 16-bit cho SPI là việc khác với thứ tự kênh BGR; không đảo R/B lần nữa.
- Giữ timing PORCTRL/FRCTRL2 baseline của panel/driver (khoảng 60 Hz), không ép
  C6 = 0x1F hay porch 25 Hz. `src/lumi_panel.c` bật inversion cho đúng cực màu.
- BLK/backlight nối P0.08 để firmware bật/tắt đèn nền khi boot, sleep và wake.
- LVGL có heap 32 KB, partial double buffer tĩnh 20% màn hình, stack display
  4 KB và `CONFIG_SPI_ASYNC=y`; buffer chỉ được trả cho LVGL sau callback DMA.
- WS2812 dùng SPIM1 riêng, không tranh SPIM3 của TFT.

## Giao diện

12 ô, **4 cột × 3 hàng**, icon và nhãn trên nền tối. Footer riêng hiển thị tên
layer, output đang chọn (USB hoặc BLE kèm số profile/trạng thái), pin phần trăm
và ký hiệu nguồn USB. Ký hiệu nguồn USB không phải phép đo dòng sạc.

- Nhấn encoder vẫn đổi OFFICE → MEDIA → FUSION 360 như keymap gốc.
- Nhãn đọc binding thực tế, cập nhật cả sau khi chỉnh trong ZMK Studio (tối đa
  khoảng 0.5 giây). Các shortcut quen thuộc có tên/icon; F/V/M/E/L hiện đúng
  chữ phím, keycode lạ hiện mã hex, behavior khác hiện tên rút gọn và tham số.
- Khi nhấn, ô đổi iàu và icon hạ 5 px; tap nhanh vẫn sáng ít nhất 100 ms.
- Ô trên màn lần lượt là keymap position 0–11 (trái sang phải, trên xuống dưới).
  Matrix điện dùng 4 COL; ba hàng phím chính nằm ở ROW1-ROW3.
  Position 12 là encoder push ở ROW0/COL3, không tạo ô thứ 13.
- Mọi cập nhật LVGL chạy trên display queue; sự kiện phím chỉ lưu bit nguyên tử.

Sau khi nạp, kiểm tra đủ 12 ô tới mép dưới, chiều/chữ đúng, nền tối, cả ba layer,
press/release từng phím, encoder, USB/BLE và Studio. Build xanh xác nhận mã biên
dịch được; hướng/màu panel và chất lượng tín hiệu SPI cần xác nhận trên phần cứng.

## Build bằng GitHub Actions

1. Copy toàn bộ nội dung thư mục này vào root repo ZMK của bạn.
2. Commit + Push lên GitHub.
3. Mở tab Actions -> `Build ZMK firmware`.
4. Khi build xanh, tải artifact `firmware`; giải nén lấy `lumi_macropad_nice_nano_v2.uf2`.
5. Double-reset nice!nano để hiện ổ USB bootloader.
6. Copy file `.uf2` vào ổ đó.

## Dùng ZMK Studio

Firmware đã bật `studio-rpc-usb-uart` và `CONFIG_ZMK_STUDIO=y`.

1. Cắm RYNOR ONE bằng USB.
2. Mở https://zmk.studio/ bằng Chrome/Edge hoặc app ZMK Studio.
3. Nếu Studio yêu cầu unlock, nhấn đồng thời phím vật lý số 1 + phím số 12 (góc trên trái + góc dưới phải/ESC).
4. Có thể sửa keymap của 13 vị trí (12 key + encoder push) và đổi tên layer.

Lưu ý: ZMK Studio hiện chưa gán lại hành vi xoay encoder. Muốn đổi encoder, sửa `sensor-bindings` trong `config/lumi_macropad.keymap`, commit rồi build lại.

## Thứ tự 13 vị trí trong keymap

1-3: hàng 1
4-6: hàng 2
7-9: hàng 3
10-12: hàng 4
13: encoder push

Không đổi thứ tự matrix transform nếu không đổi dây phần cứng.


### Gán Lumi Action vào phím

Firmware có behavior `Lumi Action` với Action 1–32. Trong LumiPad, tạo/chọn script rồi
bấm **Key Map**. Cửa sổ ZMK Studio riêng sẽ mở ngay trong app; chọn phím cần gán,
chọn behavior **Lumi Action**, chọn đúng **Action N** của script rồi Save. Nếu Studio
yêu cầu unlock, giữ đồng thời phím vật lý 1 + 12. Việc gán được lưu qua ZMK Studio,
không phải một key map giả chỉ lưu trên PC.


## LumiPad desktop app

App Windows, source C#/XAML, drivers phía PC và hướng dẫn app đã chuyển sang
[Lumipad-APP](https://github.com/mihqag148/Lumipad-APP).
Repo này chỉ build firmware RYNOR ONE. `src/lumi_app_link.*` là mã chạy trên
bàn phím để giao tiếp với app, vì vậy vẫn được giữ tại đây.

## Version và phát hành độc lập

- `VERSION` là nguồn phiên bản firmware; CMake đưa giá trị này vào HELLO `FW=`.
- Workflow `build.yml` build ZMK, sau đó mới chạy job release bằng `needs: build`.
- Release chứa `firmware.uf2` và `release-manifest.json` với `firmwareVersion`.
- App có version và release riêng ở Lumipad-APP; không cần checkout app để build firmware.
- Tăng `VERSION` trước mỗi bản phát hành mới. Release đã tồn tại được giữ nguyên.

Tên USB, BLE và splash là **RYNOR ONE**. Giữ nguyên shield `lumi_macropad`,
Kconfig symbols, pinout, VID/PID, UUID và protocol để tương thích firmware/app cũ.
App dùng ID sản phẩm `rynor-one` đồng nhất với tên RYNOR ONE.

PIXEL PRO có [repo firmware riêng](https://github.com/mihqag148/PIXEL-PRO---Lumi-Macropad).
Mã tham khảo QMK Raw HID được giữ tại `qmk/lumi_raw_hid` và copy sang repo PIXEL PRO;
đây là firmware-side adapter, không phải mã desktop và không tham gia build ZMK.

Các release và nhánh lịch sử trước khi tách được giữ nguyên để có thể khôi phục.
Nhánh `main` là nguồn firmware hiện hành. Lịch sử app/protocol đã chuyển sang
[tài liệu app](https://github.com/mihqag148/Lumipad-APP/blob/main/docs/pre-split-history.md).

### TFT backlight BLK / P0.08

Firmware reserves **P0.08** for the ST7789 module **BLK/backlight** control pin.

- ST7789 `BLK` -> nice!nano `P0.08`.
- TFT `GND` stays connected directly to nice!nano `GND`.
- Firmware holds P0.08 LOW during Zephyr startup.
- Backlight stays OFF while the ST7789/LVGL boot screen is initialized, then
  turns ON after a short delay.
- Soft sleep sends ST7789 DISPOFF and turns the backlight OFF.
- Wake sends DISPON, invalidates the LVGL screen, then turns the backlight ON
  after the redraw delay.

This wiring uses the module's own BLK input directly; no AO3400 is required.



### PC Monitor BLE + dynamic footer fix v1.10.1

- Protocol v3 fallback capabilities in the Windows app now include `PCMON`.
  This fixes Bluetooth sessions where the first GATT status read contains only
  `LUMIPAD|3|SAVER:...` or is temporarily unavailable.
- The app only reports PC Monitor as Live when both the configuration packet and
  telemetry packet were actually sent.
- The three large PC Monitor cards still use slots 1-3.
- The readable bottom row now uses slots 4-6 again instead of being hard-coded.
  Changing a footer slot in the app immediately changes the keyboard display.
- Footer text stays compact for the 101 px columns, for example:
  `CPU 52C`, `GPU 48C`, `RAM 43%`, `DOWN 12.4M`, `UP 850K`, `FPS 144`.



### ECO sleep and dynamic deep sleep v1.11.0

Power behavior is now split into user-configurable stages:

- **RGB idle timeout**: based only on physical key/encoder inactivity. The RGB
  LEDs can turn off before the display sleeps.
- **Screensaver timeout**: still does not replace the Now Playing screen while
  music is playing.
- **Soft sleep timeout**: still applies while music is playing. Example:
  screensaver 15 s + sleep 60 s keeps Now Playing visible until 60 s, then
  blanks the panel/backlight while BLE remains connected.
- **Deep sleep timeout**: configured in Settings. On battery power, firmware
  suspends ZMK devices and enters nRF52840 System OFF. Bluetooth disconnects;
  pressing a matrix key wakes/reboots the keyboard and the app reconnects.
- Background CFG/RGB/PC Monitor traffic does not count as wake activity.
  Soft sleep wakes only for physical key/encoder input, an Auto Profile switch,
  starting/opening music, or an explicit manual wake/show action.
- LVGL high-frequency timers are paused during soft sleep and the page polling
  loop drops from 500 ms to 2 s while asleep.



### Encoder rotation wake from deep sleep

Deep sleep now uses ZMK's soft-off wake-source flow instead of calling
`sys_poweroff()` directly.

Wake sources:
- Any matrix key.
- Encoder push (because it is part of the matrix).
- Encoder rotation in either direction.

Both EC11 phases P1.04 and P1.06 get dedicated wake-trigger devices that remain
suspended during normal operation and are armed only while entering deep sleep.



### Unified RGB/display wake v1.13.3

RGB and the display now share the same meaningful wake sources while keeping
independent sleep timers.

Wake both display + RGB:
- Physical key press.
- Encoder push/rotation activity.
- Auto Profile actually changes profile.
- Media playback starts/opens.
- Explicit manual Wake / Show Screensaver action.

Do not wake either from background PC Monitor, telemetry or CFG/RGB sync traffic.

The RGB timeout remains independent from the display Sleep timeout. Every
meaningful wake resets the RGB idle timer, so RGB does not immediately turn
back off after Auto Profile or Media wakes the device.



### Fast Bluetooth Now Playing v1.14.0

Protocol v4 adds the `MEDIAFAST` capability.

- Now Playing metadata, transfer BEGIN and END commands remain acknowledged.
- Artwork payload chunks use paced BLE Write Without Response on protocol v4.
- Text bitmap chunks use Base64 (`TXTCHUNK64`) instead of HEX, reducing
  encoding overhead from 100% to roughly 33%.
- The fast writer uses short dynamic bursts (more conservative for a 20-byte
  BLE payload) so Windows does not flood the controller queue.
- Legacy protocol-v3 firmware automatically stays on the old reliable
  Write-With-Response path.
- Artwork remains 48x48 RGB332 over BLE; visual quality is unchanged.
- Diagnostics log the actual text/artwork transfer duration and negotiated BLE
  payload size for hardware verification.
