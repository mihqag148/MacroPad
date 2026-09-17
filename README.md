# Lumi MacroPad - ZMK

Chuyển từ `MacroPad_nice_nano_FULL_BLE_TFT_Keymap.ino` sang ZMK cho nice!nano v2 / nRF52840.

## Đã giữ nguyên

- 12 phím macro.
- Encoder EC11: A=P1.04, B=P1.06.
- Encoder push: ROW0/COL3 = P0.09 / P0.31.
- Matrix 4x4 theo đúng dây cũ.
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

### TFT cũ

Các chân P0.17 / P0.20 / P0.22 / P0.24 / P1.00 hiện KHÔNG được ZMK sử dụng.
P0.13 / EXT_VCC cũng không bị firmware này điều khiển thủ công.

Bản này ưu tiên keyboard/BLE/ZMK Studio chạy ổn. UI ST7789 của Arduino chưa được port sang ZMK.

## Build bằng GitHub Actions

1. Copy toàn bộ nội dung thư mục này vào root repo ZMK của bạn.
2. Commit + Push lên GitHub.
3. Mở tab Actions -> `Build ZMK firmware`.
4. Khi build xanh, tải artifact `lumi_macropad_nice_nano_v2`.
5. Double-reset nice!nano để hiện ổ USB bootloader.
6. Copy file `.uf2` vào ổ đó.

## Dùng ZMK Studio

Firmware đã bật `studio-rpc-usb-uart` và `CONFIG_ZMK_STUDIO=y`.

1. Cắm MacroPad bằng USB.
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
